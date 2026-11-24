using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GhCopilot.Core.Llm
{
    /// <summary>
    /// Exception thrown when an LLM API call fails with an HTTP error, timeout, or malformed envelope.
    /// Guarantees the API key is scrubbed from any message.
    /// </summary>
    public sealed class LlmApiException : Exception
    {
        public LlmApiException(string message, HttpStatusCode? statusCode = null, bool isTransient = false, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            IsTransient = isTransient;
        }

        public HttpStatusCode? StatusCode { get; }

        public bool IsTransient { get; }
    }

    public sealed class LlmUsage
    {
        public int PromptTokens { get; set; }
        public int ResponseTokens { get; set; }
        public long DurationMs { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
    }

    public sealed class LlmCallResult
    {
        public LlmCallResult(string text, LlmUsage usage)
        {
            Text = text;
            Usage = usage;
        }

        public string Text { get; }
        public LlmUsage Usage { get; }
    }

    /// <summary>
    /// Async HttpClient-based multi-provider BYOK client supporting Google Gemini (structured responseJsonSchema),
    /// Anthropic Claude, and OpenAI / OpenAI-compatible endpoints.
    /// </summary>
    public sealed class LlmClient : IDisposable
    {
        public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
        public const string AnthropicBaseUrl = "https://api.anthropic.com/v1";
        public const string OpenAiBaseUrl = "https://api.openai.com/v1";

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _baseUrl;

#if !NET5_0_OR_GREATER
        static LlmClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
                // Ignore if OS/runtime policy restricts SecurityProtocol mutation.
            }
        }
#endif

        public LlmClient()
            : this(null, DefaultBaseUrl)
        {
        }

        public LlmClient(HttpClient? httpClient, string baseUrl = DefaultBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentException("Base URL must not be empty.", nameof(baseUrl));
            }

            _ownsHttpClient = httpClient is null;
            _httpClient = httpClient ?? new HttpClient();
            if (_ownsHttpClient)
            {
                _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            }
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public LlmUsage? LastCallUsage { get; private set; }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        /// <summary>
        /// Lists available model names (normalized without the leading "models/" prefix) for Gemini.
        /// </summary>
        public async Task<IReadOnlyList<string>> ListModelsAsync(
            string apiKey,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string? customEndpointUrl = null)
        {
            string cleanKey = SanitizeApiKey(apiKey);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? _baseUrl : customEndpointUrl!.TrimEnd('/');

            var models = new List<string>();
            string? pageToken = null;
            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(20);

            do
            {
                string url = string.IsNullOrEmpty(pageToken)
                    ? $"{baseEndpoint}/models"
                    : $"{baseEndpoint}/models?pageToken={Uri.EscapeDataString(pageToken)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", cleanKey);

                string body = await SendAndReadBodyAsync(request, cleanKey, effectiveTimeout, cancellationToken).ConfigureAwait(false);

                using JsonDocument doc = ParseJsonDocument(body, cleanKey);
                if (doc.RootElement.TryGetProperty("models", out JsonElement modelsArray) &&
                    modelsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement modelEl in modelsArray.EnumerateArray())
                    {
                        if (modelEl.TryGetProperty("name", out JsonElement nameEl) &&
                            nameEl.ValueKind == JsonValueKind.String)
                        {
                            string normalized = NormalizeModelId(nameEl.GetString() ?? string.Empty);
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                models.Add(normalized);
                            }
                        }
                    }
                }

                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out JsonElement nextTokenEl) &&
                            nextTokenEl.ValueKind == JsonValueKind.String
                    ? nextTokenEl.GetString()
                    : null;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return models;
        }

        /// <summary>
        /// Lists available model IDs for the active BYOK provider (Gemini, Anthropic, or OpenAI).
        /// </summary>
        public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(
            string apiKey,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            string cleanKey = SanitizeApiKey(apiKey);
            string normalizedProvider = NormalizeProvider(provider);
            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);

            if (normalizedProvider == "Anthropic")
            {
                string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? AnthropicBaseUrl : customEndpointUrl!.TrimEnd('/');
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseEndpoint}/models");
                req.Headers.Add("x-api-key", cleanKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                string body = await SendAndReadBodyAsync(req, cleanKey, effectiveTimeout, cancellationToken).ConfigureAwait(false);
                return ParseDataArrayModelIds(body, cleanKey);
            }

            if (normalizedProvider == "OpenAI")
            {
                string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? OpenAiBaseUrl : customEndpointUrl!.TrimEnd('/');
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseEndpoint}/models");
                req.Headers.Add("Authorization", $"Bearer {cleanKey}");
                string body = await SendAndReadBodyAsync(req, cleanKey, effectiveTimeout, cancellationToken).ConfigureAwait(false);
                return ParseDataArrayModelIds(body, cleanKey);
            }

            return await ListModelsAsync(cleanKey, effectiveTimeout, cancellationToken, customEndpointUrl).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks whether the configured model ID is available for the active provider.
        /// </summary>
        public async Task<(bool IsAvailable, string Message)> VerifyConfiguredModelAsync(
            string apiKey,
            string modelId,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null)
        {
            string normalizedTarget = NormalizeModelId(modelId);
            if (string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return (false, "Configured model ID is empty.");
            }

            string normalizedProvider = NormalizeProvider(provider);

            try
            {
                string cleanKey = SanitizeApiKey(apiKey);

                if (normalizedProvider == "Anthropic")
                {
                    string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? AnthropicBaseUrl : customEndpointUrl!.TrimEnd('/');
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseEndpoint}/models");
                    req.Headers.Add("x-api-key", cleanKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    string body = await SendAndReadBodyAsync(req, cleanKey, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                    return VerifyModelInDataArray(body, normalizedTarget, "Anthropic", cleanKey);
                }

                if (normalizedProvider == "OpenAI")
                {
                    string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? OpenAiBaseUrl : customEndpointUrl!.TrimEnd('/');
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseEndpoint}/models");
                    req.Headers.Add("Authorization", $"Bearer {cleanKey}");
                    string body = await SendAndReadBodyAsync(req, cleanKey, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                    return VerifyModelInDataArray(body, normalizedTarget, "OpenAI", cleanKey);
                }

                IReadOnlyList<string> available = await ListModelsAsync(cleanKey, TimeSpan.FromSeconds(15), cancellationToken, customEndpointUrl)
                    .ConfigureAwait(false);

                foreach (string model in available)
                {
                    if (string.Equals(model, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, $"Model '{normalizedTarget}' verified via ListModels ({available.Count} models available).");
                    }
                }

                string suggestions = FormatSuggestedModels(available, "gemini");
                return (false, $"Warning: configured model '{normalizedTarget}' was not returned by ListModels ({available.Count} models available).{suggestions}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LlmApiException ex)
            {
                return (false, $"Model check failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Model check failed: {ScrubSecret(ex.Message, apiKey)}");
            }
        }

        /// <summary>
        /// Calls the configured LLM provider using structured JSON output (and optional live viewport PNG).
        /// </summary>
        public async Task<string> GenerateGraphJsonAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            JsonElement responseJsonSchema,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            string? imageBase64Png = null)
        {
            LlmCallResult result = await GenerateGraphJsonWithUsageAsync(
                apiKey,
                modelId,
                systemInstruction,
                userPrompt,
                responseJsonSchema,
                timeout,
                cancellationToken,
                provider,
                customEndpointUrl,
                imageBase64Png).ConfigureAwait(false);
            return result.Text;
        }

        /// <summary>
        /// Calls the configured LLM provider using structured JSON output and returns text plus token usage and duration.
        /// </summary>
        public async Task<LlmCallResult> GenerateGraphJsonWithUsageAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            JsonElement responseJsonSchema,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            string? imageBase64Png = null)
        {
            string cleanKey = SanitizeApiKey(apiKey);
            string normalizedProvider = NormalizeProvider(provider);
            string normalizedModel = NormalizeModelId(modelId);
            var stopwatch = Stopwatch.StartNew();

            (string Text, int PromptTokens, int ResponseTokens) raw = await GenerateGraphJsonCoreAsync(
                cleanKey,
                modelId,
                systemInstruction,
                userPrompt,
                responseJsonSchema,
                timeout,
                cancellationToken,
                normalizedProvider,
                customEndpointUrl,
                imageBase64Png).ConfigureAwait(false);

            stopwatch.Stop();
            var usage = new LlmUsage
            {
                PromptTokens = raw.PromptTokens,
                ResponseTokens = raw.ResponseTokens,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Provider = normalizedProvider,
                ModelId = normalizedModel
            };
            LastCallUsage = usage;
            return new LlmCallResult(raw.Text, usage);
        }

        /// <summary>
        /// Calls the configured LLM provider for plain-text or markdown explanations (with optional live viewport PNG).
        /// </summary>
        public async Task<string> GenerateTextAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            string? imageBase64Png = null)
        {
            LlmCallResult result = await GenerateTextWithUsageAsync(
                apiKey,
                modelId,
                systemInstruction,
                userPrompt,
                timeout,
                cancellationToken,
                provider,
                customEndpointUrl,
                imageBase64Png).ConfigureAwait(false);
            return result.Text;
        }

        /// <summary>
        /// Calls the configured LLM provider for plain-text output and returns text plus token usage and duration.
        /// </summary>
        public async Task<LlmCallResult> GenerateTextWithUsageAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null,
            string? imageBase64Png = null)
        {
            string cleanKey = SanitizeApiKey(apiKey);
            string normalizedProvider = NormalizeProvider(provider);
            string normalizedModel = NormalizeModelId(modelId);
            var stopwatch = Stopwatch.StartNew();

            (string Text, int PromptTokens, int ResponseTokens) raw;
            if (normalizedProvider == "Anthropic")
            {
                raw = await SendAnthropicAsync(cleanKey, modelId, systemInstruction, userPrompt, temperature: 0.2, stripCodeFences: false, timeout, customEndpointUrl, cancellationToken, imageBase64Png).ConfigureAwait(false);
            }
            else if (normalizedProvider == "OpenAI")
            {
                raw = await SendOpenAiAsync(cleanKey, modelId, systemInstruction, userPrompt, temperature: 0.2, jsonMode: false, timeout, customEndpointUrl, cancellationToken, imageBase64Png).ConfigureAwait(false);
            }
            else
            {
                object generationConfig = new
                {
                    temperature = 0.2
                };

                try
                {
                    raw = await SendGeminiGenerateContentAsync(
                        cleanKey,
                        modelId,
                        systemInstruction,
                        userPrompt,
                        generationConfig,
                        stripCodeFences: false,
                        timeout,
                        customEndpointUrl,
                        cancellationToken,
                        imageBase64Png).ConfigureAwait(false);
                }
                catch (LlmApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && !string.IsNullOrWhiteSpace(imageBase64Png))
                {
                    raw = await SendGeminiGenerateContentAsync(
                        cleanKey,
                        modelId,
                        systemInstruction,
                        userPrompt,
                        generationConfig,
                        stripCodeFences: false,
                        timeout,
                        customEndpointUrl,
                        cancellationToken,
                        imageBase64Png: null).ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            var usage = new LlmUsage
            {
                PromptTokens = raw.PromptTokens,
                ResponseTokens = raw.ResponseTokens,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Provider = normalizedProvider,
                ModelId = normalizedModel
            };
            LastCallUsage = usage;
            return new LlmCallResult(raw.Text, usage);
        }

        private async Task<(string Text, int PromptTokens, int ResponseTokens)> GenerateGraphJsonCoreAsync(
            string cleanKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            JsonElement responseJsonSchema,
            TimeSpan? timeout,
            CancellationToken cancellationToken,
            string normalizedProvider,
            string? customEndpointUrl,
            string? imageBase64Png)
        {
            if (normalizedProvider == "Anthropic")
            {
                string augmentedSystem = $"{systemInstruction}\n\nReturn ONLY raw JSON strictly conforming to this JSON Schema (no markdown fences or prose):\n{responseJsonSchema.GetRawText()}";
                return await SendAnthropicAsync(cleanKey, modelId, augmentedSystem, userPrompt, temperature: 0.1, stripCodeFences: true, timeout, customEndpointUrl, cancellationToken, imageBase64Png).ConfigureAwait(false);
            }

            if (normalizedProvider == "OpenAI")
            {
                string augmentedSystem = $"{systemInstruction}\n\nReturn ONLY a JSON object strictly conforming to this JSON Schema:\n{responseJsonSchema.GetRawText()}";
                return await SendOpenAiAsync(cleanKey, modelId, augmentedSystem, userPrompt, temperature: 0.1, jsonMode: true, timeout, customEndpointUrl, cancellationToken, imageBase64Png).ConfigureAwait(false);
            }

            object generationConfig = new
            {
                temperature = 0.1,
                responseMimeType = "application/json",
                responseJsonSchema
            };

            try
            {
                return await SendGeminiGenerateContentAsync(
                    cleanKey,
                    modelId,
                    systemInstruction,
                    userPrompt,
                    generationConfig,
                    stripCodeFences: true,
                    timeout,
                    customEndpointUrl,
                    cancellationToken,
                    imageBase64Png).ConfigureAwait(false);
            }
            catch (LlmApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && !string.IsNullOrWhiteSpace(imageBase64Png) && !IsSchemaFieldError(ex.Message))
            {
                // Fallback when the chosen model/endpoint does not accept image inputs: retry text-only.
                return await GenerateGraphJsonCoreAsync(
                    cleanKey,
                    modelId,
                    systemInstruction,
                    userPrompt,
                    responseJsonSchema,
                    timeout,
                    cancellationToken,
                    normalizedProvider,
                    customEndpointUrl,
                    imageBase64Png: null).ConfigureAwait(false);
            }
            catch (LlmApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && IsSchemaFieldError(ex.Message))
            {
                // Fallback for Gemini endpoints/models that reject responseJsonSchema or strict JSON Schema keywords:
                // keep responseMimeType = application/json and enforce the schema in systemInstruction + local validator.
                string fallbackSystem = $"{systemInstruction}\n\nReturn ONLY a raw JSON object strictly conforming to this JSON Schema (no markdown fences):\n{responseJsonSchema.GetRawText()}";
                object fallbackConfig = new
                {
                    temperature = 0.1,
                    responseMimeType = "application/json"
                };

                try
                {
                    return await SendGeminiGenerateContentAsync(
                        cleanKey,
                        modelId,
                        fallbackSystem,
                        userPrompt,
                        fallbackConfig,
                        stripCodeFences: true,
                        timeout,
                        customEndpointUrl,
                        cancellationToken,
                        imageBase64Png).ConfigureAwait(false);
                }
                catch (LlmApiException imgEx) when (imgEx.StatusCode == HttpStatusCode.BadRequest && !string.IsNullOrWhiteSpace(imageBase64Png))
                {
                    return await SendGeminiGenerateContentAsync(
                        cleanKey,
                        modelId,
                        fallbackSystem,
                        userPrompt,
                        fallbackConfig,
                        stripCodeFences: true,
                        timeout,
                        customEndpointUrl,
                        cancellationToken,
                        imageBase64Png: null).ConfigureAwait(false);
                }
            }
        }

        private async Task<(string Text, int PromptTokens, int ResponseTokens)> SendGeminiGenerateContentAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            object generationConfig,
            bool stripCodeFences,
            TimeSpan? timeout,
            string? customEndpointUrl,
            CancellationToken cancellationToken,
            string? imageBase64Png = null)
        {
            ValidateKeyAndModel(apiKey, modelId, out string normalizedModelId);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? _baseUrl : customEndpointUrl!.TrimEnd('/');
            string url = $"{baseEndpoint}/models/{Uri.EscapeDataString(normalizedModelId)}:generateContent";

            object[] userParts = string.IsNullOrWhiteSpace(imageBase64Png)
                ? new object[] { new { text = userPrompt } }
                : new object[]
                {
                    new { text = userPrompt },
                    new { inline_data = new { mime_type = "image/png", data = imageBase64Png } }
                };

            var payload = new
            {
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemInstruction }
                    }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = userParts
                    }
                },
                generationConfig
            };

            string requestJson = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey.Trim());

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(Settings.AppSettings.DefaultRequestTimeoutSeconds);
            string responseBody = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return ExtractGeminiCandidateTextAndUsage(responseBody, apiKey, stripCodeFences);
        }

        private async Task<(string Text, int PromptTokens, int ResponseTokens)> SendAnthropicAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            double temperature,
            bool stripCodeFences,
            TimeSpan? timeout,
            string? customEndpointUrl,
            CancellationToken cancellationToken,
            string? imageBase64Png = null)
        {
            ValidateKeyAndModel(apiKey, modelId, out string normalizedModelId);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? AnthropicBaseUrl : customEndpointUrl!.TrimEnd('/');
            string url = $"{baseEndpoint}/messages";

            object contentValue = string.IsNullOrWhiteSpace(imageBase64Png)
                ? (object)userPrompt
                : new object[]
                {
                    new
                    {
                        type = "image",
                        source = new { type = "base64", media_type = "image/png", data = imageBase64Png }
                    },
                    new { type = "text", text = userPrompt }
                };

            var payload = new
            {
                model = normalizedModelId,
                max_tokens = 4096,
                temperature,
                system = systemInstruction,
                messages = new[]
                {
                    new { role = "user", content = contentValue }
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", apiKey.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(Settings.AppSettings.DefaultRequestTimeoutSeconds);
            string responseBody = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return ExtractAnthropicTextAndUsage(responseBody, apiKey, stripCodeFences);
        }

        private async Task<(string Text, int PromptTokens, int ResponseTokens)> SendOpenAiAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            double temperature,
            bool jsonMode,
            TimeSpan? timeout,
            string? customEndpointUrl,
            CancellationToken cancellationToken,
            string? imageBase64Png = null)
        {
            ValidateKeyAndModel(apiKey, modelId, out string normalizedModelId);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? OpenAiBaseUrl : customEndpointUrl!.TrimEnd('/');
            string url = $"{baseEndpoint}/chat/completions";

            object userContent = string.IsNullOrWhiteSpace(imageBase64Png)
                ? (object)userPrompt
                : new object[]
                {
                    new { type = "text", text = userPrompt },
                    new { type = "image_url", image_url = new { url = "data:image/png;base64," + imageBase64Png } }
                };

            object payload = jsonMode
                ? new
                {
                    model = normalizedModelId,
                    temperature,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = (object)systemInstruction },
                        new { role = "user", content = userContent }
                    }
                }
                : new
                {
                    model = normalizedModelId,
                    temperature,
                    messages = new object[]
                    {
                        new { role = "system", content = (object)systemInstruction },
                        new { role = "user", content = userContent }
                    }
                };

            string requestJson = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bearer {apiKey.Trim()}");

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(Settings.AppSettings.DefaultRequestTimeoutSeconds);
            string responseBody = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return ExtractOpenAiTextAndUsage(responseBody, apiKey, jsonMode);
        }

        private static string SanitizeApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));
            }

            string cleaned = apiKey.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
            if (cleaned.Length >= 2 &&
                ((cleaned[0] == '"' && cleaned[cleaned.Length - 1] == '"') ||
                 (cleaned[0] == '\'' && cleaned[cleaned.Length - 1] == '\'')))
            {
                cleaned = cleaned.Substring(1, cleaned.Length - 2).Trim();
            }

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));
            }

            foreach (char c in cleaned)
            {
                if (c < 0x21 || c > 0x7E)
                {
                    throw new LlmApiException("API key contains invalid whitespace or non-ASCII characters. Re-copy the key without spaces or line breaks.");
                }
            }

            return cleaned;
        }

        private static bool IsSchemaFieldError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("responseJsonSchema", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("response_json_schema", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("additionalProperties", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IReadOnlyList<string> ParseDataArrayModelIds(string body, string apiKey)
        {
            var models = new List<string>();
            using JsonDocument doc = ParseJsonDocument(body, apiKey);
            if (doc.RootElement.TryGetProperty("data", out JsonElement dataArray) &&
                dataArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in dataArray.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out JsonElement idEl) &&
                        idEl.ValueKind == JsonValueKind.String)
                    {
                        string id = (idEl.GetString() ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            models.Add(id);
                        }
                    }
                }
            }

            return models;
        }

        private static string FormatSuggestedModels(IReadOnlyList<string> available, string preferredPrefix)
        {
            if (available.Count == 0)
            {
                return string.Empty;
            }

            var picks = new List<string>(6);
            foreach (string m in available)
            {
                if (m.StartsWith(preferredPrefix, StringComparison.OrdinalIgnoreCase) &&
                    m.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) < 0 &&
                    m.IndexOf("imagen", StringComparison.OrdinalIgnoreCase) < 0 &&
                    m.IndexOf("veo", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    picks.Add(m);
                    if (picks.Count == 6) break;
                }
            }

            if (picks.Count == 0)
            {
                for (int i = 0; i < Math.Min(6, available.Count); i++)
                {
                    picks.Add(available[i]);
                }
            }

            return " Try one of: " + string.Join(", ", picks);
        }

        private static void ValidateKeyAndModel(string apiKey, string modelId, out string normalizedModelId)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));
            }

            normalizedModelId = NormalizeModelId(modelId);
            if (string.IsNullOrWhiteSpace(normalizedModelId))
            {
                throw new ArgumentException("Model ID must not be empty.", nameof(modelId));
            }
        }

        internal static string NormalizeProvider(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return "Gemini";
            }

            string trimmed = provider!.Trim();
            if (trimmed.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
                trimmed.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Anthropic";
            }

            if (trimmed.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                trimmed.IndexOf("OpenAI", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "OpenAI";
            }

            return "Gemini";
        }

        internal static string NormalizeModelId(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return string.Empty;
            }

            string trimmed = modelId.Trim();
            const string prefix = "models/";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(prefix.Length);
            }

            return trimmed;
        }

        public static string ScrubSecret(string? message, string? secret)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(secret))
            {
                return message!;
            }

            return message!.Replace(secret!.Trim(), "[REDACTED]");
        }

        private async Task<string> SendAndReadBodyAsync(
            HttpRequestMessage request,
            string apiKey,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmApiException(
                    $"LLM API request timed out after {timeout.TotalSeconds:F0}s.",
                    statusCode: null,
                    isTransient: true,
                    innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                string detail = ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message)
                    ? $"{ex.Message} ({ex.InnerException.Message})"
                    : ex.Message;
                throw new LlmApiException(
                    $"LLM API network error: {ScrubSecret(detail, apiKey)}",
                    statusCode: null,
                    isTransient: true,
                    innerException: ex);
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    bool isTransient = code == 429 || code >= 500;
                    string safeSnippet = ScrubSecret(Truncate(body, 400), apiKey);
                    throw new LlmApiException(
                        $"LLM API returned HTTP {code} ({response.ReasonPhrase}): {safeSnippet}",
                        response.StatusCode,
                        isTransient);
                }

                return body;
            }
        }

        private static (bool IsAvailable, string Message) VerifyModelInDataArray(
            string body,
            string targetModel,
            string providerLabel,
            string apiKey)
        {
            using JsonDocument doc = ParseJsonDocument(body, apiKey);
            if (doc.RootElement.TryGetProperty("data", out JsonElement dataArray) &&
                dataArray.ValueKind == JsonValueKind.Array)
            {
                int count = 0;
                foreach (JsonElement item in dataArray.EnumerateArray())
                {
                    count++;
                    if (item.TryGetProperty("id", out JsonElement idEl) &&
                        idEl.ValueKind == JsonValueKind.String &&
                        string.Equals(idEl.GetString(), targetModel, StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, $"Model '{targetModel}' verified via {providerLabel} ({count} models available).");
                    }
                }

                IReadOnlyList<string> ids = ParseDataArrayModelIds(body, apiKey);
                string suggestions = FormatSuggestedModels(ids, providerLabel == "Anthropic" ? "claude" : "gpt");
                return (false, $"Warning: '{targetModel}' was not returned by {providerLabel} models list ({count} models available).{suggestions}");
            }

            return (true, $"{providerLabel} endpoint reachable.");
        }

        private static JsonDocument ParseJsonDocument(string body, string apiKey)
        {
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new LlmApiException(
                    $"LLM API returned invalid JSON envelope: {ScrubSecret(ex.Message, apiKey)}",
                    innerException: ex);
            }
        }

        private static (string Text, int PromptTokens, int ResponseTokens) ExtractGeminiCandidateTextAndUsage(
            string responseBody,
            string apiKey,
            bool stripCodeFences)
        {
            using JsonDocument doc = ParseJsonDocument(responseBody, apiKey);

            if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                throw new LlmApiException("Gemini API response contained no candidates.");
            }

            JsonElement firstCandidate = candidates[0];
            if (!firstCandidate.TryGetProperty("content", out JsonElement content) ||
                !content.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                throw new LlmApiException("Gemini API candidate contained no content parts.");
            }

            var sb = new StringBuilder();
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out JsonElement thoughtEl) &&
                    thoughtEl.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out JsonElement textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    sb.Append(textEl.GetString());
                }
            }

            int promptTokens = 0;
            int responseTokens = 0;
            if (doc.RootElement.TryGetProperty("usageMetadata", out JsonElement usageEl) &&
                usageEl.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadNonNegativeInt(usageEl, "promptTokenCount");
                responseTokens = ReadNonNegativeInt(usageEl, "candidatesTokenCount");
                if (responseTokens == 0)
                {
                    int totalTokens = ReadNonNegativeInt(usageEl, "totalTokenCount");
                    if (totalTokens > promptTokens)
                    {
                        responseTokens = totalTokens - promptTokens;
                    }
                }
            }

            return (FinalizeExtractedText(sb.ToString(), stripCodeFences, "Gemini"), promptTokens, responseTokens);
        }

        private static (string Text, int PromptTokens, int ResponseTokens) ExtractAnthropicTextAndUsage(
            string responseBody,
            string apiKey,
            bool stripCodeFences)
        {
            using JsonDocument doc = ParseJsonDocument(responseBody, apiKey);

            if (!doc.RootElement.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array ||
                content.GetArrayLength() == 0)
            {
                throw new LlmApiException("Anthropic API response contained no content blocks.");
            }

            var sb = new StringBuilder();
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement typeEl) &&
                    string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    block.TryGetProperty("text", out JsonElement textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    sb.Append(textEl.GetString());
                }
            }

            int promptTokens = 0;
            int responseTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out JsonElement usageEl) &&
                usageEl.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadNonNegativeInt(usageEl, "input_tokens");
                responseTokens = ReadNonNegativeInt(usageEl, "output_tokens");
            }

            return (FinalizeExtractedText(sb.ToString(), stripCodeFences, "Anthropic"), promptTokens, responseTokens);
        }

        private static (string Text, int PromptTokens, int ResponseTokens) ExtractOpenAiTextAndUsage(
            string responseBody,
            string apiKey,
            bool stripCodeFences)
        {
            using JsonDocument doc = ParseJsonDocument(responseBody, apiKey);

            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw new LlmApiException("OpenAI API response contained no choices.");
            }

            JsonElement first = choices[0];
            if (!first.TryGetProperty("message", out JsonElement msg) ||
                !msg.TryGetProperty("content", out JsonElement contentEl) ||
                contentEl.ValueKind != JsonValueKind.String)
            {
                throw new LlmApiException("OpenAI API choice contained no message content.");
            }

            int promptTokens = 0;
            int responseTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out JsonElement usageEl) &&
                usageEl.ValueKind == JsonValueKind.Object)
            {
                promptTokens = ReadNonNegativeInt(usageEl, "prompt_tokens");
                responseTokens = ReadNonNegativeInt(usageEl, "completion_tokens");
            }

            return (FinalizeExtractedText(contentEl.GetString() ?? string.Empty, stripCodeFences, "OpenAI"), promptTokens, responseTokens);
        }

        private static int ReadNonNegativeInt(JsonElement obj, string propertyName)
        {
            if (obj.TryGetProperty(propertyName, out JsonElement val) &&
                val.ValueKind == JsonValueKind.Number &&
                val.TryGetInt32(out int parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            return 0;
        }

        private static string FinalizeExtractedText(string raw, bool stripCodeFences, string providerLabel)
        {
            string text = raw.Trim();
            if (stripCodeFences)
            {
                text = StripCodeFences(text);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new LlmApiException($"{providerLabel} API returned empty response text.");
            }

            return text;
        }

        private static string StripCodeFences(string text)
        {
            if (!text.StartsWith("```", StringComparison.Ordinal))
            {
                return text;
            }

            int firstNewline = text.IndexOf('\n');
            if (firstNewline < 0)
            {
                return text;
            }

            string body = text.Substring(firstNewline + 1);
            int lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                body = body.Substring(0, lastFence);
            }

            return body.Trim();
        }

        private static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value!.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
    }
}
