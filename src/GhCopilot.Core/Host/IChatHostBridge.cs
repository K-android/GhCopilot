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
}