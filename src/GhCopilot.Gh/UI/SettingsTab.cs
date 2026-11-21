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
    /// Modern dark-themed Settings tab organized into clean cards:
    /// Provider & API Key (BYOK + live verification + Fetch Models), Canvas & Library, and Status Console.
    /// </summary>
    internal sealed class SettingsTab : Panel
    {
        private readonly DropDown _providerDropDown;
        private readonly Panel _apiKeyHost;
        private readonly PasswordBox _apiKeyMaskedBox;
        private readonly TextBox _apiKeyPlainBox;
        private readonly CheckBox _showKeyCheck;
        private readonly Panel _keyBadge;
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

        public event EventHandler? SettingsSaved;

        public SettingsTab()
        {
            BackgroundColor = CopilotTheme.CanvasBg;
            AppSettings settings = AppSettings.Load();

            // ── Card 1: Provider, API Key & Model ──
            _providerDropDown = new DropDown();
            _providerDropDown.Items.Add(new ListItem { Key = "Gemini", Text = "Gemini (Google)" });
            _providerDropDown.Items.Add(new ListItem { Key = "Anthropic", Text = "Anthropic (Claude)" });
            _providerDropDown.Items.Add(new ListItem { Key = "OpenAI", Text = "OpenAI / Compatible" });
            _providerDropDown.SelectedKey = string.IsNullOrWhiteSpace(settings.Provider) ? "Gemini" : settings.Provider;
            _providerDropDown.SelectedKeyChanged += OnProviderChanged;

            _apiKeyMaskedBox = new PasswordBox();
            CopilotTheme.StylePasswordBox(_apiKeyMaskedBox);

            _apiKeyPlainBox = new TextBox { PlaceholderText = "Paste API key..." };
            CopilotTheme.StyleTextBox(_apiKeyPlainBox);

            _apiKeyHost = new Panel { Content = _apiKeyMaskedBox };

            _showKeyCheck = new CheckBox { Text = "Show / Edit Key" };
            CopilotTheme.StyleCheckBox(_showKeyCheck);
            _showKeyCheck.CheckedChanged += OnShowKeyToggled;

            Button clearKeyButton = CopilotTheme.CreateDangerButton("Clear Key", "Delete the saved encrypted API key from disk");
            clearKeyButton.MinimumSize = new Size(76, 24);
            clearKeyButton.Click += OnClearKeyClicked;

            _keyBadge = CopilotTheme.CreateBadge(
                out _keyStatusLabel,
                "Checking key...",
                CopilotTheme.SurfaceElevated,
                CopilotTheme.TextSecondary);

            _modelIdBox = new TextBox
            {
                Text = settings.ModelId,
                PlaceholderText = AppSettings.DefaultModelId
            };
            CopilotTheme.StyleTextBox(_modelIdBox);

            _verifyModelButton = CopilotTheme.CreateSecondaryButton("Verify Key & Model", "Test your API key and confirm the Model ID works (does not require Grasshopper)");
            _verifyModelButton.Click += OnVerifyModelClicked;

            _fetchModelsButton = CopilotTheme.CreateChipButton("Fetch Models", "Query available models from your API key and populate the picker");
            _fetchModelsButton.MinimumSize = new Size(92, 26);
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
            CopilotTheme.StyleTextBox(_customEndpointBox);

            var keyToolsRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            keyToolsRow.BeginHorizontal();
            keyToolsRow.Add(_showKeyCheck, xscale: true);
            keyToolsRow.Add(clearKeyButton, xscale: false);
            keyToolsRow.EndHorizontal();

            var apiCardLayout = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 6) };
            apiCardLayout.BeginVertical();
            apiCardLayout.Add(CopilotTheme.CreateSectionHeader("Provider & API Key (BYOK)"));
            apiCardLayout.Add(CopilotTheme.CreateLabel("LLM Provider", CopilotTheme.TextSecondary, CopilotTheme.SmallFont()));
            apiCardLayout.Add(_providerDropDown);
            apiCardLayout.Add(CopilotTheme.CreateLabel("API Key (encrypted locally via Windows DPAPI)", CopilotTheme.TextSecondary, CopilotTheme.SmallFont()));
            apiCardLayout.Add(_apiKeyHost);
            apiCardLayout.Add(keyToolsRow);
            apiCardLayout.Add(_keyBadge);
            apiCardLayout.Add(CopilotTheme.CreateHorizontalDivider());
            apiCardLayout.Add(CopilotTheme.CreateLabel("Model ID", CopilotTheme.TextSecondary, CopilotTheme.SmallFont()));
            apiCardLayout.Add(_modelIdBox);
            apiCardLayout.AddRow(_verifyModelButton, _fetchModelsButton);
            apiCardLayout.Add(_fetchedModelsDropDown);
            apiCardLayout.Add(CopilotTheme.CreateLabel("Custom Endpoint URL (optional)", CopilotTheme.TextSecondary, CopilotTheme.SmallFont()));
            apiCardLayout.Add(_customEndpointBox);
            apiCardLayout.EndVertical();
            Panel apiCard = CopilotTheme.CreateCard(apiCardLayout, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 10);

            // ── Card 2: Canvas & Company Library ──
            _sharedFolderBox = new TextBox
            {
                Text = settings.SharedLibraryFolder,
                PlaceholderText = @"\\server\shared\gh-library"
            };
            CopilotTheme.StyleTextBox(_sharedFolderBox);

            Button browseButton = CopilotTheme.CreateSecondaryButton("Browse...", "Choose a local or network folder with .ghuser / .gh clusters");
            browseButton.Click += OnBrowseFolderClicked;

            _autoLayoutCheck = new CheckBox
            {
                Text = "Topological layered auto-layout (prevents overlaps)",
                Checked = settings.AutoLayout
            };
            CopilotTheme.StyleCheckBox(_autoLayoutCheck);

            _sendCanvasDataCheck = new CheckBox
            {
                Text = "Send canvas metadata & outputs (never raw geometry)",
                Checked = settings.SendCanvasData
            };
            CopilotTheme.StyleCheckBox(_sendCanvasDataCheck);

            var folderRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            folderRow.BeginHorizontal();
            folderRow.Add(_sharedFolderBox, xscale: true);
            folderRow.Add(browseButton, xscale: false);
            folderRow.EndHorizontal();

            var canvasCardLayout = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 6) };
            canvasCardLayout.BeginVertical();
            canvasCardLayout.Add(CopilotTheme.CreateSectionHeader("Canvas & Library"));
            canvasCardLayout.Add(CopilotTheme.CreateLabel("Shared Company Library Folder", CopilotTheme.TextSecondary, CopilotTheme.SmallFont()));
            canvasCardLayout.Add(folderRow);
            canvasCardLayout.Add(_autoLayoutCheck);
            canvasCardLayout.Add(_sendCanvasDataCheck);
            canvasCardLayout.EndVertical();
            Panel canvasCard = CopilotTheme.CreateCard(canvasCardLayout, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 10);

            // ── Card 3: Save & Status Console ──
            Button saveButton = CopilotTheme.CreatePrimaryButton("✓ Save Settings", "Persist provider, model, encrypted key, and canvas preferences");
            saveButton.Click += OnSaveClicked;

            Button dumpIndexButton = CopilotTheme.CreateSecondaryButton("Dump Index JSON", "Export the loaded Grasshopper component index for debugging");
            dumpIndexButton.Click += OnDumpIndexClicked;

            _statusBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 76,
                Text = "Ready. Paste an API key and click Fetch Models or Verify Key & Model."
            };
            CopilotTheme.StyleTextArea(_statusBox, monospace: true);
            _statusBox.TextColor = CopilotTheme.TextSecondary;

            var consoleCardLayout = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 6) };
            consoleCardLayout.BeginVertical();
            consoleCardLayout.AddRow(saveButton, dumpIndexButton);
            consoleCardLayout.Add(CopilotTheme.CreateSectionHeader("Verification & Status"));
            consoleCardLayout.Add(_statusBox);
            consoleCardLayout.EndVertical();
            Panel consoleCard = CopilotTheme.CreateCard(consoleCardLayout, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 10);

            var layout = new DynamicLayout
            {
                Padding = new Padding(8, 6, 8, 8),
                Spacing = new Size(8, 8)
            };

            layout.BeginVertical();
            layout.Add(apiCard);
            layout.Add(canvasCard);
            layout.Add(consoleCard);
            layout.EndVertical();
            layout.Add(null);

            Content = new Scrollable
            {
                Border = BorderType.None,
                BackgroundColor = CopilotTheme.CanvasBg,
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

        private void SetConsoleStatus(string message, Color? color = null)
        {
            _statusBox.Text = message;
            _statusBox.TextColor = color ?? CopilotTheme.TextPrimary;
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
                SetConsoleStatus("Saved API key cleared from disk.", CopilotTheme.WarningAmber);
            }
            catch (Exception ex)
            {
                SetConsoleStatus($"Could not clear saved API key: {ex.Message}", CopilotTheme.DangerRose);
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
                SetConsoleStatus($"Selected model '{selectedModel}'. Click Save Settings to persist.", CopilotTheme.AccentCyan);
            }
        }

        private void RefreshKeyStatus()
        {
            bool hasStoredKey = SecretStore.TryLoadApiKey(out _, allowEnvironmentFallback: false);
            bool hasEnvKey = !hasStoredKey && SecretStore.TryLoadApiKey(out _, allowEnvironmentFallback: true);

            if (hasStoredKey)
            {
                _keyBadge.BackgroundColor = CopilotTheme.AccentEmeraldDark;
                _keyStatusLabel.TextColor = CopilotTheme.AccentEmerald;
                _keyStatusLabel.Text = "● Encrypted DPAPI key saved on disk";
            }
            else if (hasEnvKey)
            {
                _keyBadge.BackgroundColor = CopilotTheme.AccentCyanDark;
                _keyStatusLabel.TextColor = CopilotTheme.AccentCyan;
                _keyStatusLabel.Text = $"● Using {SecretStore.EnvVarName} environment variable";
            }
            else
            {
                _keyBadge.BackgroundColor = CopilotTheme.WarningAmberDark;
                _keyStatusLabel.TextColor = CopilotTheme.WarningAmber;
                _keyStatusLabel.Text = "○ No API key configured yet";
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
                SettingsSaved?.Invoke(this, EventArgs.Empty);

                string companyReloadMessage = string.Empty;
                if (folderChanged && CopilotHostRegistry.GrasshopperBridge != null)
                {
                    companyReloadMessage = " " + CopilotHostRegistry.GrasshopperBridge.ReloadCompanyLibrary();
                }

                SetConsoleStatus(
                    $"Saved ({settings.Provider} / {settings.ModelId}, AutoLayout={settings.AutoLayout}){(savedNewKey ? " + encrypted API key." : ".")}{companyReloadMessage}",
                    CopilotTheme.AccentEmerald);
            }
            catch (Exception ex)
            {
                SetConsoleStatus($"Failed to save settings: {ex.Message}", CopilotTheme.DangerRose);
            }
        }

        private async void OnVerifyModelClicked(object? sender, EventArgs e)
        {
            if (!TryResolveEffectiveApiKey(out string apiKey, out string sourceLabel))
            {
                SetConsoleStatus($"No API key entered or saved (and {SecretStore.EnvVarName} is not set). Paste an API key above first.", CopilotTheme.WarningAmber);
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
            SetConsoleStatus($"Testing {provider} model '{modelId}' using {sourceLabel}...", CopilotTheme.AccentCyan);
            try
            {
                using var client = new GeminiClient();
                (bool isAvailable, string message) = await client
                    .VerifyConfiguredModelAsync(apiKey, modelId, default, provider, endpoint)
                    .ConfigureAwait(false);
                Application.Instance?.AsyncInvoke(() =>
                {
                    SetConsoleStatus(message, isAvailable ? CopilotTheme.AccentEmerald : CopilotTheme.WarningAmber);
                    SetApiButtonsEnabled(true);
                });
            }
            catch (Exception ex)
            {
                string safe = GeminiClient.ScrubSecret(ex.Message, apiKey);
                Application.Instance?.AsyncInvoke(() =>
                {
                    SetConsoleStatus($"Verification error: {safe}", CopilotTheme.DangerRose);
                    SetApiButtonsEnabled(true);
                });
            }
        }

        private async void OnFetchModelsClicked(object? sender, EventArgs e)
        {
            if (!TryResolveEffectiveApiKey(out string apiKey, out string sourceLabel))
            {
                SetConsoleStatus($"No API key entered or saved (and {SecretStore.EnvVarName} is not set). Paste an API key above first.", CopilotTheme.WarningAmber);
                return;
            }

            string provider = string.IsNullOrWhiteSpace(_providerDropDown.SelectedKey)
                ? AppSettings.DefaultProvider
                : _providerDropDown.SelectedKey;
            string? endpoint = string.IsNullOrWhiteSpace(_customEndpointBox.Text)
                ? null
                : _customEndpointBox.Text.Trim();

            SetApiButtonsEnabled(false);
            SetConsoleStatus($"Fetching available {provider} models using {sourceLabel}...", CopilotTheme.AccentCyan);
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
                            SetConsoleStatus($"API key accepted by {provider}, but zero models were returned.", CopilotTheme.WarningAmber);
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
                            SetConsoleStatus(
                                currentExists
                                    ? $"API key valid! Loaded {filtered.Count} {provider} model(s). Current model '{current}' is available."
                                    : $"API key valid! Loaded {filtered.Count} {provider} model(s). Current model '{current}' was not found — pick one above and click Save Settings.",
                                currentExists ? CopilotTheme.AccentEmerald : CopilotTheme.WarningAmber);
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
                    SetConsoleStatus($"Fetch models failed: {safe}", CopilotTheme.DangerRose);
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
                    SetConsoleStatus("Open Grasshopper first to initialize the component index.", CopilotTheme.WarningAmber);
                    return;
                }

                SetConsoleStatus(bridge.DumpIndexToJson(), CopilotTheme.AccentEmerald);
            }
            catch (Exception ex)
            {
                SetConsoleStatus($"Index dump failed: {ex.Message}", CopilotTheme.DangerRose);
            }
        }
    }
}
