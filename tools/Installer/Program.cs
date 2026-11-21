using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

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

        internal static bool IsRhinoRunning()
        {
            return Process.GetProcessesByName("Rhino").Length > 0;
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

            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }
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

            // Remove legacy Grasshopper\Libraries\GhCopilot folder to prevent duplicate GH_AssemblyInfo ID warnings.
            string legacyDir = GetLegacyGhLibrariesDir();
            if (Directory.Exists(legacyDir))
            {
                Directory.Delete(legacyDir, recursive: true);
            }

            // Register Rhino 8 PlugIn in HKCU so Rhino loads GhCopilot.Rhino.rhp automatically on startup.
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

            string regPath = $@"Software\McNeel\Rhinoceros\8.0\Plug-Ins\{RhinoPlugInId}";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(regPath))
            {
                key.SetValue("Name", "GH Copilot", RegistryValueKind.String);
                key.SetValue("FileName", rhpPath, RegistryValueKind.String);
            }

            // Save optional pre-configuration (settings.json and DPAPI-encrypted API key).
            SaveOptionalConfiguration(apiKey, modelId, sharedFolder);

            return $"Installed GH Copilot v{Version} to:\r\n{installDir}\r\n\r\n" +
                   $"Registered Rhino 8 PlugIn:\r\n{rhpPath}\r\n\r\n" +
                   "Open Rhino 8 and Grasshopper — GH Copilot will load automatically.";
        }

        internal static string UninstallCore()
        {
            string packageRoot = GetYakPackageRoot();
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }

            string legacyDir = GetLegacyGhLibrariesDir();
            if (Directory.Exists(legacyDir))
            {
                Directory.Delete(legacyDir, recursive: true);
            }

            string regPath = $@"Software\McNeel\Rhinoceros\8.0\Plug-Ins\{RhinoPlugInId}";
            Registry.CurrentUser.DeleteSubKeyTree(regPath, throwOnMissingSubKey: false);

            return "Uninstalled GH Copilot plugin files and Rhino 8 registration.";
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
                              "  \"requestTimeoutSeconds\": 45\n" +
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
            if (Program.IsRhinoRunning())
            {
                DialogResult confirm = MessageBox.Show(
                    this,
                    "Rhino 8 appears to be running. Close Rhino before installing/updating so plugin files are not locked.\n\nContinue anyway?",
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
