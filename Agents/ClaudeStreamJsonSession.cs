/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native-mode adapter for Claude Code over bidirectional stream-json
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Talks to the Claude Code CLI in headless bidirectional stream-json mode: one long-lived process
    /// whose stdin stays open, so every turn of the conversation reuses the same context and prompt
    /// cache.
    /// <para>
    /// Two behaviours here are not obvious from the protocol and were established by measurement
    /// (docs/ARCHITECTURE.md → Modo Nativo):
    /// </para>
    /// <list type="bullet">
    /// <item>Closing stdin ends the session. It is left open between turns and only closed on dispose.</item>
    /// <item>After an interrupt the CLI exits with code 1. That is normal, not a failure — the session
    /// transparently relaunches itself with <c>--resume</c> so the user can keep talking. The flag that
    /// says so is scoped to the process being torn down, so nothing written while it shuts down can
    /// make that exit look like a crash.</item>
    /// </list>
    /// </summary>
    public class ClaudeStreamJsonSession : IAgentSession
    {
        private readonly ClaudeSessionOptions _options;
        private readonly string _sessionIdSeed;

        // Serializes launch/relaunch so an interrupt-driven relaunch and a user prompt arriving at the
        // same moment cannot start two CLI processes against one session.
        private readonly SemaphoreSlim _launchLock = new SemaphoreSlim(1, 1);

        // Control requests the CLI is blocked on. Denied on teardown so the process can exit.
        private readonly List<AgentInteractionRequest> _pendingInteractions = new List<AgentInteractionRequest>();

        // Tools the user chose "Allow for the rest of this session" on. Later approval requests for the
        // same tool are answered here without surfacing another card. Scoped to this session object, so
        // a "New chat" / model switch (which builds a fresh session) starts asking again.
        private readonly HashSet<string> _sessionAllowedTools = new HashSet<string>(StringComparer.Ordinal);

        private JsonLineProcessHost _host;
        private ClaudeStreamParser _parser;
        private string _workingDirectory = string.Empty;
        private string _resumeId = string.Empty;
        private Task _relaunchTask;
        private volatile bool _interruptRequested;
        private volatile bool _sessionIdConfirmed;
        private volatile bool _disposed;
        private int _busy;

        public ClaudeStreamJsonSession(ClaudeSessionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _sessionIdSeed = string.IsNullOrWhiteSpace(_options.SessionId)
                ? Guid.NewGuid().ToString()
                : _options.SessionId;

            SessionId = _sessionIdSeed;
            _resumeId = string.IsNullOrWhiteSpace(_options.ResumeSessionId) ? string.Empty : _options.ResumeSessionId;
        }

        public string SessionId { get; private set; }

        /// <summary>
        /// The id a relaunch may hand to <c>--resume</c>, or empty when nothing is resumable yet.
        /// <para>
        /// Measured: the CLI emits <b>nothing at all</b> until a turn runs — <c>system/init</c>, which
        /// is what confirms the id, arrives with the first turn and not at launch. So between a
        /// relaunch and the user's next prompt, <see cref="SessionId"/> is still the throwaway seed
        /// this instance was constructed with, and the CLI has never created a transcript under it.
        /// Handing that to <c>--resume</c> fails the launch outright ("No conversation found with
        /// session ID"), which surfaced as an aborted turn followed by a dead process — the exact
        /// failure seen when two composer dropdowns were changed in a row, since each change
        /// relaunches and the second one resumed the first one's unused seed.
        /// </para>
        /// <para>
        /// So: the confirmed id when there is one, otherwise the id this instance was itself launched
        /// to resume (already confirmed by whoever handed it over), otherwise empty.
        /// </para>
        /// </summary>
        public string ResumableSessionId
        {
            get { return _sessionIdConfirmed ? SessionId : (_resumeId ?? string.Empty); }
        }

        public string Model { get; private set; } = string.Empty;

        public bool SupportsInterrupt { get { return true; } }

        public bool SupportsStreaming { get { return _options.IncludePartialMessages; } }

        public bool IsBusy { get { return Volatile.Read(ref _busy) != 0; } }

        public event EventHandler<AgentEvent> Received;

        public async Task StartAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClaudeStreamJsonSession));

            _workingDirectory = workingDirectory ?? string.Empty;

            await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_host != null)
                {
                    throw new InvalidOperationException("Session already started.");
                }

                await LaunchAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _launchLock.Release();
            }
        }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClaudeStreamJsonSession));
            if (string.IsNullOrEmpty(text)) return;

            await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

            // _interruptRequested is deliberately NOT cleared here: it belongs to the process that is
            // shutting down, not to this write. Clearing it made any send that landed between "stop
            // pressed" and "the CLI actually exits" turn the expected code-1 exit into a red
            // "exited unexpectedly" banner and skip the --resume relaunch. LaunchAsync clears it.
            Volatile.Write(ref _busy, 1);

            var payload = new
            {
                type = "user",
                message = new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = text } }
                }
            };

            try
            {
                await _host.WriteLineAsync(JsonConvert.SerializeObject(payload), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Volatile.Write(ref _busy, 0);
                throw;
            }
        }

        public async Task InterruptAsync(CancellationToken cancellationToken)
        {
            if (_disposed || !IsBusy)
            {
                return;
            }

            JsonLineProcessHost host = _host;
            if (host == null || !host.IsRunning)
            {
                return;
            }

            _interruptRequested = true;

            // A card still on screen would keep the CLI blocked and swallow the interrupt.
            ClearPendingInteractions(deny: true);

            var payload = new
            {
                type = "control_request",
                request_id = Guid.NewGuid().ToString("N"),
                request = new { subtype = "interrupt" }
            };

            try
            {
                await host.WriteLineAsync(JsonConvert.SerializeObject(payload), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The process already went away — the turn is over either way.
                Debug.WriteLine($"ClaudeStreamJsonSession: interrupt not delivered: {ex.Message}");
            }
        }

        private async Task LaunchAsync(CancellationToken cancellationToken)
        {
            // The flag describes the process being replaced, so a fresh one always starts without it.
            _interruptRequested = false;

            var launchOptions = new ClaudeSessionOptions
            {
                ExecutablePath = _options.ExecutablePath,
                UseWsl = _options.UseWsl,
                WslWorkingDirectory = _options.WslWorkingDirectory,
                SessionId = _sessionIdSeed,
                ResumeSessionId = _resumeId,
                Model = _options.Model,
                Effort = _options.Effort,
                DangerouslySkipPermissions = _options.DangerouslySkipPermissions,
                PermissionMode = _options.PermissionMode,
                InteractivePermissions = _options.InteractivePermissions,
                IncludePartialMessages = _options.IncludePartialMessages,
                ExtraArguments = _options.ExtraArguments
            };

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = ClaudeCommandBuilder.GetFileName(launchOptions),
                Arguments = ClaudeCommandBuilder.GetArguments(launchOptions),
                WorkingDirectory = _workingDirectory
            };

            foreach (var pair in _options.EnvironmentOverrides)
            {
                hostOptions.EnvironmentOverrides[pair.Key] = pair.Value;
            }

            _parser = new ClaudeStreamParser(_options.IncludePartialMessages)
            {
                ControlResponder = WriteControlResponse
            };

            var host = new JsonLineProcessHost(hostOptions);
            host.LineReceived += OnLineReceived;
            host.ErrorLineReceived += OnErrorLineReceived;
            host.Exited += OnHostExited;

            _host = host;

            Debug.WriteLine($"ClaudeStreamJsonSession: launching {hostOptions.FileName} {hostOptions.Arguments}");
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Makes sure a process is available before writing to it: after an interrupt the previous one
        /// is gone and a relaunch may still be in flight.
        /// </summary>
        private async Task EnsureRunningAsync(CancellationToken cancellationToken)
        {
            Task relaunch = _relaunchTask;
            if (relaunch != null)
            {
                try
                {
                    await relaunch.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClaudeStreamJsonSession: relaunch failed: {ex.Message}");
                }
            }

            if (_host != null && _host.IsRunning)
            {
                return;
            }

            await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_host != null && _host.IsRunning)
                {
                    return;
                }

                DisposeHost();

                // ResumableSessionId, not SessionId: an unconfirmed seed has no transcript behind it,
                // and --resume on one fails the launch. Empty falls back to --session-id, which is
                // right — nothing ran under it yet.
                _resumeId = ResumableSessionId;
                await LaunchAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _launchLock.Release();
            }
        }

        private void OnLineReceived(object sender, string line)
        {
            if (_disposed || _parser == null)
            {
                return;
            }

            foreach (AgentEvent agentEvent in _parser.Parse(line))
            {
                if (agentEvent.Kind == AgentEventKind.InteractionRequested)
                {
                    AgentInteractionRequest interaction = agentEvent.Interaction;

                    bool eligibleForSession =
                        interaction != null &&
                        interaction.Kind == AgentInteractionKind.ToolApproval &&
                        !string.IsNullOrEmpty(interaction.ToolName);

                    // Already pre-approved this session: answer it and never raise a card.
                    if (eligibleForSession)
                    {
                        string tool = interaction.ToolName;
                        lock (_sessionAllowedTools)
                        {
                            if (_sessionAllowedTools.Contains(tool))
                            {
                                interaction.Allow(null);
                                continue;
                            }
                        }

                        // Let the card offer "Allow for the rest of this session"; the callback is what
                        // adds the tool to the set the check above reads.
                        interaction.OnAllowForSession = delegate
                        {
                            lock (_sessionAllowedTools)
                            {
                                _sessionAllowedTools.Add(tool);
                            }
                        };
                    }

                    // Tracked so an interrupt or a dispose can deny it: a control request left
                    // unanswered blocks the CLI forever, and the process would never exit.
                    lock (_pendingInteractions)
                    {
                        _pendingInteractions.Add(interaction);
                    }
                }
                else if (agentEvent.Kind == AgentEventKind.SessionStarted)
                {
                    // The CLI is the authority on the id: a --resume relaunch can come back with a new
                    // one, and it is what names the JSONL transcript the Session History window lists.
                    if (!string.IsNullOrEmpty(agentEvent.SessionId))
                    {
                        SessionId = agentEvent.SessionId;

                        // The transcript exists from here on, so this id is safe to --resume.
                        _sessionIdConfirmed = true;
                    }
                    if (!string.IsNullOrEmpty(agentEvent.Model))
                    {
                        Model = agentEvent.Model;
                    }
                }
                else if (agentEvent.Kind == AgentEventKind.TurnCompleted)
                {
                    Volatile.Write(ref _busy, 0);
                    ClearPendingInteractions(deny: false);
                }

                Raise(agentEvent);
            }
        }

        private void OnErrorLineReceived(object sender, string line)
        {
            // These CLIs use stderr for progress and deprecation notices, so this is diagnostics only —
            // promoting it to a user-visible error would cry wolf on every launch.
            Debug.WriteLine($"ClaudeStreamJsonSession [stderr]: {line}");
        }

        private void OnHostExited(object sender, int exitCode)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _busy, 0);

            if (_interruptRequested)
            {
                // Expected: the CLI always exits after honouring an interrupt. Bring the session back on
                // its own transcript so the next prompt continues the same conversation; the user never
                // sees the process bounce.
                _interruptRequested = false;
                _relaunchTask = Task.Run(async () =>
                {
                    try
                    {
                        await _launchLock.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (_disposed) return;

                            DisposeHost();
                            _resumeId = ResumableSessionId;
                            await LaunchAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            _launchLock.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ClaudeStreamJsonSession: relaunch after interrupt failed: {ex}");
                        Raise(AgentEvent.SessionError(
                            "The agent could not be resumed after the interruption. Restart the agent to continue."));
                    }
                });
                return;
            }

            if (exitCode != 0)
            {
                Raise(AgentEvent.SessionError(
                    $"The agent process exited unexpectedly (code {exitCode}). Restart the agent to continue."));
            }
        }

        /// <summary>
        /// Answers a <c>can_use_tool</c> control request. Fire-and-forget: the UI thread must not block
        /// on a pipe write, and a failed write means the process is gone, which ends the turn anyway.
        /// </summary>
        private void WriteControlResponse(string requestId, object response)
        {
            JsonLineProcessHost host = _host;
            if (host == null || !host.IsRunning)
            {
                return;
            }

            var payload = new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = requestId,
                    response
                }
            };

            string line = JsonConvert.SerializeObject(payload);

#pragma warning disable VSTHRD110 // Fire-and-forget by design; failures are logged, not surfaced.
            Task.Run(async () =>
            {
                try
                {
                    await host.WriteLineAsync(line, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClaudeStreamJsonSession: control response not delivered: {ex.Message}");
                }
            });
#pragma warning restore VSTHRD110
        }

        /// <summary>
        /// Drops the pending-request list, optionally denying anything still unanswered so the CLI is
        /// never left waiting on a card the user can no longer see.
        /// </summary>
        private void ClearPendingInteractions(bool deny)
        {
            AgentInteractionRequest[] pending;
            lock (_pendingInteractions)
            {
                if (_pendingInteractions.Count == 0) return;

                pending = _pendingInteractions.ToArray();
                _pendingInteractions.Clear();
            }

            if (!deny)
            {
                return;
            }

            foreach (AgentInteractionRequest request in pending)
            {
                try
                {
                    request.Deny("The session ended before this could be answered.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClaudeStreamJsonSession: denying pending interaction failed: {ex.Message}");
                }
            }
        }

        private void Raise(AgentEvent agentEvent)
        {
            EventHandler<AgentEvent> handler = Received;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, agentEvent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClaudeStreamJsonSession: subscriber threw: {ex}");
            }
        }

        private void DisposeHost()
        {
            JsonLineProcessHost host = _host;
            if (host == null)
            {
                return;
            }

            host.LineReceived -= OnLineReceived;
            host.ErrorLineReceived -= OnErrorLineReceived;
            host.Exited -= OnHostExited;

            _host = null;

            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClaudeStreamJsonSession: host dispose failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            ClearPendingInteractions(deny: true);
            _disposed = true;

            DisposeHost();
            _launchLock.Dispose();
        }
    }
}
