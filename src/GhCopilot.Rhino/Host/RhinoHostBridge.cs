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
    internal sealed class RhinoHostBridge : IRhinoHostBridge
    {
        private static readonly JsonSerializerOptions PlanJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly GeminiClient _geminiClient = new GeminiClient();
        private readonly RhinoActionExecutor _executor = new RhinoActionExecutor();

        public ChatSession Session { get; } = new ChatSession();

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

        public async Task<CopilotGenerationResponse> SendRhinoChatAsync(
            string prompt,
            bool explainOnly = false,
            CancellationToken cancellationToken = default)
        {
            string trimmed = (prompt ?? string.Empty).Trim();
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return FailureResponse("A Rhino Copilot request is already running.");
            }

            int totalTokens = 0;
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

                string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;
                ReportStage("🔭 Inspecting active Rhino 8 scene, layers & viewport...");
                (RhinoViewportSnapshot snapshot, IReadOnlyList<string> lastCreated) = await InvokeOnUiAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (
                        RhinoViewportInspector.InspectActiveDoc(),
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
                    PayloadAuditLogger.LogSentPayload("RhinoExplain", $"{settings.Provider}:{settings.ModelId}", settings.SendCanvasData, explainSystem, explainPayload);
                    ReportStage($"💭 Thinking: inspecting Rhino scene with {settings.ModelId}...");

                    string explanationText = await _geminiClient.GenerateTextAsync(
                        apiKey!,
                        settings.ModelId,
                        explainSystem,
                        explainPayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint).ConfigureAwait(false);

                    return FinishTurn(userText, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        StatusLine = "Rhino viewport & scene explained.",
                        Explanation = explanationText,
                        EstimatedInputTokens = totalTokens
                    });
                }

                if (ChatPromptBuilder.IsConversationalOnlyPrompt(trimmed))
                {
                    string convSystem = RhinoPromptBuilder.BuildConversationalSystem();
                    string convPayload = RhinoPromptBuilder.BuildPayload(Session, snapshot, trimmed, lastCreated);
                    totalTokens = EstimateSend(convSystem, convPayload, null);
                    PayloadAuditLogger.LogSentPayload("RhinoConversational", $"{settings.Provider}:{settings.ModelId}", settings.SendCanvasData, convSystem, convPayload);
                    ReportStage($"✉️ Asking {settings.ModelId} for a direct reply...");

                    string reply = await _geminiClient.GenerateTextAsync(
                        apiKey!,
                        settings.ModelId,
                        convSystem,
                        convPayload,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint).ConfigureAwait(false);

                    return FinishTurn(userText, new CopilotGenerationResponse
                    {
                        IsSuccess = true,
                        StatusLine = "Replied directly (no Rhino scene changes).",
                        Explanation = reply,
                        EstimatedInputTokens = totalTokens
                    });
                }

                if (!snapshot.HasActiveDocument)
                {
                    return FailureResponse("No active Rhino document is open.");
                }

                JsonElement schema = RhinoActionSchema.Build();
                string schemaRaw = schema.GetRawText();
                string system = RhinoPromptBuilder.BuildSystem();
                string basePayload = RhinoPromptBuilder.BuildPayload(Session, snapshot, trimmed, lastCreated);
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
                    PayloadAuditLogger.LogSentPayload(
                        attempt == 0 ? "RhinoDirect" : $"RhinoDirectRetry{attempt}",
                        $"{settings.Provider}:{settings.ModelId}",
                        settings.SendCanvasData,
                        system,
                        currentPayload);

                    lastRaw = await _geminiClient.GenerateGraphJsonAsync(
                        apiKey!,
                        settings.ModelId,
                        system,
                        currentPayload,
                        schema,
                        TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                        cancellationToken,
                        settings.Provider,
                        endpoint).ConfigureAwait(false);

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
                        EstimatedInputTokens = totalTokens
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
                        EstimatedInputTokens = totalTokens
                    });
                }

                ReportStage($"⚡ Applying {validPlan.Ops.Count} operation(s) directly to Rhino...");
                RhinoExecutionResult execResult = await InvokeOnUiAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RhinoDoc? doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Active Rhino document is unavailable.");
                    return _executor.Execute(doc, validPlan);
                }).ConfigureAwait(false);

                RhinoStateChanged?.Invoke(this, EventArgs.Empty);

                string summaryLine = BuildExecutionStatusLine(execResult);
                string explanation = string.IsNullOrWhiteSpace(validPlan.Explanation)
                    ? summaryLine
                    : validPlan.Explanation + (execResult.AppliedNotes.Count > 0 ? $"\n({summaryLine})" : string.Empty);

                return FinishTurn(userText, new CopilotGenerationResponse
                {
                    IsSuccess = true,
                    StatusLine = summaryLine,
                    Explanation = explanation,
                    RuntimeMessages = execResult.Warnings.ToList(),
                    NodeCount = execResult.CreatedCount + execResult.ModifiedCount + execResult.DeletedCount,
                    AttemptsUsed = attemptsUsed,
                    EstimatedInputTokens = totalTokens
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
                    EstimatedInputTokens = totalTokens
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
