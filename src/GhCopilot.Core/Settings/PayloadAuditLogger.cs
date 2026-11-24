using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhCopilot.Core.Settings
{
    public sealed class PayloadAuditEntry
    {
        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("sendCanvasDataEnabled")]
        public bool SendCanvasDataEnabled { get; set; }

        [JsonPropertyName("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("responseTokens")]
        public int ResponseTokens { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("systemInstruction")]
        public string SystemInstruction { get; set; } = string.Empty;

        [JsonPropertyName("promptText")]
        public string PromptText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Writes a local audit log of every outbound prompt payload and per-turn token/duration metrics
    /// so IT/users can verify data privacy and metering.
    /// </summary>
    public static class PayloadAuditLogger
    {
        private static readonly object WriteLock = new object();

        public static string GetDefaultAuditLogPath()
        {
            return SharedCopilotPaths.GetAuditLogPath();
        }

        public static void LogSentPayload(
            string action,
            string modelId,
            bool sendCanvasDataEnabled,
            string systemInstruction,
            string promptText,
            string? filePath = null)
        {
            WriteEntry(new PayloadAuditEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Action = action,
                ModelId = modelId,
                SendCanvasDataEnabled = sendCanvasDataEnabled,
                SystemInstruction = systemInstruction,
                PromptText = promptText
            }, filePath);
        }

        public static void LogTurnCompletion(
            string action,
            string provider,
            string modelId,
            bool sendCanvasDataEnabled,
            string systemInstruction,
            string promptText,
            int promptTokens,
            int responseTokens,
            long durationMs,
            string? filePath = null)
        {
            WriteEntry(new PayloadAuditEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Action = action,
                Provider = provider ?? string.Empty,
                ModelId = modelId ?? string.Empty,
                SendCanvasDataEnabled = sendCanvasDataEnabled,
                PromptTokens = Math.Max(0, promptTokens),
                ResponseTokens = Math.Max(0, responseTokens),
                DurationMs = Math.Max(0, durationMs),
                SystemInstruction = systemInstruction ?? string.Empty,
                PromptText = promptText ?? string.Empty
            }, filePath);
        }

        private static void WriteEntry(PayloadAuditEntry entry, string? filePath)
        {
            string targetPath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultAuditLogPath() : filePath!;

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

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
