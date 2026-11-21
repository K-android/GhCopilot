using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using GhCopilot.Core.Settings;
using GhCopilot.Gh.Host;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;
using Rhino.UI;

namespace GhCopilot.Gh
{
    /// <summary>
    /// Grasshopper startup hook: registers the in-process bridge and adds the GH Copilot menu once the canvas is created.
    /// </summary>
    public sealed class CopilotPriority : GH_AssemblyPriority
    {
        private const string MenuItemKey = "GhCopilotRootMenu";
        private GrasshopperHostBridge? _bridge;
        private bool _menuAttached;

        public override GH_LoadingInstruction PriorityLoad()
        {
            StartupDiagnostics.Write("Grasshopper PriorityLoad: registering host bridge.");
            StartupDiagnostics.WriteEnvironment(typeof(CopilotPriority).Assembly);
            _bridge = new GrasshopperHostBridge();
            CopilotHostRegistry.RegisterGrasshopperBridge(_bridge);
            Instances.CanvasCreated += OnCanvasCreated;
            StartupDiagnostics.Write("Grasshopper PriorityLoad: host bridge registered.");
            return GH_LoadingInstruction.Proceed;
        }

        private void OnCanvasCreated(GH_Canvas canvas)
        {
            Instances.CanvasCreated -= OnCanvasCreated;
            RhinoApp.InvokeOnUiThread((Action)AttachMenuToEditor);
        }

        private void AttachMenuToEditor()
        {
            if (_menuAttached)
            {
                return;
            }

            // Initialize Tier-1 index (or load from disk cache) once plugins have finished loading.
            _bridge?.EnsureIndexLoaded();
            StartBackgroundModelCheck();

            GH_DocumentEditor? editor = Instances.DocumentEditor;
            MenuStrip? mainMenu = editor?.MainMenuStrip;
            if (mainMenu is null)
            {
                return;
            }

            if (mainMenu.Items.ContainsKey(MenuItemKey))
            {
                _menuAttached = true;
                return;
            }

            var rootMenu = new ToolStripMenuItem(PluginConstants.ProductName)
            {
                Name = MenuItemKey
            };

            var openPanelItem = new ToolStripMenuItem("Open Dockable Panel");
            openPanelItem.Click += (_, _) =>
            {
                Panels.OpenPanel(new Guid(PluginConstants.DockPanelId));
            };

            var openModelessWindowItem = new ToolStripMenuItem("Open Copilot Window (Modeless)");
            openModelessWindowItem.Click += (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                bridge?.ShowModelessCopilotWindow();
            };

            var spawnDemoItem = new ToolStripMenuItem("Spawn Phase 1 Demo (Slider -> Add -> Panel)");
            spawnDemoItem.Click += (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    Eto.Forms.MessageBox.Show(
                        "Grasshopper bridge is not initialized.",
                        PluginConstants.ProductName,
                        Eto.Forms.MessageBoxType.Warning);
                    return;
                }

                string status = bridge.SpawnDemoGraph();
                if (status.StartsWith("Spawn failed:", StringComparison.OrdinalIgnoreCase) ||
                    status.StartsWith("No active", StringComparison.OrdinalIgnoreCase))
                {
                    Eto.Forms.MessageBox.Show(
                        status,
                        $"{PluginConstants.ProductName} - Phase 1",
                        Eto.Forms.MessageBoxType.Warning);
                }
            };

            var acceptPreviewItem = new ToolStripMenuItem("Accept Preview");
            acceptPreviewItem.Click += (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null || !bridge.AcceptActivePreview())
                {
                    Eto.Forms.MessageBox.Show(
                        "No active preview group to accept.",
                        $"{PluginConstants.ProductName} - Phase 1",
                        Eto.Forms.MessageBoxType.Information);
                }
            };

            var rejectPreviewItem = new ToolStripMenuItem("Reject Preview");
            rejectPreviewItem.Click += (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null || !bridge.RejectActivePreview())
                {
                    Eto.Forms.MessageBox.Show(
                        "No active preview group to reject.",
                        $"{PluginConstants.ProductName} - Phase 1",
                        Eto.Forms.MessageBoxType.Information);
                }
            };

            var testGeminiItem = new ToolStripMenuItem("Phase 3: Test Prompt ('circle with a radius slider')");
            testGeminiItem.Click += async (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    Eto.Forms.MessageBox.Show(
                        "Grasshopper bridge is not initialized.",
                        PluginConstants.ProductName,
                        Eto.Forms.MessageBoxType.Warning);
                    return;
                }

                string result = await bridge.GenerateGraphFromPromptAsync("circle with a radius slider");
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    Eto.Forms.MessageBox.Show(
                        result,
                        $"{PluginConstants.ProductName} - Phase 3",
                        Eto.Forms.MessageBoxType.Information);
                });
            };

            var explainSelectionItem = new ToolStripMenuItem("Explain Selection");
            explainSelectionItem.Click += async (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    return;
                }

                CopilotGenerationResponse resp = await bridge.ExplainSelectionAsync();
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    string body = string.IsNullOrWhiteSpace(resp.Explanation) ? resp.StatusLine : $"{resp.StatusLine}\n\n{resp.Explanation}";
                    Eto.Forms.MessageBox.Show(
                        body,
                        $"{PluginConstants.ProductName} - Explain Selection",
                        Eto.Forms.MessageBoxType.Information);
                });
            };

            var fixSelectionItem = new ToolStripMenuItem("Fix Selection");
            fixSelectionItem.Click += async (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    return;
                }

                CopilotGenerationResponse resp = await bridge.FixSelectionAsync();
                Eto.Forms.Application.Instance.AsyncInvoke(() =>
                {
                    Eto.Forms.MessageBox.Show(
                        resp.StatusLine,
                        $"{PluginConstants.ProductName} - Fix Selection",
                        Eto.Forms.MessageBoxType.Information);
                });
            };

            var saveRecipeItem = new ToolStripMenuItem("Save Selection as Recipe");
            saveRecipeItem.Click += (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    return;
                }

                string name = $"Recipe_{DateTime.Now:yyyyMMdd_HHmmss}";
                string msg = bridge.SaveSelectionAsRecipe(name, "Saved from Grasshopper canvas selection");
                Eto.Forms.MessageBox.Show(
                    msg,
                    $"{PluginConstants.ProductName} - Save Recipe",
                    Eto.Forms.MessageBoxType.Information);
            };

            var dumpIndexItem = new ToolStripMenuItem("Debug: Dump Index to JSON");
            dumpIndexItem.Click += (_, _) =>
            {
                IGrasshopperHostBridge? bridge = _bridge ?? CopilotHostRegistry.GrasshopperBridge;
                if (bridge is null)
                {
                    Eto.Forms.MessageBox.Show(
                        "Grasshopper bridge is not initialized.",
                        PluginConstants.ProductName,
                        Eto.Forms.MessageBoxType.Warning);
                    return;
                }

                string summary = bridge.DumpIndexToJson();
                Eto.Forms.MessageBox.Show(
                    summary,
                    $"{PluginConstants.ProductName} - Index Dump",
                    Eto.Forms.MessageBoxType.Information);
            };

            var infoItem = new ToolStripMenuItem("Phase 0 Load Proof Info...");
            infoItem.Click += (_, _) =>
            {
                Eto.Forms.MessageBox.Show(
                    CopilotHostRegistry.GetDiagnosticReport(),
                    $"{PluginConstants.ProductName} - Phase 0",
                    Eto.Forms.MessageBoxType.Information);
            };

            rootMenu.DropDownItems.Add(openPanelItem);
            rootMenu.DropDownItems.Add(openModelessWindowItem);
            rootMenu.DropDownItems.Add(new ToolStripSeparator());
            rootMenu.DropDownItems.Add(spawnDemoItem);
            rootMenu.DropDownItems.Add(acceptPreviewItem);
            rootMenu.DropDownItems.Add(rejectPreviewItem);
            rootMenu.DropDownItems.Add(new ToolStripSeparator());
            rootMenu.DropDownItems.Add(testGeminiItem);
            rootMenu.DropDownItems.Add(explainSelectionItem);
            rootMenu.DropDownItems.Add(fixSelectionItem);
            rootMenu.DropDownItems.Add(saveRecipeItem);
            rootMenu.DropDownItems.Add(dumpIndexItem);
            rootMenu.DropDownItems.Add(infoItem);

            mainMenu.Items.Add(rootMenu);
            _menuAttached = true;
        }

        private void StartBackgroundModelCheck()
        {
            if (_bridge is null || !SecretStore.TryLoadApiKey(out _))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    string message = await _bridge.VerifyConfiguredModelAsync().ConfigureAwait(false);
                    if (message.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ||
                        message.StartsWith("Model check failed:", StringComparison.OrdinalIgnoreCase))
                    {
                        RhinoApp.InvokeOnUiThread((Action)(() => RhinoApp.WriteLine($"[{PluginConstants.ProductName}] {message}")));
                    }
                }
                catch (Exception)
                {
                    // Never surface background model check exceptions at startup.
                }
            });
        }
    }
}
