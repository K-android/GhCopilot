using System;
using System.Drawing;
using GhCopilot.Core;
using GhCopilot.Core.Host;
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
            StartupDiagnostics.Write("Rhino plug-in OnLoad started.");
            try
            {
                StartupDiagnostics.WriteEnvironment(GetType().Assembly);
                if (CopilotHostRegistry.RhinoBridge is null)
                {
                    CopilotHostRegistry.RegisterRhinoBridge(new Host.RhinoHostBridge());
                }
                Panels.RegisterPanel(
                    this,
                    typeof(CopilotDockPanel),
                    PluginConstants.ProductName,
                    SystemIcons.Application);
                StartupDiagnostics.Write("Rhino dock panel registration completed.");
                return LoadReturnCode.Success;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Rhino plug-in OnLoad failed.", ex);
                errorMessage = "GH Copilot could not initialize: " + ex.GetBaseException().Message
                    + Environment.NewLine + "Details: " + StartupDiagnostics.LogPath;
                return LoadReturnCode.ErrorShowDialog;
            }
        }
    }
}
