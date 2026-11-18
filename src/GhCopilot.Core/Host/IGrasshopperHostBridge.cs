using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GhCopilot.Core.Host
{
    /// <summary>
    /// Structured result returned to the UI after running retrieval, Gemini generation, validation, and preview spawn.
    /// </summary>
    public sealed class CopilotGenerationResponse
    {
        public bool IsSuccess { get; set; }
        public string StatusLine { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
        public List<string> ValidatorWarnings { get; set; } = new List<string>();
        public List<string> RuntimeMessages { get; set; } = new List<string>();
        public int NodeCount { get; set; }
        public int WireCount { get; set; }
        public int AttemptsUsed { get; set; }
    }

    /// <summary>
    /// Contract implemented by the Grasshopper (.gha) host adapter and consumed by the Rhino (.rhp) and GH UIs.
    /// </summary>
    public interface IGrasshopperHostBridge
    {
        /// <summary>
        /// Gets a value indicating whether at least one Grasshopper canvas has been created in this session.
        /// </summary>
        bool IsCanvasCreated { get; }

        /// <summary>
        /// Gets the number of component/object proxies currently loaded in Grasshopper's ComponentServer.
        /// </summary>
        int LoadedProxyCount { get; }

        /// <summary>
        /// Gets a value indicating whether a preview group is currently active and awaiting Accept or Reject.
        /// </summary>
        bool HasActivePreview { get; }

        /// <summary>
        /// Returns a human-readable summary of the active Grasshopper runtime state.
        /// </summary>
        string GetRuntimeStatusSummary();

        /// <summary>
        /// Spawns the hardcoded Phase 1 demonstration graph into a preview group on the active Grasshopper canvas.
        /// </summary>
        string SpawnDemoGraph();

        /// <summary>
        /// Accepts the active preview group (keeps spawned objects and recolours the group).
        /// </summary>
        bool AcceptActivePreview();

        /// <summary>
        /// Rejects the active preview group (removes all spawned objects and the group in a single undo step).
        /// </summary>
        bool RejectActivePreview();

        /// <summary>
        /// Ensures the component index is loaded/built and dumps it to a local JSON file for inspection.
        /// </summary>
        string DumpIndexToJson();

        /// <summary>
        /// Forces a rescan of the configured shared company library folder and rebuilds the index cache if needed.
        /// </summary>
        string ReloadCompanyLibrary();

        /// <summary>
        /// Saves the currently selected Grasshopper canvas objects as a reusable few-shot recipe.
        /// </summary>
        string SaveSelectionAsRecipe(string recipeName, string description);

        /// <summary>
        /// Saves optional API key (DPAPI-encrypted) and Gemini model ID to local settings.
        /// </summary>
        void SaveApiKeyAndModel(string? apiKey, string? modelId);

        /// <summary>
        /// Returns the configured Gemini model ID.
        /// </summary>
        string GetConfiguredModelId();

        /// <summary>
        /// Checks via Gemini ListModels whether the configured model ID is available.
        /// </summary>
        Task<string> VerifyConfiguredModelAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs the end-to-end retrieval, Gemini generation, validation/repair loop, and preview spawn for a prompt.
        /// </summary>
        Task<string> GenerateGraphFromPromptAsync(string prompt, CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs prompt generation and returns structured explanation, warnings, and status details for the UI.
        /// </summary>
        Task<CopilotGenerationResponse> RunPromptAsync(string prompt, CancellationToken cancellationToken = default);

        /// <summary>
        /// Serializes selected canvas objects and asks Gemini to explain how they work (including any runtime messages).
        /// </summary>
        Task<CopilotGenerationResponse> ExplainSelectionAsync(string? userHint = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Serializes selected canvas objects and runtime errors, generates a replacement GraphSpec, and previews it without deleting originals until Accept.
        /// </summary>
        Task<CopilotGenerationResponse> FixSelectionAsync(string? userHint = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Opens or focuses the modeless Eto Form window owned by Rhino/Grasshopper.
        /// </summary>
        void ShowModelessCopilotWindow();
    }
}
