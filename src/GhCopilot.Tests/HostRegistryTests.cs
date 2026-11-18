using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core;
using GhCopilot.Core.Host;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class HostRegistryTests : IDisposable
    {
        public HostRegistryTests()
        {
            CopilotHostRegistry.ResetForTests();
        }

        public void Dispose()
        {
            CopilotHostRegistry.ResetForTests();
        }

        [Fact]
        public void GrasshopperBridge_InitiallyNull_AndDiagnosticReportReflectsUnregisteredState()
        {
            Assert.Null(CopilotHostRegistry.GrasshopperBridge);

            string report = CopilotHostRegistry.GetDiagnosticReport();

            Assert.Contains($"{PluginConstants.ProductName} v{PluginConstants.Version}", report);
            Assert.Contains("Grasshopper bridge: Not registered", report);
        }

        [Fact]
        public void RegisterGrasshopperBridge_ThrowsOnNull()
        {
            Assert.Throws<ArgumentNullException>(() => CopilotHostRegistry.RegisterGrasshopperBridge(null!));
        }

        [Fact]
        public void RegisterGrasshopperBridge_StoresBridgeAndPopulatesDiagnosticReport()
        {
            var fake = new FakeGrasshopperBridge(
                isCanvasCreated: true,
                loadedProxyCount: 42,
                summary: "Test GH session ready");

            CopilotHostRegistry.RegisterGrasshopperBridge(fake);

            Assert.Same(fake, CopilotHostRegistry.GrasshopperBridge);

            string report = CopilotHostRegistry.GetDiagnosticReport();
            Assert.Contains("Grasshopper bridge: Registered", report);
            Assert.Contains("Canvas created: True", report);
            Assert.Contains("Loaded proxies: 42", report);
            Assert.Contains("Summary: Test GH session ready", report);
        }

        [Fact]
        public void CoreAssembly_HasZeroRhinoGrasshopperOrEtoReferences()
        {
            var referencedNames = typeof(CopilotHostRegistry).Assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .ToArray();

            Assert.DoesNotContain(referencedNames, name =>
                name.StartsWith("Rhino", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Grasshopper", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("GH_IO", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Eto", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void PluginConstants_GuidsAreValidAndDistinct()
        {
            var rhinoId = Guid.Parse(PluginConstants.RhinoPlugInId);
            var ghId = Guid.Parse(PluginConstants.GhAssemblyId);
            var panelId = Guid.Parse(PluginConstants.DockPanelId);

            Assert.NotEqual(Guid.Empty, rhinoId);
            Assert.NotEqual(Guid.Empty, ghId);
            Assert.NotEqual(Guid.Empty, panelId);
            Assert.NotEqual(rhinoId, ghId);
            Assert.NotEqual(rhinoId, panelId);
            Assert.NotEqual(ghId, panelId);
        }

        private sealed class FakeGrasshopperBridge : IGrasshopperHostBridge
        {
            private readonly string _summary;

            public FakeGrasshopperBridge(bool isCanvasCreated, int loadedProxyCount, string summary)
            {
                IsCanvasCreated = isCanvasCreated;
                LoadedProxyCount = loadedProxyCount;
                _summary = summary;
            }

            public bool IsCanvasCreated { get; }
            public int LoadedProxyCount { get; }
            public bool HasActivePreview { get; private set; }

            public string GetRuntimeStatusSummary() => _summary;

            public string SpawnDemoGraph()
            {
                HasActivePreview = true;
                return "Fake demo spawned";
            }

            public bool AcceptActivePreview()
            {
                if (!HasActivePreview)
                {
                    return false;
                }

                HasActivePreview = false;
                return true;
            }

            public bool RejectActivePreview()
            {
                if (!HasActivePreview)
                {
                    return false;
                }

                HasActivePreview = false;
                return true;
            }

            public string DumpIndexToJson() => "Fake dump written";

            public string ReloadCompanyLibrary() => "Fake reloaded";

            public string SaveSelectionAsRecipe(string recipeName, string description) => "Fake recipe saved";

            public void SaveApiKeyAndModel(string? apiKey, string? modelId)
            {
            }

            public string GetConfiguredModelId() => "gemini-3.6-flash";

            public Task<string> VerifyConfiguredModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult("Fake verified");

            public Task<string> GenerateGraphFromPromptAsync(string prompt, CancellationToken cancellationToken = default) =>
                Task.FromResult("Fake generated");

            public Task<CopilotGenerationResponse> RunPromptAsync(string prompt, CancellationToken cancellationToken = default) =>
                Task.FromResult(new CopilotGenerationResponse { IsSuccess = true, StatusLine = "Fake generated" });

            public Task<CopilotGenerationResponse> ExplainSelectionAsync(string? userHint = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(new CopilotGenerationResponse { IsSuccess = true, StatusLine = "Fake explained", Explanation = "Fake explanation" });

            public Task<CopilotGenerationResponse> FixSelectionAsync(string? userHint = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(new CopilotGenerationResponse { IsSuccess = true, StatusLine = "Fake fixed" });

            public void ShowModelessCopilotWindow()
            {
            }
        }
    }
}
