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
        public event Action<string>? ProgressStageChanged;

        private void ReportStage(string thought)
        {
            ProgressStageChanged?.Invoke(thought);
        }

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
                    return FailureResponse("Enter a message first. Supply missing inputs, or explicitly enable Use Rhino Selection.");

                if (!TryLoadConfig(out AppSettings settings, out string apiKey, out string? configError))
                {
                    if (action != ChatAction.SaveRecipe) return FailureResponse(configError!);
                    apiKey = string.Empty;
                    settings = AppSettings.Load();
                }

                string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;
                bool isConversationalFastPath = action == ChatAction.Message && !useRhinoSelection && ChatPromptBuilder.IsConversationalOnlyPrompt(prompt);

                if (isConversationalFastPath)
                {
                    ReportStage("💭 Thinking: conversational message detected — replying without altering canvas...");
                    CanvasSnapshot readOnlySnapshot = await TryCaptureOptionalSnapshotAsync().ConfigureAwait(false);
                    string convSystem = ChatPromptBuilder.BuildConversationalSystem();
                    string convPayload = ChatPromptBuilder.BuildConversationalPayload(Session, readOnlySnapshot, user);
                    tokens = EstimateSend(convSystem, convPayload, null);
                    PayloadAuditLogger.LogSentPayload("ChatConversational", $"{settings.Provider}:{settings.ModelId}", settings.SendCanvasData, convSystem, convPayload);
                    ReportStage($"✉️ Asking {settings.ModelId} for a direct reply...");
                    string reply = await _geminiClient.GenerateTextAsync(
                        apiKey,
                        settings.ModelId,
                        convSystem,
                        convPayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint).ConfigureAwait(false);
                    return FinishChat(user, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        Explanation = reply,
                        StatusLine = "Replied (no canvas changes).",
                        EstimatedInputTokens = tokens
                    });
                }

                ReportStage("🧠 Analyzing prompt & ensuring active Grasshopper document...");
                IndexStore store = EnsureIndexLoaded();
                CanvasSnapshot snapshot = await UiThread.MutateActiveDocumentAsync(doc =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_chatDocument != null && !ReferenceEquals(_chatDocument, doc))
                        throw new InvalidOperationException("The active definition changed. Start a New Chat before continuing.");
                    _chatDocument = doc;
                    Session.SpawnedNodeIds.RemoveWhere(id => doc.FindObject(id, false) == null);
                    Session.LastSpawnedNodeIds.RemoveAll(id => doc.FindObject(id, false) == null);
                    return CaptureChatSnapshot(doc, store);
                }).ConfigureAwait(false);

                if (action == ChatAction.SaveRecipe)
                {
                    if (snapshot.Nodes.Count == 0) return FailureResponse("Select a subgraph to save, or create one in this chat first.");
                    string name = string.IsNullOrWhiteSpace(prompt) ? "Chat recipe" : prompt.Substring(0, Math.Min(48, prompt.Length));
                    RecipeRecord recipe = RecipeStore.CreateFromCanvasSnapshot(name, prompt, snapshot);
                    if (recipe.Spec.Nodes.Count == 0) return FailureResponse("The selected nodes are not indexed and cannot be saved as a recipe.");
                    RecipeStore.SaveRecipe(recipe);
                    return new CopilotGenerationResponse { IsSuccess = true, StatusLine = $"Saved recipe '{name}'.", Explanation = $"Saved recipe '{name}' ({recipe.Spec.Nodes.Count} node(s), {recipe.Spec.Wires.Count} wire(s))." };
                }

                if ((action == ChatAction.ExplainSelection || action == ChatAction.FixSelection) && snapshot.Nodes.Count == 0)
                    return FailureResponse("Select Grasshopper components first. Rhino selection is only used as input when explicitly enabled.");

                ReportStage("🔍 Retrieving matching Grasshopper components & saved recipes...");
                IReadOnlyList<ComponentRecord> shortlist = BuildShortlistForFix(store, snapshot, user);
                ReportStage($"🔧 Hydrating {shortlist.Count} candidate component pins...");
                await UiThread.MutateActiveDocumentAsync(doc => { EnsureChatDocument(doc); ParamHydrator.HydrateShortlist(shortlist); return true; }).ConfigureAwait(false);

                if (action == ChatAction.FixSelection)
                {
                    ReportStage("🩺 Inspecting selected input states and runtime errors...");
                    ChatInputState inputs = await UiThread.MutateActiveDocumentAsync(doc =>
                    { EnsureChatDocument(doc); return ChatInputInspector.Inspect(doc, snapshot.SelectedInstanceGuids); }).ConfigureAwait(false);
                    if ((inputs.MissingInputs.Count > 0 || inputs.InvalidInputs.Count > 0) && !useRhinoSelection)
                        return FinishChat(user, InputResponse(inputs));
                }

                bool patchOnly = snapshot.Nodes.Count > 0;
                IReadOnlyList<RecipeRecord> matchingRecipes = !patchOnly && action == ChatAction.Message
                    ? RecipeStore.RetrieveMatchingRecipes(
                        RecipeStore.LoadAll(sharedLibraryFolder: settings.SharedLibraryFolder),
                        prompt,
                        maxRecipes: 2)
                    : Array.Empty<RecipeRecord>();

                string system = action == ChatAction.ExplainSelection
                    ? PromptBuilder.BuildExplainSystemInstruction()
                    : ChatPromptBuilder.BuildSystem(shortlist, patchOnly);
                system += "\nNever silently read Rhino selection. Empty required inputs need user data or explicit selection consent; do not invent replacement geometry to hide missing inputs.";
                JsonElement schema = patchOnly ? GraphPatchSchema.Build() : SchemaBuilder.BuildResponseJsonSchema(shortlist);
                string basePayload = ChatPromptBuilder.Build(Session, snapshot, null, user, shortlist, matchingRecipes);

                if (action == ChatAction.ExplainSelection)
                {
                    ReportStage($"💭 Thinking: explaining {snapshot.Nodes.Count} node(s) with {settings.ModelId}...");
                    tokens = EstimateSend(system, basePayload, null);
                    PayloadAuditLogger.LogSentPayload("ChatScopedSnapshot", $"{settings.Provider}:{settings.ModelId}", settings.SendCanvasData, system, basePayload);
                    string text = await _geminiClient.GenerateTextAsync(apiKey, settings.ModelId, system, basePayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds), cancellationToken, settings.Provider, endpoint).ConfigureAwait(false);
                    return FinishChat(user, new CopilotGenerationResponse { IsSuccess = true, Explanation = text, StatusLine = "Selection explained.", EstimatedInputTokens = tokens });
                }

                (bool validOutput, GraphPatch? patch, GraphSpec? graph, string explanation, List<string> validationErrors, int initialTokens) =
                    await GenerateAndValidateChatTurnAsync(
                        apiKey, settings, endpoint, system, basePayload, schema, patchOnly, action, useRhinoSelection,
                        snapshot, shortlist, cancellationToken).ConfigureAwait(false);
                tokens += initialTokens;

                if (!validOutput)
                {
                    return PatchFailure(validationErrors, tokens);
                }

                bool hasCanvasEdits = patchOnly
                    ? (patch!.Ops.Count > 0 || useRhinoSelection)
                    : (graph!.Nodes.Count > 0 || graph.Script != null || useRhinoSelection);

                if (!hasCanvasEdits)
                {
                    return FinishChat(user, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        Explanation = explanation,
                        StatusLine = "Answered without canvas changes.",
                        EstimatedInputTokens = tokens
                    });
                }

                ReportStage("⚡ Applying live preview to Grasshopper canvas & solving...");
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
                    ReportStage($"🛠️ Auto-repairing runtime error (attempt {repairs + 1}/3)...");
                    CanvasSnapshot repairSnapshot = await UiThread.MutateActiveDocumentAsync(doc =>
                    {
                        EnsureChatDocument(doc);
                        return CaptureChatSnapshot(doc, store, scope, selectionFirst: false);
                    }).ConfigureAwait(false);
                    string repairUser = "Fix only the runtime errors from this turn using patch ops; all required inputs have been checked. Original request: " + user;
                    string repairSystem = ChatPromptBuilder.BuildSystem(shortlist, true);
                    repairSystem += "\nNever silently read Rhino selection. Empty required inputs need user data or explicit selection consent; do not invent replacement geometry to hide missing inputs.";
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
                OnPreviewStateChanged();
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
                    throw;
                }
                return new CopilotGenerationResponse { StatusLine = "Chat failed: " + ex.Message, EstimatedInputTokens = tokens };
            }
            finally { _chatGate.Release(); }
        }

        private async Task<(bool IsValid, GraphPatch? Patch, GraphSpec? Graph, string Explanation, List<string> Errors, int TotalTokens)>
            GenerateAndValidateChatTurnAsync(
                string apiKey,
                AppSettings settings,
                string? endpoint,
                string system,
                string basePayload,
                JsonElement schema,
                bool patchOnly,
                ChatAction action,
                bool useRhinoSelection,
                CanvasSnapshot snapshot,
                IReadOnlyList<ComponentRecord> shortlist,
                CancellationToken cancellationToken)
        {
            string schemaRaw = schema.GetRawText();
            string currentPayload = basePayload;
            string lastRaw = string.Empty;
            var lastErrors = new List<string>();
            int totalTokens = 0;

            Dictionary<string, ComponentRecord> shortlistMap = BuildSafeShortlistMap(shortlist);
            Dictionary<string, ComponentRecord> externalNodes = snapshot.BuildExternalSelectionRecordMap();

            for (int attempt = 0; attempt <= RepairLoop.MaxValidatorRetries; attempt++)
            {
                if (attempt > 0)
                {
                    ReportStage($"🔄 Self-correcting validation/schema issue (attempt {attempt + 1}/{RepairLoop.MaxValidatorRetries + 1})...");
                    currentPayload = ChatPromptBuilder.BuildValidationRepairPrompt(basePayload, lastRaw, lastErrors, patchOnly);
                }
                else
                {
                    ReportStage($"💭 Thinking: designing {(patchOnly ? "incremental graph patch" : "parametric definition")} with {settings.ModelId} ({shortlist.Count} candidates)...");
                }

                totalTokens += EstimateSend(system, currentPayload, schemaRaw);
                PayloadAuditLogger.LogSentPayload(
                    attempt == 0 ? "ChatScopedSnapshot" : $"ChatValidationRepairAttempt{attempt}",
                    $"{settings.Provider}:{settings.ModelId}",
                    settings.SendCanvasData,
                    system,
                    currentPayload);

                lastRaw = await _geminiClient.GenerateGraphJsonAsync(
                    apiKey,
                    settings.ModelId,
                    system,
                    currentPayload,
                    schema,
                    TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                    cancellationToken,
                    settings.Provider,
                    endpoint).ConfigureAwait(false);

                ReportStage("✅ Validating component references, pins, and wire topology...");

                if (patchOnly)
                {
                    try
                    {
                        GraphPatch patch = JsonSerializer.Deserialize<GraphPatch>(lastRaw) ?? throw new JsonException("Empty patch.");
                        PatchValidationResult validation = GraphPatchValidator.Validate(patch, snapshot, shortlist);
                        if (validation.IsValid)
                        {
                            return (true, patch, null, patch.Explanation, new List<string>(), totalTokens);
                        }

                        lastErrors = new List<string>(validation.Errors);
                    }
                    catch (JsonException ex)
                    {
                        lastErrors = new List<string> { "JSON schema error: " + ex.Message };
                    }
                }
                else
                {
                    try
                    {
                        GraphSpec graph = JsonSerializer.Deserialize<GraphSpec>(lastRaw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new JsonException("Empty graph.");
                        if (graph.Nodes == null || graph.Wires == null || graph.Nodes.Any(n => n == null) || graph.Wires.Any(w => w == null))
                        {
                            throw new JsonException("Graph nodes and wires must not contain null.");
                        }

                        if (action == ChatAction.Message &&
                            !useRhinoSelection &&
                            graph.Nodes.Count == 0 &&
                            graph.Script == null &&
                            !string.IsNullOrWhiteSpace(graph.Explanation))
                        {
                            return (true, null, graph, graph.Explanation, new List<string>(), totalTokens);
                        }

                        ValidationResult validation = GraphValidator.Validate(graph, shortlistMap, externalNodes);
                        if (validation.IsValid)
                        {
                            if (settings.AutoLayout && graph.Nodes.Count > 1 && GraphAutoLayout.NeedsAutoLayout(graph))
                            {
                                GraphAutoLayout.ApplyLayeredLayout(graph);
                            }

                            return (true, null, graph, graph.Explanation, new List<string>(), totalTokens);
                        }

                        lastErrors = validation.Errors.Select(e => e.ToString()).ToList();
                    }
                    catch (JsonException ex)
                    {
                        lastErrors = new List<string> { "JSON parse error: " + ex.Message };
                    }
                }
            }

            return (false, null, null, string.Empty, lastErrors, totalTokens);
        }

        private static Dictionary<string, ComponentRecord> BuildSafeShortlistMap(IReadOnlyList<ComponentRecord> shortlist)
        {
            var map = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (ComponentRecord record in shortlist)
            {
                if (record != null && !string.IsNullOrWhiteSpace(record.ShortId) && !map.ContainsKey(record.ShortId))
                {
                    map[record.ShortId] = record;
                }
            }

            return map;
        }

        private Task<CanvasSnapshot> TryCaptureOptionalSnapshotAsync()
        {
            var tcs = new TaskCompletionSource<CanvasSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            UiThread.InvokeOnUi(() =>
            {
                try
                {
                    GH_Document? doc = Instances.ActiveCanvas?.Document;
                    if (doc == null)
                    {
                        tcs.TrySetResult(new CanvasSnapshot());
                        return;
                    }

                    IndexStore store = EnsureIndexLoaded();
                    tcs.TrySetResult(CaptureChatSnapshot(doc, store));
                }
                catch (Exception)
                {
                    tcs.TrySetResult(new CanvasSnapshot());
                }
            });
            return tcs.Task;
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