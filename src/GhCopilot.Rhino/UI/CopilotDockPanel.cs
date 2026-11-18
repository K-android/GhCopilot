using System.Runtime.InteropServices;
using Eto.Forms;
using GhCopilot.Core;
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
            Content = new CopilotPanel();
        }
    }
}
