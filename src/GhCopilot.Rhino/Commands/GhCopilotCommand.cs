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
            Panels.OpenPanel(typeof(CopilotDockPanel).GUID);
            RhinoApp.WriteLine(CopilotHostRegistry.GetDiagnosticReport());
            return Result.Success;
        }
    }
}
