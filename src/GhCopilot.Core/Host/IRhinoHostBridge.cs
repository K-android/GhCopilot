using System;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Llm;

namespace GhCopilot.Core.Host
{
    /// <summary>
    /// Selects whether prompts execute directly on the active Rhino document,
    /// in Grasshopper, or automatically route based on prompt intent and workspace state.
    /// </summary>
    public enum CopilotWorkMode
    {
        Auto,
        RhinoDirect,
        Grasshopper
    }

    /// <summary>
    /// Contract implemented by the Rhino (.rhp) direct scene controller so users can create,
    /// transform, organize, and inspect Rhino 8 objects without opening Grasshopper.
    /// </summary>
    public interface IRhinoHostBridge
    {
        ChatSession Session { get; }
        bool CanUndoLastTurn { get; }
        bool HasActiveDocument { get; }
        event Action<int>? TokenEstimateChanged;
        event Action<string>? ProgressStageChanged;
        event EventHandler? RhinoStateChanged;

        Task<CopilotGenerationResponse> SendRhinoChatAsync(
            string prompt,
            bool explainOnly = false,
            CancellationToken cancellationToken = default);

        string UndoLastRhinoTurn();
        string SpawnRhinoDemo();
        void NewChat();
        string GetRhinoStatusSummary();
    }
}
