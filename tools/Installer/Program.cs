using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GhCopilot.Installer
{
    internal static class Program
    {
        private const string Version = "0.1.0";
        private const string RhinoPlugInId = "8A4E9C12-3B7D-4F6A-9E21-5C8D7A1B3E40";
        private const string DefaultModelId = "gemini-2.5-flash";
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("GhCopilot.GeminiApiKey.v1");

        private const int RmCchMaxAppName = 255;
        private const int RmCchMaxSvcName = 63;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RmCchMaxAppName + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RmCchMaxSvcName + 1)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string[] rgsFilenames,
            uint nApplications,
            [In] RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices,
            string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
            ref uint lpdwRebootReasons);

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    InstallCore(apiKey: null, modelId: null, sharedFolder: null, preferredRuntime: null);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Install failed: {ex.Message}");
                    return 1;
                }
            }

            if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    UninstallCore();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Uninstall failed: {ex.Message}");
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }

        internal static string GetYakPackageRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "McNeel", "Rhinoceros", "packages", "8.0", "GhCopilot");
        }

        internal static string GetYakVersionDir()
        {
            return Path.Combine(GetYakPackageRoot(), Version);
        }

        internal static string GetLegacyGhLibrariesDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Grasshopper", "Libraries", "GhCopilot");
        }

        internal static string GetSettingsDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "GhCopilot");
        }

        internal static IReadOnlyList<string> GetRunningHostProcesses()
        {
            var running = new List<string>();
            if (Process.GetProcessesByName("Rhino").Length > 0)
            {
                running.Add("Rhino 8 (Rhino.exe)");
            }
            if (Process.GetProcessesByName("Revit").Length > 0)
            {
                running.Add("Autodesk Revit / Rhino.Inside.Revit (Revit.exe)");
            }
            return running;
        }

        internal static bool IsRhinoRunning()
        {
            return GetRunningHostProcesses().Count > 0;
        }

        internal static string DetectPreferredRhinoRuntime()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string settingsFile = Path.Combine(appData, "McNeel", "Rhinoceros", "8.0", "settings", "settings-Scheme__Default.xml");
                if (File.Exists(settingsFile))
                {
                    string xml = File.ReadAllText(settingsFile, Encoding.UTF8);
                    if (xml.IndexOf("<entry key=\"LoadNetFramework\">True</entry>", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "net48";
                    }
                }
            }
            catch
            {
                // Ignore settings read errors and fall back to Rhino 8's default .NET 7 runtime.
            }

            return "net7.0";
        }

        internal static string InstallCore(
            string? apiKey,
            string? modelId,
            string? sharedFolder,
            string? preferredRuntime)
        {
            string packageRoot = GetYakPackageRoot();
            string installDir = GetYakVersionDir();
            string legacyDir = GetLegacyGhLibrariesDir();

            EnsureNoLockedFiles(packageRoot);
            EnsureNoLockedFiles(legacyDir);

            DeleteDirectoryIfExists(packageRoot);
            Directory.CreateDirectory(installDir);

            // Extract embedded multi-target Yak package (manifest.yml, net48/, net7.0/).
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream? payloadStream = asm.GetManifestResourceStream("Payload.yak"))
            {
                if (payloadStream is null)
                {
                    throw new InvalidOperationException("Embedded Payload.yak resource was not found.");
                }

                using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read);
                string fullDestRoot = Path.GetFullPath(installDir);

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string destinationPath = Path.GetFullPath(Path.Combine(installDir, relativePath));
                    if (!destinationPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Unsafe archive path: {entry.FullName}");
                    }

                    string? parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    entry.ExtractToFile(destinationPath, overwrite: true);
                    ClearMarkOfTheWeb(destinationPath);
                }
            }

            // Write Yak's active version pointer (manifest.txt) expected by Rhino 8 PackageManager.
            string manifestTxtPath = Path.Combine(packageRoot, "manifest.txt");
            File.WriteAllText(manifestTxtPath, Version + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ClearMarkOfTheWeb(manifestTxtPath);

            // Remove legacy Grasshopper\Libraries\GhCopilot folder to prevent duplicate GH_AssemblyInfo ID warnings.
            DeleteDirectoryIfExists(legacyDir);

            // Register Rhino 8 PlugIn in HKCU matching Rhino 8's native .NET plug-in schema (root + PlugIn + CommandList).
            string runtimeFolder = string.Equals(preferredRuntime, "net7.0", StringComparison.OrdinalIgnoreCase)
                ? "net7.0"
                : (string.Equals(preferredRuntime, "net48", StringComparison.OrdinalIgnoreCase)
                    ? "net48"
                    : DetectPreferredRhinoRuntime());

            string rhpPath = Path.Combine(installDir, runtimeFolder, "GhCopilot.Rhino.rhp");
            if (!File.Exists(rhpPath))
            {
                rhpPath = Path.Combine(installDir, "net48", "GhCopilot.Rhino.rhp");
            }

            string regPath = $@"Software\McNeel\Rhinoceros\8.0\Plug-Ins\{RhinoPlugInId.ToLowerInvariant()}";
            Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(regPath))
            {
                key.SetValue("Name", "GH Copilot", RegistryValueKind.String);
                key.SetValue("EnglishName", "GH Copilot", RegistryValueKind.String);
                key.SetValue("Description", "AI copilot for Grasshopper and Rhino 8.", RegistryValueKind.String);
                key.SetValue("Organization", "GH Copilot", RegistryValueKind.String);
                key.SetValue("FileName", rhpPath, RegistryValueKind.String);
                key.SetValue("LoadMode", 1, RegistryValueKind.DWord);
                key.SetValue("Type", 16, RegistryValueKind.DWord);
                key.SetValue("IsDotNETPlugIn", 1, RegistryValueKind.DWord);
                key.SetValue("AddToHelpMenu", 0, RegistryValueKind.DWord);
                key.SetValue("DirectoryInstall", 1, RegistryValueKind.DWord);

                using (RegistryKey pluginSubKey = key.CreateSubKey("PlugIn"))
                {
                    pluginSubKey.SetValue("FileName", rhpPath, RegistryValueKind.String);
                }

                using (RegistryKey commandListSubKey = key.CreateSubKey("CommandList"))
                {
                    commandListSubKey.SetValue("GhCopilot", "2;GhCopilot", RegistryValueKind.String);
                }
            }

            // Save optional pre-configuration (settings.json and DPAPI-encrypted API key).
            SaveOptionalConfiguration(apiKey, modelId, sharedFolder);

            return $"Installed GH Copilot v{Version} to:\r\n{installDir}\r\n\r\n" +
                   $"Registered Rhino 8 PlugIn:\r\n{rhpPath}\r\n\r\n" +
                   "Open Rhino 8 and run 'GhCopilot' — Direct Rhino & Grasshopper tools will load automatically.";
        }

        internal static string UninstallCore()
        {
            string packageRoot = GetYakPackageRoot();
            string legacyDir = GetLegacyGhLibrariesDir();
            EnsureNoLockedFiles(packageRoot);
            EnsureNoLockedFiles(legacyDir);
            DeleteDirectoryIfExists(packageRoot);
            DeleteDirectoryIfExists(legacyDir);

            string regPath = $@"Software\McNeel\Rhinoceros\8.0\Plug-Ins\{RhinoPlugInId.ToLowerInvariant()}";
            Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);

            return "Uninstalled GH Copilot plugin files and Rhino 8 registration.";
        }

        private static void DeleteDirectoryIfExists(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(filePath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // Ignore attribute reset failures and let Delete report real IO errors if any.
                }
            }

            Directory.Delete(directoryPath, recursive: true);
        }

        private static void EnsureNoLockedFiles(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            const int maxAttempts = 5;
            foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                Exception? lastEx = null;
                bool unlocked = false;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        FileAttributes attrs = File.GetAttributes(filePath);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
                        }

                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        unlocked = true;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        lastEx = ex;
                        if (attempt + 1 < maxAttempts)
                        {
                            Thread.Sleep(400);
                        }
                    }
                }

                if (!unlocked)
                {
                    IReadOnlyList<string> lockers = GetLockingProcesses(filePath);
                    string processDetails = lockers.Count > 0
                        ? $"Locked by: {string.Join(", ", lockers)}."
                        : "Check Task Manager for lingering Rhino.exe, Revit.exe (Rhino.Inside.Revit), or Rhino Compute processes.";

                    throw new InvalidOperationException(
                        $"Cannot overwrite '{Path.GetFileName(filePath)}' because it is open in another process. {processDetails}\n" +
                        $"Close Rhino 8 and Autodesk Revit completely, wait a couple of seconds, and run Setup again.\n" +
                        $"File: {filePath}",
                        lastEx);
                }
            }
        }

        private static IReadOnlyList<string> GetLockingProcesses(string filePath)
        {
            var results = new List<string>();
            uint handle = 0;
            try
            {
                string key = Guid.NewGuid().ToString("N");
                if (RmStartSession(out handle, 0, key) != 0)
                {
                    return results;
                }

                if (RmRegisterResources(handle, 1, new[] { filePath }, 0, null, 0, null) != 0)
                {
                    return results;
                }

                uint pnProcInfoNeeded = 0;
                uint pnProcInfo = 0;
                uint rebootReasons = 0;
                int res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref rebootReasons);
                if (res == 234 && pnProcInfoNeeded > 0) // ERROR_MORE_DATA
                {
                    var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                    pnProcInfo = pnProcInfoNeeded;
                    res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref rebootReasons);
                    if (res == 0)
                    {
                        var seen = new HashSet<int>();
                        for (int i = 0; i < pnProcInfo; i++)
                        {
                            int pid = processInfo[i].Process.dwProcessId;
                            if (!seen.Add(pid))
                            {
                                continue;
                            }

                            string appName = processInfo[i].strAppName;
                            if (string.IsNullOrWhiteSpace(appName))
                            {
                                try
                                {
                                    appName = Process.GetProcessById(pid).ProcessName;
                                }
                                catch
                                {
                                    appName = "Unknown process";
                                }
                            }

                            results.Add($"{appName} (PID {pid})");
                        }
                    }
                }
            }
            catch
            {
                // Ignore Restart Manager errors and fall back to generic process guidance.
            }
            finally
            {
                if (handle != 0)
                {
                    try { RmEndSession(handle); } catch { }
                }
            }

            return results;
        }

        private static void SaveOptionalConfiguration(string? apiKey, string? modelId, string? sharedFolder)
        {
            string settingsDir = GetSettingsDir();
            bool hasModelOrFolder = !string.IsNullOrWhiteSpace(modelId) || !string.IsNullOrWhiteSpace(sharedFolder);

            if (hasModelOrFolder)
            {
                Directory.CreateDirectory(settingsDir);
                string settingsFile = Path.Combine(settingsDir, "settings.json");

                string resolvedModel = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId!.Trim();
                string resolvedFolder = string.IsNullOrWhiteSpace(sharedFolder) ? string.Empty : sharedFolder!.Trim();

                string json = "{\n" +
                              $"  \"modelId\": \"{EscapeJson(resolvedModel)}\",\n" +
                              $"  \"sharedLibraryFolder\": \"{EscapeJson(resolvedFolder)}\",\n" +
                              "  \"sendCanvasData\": false,\n" +
                              "  \"planFirstMode\": true,\n" +
                              "  \"requestTimeoutSeconds\": 180\n" +
                              "}\n";
                File.WriteAllText(settingsFile, json, Encoding.UTF8);
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                Directory.CreateDirectory(settingsDir);
                string keyFile = Path.Combine(settingsDir, "api-key.dpapi");
                byte[] plain = Encoding.UTF8.GetBytes(apiKey!.Trim());
                byte[] protectedBytes = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
                Array.Clear(plain, 0, plain.Length);
                File.WriteAllBytes(keyFile, protectedBytes);
            }
        }

        private static void ClearMarkOfTheWeb(string filePath)
        {
            try
            {
                DeleteFileW(filePath + ":Zone.Identifier");
            }
            catch
            {
                // Ignore if ADS does not exist or filesystem does not support streams.
            }
        }

        private static string EscapeJson(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _apiKeyBox;
        private readonly TextBox _modelBox;
        private readonly TextBox _sharedFolderBox;
        private readonly ComboBox _runtimeCombo;
        private readonly Label _statusLabel;

        public SetupForm()
        {
            Text = "GH Copilot Setup (Rhino 8 / Grasshopper)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 390);
            Font = new Font("Segoe UI", 9f);

            var title = new Label
            {
                Text = "GH Copilot v0.1.0 — Standalone Installer",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Location = new Point(16, 14),
                AutoSize = true
            };

            var subtitle = new Label
            {
                Text = "Installs the multi-target Rhino 8 + Grasshopper plugin so it auto-loads on startup.",
                Location = new Point(16, 42),
                Size = new Size(488, 22)
            };

            var runtimeLabel = new Label
            {
                Text = "Rhino 8 .NET Runtime Registration:",
                Location = new Point(16, 74),
                AutoSize = true
            };

            _runtimeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(16, 94),
                Width = 488
            };
            _runtimeCombo.Items.AddRange(new object[]
            {
                "Auto-detect (Recommended)",
                "net7.0 (.NET Core - Rhino 8 Windows default)",
                "net48 (.NET Framework)"
            });
            _runtimeCombo.SelectedIndex = 0;

            var modelLabel = new Label
            {
                Text = "Optional — Gemini Model ID:",
                Location = new Point(16, 128),
                AutoSize = true
            };

            _modelBox = new TextBox
            {
                Text = "gemini-2.5-flash",
                Location = new Point(16, 148),
                Width = 488
            };

            var keyLabel = new Label
            {
                Text = "Optional — Gemini API Key (stored DPAPI-encrypted; leave blank to keep existing):",
                Location = new Point(16, 180),
                AutoSize = true
            };

            _apiKeyBox = new TextBox
            {
                UseSystemPasswordChar = true,
                Location = new Point(16, 200),
                Width = 488
            };

            var folderLabel = new Label
            {
                Text = "Optional — Shared Company Library Folder (.ghuser / .gh clusters):",
                Location = new Point(16, 232),
                AutoSize = true
            };

            _sharedFolderBox = new TextBox
            {
                Location = new Point(16, 252),
                Width = 400
            };

            var browseButton = new Button
            {
                Text = "Browse...",
                Location = new Point(422, 250),
                Size = new Size(82, 25)
            };
            browseButton.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog();
                if (!string.IsNullOrWhiteSpace(_sharedFolderBox.Text) && Directory.Exists(_sharedFolderBox.Text))
                {
                    dialog.SelectedPath = _sharedFolderBox.Text;
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _sharedFolderBox.Text = dialog.SelectedPath;
                }
            };

            var installButton = new Button
            {
                Text = "Install / Update GH Copilot",
                Location = new Point(16, 294),
                Size = new Size(210, 32)
            };
            installButton.Click += OnInstallClicked;

            var uninstallButton = new Button
            {
                Text = "Uninstall",
                Location = new Point(236, 294),
                Size = new Size(120, 32)
            };
            uninstallButton.Click += OnUninstallClicked;

            var closeButton = new Button
            {
                Text = "Close",
                Location = new Point(404, 294),
                Size = new Size(100, 32)
            };
            closeButton.Click += (_, _) => Close();

            _statusLabel = new Label
            {
                Text = Directory.Exists(Program.GetYakVersionDir())
                    ? $"Status: Currently installed at {Program.GetYakVersionDir()}"
                    : "Status: Not installed yet.",
                Location = new Point(16, 338),
                Size = new Size(488, 40)
            };

            Controls.AddRange(new Control[]
            {
                title, subtitle,
                runtimeLabel, _runtimeCombo,
                modelLabel, _modelBox,
                keyLabel, _apiKeyBox,
                folderLabel, _sharedFolderBox, browseButton,
                installButton, uninstallButton, closeButton,
                _statusLabel
            });
        }

        private void OnInstallClicked(object? sender, EventArgs e)
        {
            IReadOnlyList<string> runningHosts = Program.GetRunningHostProcesses();
            if (runningHosts.Count > 0)
            {
                DialogResult confirm = MessageBox.Show(
                    this,
                    $"The following host process(es) are currently running and may lock plugin files:\n\n• {string.Join("\n• ", runningHosts)}\n\nClose Rhino 8 and Autodesk Revit before installing/updating.\n\nContinue anyway?",
                    "GH Copilot Setup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                string? runtimeChoice = _runtimeCombo.SelectedIndex switch
                {
                    1 => "net7.0",
                    2 => "net48",
                    _ => null
                };

                string summary = Program.InstallCore(
                    _apiKeyBox.Text,
                    _modelBox.Text,
                    _sharedFolderBox.Text,
                    runtimeChoice);

                _apiKeyBox.Text = string.Empty;
                _statusLabel.Text = "Status: Installed cleanly. Open Rhino 8 & Grasshopper!";
                MessageBox.Show(this, summary, "GH Copilot Setup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Error: {ex.Message}";
                MessageBox.Show(this, $"Installation failed:\n\n{ex.Message}", "GH Copilot Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnUninstallClicked(object? sender, EventArgs e)
        {
            try
            {
                string summary = Program.UninstallCore();
                _statusLabel.Text = "Status: Uninstalled.";
                MessageBox.Show(this, summary, "GH Copilot Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Uninstall failed:\n\n{ex.Message}", "GH Copilot Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
