using System;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using Rhino.UI;

namespace GhCopilot.UI
{
    /// <summary>
    /// Root GH Copilot UI combining PromptTab, SettingsTab, and PreviewBar, plus modeless Eto Form fallback host.
    /// </summary>
    internal sealed class CopilotPanel : Panel
    {
        private static Form? _modelessForm;
        private readonly PreviewBar _previewBar;

        public CopilotPanel()
        {
            _previewBar = new PreviewBar();

            var promptTab = new PromptTab(_previewBar);
            var settingsTab = new SettingsTab();

            var tabs = new TabControl();
            tabs.Pages.Add(new TabPage { Text = "Prompt", Content = promptTab });
            tabs.Pages.Add(new TabPage { Text = "Settings", Content = settingsTab });

            var layout = new DynamicLayout
            {
                Padding = new Padding(4),
                Spacing = new Size(4, 4)
            };

            layout.Add(tabs, yscale: true);
            layout.Add(_previewBar);

            Content = layout;
        }

        public void RefreshPreviewBar()
        {
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
                ClientSize = new Size(430, 560),
                MinimumSize = new Size(340, 440),
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
