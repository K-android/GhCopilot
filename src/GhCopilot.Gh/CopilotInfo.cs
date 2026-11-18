using System;
using GhCopilot.Core;
using Grasshopper.Kernel;

namespace GhCopilot.Gh
{
    /// <summary>
    /// Grasshopper assembly metadata for GH Copilot.
    /// </summary>
    public sealed class CopilotInfo : GH_AssemblyInfo
    {
        public override string Name => PluginConstants.ProductName;

        public override string Description => PluginConstants.Description;

        public override Guid Id => new Guid(PluginConstants.GhAssemblyId);

        public override string AuthorName => "GH Copilot Team";

        public override string AuthorContact => string.Empty;

        public override string Version => PluginConstants.Version;
    }
}
