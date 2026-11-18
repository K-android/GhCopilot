using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GhCopilot.Core.Settings
{
    /// <summary>
    /// Encrypts and stores the user's Gemini API key using Windows DPAPI (CurrentUser scope).
    /// Plain-text keys are never written to disk or logged.
    /// </summary>
    public static class SecretStore
    {
        public const string EnvVarName = "GEMINI_API_KEY";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GhCopilot.GeminiApiKey.v1");

        public static string GetDefaultKeyFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "GhCopilot", "api-key.dpapi");
        }

        public static void SaveApiKey(string apiKey, string? filePath = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key must not be empty.", nameof(apiKey));
            }

#if NET5_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows DPAPI secret storage is only supported on Windows.");
            }
#else
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Windows DPAPI secret storage is only supported on Windows.");
            }
#endif

            string path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultKeyFilePath() : filePath!;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] plainBytes = Encoding.UTF8.GetBytes(apiKey.Trim());
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            Array.Clear(plainBytes, 0, plainBytes.Length);

            File.WriteAllBytes(path, protectedBytes);
        }

        public static bool TryLoadApiKey(out string? apiKey, string? filePath = null, bool allowEnvironmentFallback = true)
        {
            apiKey = null;
            string path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultKeyFilePath() : filePath!;

#if NET5_0_OR_GREATER
            bool isWindows = OperatingSystem.IsWindows();
#else
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif
            if (File.Exists(path) && isWindows)
            {
                try
                {
                    byte[] protectedBytes = File.ReadAllBytes(path);
                    byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                    string decrypted = Encoding.UTF8.GetString(plainBytes).Trim();
                    Array.Clear(plainBytes, 0, plainBytes.Length);

                    if (!string.IsNullOrWhiteSpace(decrypted))
                    {
                        apiKey = decrypted;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Corrupted or different-user DPAPI blob: fall through safely without crashing.
                }
            }

            if (allowEnvironmentFallback)
            {
                string? envKey = Environment.GetEnvironmentVariable(EnvVarName);
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    apiKey = envKey!.Trim();
                    return true;
                }
            }

            return false;
        }

        public static void DeleteApiKey(string? filePath = null)
        {
            string path = string.IsNullOrWhiteSpace(filePath) ? GetDefaultKeyFilePath() : filePath!;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
