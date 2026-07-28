/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Contract implemented by every native-mode agent session adapter
 *
 * *******************************************************************************************************************/

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// A live conversation with an AI code agent over a structured (non-terminal) channel.
    /// <para>
    /// Implementations wrap very different transports — Claude's bidirectional stream-json, ACP
    /// JSON-RPC, one-shot-plus-resume CLIs — but all of them normalize onto <see cref="AgentEvent"/>,
    /// so <c>ClaudeCodeControl</c> talks to one interface regardless of the selected provider.
    /// </para>
    /// <para>
    /// Nothing in this namespace may reference WPF or the Visual Studio SDK: it is unit-tested from
    /// <c>Tests/</c> without a running IDE.
    /// </para>
    /// </summary>
    public interface IAgentSession : IDisposable
    {
        /// <summary>
        /// Provider session id, once known. Empty until <see cref="AgentEventKind.SessionStarted"/>
        /// is raised. For Claude this is the UUID that also names the JSONL transcript, which is what
        /// lets the existing Session History window resume a native-mode conversation.
        /// </summary>
        string SessionId { get; }

        /// <summary>
        /// The id a relaunch may safely hand to <c>--resume</c>, or empty when there is nothing to
        /// resume yet.
        /// <para>
        /// Not the same thing as <see cref="SessionId"/>: adapters seed that with an id they *intend*
        /// to use, and Claude only creates the transcript once a turn actually runs. Resuming an id
        /// the CLI never created fails the launch ("No conversation found with session ID"), so a
        /// relaunch must ask for this instead.
        /// </para>
        /// </summary>
        string ResumableSessionId { get; }

        /// <summary>Model reported by the provider, once known.</summary>
        string Model { get; }

        /// <summary>True when <see cref="InterruptAsync"/> can actually stop a turn in flight.</summary>
        bool SupportsInterrupt { get; }

        /// <summary>
        /// False for adapters that can only deliver the whole answer at once (print-mode CLIs);
        /// the UI shows a spinner and a "limited mode" hint instead of streaming text.
        /// </summary>
        bool SupportsStreaming { get; }

        /// <summary>True between sending a prompt and the matching turn-completed event.</summary>
        bool IsBusy { get; }

        /// <summary>
        /// Raised for every normalized event. May fire on a background thread — subscribers are
        /// responsible for marshalling to the UI thread.
        /// </summary>
        event EventHandler<AgentEvent> Received;

        /// <summary>
        /// Launches the agent for the given working directory. Completes once the process is running;
        /// readiness is signalled separately by <see cref="AgentEventKind.SessionStarted"/>.
        /// </summary>
        Task StartAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>
        /// Sends one user turn. Throws <see cref="InvalidOperationException"/> if the session is not
        /// started. Safe to call while <see cref="IsBusy"/> only if the provider queues turns.
        /// </summary>
        Task SendAsync(string text, CancellationToken cancellationToken);

        /// <summary>
        /// Aborts the turn in flight. No-op when <see cref="SupportsInterrupt"/> is false.
        /// </summary>
        Task InterruptAsync(CancellationToken cancellationToken);
    }
}
