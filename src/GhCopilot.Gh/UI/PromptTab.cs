using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using GhCopilot.Core.Llm;
using Rhino;

namespace GhCopilot.UI
{
    /// <summary>
    /// Messaging-style chat workspace with live AI thoughts/ETA indicator, auto-launched Grasshopper document,
    /// context-aware starter chips (auto-hidden once conversation begins), and collapsible diagnostics drawer.
    /// </summary>
    internal sealed class PromptTab : Panel
    {
        private const string WelcomePlaceholder =
            "Welcome to GH Copilot!\n\n" +
            "• Chat naturally — ask questions or say hi without altering your canvas.\n" +
            "• Open any existing .gh file or Rhino 8 scene — Copilot inspects open canvas nodes & the active Rhino viewport automatically.\n" +
            "• Describe parametric geometry in plain English (e.g. a tower, grid, or circle + slider).\n\n" +
            "Tip: Press Ctrl+Enter to send.";

        private readonly PreviewBar _previewBar;
        private readonly TextArea _promptBox;
        private readonly Button _sendButton;
        private readonly Button _explainButton;
        private readonly Button _fixButton;
        private readonly Button _saveRecipeButton;
        private readonly Button _cancelButton;
        private readonly Button _demoButton;
        private readonly Button _newChatButton;
        private readonly Panel _starterChipsPanel;
        private readonly Button _chipCircle;
        private readonly Button _chipGrid;
        private readonly Button _chipTower;
        private readonly CheckBox _useRhinoSelection;
        private readonly Panel _thinkingCard;
        private readonly Label _thinkingStageLabel;
        private readonly Label _thinkingTrailLabel;
        private readonly Label _etaLabel;
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly Label _tokenEstimateLabel;
        private readonly TextArea _transcriptBox;
        private readonly Button _diagnosticsToggle;
        private readonly Panel _diagnosticsBody;
        private readonly TextArea _warningsBox;

        private CancellationTokenSource? _activeCts;
        private UITimer? _etaTimer;
        private readonly Stopwatch _turnStopwatch = new Stopwatch();
        private double _expectedDurationSeconds = 9.0;
        private double _emaQuickTurnSeconds = 3.5;
        private double _emaGraphTurnSeconds = 11.0;
        private readonly List<string> _thoughtSteps = new List<string>();
        private string _latestThought = string.Empty;
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
                Height = 165,
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
            transcriptHeader.Add(CopilotTheme.CreateSectionHeader("Chat"), xscale: true);
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

            // Live AI Processing / Thoughts / ETA card (shown only while a turn is running)
            Panel aiWorkingBadge = CopilotTheme.CreateBadge(
                out _,
                "✦ AI THINKING",
                CopilotTheme.AccentCyanDark,
                CopilotTheme.AccentCyan);

            _etaLabel = CopilotTheme.CreateLabel(
                "0.0s • ETA ~9s",
                CopilotTheme.AccentCyan,
                CopilotTheme.MonoFont());

            _thinkingStageLabel = CopilotTheme.CreateLabel(
                "Preparing request...",
                CopilotTheme.TextPrimary,
                CopilotTheme.SmallBoldFont());

            _thinkingTrailLabel = CopilotTheme.CreateLabel(
                "Waiting for model stream...",
                CopilotTheme.TextSecondary,
                CopilotTheme.SmallFont());

            _progressBar = new ProgressBar
            {
                Indeterminate = true,
                Visible = true,
                Height = 5
            };

            var thinkingHeader = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 2) };
            thinkingHeader.BeginHorizontal();
            thinkingHeader.Add(aiWorkingBadge, xscale: false);
            thinkingHeader.Add(_thinkingStageLabel, xscale: true);
            thinkingHeader.Add(_etaLabel, xscale: false);
            thinkingHeader.EndHorizontal();

            var thinkingContent = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(4, 4) };
            thinkingContent.BeginVertical();
            thinkingContent.Add(thinkingHeader);
            thinkingContent.Add(_thinkingTrailLabel);
            thinkingContent.Add(_progressBar);
            thinkingContent.EndVertical();

            _thinkingCard = CopilotTheme.CreateCard(thinkingContent, CopilotTheme.AccentCyan, CopilotTheme.SurfaceElevated, padding: 8);
            _thinkingCard.Visible = false;

            // One-click starter prompt chips (shown only at the very start before user types or sends)
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
            _starterChipsPanel = new Panel { Content = chipsRow };

            // Quick canvas action buttons
            _explainButton = CopilotTheme.CreateSecondaryButton("Explain", "Explain selected Grasshopper nodes (or the whole open file & Rhino viewport when nothing is selected)");
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
                Height = 58,
                Wrap = true
            };
            CopilotTheme.StyleTextArea(_promptBox);
            _promptBox.KeyDown += OnPromptKeyDown;
            _promptBox.TextChanged += (_, __) => UpdateStarterChipsVisibility();

            _useRhinoSelection = new CheckBox
            {
                Text = "Use Rhino Selection",
                Checked = false,
                ToolTip = "Explicitly bind currently selected Rhino geometry as inputs for this turn only (auto-resets)"
            };
            CopilotTheme.StyleCheckBox(_useRhinoSelection);

            _cancelButton = CopilotTheme.CreateDangerButton("Stop", "Abort the active request and roll back any pending edits");
            _cancelButton.Enabled = false;
            _cancelButton.Click += OnCancelClicked;

            _sendButton = CopilotTheme.CreatePrimaryButton("Send ➤", "Send message to GH Copilot (Ctrl+Enter)");
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

            // Status & collapsible diagnostics drawer
            _statusLabel = CopilotTheme.CreateLabel(
                CopilotHostRegistry.GrasshopperBridge != null
                    ? "Ready — Grasshopper connected."
                    : "Ready — Grasshopper opens automatically on Send.",
                CopilotTheme.TextSecondary,
                CopilotTheme.SmallFont());

            _diagnosticsToggle = CopilotTheme.CreateSecondaryButton("▸ Diagnostics: Clean", "Toggle validator warnings and Grasshopper runtime messages");
            _diagnosticsToggle.MinimumSize = new Size(124, 22);
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
            layout.Add(_thinkingCard);
            layout.Add(_starterChipsPanel);
            layout.Add(toolsRow);
            layout.Add(composerCard);
            layout.Add(statusBar);
            layout.Add(_diagnosticsBody);
            layout.EndVertical();
            Content = layout;

            SyncTranscriptFromSession();
            UpdateStarterChipsVisibility();
            CopilotHostRegistry.GrasshopperBridgeRegistered += OnBridgeRegistered;
            _previewBar.PreviewActionCompleted += (_, status) =>
            {
                SetStatus(status, CopilotTheme.AccentEmerald);
                AppendBubble("GH Copilot", status);
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
                StopEtaTimer();
            }

            base.Dispose(disposing);
        }

        private void UpdateStarterChipsVisibility()
        {
            bool show = !_hasTranscriptEntries && string.IsNullOrWhiteSpace(_promptBox.Text);
            _starterChipsPanel.Visible = show;
        }

        private void PopulatePrompt(string sample)
        {
            if (!_promptBox.Enabled) return;
            _promptBox.Text = sample;
            _starterChipsPanel.Visible = false;
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

        private void StartEtaTimer(bool isQuickTurn)
        {
            StopEtaTimer();
            _thoughtSteps.Clear();
            _latestThought = string.Empty;
            _expectedDurationSeconds = isQuickTurn ? _emaQuickTurnSeconds : _emaGraphTurnSeconds;
            _turnStopwatch.Restart();
            UpdateThinkingDisplay(isQuickTurn
                ? "💭 Reading message context..."
                : "🧠 Analyzing prompt & preparing Grasshopper workspace...");

            _etaTimer = new UITimer { Interval = 0.25 };
            _etaTimer.Elapsed += (_, __) => UpdateEtaTick();
            _etaTimer.Start();
            _thinkingCard.Visible = true;
        }

        private void StopEtaTimer()
        {
            _turnStopwatch.Stop();
            if (_etaTimer != null)
            {
                _etaTimer.Stop();
                _etaTimer.Dispose();
                _etaTimer = null;
            }
            _thinkingCard.Visible = false;
        }

        private void UpdateEtaTick()
        {
            double elapsed = _turnStopwatch.Elapsed.TotalSeconds;
            double remaining = Math.Max(0.5, _expectedDurationSeconds - elapsed);
            _etaLabel.Text = elapsed < _expectedDurationSeconds
                ? $"{elapsed:F1}s • ETA ~{remaining:F0}s"
                : $"{elapsed:F1}s • Finalizing...";
        }

        private void UpdateThinkingDisplay(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return;
            _latestThought = stage;
            if (_thoughtSteps.Count == 0 || !string.Equals(_thoughtSteps[_thoughtSteps.Count - 1], stage, StringComparison.Ordinal))
            {
                if (_thoughtSteps.Count >= 4) _thoughtSteps.RemoveAt(0);
                _thoughtSteps.Add(stage);
            }

            _thinkingStageLabel.Text = stage;
            _thinkingStageLabel.ToolTip = stage;
            _thinkingTrailLabel.Text = _thoughtSteps.Count > 1
                ? "Steps: " + string.Join("  →  ", _thoughtSteps)
                : "Live reasoning pipeline active...";
            _thinkingTrailLabel.ToolTip = _thinkingTrailLabel.Text;
            UpdateEtaTick();
        }

        private void OnBridgeRegistered(object? sender, EventArgs e)
        {
            Application.Instance?.AsyncInvoke(() =>
            {
                SyncTranscriptFromSession();
                UpdateStarterChipsVisibility();
                UpdateActionState();
                if (_statusLabel.Text.StartsWith("Ready", StringComparison.Ordinal) ||
                    _statusLabel.Text.StartsWith("Waiting for Grasshopper", StringComparison.Ordinal))
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
                if (!string.IsNullOrEmpty(chat.Session.Summary))
                {
                    AppendBubble("Context", chat.Session.Summary);
                }

                foreach (var message in chat.Session.Messages)
                {
                    bool isUser = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
                    AppendBubble(isUser ? "You" : "GH Copilot", message.Content);
                }
            }
        }

        private async Task<IGrasshopperHostBridge?> EnsureGrasshopperLoadedAsync()
        {
            if (CopilotHostRegistry.GrasshopperBridge != null)
            {
                return CopilotHostRegistry.GrasshopperBridge;
            }

            SetStatus("Opening Grasshopper workspace...", CopilotTheme.AccentCyan);
            try
            {
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        RhinoApp.RunScript("_-Grasshopper _Window _Show _Enter", false);
                    }
                    catch (Exception)
                    {
                        // Ignore if running in headless test harness without Rhino command host.
                    }
                }));
            }
            catch (Exception)
            {
                // Ignore if RhinoApp host is unavailable outside Rhino.
            }

            for (int i = 0; i < 20; i++)
            {
                if (CopilotHostRegistry.GrasshopperBridge != null)
                {
                    return CopilotHostRegistry.GrasshopperBridge;
                }

                await Task.Delay(150).ConfigureAwait(true);
            }

            return CopilotHostRegistry.GrasshopperBridge;
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

        private async Task RunBridgeActionAsync(string prompt, ChatAction action, string busyMessage)
        {
            if (!CanStartAction()) return;

            IGrasshopperHostBridge? bridge = await EnsureGrasshopperLoadedAsync().ConfigureAwait(true);
            if (bridge is null)
            {
                const string missing = "Could not start Grasshopper automatically. Please open Grasshopper from Rhino and try again.";
                SetStatus(missing, CopilotTheme.WarningAmber);
                AppendBubble("GH Copilot", missing);
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            bool useRhinoSelection = _useRhinoSelection.Checked == true;
            if (useRhinoSelection && !(bridge is IChatHostBridge))
            {
                SetStatus("This host does not support explicit Rhino selection. Uncheck Use Rhino Selection to continue.", CopilotTheme.WarningAmber);
                return;
            }

            await ExecuteAsync(bridge, prompt, action, useRhinoSelection, busyMessage).ConfigureAwait(true);
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
            bool isQuickTurn = action == ChatAction.ExplainSelection ||
                               action == ChatAction.SaveRecipe ||
                               (action == ChatAction.Message && !useRhinoSelection && ChatPromptBuilder.IsConversationalOnlyPrompt(prompt));

            Action<int> onEstimate = estimate => Application.Instance?.AsyncInvoke(() =>
            {
                if (ReferenceEquals(_activeCts, cts)) ShowTokenEstimate(estimate);
            });
            Action<string> onStage = stage => Application.Instance?.AsyncInvoke(() =>
            {
                if (ReferenceEquals(_activeCts, cts))
                {
                    UpdateThinkingDisplay(stage);
                    SetStatus(stage, CopilotTheme.AccentCyan);
                }
            });

            if (chat != null)
            {
                chat.TokenEstimateChanged += onEstimate;
                chat.ProgressStageChanged += onStage;
            }

            SetRunningState(isRunning: true, statusText: busyMessage);
            StartEtaTimer(isQuickTurn);
            _tokenEstimateLabel.Text = "~… tok";
            _starterChipsPanel.Visible = false;

            AppendBubble("You", (action == ChatAction.Message ? "" : "[" + action + "] ")
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
                if (chat != null)
                {
                    chat.TokenEstimateChanged -= onEstimate;
                    chat.ProgressStageChanged -= onStage;
                }

                double elapsedSeconds = _turnStopwatch.Elapsed.TotalSeconds;
                string thoughtSummary = _latestThought;

                Application.Instance?.AsyncInvoke(() =>
                {
                    try
                    {
                        if (response != null)
                        {
                            if (response.IsSuccess)
                            {
                                if (isQuickTurn) _emaQuickTurnSeconds = Math.Max(1.0, 0.6 * _emaQuickTurnSeconds + 0.4 * elapsedSeconds);
                                else _emaGraphTurnSeconds = Math.Max(2.0, 0.6 * _emaGraphTurnSeconds + 0.4 * elapsedSeconds);
                            }

                            SetStatus(response.StatusLine, response.IsSuccess ? CopilotTheme.AccentEmerald : CopilotTheme.DangerRose);
                            string answer = string.IsNullOrWhiteSpace(response.Explanation) ? response.StatusLine : response.Explanation;
                            AppendBubble(
                                "GH Copilot",
                                (response.IsSuccess ? "" : "[Error] ") + answer,
                                thoughtSummary,
                                elapsedSeconds);

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
                            AppendBubble("GH Copilot", msg, thoughtSummary, elapsedSeconds);
                            _tokenEstimateLabel.Text = "~— tok";
                        }
                    }
                    finally
                    {
                        StopEtaTimer();
                        _useRhinoSelection.Checked = false;
                        _activeCts = null;
                        cts.Dispose();
                        SetRunningState(isRunning: false);
                        UpdateStarterChipsVisibility();
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
                SetStatus("Enter a message or pick a starter chip first.", CopilotTheme.WarningAmber);
                return;
            }

            await RunBridgeActionAsync(
                prompt, ChatAction.Message, "Processing your message...");
        }

        private async void OnExplainClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(
                hint, ChatAction.ExplainSelection, "Inspecting selection and asking LLM to explain...");
        }

        private async void OnFixClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(
                hint, ChatAction.FixSelection, "Inspecting selection and generating fix preview...");
        }

        private void OnCancelClicked(object? sender, EventArgs e)
        {
            SetStatus("Cancelling request...", CopilotTheme.WarningAmber);
            _activeCts?.Cancel();
        }

        private async void OnDemoClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;
            IGrasshopperHostBridge? bridge = await EnsureGrasshopperLoadedAsync().ConfigureAwait(true);
            if (bridge is null)
            {
                const string missing = "Could not start Grasshopper automatically. Please open Grasshopper from Rhino and try again.";
                SetStatus(missing, CopilotTheme.WarningAmber);
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            _starterChipsPanel.Visible = false;
            AppendBubble("You", "Spawn Demo (No LLM)");
            try
            {
                string status = bridge.SpawnDemoGraph();
                SetStatus(status, CopilotTheme.AccentEmerald);
                AppendBubble("GH Copilot", status + Environment.NewLine + "Slider(12.5) + Slider(7.5) -> Addition -> Panel.");
                UpdateDiagnosticsDisplay(new List<string>());
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message, CopilotTheme.DangerRose);
                AppendBubble("GH Copilot", "Error: " + ex.Message);
            }
            finally
            {
                _previewBar.RefreshState();
                UpdateStarterChipsVisibility();
            }
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
                UpdateStarterChipsVisibility();
                SetStatus("New chat ready. Accepted canvas objects are unchanged.", CopilotTheme.AccentCyan);
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message, CopilotTheme.DangerRose);
                AppendBubble("GH Copilot", "Error: " + ex.Message);
            }
        }

        private void AppendBubble(string role, string content, string? thought = null, double? elapsedSeconds = null)
        {
            string timestamp = DateTime.Now.ToString("HH:mm");
            string title = role == "You"
                ? $"┌─ You  •  {timestamp} ──────────────────────────"
                : elapsedSeconds.HasValue && elapsedSeconds.Value > 0
                    ? $"┌─ ✦ {role}  •  {timestamp} ({elapsedSeconds.Value:F1}s) ──────────"
                    : $"┌─ ✦ {role}  •  {timestamp} ──────────────────";

            var lines = new List<string> { title };
            if (!string.IsNullOrWhiteSpace(thought))
            {
                lines.Add($"│ {thought}");
                lines.Add("│");
            }

            string normalized = (content ?? string.Empty).Replace("\r\n", "\n");
            foreach (string line in normalized.Split('\n'))
            {
                lines.Add($"│ {line}");
            }

            lines.Add("└───────────────────────────────────────────");
            string bubble = string.Join(Environment.NewLine, lines);

            if (!_hasTranscriptEntries)
            {
                _hasTranscriptEntries = true;
                _transcriptBox.TextColor = CopilotTheme.TextPrimary;
                _transcriptBox.Text = bubble;
            }
            else
            {
                _transcriptBox.Text = (_transcriptBox.Text ?? string.Empty) + Environment.NewLine + Environment.NewLine + bubble;
            }

            _starterChipsPanel.Visible = false;
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
            if (statusText != null)
            {
                SetStatus(statusText, CopilotTheme.AccentCyan);
            }
        }
    }
}
