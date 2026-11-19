using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Settings;
using GhCopilot.Gh.Canvas;
using GhCopilot.Gh.Index;
using Grasshopper;
using Grasshopper.Kernel;

namespace GhCopilot.Gh.Host
{
    public sealed partial class GrasshopperHostBridge
    {
        private readonly SemaphoreSlim _chatGate = new SemaphoreSlim(1, 1);
        private ChatTurnTransaction? _chatPreview;
        private readonly List<Guid> _previousChatScope = new List<Guid>();
        private GH_Document? _chatDocument;
        public ChatSession Session { get; } = new ChatSession();
        public event Action<int>? TokenEstimateChanged;

        public void NewChat()
        {
            if (!_chatGate.Wait(0)) throw new InvalidOperationException("Wait for the current request to finish.");
            try
            {
                if (HasActivePreview) throw new InvalidOperationException("Accept or reject the pending turn first.");
                Session.Clear();
                _chatDocument = null;
            }
            finally { _chatGate.Release(); }
        }

        public async Task<CopilotGenerationResponse> SendChatAsync(string prompt, ChatAction action = ChatAction.Message,
            bool useRhinoSelection = false, CancellationToken cancellationToken = default)
        {
            prompt = prompt ?? string.Empty;
            if (!await _chatGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return FailureResponse("A chat request is already running.");
            ChatTurnTransaction? transaction = null;
            int tokens = 0;
            string user = action == ChatAction.Message ? prompt.Trim() : action + ": " + prompt.Trim();
            if (useRhinoSelection) user += "\nExplicit permission: use my currently selected Rhino objects as inputs for this turn.";
            try
            {
                if (HasActivePreview) return FailureResponse("Accept or reject the pending turn before sending another message.");
                if (string.IsNullOrWhiteSpace(prompt) && action == ChatAction.Message)
                    return FinishChat(user, FailureResponse("Enter a message first. Supply missing inputs, or explicitly enable Use Rhino Selection."));

                IndexStore store = EnsureIndexLoaded();
                CanvasSnapshot snapshot = await UiThread.MutateActiveDocumentAsync(doc =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_chatDocument != null && !ReferenceEquals(_chatDocument, doc))
                        throw new InvalidOperationException("The active definition changed. Start a New Chat before continuing.");
                    _chatDocument = doc;
                    return CaptureChatSnapshot(doc, store);
                }).ConfigureAwait(false);

                if (action == ChatAction.SaveRecipe)
                {
                    if (snapshot.Nodes.Count == 0) return FinishChat(user, FailureResponse("Select a subgraph to save, or create one in this chat first."));
                    string name = string.IsNullOrWhiteSpace(prompt) ? "Chat recipe" : prompt.Substring(0, Math.Min(48, prompt.Length));
                    RecipeRecord recipe = RecipeStore.CreateFromCanvasSnapshot(name, prompt, snapshot);
                    if (recipe.Spec.Nodes.Count == 0) return FinishChat(user, FailureResponse("The selected nodes are not indexed and cannot be saved as a recipe."));
                    RecipeStore.SaveRecipe(recipe);
                    return FinishChat(user, new CopilotGenerationResponse { IsSuccess = true, StatusLine = $"Saved recipe '{name}'." });
                }

                if ((action == ChatAction.ExplainSelection || action == ChatAction.FixSelection) && snapshot.Nodes.Count == 0)
                    return FinishChat(user, FailureResponse("Select Grasshopper components first. Rhino selection is only used as input when explicitly enabled."));
                if (!TryLoadConfig(out AppSettings settings, out string apiKey, out string? configError))
                    return FinishChat(user, FailureResponse(configError!));

                IReadOnlyList<ComponentRecord> shortlist = BuildShortlistForFix(store, snapshot, user);
                await UiThread.MutateActiveDocumentAsync(doc => { EnsureChatDocument(doc); ParamHydrator.HydrateShortlist(shortlist); return true; }).ConfigureAwait(false);
                string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;

                if (action == ChatAction.FixSelection)
                {
                    ChatInputState inputs = await UiThread.MutateActiveDocumentAsync(doc =>
                    { EnsureChatDocument(doc); return ChatInputInspector.Inspect(doc, snapshot.SelectedInstanceGuids); }).ConfigureAwait(false);
                    if ((inputs.MissingInputs.Count > 0 || inputs.InvalidInputs.Count > 0) && !useRhinoSelection)
                        return FinishChat(user, InputResponse(inputs));
                }

                bool patchOnly = snapshot.Nodes.Count > 0 || Session.SpawnedNodeIds.Count > 0;
                string system = action == ChatAction.ExplainSelection
                    ? PromptBuilder.BuildExplainSystemInstruction()
                    : ChatPromptBuilder.BuildSystem(shortlist, patchOnly);
                system += "\nNever silently read Rhino selection. Empty required inputs need user data or explicit selection consent; do not invent replacement geometry to hide missing inputs.";
                JsonElement schema = patchOnly ? GraphPatchSchema.Build() : SchemaBuilder.BuildResponseJsonSchema(shortlist);
                string payload = ChatPromptBuilder.Build(Session, snapshot, null, user, shortlist);
                tokens = EstimateSend(system, payload, action == ChatAction.ExplainSelection ? null : schema.GetRawText());
                PayloadAuditLogger.LogSentPayload("ChatScopedSnapshot", $"{settings.Provider}:{settings.ModelId}", settings.SendCanvasData, system, payload);

                if (action == ChatAction.ExplainSelection)
                {
                    string text = await _geminiClient.GenerateTextAsync(apiKey, settings.ModelId, system, payload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), cancellationToken, settings.Provider, endpoint).ConfigureAwait(false);
                    return FinishChat(user, new CopilotGenerationResponse { IsSuccess = true, Explanation = text, StatusLine = "Selection explained.", EstimatedInputTokens = tokens });
                }

                string raw = await _geminiClient.GenerateGraphJsonAsync(apiKey, settings.ModelId, system, payload, schema,
                    TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), cancellationToken, settings.Provider, endpoint).ConfigureAwait(false);
                GraphPatch? patch = null;
                GraphSpec? graph = null;
                string explanation;
                if (patchOnly)
                {
                    patch = JsonSerializer.Deserialize<GraphPatch>(raw) ?? throw new JsonException("Empty patch.");
                    PatchValidationResult validation = GraphPatchValidator.Validate(patch, snapshot, shortlist);
                    if (!validation.IsValid) return FinishChat(user, PatchFailure(validation.Errors, tokens));
                    explanation = patch.Explanation;
                }
                else
                {
                    graph = JsonSerializer.Deserialize<GraphSpec>(raw) ?? throw new JsonException("Empty graph.");
                    if (graph.Nodes == null || graph.Wires == null || graph.Nodes.Any(n => n == null) || graph.Wires.Any(w => w == null))
                        throw new JsonException("Graph nodes and wires must not contain null.");
                    ValidationResult validation = GraphValidator.Validate(graph, shortlist.ToDictionary(r => r.ShortId));
                    if (!validation.IsValid) return FinishChat(user, PatchFailure(validation.Errors.Select(e => e.ToString()).ToList(), tokens));
                    if (settings.AutoLayout && GraphAutoLayout.NeedsAutoLayout(graph)) GraphAutoLayout.ApplyLayeredLayout(graph);
                    explanation = graph.Explanation;
                }

                _previousChatScope.Clear();
                _previousChatScope.AddRange(Session.LastSpawnedNodeIds);
                var scope = new List<Guid>(snapshot.SelectedInstanceGuids);
                ChatInputState state = await UiThread.MutateActiveDocumentAsync(doc =>
                {
                    EnsureChatDocument(doc);
                    cancellationToken.ThrowIfCancellationRequested();
                    CanvasSnapshot current = CaptureChatSnapshot(doc, store);
                    EnsureUnchanged(snapshot, current);
                    transaction = new ChatTurnTransaction(doc);
                    if (graph != null) transaction.ApplyGraph(graph, snapshot, id => ResolveChatProxy(store, id));
                    else transaction.Apply(patch!, snapshot, id => ResolveChatProxy(store, id));
                    scope.AddRange(transaction.AddedNodeIds);
                    scope = scope.Distinct().Where(id => doc.FindObject(id, false) != null).ToList();
                    if (useRhinoSelection)
                    {
                        var bindingErrors = new List<string>();
                        transaction.MutateInputs(scope, () => bindingErrors.AddRange(ChatInputInspector.BindRhinoSelection(doc, scope)));
                        doc.NewSolution(false);
                        ChatInputState bound = ChatInputInspector.Inspect(doc, scope);
                        bound.InvalidInputs.AddRange(bindingErrors);
                        return bound;
                    }
                    return ChatInputInspector.Inspect(doc, scope);
                }).ConfigureAwait(false);

                int repairs = 0;
                var repairNotes = new List<string>();
                while (repairs < 3 && RuntimeRepairPolicy.ShouldRepair(state.MissingInputs, state.InvalidInputs, state.RuntimeMessages))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CanvasSnapshot repairSnapshot = await UiThread.MutateActiveDocumentAsync(doc =>
                    {
                        EnsureChatDocument(doc);
                        return CaptureChatSnapshot(doc, store, scope, selectionFirst: false);
                    }).ConfigureAwait(false);
                    string repairUser = "Fix only the runtime errors from this turn using patch ops; all required inputs have been checked. Original request: " + user;
                    string repairSystem = ChatPromptBuilder.BuildSystem(shortlist, true);
                    string repairPayload = ChatPromptBuilder.Build(Session, repairSnapshot, state.RuntimeMessages, repairUser, shortlist);
                    JsonElement repairSchema = GraphPatchSchema.Build();
                    tokens += EstimateSend(repairSystem, repairPayload, repairSchema.GetRawText());
                    PayloadAuditLogger.LogSentPayload("ChatRuntimeRepairScopedSnapshot", $"{settings.Provider}:{settings.ModelId}", settings.SendCanvasData, repairSystem, repairPayload);
                    repairs++;
                    GraphPatch fix;
                    try
                    {
                        string fixRaw = await _geminiClient.GenerateGraphJsonAsync(apiKey, settings.ModelId, repairSystem, repairPayload, repairSchema,
                            TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), cancellationToken, settings.Provider, endpoint).ConfigureAwait(false);
                        fix = JsonSerializer.Deserialize<GraphPatch>(fixRaw) ?? throw new JsonException("Empty repair patch.");
                    }
                    catch (Exception ex) when (ex is JsonException || ex is GeminiApiException)
                    { repairNotes.Add("Auto-repair stopped: " + ex.Message); break; }
                    PatchValidationResult validation = GraphPatchValidator.Validate(fix, repairSnapshot, shortlist);
                    if (!validation.IsValid) { repairNotes.AddRange(validation.Errors.Select(e => "Repair rejected: " + e)); break; }
                    state = await UiThread.MutateActiveDocumentAsync(doc =>
                    {
                        EnsureChatDocument(doc);
                        cancellationToken.ThrowIfCancellationRequested();
                        EnsureUnchanged(repairSnapshot, CaptureChatSnapshot(doc, store, scope, false));
                        // Inputs may have been cleared by the user while the request was in flight.
                        ChatInputState before = ChatInputInspector.Inspect(doc, scope);
                        if (!RuntimeRepairPolicy.ShouldRepair(before.MissingInputs, before.InvalidInputs, before.RuntimeMessages)) return before;
                        transaction!.Apply(fix, repairSnapshot, id => ResolveChatProxy(store, id));
                        scope.AddRange(transaction.AddedNodeIds);
                        scope = scope.Distinct().Where(id => doc.FindObject(id, false) != null).ToList();
                        return ChatInputInspector.Inspect(doc, scope);
                    }).ConfigureAwait(false);
                    repairNotes.Add($"Auto-repair {repairs}: {fix.Explanation}");
                }

                Session.LastSpawnedNodeIds.Clear();
                Session.LastSpawnedNodeIds.AddRange(scope);
                foreach (Guid id in transaction!.AddedNodeIds) Session.SpawnedNodeIds.Add(id);
                lock (_previewLock) _chatPreview = transaction;
                transaction = null; // ownership passes to Accept/Reject
                CopilotGenerationResponse result = InputResponse(state);
                result.IsSuccess = true;
                result.NodeCount = scope.Count;
                result.AttemptsUsed = 1 + repairs;
                result.EstimatedInputTokens = tokens;
                result.Explanation = explanation + (repairNotes.Count > 0 ? "\n" + string.Join("\n", repairNotes) : "");
                result.StatusLine = $"Turn preview ready; Accept or Reject. Auto-repair attempts: {repairs}/3.";
                if (state.MissingInputs.Count > 0 || state.InvalidInputs.Count > 0) result.Explanation += "\n" + InputResponse(state).StatusLine;
                if (repairs == 3 && state.RuntimeMessages.Any(m => m.StartsWith("[Error]", StringComparison.Ordinal)))
                    result.Explanation += "\nStopped after 3 repair attempts. Remaining errors are shown below.";
                return FinishChat(user, result);
            }
            catch (Exception ex)
            {
                if (transaction != null)
                    await RollbackChatAsync(transaction).ConfigureAwait(false);
                if (ex is OperationCanceledException)
                {
                    Session.CompleteTurn(user, "Request cancelled; this turn was rolled back.");
                    throw;
                }
                return FinishChat(user, new CopilotGenerationResponse { StatusLine = "Chat failed: " + ex.Message, EstimatedInputTokens = tokens });
            }
            finally { _chatGate.Release(); }
        }

        private CanvasSnapshot CaptureChatSnapshot(GH_Document doc, IndexStore store, IReadOnlyList<Guid>? scope = null, bool selectionFirst = true)
        {
            if (!selectionFirst)
            {
                // Explicit IDs avoid changing the user's selection or widening the repair scope.
                return CanvasSerializer.SerializeObjects(doc, store, scope ?? Array.Empty<Guid>(), Session.GetNodeId);
            }
            return CanvasSerializer.SerializeSelection(doc, store, false, scope ?? Session.LastSpawnedNodeIds, Session.GetNodeId);
        }

        private void EnsureChatDocument(GH_Document doc)
        {
            if (!ReferenceEquals(doc, _chatDocument)) throw new InvalidOperationException("Active definition changed; return to the original document before continuing.");
        }

        private static Task RollbackChatAsync(ChatTurnTransaction transaction)
        {
            // Cancellation/document switching must roll back the owning document, not whichever
            // definition happens to be active by the time the network operation finishes.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            UiThread.InvokeOnUi(() =>
            {
                void Restore(GH_Document doc)
                {
                    try { transaction.Reject(); completion.TrySetResult(true); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }
                GH_Document original = transaction.Document;
                if (original.SolutionState == GH_ProcessStep.PreProcess || original.SolutionState == GH_ProcessStep.Process)
                    original.ScheduleSolution(5, Restore);
                else Restore(original);
            });
            return completion.Task;
        }

        private static void EnsureUnchanged(CanvasSnapshot before, CanvasSnapshot after)
        {
            if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after))
                throw new InvalidOperationException("The scoped canvas changed while the model was responding. Nothing was applied; send again to refresh context.");
        }

        private static IGH_ObjectProxy? ResolveChatProxy(IndexStore store, string id)
        {
            ComponentRecord? record = store.GetByShortId(id);
            return record == null ? null : Instances.ComponentServer?.EmitObjectProxy(record.ComponentGuid)
                ?? UserObjectFolderLoader.TryGetCompanyProxy(record.ComponentGuid);
        }

        private int EstimateSend(string system, string payload, string? schema)
        {
            int estimate = ChatPromptBuilder.EstimateTokens(system, payload, schema);
            TokenEstimateChanged?.Invoke(estimate);
            return estimate;
        }

        private CopilotGenerationResponse FinishChat(string user, CopilotGenerationResponse response)
        {
            Session.CompleteTurn(user, string.IsNullOrWhiteSpace(response.Explanation) ? response.StatusLine : response.Explanation);
            Session.LastMessages.Clear();
            Session.LastMessages.AddRange(response.ValidatorWarnings);
            Session.LastMessages.AddRange(response.RuntimeMessages);
            return response;
        }

        private static CopilotGenerationResponse PatchFailure(List<string> errors, int tokens) => new CopilotGenerationResponse
        { StatusLine = "Patch validation failed; no changes applied.", ValidatorWarnings = errors, EstimatedInputTokens = tokens };

        private static CopilotGenerationResponse InputResponse(ChatInputState state)
        {
            var messages = new List<string>(state.RuntimeMessages);
            messages.AddRange(state.MissingInputs.Select(s => "[Missing input] " + s));
            messages.AddRange(state.InvalidInputs.Select(s => "[Invalid input] " + s));
            bool blocked = state.MissingInputs.Count > 0 || state.InvalidInputs.Count > 0;
            return new CopilotGenerationResponse { RuntimeMessages = messages, StatusLine = blocked
                ? "Automatic repair paused. Insert valid data into the listed inputs, or explicitly enable Use Rhino Selection and send again."
                : "No missing or invalid required inputs." };
        }
    }
}