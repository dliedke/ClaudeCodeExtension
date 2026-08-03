/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Encapsulates state for a single native-mode chat session
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Bundles all session-specific state: agent process, transcript UI, streaming buffers, turn state,
    /// and settings snapshot. Enables independent sessions to run in parallel.
    /// </summary>
    public class NativeChatSessionState
    {
        public NativeChatSessionState(string sessionId, IAgentSession agentSession, ChatTranscriptView transcript, int windowId = 0)
        {
            SessionId = sessionId;
            AgentSession = agentSession;
            ChatTranscript = transcript;
            WindowId = windowId;
            PendingToolCalls = new Dictionary<string, ChatMessageViewModel>(StringComparer.Ordinal);
            CodexPromptQueue = new Queue<string>();
            AttachedFiles = new List<string>();
            SessionCts = new CancellationTokenSource();
        }

        public string SessionId { get; }
        public int WindowId { get; }
        public IAgentSession AgentSession { get; set; }
        public ChatTranscriptView ChatTranscript { get; set; }

        /// <summary>The document tab hosting this session, so its caption can be kept in sync.</summary>
        public NativeChatToolWindow Window { get; set; }

        /// <summary>
        /// Files staged in this tab's composer (📎, drag &amp; drop, pasted images). Per-session, so an
        /// attachment made in one chat is never sent by another; the panel keeps its own global list.
        /// </summary>
        public List<string> AttachedFiles { get; }

        // Streaming state (per-turn)
        public ChatMessageViewModel StreamingAssistantMessage { get; set; }
        public ChatMessageViewModel StreamingThinkingMessage { get; set; }
        public Dictionary<string, ChatMessageViewModel> PendingToolCalls { get; }

        // Turn state
        public DateTime TurnStartedUtc { get; set; }
        public bool TurnInFlight { get; set; }
        public int TurnOutputTokens { get; set; }
        public int TurnInputTokens { get; set; }
        public AgentFinishConfig TurnFinishConfig { get; set; }
        public string LastRateLimitNotice { get; set; }

        // Codex native queue (one-shot agents)
        public Queue<string> CodexPromptQueue { get; }
        public bool IsCodexQueueOwner { get; set; }
        public TaskCompletionSource<bool> CodexTurnRendered { get; set; }
        public int CancelledCodexPromptCount { get; set; }

        // Lifecycle
        public CancellationTokenSource SessionCts { get; set; }

        // Claude-specific
        public ClaudeSessionOptions ClaudeOptions { get; set; }

        // Session settings snapshot
        public AiProvider SelectedProvider { get; set; }
        public string SelectedModel { get; set; }
        public EffortLevel SelectedEffortLevel { get; set; }

        // Event handler for cleanup
        internal EventHandler<AgentEvent> EventHandler { get; set; }

        public void Dispose()
        {
            try { ChatTranscript?.AbandonPendingInteractions(); } catch { }
            try { PendingToolCalls.Clear(); } catch { }
            try { CodexPromptQueue.Clear(); } catch { }
            try { AttachedFiles.Clear(); } catch { }
            try { if (AgentSession != null && EventHandler != null) AgentSession.Received -= EventHandler; } catch { }
            try { AgentSession?.Dispose(); } catch { }
            try { SessionCts?.Cancel(); SessionCts?.Dispose(); } catch { }
            ChatTranscript = null;
            AgentSession = null;
            Window = null;
        }
    }
}
