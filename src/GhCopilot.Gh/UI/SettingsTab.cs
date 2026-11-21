using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core.Host;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Settings;

namespace GhCopilot.UI
{
    /// <summary>
    /// Settings tab managing BYOK LLM provider (Gemini / Anthropic / OpenAI), DPAPI-encrypted API key,
    /// live key/model verification and model discovery, shared library folder, auto-layout, and privacy toggle.
    /// </summary>
    internal sealed class SettingsTab : Panel
    {
        private readonly DropDown _providerDropDown;
        private readonly Panel _apiKeyHost;
        private readonly PasswordBox _apiKeyMaskedBox;
        private readonly TextBox _apiKeyPlainBox;
        private readonly CheckBox _showKeyCheck;
        private readonly Label _keyStatusLabel;
        private readonly TextBox _modelIdBox;
        private readonly DropDown _fetchedModelsDropDown;
        private readonly Button _verifyModelButton;
        private readonly Button _fetchModelsButton;
        private readonly TextBox _customEndpointBox;
        private readonly TextBox _sharedFolderBox;
        private readonly CheckBox _sendCanvasDataCheck;
        private readonly CheckBox _autoLayoutCheck;
        private readonly TextArea _statusBox;
        private bool _updatingModelFromPicker;

        public SettingsTab()
        {
            AppSettings settings = AppSettings.Load();

            _providerDropDown = new DropDown();
            _providerDropDown.Items.Add(new ListItem { Key = "Gemini", Text = "Gemini (Google)" });
            _providerDropDown.Items.Add(new ListItem { Key = "Anthropic", Text = "Anthropic (Claude)" });
            _providerDropDown.Items.Add(new ListItem { Key = "OpenAI", Text = "OpenAI / Compatible" });
            _providerDropDown.SelectedKey = string.IsNullOrWhiteSpace(settings.Provider) ? "Gemini" : settings.Provider;
            _providerDropDown.SelectedKeyChanged += OnProviderChanged;

            _apiKeyMaskedBox = new PasswordBox();
            _apiKeyPlainBox = new TextBox { PlaceholderText = "Paste API key..." };
            _apiKeyHost = new Panel { Content = _apiKeyMaskedBox };

            _showKeyCheck = new CheckBox { Text = "Show / Edit Key" };
            _showKeyCheck.CheckedChanged += OnShowKeyToggled;

            var clearKeyButton = new Button { Text = "Clear Key" };
            clearKeyButton.Click += OnClearKeyClicked;

            _keyStatusLabel = new Label();

            _modelIdBox = new TextBox
            {
                Text = settings.ModelId,
                PlaceholderText = AppSettings.DefaultModelId
            };

            _verifyModelButton = new Button { Text = "Verify Key & Model" };
            _verifyModelButton.Click += OnVerifyModelClicked;

            _fetchModelsButton = new Button { Text = "Fetch Models" };
            _fetchModelsButton.Click += OnFetchModelsClicked;

            _fetchedModelsDropDown = new DropDown { Enabled = false };
            _fetchedModelsDropDown.Items.Add(new ListItem { Key = string.Empty, Text = "(Click Fetch Models to load available models)" });
            _fetchedModelsDropDown.SelectedIndex = 0;
            _fetchedModelsDropDown.SelectedKeyChanged += OnFetchedModelSelected;

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

            _statusBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 72,
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
            layout.Add(_apiKeyHost);
            layout.AddRow(_showKeyCheck, clearKeyButton);
            layout.Add(_keyStatusLabel);
            layout.Add(new Label { Text = "Model ID:" });
            layout.Add(_modelIdBox);
            layout.AddRow(_verifyModelButton, _fetchModelsButton);
            layout.Add(_fetchedModelsDropDown);
            layout.Add(new Label { Text = "Custom Endpoint URL (Optional):" });
            layout.Add(_customEndpointBox);
            layout.Add(new Label { Text = "Shared Company Library Folder:" });
            layout.AddRow(_sharedFolderBox, browseButton);
            layout.Add(_sendCanvasDataCheck);
            layout.Add(_autoLayoutCheck);
            layout.AddRow(saveButton, dumpIndexButton);
            layout.Add(new Label { Text = "Settings / API Verification Status:" });
            layout.Add(_statusBox);
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

        private string GetEnteredApiKey()
        {
            return (_showKeyCheck.Checked == true ? _apiKeyPlainBox.Text : _apiKeyMaskedBox.Text)?.Trim() ?? string.Empty;
        }

        private void SetEnteredApiKey(string value)
        {
            _apiKeyMaskedBox.Text = value;
            _apiKeyPlainBox.Text = value;
        }

        private bool TryResolveEffectiveApiKey(out string apiKey, out string sourceLabel)
        {
            string entered = GetEnteredApiKey();
            if (!string.IsNullOrWhiteSpace(entered))
            {
                apiKey = entered;
                sourceLabel = "entered box";
                return true;
            }

            if (SecretStore.TryLoadApiKey(out string? stored, allowEnvironmentFallback: false) && !string.IsNullOrWhiteSpace(stored))
            {
                apiKey = stored!;
                sourceLabel = "saved DPAPI key";
                return true;
            }

            if (SecretStore.TryLoadApiKey(out string? envKey, allowEnvironmentFallback: true) && !string.IsNullOrWhiteSpace(envKey))
            {
                apiKey = envKey!;
                sourceLabel = SecretStore.EnvVarName;
                return true;
            }

            apiKey = string.Empty;
            sourceLabel = string.Empty;
            return false;
        }

        private void OnShowKeyToggled(object? sender, EventArgs e)
        {
            bool show = _showKeyCheck.Checked == true;
            if (show)
            {
                string current = _apiKeyMaskedBox.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(current) && SecretStore.TryLoadApiKey(out string? stored))
                {
                    current = stored ?? string.Empty;
                }

                _apiKeyPlainBox.Text = current;
                _apiKeyHost.Content = _apiKeyPlainBox;
            }
            else
            {
                _apiKeyMaskedBox.Text = _apiKeyPlainBox.Text ?? string.Empty;
                _apiKeyHost.Content = _apiKeyMaskedBox;
            }
        }

        private void OnClearKeyClicked(object? sender, EventArgs e)
        {
            try
            {
                SecretStore.DeleteApiKey();
                SetEnteredApiKey(string.Empty);
                RefreshKeyStatus();
                _statusBox.Text = "Saved API key cleared from disk.";
            }
            catch (Exception ex)
            {
                _statusBox.Text = $"Could not clear saved API key: {ex.Message}";
            }
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

        private void OnFetchedModelSelected(object? sender, EventArgs e)
        {
            if (_updatingModelFromPicker)
            {
                return;
            }

            string selectedModel = _fetchedModelsDropDown.SelectedKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(selectedModel))
            {
                _modelIdBox.Text = selectedModel;
                _statusBox.Text = $"Selected model '{selectedModel}'. Click Save Settings to persist.";
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
            try
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

                string enteredKey = GetEnteredApiKey();
                bool savedNewKey = !string.IsNullOrWhiteSpace(enteredKey);
                if (savedNewKey)
                {
                    SecretStore.SaveApiKey(enteredKey);
                    if (_showKeyCheck.Checked != true)
                    {
                        SetEnteredApiKey(string.Empty);
                    }
                }

                RefreshKeyStatus();

                string companyReloadMessage = string.Empty;
                if (folderChanged && CopilotHostRegistry.GrasshopperBridge != null)
                {
                    companyReloadMessage = " " + CopilotHostRegistry.GrasshopperBridge.ReloadCompanyLibrary();
                }

                _statusBox.Text = $"Saved ({settings.Provider} / {settings.ModelId}, AutoLayout={settings.AutoLayout}){(savedNewKey ? " + encrypted API key." : ".")}{companyReloadMessage}";
            }
            catch (Exception ex)
            {
                _statusBox.Text = $"Failed to save settings: {ex.Message}";
            }
        }

        private async void OnVerifyModelClicked(object? sender, EventArgs e)
        {
            if (!TryResolveEffectiveApiKey(out string apiKey, out string sourceLabel))
            {
                _statusBox.Text = $"No API key entered or saved (and {SecretStore.EnvVarName} is not set). Paste an API key above first.";
                return;
            }

            string provider = string.IsNullOrWhiteSpace(_providerDropDown.SelectedKey)
                ? AppSettings.DefaultProvider
                : _providerDropDown.SelectedKey;
            string modelId = string.IsNullOrWhiteSpace(_modelIdBox.Text)
                ? AppSettings.DefaultModelId
                : _modelIdBox.Text.Trim();
            string? endpoint = string.IsNullOrWhiteSpace(_customEndpointBox.Text)
                ? null
                : _customEndpointBox.Text.Trim();

            SetApiButtonsEnabled(false);
            _statusBox.Text = $"Testing {provider} model '{modelId}' using {sourceLabel}...";
            try
            {
                using var client = new GeminiClient();
                (_, string message) = await client
                    .VerifyConfiguredModelAsync(apiKey, modelId, default, provider, endpoint)
                    .ConfigureAwait(false);
                Application.Instance?.AsyncInvoke(() =>
                {
                    _statusBox.Text = message;
                    SetApiButtonsEnabled(true);
                });
            }
            catch (Exception ex)
            {
                string safe = GeminiClient.ScrubSecret(ex.Message, apiKey);
                Application.Instance?.AsyncInvoke(() =>
                {
                    _statusBox.Text = $"Verification error: {safe}";
                    SetApiButtonsEnabled(true);
                });
            }
        }

        private async void OnFetchModelsClicked(object? sender, EventArgs e)
        {
            if (!TryResolveEffectiveApiKey(out string apiKey, out string sourceLabel))
            {
                _statusBox.Text = $"No API key entered or saved (and {SecretStore.EnvVarName} is not set). Paste an API key above first.";
                return;
            }

            string provider = string.IsNullOrWhiteSpace(_providerDropDown.SelectedKey)
                ? AppSettings.DefaultProvider
                : _providerDropDown.SelectedKey;
            string? endpoint = string.IsNullOrWhiteSpace(_customEndpointBox.Text)
                ? null
                : _customEndpointBox.Text.Trim();

            SetApiButtonsEnabled(false);
            _statusBox.Text = $"Fetching available {provider} models using {sourceLabel}...";
            try
            {
                using var client = new GeminiClient();
                IReadOnlyList<string> models = await client
                    .ListAvailableModelsAsync(apiKey, provider, endpoint)
                    .ConfigureAwait(false);

                List<string> filtered = models
                    .Where(m => !string.IsNullOrWhiteSpace(m) &&
                                m.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) < 0 &&
                                m.IndexOf("imagen", StringComparison.OrdinalIgnoreCase) < 0 &&
                                m.IndexOf("veo", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (filtered.Count == 0)
                {
                    filtered = models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
                }

                Application.Instance?.AsyncInvoke(() =>
                {
                    _updatingModelFromPicker = true;
                    try
                    {
                        _fetchedModelsDropDown.Items.Clear();
                        if (filtered.Count == 0)
                        {
                            _fetchedModelsDropDown.Items.Add(new ListItem { Key = string.Empty, Text = "(No models returned)" });
                            _fetchedModelsDropDown.SelectedIndex = 0;
                            _fetchedModelsDropDown.Enabled = false;
                            _statusBox.Text = $"API key accepted by {provider}, but zero models were returned.";
                        }
                        else
                        {
                            _fetchedModelsDropDown.Items.Add(new ListItem { Key = string.Empty, Text = $"Select from {filtered.Count} available model(s)..." });
                            foreach (string id in filtered)
                            {
                                _fetchedModelsDropDown.Items.Add(new ListItem { Key = id, Text = id });
                            }

                            _fetchedModelsDropDown.SelectedIndex = 0;
                            _fetchedModelsDropDown.Enabled = true;

                            string current = _modelIdBox.Text?.Trim() ?? string.Empty;
                            bool currentExists = filtered.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
                            _statusBox.Text = currentExists
                                ? $"API key valid! Loaded {filtered.Count} {provider} model(s). Current model '{current}' is available."
                                : $"API key valid! Loaded {filtered.Count} {provider} model(s). Current model '{current}' was not found — pick one from the dropdown above and click Save Settings.";
                        }
                    }
                    finally
                    {
                        _updatingModelFromPicker = false;
                        SetApiButtonsEnabled(true);
                    }
                });
            }
            catch (Exception ex)
            {
                string safe = GeminiClient.ScrubSecret(ex.Message, apiKey);
                Application.Instance?.AsyncInvoke(() =>
                {
                    _statusBox.Text = $"Fetch models failed: {safe}";
                    SetApiButtonsEnabled(true);
                });
            }
        }

        private void SetApiButtonsEnabled(bool enabled)
        {
            _verifyModelButton.Enabled = enabled;
            _fetchModelsButton.Enabled = enabled;
        }

        private void OnDumpIndexClicked(object? sender, EventArgs e)
        {
            try
            {
                IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    _statusBox.Text = "Open Grasshopper first to initialize the component index.";
                    return;
                }

                _statusBox.Text = bridge.DumpIndexToJson();
            }
            catch (Exception ex)
            {
                _statusBox.Text = $"Index dump failed: {ex.Message}";
            }
        }
    }
}
