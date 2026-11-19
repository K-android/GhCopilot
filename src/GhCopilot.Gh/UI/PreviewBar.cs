using System;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;

namespace GhCopilot.UI
{
    /// <summary>
    /// Persistent preview action bar driving GroupPreview Accept / Reject.
    /// </summary>
    internal sealed class PreviewBar : Panel
    {
        private readonly Label _previewStatusLabel;
        private readonly Button _acceptButton;
        private readonly Button _rejectButton;
        private bool _isBusy;

        public PreviewBar()
        {
            _previewStatusLabel = new Label
            {
                Text = "No active preview on canvas."
            };

            _acceptButton = new Button
            {
                Text = "Accept Preview",
                Enabled = false
            };
            _acceptButton.Click += OnAcceptClicked;

            _rejectButton = new Button
            {
                Text = "Reject Preview",
                Enabled = false
            };
            _rejectButton.Click += OnRejectClicked;

            var layout = new DynamicLayout
            {
                Padding = new Padding(8),
                Spacing = new Size(6, 6)
            };

            layout.BeginVertical();
            layout.Add(_previewStatusLabel);
            layout.AddRow(_acceptButton, _rejectButton);
            layout.EndVertical();

            Content = layout;
            RefreshState();
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
            _previewStatusLabel.Text = hasPreview
                ? "Preview ready: Accept to keep or Reject to undo cleanly."
                : "No active preview on canvas.";
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
