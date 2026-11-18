using System.Drawing;
using GhCopilot.Core;
using GhCopilot.Rhino.UI;
using Rhino.PlugIns;
using Rhino.UI;

namespace GhCopilot.Rhino
{
    /// <summary>
    /// Rhino 8 PlugIn host for GH Copilot. Registers the dockable Eto panel and Rhino commands.
    /// </summary>
    public sealed class GhCopilotRhinoPlugIn : PlugIn
    {
        public GhCopilotRhinoPlugIn()
        {
            Instance = this;
        }

        public static GhCopilotRhinoPlugIn? Instance { get; private set; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Panels.RegisterPanel(
                this,
                typeof(CopilotDockPanel),
                PluginConstants.ProductName,
                SystemIcons.Application);

            return LoadReturnCode.Success;
        }
    }
}
