using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GhCopilot.Core.Host
{
    /// <summary>Best-effort startup logging; deliberately independent of settings, JSON and host SDKs.</summary>
    public static class StartupDiagnostics
    {
        private static readonly object Sync = new object();

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhCopilot", "startup.log");

        public static void Write(string stage, Exception? error = null)
        {
            try
            {
                string text = DateTime.UtcNow.ToString("O") + " " + stage + Environment.NewLine;
                if (error != null) text += error + Environment.NewLine;
                lock (Sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                        File.WriteAllText(LogPath, string.Empty);
                    File.AppendAllText(LogPath, text, Encoding.UTF8);
                }
            }
            catch (Exception) { /* Diagnostics must never break Rhino startup. */ }
        }

        public static void WriteEnvironment(Assembly pluginAssembly)
        {
            Write("Plug-in assembly: " + pluginAssembly.FullName + " @ " + pluginAssembly.Location);
            Write("CLR: " + RuntimeInformation.FrameworkDescription + " (" + Environment.Version + "); 64-bit: " + Environment.Is64BitProcess);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = assembly.GetName().Name;
                if (name != "Eto" && name != "Eto.Wpf" && name != "RhinoCommon" && name != "GhCopilot.Core") continue;
                try { Write("Loaded assembly: " + assembly.FullName + " @ " + assembly.Location); }
                catch (Exception) { }
            }
        }
    }
}