using System;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Llm;

namespace GhCopilot.Core.Host
{
    public enum ChatAction { Message, ExplainSelection, FixSelection, SaveRecipe }

    public interface IChatHostBridge
    {
        ChatSession Session { get; }
        event Action<int>? TokenEstimateChanged;
        event Action<string>? ProgressStageChanged;
        event EventHandler? PreviewStateChanged;
        Task<CopilotGenerationResponse> SendChatAsync(string prompt, ChatAction action = ChatAction.Message,
            bool useRhinoSelection = false, CancellationToken cancellationToken = default);
        void NewChat();
    }

    /// <summary>
    /// Optional headless execution capability for host bridges so automated callers (e.g. local IPC)
    /// can override Plan-First and viewport capture behavior without altering existing host contracts.
    /// </summary>
    public interface IHeadlessChatBridge
    {
        Task<CopilotGenerationResponse> SendHeadlessChatAsync(
            string prompt,
            bool skipPlanFirst = true,
            bool captureViewport = false,
            CancellationToken cancellationToken = default);
    }
}