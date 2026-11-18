using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhCopilot.Core.Index;

namespace GhCopilot.Core.Settings
{
    public sealed class PayloadAuditEntry
    {
        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("sendCanvasDataEnabled")]
        public bool SendCanvasDataEnabled { get; set; }

        [JsonPropertyName("systemInstruction")]
        public string SystemInstruction { get; set; } = string.Empty;

        [JsonPropertyName("promptText")]
        public string PromptText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Writes a local audit log of every outbound prompt payload sent to Gemini so IT/users can verify data privacy.
    /// </summary>
    public static class PayloadAuditLogger
    {
        private static readonly object WriteLock = new object();

        public static string GetDefaultAuditLogPath()
        {
            return Path.Combine(IndexStore.GetDefaultDataDirectory(), "sent-payloads.jsonl");
        }

        public static void LogSentPayload(
            string action,
            string modelId,
            bool sendCanvasDataEnabled,
            string systemInstruction,
            string promptText,
            string? filePath = null)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultAuditLogPath() : filePath!;

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var entry = new PayloadAuditEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    Action = action,
                    ModelId = modelId,
                    SendCanvasDataEnabled = sendCanvasDataEnabled,
                    SystemInstruction = systemInstruction,
                    PromptText = promptText
                };

                string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                lock (WriteLock)
                {
                    File.AppendAllText(targetPath, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Best-effort audit logging: never block generation if file cannot be opened.
            }
        }
    }
}
