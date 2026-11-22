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
        private static IRhinoHostBridge? _rhinoBridge;

        /// <summary>
        /// Raised after the Grasshopper host bridge registers during PriorityLoad.
        /// </summary>
        public static event EventHandler? GrasshopperBridgeRegistered;

        /// <summary>
        /// Raised after the Rhino direct host bridge registers.
        /// </summary>
        public static event EventHandler? RhinoBridgeRegistered;

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
        /// Gets the registered Rhino direct host bridge, or null if not registered yet.
        /// </summary>
        public static IRhinoHostBridge? RhinoBridge
        {
            get
            {
                lock (SyncLock)
                {
                    return _rhinoBridge;
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

        /// <summary>
        /// Registers the direct Rhino host bridge instance created during plug-in initialization.
        /// </summary>
        public static void RegisterRhinoBridge(IRhinoHostBridge bridge)
        {
            if (bridge is null)
            {
                throw new ArgumentNullException(nameof(bridge));
            }

            lock (SyncLock)
            {
                _rhinoBridge = bridge;
            }

            RhinoBridgeRegistered?.Invoke(null, EventArgs.Empty);
        }

        internal static void ResetForTests()
        {
            lock (SyncLock)
            {
                _grasshopperBridge = null;
                _rhinoBridge = null;
            }
        }

        /// <summary>
        /// Builds a diagnostic status string for Phase 0 load verification.
        /// </summary>
        public static string GetDiagnosticReport()
        {
            IGrasshopperHostBridge? bridge = GrasshopperBridge;
            IRhinoHostBridge? rhinoBridge = RhinoBridge;
            var sb = new StringBuilder();
            sb.AppendLine($"{PluginConstants.ProductName} v{PluginConstants.Version}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");

            if (rhinoBridge is null)
            {
                sb.AppendLine("Rhino Direct bridge: Not registered");
            }
            else
            {
                sb.AppendLine("Rhino Direct bridge: Registered");
                sb.AppendLine($"Rhino summary: {rhinoBridge.GetRhinoStatusSummary()}");
            }

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
