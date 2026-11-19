using System;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using GhCopilot.Rhino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace GhCopilot.Rhino.Commands
{
    /// <summary>
    /// Rhino command that opens the GH Copilot dockable panel and writes load diagnostics to the command history.
    /// </summary>
    public sealed class GhCopilotCommand : Command
    {
        public GhCopilotCommand()
        {
            Instance = this;
        }

        public static GhCopilotCommand? Instance { get; private set; }

        public override string EnglishName => PluginConstants.RhinoCommandName;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            StartupDiagnostics.Write("GhCopilot command: opening dock panel.");
            try
            {
                Panels.OpenPanel(typeof(CopilotDockPanel).GUID);
                // Opening a UI must not query a still-initializing Grasshopper component server.
                // The detailed host report remains available through the Grasshopper menu.
                RhinoApp.WriteLine("GH Copilot panel opened. Open Grasshopper before sending a request.");
                StartupDiagnostics.Write("GhCopilot command: dock panel open completed.");
                return Result.Success;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("GhCopilot command failed.", ex);
                RhinoApp.WriteLine("GH Copilot could not open: {0}", ex.GetBaseException().Message);
                RhinoApp.WriteLine("Startup details: {0}", StartupDiagnostics.LogPath);
                return Result.Failure;
            }
        }
    }
}
