using System;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;

namespace GhCopilot.UI
{
    /// <summary>
    /// Persistent studio preview action footer driving GroupPreview / ChatTurnTransaction Accept and Reject.
    /// </summary>
    internal sealed class PreviewBar : Panel
    {
        private readonly Panel _card;
        private readonly Panel _pill;
        private readonly Label _pillLabel;
        private readonly Label _previewStatusLabel;
        private readonly Button _acceptButton;
        private readonly Button _rejectButton;
        private IChatHostBridge? _subscribedChatBridge;
        private bool _isBusy;

        public PreviewBar()
        {
            BackgroundColor = CopilotTheme.CanvasBg;

            _pill = CopilotTheme.CreateBadge(
                out _pillLabel,
                "IDLE",
                CopilotTheme.SurfaceElevated,
                CopilotTheme.TextMuted);

            _previewStatusLabel = CopilotTheme.CreateLabel(
                "No pending preview on canvas.",
                CopilotTheme.TextSecondary,
                CopilotTheme.SmallFont());

            _acceptButton = CopilotTheme.CreatePrimaryButton(
                "✓ Accept",
                "Keep the preview changes on the Grasshopper canvas as one undo step");
            _acceptButton.Enabled = false;
            _acceptButton.Click += OnAcceptClicked;

            _rejectButton = CopilotTheme.CreateDangerButton(
                "✕ Reject",
                "Roll back only this turn's preview changes cleanly");
            _rejectButton.Enabled = false;
            _rejectButton.Click += OnRejectClicked;

            var layout = new DynamicLayout
            {
                Padding = Padding.Empty,
                Spacing = new Size(8, 6)
            };

            layout.BeginVertical();
            layout.BeginHorizontal();
            layout.Add(_pill, xscale: false);
            layout.Add(_previewStatusLabel, xscale: true);
            layout.EndHorizontal();
            layout.AddRow(_acceptButton, _rejectButton);
            layout.EndVertical();

            _card = CopilotTheme.CreateCard(layout, CopilotTheme.BorderSubtle, CopilotTheme.Surface, padding: 8);
            Content = _card;

            AttachBridgeEvents();
            CopilotHostRegistry.GrasshopperBridgeRegistered += OnBridgeRegistered;
            RefreshState();
        }

        private void OnBridgeRegistered(object? sender, EventArgs e)
        {
            Application.Instance?.AsyncInvoke(() =>
            {
                AttachBridgeEvents();
                RefreshState();
            });
        }

        private void OnBridgePreviewStateChanged(object? sender, EventArgs e)
        {
            Application.Instance?.AsyncInvoke(RefreshState);
        }

        private void AttachBridgeEvents()
        {
            IChatHostBridge? chat = CopilotHostRegistry.GrasshopperBridge as IChatHostBridge;
            if (ReferenceEquals(chat, _subscribedChatBridge))
            {
                return;
            }

            if (_subscribedChatBridge != null)
            {
                _subscribedChatBridge.PreviewStateChanged -= OnBridgePreviewStateChanged;
            }

            _subscribedChatBridge = chat;
            if (_subscribedChatBridge != null)
            {
                _subscribedChatBridge.PreviewStateChanged += OnBridgePreviewStateChanged;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CopilotHostRegistry.GrasshopperBridgeRegistered -= OnBridgeRegistered;
                if (_subscribedChatBridge != null)
                {
                    _subscribedChatBridge.PreviewStateChanged -= OnBridgePreviewStateChanged;
                    _subscribedChatBridge = null;
                }
            }

            base.Dispose(disposing);
        }

        public event EventHandler<string>? PreviewActionCompleted;
        public event EventHandler? PreviewStateChanged;

        public void SetBusy(bool busy)
        {
            _isBusy = busy;
            RefreshState();
        }

        public void RefreshState()
        {
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            bool hasPreview = bridge != null && bridge.HasActivePreview;

            _acceptButton.Enabled = hasPreview && !_isBusy;
            _rejectButton.Enabled = hasPreview && !_isBusy;

            if (hasPreview)
            {
                CopilotTheme.SetCardBorder(_card, CopilotTheme.WarningAmber);
                _pill.BackgroundColor = CopilotTheme.WarningAmberDark;
                _pillLabel.TextColor = CopilotTheme.WarningAmber;
                _pillLabel.Text = "PREVIEW READY";
                _previewStatusLabel.TextColor = CopilotTheme.TextPrimary;
                _previewStatusLabel.Text = "Review on canvas, then Accept or Reject.";
            }
            else if (_isBusy)
            {
                CopilotTheme.SetCardBorder(_card, CopilotTheme.AccentCyan);
                _pill.BackgroundColor = CopilotTheme.AccentCyanDark;
                _pillLabel.TextColor = CopilotTheme.AccentCyan;
                _pillLabel.Text = "WORKING";
                _previewStatusLabel.TextColor = CopilotTheme.TextSecondary;
                _previewStatusLabel.Text = "Generating and validating graph turn...";
            }
            else
            {
                CopilotTheme.SetCardBorder(_card, CopilotTheme.BorderSubtle);
                _pill.BackgroundColor = CopilotTheme.SurfaceElevated;
                _pillLabel.TextColor = CopilotTheme.TextMuted;
                _pillLabel.Text = "IDLE";
                _previewStatusLabel.TextColor = CopilotTheme.TextSecondary;
                _previewStatusLabel.Text = "No pending preview on canvas.";
            }

            PreviewStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnAcceptClicked(object? sender, EventArgs e)
        {
            if (_isBusy) return;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null || !bridge.AcceptActivePreview())
            {
                RefreshState();
                MessageBox.Show(this, "No active preview group to accept.", PluginConstants.ProductName, MessageBoxType.Information);
                return;
            }

            RefreshState();
            PreviewActionCompleted?.Invoke(this, "Preview accepted.");
        }

        private void OnRejectClicked(object? sender, EventArgs e)
        {
            if (_isBusy) return;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null || !bridge.RejectActivePreview())
            {
                RefreshState();
                MessageBox.Show(this, "No active preview group to reject.", PluginConstants.ProductName, MessageBoxType.Information);
                return;
            }

            RefreshState();
            PreviewActionCompleted?.Invoke(this, "Preview rejected and removed.");
        }
    }
}
