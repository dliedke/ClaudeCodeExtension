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
using System.Collections.Concurrent;
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

        /// <summary>
        /// The previous <see cref="IAgentSession.SessionId"/> this tab's agent is carrying forward
        /// through a resume, or the current agent's own (still-unconfirmed) id when it isn't resuming
        /// anything — null once consumed. The CLI is the authority on the id and can hand back a
        /// different one on every relaunch even for the same conversation (see
        /// <c>ClaudeStreamJsonSession.SessionId</c>); since custom titles/colors are keyed by that id
        /// (and shared with the Session History window, so they can't switch to a synthetic key
        /// instead), the next confirmed <c>SessionStarted</c> event migrates the stored entry from this
        /// id to the new one rather than orphaning it. Set by <c>RelaunchSessionAsync</c> and consumed
        /// (cleared) by <c>ApplyAgentEventToSession</c>.
        /// </summary>
        public string MigrationSourceSessionId { get; set; }

        // Claude-specific
        public ClaudeSessionOptions ClaudeOptions { get; set; }

        // Session settings snapshot. Seeded from the global settings at CreateAndRegisterSession
        // time, then mutated independently by this tab's own selector menus (v163.0) — never written
        // back to Settings, which is why a parallel tab can run Opus while the panel stays on Sonnet.
        public AiProvider SelectedProvider { get; set; }
        public string SelectedModel { get; set; }
        public ClaudeModel SelectedClaudeModel { get; set; }
        public EffortLevel SelectedEffortLevel { get; set; }
        public CodexReasoningLevel SelectedCodexReasoningLevel { get; set; }
        public bool SkipPermissions { get; set; }
        public bool PlanMode { get; set; }

        /// <summary>
        /// Serializes this session's own relaunches (model/effort/permission switch, "New chat").
        /// Deliberately separate from the panel's <c>_nativeLifecycleSemaphore</c>: that one also
        /// guards a full agent switch, which never touches a parallel session, so sharing it would
        /// make an unrelated tab's relaunch wait on this tab for no reason.
        /// </summary>
        public SemaphoreSlim RelaunchLock { get; } = new SemaphoreSlim(1, 1);

        // Event handler for cleanup
        internal EventHandler<AgentEvent> EventHandler { get; set; }

        /// <summary>
        /// Events queued for this session, drained strictly in arrival order by the pump in
        /// <c>ClaudeCodeControl.NativeMode.cs</c>. An independent fire-and-forget
        /// <c>SwitchToMainThreadAsync</c> per event has no ordering guarantee once anything on the main
        /// thread pumps a nested message loop (a modal permission dialog, for one), so this queue plus
        /// <see cref="EventPumpRunning"/> is what guarantees streamed text renders in the order the
        /// agent sent it.
        /// </summary>
        internal readonly ConcurrentQueue<AgentEvent> PendingEvents = new ConcurrentQueue<AgentEvent>();

        /// <summary>1 while a drain loop owns <see cref="PendingEvents"/>; guards against a second loop starting. Plain field (not a property) so it can be passed to Interlocked by ref.</summary>
        internal int EventPumpRunning;

        public void Dispose()
        {
            try { ChatTranscript?.AbandonPendingInteractions(); } catch { }
            try { PendingToolCalls.Clear(); } catch { }
            try { CodexPromptQueue.Clear(); } catch { }
            try { while (PendingEvents.TryDequeue(out _)) { } } catch { }
            try { AttachedFiles.Clear(); } catch { }
            try { if (AgentSession != null && EventHandler != null) AgentSession.Received -= EventHandler; } catch { }
            try { AgentSession?.Dispose(); } catch { }
            try { SessionCts?.Cancel(); SessionCts?.Dispose(); } catch { }
            try { RelaunchLock?.Dispose(); } catch { }
            ChatTranscript = null;
            AgentSession = null;
            Window = null;
        }
    }
}
