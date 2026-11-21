using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;

namespace GhCopilot.UI
{
    /// <summary>
    /// Modern dark-themed conversational chat tab with starter chips, quick canvas tools,
    /// studio composer card, explicit Rhino selection consent, and collapsible diagnostics drawer.
    /// </summary>
    internal sealed class PromptTab : Panel
    {
        private const string WelcomePlaceholder =
            "Welcome to GH Copilot.\n" +
            "Describe a Grasshopper definition to create, select nodes to explain or fix, " +
            "or ask follow-up edits in plain English.\n\n" +
            "Tip: Press Ctrl+Enter to send. Click a starter chip below to try an example.";

        private readonly PreviewBar _previewBar;
        private readonly TextArea _promptBox;
        private readonly Button _sendButton;
        private readonly Button _explainButton;
        private readonly Button _fixButton;
        private readonly Button _saveRecipeButton;
        private readonly Button _cancelButton;
        private readonly Button _demoButton;
        private readonly Button _newChatButton;
        private readonly Button _chipCircle;
        private readonly Button _chipGrid;
        private readonly Button _chipTower;
        private readonly CheckBox _useRhinoSelection;
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly Label _tokenEstimateLabel;
        private readonly TextArea _transcriptBox;
        private readonly Button _diagnosticsToggle;
        private readonly Panel _diagnosticsBody;
        private readonly TextArea _warningsBox;
        private CancellationTokenSource? _activeCts;
        private bool _hasTranscriptEntries;
        private bool _diagnosticsExpanded;

        public PromptTab(PreviewBar previewBar)
        {
            _previewBar = previewBar ?? throw new ArgumentNullException(nameof(previewBar));
            BackgroundColor = CopilotTheme.CanvasBg;

            _transcriptBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 160,
                Text = WelcomePlaceholder
            };
            CopilotTheme.StyleTextArea(_transcriptBox);
            _transcriptBox.TextColor = CopilotTheme.TextSecondary;

            _newChatButton = CopilotTheme.CreateSecondaryButton("+ New Chat", "Clear chat history and start fresh (keeps accepted canvas objects)");
            _newChatButton.Click += OnNewChatClicked;

            Panel tokenPill = CopilotTheme.CreateBadge(
                out _tokenEstimateLabel,
                "~0 tok",
                CopilotTheme.SurfaceElevated,
                CopilotTheme.TextSecondary);
            tokenPill.ToolTip = "Approximate input token estimate (not provider billing usage)";

            var transcriptHeader = new DynamicLayout
            {
                Padding = Padding.Empty,
                Spacing = new Size(6, 4)
            };
            transcriptHeader.BeginHorizontal();
            transcriptHeader.Add(CopilotTheme.CreateSectionHeader("Conversation"), xscale: true);
            transcriptHeader.Add(tokenPill, xscale: false);
            transcriptHeader.Add(_newChatButton, xscale: false);
            transcriptHeader.EndHorizontal();

            var transcriptCardContent = new DynamicLayout
            {
                Padding = Padding.Empty,
                Spacing = new Size(6, 6)
            };
            transcriptCardContent.BeginVertical();
            transcriptCardContent.Add(transcriptHeader);
            transcriptCardContent.Add(_transcriptBox, yscale: true);
            transcriptCardContent.EndVertical();
            Panel transcriptCard = CopilotTheme.CreateCard(transcriptCardContent, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 8);

            // One-click starter prompt chips
            _chipCircle = CopilotTheme.CreateChipButton("Circle + Slider", "Insert sample prompt: parametric circle with radius slider");
            _chipCircle.Click += (_, __) => PopulatePrompt("Create a circle with an editable radius slider from 1 to 50");

            _chipGrid = CopilotTheme.CreateChipButton("Attractor Grid", "Insert sample prompt: rectangular grid scaled by a point attractor");
            _chipGrid.Click += (_, __) => PopulatePrompt("Create a 10x10 point grid with circles whose radii vary by distance to an attractor point");

            _chipTower = CopilotTheme.CreateChipButton("Spiral Tower", "Insert sample prompt: procedural twisted tower");
            _chipTower.Click += (_, __) => PopulatePrompt("Create a parametric twisted tower with floor count, height, and rotation sliders");

            var chipsRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            chipsRow.BeginHorizontal();
            chipsRow.Add(CopilotTheme.CreateLabel("Try:", CopilotTheme.TextMuted, CopilotTheme.SmallBoldFont()), xscale: false);
            chipsRow.Add(_chipCircle, xscale: true);
            chipsRow.Add(_chipGrid, xscale: true);
            chipsRow.Add(_chipTower, xscale: true);
            chipsRow.EndHorizontal();

            // Quick canvas action buttons
            _explainButton = CopilotTheme.CreateSecondaryButton("Explain", "Explain the currently selected Grasshopper components");
            _explainButton.Click += OnExplainClicked;

            _fixButton = CopilotTheme.CreateSecondaryButton("Fix Errors", "Inspect and repair runtime errors on selected components");
            _fixButton.Click += OnFixClicked;

            _saveRecipeButton = CopilotTheme.CreateSecondaryButton("Save Recipe", "Save selected components locally as a reusable recipe");
            _saveRecipeButton.Click += OnSaveRecipeClicked;

            _demoButton = CopilotTheme.CreateSecondaryButton("Demo", "Spawn an offline demo graph (no API key required)");
            _demoButton.Click += OnDemoClicked;

            var toolsRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            toolsRow.AddRow(_explainButton, _fixButton, _saveRecipeButton, _demoButton);

            // Studio composer card
            _promptBox = new TextArea
            {
                Text = string.Empty,
                Height = 62,
                Wrap = true
            };
            CopilotTheme.StyleTextArea(_promptBox);
            _promptBox.KeyDown += OnPromptKeyDown;

            _useRhinoSelection = new CheckBox
            {
                Text = "Use Rhino Selection",
                Checked = false,
                ToolTip = "Explicitly bind currently selected Rhino geometry as inputs for this turn only (auto-resets)"
            };
            CopilotTheme.StyleCheckBox(_useRhinoSelection);

            _cancelButton = CopilotTheme.CreateDangerButton("Cancel", "Abort the active request and roll back any pending edits");
            _cancelButton.Enabled = false;
            _cancelButton.Click += OnCancelClicked;

            _sendButton = CopilotTheme.CreatePrimaryButton("Send ↵", "Send message to GH Copilot (Ctrl+Enter)");
            _sendButton.Click += OnSendClicked;

            var composerFooter = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            composerFooter.BeginHorizontal();
            composerFooter.Add(_useRhinoSelection, xscale: true);
            composerFooter.Add(_cancelButton, xscale: false);
            composerFooter.Add(_sendButton, xscale: false);
            composerFooter.EndHorizontal();

            var composerContent = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 6) };
            composerContent.BeginVertical();
            composerContent.Add(CopilotTheme.CreateSectionHeader("Message  •  Ctrl+Enter to send"));
            composerContent.Add(_promptBox);
            composerContent.Add(composerFooter);
            composerContent.EndVertical();
            Panel composerCard = CopilotTheme.CreateCard(composerContent, CopilotTheme.BorderStrong, CopilotTheme.Surface, padding: 8);

            // Progress, status & collapsible diagnostics drawer
            _progressBar = new ProgressBar
            {
                Indeterminate = true,
                Visible = false,
                Height = 6
            };

            _statusLabel = CopilotTheme.CreateLabel(
                CopilotHostRegistry.GrasshopperBridge != null
                    ? "Ready — Grasshopper connected."
                    : "Waiting for Grasshopper — open Grasshopper to enable canvas tools.",
                CopilotTheme.TextSecondary,
                CopilotTheme.SmallFont());

            _diagnosticsToggle = CopilotTheme.CreateSecondaryButton("▸ Diagnostics: Clean", "Toggle validator warnings and Grasshopper runtime messages");
            _diagnosticsToggle.MinimumSize = new Size(120, 22);
            _diagnosticsToggle.Click += OnToggleDiagnostics;

            _warningsBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 68,
                Text = "None."
            };
            CopilotTheme.StyleTextArea(_warningsBox, monospace: true);
            _warningsBox.TextColor = CopilotTheme.TextSecondary;

            _diagnosticsBody = new Panel
            {
                Visible = false,
                Padding = new Padding(0, 4, 0, 0),
                Content = _warningsBox
            };

            var statusBar = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            statusBar.BeginHorizontal();
            statusBar.Add(_statusLabel, xscale: true);
            statusBar.Add(_diagnosticsToggle, xscale: false);
            statusBar.EndHorizontal();

            var layout = new DynamicLayout
            {
                Padding = new Padding(8, 6, 8, 4),
                Spacing = new Size(6, 6)
            };

            layout.BeginVertical();
            layout.Add(transcriptCard, yscale: true);
            layout.Add(chipsRow);
            layout.Add(toolsRow);
            layout.Add(composerCard);
            layout.Add(_progressBar);
            layout.Add(statusBar);
            layout.Add(_diagnosticsBody);
            layout.EndVertical();
            Content = layout;

            SyncTranscriptFromSession();
            CopilotHostRegistry.GrasshopperBridgeRegistered += OnBridgeRegistered;
            _previewBar.PreviewActionCompleted += (_, status) =>
            {
                SetStatus(status, CopilotTheme.AccentEmerald);
                AppendTranscript("GH Copilot", status);
                UpdateActionState();
            };
            _previewBar.PreviewStateChanged += (_, __) => UpdateActionState();
            UpdateActionState();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CopilotHostRegistry.GrasshopperBridgeRegistered -= OnBridgeRegistered;
            }

            base.Dispose(disposing);
        }

        private void PopulatePrompt(string sample)
        {
            if (!_promptBox.Enabled) return;
            _promptBox.Text = sample;
            _promptBox.Focus();
            _promptBox.CaretIndex = _promptBox.Text.Length;
        }

        private void OnToggleDiagnostics(object? sender, EventArgs e)
        {
            SetDiagnosticsExpanded(!_diagnosticsExpanded);
        }

        private void SetDiagnosticsExpanded(bool expanded)
        {
            _diagnosticsExpanded = expanded;
            _diagnosticsBody.Visible = expanded;
            string current = _diagnosticsToggle.Text ?? string.Empty;
            if (current.StartsWith("▸ ", StringComparison.Ordinal) || current.StartsWith("▾ ", StringComparison.Ordinal))
            {
                _diagnosticsToggle.Text = (expanded ? "▾ " : "▸ ") + current.Substring(2);
            }
        }

        private void UpdateDiagnosticsDisplay(List<string> messages)
        {
            if (messages.Count == 0)
            {
                _warningsBox.Text = "None.";
                _warningsBox.TextColor = CopilotTheme.TextSecondary;
                _diagnosticsToggle.BackgroundColor = CopilotTheme.SurfaceElevated;
                _diagnosticsToggle.TextColor = CopilotTheme.TextSecondary;
                _diagnosticsToggle.Text = (_diagnosticsExpanded ? "▾ " : "▸ ") + "Diagnostics: Clean";
            }
            else
            {
                _warningsBox.Text = string.Join(Environment.NewLine, messages);
                bool hasError = messages.Exists(m => m.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || m.StartsWith("[Validator]", StringComparison.Ordinal));
                _warningsBox.TextColor = hasError ? Color.FromArgb(0xFC, 0xA5, 0xA5) : CopilotTheme.WarningAmber;
                _diagnosticsToggle.BackgroundColor = hasError ? CopilotTheme.DangerRoseDark : CopilotTheme.WarningAmberDark;
                _diagnosticsToggle.TextColor = hasError ? Color.FromArgb(0xFC, 0xA5, 0xA5) : CopilotTheme.WarningAmber;
                _diagnosticsToggle.Text = $"▾ Diagnostics ({messages.Count})";
                SetDiagnosticsExpanded(true);
            }
        }

        private void SetStatus(string text, Color? color = null)
        {
            _statusLabel.Text = text;
            _statusLabel.ToolTip = text;
            _statusLabel.TextColor = color ?? CopilotTheme.TextSecondary;
        }

        private void OnBridgeRegistered(object? sender, EventArgs e)
        {
            Application.Instance?.AsyncInvoke(() =>
            {
                SyncTranscriptFromSession();
                UpdateActionState();
                if (_statusLabel.Text.StartsWith("Waiting for Grasshopper", StringComparison.Ordinal) ||
                    _statusLabel.Text == "Open Grasshopper first to initialize the .gha bridge.")
                {
                    SetStatus("Ready — Grasshopper connected.", CopilotTheme.AccentEmerald);
                }
            });
        }

        private void SyncTranscriptFromSession()
        {
            if (_hasTranscriptEntries)
            {
                return;
            }

            if (CopilotHostRegistry.GrasshopperBridge is IChatHostBridge chat)
            {
                if (!string.IsNullOrEmpty(chat.Session.Summary)) AppendTranscript("Context", chat.Session.Summary);
                foreach (var message in chat.Session.Messages)
                {
                    string displayRole = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "You" : "GH Copilot";
                    AppendTranscript(displayRole, message.Content);
                }
            }
        }

        private bool CanStartAction()
        {
            if (_activeCts != null) return false;
            if (CopilotHostRegistry.GrasshopperBridge?.HasActivePreview == true)
            {
                SetStatus("Accept or reject the pending preview before continuing.", CopilotTheme.WarningAmber);
                UpdateActionState();
                return false;
            }
            return true;
        }

        private Task RunBridgeActionAsync(string prompt, ChatAction action, string busyMessage)
        {
            if (!CanStartAction()) return Task.CompletedTask;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                SetStatus(missing, CopilotTheme.WarningAmber);
                AppendTranscript("GH Copilot", missing);
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return Task.CompletedTask;
            }

            bool useRhinoSelection = _useRhinoSelection.Checked == true;
            if (useRhinoSelection && !(bridge is IChatHostBridge))
            {
                SetStatus("This host does not support explicit Rhino selection. Uncheck Use Rhino Selection to continue.", CopilotTheme.WarningAmber);
                return Task.CompletedTask;
            }
            return ExecuteAsync(bridge, prompt, action, useRhinoSelection, busyMessage);
        }

        private async Task ExecuteAsync(
            IGrasshopperHostBridge bridge,
            string prompt,
            ChatAction action,
            bool useRhinoSelection,
            string busyMessage)
        {
            var cts = new CancellationTokenSource();
            _activeCts = cts;
            IChatHostBridge? chat = bridge as IChatHostBridge;
            Action<int> onEstimate = estimate => Application.Instance?.AsyncInvoke(() =>
            {
                if (ReferenceEquals(_activeCts, cts)) ShowTokenEstimate(estimate);
            });
            if (chat != null) chat.TokenEstimateChanged += onEstimate;
            SetRunningState(isRunning: true, statusText: busyMessage);
            _tokenEstimateLabel.Text = "~… tok";
            AppendTranscript("You", (action == ChatAction.Message ? "" : "[" + action + "] ")
                + prompt + (useRhinoSelection ? " [Use Rhino Selection]" : ""));
            UpdateDiagnosticsDisplay(new List<string>());

            CopilotGenerationResponse? response = null;
            string? failure = null;
            try
            {
                if (chat != null)
                {
                    response = await chat.SendChatAsync(prompt, action, useRhinoSelection, cts.Token);
                }
                else
                {
                    switch (action)
                    {
                        case ChatAction.ExplainSelection:
                            response = await bridge.ExplainSelectionAsync(prompt, cts.Token);
                            break;
                        case ChatAction.FixSelection:
                            response = await bridge.FixSelectionAsync(prompt, cts.Token);
                            break;
                        case ChatAction.SaveRecipe:
                            string name = prompt.Length > 48 ? prompt.Substring(0, 48).Trim() : prompt;
                            string result = bridge.SaveSelectionAsRecipe(name, prompt);
                            response = new CopilotGenerationResponse { IsSuccess = true, StatusLine = result, Explanation = result };
                            break;
                        default:
                            response = await bridge.RunPromptAsync(prompt, cts.Token);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                failure = "Request cancelled.";
            }
            catch (Exception ex)
            {
                failure = $"Error: {ex.Message}";
            }
            finally
            {
                if (chat != null) chat.TokenEstimateChanged -= onEstimate;
                Application.Instance?.AsyncInvoke(() =>
                {
                    try
                    {
                        if (response != null)
                        {
                            SetStatus(response.StatusLine, response.IsSuccess ? CopilotTheme.AccentEmerald : CopilotTheme.DangerRose);
                            string answer = string.IsNullOrWhiteSpace(response.Explanation) ? response.StatusLine : response.Explanation;
                            AppendTranscript("GH Copilot", (response.IsSuccess ? "" : "[Error] ") + answer);
                            var combinedMessages = new List<string>();
                            foreach (string warning in response.ValidatorWarnings) combinedMessages.Add("[Validator] " + warning);
                            combinedMessages.AddRange(response.RuntimeMessages);
                            UpdateDiagnosticsDisplay(combinedMessages);
                            if (chat != null) ShowTokenEstimate(response.EstimatedInputTokens);
                            else _tokenEstimateLabel.Text = "n/a";
                            if (response.IsSuccess && action == ChatAction.Message)
                            {
                                _promptBox.Text = string.Empty;
                            }
                        }
                        else
                        {
                            string msg = failure ?? "No response returned.";
                            SetStatus(msg, CopilotTheme.DangerRose);
                            AppendTranscript("GH Copilot", msg);
                            _tokenEstimateLabel.Text = "~— tok";
                        }
                    }
                    finally
                    {
                        _useRhinoSelection.Checked = false;
                        _activeCts = null;
                        cts.Dispose();
                        SetRunningState(isRunning: false);
                    }
                });
            }
        }

        private void OnPromptKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Keys.Enter && e.Modifiers == Keys.Control)
            {
                e.Handled = true;
                if (_sendButton.Enabled)
                {
                    OnSendClicked(this, EventArgs.Empty);
                }
            }
        }

        private async void OnSendClicked(object? sender, EventArgs e)
        {
            string prompt = _promptBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                SetStatus("Enter a message or click a starter chip first.", CopilotTheme.WarningAmber);
                return;
            }

            await RunBridgeActionAsync(
                prompt, ChatAction.Message, "Retrieving components and calling LLM...");
        }

        private async void OnExplainClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(
                hint, ChatAction.ExplainSelection, "Serializing selection and asking LLM to explain...");
        }

        private async void OnFixClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(
                hint, ChatAction.FixSelection, "Serializing selection and generating fix preview...");
        }

        private void OnCancelClicked(object? sender, EventArgs e)
        {
            _activeCts?.Cancel();
        }

        private void OnDemoClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                SetStatus(missing, CopilotTheme.WarningAmber);
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            AppendTranscript("You", "Spawn Demo (No LLM)");
            try
            {
                string status = bridge.SpawnDemoGraph();
                SetStatus(status, CopilotTheme.AccentEmerald);
                AppendTranscript("GH Copilot", status + Environment.NewLine + "Slider(12.5) + Slider(7.5) -> Addition -> Panel.");
                UpdateDiagnosticsDisplay(new List<string>());
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message, CopilotTheme.DangerRose);
                AppendTranscript("GH Copilot", "Error: " + ex.Message);
            }
            finally { _previewBar.RefreshState(); }
        }

        private async void OnSaveRecipeClicked(object? sender, EventArgs e)
        {
            string text = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(text, ChatAction.SaveRecipe, "Saving selected graph as a recipe...");
        }

        private void OnNewChatClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;
            try
            {
                (CopilotHostRegistry.GrasshopperBridge as IChatHostBridge)?.NewChat();
                _hasTranscriptEntries = false;
                _transcriptBox.TextColor = CopilotTheme.TextSecondary;
                _transcriptBox.Text = WelcomePlaceholder;
                _promptBox.Text = string.Empty;
                _useRhinoSelection.Checked = false;
                _tokenEstimateLabel.Text = "~0 tok";
                UpdateDiagnosticsDisplay(new List<string>());
                SetDiagnosticsExpanded(false);
                SetStatus("New chat started. Accepted canvas objects are unchanged.", CopilotTheme.AccentCyan);
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message, CopilotTheme.DangerRose);
                AppendTranscript("GH Copilot", "Error: " + ex.Message);
            }
        }

        private void AppendTranscript(string role, string content)
        {
            string header = $"── {role} ──────────────────────────";
            string entry = header + Environment.NewLine + content;
            if (!_hasTranscriptEntries)
            {
                _hasTranscriptEntries = true;
                _transcriptBox.TextColor = CopilotTheme.TextPrimary;
                _transcriptBox.Text = entry;
            }
            else
            {
                _transcriptBox.Text = (_transcriptBox.Text ?? string.Empty) + Environment.NewLine + Environment.NewLine + entry;
            }

            _transcriptBox.CaretIndex = _transcriptBox.Text.Length;
        }

        private void ShowTokenEstimate(int estimate)
        {
            _tokenEstimateLabel.Text = $"~{Math.Max(0, estimate):N0} tok";
        }

        private void UpdateActionState()
        {
            bool running = _activeCts != null;
            bool canAct = !running && CopilotHostRegistry.GrasshopperBridge?.HasActivePreview != true;
            _sendButton.Enabled = canAct;
            _explainButton.Enabled = canAct;
            _fixButton.Enabled = canAct;
            _saveRecipeButton.Enabled = canAct;
            _demoButton.Enabled = canAct;
            _newChatButton.Enabled = canAct;
            _useRhinoSelection.Enabled = canAct;
            _chipCircle.Enabled = canAct;
            _chipGrid.Enabled = canAct;
            _chipTower.Enabled = canAct;
            _promptBox.Enabled = !running;
        }

        private void SetRunningState(bool isRunning, string? statusText = null)
        {
            UpdateActionState();
            _previewBar.SetBusy(isRunning);
            _cancelButton.Enabled = isRunning;
            _progressBar.Visible = isRunning;
            if (statusText != null)
            {
                SetStatus(statusText, CopilotTheme.AccentCyan);
            }
        }
    }
}
