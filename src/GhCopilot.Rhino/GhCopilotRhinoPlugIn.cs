using System;
using System.Drawing;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using GhCopilot.Core.Settings;
using GhCopilot.Rhino.Host;
using GhCopilot.Rhino.UI;
using Rhino.PlugIns;
using Rhino.UI;

namespace GhCopilot.Rhino
{
    /// <summary>
    /// Rhino 8 PlugIn host for GH Copilot. Registers the dockable Eto panel, commands, and optional headless pipe bridge.
    /// </summary>
    public sealed class GhCopilotRhinoPlugIn : PlugIn
    {
        private HeadlessBridgeServer? _headlessServer;

        public GhCopilotRhinoPlugIn()
        {
            Instance = this;
        }

        public static GhCopilotRhinoPlugIn? Instance { get; private set; }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            StartupDiagnostics.Write("Rhino plug-in OnLoad started.");
            try
            {
                StartupDiagnostics.WriteEnvironment(GetType().Assembly);
                try
                {
                    if (CopilotHostRegistry.RhinoBridge is null)
                    {
                        CopilotHostRegistry.RegisterRhinoBridge(new Host.RhinoHostBridge());
                    }
                }
                catch (Exception bridgeEx)
                {
                    StartupDiagnostics.Write("Rhino direct bridge initialization warning (panel will still register).", bridgeEx);
                }

                try
                {
                    if (AppSettings.Load().EnableHeadlessBridge)
                    {
                        _headlessServer = new HeadlessBridgeServer();
                        _headlessServer.Start();
                    }
                }
                catch (Exception headlessEx)
                {
                    StartupDiagnostics.Write("Headless bridge initialization warning (panel will still register).", headlessEx);
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

        protected override void OnShutdown()
        {
            try
            {
                _headlessServer?.Dispose();
                _headlessServer = null;
            }
            catch (Exception)
            {
                // Ignore shutdown cleanup errors.
            }

            base.OnShutdown();
        }
    }
}
