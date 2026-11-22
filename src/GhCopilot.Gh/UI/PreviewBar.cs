using System;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;

namespace GhCopilot.UI
{
    /// <summary>
    /// Persistent studio action footer driving Grasshopper Accept/Reject previews and Direct Rhino Undo.
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
        private IRhinoHostBridge? _subscribedRhinoBridge;
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
                "Ready — Rhino Direct or Grasshopper preview idle.",
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
            CopilotHostRegistry.RhinoBridgeRegistered += OnBridgeRegistered;
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
            if (!ReferenceEquals(chat, _subscribedChatBridge))
            {
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

            IRhinoHostBridge? rhino = CopilotHostRegistry.RhinoBridge;
            if (!ReferenceEquals(rhino, _subscribedRhinoBridge))
            {
                if (_subscribedRhinoBridge != null)
                {
                    _subscribedRhinoBridge.RhinoStateChanged -= OnBridgePreviewStateChanged;
                }

                _subscribedRhinoBridge = rhino;
                if (_subscribedRhinoBridge != null)
                {
                    _subscribedRhinoBridge.RhinoStateChanged += OnBridgePreviewStateChanged;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CopilotHostRegistry.GrasshopperBridgeRegistered -= OnBridgeRegistered;
                CopilotHostRegistry.RhinoBridgeRegistered -= OnBridgeRegistered;
                if (_subscribedChatBridge != null)
                {
                    _subscribedChatBridge.PreviewStateChanged -= OnBridgePreviewStateChanged;
                    _subscribedChatBridge = null;
                }
                if (_subscribedRhinoBridge != null)
                {
                    _subscribedRhinoBridge.RhinoStateChanged -= OnBridgePreviewStateChanged;
                    _subscribedRhinoBridge = null;
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
            IGrasshopperHostBridge? ghBridge = CopilotHostRegistry.GrasshopperBridge;
            IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
            bool hasPreview = ghBridge != null && ghBridge.HasActivePreview;
            bool canUndoRhino = rhinoBridge != null && rhinoBridge.CanUndoLastTurn;

            if (hasPreview)
            {
                _acceptButton.Enabled = !_isBusy;
                _rejectButton.Text = "✕ Reject";
                _rejectButton.ToolTip = "Roll back only this turn's preview changes cleanly";
                _rejectButton.Enabled = !_isBusy;

                CopilotTheme.SetCardBorder(_card, CopilotTheme.WarningAmber);
                _pill.BackgroundColor = CopilotTheme.WarningAmberDark;
                _pillLabel.TextColor = CopilotTheme.WarningAmber;
                _pillLabel.Text = "PREVIEW READY";
                _previewStatusLabel.TextColor = CopilotTheme.TextPrimary;
                _previewStatusLabel.Text = "Review on canvas, then Accept or Reject.";
            }
            else if (_isBusy)
            {
                _acceptButton.Enabled = false;
                _rejectButton.Text = "✕ Reject";
                _rejectButton.ToolTip = "Roll back only this turn's preview changes cleanly";
                _rejectButton.Enabled = false;

                CopilotTheme.SetCardBorder(_card, CopilotTheme.AccentCyan);
                _pill.BackgroundColor = CopilotTheme.AccentCyanDark;
                _pillLabel.TextColor = CopilotTheme.AccentCyan;
                _pillLabel.Text = "WORKING";
                _previewStatusLabel.TextColor = CopilotTheme.TextSecondary;
                _previewStatusLabel.Text = "Processing your request...";
            }
            else if (canUndoRhino)
            {
                _acceptButton.Enabled = false;
                _rejectButton.Text = "↶ Undo Rhino";
                _rejectButton.ToolTip = "Undo the last direct Rhino edit (or press Ctrl+Z in Rhino)";
                _rejectButton.Enabled = true;

                CopilotTheme.SetCardBorder(_card, CopilotTheme.AccentCyan);
                _pill.BackgroundColor = CopilotTheme.AccentCyanDark;
                _pillLabel.TextColor = CopilotTheme.AccentCyan;
                _pillLabel.Text = "RHINO APPLIED";
                _previewStatusLabel.TextColor = CopilotTheme.TextPrimary;
                _previewStatusLabel.Text = "Direct Rhino edit applied (Ctrl+Z or Undo supported).";
            }
            else
            {
                _acceptButton.Enabled = false;
                _rejectButton.Text = "✕ Reject";
                _rejectButton.ToolTip = "Roll back only this turn's preview changes cleanly";
                _rejectButton.Enabled = false;

                CopilotTheme.SetCardBorder(_card, CopilotTheme.BorderSubtle);
                _pill.BackgroundColor = CopilotTheme.SurfaceElevated;
                _pillLabel.TextColor = CopilotTheme.TextMuted;
                _pillLabel.Text = "IDLE";
                _previewStatusLabel.TextColor = CopilotTheme.TextSecondary;
                _previewStatusLabel.Text = "Ready — Rhino Direct or Grasshopper preview idle.";
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
            if (bridge != null && bridge.HasActivePreview)
            {
                if (!bridge.RejectActivePreview())
                {
                    RefreshState();
                    MessageBox.Show(this, "No active preview group to reject.", PluginConstants.ProductName, MessageBoxType.Information);
                    return;
                }

                RefreshState();
                PreviewActionCompleted?.Invoke(this, "Preview rejected and removed.");
                return;
            }

            IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
            if (rhinoBridge != null && rhinoBridge.CanUndoLastTurn)
            {
                string result = rhinoBridge.UndoLastRhinoTurn();
                RefreshState();
                PreviewActionCompleted?.Invoke(this, result);
                return;
            }

            RefreshState();
            MessageBox.Show(this, "No active preview or undoable direct Rhino edit.", PluginConstants.ProductName, MessageBoxType.Information);
        }
    }
}
