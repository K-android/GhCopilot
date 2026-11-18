using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("GhCopilot.Tests")]

namespace GhCopilot.Core
{
    /// <summary>
    /// Central metadata and fixed structural identifiers for GH Copilot host registration.
    /// </summary>
    public static class PluginConstants
    {
        public const string ProductName = "GH Copilot";
        public const string Version = "0.1.0";
        public const string Description = "AI copilot for Grasshopper and Rhino 8.";
        public const string RhinoCommandName = "GhCopilot";

        public const string RhinoPlugInId = "8A4E9C12-3B7D-4F6A-9E21-5C8D7A1B3E40";
        public const string GhAssemblyId = "B27F4D89-6C1E-4A3B-8D92-1F4E6C8A9B02";
        public const string DockPanelId = "D59A1E34-7F2B-4C8E-A163-9B0D2E4F6A18";
    }
}
