using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhCopilot.Core.Settings;

namespace GhCopilot.Core.Host
{
    public sealed class HeadlessPromptRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "auto";

        [JsonPropertyName("skipPlanFirst")]
        public bool SkipPlanFirst { get; set; } = true;

        [JsonPropertyName("captureViewport")]
        public bool CaptureViewport { get; set; }

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = AppSettings.DefaultRequestTimeoutSeconds;
    }

    public sealed class HeadlessPromptResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "ok";

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = string.Empty;

        [JsonPropertyName("objectsCreated")]
        public int ObjectsCreated { get; set; }

        [JsonPropertyName("objectsModified")]
        public int ObjectsModified { get; set; }

        [JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new List<string>();

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonPropertyName("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("responseTokens")]
        public int ResponseTokens { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;
    }

    public sealed class HeadlessEndpointInfo
    {
        [JsonPropertyName("pid")]
        public int Pid { get; set; }

        [JsonPropertyName("pipeName")]
        public string PipeName { get; set; } = string.Empty;

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("rhinoVersion")]
        public string RhinoVersion { get; set; } = string.Empty;

        [JsonPropertyName("documentName")]
        public string DocumentName { get; set; } = string.Empty;

        [JsonPropertyName("startedAtUtc")]
        public DateTime StartedAtUtc { get; set; }
    }

    /// <summary>
    /// Discovers active local headless GH Copilot endpoints (Rhino 8 and Rhino.Inside.Revit)
    /// and prunes stale PID registrations left by closed processes.
    /// </summary>
    public static class HeadlessEndpointDiscovery
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string BuildPipeName(int pid) => $"GhCopilot.Headless.{pid}";

        public static string GetEndpointFilePath(int pid, string? directory = null)
        {
            string dir = string.IsNullOrWhiteSpace(directory) ? SharedCopilotPaths.GetHeadlessDirectory() : directory!;
            return Path.Combine(dir, $"headless-{pid}.json");
        }

        public static void WriteEndpoint(HeadlessEndpointInfo info, string? directory = null)
        {
            if (info is null) throw new ArgumentNullException(nameof(info));
            if (info.Pid <= 0) throw new ArgumentException("PID must be positive.", nameof(info));
            if (string.IsNullOrWhiteSpace(info.PipeName))
            {
                info.PipeName = BuildPipeName(info.Pid);
            }

            string path = GetEndpointFilePath(info.Pid, directory);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(info, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        public static void RemoveEndpoint(int pid, string? directory = null)
        {
            if (pid <= 0) return;
            try
            {
                string path = GetEndpointFilePath(pid, directory);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup.
            }
        }

        public static List<HeadlessEndpointInfo> ListActiveEndpoints(
            string? directory = null,
            Func<int, bool>? isProcessAlive = null)
        {
            string dir = string.IsNullOrWhiteSpace(directory) ? SharedCopilotPaths.GetHeadlessDirectory() : directory!;
            var active = new List<HeadlessEndpointInfo>();
            if (!Directory.Exists(dir))
            {
                return active;
            }

            Func<int, bool> aliveCheck = isProcessAlive ?? DefaultIsProcessAlive;
            foreach (string file in Directory.GetFiles(dir, "headless-*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    HeadlessEndpointInfo? info = JsonSerializer.Deserialize<HeadlessEndpointInfo>(json, JsonOptions);
                    if (info is null || info.Pid <= 0 || string.IsNullOrWhiteSpace(info.PipeName) || !aliveCheck(info.Pid))
                    {
                        TryDeleteFile(file);
                        continue;
                    }

                    active.Add(info);
                }
                catch (Exception)
                {
                    // Skip unreadable or transiently locked files.
                }
            }

            active.Sort((a, b) => b.StartedAtUtc.CompareTo(a.StartedAtUtc));
            return active;
        }

        private static bool DefaultIsProcessAlive(int pid)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                try
                {
                    return !p.HasExited;
                }
                catch (Exception)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Ignore cleanup failure.
            }
        }
    }
}
