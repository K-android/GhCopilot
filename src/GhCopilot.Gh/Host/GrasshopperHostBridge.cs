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
    public sealed class GrasshopperHostBridge : IGrasshopperHostBridge
    {
        private readonly object _previewLock = new object();
        private readonly object _indexLock = new object();
        private readonly GeminiClient _geminiClient = new GeminiClient();
        private GroupPreview? _activePreview;
        private IndexStore? _indexStore;

        public bool IsCanvasCreated => Instances.ActiveCanvas != null;

        public int LoadedProxyCount => Instances.ComponentServer?.ObjectProxies?.Count ?? 0;

        public bool HasActivePreview
        {
            get
            {
                lock (_previewLock)
                {
                    return _activePreview != null && _activePreview.IsActive;
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
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return FailureResponse("Enter a prompt first.");
            }

            if (!TryLoadConfig(out AppSettings settings, out string apiKey, out string? configError))
            {
                return FailureResponse(configError!);
            }

            IndexStore store = EnsureIndexLoaded();
            IReadOnlyList<ComponentRecord> shortlist = Retriever.Retrieve(store.AllComponents, prompt, 60);
            if (shortlist.Count == 0)
            {
                return FailureResponse("Component index is empty; cannot generate graph.");
            }

            ParamHydrator.HydrateShortlist(shortlist);

            // Check if any objects are currently selected on the canvas so the model can auto-wire from $selected / $s1.
            CanvasSnapshot? selectedContext = null;
            if (settings.SendCanvasData)
            {
                selectedContext = await UiThread.MutateActiveDocumentAsync(
                    doc => CanvasSerializer.SerializeSelection(doc, store, includeNonSelectedNeighbors: false)).ConfigureAwait(false);
                if (selectedContext.SelectedInstanceGuids.Count == 0)
                {
                    selectedContext = null;
                }
            }

            List<RecipeRecord> allRecipes = RecipeStore.LoadAll(sharedLibraryFolder: settings.SharedLibraryFolder);
            IReadOnlyList<RecipeRecord> matchingRecipes = RecipeStore.RetrieveMatchingRecipes(allRecipes, prompt, maxRecipes: 2);

            TimeSpan timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
            string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;

            RepairLoopResult loopResult = await RepairLoop.GenerateAndValidateAsync(
                _geminiClient,
                apiKey,
                settings.ModelId,
                prompt,
                shortlist,
                timeout: timeout,
                cancellationToken: cancellationToken,
                sendCanvasDataEnabled: settings.SendCanvasData,
                provider: settings.Provider,
                customEndpointUrl: endpoint,
                matchingRecipes: matchingRecipes,
                selectedContext: selectedContext,
                autoLayout: settings.AutoLayout).ConfigureAwait(false);

            if (!loopResult.IsSuccess || loopResult.Spec is null)
            {
                return new CopilotGenerationResponse
                {
                    IsSuccess = false,
                    AttemptsUsed = loopResult.AttemptsUsed,
                    StatusLine = $"Generation failed after {loopResult.AttemptsUsed} attempt(s): {string.Join("; ", loopResult.Errors)}",
                    RuntimeMessages = loopResult.Errors.ToList()
                };
            }

            return await SpawnWithOptionalRuntimeRepairAsync(
                loopResult,
                prompt,
                shortlist,
                store,
                apiKey,
                settings,
                replacedOriginalGuids: null,
                selectionSnapshot: selectedContext,
                preserveExternalWiresOnFix: false,
                actionLabel: "Spawned preview",
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<CopilotGenerationResponse> ExplainSelectionAsync(
            string? userHint = null,
            CancellationToken cancellationToken = default)
        {
            if (!TryLoadConfig(out AppSettings settings, out string apiKey, out string? configError))
            {
                return FailureResponse(configError!);
            }

            IndexStore store = EnsureIndexLoaded();
            CanvasSnapshot snapshot = await UiThread.MutateActiveDocumentAsync(
                doc => CanvasSerializer.SerializeSelection(doc, store, settings.SendCanvasData)).ConfigureAwait(false);

            if (snapshot.SelectedInstanceGuids.Count == 0)
            {
                return FailureResponse("Select one or more objects on the Grasshopper canvas first.");
            }

            string systemInstruction = PromptBuilder.BuildExplainSystemInstruction();
            string promptText = PromptBuilder.BuildExplainSelectionPrompt(snapshot, userHint, settings.SendCanvasData);

            PayloadAuditLogger.LogSentPayload(
                "ExplainSelection",
                $"{settings.Provider}:{settings.ModelId}",
                settings.SendCanvasData,
                systemInstruction,
                promptText);

            try
            {
                string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;
                string explanation = await _geminiClient.GenerateTextAsync(
                    apiKey,
                    settings.ModelId,
                    systemInstruction,
                    promptText,
                    TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                    cancellationToken,
                    settings.Provider,
                    endpoint).ConfigureAwait(false);

                List<string> selectedRuntimeMessages = snapshot.Nodes
                    .Where(n => n.IsSelected)
                    .SelectMany(n => n.RuntimeMessages.Select(m => $"[{m.Level}] {n.Name}: {m.Message}"))
                    .ToList();

                return new CopilotGenerationResponse
                {
                    IsSuccess = true,
                    NodeCount = snapshot.SelectedInstanceGuids.Count,
                    StatusLine = $"Explained {snapshot.SelectedInstanceGuids.Count} selected object(s).",
                    Explanation = explanation,
                    RuntimeMessages = selectedRuntimeMessages
                };
            }
            catch (GeminiApiException ex)
            {
                return FailureResponse($"Explain failed: {ex.Message}");
            }
        }

        public async Task<CopilotGenerationResponse> FixSelectionAsync(
            string? userHint = null,
            CancellationToken cancellationToken = default)
        {
            if (!TryLoadConfig(out AppSettings settings, out string apiKey, out string? configError))
            {
                return FailureResponse(configError!);
            }

            IndexStore store = EnsureIndexLoaded();
            CanvasSnapshot snapshot = await UiThread.MutateActiveDocumentAsync(
                doc => CanvasSerializer.SerializeSelection(doc, store, settings.SendCanvasData)).ConfigureAwait(false);

            if (snapshot.SelectedInstanceGuids.Count == 0)
            {
                return FailureResponse("Select one or more broken objects on the Grasshopper canvas first.");
            }

            IReadOnlyList<ComponentRecord> shortlist = BuildShortlistForFix(store, snapshot, userHint);
            if (shortlist.Count == 0)
            {
                return FailureResponse("Component index is empty; cannot fix selection.");
            }

            ParamHydrator.HydrateShortlist(shortlist);

            TimeSpan timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
            string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;

            RepairLoopResult loopResult = await RepairLoop.FixSelectionAndValidateAsync(
                _geminiClient,
                apiKey,
                settings.ModelId,
                snapshot,
                userHint,
                settings.SendCanvasData,
                shortlist,
                timeout: timeout,
                cancellationToken: cancellationToken,
                provider: settings.Provider,
                customEndpointUrl: endpoint,
                autoLayout: settings.AutoLayout).ConfigureAwait(false);

            if (!loopResult.IsSuccess || loopResult.Spec is null)
            {
                return new CopilotGenerationResponse
                {
                    IsSuccess = false,
                    AttemptsUsed = loopResult.AttemptsUsed,
                    StatusLine = $"Fix failed after {loopResult.AttemptsUsed} attempt(s): {string.Join("; ", loopResult.Errors)}",
                    RuntimeMessages = loopResult.Errors.ToList()
                };
            }

            string logicalPrompt = string.IsNullOrWhiteSpace(userHint)
                ? "Fix selected Grasshopper components"
                : userHint!.Trim();

            return await SpawnWithOptionalRuntimeRepairAsync(
                loopResult,
                logicalPrompt,
                shortlist,
                store,
                apiKey,
                settings,
                replacedOriginalGuids: snapshot.SelectedInstanceGuids,
                selectionSnapshot: snapshot,
                preserveExternalWiresOnFix: true,
                actionLabel: "Spawned replacement preview (originals & external wires preserved until Accept)",
                cancellationToken).ConfigureAwait(false);
        }

        public string SpawnDemoGraph()
        {
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
            bool accepted = false;

            UiThread.MutateActiveDocument(doc =>
            {
                lock (_previewLock)
                {
                    if (_activePreview != null && _activePreview.IsActive)
                    {
                        accepted = _activePreview.Accept(doc);
                        if (accepted)
                        {
                            _activePreview = null;
                        }
                    }
                }
            });

            return accepted;
        }

        public bool RejectActivePreview()
        {
            bool rejected = false;

            UiThread.MutateActiveDocument(doc =>
            {
                lock (_previewLock)
                {
                    if (_activePreview != null && _activePreview.IsActive)
                    {
                        rejected = _activePreview.Reject(doc);
                        if (rejected)
                        {
                            _activePreview = null;
                        }
                    }
                }
            });

            return rejected;
        }

        private async Task<CopilotGenerationResponse> SpawnWithOptionalRuntimeRepairAsync(
            RepairLoopResult loopResult,
            string logicalPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            IndexStore store,
            string apiKey,
            AppSettings settings,
            IReadOnlyList<Guid>? replacedOriginalGuids,
            CanvasSnapshot? selectionSnapshot,
            bool preserveExternalWiresOnFix,
            string actionLabel,
            CancellationToken cancellationToken)
        {
            GraphSpec finalSpec = loopResult.Spec!;
            List<string> validatorWarnings = loopResult.Validation?.Warnings.Select(w => w.Message).ToList() ?? new List<string>();
            string? endpoint = string.IsNullOrWhiteSpace(settings.CustomEndpointUrl) ? null : settings.CustomEndpointUrl;

            (List<string> spawnWarnings, List<string> runtimeMessages) =
                await SpawnOnCanvasAsync(
                    finalSpec,
                    store,
                    replacedOriginalGuids,
                    selectionSnapshot,
                    preserveExternalWiresOnFix).ConfigureAwait(false);

            bool ranRuntimeRepair = false;
            if (runtimeMessages.Count > 0)
            {
                ranRuntimeRepair = true;
                RepairLoopResult runtimeRepair = await RepairLoop.RepairRuntimeMessagesAsync(
                    _geminiClient,
                    apiKey,
                    settings.ModelId,
                    logicalPrompt,
                    shortlist,
                    finalSpec,
                    runtimeMessages,
                    timeout: TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                    cancellationToken: cancellationToken,
                    sendCanvasDataEnabled: settings.SendCanvasData,
                    provider: settings.Provider,
                    customEndpointUrl: endpoint,
                    selectedContext: preserveExternalWiresOnFix ? null : selectionSnapshot,
                    autoLayout: settings.AutoLayout).ConfigureAwait(false);

                if (runtimeRepair.IsSuccess && runtimeRepair.Spec != null)
                {
                    finalSpec = runtimeRepair.Spec;
                    validatorWarnings = runtimeRepair.Validation?.Warnings.Select(w => w.Message).ToList() ?? new List<string>();
                    (spawnWarnings, runtimeMessages) =
                        await SpawnOnCanvasAsync(
                            finalSpec,
                            store,
                            replacedOriginalGuids,
                            selectionSnapshot,
                            preserveExternalWiresOnFix).ConfigureAwait(false);
                }
            }

            var allRuntimeMessages = new List<string>(spawnWarnings.Count + runtimeMessages.Count);
            foreach (string sw in spawnWarnings)
            {
                allRuntimeMessages.Add($"[Wire] {sw}");
            }
            allRuntimeMessages.AddRange(runtimeMessages);

            int spawnedCount = finalSpec.Nodes.Count + (finalSpec.Script != null && !string.IsNullOrWhiteSpace(finalSpec.Script.Code) ? 1 : 0);

            return new CopilotGenerationResponse
            {
                IsSuccess = true,
                NodeCount = spawnedCount,
                WireCount = finalSpec.Wires.Count,
                AttemptsUsed = loopResult.AttemptsUsed,
                Explanation = finalSpec.Explanation ?? string.Empty,
                ValidatorWarnings = validatorWarnings,
                RuntimeMessages = allRuntimeMessages,
                StatusLine = $"{actionLabel} ({spawnedCount} node(s), {finalSpec.Wires.Count} wire(s)) in {loopResult.AttemptsUsed} attempt(s){(ranRuntimeRepair ? " + 1 runtime auto-fix pass" : string.Empty)}."
            };
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

        private Task<(List<string> SpawnWarnings, List<string> RuntimeMessages)> SpawnOnCanvasAsync(
            GraphSpec spec,
            IndexStore store,
            IReadOnlyList<Guid>? replacedOriginalGuids = null,
            CanvasSnapshot? selectionSnapshot = null,
            bool preserveExternalWiresOnFix = false)
        {
            return UiThread.MutateActiveDocumentAsync(doc =>
            {
                RejectExistingPreviewIfActive(doc);

                GroupPreview preview = CanvasSpawner.Spawn(
                    doc,
                    spec,
                    shortRef =>
                    {
                        ComponentRecord? record = store.GetByShortId(shortRef);
                        if (record is null || record.ComponentGuid == Guid.Empty)
                        {
                            return null;
                        }

                        return Instances.ComponentServer?.EmitObjectProxy(record.ComponentGuid)
                               ?? UserObjectFolderLoader.TryGetCompanyProxy(record.ComponentGuid);
                    },
                    out List<string> spawnWarnings,
                    replacedOriginalGuids,
                    selectionSnapshot,
                    preserveExternalWiresOnFix);

                lock (_previewLock)
                {
                    _activePreview = preview;
                }

                List<string> runtimeMessages = CanvasSpawner.CollectRuntimeMessages(doc, preview);
                return (spawnWarnings, runtimeMessages);
            });
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
