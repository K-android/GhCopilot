using System;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core.Host;
using GhCopilot.Core.Settings;

namespace GhCopilot.UI
{
    /// <summary>
    /// Settings tab managing BYOK LLM provider (Gemini / Anthropic / OpenAI), DPAPI-encrypted API key,
    /// model ID, shared library folder, auto-layout, and privacy toggle.
    /// </summary>
    internal sealed class SettingsTab : Panel
    {
        private readonly DropDown _providerDropDown;
        private readonly PasswordBox _apiKeyBox;
        private readonly Label _keyStatusLabel;
        private readonly TextBox _modelIdBox;
        private readonly TextBox _customEndpointBox;
        private readonly TextBox _sharedFolderBox;
        private readonly CheckBox _sendCanvasDataCheck;
        private readonly CheckBox _autoLayoutCheck;
        private readonly Label _statusLabel;

        public SettingsTab()
        {
            AppSettings settings = AppSettings.Load();

            _providerDropDown = new DropDown();
            _providerDropDown.Items.Add(new ListItem { Key = "Gemini", Text = "Gemini (Google)" });
            _providerDropDown.Items.Add(new ListItem { Key = "Anthropic", Text = "Anthropic (Claude)" });
            _providerDropDown.Items.Add(new ListItem { Key = "OpenAI", Text = "OpenAI / Compatible" });
            _providerDropDown.SelectedKey = string.IsNullOrWhiteSpace(settings.Provider) ? "Gemini" : settings.Provider;
            _providerDropDown.SelectedKeyChanged += OnProviderChanged;

            _apiKeyBox = new PasswordBox();
            _keyStatusLabel = new Label();

            _modelIdBox = new TextBox
            {
                Text = settings.ModelId,
                PlaceholderText = AppSettings.DefaultModelId
            };

            var verifyModelButton = new Button { Text = "Verify Model" };
            verifyModelButton.Click += OnVerifyModelClicked;

            _customEndpointBox = new TextBox
            {
                Text = settings.CustomEndpointUrl,
                PlaceholderText = "(Optional) Custom API base URL"
            };

            _sharedFolderBox = new TextBox
            {
                Text = settings.SharedLibraryFolder,
                PlaceholderText = @"\\server\shared\gh-library"
            };

            var browseButton = new Button { Text = "Browse..." };
            browseButton.Click += OnBrowseFolderClicked;

            _sendCanvasDataCheck = new CheckBox
            {
                Text = "Send canvas data & selected outputs (metadata only, never geometry)",
                Checked = settings.SendCanvasData
            };

            _autoLayoutCheck = new CheckBox
            {
                Text = "Topological layered auto-layout (prevents overlapping nodes)",
                Checked = settings.AutoLayout
            };

            var saveButton = new Button { Text = "Save Settings" };
            saveButton.Click += OnSaveClicked;

            var dumpIndexButton = new Button { Text = "Debug: Dump Index JSON" };
            dumpIndexButton.Click += OnDumpIndexClicked;

            _statusLabel = new Label
            {
                Text = string.Empty
            };

            var layout = new DynamicLayout
            {
                Padding = new Padding(8),
                Spacing = new Size(6, 6)
            };

            layout.BeginVertical();
            layout.Add(new Label { Text = "LLM Provider (BYOK):" });
            layout.Add(_providerDropDown);
            layout.Add(new Label { Text = "API Key (stored encrypted via Windows DPAPI):" });
            layout.Add(_apiKeyBox);
            layout.Add(_keyStatusLabel);
            layout.Add(new Label { Text = "Model ID:" });
            layout.AddRow(_modelIdBox, verifyModelButton);
            layout.Add(new Label { Text = "Custom Endpoint URL (Optional):" });
            layout.Add(_customEndpointBox);
            layout.Add(new Label { Text = "Shared Company Library Folder:" });
            layout.AddRow(_sharedFolderBox, browseButton);
            layout.Add(_sendCanvasDataCheck);
            layout.Add(_autoLayoutCheck);
            layout.AddRow(saveButton, dumpIndexButton);
            layout.Add(_statusLabel);
            layout.EndVertical();
            layout.Add(null);

            Content = new Scrollable
            {
                Border = BorderType.None,
                ExpandContentWidth = true,
                ExpandContentHeight = false,
                Content = layout
            };
            RefreshKeyStatus();
        }

        private void OnProviderChanged(object? sender, EventArgs e)
        {
            string selected = _providerDropDown.SelectedKey ?? "Gemini";
            if (selected == "Anthropic" &&
                (string.IsNullOrWhiteSpace(_modelIdBox.Text) || _modelIdBox.Text.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) || _modelIdBox.Text.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)))
            {
                _modelIdBox.Text = "claude-opus-4-6";
            }
            else if (selected == "OpenAI" &&
                     (string.IsNullOrWhiteSpace(_modelIdBox.Text) || _modelIdBox.Text.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) || _modelIdBox.Text.StartsWith("claude", StringComparison.OrdinalIgnoreCase)))
            {
                _modelIdBox.Text = "gpt-4.1";
            }
            else if (selected == "Gemini" &&
                     (string.IsNullOrWhiteSpace(_modelIdBox.Text) || !_modelIdBox.Text.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)))
            {
                _modelIdBox.Text = AppSettings.DefaultModelId;
            }
        }

        private void RefreshKeyStatus()
        {
            bool hasStoredKey = SecretStore.TryLoadApiKey(out _, allowEnvironmentFallback: false);
            bool hasEnvKey = !hasStoredKey && SecretStore.TryLoadApiKey(out _, allowEnvironmentFallback: true);

            if (hasStoredKey)
            {
                _keyStatusLabel.Text = "Key status: Encrypted DPAPI key saved on disk.";
            }
            else if (hasEnvKey)
            {
                _keyStatusLabel.Text = $"Key status: Using {SecretStore.EnvVarName} environment variable.";
            }
            else
            {
                _keyStatusLabel.Text = "Key status: No API key configured.";
            }
        }

        private void OnBrowseFolderClicked(object? sender, EventArgs e)
        {
            var dialog = new SelectFolderDialog
            {
                Title = "Select Shared Company Library Folder"
            };

            if (!string.IsNullOrWhiteSpace(_sharedFolderBox.Text))
            {
                dialog.Directory = _sharedFolderBox.Text.Trim();
            }

            if (dialog.ShowDialog(this) == DialogResult.Ok && !string.IsNullOrWhiteSpace(dialog.Directory))
            {
                _sharedFolderBox.Text = dialog.Directory;
            }
        }

        private void OnSaveClicked(object? sender, EventArgs e)
        {
            string provider = string.IsNullOrWhiteSpace(_providerDropDown.SelectedKey)
                ? AppSettings.DefaultProvider
                : _providerDropDown.SelectedKey;

            string modelId = string.IsNullOrWhiteSpace(_modelIdBox.Text)
                ? AppSettings.DefaultModelId
                : _modelIdBox.Text.Trim();

            var settings = AppSettings.Load();
            bool folderChanged = !string.Equals(
                settings.SharedLibraryFolder?.Trim() ?? string.Empty,
                _sharedFolderBox.Text?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            settings.Provider = provider;
            settings.ModelId = modelId;
            settings.CustomEndpointUrl = _customEndpointBox.Text?.Trim() ?? string.Empty;
            settings.SharedLibraryFolder = _sharedFolderBox.Text?.Trim() ?? string.Empty;
            settings.SendCanvasData = _sendCanvasDataCheck.Checked == true;
            settings.AutoLayout = _autoLayoutCheck.Checked != false;
            settings.Save();

            string? newKey = string.IsNullOrWhiteSpace(_apiKeyBox.Text) ? null : _apiKeyBox.Text.Trim();
            if (newKey != null)
            {
                SecretStore.SaveApiKey(newKey);
                _apiKeyBox.Text = string.Empty;
            }

            RefreshKeyStatus();

            string companyReloadMessage = string.Empty;
            if (folderChanged && CopilotHostRegistry.GrasshopperBridge != null)
            {
                companyReloadMessage = " " + CopilotHostRegistry.GrasshopperBridge.ReloadCompanyLibrary();
            }

            _statusLabel.Text = $"Saved ({settings.Provider} / {settings.ModelId}, AutoLayout={settings.AutoLayout}){(newKey != null ? " + encrypted API key." : ".")}{companyReloadMessage}";
        }

        private async void OnVerifyModelClicked(object? sender, EventArgs e)
        {
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                _statusLabel.Text = "Open Grasshopper first to verify via the host bridge.";
                return;
            }

            _statusLabel.Text = "Checking model via provider API...";
            string message = await bridge.VerifyConfiguredModelAsync();
            Application.Instance.AsyncInvoke(() => _statusLabel.Text = message);
        }

        private void OnDumpIndexClicked(object? sender, EventArgs e)
        {
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                _statusLabel.Text = "Open Grasshopper first to initialize the component index.";
                return;
            }

            _statusLabel.Text = bridge.DumpIndexToJson();
        }
    }
}
