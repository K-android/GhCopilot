using System;
using System.Collections.Generic;
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
    public sealed class GeminiApiException : Exception
    {
        public GeminiApiException(string message, HttpStatusCode? statusCode = null, bool isTransient = false, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            IsTransient = isTransient;
        }

        public HttpStatusCode? StatusCode { get; }

        public bool IsTransient { get; }
    }

    /// <summary>
    /// Async HttpClient-based multi-provider BYOK client supporting Google Gemini (structured responseJsonSchema),
    /// Anthropic Claude, and OpenAI / OpenAI-compatible endpoints.
    /// </summary>
    public sealed class GeminiClient : IDisposable
    {
        public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
        public const string AnthropicBaseUrl = "https://api.anthropic.com/v1";
        public const string OpenAiBaseUrl = "https://api.openai.com/v1";

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _baseUrl;

        public GeminiClient()
            : this(null, DefaultBaseUrl)
        {
        }

        public GeminiClient(HttpClient? httpClient, string baseUrl = DefaultBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentException("Base URL must not be empty.", nameof(baseUrl));
            }

            _ownsHttpClient = httpClient is null;
            _httpClient = httpClient ?? new HttpClient();
            _baseUrl = baseUrl.TrimEnd('/');
        }

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
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));
            }

            var models = new List<string>();
            string? pageToken = null;
            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(20);

            do
            {
                string url = string.IsNullOrEmpty(pageToken)
                    ? $"{_baseUrl}/models"
                    : $"{_baseUrl}/models?pageToken={Uri.EscapeDataString(pageToken)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", apiKey.Trim());

                string body = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken).ConfigureAwait(false);

                using JsonDocument doc = ParseJsonDocument(body, apiKey);
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
                if (normalizedProvider == "Anthropic")
                {
                    string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? AnthropicBaseUrl : customEndpointUrl!.TrimEnd('/');
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseEndpoint}/models");
                    req.Headers.Add("x-api-key", apiKey.Trim());
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    string body = await SendAndReadBodyAsync(req, apiKey, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                    return VerifyModelInDataArray(body, normalizedTarget, "Anthropic", apiKey);
                }

                if (normalizedProvider == "OpenAI")
                {
                    string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? OpenAiBaseUrl : customEndpointUrl!.TrimEnd('/');
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseEndpoint}/models");
                    req.Headers.Add("Authorization", $"Bearer {apiKey.Trim()}");
                    string body = await SendAndReadBodyAsync(req, apiKey, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                    return VerifyModelInDataArray(body, normalizedTarget, "OpenAI", apiKey);
                }

                IReadOnlyList<string> available = await ListModelsAsync(apiKey, TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);

                foreach (string model in available)
                {
                    if (string.Equals(model, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, $"Model '{normalizedTarget}' verified via ListModels ({available.Count} models available).");
                    }
                }

                return (false, $"Warning: configured model '{normalizedTarget}' was not returned by ListModels ({available.Count} models available).");
            }
            catch (GeminiApiException ex)
            {
                return (false, $"Model check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Calls the configured LLM provider using structured JSON output.
        /// </summary>
        public Task<string> GenerateGraphJsonAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            JsonElement responseJsonSchema,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null)
        {
            string normalizedProvider = NormalizeProvider(provider);
            if (normalizedProvider == "Anthropic")
            {
                string augmentedSystem = $"{systemInstruction}\n\nReturn ONLY raw JSON strictly conforming to this JSON Schema (no markdown fences or prose):\n{responseJsonSchema.GetRawText()}";
                return SendAnthropicAsync(apiKey, modelId, augmentedSystem, userPrompt, temperature: 0.1, stripCodeFences: true, timeout, customEndpointUrl, cancellationToken);
            }

            if (normalizedProvider == "OpenAI")
            {
                string augmentedSystem = $"{systemInstruction}\n\nReturn ONLY a JSON object strictly conforming to this JSON Schema:\n{responseJsonSchema.GetRawText()}";
                return SendOpenAiAsync(apiKey, modelId, augmentedSystem, userPrompt, temperature: 0.1, jsonMode: true, timeout, customEndpointUrl, cancellationToken);
            }

            object generationConfig = new
            {
                temperature = 0.1,
                responseMimeType = "application/json",
                responseJsonSchema
            };

            return SendGeminiGenerateContentAsync(
                apiKey,
                modelId,
                systemInstruction,
                userPrompt,
                generationConfig,
                stripCodeFences: true,
                timeout,
                customEndpointUrl,
                cancellationToken);
        }

        /// <summary>
        /// Calls the configured LLM provider for plain-text or markdown explanations (used by Explain Selection).
        /// </summary>
        public Task<string> GenerateTextAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            string provider = "Gemini",
            string? customEndpointUrl = null)
        {
            string normalizedProvider = NormalizeProvider(provider);
            if (normalizedProvider == "Anthropic")
            {
                return SendAnthropicAsync(apiKey, modelId, systemInstruction, userPrompt, temperature: 0.2, stripCodeFences: false, timeout, customEndpointUrl, cancellationToken);
            }

            if (normalizedProvider == "OpenAI")
            {
                return SendOpenAiAsync(apiKey, modelId, systemInstruction, userPrompt, temperature: 0.2, jsonMode: false, timeout, customEndpointUrl, cancellationToken);
            }

            object generationConfig = new
            {
                temperature = 0.2
            };

            return SendGeminiGenerateContentAsync(
                apiKey,
                modelId,
                systemInstruction,
                userPrompt,
                generationConfig,
                stripCodeFences: false,
                timeout,
                customEndpointUrl,
                cancellationToken);
        }

        private async Task<string> SendGeminiGenerateContentAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            object generationConfig,
            bool stripCodeFences,
            TimeSpan? timeout,
            string? customEndpointUrl,
            CancellationToken cancellationToken)
        {
            ValidateKeyAndModel(apiKey, modelId, out string normalizedModelId);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? _baseUrl : customEndpointUrl!.TrimEnd('/');
            string url = $"{baseEndpoint}/models/{Uri.EscapeDataString(normalizedModelId)}:generateContent";

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
                        parts = new[]
                        {
                            new { text = userPrompt }
                        }
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

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(45);
            string responseBody = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return ExtractGeminiCandidateText(responseBody, apiKey, stripCodeFences);
        }

        private async Task<string> SendAnthropicAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            double temperature,
            bool stripCodeFences,
            TimeSpan? timeout,
            string? customEndpointUrl,
            CancellationToken cancellationToken)
        {
            ValidateKeyAndModel(apiKey, modelId, out string normalizedModelId);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? AnthropicBaseUrl : customEndpointUrl!.TrimEnd('/');
            string url = $"{baseEndpoint}/messages";

            var payload = new
            {
                model = normalizedModelId,
                max_tokens = 4096,
                temperature,
                system = systemInstruction,
                messages = new[]
                {
                    new { role = "user", content = userPrompt }
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", apiKey.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
            string responseBody = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return ExtractAnthropicText(responseBody, apiKey, stripCodeFences);
        }

        private async Task<string> SendOpenAiAsync(
            string apiKey,
            string modelId,
            string systemInstruction,
            string userPrompt,
            double temperature,
            bool jsonMode,
            TimeSpan? timeout,
            string? customEndpointUrl,
            CancellationToken cancellationToken)
        {
            ValidateKeyAndModel(apiKey, modelId, out string normalizedModelId);
            string baseEndpoint = string.IsNullOrWhiteSpace(customEndpointUrl) ? OpenAiBaseUrl : customEndpointUrl!.TrimEnd('/');
            string url = $"{baseEndpoint}/chat/completions";

            object payload = jsonMode
                ? new
                {
                    model = normalizedModelId,
                    temperature,
                    response_format = new { type = "json_object" },
                    messages = new[]
                    {
                        new { role = "system", content = systemInstruction },
                        new { role = "user", content = userPrompt }
                    }
                }
                : new
                {
                    model = normalizedModelId,
                    temperature,
                    messages = new[]
                    {
                        new { role = "system", content = systemInstruction },
                        new { role = "user", content = userPrompt }
                    }
                };

            string requestJson = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bearer {apiKey.Trim()}");

            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
            string responseBody = await SendAndReadBodyAsync(request, apiKey, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            return ExtractOpenAiText(responseBody, apiKey, jsonMode);
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

        private static string NormalizeProvider(string? provider)
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

        internal static string ScrubSecret(string? message, string? secret)
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
                throw new GeminiApiException(
                    $"LLM API request timed out after {timeout.TotalSeconds:F0}s.",
                    statusCode: null,
                    isTransient: true,
                    innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                throw new GeminiApiException(
                    $"LLM API network error: {ScrubSecret(ex.Message, apiKey)}",
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
                    throw new GeminiApiException(
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

                return (false, $"Warning: '{targetModel}' was not returned by {providerLabel} models list ({count} models available).");
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
                throw new GeminiApiException(
                    $"LLM API returned invalid JSON envelope: {ScrubSecret(ex.Message, apiKey)}",
                    innerException: ex);
            }
        }

        private static string ExtractGeminiCandidateText(string responseBody, string apiKey, bool stripCodeFences)
        {
            using JsonDocument doc = ParseJsonDocument(responseBody, apiKey);

            if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                throw new GeminiApiException("Gemini API response contained no candidates.");
            }

            JsonElement firstCandidate = candidates[0];
            if (!firstCandidate.TryGetProperty("content", out JsonElement content) ||
                !content.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                throw new GeminiApiException("Gemini API candidate contained no content parts.");
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

            return FinalizeExtractedText(sb.ToString(), stripCodeFences, "Gemini");
        }

        private static string ExtractAnthropicText(string responseBody, string apiKey, bool stripCodeFences)
        {
            using JsonDocument doc = ParseJsonDocument(responseBody, apiKey);

            if (!doc.RootElement.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array ||
                content.GetArrayLength() == 0)
            {
                throw new GeminiApiException("Anthropic API response contained no content blocks.");
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

            return FinalizeExtractedText(sb.ToString(), stripCodeFences, "Anthropic");
        }

        private static string ExtractOpenAiText(string responseBody, string apiKey, bool stripCodeFences)
        {
            using JsonDocument doc = ParseJsonDocument(responseBody, apiKey);

            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw new GeminiApiException("OpenAI API response contained no choices.");
            }

            JsonElement first = choices[0];
            if (!first.TryGetProperty("message", out JsonElement msg) ||
                !msg.TryGetProperty("content", out JsonElement contentEl) ||
                contentEl.ValueKind != JsonValueKind.String)
            {
                throw new GeminiApiException("OpenAI API choice contained no message content.");
            }

            return FinalizeExtractedText(contentEl.GetString() ?? string.Empty, stripCodeFences, "OpenAI");
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
                throw new GeminiApiException($"{providerLabel} API returned empty response text.");
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
