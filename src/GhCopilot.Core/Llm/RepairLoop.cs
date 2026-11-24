using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using GhCopilot.Core.Settings;

namespace GhCopilot.Core.Llm
{
    public sealed class RepairLoopResult
    {
        public RepairLoopResult(
            bool isSuccess,
            GraphSpec? spec,
            ValidationResult? validation,
            int attemptsUsed,
            IReadOnlyList<string> errors,
            string rawResponse = "")
        {
            IsSuccess = isSuccess;
            Spec = spec;
            Validation = validation;
            AttemptsUsed = attemptsUsed;
            Errors = errors;
            RawResponse = rawResponse;
        }

        public bool IsSuccess { get; }
        public GraphSpec? Spec { get; }
        public ValidationResult? Validation { get; }
        public int AttemptsUsed { get; }
        public IReadOnlyList<string> Errors { get; }
        public string RawResponse { get; }
    }

    public sealed class RunLogEntry
    {
        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("shortlistIds")]
        public List<string> ShortlistIds { get; set; } = new List<string>();

        [JsonPropertyName("rawResponse")]
        public string RawResponse { get; set; } = string.Empty;

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("attemptsUsed")]
        public int AttemptsUsed { get; set; }

        [JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new List<string>();

        [JsonPropertyName("outcome")]
        public string Outcome { get; set; } = string.Empty;
    }

    /// <summary>
    /// Orchestrates multi-provider structured generation, GraphValidator verification, bounded validator repair retries,
    /// runtime-message repair, fix-selection repair, auto-layout normalization, payload auditing, and JSONL eval logging.
    /// </summary>
    public static class RepairLoop
    {
        public const int MaxValidatorRetries = 2;

        private static readonly JsonSerializerOptions SpecJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static string GetDefaultRunsLogPath()
        {
            return Path.Combine(IndexStore.GetDefaultDataDirectory(), "runs.jsonl");
        }

        public static Task<RepairLoopResult> GenerateAndValidateAsync(
            LlmClient client,
            string apiKey,
            string modelId,
            string userPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            string? logFilePath = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string? auditLogPath = null,
            bool sendCanvasDataEnabled = false,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            IReadOnlyList<RecipeRecord>? matchingRecipes = null,
            CanvasSnapshot? selectedContext = null,
            bool autoLayout = true,
            int maxNodeCount = GraphValidator.MaxNodeCount)
        {
            if (shortlist is null) throw new ArgumentNullException(nameof(shortlist));

            string initialPrompt = PromptBuilder.BuildUserPrompt(userPrompt, shortlist, matchingRecipes, selectedContext);
            Dictionary<string, ComponentRecord>? externalNodes = selectedContext?.BuildExternalSelectionRecordMap();

            return RunBoundedValidationLoopAsync(
                client,
                apiKey,
                modelId,
                provider,
                customEndpointUrl,
                actionName: "Generate",
                logicalPromptForRepair: userPrompt,
                initialUserPrompt: initialPrompt,
                shortlist,
                externalNodes,
                autoLayout,
                maxNodeCount,
                sendCanvasDataEnabled,
                logFilePath,
                auditLogPath,
                timeout,
                cancellationToken);
        }

        public static Task<RepairLoopResult> FixSelectionAndValidateAsync(
            LlmClient client,
            string apiKey,
            string modelId,
            CanvasSnapshot snapshot,
            string? userHint,
            bool includeNonSelectedContext,
            IReadOnlyList<ComponentRecord> shortlist,
            string? logFilePath = null,
            string? auditLogPath = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            bool autoLayout = true,
            int maxNodeCount = GraphValidator.MaxNodeCount)
        {
            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
            if (shortlist is null) throw new ArgumentNullException(nameof(shortlist));

            string initialPrompt = PromptBuilder.BuildFixSelectionPrompt(
                snapshot,
                shortlist,
                userHint,
                includeNonSelectedContext);

            string logicalPrompt = string.IsNullOrWhiteSpace(userHint)
                ? "Fix selected Grasshopper components"
                : userHint!.Trim();

            return RunBoundedValidationLoopAsync(
                client,
                apiKey,
                modelId,
                provider,
                customEndpointUrl,
                actionName: "FixSelection",
                logicalPromptForRepair: logicalPrompt,
                initialUserPrompt: initialPrompt,
                shortlist,
                externalNodes: null,
                autoLayout,
                maxNodeCount,
                includeNonSelectedContext,
                logFilePath,
                auditLogPath,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// Performs one bounded auto-fix pass when post-solve Grasshopper RuntimeMessages contain errors or warnings.
        /// </summary>
        public static async Task<RepairLoopResult> RepairRuntimeMessagesAsync(
            LlmClient client,
            string apiKey,
            string modelId,
            string userPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            GraphSpec currentSpec,
            IReadOnlyList<string> runtimeMessages,
            string? logFilePath = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string? auditLogPath = null,
            bool sendCanvasDataEnabled = false,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            CanvasSnapshot? selectedContext = null,
            bool autoLayout = true,
            int maxNodeCount = GraphValidator.MaxNodeCount)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (shortlist is null) throw new ArgumentNullException(nameof(shortlist));
            if (currentSpec is null) throw new ArgumentNullException(nameof(currentSpec));

            Dictionary<string, ComponentRecord> shortlistById = BuildShortlistMap(shortlist);
            Dictionary<string, ComponentRecord>? externalNodes = selectedContext?.BuildExternalSelectionRecordMap();
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(shortlist);
            string systemInstruction = PromptBuilder.BuildSystemInstruction();
            string prompt = PromptBuilder.BuildRuntimeRepairPrompt(userPrompt, shortlist, currentSpec, runtimeMessages);

            string rawResponse = string.Empty;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LlmCallResult callResult = await client.GenerateGraphJsonWithUsageAsync(
                    apiKey,
                    modelId,
                    systemInstruction,
                    prompt,
                    schema,
                    timeout,
                    cancellationToken,
                    provider,
                    customEndpointUrl).ConfigureAwait(false);
                rawResponse = callResult.Text;

                PayloadAuditLogger.LogTurnCompletion(
                    "RuntimeRepair",
                    callResult.Usage.Provider,
                    modelId,
                    sendCanvasDataEnabled,
                    systemInstruction,
                    prompt,
                    callResult.Usage.PromptTokens,
                    callResult.Usage.ResponseTokens,
                    callResult.Usage.DurationMs,
                    auditLogPath);
            }
            catch (LlmApiException ex)
            {
                sw.Stop();
                PayloadAuditLogger.LogTurnCompletion(
                    "RuntimeRepair",
                    provider,
                    modelId,
                    sendCanvasDataEnabled,
                    systemInstruction,
                    prompt,
                    0,
                    0,
                    sw.ElapsedMilliseconds,
                    auditLogPath);
                var errors = new List<string> { ex.Message };
                AppendRunLog(logFilePath, modelId, userPrompt, shortlist, rawResponse, false, 1, errors, "RuntimeRepairApiError");
                return new RepairLoopResult(false, null, null, 1, errors, rawResponse);
            }

            if (!TryDeserializeSpec(rawResponse, out GraphSpec? spec, out string? parseError))
            {
                var errors = new List<string> { parseError ?? "Malformed JSON during runtime repair." };
                AppendRunLog(logFilePath, modelId, userPrompt, shortlist, rawResponse, false, 1, errors, "RuntimeRepairParseError");
                return new RepairLoopResult(false, null, null, 1, errors, rawResponse);
            }

            ValidationResult validation = GraphValidator.Validate(spec!, shortlistById, externalNodes, maxNodeCount);
            if (!validation.IsValid)
            {
                List<string> errors = validation.Errors.Select(e => e.ToString()).ToList();
                AppendRunLog(logFilePath, modelId, userPrompt, shortlist, rawResponse, false, 1, errors, "RuntimeRepairValidationFailed");
                return new RepairLoopResult(false, null, validation, 1, errors, rawResponse);
            }

            if (autoLayout && spec!.Nodes.Count > 1 && GraphAutoLayout.NeedsAutoLayout(spec))
            {
                GraphAutoLayout.ApplyLayeredLayout(spec);
            }

            AppendRunLog(logFilePath, modelId, userPrompt, shortlist, rawResponse, true, 1, new List<string>(), "RuntimeRepairValid");
            return new RepairLoopResult(true, spec, validation, 1, Array.Empty<string>(), rawResponse);
        }

        private static async Task<RepairLoopResult> RunBoundedValidationLoopAsync(
            LlmClient client,
            string apiKey,
            string modelId,
            string provider,
            string? customEndpointUrl,
            string actionName,
            string logicalPromptForRepair,
            string initialUserPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            Dictionary<string, ComponentRecord>? externalNodes,
            bool autoLayout,
            int maxNodeCount,
            bool sendCanvasDataEnabled,
            string? logFilePath,
            string? auditLogPath,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));

            Dictionary<string, ComponentRecord> shortlistById = BuildShortlistMap(shortlist);
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(shortlist);
            string systemInstruction = PromptBuilder.BuildSystemInstruction();

            string currentPrompt = initialUserPrompt;
            string lastRawResponse = string.Empty;
            var lastErrors = new List<string>();
            ValidationResult? lastValidation = null;

            for (int attempt = 0; attempt <= MaxValidatorRetries; attempt++)
            {
                if (attempt == MaxValidatorRetries)
                {
                    currentPrompt = PromptBuilder.BuildScriptFallbackPrompt(
                        logicalPromptForRepair,
                        shortlist,
                        lastRawResponse,
                        lastErrors);
                }
                else if (attempt > 0)
                {
                    currentPrompt = PromptBuilder.BuildValidatorRepairPrompt(
                        logicalPromptForRepair,
                        shortlist,
                        lastRawResponse,
                        lastErrors);
                }

                string auditAction = attempt == 0 ? actionName : $"{actionName}_Retry{attempt}";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    LlmCallResult callResult = await client.GenerateGraphJsonWithUsageAsync(
                        apiKey,
                        modelId,
                        systemInstruction,
                        currentPrompt,
                        schema,
                        timeout,
                        cancellationToken,
                        provider,
                        customEndpointUrl).ConfigureAwait(false);
                    lastRawResponse = callResult.Text;

                    PayloadAuditLogger.LogTurnCompletion(
                        auditAction,
                        callResult.Usage.Provider,
                        modelId,
                        sendCanvasDataEnabled,
                        systemInstruction,
                        currentPrompt,
                        callResult.Usage.PromptTokens,
                        callResult.Usage.ResponseTokens,
                        callResult.Usage.DurationMs,
                        auditLogPath);
                }
                catch (LlmApiException ex)
                {
                    sw.Stop();
                    PayloadAuditLogger.LogTurnCompletion(
                        auditAction,
                        provider,
                        modelId,
                        sendCanvasDataEnabled,
                        systemInstruction,
                        currentPrompt,
                        0,
                        0,
                        sw.ElapsedMilliseconds,
                        auditLogPath);
                    lastErrors = new List<string> { ex.Message };
                    AppendRunLog(logFilePath, modelId, logicalPromptForRepair, shortlist, lastRawResponse, false, attempt + 1, lastErrors, "ApiError");
                    return new RepairLoopResult(false, null, lastValidation, attempt + 1, lastErrors, lastRawResponse);
                }

                if (!TryDeserializeSpec(lastRawResponse, out GraphSpec? parsedSpec, out string? parseError))
                {
                    lastErrors = new List<string> { parseError ?? "Malformed JSON output." };
                    lastValidation = null;
                    continue;
                }

                lastValidation = GraphValidator.Validate(parsedSpec!, shortlistById, externalNodes, maxNodeCount);
                if (lastValidation.IsValid)
                {
                    if (autoLayout && parsedSpec!.Nodes.Count > 1 && GraphAutoLayout.NeedsAutoLayout(parsedSpec))
                    {
                        GraphAutoLayout.ApplyLayeredLayout(parsedSpec);
                    }

                    string outcome = attempt == 0 ? "ValidFirstPass" : "ValidAfterRepair";
                    AppendRunLog(logFilePath, modelId, logicalPromptForRepair, shortlist, lastRawResponse, true, attempt + 1, new List<string>(), outcome);
                    return new RepairLoopResult(true, parsedSpec, lastValidation, attempt + 1, Array.Empty<string>(), lastRawResponse);
                }

                lastErrors = lastValidation.Errors.Select(e => e.ToString()).ToList();
            }

            AppendRunLog(logFilePath, modelId, logicalPromptForRepair, shortlist, lastRawResponse, false, MaxValidatorRetries + 1, lastErrors, "ValidationFailed");
            return new RepairLoopResult(false, null, lastValidation, MaxValidatorRetries + 1, lastErrors, lastRawResponse);
        }

        private static Dictionary<string, ComponentRecord> BuildShortlistMap(IReadOnlyList<ComponentRecord> shortlist)
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

        private static bool TryDeserializeSpec(string rawJson, out GraphSpec? spec, out string? error)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                error = "Model returned empty JSON.";
                return false;
            }

            try
            {
                spec = JsonSerializer.Deserialize<GraphSpec>(rawJson, SpecJsonOptions);
                if (spec is null)
                {
                    error = "JSON deserialized to null GraphSpec.";
                    return false;
                }

                error = null;
                return true;
            }
            catch (JsonException ex)
            {
                error = $"JSON parse error: {ex.Message}";
                return false;
            }
        }

        private static void AppendRunLog(
            string? logFilePath,
            string modelId,
            string prompt,
            IReadOnlyList<ComponentRecord> shortlist,
            string rawResponse,
            bool isValid,
            int attemptsUsed,
            List<string> errors,
            string outcome)
        {
            string targetPath = string.IsNullOrWhiteSpace(logFilePath) ? GetDefaultRunsLogPath() : logFilePath!;

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var entry = new RunLogEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    ModelId = modelId,
                    Prompt = prompt,
                    ShortlistIds = shortlist.Select(c => c.ShortId).ToList(),
                    RawResponse = rawResponse,
                    IsValid = isValid,
                    AttemptsUsed = attemptsUsed,
                    Errors = errors,
                    Outcome = outcome
                };

                string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                File.AppendAllText(targetPath, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Best-effort eval logging: never fail generation if disk log cannot be written.
            }
        }
    }
}
