using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Rhino;
using GhCopilot.Core.Settings;
using GhCopilot.Gh.Canvas;
using Rhino;

namespace GhCopilot.Rhino.Host
{
    /// <summary>
    /// Executes natural-language commands, conversational queries, and demo geometry directly against
    /// <see cref="RhinoDoc.ActiveDoc"/> without requiring Grasshopper to be opened.
    /// </summary>
    internal sealed class RhinoHostBridge : IRhinoHostBridge, IHeadlessChatBridge, IActionPlanHost<RhinoActionPlan>
    {
        private static readonly JsonSerializerOptions PlanJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly LlmClient _llmClient = new LlmClient();
        private readonly RhinoActionExecutor _executor = new RhinoActionExecutor();

        public ChatSession Session => CopilotHostRegistry.SharedSession;

        public bool CanUndoLastTurn => _executor.CanUndoLastTurn;

        public bool HasActiveDocument
        {
            get
            {
                try
                {
                    return RhinoDoc.ActiveDoc != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public event Action<int>? TokenEstimateChanged;
        public event Action<string>? ProgressStageChanged;
        public event EventHandler? RhinoStateChanged;

        public string GetRhinoStatusSummary()
        {
            try
            {
                RhinoDoc? doc = RhinoDoc.ActiveDoc;
                if (doc is null)
                {
                    return "No active Rhino document";
                }

                string name = string.IsNullOrWhiteSpace(doc.Name) ? "(untitled)" : doc.Name;
                return $"Doc={name}, Units={doc.ModelUnitSystem}, CanUndo={CanUndoLastTurn}";
            }
            catch (Exception)
            {
                return "Rhino host initialized";
            }
        }

        public void NewChat()
        {
            if (!_gate.Wait(0))
            {
                throw new InvalidOperationException("Wait for the current Rhino request to finish.");
            }

            try
            {
                Session.Clear();
                _executor.ResetTracking();
                RhinoStateChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _gate.Release();
            }
        }

        public string UndoLastRhinoTurn()
        {
            bool undone = false;
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                RhinoDoc? doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                {
                    undone = _executor.UndoLastTurn(doc);
                }
            }));

            RhinoStateChanged?.Invoke(this, EventArgs.Empty);
            return undone
                ? "Undid the last direct Rhino action (Ctrl+Z)."
                : "Nothing to undo in the active Rhino document.";
        }

        public string SpawnRhinoDemo()
        {
            RhinoExecutionResult? exec = null;
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                RhinoDoc? doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                {
                    exec = _executor.SpawnDemoScene(doc);
                }
            }));

            RhinoStateChanged?.Invoke(this, EventArgs.Empty);
            if (exec is null)
            {
                return "No active Rhino document is open.";
            }

            return $"Spawned Rhino Demo directly ({exec.CreatedCount} object(s) created on layer 'GH Copilot Demo').";
        }

        public ActionPlanValidationResult Validate(RhinoActionPlan plan)
        {
            return RhinoActionPlanValidator.Validate(plan).ToHostResult();
        }

        public ActionPlanExecutionResult Execute(RhinoActionPlan plan)
        {
            if (plan is null)
            {
                var nullResult = new ActionPlanExecutionResult { IsSuccess = false };
                nullResult.Errors.Add("Action plan must not be null.");
                return nullResult;
            }

            ActionPlanValidationResult validation = Validate(plan);
            if (!validation.IsValid)
            {
                var invalidResult = new ActionPlanExecutionResult { IsSuccess = false };
                invalidResult.Errors.AddRange(validation.Errors);
                return invalidResult;
            }

            RhinoExecutionResult? exec = null;
            string? docError = null;
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                RhinoDoc? doc = RhinoDoc.ActiveDoc;
                if (doc is null)
                {
                    docError = "No active Rhino document is open.";
                    return;
                }

                exec = _executor.Execute(doc, plan);
            }));

            RhinoStateChanged?.Invoke(this, EventArgs.Empty);

            if (docError != null || exec is null)
            {
                var errResult = new ActionPlanExecutionResult { IsSuccess = false };
                errResult.Errors.Add(docError ?? "Rhino execution failed.");
                return errResult;
            }

            var hostResult = new ActionPlanExecutionResult
            {
                IsSuccess = true,
                Summary = BuildExecutionStatusLine(exec),
                AffectedCount = exec.CreatedCount + exec.ModifiedCount + exec.DeletedCount
            };
            foreach (Guid id in exec.CreatedIds)
            {
                hostResult.AffectedIds.Add(id.ToString("N"));
            }
            hostResult.Warnings.AddRange(exec.Warnings);
            return hostResult;
        }

        public bool Rollback()
        {
            bool undone = false;
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                RhinoDoc? doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                {
                    undone = _executor.UndoLastTurn(doc);
                }
            }));

            RhinoStateChanged?.Invoke(this, EventArgs.Empty);
            return undone;
        }

        public Task<CopilotGenerationResponse> SendHeadlessChatAsync(
            string prompt,
            bool skipPlanFirst = true,
            bool captureViewport = false,
            CancellationToken cancellationToken = default)
        {
            return SendRhinoChatCoreAsync(
                prompt,
                explainOnly: false,
                skipPlanFirstOverride: skipPlanFirst,
                captureViewportOverride: captureViewport,
                cancellationToken);
        }

        public Task<CopilotGenerationResponse> SendRhinoChatAsync(
            string prompt,
            bool explainOnly = false,
            CancellationToken cancellationToken = default)
        {
            return SendRhinoChatCoreAsync(
                prompt,
                explainOnly,
                skipPlanFirstOverride: false,
                captureViewportOverride: null,
                cancellationToken);
        }

        public Task<CopilotGenerationResponse> SendRhinoChatAsync(
            string prompt,
            bool explainOnly,
            bool skipPlanFirstOverride,
            bool? captureViewportOverride,
            CancellationToken cancellationToken = default)
        {
            return SendRhinoChatCoreAsync(
                prompt,
                explainOnly,
                skipPlanFirstOverride,
                captureViewportOverride,
                cancellationToken);
        }

        private async Task<CopilotGenerationResponse> SendRhinoChatCoreAsync(
            string prompt,
            bool explainOnly,
            bool skipPlanFirstOverride,
            bool? captureViewportOverride,
            CancellationToken cancellationToken)
        {
            string trimmed = (prompt ?? string.Empty).Trim();
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return FailureResponse("A Rhino Copilot request is already running.");
            }

            int totalTokens = 0;
            int actualPromptTokens = 0;
            int actualResponseTokens = 0;
            long totalDurationMs = 0;
            string userText = explainOnly
                ? (string.IsNullOrEmpty(trimmed) ? "Explain active Rhino scene & viewport" : "Explain: " + trimmed)
                : trimmed;

            try
            {
                if (!explainOnly && string.IsNullOrWhiteSpace(trimmed))
                {
                    return FailureResponse("Enter a message first.");
                }

                AppSettings settings = AppSettings.Load();
                if (!SecretStore.TryLoadApiKey(out string? apiKey) || string.IsNullOrWhiteSpace(apiKey))
                {
                    return FailureResponse($"No API key configured for {settings.Provider}. Save a key in Settings or set {SecretStore.EnvVarName}.");
                }

                string? endpoint = settings.GetEffectiveEndpointUrl();
                bool captureBeforeImage = captureViewportOverride ?? (explainOnly || ChatPromptBuilder.ExplicitlyRequestsViewportVision(trimmed));
                bool captureAfterImage = captureViewportOverride ?? settings.AutoVerifyViewportAfterBuild;

                ReportStage(captureBeforeImage
                    ? "🔭 Inspecting active Rhino 8 scene, layers & live viewport image..."
                    : "🔭 Inspecting active Rhino 8 scene & layers...");
                (RhinoViewportSnapshot snapshot, IReadOnlyList<string> lastCreated) = await InvokeOnUiAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (
                        RhinoViewportInspector.InspectActiveDoc(captureImage: captureBeforeImage),
                        _executor.DescribeLastCreated(RhinoDoc.ActiveDoc));
                }).ConfigureAwait(false);

                if (explainOnly)
                {
                    if (!snapshot.HasActiveDocument)
                    {
                        return FailureResponse("No active Rhino document is open to inspect.");
                    }

                    string explainSystem = RhinoPromptBuilder.BuildExplainSystem();
                    string explainPayload = RhinoPromptBuilder.BuildPayload(Session, snapshot, userText, lastCreated);
                    totalTokens = EstimateSend(explainSystem, explainPayload, null);
                    ReportStage($"💭 Thinking: inspecting live Rhino viewport with {settings.ModelId}...");

                    LlmCallResult explainCall = await _llmClient.GenerateTextWithUsageAsync(
                        apiKey!,
                        settings.ModelId,
                        explainSystem,
                        explainPayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint,
                        captureBeforeImage ? snapshot.ViewportImageBase64Png : null).ConfigureAwait(false);

                    PayloadAuditLogger.LogTurnCompletion(
                        "RhinoExplain",
                        explainCall.Usage.Provider,
                        settings.ModelId,
                        settings.SendCanvasData,
                        explainSystem,
                        explainPayload,
                        explainCall.Usage.PromptTokens,
                        explainCall.Usage.ResponseTokens,
                        explainCall.Usage.DurationMs);

                    string explainSummary = RhinoViewportSnapshot.BuildDiffSummary(null, snapshot);
                    Session.LastViewportVerification = explainSummary;

                    return FinishTurn(userText, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        StatusLine = "Rhino viewport & scene explained.",
                        Explanation = explainCall.Text,
                        EstimatedInputTokens = totalTokens,
                        PromptTokens = explainCall.Usage.PromptTokens,
                        ResponseTokens = explainCall.Usage.ResponseTokens,
                        DurationMs = explainCall.Usage.DurationMs,
                        ModelId = explainCall.Usage.ModelId,
                        Provider = explainCall.Usage.Provider,
                        ViewportImageBase64Png = snapshot.ViewportImageBase64Png,
                        ViewportVerificationSummary = explainSummary
                    });
                }

                bool approvingPendingPlan = !string.IsNullOrWhiteSpace(Session.PendingPlanSummary) &&
                                            ChatPromptBuilder.IsApprovalOrBuildConfirmation(trimmed);

                if (!approvingPendingPlan && ChatPromptBuilder.IsConversationalOnlyPrompt(trimmed))
                {
                    string convSystem = RhinoPromptBuilder.BuildConversationalSystem();
                    string convPayload = RhinoPromptBuilder.BuildPayload(Session, snapshot, trimmed, lastCreated);
                    totalTokens = EstimateSend(convSystem, convPayload, null);
                    ReportStage(captureBeforeImage
                        ? $"✉️ Asking {settings.ModelId} (with live viewport view)..."
                        : $"✉️ Asking {settings.ModelId}...");

                    LlmCallResult convCall = await _llmClient.GenerateTextWithUsageAsync(
                        apiKey!,
                        settings.ModelId,
                        convSystem,
                        convPayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint,
                        captureBeforeImage ? snapshot.ViewportImageBase64Png : null).ConfigureAwait(false);

                    PayloadAuditLogger.LogTurnCompletion(
                        "RhinoConversational",
                        convCall.Usage.Provider,
                        settings.ModelId,
                        settings.SendCanvasData,
                        convSystem,
                        convPayload,
                        convCall.Usage.PromptTokens,
                        convCall.Usage.ResponseTokens,
                        convCall.Usage.DurationMs);

                    string convSummary = RhinoViewportSnapshot.BuildDiffSummary(null, snapshot);
                    Session.LastViewportVerification = convSummary;

                    return FinishTurn(userText, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        StatusLine = "Replied directly (no Rhino scene changes).",
                        Explanation = convCall.Text,
                        EstimatedInputTokens = totalTokens,
                        PromptTokens = convCall.Usage.PromptTokens,
                        ResponseTokens = convCall.Usage.ResponseTokens,
                        DurationMs = convCall.Usage.DurationMs,
                        ModelId = convCall.Usage.ModelId,
                        Provider = convCall.Usage.Provider,
                        ViewportImageBase64Png = snapshot.ViewportImageBase64Png,
                        ViewportVerificationSummary = convSummary
                    });
                }

                bool isSingleDirectOp = ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt(trimmed);
                if (!skipPlanFirstOverride &&
                    ChatPromptBuilder.ShouldPlanBeforeBuilding(
                        trimmed,
                        Session,
                        settings.PlanFirstMode,
                        forcePlanFirstAlways: settings.ForcePlanFirstAlways,
                        isSingleDirectRhinoOp: isSingleDirectOp))
                {
                    string planSystem = ChatPromptBuilder.BuildPlanAndClarifySystem(CopilotWorkMode.RhinoDirect);
                    string planPayload = RhinoPromptBuilder.BuildPayload(Session, snapshot, trimmed, lastCreated);
                    totalTokens = EstimateSend(planSystem, planPayload, null);
                    ReportStage($"🧠 Drafting Rhino plan & clarifying questions with {settings.ModelId}...");

                    LlmCallResult planCall = await _llmClient.GenerateTextWithUsageAsync(
                        apiKey!,
                        settings.ModelId,
                        planSystem,
                        planPayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint,
                        captureBeforeImage ? snapshot.ViewportImageBase64Png : null).ConfigureAwait(false);

                    PayloadAuditLogger.LogTurnCompletion(
                        "RhinoPlanFirst",
                        planCall.Usage.Provider,
                        settings.ModelId,
                        settings.SendCanvasData,
                        planSystem,
                        planPayload,
                        planCall.Usage.PromptTokens,
                        planCall.Usage.ResponseTokens,
                        planCall.Usage.DurationMs);

                    Session.PendingPlanSummary = planCall.Text;
                    string planViewSummary = RhinoViewportSnapshot.BuildDiffSummary(null, snapshot);
                    Session.LastViewportVerification = planViewSummary;

                    return FinishTurn(userText, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        IsPlanProposal = true,
                        StatusLine = "Plan ready — tap Approve & Build or reply to refine.",
                        Explanation = planCall.Text,
                        EstimatedInputTokens = totalTokens,
                        PromptTokens = planCall.Usage.PromptTokens,
                        ResponseTokens = planCall.Usage.ResponseTokens,
                        DurationMs = planCall.Usage.DurationMs,
                        ModelId = planCall.Usage.ModelId,
                        Provider = planCall.Usage.Provider,
                        ViewportImageBase64Png = snapshot.ViewportImageBase64Png,
                        ViewportVerificationSummary = planViewSummary
                    });
                }

                if (!snapshot.HasActiveDocument)
                {
                    return FailureResponse("No active Rhino document is open.");
                }

                string effectiveUser = ChatPromptBuilder.BuildEffectiveExecutionPrompt(trimmed, Session);
                JsonElement schema = RhinoActionSchema.Build();
                string schemaRaw = schema.GetRawText();
                string system = RhinoPromptBuilder.BuildSystem();
                string basePayload = RhinoPromptBuilder.BuildPayload(Session, snapshot, effectiveUser, lastCreated);
                string currentPayload = basePayload;
                string lastRaw = string.Empty;
                var lastErrors = new List<string>();
                RhinoActionPlan? validPlan = null;
                int attemptsUsed = 0;

                for (int attempt = 0; attempt <= RepairLoop.MaxValidatorRetries; attempt++)
                {
                    attemptsUsed = attempt + 1;
                    if (attempt > 0)
                    {
                        ReportStage($"🔄 Self-correcting Rhino action plan (attempt {attempt + 1}/{RepairLoop.MaxValidatorRetries + 1})...");
                        currentPayload = RhinoPromptBuilder.BuildValidationRepairPrompt(basePayload, lastRaw, lastErrors);
                    }
                    else
                    {
                        ReportStage($"💭 Thinking: planning Rhino actions with {settings.ModelId}...");
                    }

                    totalTokens += EstimateSend(system, currentPayload, schemaRaw);
                    string auditAction = attempt == 0 ? "RhinoDirect" : $"RhinoDirectRetry{attempt}";

                    LlmCallResult callResult = await _llmClient.GenerateGraphJsonWithUsageAsync(
                        apiKey!,
                        settings.ModelId,
                        system,
                        currentPayload,
                        schema,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint,
                        attempt == 0 && captureBeforeImage ? snapshot.ViewportImageBase64Png : null).ConfigureAwait(false);

                    lastRaw = callResult.Text;
                    actualPromptTokens += callResult.Usage.PromptTokens;
                    actualResponseTokens += callResult.Usage.ResponseTokens;
                    totalDurationMs += callResult.Usage.DurationMs;

                    PayloadAuditLogger.LogTurnCompletion(
                        auditAction,
                        callResult.Usage.Provider,
                        settings.ModelId,
                        settings.SendCanvasData,
                        system,
                        currentPayload,
                        callResult.Usage.PromptTokens,
                        callResult.Usage.ResponseTokens,
                        callResult.Usage.DurationMs);

                    ReportStage("✅ Validating Rhino operations & coordinates...");
                    try
                    {
                        RhinoActionPlan parsed = JsonSerializer.Deserialize<RhinoActionPlan>(lastRaw, PlanJsonOptions)
                            ?? throw new JsonException("Empty RhinoActionPlan.");
                        RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(parsed);
                        if (validation.IsValid)
                        {
                            validPlan = parsed;
                            break;
                        }

                        lastErrors = new List<string>(validation.Errors);
                    }
                    catch (JsonException ex)
                    {
                        lastErrors = new List<string> { "JSON parse error: " + ex.Message };
                    }
                }

                if (validPlan is null)
                {
                    return new CopilotGenerationResponse
                    {
                        IsSuccess = false,
                        StatusLine = "Rhino action plan failed validation; nothing modified.",
                        ValidatorWarnings = lastErrors,
                        AttemptsUsed = attemptsUsed,
                        EstimatedInputTokens = totalTokens,
                        PromptTokens = actualPromptTokens,
                        ResponseTokens = actualResponseTokens,
                        DurationMs = totalDurationMs,
                        ModelId = settings.ModelId,
                        Provider = settings.Provider
                    };
                }

                if (validPlan.Ops.Count == 0)
                {
                    return FinishTurn(userText, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        StatusLine = "Replied without changing the Rhino document.",
                        Explanation = validPlan.Explanation,
                        AttemptsUsed = attemptsUsed,
                        EstimatedInputTokens = totalTokens,
                        PromptTokens = actualPromptTokens,
                        ResponseTokens = actualResponseTokens,
                        DurationMs = totalDurationMs,
                        ModelId = settings.ModelId,
                        Provider = settings.Provider
                    });
                }

                ReportStage($"⚡ Applying {validPlan.Ops.Count} operation(s) directly to Rhino...");
                (RhinoExecutionResult execResult, RhinoViewportSnapshot afterViewport) = await InvokeOnUiAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RhinoDoc? doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Active Rhino document is unavailable.");
                    RhinoExecutionResult exec = _executor.Execute(doc, validPlan);
                    RhinoViewportSnapshot after = RhinoViewportInspector.InspectActiveDoc(captureImage: captureAfterImage);
                    return (exec, after);
                }).ConfigureAwait(false);

                RhinoStateChanged?.Invoke(this, EventArgs.Empty);

                string summaryLine = BuildExecutionStatusLine(execResult);
                string afterSummary = RhinoViewportSnapshot.BuildDiffSummary(snapshot, afterViewport);
                string explanation = string.IsNullOrWhiteSpace(validPlan.Explanation)
                    ? summaryLine
                    : validPlan.Explanation + (execResult.AppliedNotes.Count > 0 ? $"\n({summaryLine})" : string.Empty);

                Session.PendingPlanSummary = string.Empty;
                Session.HasBuiltInCurrentSession = true;
                Session.LastExecutedSummary = explanation;
                Session.LastViewportVerification = afterSummary;

                return FinishTurn(userText, new CopilotGenerationResponse
                {
                    IsSuccess = true,
                    StatusLine = summaryLine,
                    Explanation = explanation,
                    RuntimeMessages = execResult.Warnings.ToList(),
                    NodeCount = execResult.CreatedCount + execResult.ModifiedCount + execResult.DeletedCount,
                    AttemptsUsed = attemptsUsed,
                    EstimatedInputTokens = totalTokens,
                    PromptTokens = actualPromptTokens,
                    ResponseTokens = actualResponseTokens,
                    DurationMs = totalDurationMs,
                    ModelId = settings.ModelId,
                    Provider = settings.Provider,
                    ViewportImageBase64Png = afterViewport.ViewportImageBase64Png,
                    ViewportVerificationSummary = afterSummary
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CopilotGenerationResponse
                {
                    IsSuccess = false,
                    StatusLine = "Rhino Direct failed: " + ex.Message,
                    EstimatedInputTokens = totalTokens,
                    PromptTokens = actualPromptTokens,
                    ResponseTokens = actualResponseTokens,
                    DurationMs = totalDurationMs
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string BuildExecutionStatusLine(RhinoExecutionResult exec)
        {
            var parts = new List<string>();
            if (exec.CreatedCount > 0) parts.Add($"{exec.CreatedCount} created");
            if (exec.ModifiedCount > 0) parts.Add($"{exec.ModifiedCount} modified");
            if (exec.DeletedCount > 0) parts.Add($"{exec.DeletedCount} deleted");
            if (exec.ViewportUpdated) parts.Add("viewport updated");

            if (parts.Count == 0)
            {
                return exec.Warnings.Count > 0
                    ? "Completed with warnings (no objects modified)."
                    : "Executed Rhino actions.";
            }

            return "Applied directly to Rhino: " + string.Join(", ", parts) + ".";
        }

        private static Task<T> InvokeOnUiAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));
            return tcs.Task;
        }

        private void ReportStage(string thought)
        {
            ProgressStageChanged?.Invoke(thought);
        }

        private int EstimateSend(string system, string payload, string? schema)
        {
            int estimate = ChatPromptBuilder.EstimateTokens(system, payload, schema);
            TokenEstimateChanged?.Invoke(estimate);
            return estimate;
        }

        private CopilotGenerationResponse FinishTurn(string user, CopilotGenerationResponse response)
        {
            Session.CompleteTurn(user, string.IsNullOrWhiteSpace(response.Explanation) ? response.StatusLine : response.Explanation);
            Session.LastMessages.Clear();
            Session.LastMessages.AddRange(response.ValidatorWarnings);
            Session.LastMessages.AddRange(response.RuntimeMessages);
            return response;
        }

        private static CopilotGenerationResponse FailureResponse(string statusLine)
        {
            return new CopilotGenerationResponse
            {
                IsSuccess = false,
                StatusLine = statusLine
            };
        }
    }
}
