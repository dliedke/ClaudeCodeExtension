/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Provider-agnostic event model produced by native-mode agent sessions
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Kind of <see cref="AgentEvent"/>. Every native-mode adapter (Claude stream-json, ACP,
    /// one-shot+resume, print) normalizes its own wire protocol into these, so the chat UI never
    /// needs to know which provider is running.
    /// </summary>
    public enum AgentEventKind
    {
        /// <summary>
        /// Session is up. Carries session id, model and the tool/slash-command inventory.
        /// <para>
        /// Measured, and easy to get wrong: Claude re-announces this at the start of <b>every turn</b>,
        /// not once per conversation (the tool list can even grow between turns as skills load). The UI
        /// must treat it as "here is the current inventory" and never as "new conversation" — clearing
        /// the transcript on it would wipe the history on the second prompt.
        /// </para>
        /// </summary>
        SessionStarted,

        /// <summary>A chunk of assistant-visible text. Chunks are appended, never replaced.</summary>
        AssistantText,

        /// <summary>A chunk of extended-thinking text, rendered in a collapsed block.</summary>
        Thinking,

        /// <summary>The agent invoked a tool. Carries the tool name and its input payload.</summary>
        ToolCallStarted,

        /// <summary>A tool call produced a result (or an error).</summary>
        ToolCallCompleted,

        /// <summary>
        /// The agent needs the user to approve a tool call. Only providers whose protocol has a real
        /// permission channel (ACP) emit this; Claude stream-json cannot — see
        /// <see cref="AgentPermissionDenial"/>.
        /// </summary>
        PermissionRequested,

        /// <summary>
        /// The agent is blocked on the user: a multiple-choice question, a plan waiting for approval,
        /// or a tool call needing a yes/no. Claude Code raises these over the stdio permission channel
        /// (see <see cref="AgentInteractionRequest"/>); answering unblocks the turn.
        /// </summary>
        InteractionRequested,

        /// <summary>The turn ended. Carries usage, cost and any permission denials.</summary>
        TurnCompleted,

        /// <summary>
        /// Token counts reported <b>during</b> a turn, so the status line can count up live instead of
        /// only settling once at the end. Providers whose protocol says nothing until the turn is over
        /// simply never raise it — the elapsed clock still runs.
        /// </summary>
        UsageUpdated,

        /// <summary>Fresh rate-limit/quota data reported by the CLI mid-stream.</summary>
        RateLimitUpdated,

        /// <summary>The session failed. Carries a human-readable message.</summary>
        SessionError
    }

    /// <summary>
    /// Token counts and cost for a completed turn.
    /// </summary>
    public class AgentUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheCreationTokens { get; set; }
        public double CostUsd { get; set; }
        public int DurationMs { get; set; }
    }

    /// <summary>
    /// A tool call the CLI blocked because permission was never granted.
    /// <para>
    /// This is the *only* signal the Claude stream-json protocol gives about permissions: there is no
    /// interactive channel, so a tool needing approval is silently auto-denied and merely listed here
    /// on the final result. The chat UI must surface these prominently — otherwise the agent appears
    /// to have quietly ignored the request. See docs/ARCHITECTURE.md → Modo Nativo.
    /// </para>
    /// </summary>
    public class AgentPermissionDenial
    {
        public string ToolName { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;

        /// <summary>Raw JSON of the tool input, for display.</summary>
        public string ToolInputJson { get; set; } = string.Empty;
    }

    /// <summary>
    /// One answer the user may give to a permission request.
    /// </summary>
    public class AgentPermissionOption
    {
        public string OptionId { get; set; } = string.Empty;

        /// <summary>Label to show on the button, as worded by the agent.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// ACP option kind: <c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c> or
        /// <c>reject_always</c>. Used only to order the buttons and pick the default.
        /// </summary>
        public string Kind { get; set; } = string.Empty;
    }

    /// <summary>
    /// A pending approval the agent is blocked on. Answering it unblocks the turn.
    /// <para>
    /// Only protocols with a real permission channel produce these — in practice the ACP agents.
    /// Measured caveat: Devin does not use this channel for edits, it uses session modes
    /// (<c>accept-edits</c>, <c>ask</c>, <c>bypass</c>) instead, so this may never fire for it.
    /// The adapter still handles it, because the protocol allows it and other ACP agents use it.
    /// </para>
    /// <para>
    /// Exactly one of <see cref="Respond"/> / <see cref="Cancel"/> takes effect; later calls are
    /// ignored, so a double-click on a dialog button cannot corrupt the JSON-RPC exchange.
    /// </para>
    /// </summary>
    public class AgentPermissionRequest
    {
        private readonly Action<string> _respond;
        private readonly object _gate = new object();
        private bool _answered;

        public AgentPermissionRequest(Action<string> respond)
        {
            _respond = respond;
        }

        /// <summary>Tool call awaiting approval, when the agent reported one.</summary>
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        /// <summary>Human-readable description of what the agent wants to do.</summary>
        public string Description { get; set; } = string.Empty;

        public IReadOnlyList<AgentPermissionOption> Options { get; set; }

        /// <summary>Approves or refuses with one of the <see cref="Options"/>.</summary>
        public void Respond(string optionId)
        {
            lock (_gate)
            {
                if (_answered) return;
                _answered = true;
            }

            _respond?.Invoke(optionId);
        }

        /// <summary>
        /// Declines to answer at all. Sent when the session is torn down with a dialog still open —
        /// leaving the agent waiting forever would wedge the process.
        /// </summary>
        public void Cancel()
        {
            lock (_gate)
            {
                if (_answered) return;
                _answered = true;
            }

            _respond?.Invoke(null);
        }
    }

    /// <summary>What the agent is waiting for. Picks the card the chat renders.</summary>
    public enum AgentInteractionKind
    {
        /// <summary>One or more multiple-choice questions (Claude's <c>AskUserQuestion</c>).</summary>
        Question,

        /// <summary>A plan written in plan mode, waiting to be approved (<c>ExitPlanMode</c>).</summary>
        PlanReview,

        /// <summary>Any other tool call the agent may not run without a yes.</summary>
        ToolApproval
    }

    /// <summary>One selectable answer to an <see cref="AgentQuestion"/>.</summary>
    public class AgentQuestionOption
    {
        public string Label { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>A single question inside an <see cref="AgentInteractionKind.Question"/> request.</summary>
    public class AgentQuestion
    {
        /// <summary>Short tag shown above the question, e.g. "Sabor".</summary>
        public string Header { get; set; } = string.Empty;

        /// <summary>
        /// The question text. This doubles as the key the CLI expects back in the answers map, so it
        /// must be echoed verbatim.
        /// </summary>
        public string Question { get; set; } = string.Empty;

        public bool MultiSelect { get; set; }

        public IReadOnlyList<AgentQuestionOption> Options { get; set; }
    }

    /// <summary>
    /// A blocked turn waiting on the user, raised over Claude Code's stdio permission channel.
    /// <para>
    /// Measured, and the reason this class exists at all: the CLI only offers <c>AskUserQuestion</c>
    /// and <c>ExitPlanMode</c> when it is launched with a permission prompt tool. Without it those two
    /// tools are absent from the inventory entirely and the agent answers "not available in this
    /// environment" — which is exactly what plan mode used to do here. With it, the CLI sends a
    /// <c>can_use_tool</c> control request and waits for a <c>control_response</c>.
    /// </para>
    /// <para>
    /// Exactly one of <see cref="Allow"/> / <see cref="Deny"/> takes effect; later calls are ignored,
    /// so double-clicking a card cannot corrupt the JSON-RPC exchange. Leaving one unanswered wedges
    /// the turn, so the session cancels any outstanding request when it is torn down.
    /// </para>
    /// </summary>
    public class AgentInteractionRequest
    {
        private readonly Action<bool, string, IDictionary<string, string>> _respond;
        private readonly object _gate = new object();
        private bool _answered;

        public AgentInteractionRequest(Action<bool, string, IDictionary<string, string>> respond)
        {
            _respond = respond;
        }

        public AgentInteractionKind Kind { get; set; }

        public string ToolName { get; set; } = string.Empty;

        public string ToolUseId { get; set; } = string.Empty;

        /// <summary>Raw JSON of the tool input, shown on the approval card.</summary>
        public string ToolInputJson { get; set; } = string.Empty;

        /// <summary>Plan markdown for <see cref="AgentInteractionKind.PlanReview"/>.</summary>
        public string PlanText { get; set; } = string.Empty;

        /// <summary>Questions for <see cref="AgentInteractionKind.Question"/>.</summary>
        public IReadOnlyList<AgentQuestion> Questions { get; set; }

        public bool IsAnswered
        {
            get { lock (_gate) { return _answered; } }
        }

        /// <summary>
        /// Set by the session for a <see cref="AgentInteractionKind.ToolApproval"/> whose tool can be
        /// pre-approved for the rest of the run. When present the card offers "Allow for the rest of
        /// this session"; invoking it records the tool so later requests for it are answered without
        /// another card. Null when the interaction is not eligible (questions, plan reviews).
        /// </summary>
        public Action OnAllowForSession { get; set; }

        /// <summary>
        /// Lets the tool run. <paramref name="answers"/> maps question text to the chosen label and is
        /// only meaningful for <see cref="AgentInteractionKind.Question"/>; pass null otherwise.
        /// </summary>
        public void Allow(IDictionary<string, string> answers)
        {
            lock (_gate)
            {
                if (_answered) return;
                _answered = true;
            }

            _respond?.Invoke(true, null, answers);
        }

        /// <summary>
        /// Allows this call and, via <see cref="OnAllowForSession"/>, tells the session to stop asking
        /// for the same tool for the rest of the run. Falls back to a plain <see cref="Allow(IDictionary{string,string})"/>
        /// when the interaction was not marked eligible.
        /// </summary>
        public void AllowForSession()
        {
            lock (_gate)
            {
                if (_answered) return;
            }

            Action remember = OnAllowForSession;
            if (remember != null)
            {
                try { remember(); }
                catch (Exception ex) { Debug.WriteLine($"AllowForSession bookkeeping failed: {ex.Message}"); }
            }

            Allow(null);
        }

        /// <summary>Refuses the tool call. The message is fed back to the agent as the reason.</summary>
        public void Deny(string message)
        {
            lock (_gate)
            {
                if (_answered) return;
                _answered = true;
            }

            _respond?.Invoke(false, message, null);
        }
    }

    /// <summary>
    /// Quota snapshot reported by the CLI. Lets the inline usage bars be fed straight from the agent
    /// stream instead of scraping claude.ai through WebView2.
    /// </summary>
    public class AgentRateLimit
    {
        /// <summary>e.g. "allowed", "allowed_warning", "rejected".</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>e.g. "five_hour", "weekly".</summary>
        public string LimitType { get; set; } = string.Empty;

        /// <summary>Unix seconds when the window resets. 0 when unknown.</summary>
        public long ResetsAtUnix { get; set; }

        public bool IsUsingOverage { get; set; }
    }

    /// <summary>
    /// A single normalized event from an agent session. One class rather than a type hierarchy so
    /// adapters stay allocation-cheap and the UI can switch on <see cref="Kind"/>; fields not
    /// relevant to a given kind are left at their defaults.
    /// </summary>
    public class AgentEvent
    {
        public AgentEventKind Kind { get; set; }

        /// <summary>Text payload: assistant/thinking chunk, or the message for <see cref="AgentEventKind.SessionError"/>.</summary>
        public string Text { get; set; } = string.Empty;

        // --- SessionStarted ---
        public string SessionId { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public IReadOnlyList<string> Tools { get; set; }
        public IReadOnlyList<string> SlashCommands { get; set; }

        // --- Tool calls ---
        public string ToolCallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;

        /// <summary>Raw JSON of the tool input, for display.</summary>
        public string ToolInputJson { get; set; } = string.Empty;

        /// <summary>Tool output text, for <see cref="AgentEventKind.ToolCallCompleted"/>.</summary>
        public string ToolResult { get; set; } = string.Empty;

        public bool IsError { get; set; }

        // --- TurnCompleted ---
        public AgentUsage Usage { get; set; }
        public IReadOnlyList<AgentPermissionDenial> PermissionDenials { get; set; }

        /// <summary>
        /// True when the turn ended because the user interrupted it, rather than completing normally.
        /// The UI shows this as a neutral "interrompido", not an error.
        /// </summary>
        public bool WasInterrupted { get; set; }

        // --- RateLimitUpdated ---
        public AgentRateLimit RateLimit { get; set; }

        // --- PermissionRequested ---
        public AgentPermissionRequest PermissionRequest { get; set; }

        // --- InteractionRequested ---
        public AgentInteractionRequest Interaction { get; set; }

        public static AgentEvent SessionStarted(string sessionId, string model,
            IReadOnlyList<string> tools, IReadOnlyList<string> slashCommands)
        {
            return new AgentEvent
            {
                Kind = AgentEventKind.SessionStarted,
                SessionId = sessionId ?? string.Empty,
                Model = model ?? string.Empty,
                Tools = tools,
                SlashCommands = slashCommands
            };
        }

        public static AgentEvent AssistantText(string text)
        {
            return new AgentEvent { Kind = AgentEventKind.AssistantText, Text = text ?? string.Empty };
        }

        public static AgentEvent Thinking(string text)
        {
            return new AgentEvent { Kind = AgentEventKind.Thinking, Text = text ?? string.Empty };
        }

        public static AgentEvent ToolCallStarted(string id, string name, string inputJson)
        {
            return new AgentEvent
            {
                Kind = AgentEventKind.ToolCallStarted,
                ToolCallId = id ?? string.Empty,
                ToolName = name ?? string.Empty,
                ToolInputJson = inputJson ?? string.Empty
            };
        }

        public static AgentEvent ToolCallCompleted(string id, string result, bool isError)
        {
            return new AgentEvent
            {
                Kind = AgentEventKind.ToolCallCompleted,
                ToolCallId = id ?? string.Empty,
                ToolResult = result ?? string.Empty,
                IsError = isError
            };
        }

        public static AgentEvent TurnCompleted(AgentUsage usage,
            IReadOnlyList<AgentPermissionDenial> denials, bool wasInterrupted)
        {
            return new AgentEvent
            {
                Kind = AgentEventKind.TurnCompleted,
                Usage = usage,
                PermissionDenials = denials,
                WasInterrupted = wasInterrupted
            };
        }

        /// <summary>
        /// A mid-turn usage snapshot. <see cref="AgentUsage.OutputTokens"/> is what the caller
        /// accumulates; the input and cache counts describe the request that produced this message.
        /// </summary>
        public static AgentEvent UsageUpdated(AgentUsage usage)
        {
            return new AgentEvent { Kind = AgentEventKind.UsageUpdated, Usage = usage };
        }

        public static AgentEvent RateLimitUpdated(AgentRateLimit rateLimit)
        {
            return new AgentEvent { Kind = AgentEventKind.RateLimitUpdated, RateLimit = rateLimit };
        }

        public static AgentEvent PermissionRequested(AgentPermissionRequest request)
        {
            return new AgentEvent { Kind = AgentEventKind.PermissionRequested, PermissionRequest = request };
        }

        public static AgentEvent InteractionRequested(AgentInteractionRequest interaction)
        {
            return new AgentEvent { Kind = AgentEventKind.InteractionRequested, Interaction = interaction };
        }

        public static AgentEvent SessionError(string message)
        {
            return new AgentEvent { Kind = AgentEventKind.SessionError, Text = message ?? string.Empty };
        }
    }
}
