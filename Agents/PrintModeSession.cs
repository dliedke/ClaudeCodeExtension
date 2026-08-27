/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Last-resort agent session for CLIs whose only headless mode prints plain text (Antigravity)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Launch settings for a print-mode CLI.
    /// </summary>
    public class PrintModeSessionOptions
    {
        /// <summary>Executable, unquoted. May be a bare name resolved through PATH.</summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>Model alias to request, or empty for the CLI default.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>Mapped from the provider's existing "skip permissions" setting.</summary>
        public bool SkipApprovals { get; set; }

        public string DisplayName { get; set; } = "agent";

        /// <summary>
        /// User-supplied extra flags (Settings → CLI Paths → "Extra launch arguments"), inserted
        /// verbatim before <c>--print &lt;prompt&gt;</c> on every turn. Empty adds nothing.
        /// </summary>
        public string ExtraArguments { get; set; } = string.Empty;

        public IDictionary<string, string> EnvironmentOverrides { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drives a CLI that offers nothing but "run one prompt and print the answer".
    /// <para>
    /// Antigravity is the only provider in this shape: its headless surface is <c>--print</c>, plain
    /// text, no event stream. So this session cannot show thinking, tool calls, token counts or
    /// partial output — the answer appears in one piece when the turn ends, which is why
    /// <see cref="SupportsStreaming"/> is false and the panel shows a "limited mode" hint.
    /// </para>
    /// <para>
    /// Continuity between turns relies on the CLI's own <c>--continue</c> ("resume the most recent
    /// conversation"), because print mode never reports a conversation id to resume by. That is
    /// per-CLI state, not per-panel: if the same CLI is used elsewhere between two prompts, the
    /// second prompt continues that other conversation instead.
    /// </para>
    /// <para>
    /// Every turn is a fresh process, so the CLI's own workspace state never carries over from the
    /// process's working directory: <c>agy --print</c> ignores its OS cwd for workspace purposes and
    /// reports no active workspace unless told about the folder explicitly via <c>--add-dir</c>, which
    /// is why every turn passes it (confirmed against a real Antigravity install).
    /// </para>
    /// </summary>
    public class PrintModeSession : IAgentSession
    {
        private readonly PrintModeSessionOptions _options;

        private JsonLineProcessHost _host;
        private string _workingDirectory = string.Empty;
        private bool _hasPriorTurn;
        private int _busy;
        private volatile bool _disposed;
        private volatile bool _interrupted;

        public PrintModeSession(PrintModeSessionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public string SessionId
        {
            get { return string.Empty; }
        }

        /// <summary>Print mode has no session to resume.</summary>
        public string ResumableSessionId
        {
            get { return string.Empty; }
        }

        public string Model
        {
            get { return _options.Model ?? string.Empty; }
        }

        /// <summary>Stopping means killing the process; whatever it had already printed is lost.</summary>
        public bool SupportsInterrupt
        {
            get { return true; }
        }

        /// <summary>No event stream, so the answer can only arrive whole.</summary>
        public bool SupportsStreaming
        {
            get { return false; }
        }

        public bool IsBusy
        {
            get { return Volatile.Read(ref _busy) != 0; }
        }

        public event EventHandler<AgentEvent> Received;

        public Task StartAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PrintModeSession));

            _workingDirectory = workingDirectory ?? string.Empty;

            Raise(AgentEvent.SessionStarted(string.Empty, Model, null, null));

            return Task.CompletedTask;
        }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PrintModeSession));
            if (string.IsNullOrWhiteSpace(text)) return;

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("A turn is already running.");
            }

            _interrupted = false;

            var answer = new StringBuilder();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = PrintModeCommandBuilder.GetFileName(_options),
                Arguments = PrintModeCommandBuilder.GetArguments(_options, text, _hasPriorTurn, _workingDirectory),
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
                lock (answer)
                {
                    answer.AppendLine(line);
                }
            };
            EventHandler<string> onError = delegate (object sender, string line)
            {
                Debug.WriteLine($"{_options.DisplayName} stderr: {line}");
            };
            EventHandler<int> onExited = delegate (object sender, int code) { exited.TrySetResult(code); };

            host.LineReceived += onLine;
            host.ErrorLineReceived += onError;
            host.Exited += onExited;

            try
            {
                await host.StartAsync(cancellationToken);

                // The prompt is already on the command line; closing stdin keeps the CLI from waiting on
                // input it will never get — an unanswered console prompt here would hang the turn forever.
                host.CloseInput();

                int exitCode = await exited.Task;
                await host.WaitForOutputDrainAsync(2000);

                string output;
                lock (answer)
                {
                    output = answer.ToString().TrimEnd();
                }

                if (!string.IsNullOrEmpty(output))
                {
                    Raise(AgentEvent.AssistantText(output));
                }

                if (exitCode != 0 && !_interrupted)
                {
                    Raise(AgentEvent.SessionError(
                        $"{_options.DisplayName} exited with code {exitCode}."));
                }
                else if (!_interrupted)
                {
                    // Only a turn that actually reached the CLI may be continued; otherwise the next
                    // prompt would attach itself to whatever unrelated conversation came before.
                    _hasPriorTurn = true;
                }

                Raise(AgentEvent.TurnCompleted(null, null, _interrupted));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{_options.DisplayName}: turn failed: {ex}");
                Raise(AgentEvent.SessionError($"The turn failed: {ex.Message}"));
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
    /// Builds the command line for one print-mode turn.
    /// </summary>
    public static class PrintModeCommandBuilder
    {
        public static string GetFileName(PrintModeSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            return IsBatchScript(options.ExecutablePath) ? "cmd.exe" : options.ExecutablePath;
        }

        public static string GetArguments(PrintModeSessionOptions options, string prompt, bool continueConversation, string workingDirectory)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var arguments = new StringBuilder();

            if (continueConversation)
            {
                arguments.Append("--continue ");
            }

            // agy ignores the process's working directory for its own notion of "workspace" — without
            // --add-dir it reports no active workspace and offers to scaffold a project under its own
            // scratch folder instead of seeing the one VS opened.
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                arguments.Append("--add-dir ").Append(Quote(workingDirectory)).Append(' ');
            }

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                arguments.Append("--model ").Append(options.Model).Append(' ');
            }

            if (options.SkipApprovals)
            {
                arguments.Append("--dangerously-skip-permissions ");
            }

            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
            {
                arguments.Append(options.ExtraArguments.Trim()).Append(' ');
            }

            arguments.Append("--print ").Append(Quote(prompt));

            if (IsBatchScript(options.ExecutablePath))
            {
                return "/c " + Quote(options.ExecutablePath + " " + arguments);
            }

            return arguments.ToString();
        }

        private static bool IsBatchScript(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
