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

        [JsonPropertyName("sharedLibraryFolder")]
        public string SharedLibraryFolder { get; set; } = string.Empty;

        [JsonPropertyName("sendCanvasData")]
        public bool SendCanvasData { get; set; }

        [JsonPropertyName("autoLayout")]
        public bool AutoLayout { get; set; } = true;

        [JsonPropertyName("planFirstMode")]
        public bool PlanFirstMode { get; set; } = true;

        [JsonPropertyName("requestTimeoutSeconds")]
        public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

        public static string GetDefaultSettingsPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "GhCopilot", "settings.json");
        }

        public static AppSettings Load(string? filePath = null)
        {
            string path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultSettingsPath() : filePath!;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is null)
                {
                    return new AppSettings();
                }

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

                return loaded;
            }
            catch (Exception)
            {
                return new AppSettings();
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
