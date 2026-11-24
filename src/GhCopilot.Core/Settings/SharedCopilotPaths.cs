using System;
using System.IO;

namespace GhCopilot.Core.Settings
{
    /// <summary>
    /// Resolves the shared settings, DPAPI secret store, audit log, and headless endpoint directory
    /// used across Rhino, Grasshopper, and external CAD/BIM host integrations.
    /// </summary>
    public static class SharedCopilotPaths
    {
        public const string HomeEnvVarName = "GHCOPILOT_HOME";

        public static string GetSharedDirectory()
        {
            string? overrideDir = Environment.GetEnvironmentVariable(HomeEnvVarName);
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                return overrideDir!.Trim();
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "GhCopilot");
        }

        public static string GetSettingsPath()
        {
            return Path.Combine(GetSharedDirectory(), "settings.json");
        }

        public static string GetApiKeyPath()
        {
            return Path.Combine(GetSharedDirectory(), "api-key.dpapi");
        }

        public static string GetCompanyDefaultsPath()
        {
            return Path.Combine(GetSharedDirectory(), "company-defaults.json");
        }

        public static string GetAuditLogPath()
        {
            return Path.Combine(GetSharedDirectory(), "sent-payloads.jsonl");
        }

        public static string GetHeadlessDirectory()
        {
            return Path.Combine(GetSharedDirectory(), "headless");
        }
    }
}
