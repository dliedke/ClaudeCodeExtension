/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Agent session for CLIs that stream JSON but exit after every turn (Codex, Cursor Agent)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Launch settings shared by the one-shot CLIs.
    /// </summary>
    public class OneShotSessionOptions
    {
        /// <summary>Executable, unquoted. May be a bare name resolved through PATH.</summary>
        public string ExecutablePath { get; set; } = string.Empty;

        public bool UseWsl { get; set; }

        /// <summary>Working directory in Linux form, used only when <see cref="UseWsl"/> is set.</summary>
        public string WslWorkingDirectory { get; set; } = string.Empty;

        /// <summary>Model alias to request, or empty for the CLI default.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>Codex reasoning effort, or empty for the selected model's default.</summary>
        public string ReasoningEffort { get; set; } = string.Empty;

        /// <summary>Mapped from the provider's existing "full auto" / "yolo" setting.</summary>
        public bool SkipApprovals { get; set; }

        /// <summary>
        /// Existing provider session to continue on the first turn. Empty starts a new conversation.
        /// Session History uses this for Codex; later turns keep using the id reported by the CLI.
        /// </summary>
        public string ResumeSessionId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = "agent";

        /// <summary>
        /// User-supplied extra flags (Settings → CLI Paths → "Extra launch arguments"), appended
        /// verbatim to every turn's command line, ahead of the trailing stdin marker. Empty adds
        /// nothing.
        /// </summary>
        public string ExtraArguments { get; set; } = string.Empty;

        public IDictionary<string, string> EnvironmentOverrides { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects what one turn produced. The protocol writes into this; the session reads it once the
    /// process exits.
    /// </summary>
    public class OneShotTurnSink
    {
        private readonly Action<AgentEvent> _emit;

        public OneShotTurnSink(Action<AgentEvent> emit)
        {
            _emit = emit;
        }

        /// <summary>Session/thread id the CLI reported, which the next turn resumes from.</summary>
        public string SessionId { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public AgentUsage Usage { get; set; }

        /// <summary>True once the CLI reported the turn as finished, however it ended.</summary>
        public bool TurnEnded { get; set; }

        /// <summary>
        /// Scratch space for whatever bookkeeping a protocol needs within one turn. It lives on the
        /// sink rather than on the protocol because the protocol instance is shared across turns.
        /// </summary>
        public object ProtocolState { get; set; }

        public void Emit(AgentEvent agentEvent)
        {
            if (agentEvent != null)
            {
                _emit?.Invoke(agentEvent);
            }
        }
    }

    /// <summary>
    /// Per-CLI knowledge for <see cref="OneShotResumeSession"/>: how to build the command line for a
    /// turn, and how to read the JSON lines it produces.
    /// <para>
    /// Implementations are pure — no process, no UI — so they can be unit-tested against captured
    /// transcripts.
    /// </para>
    /// </summary>
    public interface IOneShotTurnProtocol
    {
        /// <summary>Arguments for one turn. <paramref name="resumeSessionId"/> is empty on the first.</summary>
        string BuildArguments(OneShotSessionOptions options, string resumeSessionId);

        /// <summary>Handles one line of the CLI's stdout.</summary>
        void HandleLine(string line, OneShotTurnSink sink);
    }

    /// <summary>
    /// Bridges CLIs that emit a proper JSON stream but end the process with the turn.
    /// <para>
    /// Codex and Cursor Agent share that shape, so one session type covers both: it keeps the id the
    /// CLI hands out and relaunches with the resume flag for every later turn. Verified with Cursor —
    /// turn 2 recalled what turn 1 wrote to disk, on a fresh process.
    /// </para>
    /// <para>
    /// The trade-off is inherent to the transport, not to this class: startup latency is paid per
    /// message and the provider's prompt cache does not carry over.
    /// </para>
    /// </summary>
    public class OneShotResumeSession : IAgentSession
    {
        private readonly OneShotSessionOptions _options;
        private readonly IOneShotTurnProtocol _protocol;

        private JsonLineProcessHost _host;
        private string _workingDirectory = string.Empty;
        private int _busy;
        private volatile bool _disposed;
        private volatile bool _interrupted;

        public OneShotResumeSession(OneShotSessionOptions options, IOneShotTurnProtocol protocol)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            SessionId = _options.ResumeSessionId ?? string.Empty;
        }

        public string SessionId { get; private set; } = string.Empty;

        /// <summary>Only ever set from what the CLI reported, so it is confirmed by construction.</summary>
        public string ResumableSessionId
        {
            get { return SessionId; }
        }

        public string Model { get; private set; } = string.Empty;

        /// <summary>Stopping a turn means killing the process, which every CLI here tolerates.</summary>
        public bool SupportsInterrupt
        {
            get { return true; }
        }

        public bool SupportsStreaming
        {
            get { return true; }
        }

        public bool IsBusy
        {
            get { return Volatile.Read(ref _busy) != 0; }
        }

        public event EventHandler<AgentEvent> Received;

        /// <summary>
        /// Changes the reasoning effort used by future one-shot turns. A running turn keeps the value
        /// it launched with; the next process resumes the same thread with the new override.
        /// </summary>
        public void SetReasoningEffort(string reasoningEffort)
        {
            lock (_options)
            {
                _options.ReasoningEffort = reasoningEffort ?? string.Empty;
            }
        }

        /// <summary>
        /// Records the workspace. Nothing is launched here: with no persistent process there is nothing
        /// to start until the user actually sends something.
        /// </summary>
        public Task StartAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OneShotResumeSession));

            _workingDirectory = workingDirectory ?? string.Empty;

            Raise(AgentEvent.SessionStarted(SessionId, string.Empty, null, null));

            return Task.CompletedTask;
        }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OneShotResumeSession));
            if (string.IsNullOrWhiteSpace(text)) return;

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("A turn is already running.");
            }

            _interrupted = false;

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new OneShotTurnSink(Raise);

            string arguments;
            lock (_options)
            {
                arguments = OneShotCommandBuilder.GetArguments(_options, _protocol, SessionId);
            }

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = OneShotCommandBuilder.GetFileName(_options),
                Arguments = arguments,
                WorkingDirectory = _workingDirectory
            };

            foreach (KeyValuePair<string, string> pair in _options.EnvironmentOverrides)
            {
                hostOptions.EnvironmentOverrides[pair.Key] = pair.Value;
            }

            var host = new JsonLineProcessHost(hostOptions);
            _host = host;

            EventHandler<string> onLine = delegate (object sender, string line)
            {
                try
                {
                    _protocol.HandleLine(line, sink);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{_options.DisplayName}: failed to handle line: {ex}");
                }
            };
            // Kept so a failed turn can quote the CLI instead of only its exit code: these CLIs explain
            // themselves on stderr ("input is not valid UTF-8", an expired login, an unknown flag) and
            // Debug.WriteLine is compiled out of Release, which left the user with a bare exit code.
            var stderrTail = new Queue<string>();

            EventHandler<string> onError = delegate (object sender, string line)
            {
                Debug.WriteLine($"{_options.DisplayName} stderr: {line}");

                lock (stderrTail)
                {
                    stderrTail.Enqueue(line);
                    if (stderrTail.Count > StderrTailLines)
                    {
                        stderrTail.Dequeue();
                    }
                }
            };
            EventHandler<int> onExited = delegate (object sender, int code) { exited.TrySetResult(code); };

            host.LineReceived += onLine;
            host.ErrorLineReceived += onError;
            host.Exited += onExited;

            try
            {
                await host.StartAsync(cancellationToken);

                // The prompt goes in over stdin — confirmed for both CLIs — so nothing has to be escaped
                // onto a command line and a multi-page prompt cannot overflow it. EOF is the CLI's cue
                // to start working.
                await host.WriteLineAsync(text, cancellationToken);
                host.CloseInput();

                int exitCode = await exited.Task;
                await host.WaitForOutputDrainAsync(2000);

                if (!string.IsNullOrEmpty(sink.SessionId))
                {
                    SessionId = sink.SessionId;
                }
                if (!string.IsNullOrEmpty(sink.Model))
                {
                    Model = sink.Model;
                }

                // A non-zero exit with no reported ending is a real failure — a crash, a bad flag, an
                // expired login. When the CLI did report the ending, it already said what went wrong.
                if (exitCode != 0 && !sink.TurnEnded && !_interrupted)
                {
                    Raise(AgentEvent.SessionError(
                        $"{_options.DisplayName} exited with code {exitCode} without finishing the turn."
                        + DescribeStderr(stderrTail)));
                }

                Raise(AgentEvent.TurnCompleted(sink.Usage, null, _interrupted));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{_options.DisplayName}: turn failed: {ex}");

                // The stderr tail matters most on exactly this path: a CLI that dies between launch and
                // the prompt write leaves nothing but "Agent process closed its input pipe", while the
                // reason it died — an expired login, an unknown flag, a session id it cannot resume —
                // is sitting in the lines it printed on its way out.
                Raise(AgentEvent.SessionError($"The turn failed: {ex.Message}" + DescribeStderr(stderrTail)));
                Raise(AgentEvent.TurnCompleted(null, null, _interrupted));
                throw;
            }
            finally
            {
                host.LineReceived -= onLine;
                host.ErrorLineReceived -= onError;
                host.Exited -= onExited;

                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{_options.DisplayName}: host dispose failed: {ex.Message}");
                }

                if (ReferenceEquals(_host, host))
                {
                    _host = null;
                }

                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>
        /// Kills the turn's process. The conversation survives: the next prompt resumes from the id the
        /// CLI already handed out.
        /// </summary>
        public Task InterruptAsync(CancellationToken cancellationToken)
        {
            JsonLineProcessHost host = _host;
            if (host == null || !host.IsRunning)
            {
                return Task.FromResult(0);
            }

            _interrupted = true;

            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{_options.DisplayName}: interrupt failed: {ex.Message}");
            }

            return Task.FromResult(0);
        }

        /// <summary>How many trailing stderr lines a failure message may quote.</summary>
        private const int StderrTailLines = 5;

        /// <summary>Longest stderr excerpt to append, so a chatty CLI cannot fill the transcript.</summary>
        private const int StderrExcerptLength = 600;

        /// <summary>
        /// Formats the tail of stderr for a failure message, or an empty string when the CLI said
        /// nothing. Noise about the shell itself is dropped: WSL's non-interactive bash always reports
        /// that it cannot set the terminal process group, which is expected and explains nothing.
        /// </summary>
        private static string DescribeStderr(Queue<string> stderrTail)
        {
            string[] lines;
            lock (stderrTail)
            {
                lines = stderrTail.ToArray();
            }

            var kept = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = (line ?? string.Empty).Trim();
                if (trimmed.Length == 0 ||
                    trimmed.StartsWith("bash: cannot set terminal process group", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("bash: no job control in this shell", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                kept.Add(trimmed);
            }

            if (kept.Count == 0)
            {
                return string.Empty;
            }

            string excerpt = string.Join(" ", kept);
            if (excerpt.Length > StderrExcerptLength)
            {
                excerpt = excerpt.Substring(excerpt.Length - StderrExcerptLength);
            }

            return " " + excerpt;
        }

        private void Raise(AgentEvent agentEvent)
        {
            EventHandler<AgentEvent> handler = Received;
            if (handler == null) return;

            try
            {
                handler(this, agentEvent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{_options.DisplayName}: subscriber threw on {agentEvent.Kind}: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            JsonLineProcessHost host = _host;
            _host = null;

            if (host != null)
            {
                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{_options.DisplayName}: dispose failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Wraps a protocol's arguments in whatever shell the executable needs — WSL, a .cmd shim, or
    /// neither.
    /// </summary>
    public static class OneShotCommandBuilder
    {
        public static string GetFileName(OneShotSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (options.UseWsl)
            {
                return "wsl.exe";
            }

            return IsBatchScript(options.ExecutablePath) ? "cmd.exe" : options.ExecutablePath;
        }

        public static string GetArguments(OneShotSessionOptions options, IOneShotTurnProtocol protocol, string resumeSessionId)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (protocol == null) throw new ArgumentNullException(nameof(protocol));

            string arguments = protocol.BuildArguments(options, resumeSessionId);

            if (options.UseWsl)
            {
                string inner = string.Empty;
                if (!string.IsNullOrWhiteSpace(options.WslWorkingDirectory))
                {
                    inner += "cd " + QuoteForBash(options.WslWorkingDirectory) + " && ";
                }
                inner += options.ExecutablePath + " " + arguments;

                // -i, not -li: a login shell prints its motd onto stdout, and stdout is the JSON stream.
                return "bash -ic " + QuoteForWindowsArgument(inner);
            }

            if (IsBatchScript(options.ExecutablePath))
            {
                return "/c " + QuoteForWindowsArgument(options.ExecutablePath + " " + arguments);
            }

            return arguments;
        }

        private static bool IsBatchScript(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteForBash(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";
        }

        private static string QuoteForWindowsArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
