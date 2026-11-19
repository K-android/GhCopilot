using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GhCopilot.Core.Host
{
    /// <summary>
    /// Thread-safe in-process registry connecting the Rhino (.rhp) and Grasshopper (.gha) host plugins.
    /// </summary>
    public static class CopilotHostRegistry
    {
        private static readonly object SyncLock = new object();
        private static IGrasshopperHostBridge? _grasshopperBridge;

        /// <summary>
        /// Raised after the Grasshopper host bridge registers during PriorityLoad.
        /// </summary>
        public static event EventHandler? GrasshopperBridgeRegistered;

        /// <summary>
        /// Gets the registered Grasshopper host bridge, or null if Grasshopper has not loaded yet.
        /// </summary>
        public static IGrasshopperHostBridge? GrasshopperBridge
        {
            get
            {
                lock (SyncLock)
                {
                    return _grasshopperBridge;
                }
            }
        }

        /// <summary>
        /// Registers the Grasshopper host bridge instance created during PriorityLoad.
        /// </summary>
        public static void RegisterGrasshopperBridge(IGrasshopperHostBridge bridge)
        {
            if (bridge is null)
            {
                throw new ArgumentNullException(nameof(bridge));
            }

            lock (SyncLock)
            {
                _grasshopperBridge = bridge;
            }

            GrasshopperBridgeRegistered?.Invoke(null, EventArgs.Empty);
        }

        internal static void ResetForTests()
        {
            lock (SyncLock)
            {
                _grasshopperBridge = null;
            }
        }

        /// <summary>
        /// Builds a diagnostic status string for Phase 0 load verification.
        /// </summary>
        public static string GetDiagnosticReport()
        {
            IGrasshopperHostBridge? bridge = GrasshopperBridge;
            var sb = new StringBuilder();
            sb.AppendLine($"{PluginConstants.ProductName} v{PluginConstants.Version}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");

            if (bridge is null)
            {
                sb.AppendLine("Grasshopper bridge: Not registered (open Grasshopper to initialize .gha bridge)");
            }
            else
            {
                sb.AppendLine("Grasshopper bridge: Registered");
                sb.AppendLine($"Canvas created: {bridge.IsCanvasCreated}");
                sb.AppendLine($"Loaded proxies: {bridge.LoadedProxyCount}");
                sb.AppendLine($"Summary: {bridge.GetRuntimeStatusSummary()}");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
