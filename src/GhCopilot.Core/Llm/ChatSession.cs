using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GhCopilot.Core.Llm
{
    public sealed class ChatMessage
    {
        public ChatMessage(string role, string content)
        {
            Role = role ?? throw new ArgumentNullException(nameof(role));
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        public string Role { get; }
        public string Content { get; }
    }

    /// <summary>
    /// Per-chat state. CompleteTurn commits a whole pair, never a partial/cancelled turn.
    /// Owned by the host; callers must serialize access with generation/preview operations.
    /// </summary>
    public sealed class ChatSession
    {
        public const int MaxRecentTurns = 6;
        public const int MaxSummaryCharacters = 2048;
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private readonly List<string> _summaryLines = new List<string>();
        private readonly Dictionary<Guid, string> _nodeIds = new Dictionary<Guid, string>();
        private readonly Dictionary<string, string> _anonymousNodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private int _nextNodeId = 1;
        private long _foldedTurns;

        public ChatSession()
        {
            Messages = _messages.AsReadOnly();
        }

        public IReadOnlyList<ChatMessage> Messages { get; }
        public string Summary { get; private set; } = string.Empty;
        public HashSet<Guid> SpawnedNodeIds { get; } = new HashSet<Guid>();
        public List<Guid> LastSpawnedNodeIds { get; } = new List<Guid>();
        /// <summary>Latest validator/runtime diagnostics, not the conversation transcript.</summary>
        public List<string> LastMessages { get; } = new List<string>();
        /// <summary>Latest proposed plan text awaiting user approval in Plan-First mode.</summary>
        public string PendingPlanSummary { get; set; } = string.Empty;
        /// <summary>Concise summary of the latest build or edit executed in this session.</summary>
        public string LastExecutedSummary { get; set; } = string.Empty;
        /// <summary>Latest viewport before/after verification summary.</summary>
        public string LastViewportVerification { get; set; } = string.Empty;
        /// <summary>True once at least one build/edit has been executed in the current session.</summary>
        public bool HasBuiltInCurrentSession { get; set; }

        public string GetNodeId(Guid instanceGuid)
        {
            if (!_nodeIds.TryGetValue(instanceGuid, out string? id))
            {
                id = NextNodeId();
                _nodeIds.Add(instanceGuid, id);
            }
            return id;
        }

        // Metadata-only snapshots may lack instance GUIDs. Keep distinct snapshot aliases
        // distinct without allocating fake GUIDs or colliding with real canvas node IDs.
        internal string GetSnapshotNodeId(Guid instanceGuid, string snapshotId)
        {
            if (instanceGuid != Guid.Empty) return GetNodeId(instanceGuid);
            if (!_anonymousNodeIds.TryGetValue(snapshotId, out string? id))
            {
                id = NextNodeId();
                _anonymousNodeIds.Add(snapshotId, id);
            }
            return id;
        }

        public void CompleteTurn(string user, string assistant)
        {
            if (user is null) throw new ArgumentNullException(nameof(user));
            if (assistant is null) throw new ArgumentNullException(nameof(assistant));
            _messages.Add(new ChatMessage("user", user));
            _messages.Add(new ChatMessage("assistant", assistant));

            while (_messages.Count > MaxRecentTurns * 2)
            {
                _foldedTurns++;
                _summaryLines.Add("user: " + Excerpt(_messages[0].Content)
                    + " | assistant: " + Excerpt(_messages[1].Content));
                _messages.RemoveRange(0, 2);
                string header = "LOSSY SUMMARY (" + _foldedTurns.ToString(CultureInfo.InvariantCulture)
                    + " older turns; excerpts only, earlier excerpts may be omitted):\n";
                while (header.Length + string.Join("\n", _summaryLines).Length > MaxSummaryCharacters)
                {
                    _summaryLines.RemoveAt(0);
                }
                Summary = header + string.Join("\n", _summaryLines);
            }
        }

        public void Clear()
        {
            _messages.Clear();
            _summaryLines.Clear();
            _nodeIds.Clear();
            _anonymousNodeIds.Clear();
            SpawnedNodeIds.Clear();
            LastSpawnedNodeIds.Clear();
            LastMessages.Clear();
            Summary = string.Empty;
            PendingPlanSummary = string.Empty;
            LastExecutedSummary = string.Empty;
            LastViewportVerification = string.Empty;
            HasBuiltInCurrentSession = false;
            _foldedTurns = 0;
            _nextNodeId = 1;
        }

        public void RecordPreviewDecision(bool accepted)
        {
            if (_messages.Count == 0) return;
            int last = _messages.Count - 1;
            ChatMessage message = _messages[last];
            _messages[last] = new ChatMessage(message.Role, message.Content + "\n[Preview decision: "
                + (accepted ? "accepted; changes kept" : "rejected; this turn's graph changes rolled back") + "]");
        }

        private string NextNodeId() => "n" + (_nextNodeId++).ToString(CultureInfo.InvariantCulture);

        private static string Excerpt(string text)
        {
            const int limit = 160;
            var result = new StringBuilder(limit + 3);
            bool whitespace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    whitespace = result.Length > 0;
                    continue;
                }
                if (result.Length + (whitespace ? 1 : 0) >= limit)
                {
                    result.Append("...");
                    break;
                }
                if (whitespace) result.Append(' ');
                result.Append(c);
                whitespace = false;
            }
            return result.ToString();
        }
    }
}