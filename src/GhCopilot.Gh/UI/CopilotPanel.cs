using System;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using GhCopilot.Core.Settings;
using Rhino.UI;

namespace GhCopilot.UI
{
    /// <summary>
    /// Root dark-themed GH Copilot shell combining branded header with live status pills,
    /// sleek segmented Chat / Settings switcher, and persistent contextual PreviewBar footer.
    /// </summary>
    internal sealed class CopilotPanel : Panel
    {
        private static Form? _modelessForm;
        private readonly PreviewBar _previewBar;
        private readonly PromptTab _promptTab;
        private readonly SettingsTab _settingsTab;
        private readonly Button _chatTabButton;
        private readonly Button _settingsTabButton;
        private readonly Panel _tabHost;
        private readonly Panel _connectionBadge;
        private readonly Label _connectionLabel;
        private readonly Label _modelBadgeLabel;

        public CopilotPanel()
        {
            BackgroundColor = CopilotTheme.CanvasBg;

            StartupDiagnostics.Write("Shared UI: constructing preview bar.");
            _previewBar = new PreviewBar();

            StartupDiagnostics.Write("Shared UI: constructing chat tab.");
            _promptTab = new PromptTab(_previewBar);

            StartupDiagnostics.Write("Shared UI: constructing settings tab.");
            _settingsTab = new SettingsTab();
            _settingsTab.SettingsSaved += (_, __) => RefreshHeaderBadges();

            // ── Branded Studio Header ──
            Label titleLabel = CopilotTheme.CreateLabel(
                "◆ GH Copilot",
                CopilotTheme.TextPrimary,
                CopilotTheme.TitleFont());

            _connectionBadge = CopilotTheme.CreateBadge(
                out _connectionLabel,
                "○ Open GH",
                CopilotTheme.WarningAmberDark,
                CopilotTheme.WarningAmber);
            _connectionBadge.ToolTip = "Grasshopper host bridge connection status";

            Panel modelBadge = CopilotTheme.CreateBadge(
                out _modelBadgeLabel,
                "Gemini",
                CopilotTheme.AccentCyanDark,
                CopilotTheme.AccentCyan);
            modelBadge.ToolTip = "Active BYOK provider and model (click Settings to configure)";

            var headerTop = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            headerTop.BeginHorizontal();
            headerTop.Add(titleLabel, xscale: true);
            headerTop.Add(_connectionBadge, xscale: false);
            headerTop.EndHorizontal();

            var headerBottom = new DynamicLayout { Padding = Padding.Empty, Spacing = new Size(6, 4) };
            headerBottom.BeginHorizontal();
            headerBottom.Add(modelBadge, xscale: false);
            headerBottom.Add(null, xscale: true);
            headerBottom.Add(CopilotTheme.CreateLabel($"v{PluginConstants.Version}", CopilotTheme.TextMuted, CopilotTheme.SmallFont()), xscale: false);
            headerBottom.EndHorizontal();

            // ── Segmented Chat / Settings Switcher ──
            _chatTabButton = CopilotTheme.CreateTabButton("Chat", active: true);
            _chatTabButton.Click += (_, __) => SelectTab(showSettings: false);

            _settingsTabButton = CopilotTheme.CreateTabButton("Settings", active: false);
            _settingsTabButton.Click += (_, __) => SelectTab(showSettings: true);

            var segmentedBar = new DynamicLayout
            {
                Padding = new Padding(2),
                Spacing = new Size(4, 0)
            };
            segmentedBar.AddRow(_chatTabButton, _settingsTabButton);
            Panel segmentedContainer = CopilotTheme.CreateCard(segmentedBar, CopilotTheme.BorderSubtle, CopilotTheme.InputBg, padding: 2);

            var headerContent = new DynamicLayout { Padding = new Padding(10, 8, 10, 8), Spacing = new Size(6, 6) };
            headerContent.BeginVertical();
            headerContent.Add(headerTop);
            headerContent.Add(headerBottom);
            headerContent.Add(segmentedContainer);
            headerContent.EndVertical();

            var headerBar = new Panel
            {
                BackgroundColor = CopilotTheme.Surface,
                Content = headerContent
            };

            _tabHost = new Panel
            {
                BackgroundColor = CopilotTheme.CanvasBg,
                Content = _promptTab
            };

            var footerHost = new Panel
            {
                BackgroundColor = CopilotTheme.CanvasBg,
                Padding = new Padding(8, 4, 8, 8),
                Content = _previewBar
            };

            var layout = new DynamicLayout
            {
                Padding = Padding.Empty,
                Spacing = Size.Empty
            };

            layout.BeginVertical();
            layout.Add(headerBar);
            layout.Add(CopilotTheme.CreateHorizontalDivider());
            layout.Add(_tabHost, yscale: true);
            layout.Add(footerHost);
            layout.EndVertical();

            Content = layout;
            CopilotHostRegistry.GrasshopperBridgeRegistered += OnBridgeRegistered;
            RefreshHeaderBadges();
            StartupDiagnostics.Write("Shared UI: layout attached.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CopilotHostRegistry.GrasshopperBridgeRegistered -= OnBridgeRegistered;
                if (_tabHost.Content != _promptTab) _promptTab.Dispose();
                if (_tabHost.Content != _settingsTab) _settingsTab.Dispose();
            }

            base.Dispose(disposing);
        }

        private void OnBridgeRegistered(object? sender, EventArgs e)
        {
            Application.Instance?.AsyncInvoke(RefreshHeaderBadges);
        }

        public void SelectTab(bool showSettings)
        {
            CopilotTheme.ApplyTabButtonState(_chatTabButton, active: !showSettings);
            CopilotTheme.ApplyTabButtonState(_settingsTabButton, active: showSettings);
            _tabHost.Content = showSettings ? (Control)_settingsTab : _promptTab;
        }

        public void RefreshHeaderBadges()
        {
            bool connected = CopilotHostRegistry.GrasshopperBridge != null;
            if (connected)
            {
                _connectionBadge.BackgroundColor = CopilotTheme.AccentEmeraldDark;
                _connectionLabel.TextColor = CopilotTheme.AccentEmerald;
                _connectionLabel.Text = "● GH Ready";
            }
            else
            {
                _connectionBadge.BackgroundColor = CopilotTheme.WarningAmberDark;
                _connectionLabel.TextColor = CopilotTheme.WarningAmber;
                _connectionLabel.Text = "○ Open GH";
            }

            AppSettings settings = AppSettings.Load();
            string provider = string.IsNullOrWhiteSpace(settings.Provider) ? AppSettings.DefaultProvider : settings.Provider;
            string model = string.IsNullOrWhiteSpace(settings.ModelId) ? AppSettings.DefaultModelId : settings.ModelId;
            _modelBadgeLabel.Text = $"{provider} • {model}";
        }

        public void RefreshPreviewBar()
        {
            RefreshHeaderBadges();
            _previewBar.RefreshState();
        }

        public static void ShowModelessForm()
        {
            if (_modelessForm != null && _modelessForm.Visible)
            {
                if (_modelessForm.Content is CopilotPanel existingPanel)
                {
                    existingPanel.RefreshPreviewBar();
                }
                _modelessForm.BringToFront();
                return;
            }

            var panel = new CopilotPanel();
            var form = new Form
            {
                Title = PluginConstants.ProductName,
                BackgroundColor = CopilotTheme.CanvasBg,
                ClientSize = new Size(460, 700),
                MinimumSize = new Size(360, 480),
                Resizable = true,
                Owner = RhinoEtoApp.MainWindowForOwner,
                Content = panel
            };

            form.Closed += (_, _) => _modelessForm = null;
            _modelessForm = form;
            form.Show();
        }
    }
}
