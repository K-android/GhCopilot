using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Settings;
using GhCopilot.Gh.Canvas;
using GhCopilot.Gh.Index;
using GhCopilot.UI;
using Grasshopper;
using Grasshopper.Kernel;

namespace GhCopilot.Gh.Host
{
    /// <summary>
    /// Exposes live Grasshopper host state, preview actions, index management, recipes,
    /// multi-provider BYOK generation, and canvas awareness.
    /// </summary>
    public sealed partial class GrasshopperHostBridge : IGrasshopperHostBridge, IChatHostBridge
    {
        private readonly object _previewLock = new object();
        private readonly object _indexLock = new object();
        private readonly GeminiClient _geminiClient = new GeminiClient();
        private GroupPreview? _activePreview;
        private IndexStore? _indexStore;
        public event EventHandler? PreviewStateChanged;

        public bool IsCanvasCreated => Instances.ActiveCanvas != null;

        public int LoadedProxyCount => Instances.ComponentServer?.ObjectProxies?.Count ?? 0;

        public bool HasActivePreview
        {
            get
            {
                lock (_previewLock)
                {
                    return _chatPreview != null || (_activePreview != null && _activePreview.IsActive);
                }
            }
        }

        public string GetRuntimeStatusSummary()
        {
            string indexStatus;
            lock (_indexLock)
            {
                indexStatus = _indexStore is null
                    ? "Index=Not loaded"
                    : $"Index={_indexStore.AllComponents.Count} (FromCache={_indexStore.LoadedFromCache})";
            }

            AppSettings settings = AppSettings.Load();
            return $"Grasshopper ActiveCanvas={(IsCanvasCreated ? "Ready" : "Not open")}, Proxies={LoadedProxyCount}, {indexStatus}, Provider={settings.Provider}, Model={settings.ModelId}, ActivePreview={HasActivePreview}";
        }

        public IndexStore EnsureIndexLoaded()
        {
            lock (_indexLock)
            {
                if (_indexStore != null)
                {
                    return _indexStore;
                }

                string cacheKey = ProxyScanner.BuildCurrentSessionCacheKey();
                _indexStore = IndexStore.LoadOrBuild(
                    IndexStore.GetDefaultCacheFilePath(),
                    cacheKey,
                    ProxyScanner.ScanCurrentSession);

                return _indexStore;
            }
        }

        public string DumpIndexToJson()
        {
            IndexStore store = EnsureIndexLoaded();
            IReadOnlyList<ComponentRecord> sampleShortlist = Retriever.Retrieve(
                store.AllComponents,
                "slider addition panel circle line point extrude",
                40);
            ParamHydrator.HydrateShortlist(sampleShortlist);

            string dumpPath = IndexStore.GetDefaultDumpFilePath();
            store.SaveToFile(dumpPath);

            int nativeCount = store.AllComponents.Count(c => string.Equals(c.Source, "native", StringComparison.OrdinalIgnoreCase));
            int pluginCount = store.AllComponents.Count(c => string.Equals(c.Source, "plugin", StringComparison.OrdinalIgnoreCase));
            int userObjectCount = store.AllComponents.Count(c =>
                string.Equals(c.Source, "userobject", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Source, "cluster", StringComparison.OrdinalIgnoreCase));

            return $"Dumped {store.AllComponents.Count} components (native={nativeCount}, plugin={pluginCount}, userobject/cluster={userObjectCount}, loadedFromCache={store.LoadedFromCache}) to:\n{dumpPath}";
        }

        public string ReloadCompanyLibrary()
        {
            lock (_indexLock)
            {
                _indexStore = null;
            }

            IndexStore store = EnsureIndexLoaded();
            CompanyLibraryScanResult scan = UserObjectFolderLoader.LastScanResult;
            int companyCount = store.AllComponents.Count(c => c.IsCompany);

            if (!scan.IsReachable)
            {
                return $"Company library warning: {string.Join("; ", scan.Warnings)}";
            }

            return scan.Warnings.Count > 0
                ? $"Loaded {companyCount} company item(s) ({store.AllComponents.Count} total). Warnings: {string.Join("; ", scan.Warnings)}"
                : $"Loaded {companyCount} company item(s) ({store.AllComponents.Count} total).";
        }

        public string SaveSelectionAsRecipe(string recipeName, string description)
        {
            if (string.IsNullOrWhiteSpace(recipeName))
            {
                return "Enter a recipe name or description in the Prompt box first.";
            }

            IndexStore store = EnsureIndexLoaded();
            string status = "Scheduled recipe capture on Grasshopper UI thread.";

            UiThread.MutateActiveDocument(
                doc =>
                {
                    CanvasSnapshot snapshot = CanvasSerializer.SerializeSelection(doc, store, includeNonSelectedNeighbors: false);
                    if (snapshot.SelectedInstanceGuids.Count == 0)
                    {
                        status = "Select at least one component on the Grasshopper canvas to save as a recipe.";
                        return;
                    }

                    RecipeRecord recipe = RecipeStore.CreateFromCanvasSnapshot(recipeName, description, snapshot);
                    if (recipe.Spec.Nodes.Count == 0)
                    {
                        status = "None of the selected components could be mapped to indexed proxies.";
                        return;
                    }

                    RecipeStore.SaveRecipe(recipe);
                    status = $"Saved recipe '{recipe.Name}' ({recipe.Spec.Nodes.Count} node(s), {recipe.Spec.Wires.Count} wire(s)) to {RecipeStore.GetDefaultRecipesPath()}.";
                },
                err => status = err);

            return status;
        }

        public void SaveApiKeyAndModel(string? apiKey, string? modelId)
        {
            AppSettings settings = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                settings.ModelId = modelId!.Trim();
            }
            settings.Save();

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                SecretStore.SaveApiKey(apiKey!.Trim());
            }
        }

        public string GetConfiguredModelId()
        {
            return AppSettings.Load().ModelId;
        }

        public async Task<string> VerifyConfiguredModelAsync(CancellationToken cancellationToken = default)
        {
            AppSettings settings = AppSettings.Load();
            if (!SecretStore.TryLoadApiKey(out string? apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                return $"Model check skipped: no API key saved (or {SecretStore.EnvVarName} not set).";
            }

            string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;
            (_, string message) = await _geminiClient
                .VerifyConfiguredModelAsync(apiKey!, settings.ModelId, cancellationToken, settings.Provider, endpoint)
                .ConfigureAwait(false);

            return message;
        }

        public void ShowModelessCopilotWindow()
        {
            UiThread.InvokeOnUi(CopilotPanel.ShowModelessForm);
        }

        public async Task<string> GenerateGraphFromPromptAsync(string prompt, CancellationToken cancellationToken = default)
        {
            CopilotGenerationResponse response = await RunPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return response.StatusLine;
            }

            var sb = new StringBuilder();
            sb.AppendLine(response.StatusLine);
            if (!string.IsNullOrWhiteSpace(response.Explanation))
            {
                sb.AppendLine($"Explanation: {response.Explanation}");
            }

            if (response.ValidatorWarnings.Count > 0)
            {
                sb.AppendLine($"Validator warnings ({response.ValidatorWarnings.Count}): {string.Join("; ", response.ValidatorWarnings)}");
            }

            if (response.RuntimeMessages.Count > 0)
            {
                sb.AppendLine($"Remaining runtime messages:\n- {string.Join("\n- ", response.RuntimeMessages)}");
            }

            return sb.ToString().TrimEnd();
        }

        public async Task<CopilotGenerationResponse> RunPromptAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return await SendChatAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<CopilotGenerationResponse> ExplainSelectionAsync(
            string? userHint = null,
            CancellationToken cancellationToken = default)
        {
            return await SendChatAsync(userHint ?? string.Empty, ChatAction.ExplainSelection, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<CopilotGenerationResponse> FixSelectionAsync(
            string? userHint = null,
            CancellationToken cancellationToken = default)
        {
            return await SendChatAsync(userHint ?? string.Empty, ChatAction.FixSelection, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public string SpawnDemoGraph()
        {
            if (HasActivePreview || _chatGate.CurrentCount == 0)
            {
                return "Finish the pending turn first.";
            }

            string result = "Scheduled Phase 1 demo spawn on Grasshopper UI thread.";

            UiThread.MutateActiveDocument(
                doc =>
                {
                    try
                    {
                        RejectExistingPreviewIfActive(doc);

                        GraphSpec demo = CanvasSpawner.CreatePhase1DemoSpec();
                        GroupPreview preview = CanvasSpawner.Spawn(
                            doc,
                            demo,
                            CanvasSpawner.ResolveDemoProxy,
                            out List<string> warnings);

                        lock (_previewLock)
                        {
                            _activePreview = preview;
                        }

                        result = warnings.Count == 0
                            ? $"Spawned Phase 1 demo ({demo.Nodes.Count} nodes, {demo.Wires.Count} wires) inside preview group."
                            : $"Spawned Phase 1 demo with {warnings.Count} warning(s): {string.Join("; ", warnings)}";
                        OnPreviewStateChanged();
                    }
                    catch (Exception ex)
                    {
                        result = $"Spawn failed: {ex.Message}";
                    }
                },
                err => result = err);

            return result;
        }

        public bool AcceptActivePreview()
        {
            if (_chatGate.CurrentCount == 0)
            {
                return false;
            }

            bool accepted = false;

            UiThread.MutateActiveDocument(doc =>
            {
                lock (_previewLock)
                {
                    if (_chatPreview != null)
                    {
                        if (!ReferenceEquals(doc, _chatPreview.Document)) return;
                        _chatPreview.Accept();
                        _chatPreview = null;
                        Session.RecordPreviewDecision(true);
                        accepted = true;
                        OnPreviewStateChanged();
                        return;
                    }
                    if (_activePreview != null && _activePreview.IsActive)
                    {
                        accepted = _activePreview.Accept(doc);
                        if (accepted)
                        {
                            _activePreview = null;
                            OnPreviewStateChanged();
                        }
                    }
                }
            });

            return accepted;
        }

        public bool RejectActivePreview()
        {
            if (_chatGate.CurrentCount == 0)
            {
                return false;
            }

            bool rejected = false;

            UiThread.MutateActiveDocument(doc =>
            {
                lock (_previewLock)
                {
                    if (_chatPreview != null)
                    {
                        if (!ReferenceEquals(doc, _chatPreview.Document)) return;
                        _chatPreview.Reject();
                        _chatPreview = null;
                        Session.LastSpawnedNodeIds.Clear();
                        Session.LastSpawnedNodeIds.AddRange(_previousChatScope);
                        Session.SpawnedNodeIds.RemoveWhere(id => doc.FindObject(id, false) == null);
                        Session.RecordPreviewDecision(false);
                        rejected = true;
                        OnPreviewStateChanged();
                        return;
                    }
                    if (_activePreview != null && _activePreview.IsActive)
                    {
                        rejected = _activePreview.Reject(doc);
                        if (rejected)
                        {
                            _activePreview = null;
                            OnPreviewStateChanged();
                        }
                    }
                }
            });

            return rejected;
        }

        private void OnPreviewStateChanged()
        {
            PreviewStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static IReadOnlyList<ComponentRecord> BuildShortlistForFix(
            IndexStore store,
            CanvasSnapshot snapshot,
            string? userHint)
        {
            var result = new List<ComponentRecord>(60);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CanvasNodeSnapshot node in snapshot.Nodes)
            {
                ComponentRecord? record = !string.IsNullOrWhiteSpace(node.ShortId)
                    ? store.GetByShortId(node.ShortId)
                    : store.GetByGuid(node.ComponentGuid);

                if (record != null && seen.Add(record.ShortId))
                {
                    result.Add(record);
                }
            }

            string retrievalQuery = string.Join(" ", new[]
            {
                userHint ?? string.Empty,
                string.Join(" ", snapshot.Nodes.Select(n => n.Name)),
                string.Join(" ", snapshot.Nodes.SelectMany(n => n.RuntimeMessages.Select(m => m.Message)))
            });

            foreach (ComponentRecord candidate in Retriever.Retrieve(store.AllComponents, retrievalQuery, 60))
            {
                if (result.Count >= 60)
                {
                    break;
                }

                if (seen.Add(candidate.ShortId))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }

        private static bool TryLoadConfig(out AppSettings settings, out string apiKey, out string? error)
        {
            settings = AppSettings.Load();
            if (!SecretStore.TryLoadApiKey(out string? loadedKey) || string.IsNullOrWhiteSpace(loadedKey))
            {
                apiKey = string.Empty;
                error = $"No API key configured for {settings.Provider}. Save a key in Settings or set {SecretStore.EnvVarName}.";
                return false;
            }

            apiKey = loadedKey!;
            error = null;
            return true;
        }

        private static CopilotGenerationResponse FailureResponse(string statusLine)
        {
            return new CopilotGenerationResponse
            {
                IsSuccess = false,
                StatusLine = statusLine
            };
        }

        private void RejectExistingPreviewIfActive(GH_Document doc)
        {
            lock (_previewLock)
            {
                if (_activePreview != null && _activePreview.IsActive)
                {
                    _activePreview.Reject(doc);
                    _activePreview = null;
                }
            }
        }
    }
}
