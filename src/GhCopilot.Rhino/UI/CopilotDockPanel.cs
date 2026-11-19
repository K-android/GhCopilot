using System;
using System.Runtime.InteropServices;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using GhCopilot.UI;

namespace GhCopilot.Rhino.UI
{
    /// <summary>
    /// Native Rhino dockable Eto panel hosting the shared GH Copilot Prompt/Settings/PreviewBar UI.
    /// </summary>
    [Guid(PluginConstants.DockPanelId)]
    public sealed class CopilotDockPanel : Panel
    {
        public CopilotDockPanel()
        {
            StartupDiagnostics.Write("Rhino dock panel constructor entered.");
            try
            {
                Content = new CopilotPanel();
                StartupDiagnostics.Write("Rhino dock panel content attached.");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Rhino dock panel construction failed.", ex);
                Content = new TextArea
                {
                    ReadOnly = true,
                    Wrap = true,
                    Text = "GH Copilot could not initialize.\n\n" + ex.GetBaseException().Message
                        + "\n\nStartup details: " + StartupDiagnostics.LogPath
                };
            }
        }
    }
}
