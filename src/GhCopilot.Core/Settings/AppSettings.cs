using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhCopilot.Core.Settings
{
    /// <summary>
    /// User-configurable GH Copilot settings stored locally in JSON (never contains the API key).
    /// </summary>
    public sealed class AppSettings
    {
        public const string DefaultProvider = "Gemini";
        public const string DefaultModelId = "gemini-2.5-flash";
        public const int DefaultRequestTimeoutSeconds = 180;
        public const int DefaultRetrievalMaxItems = 90;
        public const int MinRetrievalMaxItems = 10;
        public const int MaxRetrievalMaxItems = 140;
        public const int DefaultMaxGraphNodes = 200;
        public const int MinMaxGraphNodes = 10;
        public const int MaxMaxGraphNodes = 500;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = DefaultProvider;

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = DefaultModelId;

        [JsonPropertyName("customEndpointUrl")]
        public string CustomEndpointUrl { get; set; } = string.Empty;

        [JsonPropertyName("companyEndpointUrl")]
        public string CompanyEndpointUrl { get; set; } = string.Empty;

        [JsonPropertyName("sharedLibraryFolder")]
        public string SharedLibraryFolder { get; set; } = string.Empty;

        [JsonPropertyName("sendCanvasData")]
        public bool SendCanvasData { get; set; }

        [JsonPropertyName("autoLayout")]
        public bool AutoLayout { get; set; } = true;

        [JsonPropertyName("planFirstMode")]
        public bool PlanFirstMode { get; set; } = true;

        [JsonPropertyName("autoVerifyViewportAfterBuild")]
        public bool AutoVerifyViewportAfterBuild { get; set; }

        [JsonPropertyName("forcePlanFirstAlways")]
        public bool ForcePlanFirstAlways { get; set; }

        [JsonPropertyName("retrievalMaxItems")]
        public int RetrievalMaxItems { get; set; } = DefaultRetrievalMaxItems;

        [JsonPropertyName("maxGraphNodes")]
        public int MaxGraphNodes { get; set; } = DefaultMaxGraphNodes;

        [JsonPropertyName("enableHeadlessBridge")]
        public bool EnableHeadlessBridge { get; set; } = true;

        [JsonPropertyName("requestTimeoutSeconds")]
        public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

        public string? GetEffectiveEndpointUrl()
        {
            if (!string.IsNullOrWhiteSpace(CustomEndpointUrl))
            {
                return CustomEndpointUrl.Trim();
            }

            if (!string.IsNullOrWhiteSpace(CompanyEndpointUrl))
            {
                return CompanyEndpointUrl.Trim();
            }

            return null;
        }

        public static string GetDefaultSettingsPath()
        {
            return SharedCopilotPaths.GetSettingsPath();
        }

        public static AppSettings Load(string? filePath = null)
        {
            bool useDefaultLocation = string.IsNullOrWhiteSpace(filePath);
            string path = useDefaultLocation ? GetDefaultSettingsPath() : filePath!;
            AppSettings result;

            if (!File.Exists(path))
            {
                result = new AppSettings();
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    result = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
                catch (Exception)
                {
                    result = new AppSettings();
                }
            }

            NormalizeAndApplyCompanyDefaults(result, checkCompanyDefaults: useDefaultLocation);
            return result;
        }

        private static void NormalizeAndApplyCompanyDefaults(AppSettings loaded, bool checkCompanyDefaults)
        {
            if (string.IsNullOrWhiteSpace(loaded.Provider))
            {
                loaded.Provider = DefaultProvider;
            }

            if (string.IsNullOrWhiteSpace(loaded.ModelId))
            {
                loaded.ModelId = DefaultModelId;
            }

            if (loaded.RequestTimeoutSeconds <= 45)
            {
                loaded.RequestTimeoutSeconds = DefaultRequestTimeoutSeconds;
            }

            if (loaded.RetrievalMaxItems <= 0)
            {
                loaded.RetrievalMaxItems = DefaultRetrievalMaxItems;
            }
            else
            {
                loaded.RetrievalMaxItems = Math.Max(MinRetrievalMaxItems, Math.Min(MaxRetrievalMaxItems, loaded.RetrievalMaxItems));
            }

            if (loaded.MaxGraphNodes <= 0)
            {
                loaded.MaxGraphNodes = DefaultMaxGraphNodes;
            }
            else
            {
                loaded.MaxGraphNodes = Math.Max(MinMaxGraphNodes, Math.Min(MaxMaxGraphNodes, loaded.MaxGraphNodes));
            }

            if (checkCompanyDefaults && string.IsNullOrWhiteSpace(loaded.CompanyEndpointUrl))
            {
                string companyDefaultsPath = SharedCopilotPaths.GetCompanyDefaultsPath();
                if (File.Exists(companyDefaultsPath))
                {
                    try
                    {
                        string defaultsJson = File.ReadAllText(companyDefaultsPath, Encoding.UTF8);
                        using JsonDocument doc = JsonDocument.Parse(defaultsJson);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("companyEndpointUrl", out JsonElement compEl) &&
                                compEl.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(compEl.GetString()))
                            {
                                loaded.CompanyEndpointUrl = compEl.GetString()!.Trim();
                            }
                            else if (doc.RootElement.TryGetProperty("customEndpointUrl", out JsonElement custEl) &&
                                     custEl.ValueKind == JsonValueKind.String &&
                                     !string.IsNullOrWhiteSpace(custEl.GetString()))
                            {
                                loaded.CompanyEndpointUrl = custEl.GetString()!.Trim();
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore malformed optional defaults file.
                    }
                }
            }
        }

        public void Save(string? filePath = null)
        {
            string path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultSettingsPath() : filePath!;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
    }
}
