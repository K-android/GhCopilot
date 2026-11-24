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
using GhCopilot.Core.Settings;
using Rhino;

namespace GhCopilot.UI
{
    /// <summary>
    /// WhatsApp-style chat workspace supporting Plan-First architectural collaboration (questions + schematic
    /// diagrams + approval), Direct Rhino 8 Mode, Grasshopper parametric mode, live viewport vision, and ETA.
    /// </summary>
    internal sealed class PromptTab : Panel
    {
        private static readonly string[] RhinoChipLabels = { "Plan Pavilion", "Box + Zoom", "Inspect Scene" };
        private static readonly string[] RhinoChipTooltips =
        {
            "Collaborate on a plan and questions first, then build directly in Rhino 8",
            "Create a 20x20x10 emerald box directly in Rhino and zoom extents",
            "Inspect current Rhino viewport screenshot, objects, layers, and camera"
        };
        private static readonly string[] RhinoChipPrompts =
        {
            "Design a minimalist pavilion canopy with columns and roof slab in Rhino",
            "Create a 20x20x10 box at the origin, color it emerald green, and zoom extents",
            "What do you see in my live Rhino viewport right now?"
        };

        private static readonly string[] GhChipLabels = { "Circle + Slider", "Attractor Grid", "Spiral Tower" };
        private static readonly string[] GhChipTooltips =
        {
            "Insert sample prompt: parametric circle with radius slider",
            "Insert sample prompt: rectangular grid scaled by a point attractor",
            "Insert sample prompt: procedural twisted tower"
        };
        private static readonly string[] GhChipPrompts =
        {
            "Create a circle with an editable radius slider from 1 to 50",
            "Create a 10x10 point grid with circles whose radii vary by distance to an attractor point",
            "Create a parametric twisted tower with floor count, height, and rotation sliders"
        };

        private readonly PreviewBar _previewBar;
        private readonly Button _modeAutoButton;
        private readonly Button _modeRhinoButton;
        private readonly Button _modeGhButton;
        private readonly WhatsAppChatControl _chatControl;
        private readonly Panel _approvalCard;
        private readonly Button _approveBuildButton;
        private readonly Button _verifyViewportButton;
        private readonly TextArea _promptBox;
        private readonly Button _sendButton;
        private readonly Button _explainButton;
        private readonly Button _fixButton;
        private readonly Button _saveRecipeButton;
        private readonly Button _cancelButton;
        private readonly Button _demoButton;
        private readonly Button _newChatButton;
        private readonly Panel _starterChipsPanel;
        private readonly Button _chipOne;
        private readonly Button _chipTwo;
        private readonly Button _chipThree;
        private readonly CheckBox _planFirstCheck;
        private readonly CheckBox _useRhinoSelection;
        private readonly Panel _thinkingCard;
        private readonly Label _thinkingStageLabel;
        private readonly Label _thinkingTrailLabel;
        private readonly Label _etaLabel;
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly Label _tokenEstimateLabel;
        private readonly Button _diagnosticsToggle;
        private readonly Panel _diagnosticsBody;
        private readonly TextArea _warningsBox;

        private CopilotWorkMode _selectedMode = CopilotWorkMode.Auto;
        private CopilotWorkMode? _pendingPlanTargetMode;
        private CopilotWorkMode? _lastBuiltMode;
        private CancellationTokenSource? _activeCts;
        private UITimer? _etaTimer;
        private readonly Stopwatch _turnStopwatch = new Stopwatch();
        private double _expectedDurationSeconds = 9.0;
        private double _emaQuickTurnSeconds = 4.0;
        private double _emaGraphTurnSeconds = 12.0;
        private readonly List<string> _thoughtSteps = new List<string>();
        private string _latestThought = string.Empty;
        private bool _hasTranscriptEntries;
        private bool _diagnosticsExpanded;

        public PromptTab(PreviewBar previewBar)
        {
            _previewBar = previewBar ?? throw new ArgumentNullException(nameof(previewBar));
            BackgroundColor = CopilotTheme.CanvasBg;
            AppSettings initialSettings = AppSettings.Load();

            // Mode selector (Auto | Rhino Direct | Grasshopper)
            _modeAutoButton = CreateCompactModeButton("Auto", "Automatically pick Rhino Direct or Grasshopper based on your prompt and open workspace");
            _modeAutoButton.Click += (_, __) => SetWorkMode(CopilotWorkMode.Auto);

            _modeRhinoButton = CreateCompactModeButton("Rhino Direct", "Control Rhino 8 geometry, layers, colors, and viewport directly (never launches Grasshopper)");
            _modeRhinoButton.Click += (_, __) => SetWorkMode(CopilotWorkMode.RhinoDirect);

            _modeGhButton = CreateCompactModeButton("Grasshopper", "Build or inspect parametric Grasshopper definitions (auto-launches Grasshopper if needed)");
            _modeGhButton.Click += (_, __) => SetWorkMode(CopilotWorkMode.Grasshopper);

            var modeRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(4, 0) };
            modeRow.BeginHorizontal();
            modeRow.Add(CopilotTheme.CreateLabel("Mode:", CopilotTheme.TextSecondary, CopilotTheme.SmallBoldFont()), xscale: false);
            modeRow.Add(_modeAutoButton, xscale: true);
            modeRow.Add(_modeRhinoButton, xscale: true);
            modeRow.Add(_modeGhButton, xscale: true);
            modeRow.EndHorizontal();
            Panel modeCard = CopilotTheme.CreateCard(modeRow, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 4);

            // WhatsApp-style graphical chat control
            _chatControl = new WhatsAppChatControl { Height = 170 };
            _chatControl.StatusHintRequested += (_, hint) => SetStatus(hint, CopilotTheme.AccentCyan);

            _newChatButton = CopilotTheme.CreateSecondaryButton("+ New Chat", "Clear chat history and start fresh (keeps Rhino & canvas objects)");
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
            transcriptCardContent.Add(_chatControl, yscale: true);
            transcriptCardContent.EndVertical();
            Panel transcriptCard = CopilotTheme.CreateCard(transcriptCardContent, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 6);

            // Live AI Processing / Thoughts / ETA card (shown while a turn is running)
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

            // Plan Approval & Viewport Check bar (contextual: Approve Plan / Verify Live Viewport)
            _approveBuildButton = CopilotTheme.CreatePrimaryButton(
                "✓ Approve & Build",
                "Approve the proposed plan and build it in Rhino / Grasshopper now");
            _approveBuildButton.MinimumSize = new Size(92, 26);
            _approveBuildButton.Click += OnApproveBuildClicked;

            _verifyViewportButton = CopilotTheme.CreateChipButton(
                "👁️ Verify Viewport",
                "Capture the live Rhino 8 viewport screenshot and ask Copilot to inspect it");
            _verifyViewportButton.MinimumSize = new Size(92, 26);
            _verifyViewportButton.Click += OnVerifyViewportClicked;

            var approvalRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            approvalRow.AddRow(_approveBuildButton, _verifyViewportButton);
            _approvalCard = CopilotTheme.CreateCard(approvalRow, CopilotTheme.AccentCyan, CopilotTheme.SurfaceElevated, padding: 5);
            _approvalCard.Visible = false;

            // Context-aware starter prompt chips (shown only before user types or sends)
            _chipOne = CopilotTheme.CreateChipButton("Plan Pavilion");
            _chipOne.Click += (_, __) => PopulatePromptForChip(0);

            _chipTwo = CopilotTheme.CreateChipButton("Box + Zoom");
            _chipTwo.Click += (_, __) => PopulatePromptForChip(1);

            _chipThree = CopilotTheme.CreateChipButton("Inspect Scene");
            _chipThree.Click += (_, __) => PopulatePromptForChip(2);

            var chipsRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            chipsRow.BeginHorizontal();
            chipsRow.Add(CopilotTheme.CreateLabel("Try:", CopilotTheme.TextMuted, CopilotTheme.SmallBoldFont()), xscale: false);
            chipsRow.Add(_chipOne, xscale: true);
            chipsRow.Add(_chipTwo, xscale: true);
            chipsRow.Add(_chipThree, xscale: true);
            chipsRow.EndHorizontal();
            _starterChipsPanel = new Panel { Content = chipsRow };

            // Quick workspace action buttons
            _explainButton = CopilotTheme.CreateSecondaryButton("Explain", "Explain active Rhino viewport/scene (or selected Grasshopper nodes)");
            _explainButton.Click += OnExplainClicked;

            _fixButton = CopilotTheme.CreateSecondaryButton("Fix Errors", "Inspect and repair runtime errors on selected Grasshopper components");
            _fixButton.Click += OnFixClicked;

            _saveRecipeButton = CopilotTheme.CreateSecondaryButton("Save Recipe", "Save selected Grasshopper components locally as a reusable recipe");
            _saveRecipeButton.Click += OnSaveRecipeClicked;

            _demoButton = CopilotTheme.CreateSecondaryButton("Demo", "Spawn an offline demo directly in Rhino or Grasshopper (no API key required)");
            _demoButton.Click += OnDemoClicked;

            var toolsRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            toolsRow.AddRow(_explainButton, _fixButton, _saveRecipeButton, _demoButton);

            // Studio composer card
            _promptBox = new TextArea
            {
                Text = string.Empty,
                Height = 52,
                Wrap = true
            };
            CopilotTheme.StyleTextArea(_promptBox);
            _promptBox.KeyDown += OnPromptKeyDown;
            _promptBox.TextChanged += (_, __) => UpdateStarterChipsVisibility();

            _planFirstCheck = new CheckBox
            {
                Text = "Plan First",
                Checked = initialSettings.PlanFirstMode,
                ToolTip = "Discuss the plan and clarifying questions (with diagrams when needed) before building"
            };
            CopilotTheme.StyleCheckBox(_planFirstCheck);
            _planFirstCheck.CheckedChanged += OnPlanFirstToggled;

            _useRhinoSelection = new CheckBox
            {
                Text = "Use Rhino Selection",
                Checked = false,
                ToolTip = "Explicitly bind currently selected Rhino geometry into Grasshopper inputs for this turn only (auto-resets)"
            };
            CopilotTheme.StyleCheckBox(_useRhinoSelection);

            _cancelButton = CopilotTheme.CreateDangerButton("Stop", "Abort the active request");
            _cancelButton.Enabled = false;
            _cancelButton.Click += OnCancelClicked;

            _sendButton = CopilotTheme.CreatePrimaryButton("Send ➤", "Send message to GH Copilot (Ctrl+Enter)");
            _sendButton.Click += OnSendClicked;

            var optionsRow = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(10, 2) };
            optionsRow.BeginHorizontal();
            optionsRow.Add(_planFirstCheck, xscale: false);
            optionsRow.Add(_useRhinoSelection, xscale: true);
            optionsRow.EndHorizontal();

            var composerFooter = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            composerFooter.BeginHorizontal();
            composerFooter.Add(CopilotTheme.CreateLabel("Ctrl+Enter to send", CopilotTheme.TextMuted, CopilotTheme.SmallFont()), xscale: true);
            composerFooter.Add(_cancelButton, xscale: false);
            composerFooter.Add(_sendButton, xscale: false);
            composerFooter.EndHorizontal();

            var composerContent = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 5) };
            composerContent.BeginVertical();
            composerContent.Add(_promptBox);
            composerContent.Add(optionsRow);
            composerContent.Add(composerFooter);
            composerContent.EndVertical();
            Panel composerCard = CopilotTheme.CreateCard(composerContent, CopilotTheme.BorderStrong, CopilotTheme.Surface, padding: 8);

            // Status & collapsible diagnostics drawer
            _statusLabel = CopilotTheme.CreateLabel(
                BuildDefaultReadyStatus(),
                CopilotTheme.TextSecondary,
                CopilotTheme.SmallFont());

            _diagnosticsToggle = CopilotTheme.CreateSecondaryButton("▸ Diagnostics: Clean", "Toggle validator warnings and runtime messages");
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
                Spacing = new Size(6, 5)
            };

            layout.BeginVertical();
            layout.Add(modeCard);
            layout.Add(transcriptCard, yscale: true);
            layout.Add(_thinkingCard);
            layout.Add(_approvalCard);
            layout.Add(_starterChipsPanel);
            layout.Add(toolsRow);
            layout.Add(composerCard);
            layout.Add(statusBar);
            layout.Add(_diagnosticsBody);
            layout.EndVertical();
            Content = layout;

            SyncTranscriptFromSession();
            SetWorkMode(CopilotWorkMode.Auto, announceStatus: false);
            CopilotHostRegistry.GrasshopperBridgeRegistered += OnBridgeRegistered;
            CopilotHostRegistry.RhinoBridgeRegistered += OnBridgeRegistered;
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
                CopilotHostRegistry.RhinoBridgeRegistered -= OnBridgeRegistered;
                StopEtaTimer();
            }

            base.Dispose(disposing);
        }

        private static Button CreateCompactModeButton(string text, string toolTip)
        {
            var button = CopilotTheme.CreateTabButton(text, active: false);
            button.MinimumSize = new Size(52, 24);
            button.ToolTip = toolTip;
            return button;
        }

        private bool IsRhinoPrimaryMode()
        {
            if (_selectedMode == CopilotWorkMode.RhinoDirect) return true;
            if (_selectedMode == CopilotWorkMode.Grasshopper) return false;
            return CopilotHostRegistry.GrasshopperBridge == null;
        }

        private void SetWorkMode(CopilotWorkMode mode, bool announceStatus = true)
        {
            _selectedMode = mode;
            CopilotTheme.ApplyTabButtonState(_modeAutoButton, mode == CopilotWorkMode.Auto);
            CopilotTheme.ApplyTabButtonState(_modeRhinoButton, mode == CopilotWorkMode.RhinoDirect);
            CopilotTheme.ApplyTabButtonState(_modeGhButton, mode == CopilotWorkMode.Grasshopper);

            RefreshStarterChipsContent();
            UpdateStarterChipsVisibility();
            UpdateActionState();

            if (announceStatus)
            {
                string status = mode switch
                {
                    CopilotWorkMode.RhinoDirect => "Mode: Rhino Direct (edits Rhino scene directly without Grasshopper).",
                    CopilotWorkMode.Grasshopper => "Mode: Grasshopper (parametric canvas generation & patch mode).",
                    _ => "Mode: Auto (routes automatically between Rhino Direct and Grasshopper)."
                };
                SetStatus(status, CopilotTheme.AccentCyan);
            }
        }

        private void OnPlanFirstToggled(object? sender, EventArgs e)
        {
            try
            {
                AppSettings settings = AppSettings.Load();
                settings.PlanFirstMode = _planFirstCheck.Checked != false;
                settings.Save();
                SetStatus(
                    settings.PlanFirstMode
                        ? "Plan First enabled: Copilot will confirm a plan & questions before building."
                        : "Plan First disabled: Copilot will build immediately on Send.",
                    CopilotTheme.AccentCyan);
            }
            catch (Exception)
            {
                // Non-fatal UI persistence.
            }
        }

        private void RefreshStarterChipsContent()
        {
            bool rhinoPrimary = IsRhinoPrimaryMode();
            string[] labels = rhinoPrimary ? RhinoChipLabels : GhChipLabels;
            string[] tips = rhinoPrimary ? RhinoChipTooltips : GhChipTooltips;

            _chipOne.Text = labels[0];
            _chipOne.ToolTip = tips[0];
            _chipTwo.Text = labels[1];
            _chipTwo.ToolTip = tips[1];
            _chipThree.Text = labels[2];
            _chipThree.ToolTip = tips[2];
        }

        private void PopulatePromptForChip(int slotIndex)
        {
            string[] prompts = IsRhinoPrimaryMode() ? RhinoChipPrompts : GhChipPrompts;
            if (slotIndex >= 0 && slotIndex < prompts.Length)
            {
                PopulatePrompt(prompts[slotIndex]);
            }
        }

        private string BuildDefaultReadyStatus()
        {
            bool ghReady = CopilotHostRegistry.GrasshopperBridge != null;
            bool rhinoReady = CopilotHostRegistry.RhinoBridge != null;
            if (ghReady && rhinoReady) return "Ready — Rhino Direct & Grasshopper connected.";
            if (ghReady) return "Ready — Grasshopper connected.";
            if (rhinoReady) return "Ready — Rhino Direct ready (no Grasshopper needed).";
            return "Ready — Rhino & Grasshopper workspace ready.";
        }

        private void UpdateStarterChipsVisibility()
        {
            bool show = !_hasTranscriptEntries && string.IsNullOrWhiteSpace(_promptBox.Text);
            _starterChipsPanel.Visible = show;
        }

        private void RefreshApprovalCardVisibility()
        {
            bool running = _activeCts != null;
            ChatSession session = CopilotHostRegistry.SharedSession;
            bool hasPendingPlan = !string.IsNullOrWhiteSpace(session.PendingPlanSummary);
            bool hasBuilt = session.HasBuiltInCurrentSession;

            _approvalCard.Visible = !running && (hasPendingPlan || hasBuilt);
            _approveBuildButton.Visible = hasPendingPlan;
            _approveBuildButton.Enabled = !running && hasPendingPlan;
            _verifyViewportButton.Enabled = !running;
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

        private void StartEtaTimer(bool isQuickTurn, bool isRhinoDirect = false)
        {
            StopEtaTimer();
            _thoughtSteps.Clear();
            _latestThought = string.Empty;
            _expectedDurationSeconds = isQuickTurn ? _emaQuickTurnSeconds : _emaGraphTurnSeconds;
            _turnStopwatch.Restart();
            UpdateThinkingDisplay(isQuickTurn
                ? "💭 Reading context & live viewport..."
                : (isRhinoDirect
                    ? "🧠 Inspecting Rhino viewport & planning actions..."
                    : "🧠 Analyzing prompt & preparing Grasshopper workspace..."));

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
            _chatControl.SetThinkingState(false);
        }

        private void UpdateEtaTick()
        {
            double elapsed = _turnStopwatch.Elapsed.TotalSeconds;
            double remaining = Math.Max(0.5, _expectedDurationSeconds - elapsed);
            string etaText = elapsed < _expectedDurationSeconds
                ? $"{elapsed:F1}s • ETA ~{remaining:F0}s"
                : $"{elapsed:F1}s • Finalizing...";
            _etaLabel.Text = etaText;

            string trail = _thoughtSteps.Count > 1
                ? "Steps: " + string.Join("  →  ", _thoughtSteps)
                : "Inspecting scene & thinking...";
            _chatControl.SetThinkingState(true, _latestThought, trail, etaText);
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
                RefreshStarterChipsContent();
                UpdateStarterChipsVisibility();
                UpdateActionState();
                if (_statusLabel.Text.StartsWith("Ready", StringComparison.Ordinal) ||
                    _statusLabel.Text.StartsWith("Waiting for", StringComparison.Ordinal))
                {
                    SetStatus(BuildDefaultReadyStatus(), CopilotTheme.AccentEmerald);
                }
            });
        }

        private void SyncTranscriptFromSession()
        {
            if (_hasTranscriptEntries)
            {
                return;
            }

            ChatSession session = CopilotHostRegistry.SharedSession;
            if (session.Messages.Count > 0)
            {
                if (!string.IsNullOrEmpty(session.Summary))
                {
                    AppendBubble("Context", session.Summary);
                }

                foreach (ChatMessage message in session.Messages)
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
            return _activeCts == null;
        }

        private CopilotWorkMode ResolveRoutingForPrompt(string prompt, bool useRhinoSelection)
        {
            bool isGhOpen = CopilotHostRegistry.GrasshopperBridge != null;
            if (_selectedMode != CopilotWorkMode.Auto || useRhinoSelection)
            {
                return RhinoPromptBuilder.ResolveEffectiveMode(_selectedMode, prompt, isGhOpen, useRhinoSelection);
            }

            ChatSession session = CopilotHostRegistry.SharedSession;
            if (RhinoPromptBuilder.HasExplicitGrasshopperIntent(prompt) ||
                ChatPromptBuilder.RequestsRhinoSelectionInPrompt(prompt))
            {
                return CopilotWorkMode.Grasshopper;
            }

            CopilotWorkMode resolved = RhinoPromptBuilder.ResolveEffectiveMode(CopilotWorkMode.Auto, prompt, isGhOpen, false);

            // Preserve planned/active target engine when user approves a plan or requests a follow-up tweak
            // so e.g. "yes, build it" or "make it taller" continues in whichever mode drafted/built it.
            if (!string.IsNullOrWhiteSpace(session.PendingPlanSummary) && _pendingPlanTargetMode.HasValue)
            {
                return _pendingPlanTargetMode.Value;
            }

            if (session.HasBuiltInCurrentSession &&
                _lastBuiltMode.HasValue &&
                ChatPromptBuilder.IsFollowUpAlterationPrompt(prompt))
            {
                return _lastBuiltMode.Value;
            }

            return resolved;
        }

        private async Task SendPromptTextAsync(string prompt)
        {
            if (!CanStartAction()) return;

            bool useRhinoSelection = _useRhinoSelection.Checked == true;
            CopilotWorkMode effectiveMode = ResolveRoutingForPrompt(prompt, useRhinoSelection);

            if (effectiveMode == CopilotWorkMode.RhinoDirect)
            {
                IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
                if (rhinoBridge is null)
                {
                    const string missingRhino = "Direct Rhino host bridge is not initialized yet.";
                    SetStatus(missingRhino, CopilotTheme.WarningAmber);
                    AppendBubble("GH Copilot", missingRhino);
                    return;
                }

                await ExecuteRhinoDirectAsync(rhinoBridge, prompt, explainOnly: false, "Working in Direct Rhino Mode...", effectiveMode);
                return;
            }

            await RunBridgeActionAsync(prompt, ChatAction.Message, "Processing in Grasshopper...", effectiveMode);
        }

        private async Task RunBridgeActionAsync(
            string prompt,
            ChatAction action,
            string busyMessage,
            CopilotWorkMode targetMode = CopilotWorkMode.Grasshopper)
        {
            if (!CanStartAction()) return;

            // Avoid launching Grasshopper unnecessarily when merely planning in Auto or chatting conversationally.
            bool needsGrasshopperLaunch = true;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null && needsGrasshopperLaunch)
            {
                bridge = await EnsureGrasshopperLoadedAsync().ConfigureAwait(true);
            }

            if (bridge is null)
            {
                const string missing = "Could not start Grasshopper automatically. Switch to Rhino Direct or open Grasshopper from Rhino and try again.";
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

            await ExecuteGrasshopperAsync(bridge, prompt, action, useRhinoSelection, busyMessage, targetMode).ConfigureAwait(true);
        }

        private Task ExecuteGrasshopperAsync(
            IGrasshopperHostBridge bridge,
            string prompt,
            ChatAction action,
            bool useRhinoSelection,
            string busyMessage,
            CopilotWorkMode targetMode)
        {
            IChatHostBridge? chat = bridge as IChatHostBridge;
            ChatSession session = CopilotHostRegistry.SharedSession;
            AppSettings settings = AppSettings.Load();
            bool isQuickTurn = action == ChatAction.ExplainSelection ||
                               action == ChatAction.SaveRecipe ||
                               (action == ChatAction.Message && !useRhinoSelection &&
                                (ChatPromptBuilder.IsConversationalOnlyPrompt(prompt) ||
                                 ChatPromptBuilder.ShouldPlanBeforeBuilding(
                                     prompt,
                                     session,
                                     _planFirstCheck.Checked != false,
                                     forcePlanFirstAlways: settings.ForcePlanFirstAlways)));
            string userBubble = (action == ChatAction.Message ? string.Empty : "[" + action + "] ")
                + prompt + (useRhinoSelection ? " [Use Rhino Selection]" : string.Empty);

            return RunManagedTurnAsync(
                userBubble,
                isQuickTurn,
                isRhinoDirect: false,
                targetMode: targetMode,
                clearPromptOnSuccess: action == ChatAction.Message,
                busyMessage,
                async (ct, onEstimate, onStage) =>
                {
                    if (chat != null)
                    {
                        chat.TokenEstimateChanged += onEstimate;
                        chat.ProgressStageChanged += onStage;
                        try
                        {
                            return await chat.SendChatAsync(prompt, action, useRhinoSelection, ct);
                        }
                        finally
                        {
                            chat.TokenEstimateChanged -= onEstimate;
                            chat.ProgressStageChanged -= onStage;
                        }
                    }

                    switch (action)
                    {
                        case ChatAction.ExplainSelection:
                            return await bridge.ExplainSelectionAsync(prompt, ct);
                        case ChatAction.FixSelection:
                            return await bridge.FixSelectionAsync(prompt, ct);
                        case ChatAction.SaveRecipe:
                            string name = prompt.Length > 48 ? prompt.Substring(0, 48).Trim() : prompt;
                            string result = bridge.SaveSelectionAsRecipe(name, prompt);
                            return new CopilotGenerationResponse { IsSuccess = true, StatusLine = result, Explanation = result };
                        default:
                            return await bridge.RunPromptAsync(prompt, ct);
                    }
                },
                supportsTokens: chat != null);
        }

        private Task ExecuteRhinoDirectAsync(
            IRhinoHostBridge rhinoBridge,
            string prompt,
            bool explainOnly,
            string busyMessage,
            CopilotWorkMode targetMode = CopilotWorkMode.RhinoDirect)
        {
            ChatSession session = CopilotHostRegistry.SharedSession;
            AppSettings settings = AppSettings.Load();
            bool isQuickTurn = explainOnly ||
                               ChatPromptBuilder.IsConversationalOnlyPrompt(prompt) ||
                               ChatPromptBuilder.ShouldPlanBeforeBuilding(
                                   prompt,
                                   session,
                                   _planFirstCheck.Checked != false,
                                   forcePlanFirstAlways: settings.ForcePlanFirstAlways,
                                   isSingleDirectRhinoOp: ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt(prompt));
            string displayPrompt = explainOnly
                ? (string.IsNullOrWhiteSpace(prompt) ? "Verify & explain my live Rhino viewport" : "[Explain Rhino] " + prompt)
                : prompt;

            return RunManagedTurnAsync(
                displayPrompt,
                isQuickTurn,
                isRhinoDirect: true,
                targetMode: targetMode,
                clearPromptOnSuccess: !explainOnly,
                busyMessage,
                async (ct, onEstimate, onStage) =>
                {
                    rhinoBridge.TokenEstimateChanged += onEstimate;
                    rhinoBridge.ProgressStageChanged += onStage;
                    try
                    {
                        return await rhinoBridge.SendRhinoChatAsync(prompt, explainOnly, ct);
                    }
                    finally
                    {
                        rhinoBridge.TokenEstimateChanged -= onEstimate;
                        rhinoBridge.ProgressStageChanged -= onStage;
                    }
                },
                supportsTokens: true);
        }

        private async Task RunManagedTurnAsync(
            string userBubbleText,
            bool isQuickTurn,
            bool isRhinoDirect,
            CopilotWorkMode targetMode,
            bool clearPromptOnSuccess,
            string busyMessage,
            Func<CancellationToken, Action<int>, Action<string>, Task<CopilotGenerationResponse>> runner,
            bool supportsTokens)
        {
            var cts = new CancellationTokenSource();
            _activeCts = cts;

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

            SetRunningState(isRunning: true, statusText: busyMessage);
            StartEtaTimer(isQuickTurn, isRhinoDirect);
            _tokenEstimateLabel.Text = "~… tok";
            _starterChipsPanel.Visible = false;

            AppendBubble("You", userBubbleText);
            UpdateDiagnosticsDisplay(new List<string>());

            CopilotGenerationResponse? response = null;
            string? failure = null;
            try
            {
                response = await runner(cts.Token, onEstimate, onStage);
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

                                if (response.IsPlanProposal)
                                {
                                    _pendingPlanTargetMode = targetMode;
                                }
                                else if (response.NodeCount > 0)
                                {
                                    _pendingPlanTargetMode = null;
                                    _lastBuiltMode = targetMode;
                                }
                            }

                            SetStatus(response.StatusLine, response.IsSuccess ? CopilotTheme.AccentEmerald : CopilotTheme.DangerRose);
                            string answer = string.IsNullOrWhiteSpace(response.Explanation) ? response.StatusLine : response.Explanation;
                            AppendBubble(
                                "GH Copilot",
                                (response.IsSuccess ? "" : "[Error] ") + answer,
                                thoughtSummary,
                                elapsedSeconds,
                                response.IsPlanProposal);

                            var combinedMessages = new List<string>();
                            foreach (string warning in response.ValidatorWarnings) combinedMessages.Add("[Validator] " + warning);
                            combinedMessages.AddRange(response.RuntimeMessages);
                            UpdateDiagnosticsDisplay(combinedMessages);
                            if (response.PromptTokens > 0 || response.ResponseTokens > 0)
                            {
                                ShowActualTokens(response.PromptTokens, response.ResponseTokens, response.ModelId, response.DurationMs);
                            }
                            else if (supportsTokens)
                            {
                                ShowTokenEstimate(response.EstimatedInputTokens);
                            }
                            else
                            {
                                _tokenEstimateLabel.Text = "n/a";
                            }
                            if (response.IsSuccess && clearPromptOnSuccess)
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
                        _previewBar.RefreshState();
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

            await SendPromptTextAsync(prompt);
        }

        private async void OnApproveBuildClicked(object? sender, EventArgs e)
        {
            await SendPromptTextAsync("[Approve & Build] Yes, approved — build the planned design now.");
        }

        private async void OnVerifyViewportClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;

            IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
            if (rhinoBridge != null)
            {
                await ExecuteRhinoDirectAsync(
                    rhinoBridge,
                    "Look closely at my live Rhino viewport image and geometry right now. Verify whether the last changes were implemented cleanly or if anything needs adjustment.",
                    explainOnly: true,
                    "Inspecting live Rhino viewport image...");
                return;
            }

            await RunBridgeActionAsync(
                "Inspect my live Rhino viewport image and geometry right now and confirm whether everything is working cleanly.",
                ChatAction.ExplainSelection,
                "Inspecting live viewport...");
        }

        private async void OnExplainClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;
            string hint = _promptBox.Text?.Trim() ?? string.Empty;

            if (IsRhinoPrimaryMode())
            {
                IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
                if (rhinoBridge != null)
                {
                    await ExecuteRhinoDirectAsync(rhinoBridge, hint, explainOnly: true, "Inspecting Rhino scene & viewport...");
                    return;
                }
            }

            await RunBridgeActionAsync(hint, ChatAction.ExplainSelection, "Inspecting selection and asking LLM to explain...");
        }

        private async void OnFixClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(hint, ChatAction.FixSelection, "Inspecting selection and generating fix preview...");
        }

        private void OnCancelClicked(object? sender, EventArgs e)
        {
            SetStatus("Cancelling request...", CopilotTheme.WarningAmber);
            _activeCts?.Cancel();
        }

        private async void OnDemoClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;

            if (IsRhinoPrimaryMode())
            {
                IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
                if (rhinoBridge != null)
                {
                    _starterChipsPanel.Visible = false;
                    AppendBubble("You", "Spawn Rhino Demo (Direct Rhino, No LLM)");
                    try
                    {
                        string status = rhinoBridge.SpawnRhinoDemo();
                        CopilotHostRegistry.SharedSession.HasBuiltInCurrentSession = true;
                        _lastBuiltMode = CopilotWorkMode.RhinoDirect;
                        SetStatus(status, CopilotTheme.AccentEmerald);
                        AppendBubble("GH Copilot", status + Environment.NewLine + "Layer 'GH Copilot Demo': pedestal plinth + orb + 4 columns (Shaded + Zoom Extents).");
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
                        UpdateActionState();
                        UpdateStarterChipsVisibility();
                    }
                    return;
                }
            }

            IGrasshopperHostBridge? bridge = await EnsureGrasshopperLoadedAsync().ConfigureAwait(true);
            if (bridge is null)
            {
                const string missing = "Could not start Grasshopper automatically. Switch to Rhino Direct or open Grasshopper from Rhino and try again.";
                SetStatus(missing, CopilotTheme.WarningAmber);
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            _starterChipsPanel.Visible = false;
            AppendBubble("You", "Spawn Demo (No LLM)");
            try
            {
                string status = bridge.SpawnDemoGraph();
                CopilotHostRegistry.SharedSession.HasBuiltInCurrentSession = true;
                _lastBuiltMode = CopilotWorkMode.Grasshopper;
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
                UpdateActionState();
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
                CopilotHostRegistry.RhinoBridge?.NewChat();
                CopilotHostRegistry.SharedSession.Clear();
                _pendingPlanTargetMode = null;
                _lastBuiltMode = null;
                _hasTranscriptEntries = false;
                _chatControl.ClearMessages();
                _promptBox.Text = string.Empty;
                _useRhinoSelection.Checked = false;
                _tokenEstimateLabel.Text = "~0 tok";
                UpdateDiagnosticsDisplay(new List<string>());
                SetDiagnosticsExpanded(false);
                UpdateStarterChipsVisibility();
                UpdateActionState();
                _previewBar.RefreshState();
                SetStatus("New chat ready. Existing Rhino & canvas objects are unchanged.", CopilotTheme.AccentCyan);
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message, CopilotTheme.DangerRose);
                AppendBubble("GH Copilot", "Error: " + ex.Message);
            }
        }

        private void AppendBubble(
            string role,
            string content,
            string? thought = null,
            double? elapsedSeconds = null,
            bool isPlanProposal = false,
            string? viewportImageBase64Png = null,
            string? viewportSummary = null)
        {
            _chatControl.AppendMessage(
                role,
                content,
                thought,
                elapsedSeconds,
                isPlanProposal,
                viewportImageBase64Png,
                viewportSummary);
            _hasTranscriptEntries = _chatControl.HasMessages;
            _starterChipsPanel.Visible = false;
            RefreshApprovalCardVisibility();
        }

        private void ShowTokenEstimate(int estimate)
        {
            _tokenEstimateLabel.Text = $"~{Math.Max(0, estimate):N0} tok";
            _tokenEstimateLabel.ToolTip = "Approximate input token estimate";
        }

        private void ShowActualTokens(int promptTokens, int responseTokens, string? modelId, long durationMs)
        {
            _tokenEstimateLabel.Text = $"{Math.Max(0, promptTokens):N0} in / {Math.Max(0, responseTokens):N0} out";
            string modelText = string.IsNullOrWhiteSpace(modelId) ? "model" : modelId!;
            _tokenEstimateLabel.ToolTip = $"Actual provider tokens ({modelText}, {durationMs} ms)";
        }

        private void UpdateActionState()
        {
            bool running = _activeCts != null;
            bool ghToolsAvailable = _selectedMode != CopilotWorkMode.RhinoDirect;

            _modeAutoButton.Enabled = !running;
            _modeRhinoButton.Enabled = !running;
            _modeGhButton.Enabled = !running;
            _sendButton.Enabled = !running;
            _explainButton.Enabled = !running;
            _fixButton.Enabled = !running && ghToolsAvailable;
            _saveRecipeButton.Enabled = !running && ghToolsAvailable;
            _demoButton.Enabled = !running;
            _newChatButton.Enabled = !running;
            _planFirstCheck.Enabled = !running;
            _useRhinoSelection.Enabled = !running && ghToolsAvailable;
            _chipOne.Enabled = !running;
            _chipTwo.Enabled = !running;
            _chipThree.Enabled = !running;
            _promptBox.Enabled = !running;
            RefreshApprovalCardVisibility();
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
