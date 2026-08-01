/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Agent session over ACP (Agent Client Protocol) — covers OpenCode, Devin and Reasonix
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Drives an ACP agent: JSON-RPC 2.0 messages, one per line, over the child process's stdio.
    /// <para>
    /// One adapter serves every ACP CLI — <c>opencode acp</c>, <c>devin acp</c>, <c>reasonix acp</c>
    /// all speak the same protocol, verified live against each of them.
    /// </para>
    /// <para>
    /// Shapes confirmed on the wire (Devin 0.0.0-dev, Reasonix 0.53.2, OpenCode 1.17.18):
    /// <list type="bullet">
    /// <item><c>initialize</c> → <c>{protocolVersion, agentCapabilities, agentInfo, authMethods}</c></item>
    /// <item><c>session/new</c> → <c>{sessionId, modes?, configOptions?}</c></item>
    /// <item><c>session/prompt</c> → notifications, then a <b>response</b> carrying
    /// <c>{stopReason, usage?}</c>. The response — not any notification — is what ends a turn.</item>
    /// <item><c>session/update</c> notifications discriminated by <c>update.sessionUpdate</c>:
    /// <c>agent_message_chunk</c>, <c>agent_thought_chunk</c>, <c>tool_call</c>,
    /// <c>tool_call_update</c>, <c>usage_update</c>, plus inventory chatter this adapter ignores.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class AcpSession : IAgentSession
    {
        private readonly AcpSessionOptions _options;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> _pending
            = new ConcurrentDictionary<long, TaskCompletionSource<JToken>>();
        private readonly ConcurrentDictionary<string, AgentPermissionRequest> _openPermissions
            = new ConcurrentDictionary<string, AgentPermissionRequest>();
        private readonly ConcurrentDictionary<string, string> _toolNames
            = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private JsonLineProcessHost _host;

        /// <summary>
        /// Last few stderr lines from the running agent. These CLIs explain themselves there — an
        /// expired login, a bad flag, a crash — and the raw "closed its input pipe" IOException the
        /// write throws says nothing about why the process went away. Same trick, and the same caps,
        /// as <c>OneShotResumeSession</c>.
        /// </summary>
        private readonly Queue<string> _stderrTail = new Queue<string>();
        private const int StderrTailLines = 8;
        private const int StderrExcerptLength = 800;

        /// <summary>The agent's model picker from <c>session/new</c>, kept so the model can also be
        /// switched later without restarting the agent.</summary>
        private JToken _modelOption;

        private long _nextRequestId;
        private int _busy;
        private volatile bool _disposed;

        public AcpSession(AcpSessionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public string SessionId { get; private set; } = string.Empty;

        /// <summary>Only ever set from what the agent reported, so it is confirmed by construction.</summary>
        public string ResumableSessionId { get { return SessionId; } }

        public string Model { get; private set; } = string.Empty;

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

        #region Lifecycle

        /// <summary>
        /// Answers a CLI's first-ever-run console prompt before the real handshake starts.
        /// <para>
        /// Launches a disposable copy of the same command, writes "n\n" to its stdin the instant it is
        /// up, then kills it after a short grace period. When the prompt exists this answers it and the
        /// CLI persists the choice to its own config, so the real session started right after never
        /// sees it again; when the prompt does not exist (already answered on a prior run) the extra
        /// "n\n" lands on an already-running ACP server's stdin as one malformed JSON-RPC line, which
        /// every one of these CLIs already tolerates — this adapter does the same for lines it cannot
        /// parse (see <see cref="OnLineReceived"/>). The grace period is spent either way: an
        /// already-answered CLI just becomes a live ACP server we throw away instead of exiting on its
        /// own, since <c>acp</c> mode never exits by itself.
        /// </para>
        /// </summary>
        private static async Task PrimeFirstRunPromptAsync(JsonLineProcessOptions hostOptions,
            Action<string> log, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = hostOptions.FileName,
                Arguments = hostOptions.Arguments,
                WorkingDirectory = hostOptions.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            foreach (KeyValuePair<string, string> pair in hostOptions.EnvironmentOverrides)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            Process process = null;
            try
            {
                process = Process.Start(startInfo);
                if (process == null)
                {
                    log?.Invoke("priming: Process.Start returned null");
                    return;
                }

                // Drained so the child never blocks on a full pipe buffer while it is alive. stderr is
                // kept rather than discarded: if the primer cannot even launch (a broken shim, a bad
                // PATH), its complaint here is the same one the real session is about to hit.
                process.StandardOutput.BaseStream.CopyToAsync(Stream.Null).Forget();
                Task<string> primerStderr = process.StandardError.ReadToEndAsync();

                using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    await stdin.WriteAsync("n\n").ConfigureAwait(false);
                    await stdin.FlushAsync().ConfigureAwait(false);
                }

                await Task.WhenAny(
                    WaitForExitAsync(process),
                    Task.Delay(1500, cancellationToken)).ConfigureAwait(false);

                if (process.HasExited && log != null)
                {
                    // The primer answering and leaving is the normal path once the prompt has been
                    // answered before; a *non-zero* code here means the command itself is broken, which
                    // is the same wall the real session is about to hit. The read is already finished
                    // (the pipe closed with the process), but it is awaited with a cap rather than
                    // blocked on, so a stuck grandchild holding the handle cannot hang the launch.
                    string stderr = string.Empty;
                    if (await Task.WhenAny(primerStderr, Task.Delay(250)).ConfigureAwait(false) == primerStderr)
                    {
                        stderr = await primerStderr.ConfigureAwait(false);
                    }

                    log($"priming: exited on its own, code={process.ExitCode}" +
                        (string.IsNullOrWhiteSpace(stderr) ? "" : ", stderr=" + Truncate(stderr.Trim(), 400)));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ACP: first-run prompt priming failed: {ex.Message}");
                log?.Invoke($"priming: failed: {ex.Message}");
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            // Still up after the grace period: the prompt was not there to answer, so
                            // this is a live ACP server we never wanted. Expected on most launches.
                            ProcessTree.Kill(process.Id);

                            // Kill() only requests termination — the OS can take a moment to actually
                            // tear the process down and release whatever it was holding. Wait for the
                            // primer to actually be gone before handing back.
                            bool gone = await Task.WhenAny(WaitForExitAsync(process), Task.Delay(2000))
                                .ConfigureAwait(false) != null && process.HasExited;

                            log?.Invoke($"priming: killed after the grace period, confirmedGone={gone}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ACP: could not kill priming process: {ex.Message}");
                    }

                    process.Dispose();
                }

                // A short unconditional grace period before the real session starts. Note the Kill()
                // wait above only runs when the primer had to be killed, and once the first-run prompt
                // has already been answered the primer instead exits on its own in ~170ms — so without
                // this there is no wait at all on the common path.
                // Honest scope: this was added on the theory that the real session was racing the
                // primer's teardown for a workspace-scoped lock, and **that theory was refuted** —
                // driving this exact adapter outside Visual Studio starts Reasonix reliably with no
                // delay whatsoever, with the primer, and even with a second Reasonix already running on
                // the same workspace. It is kept only as cheap insurance for the OS-teardown window,
                // not as the fix for anything measured.
                try
                {
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The caller gave up on the whole native-mode launch — nothing left to wait for.
                }
            }
        }

        private static Task WaitForExitAsync(Process process)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => tcs.TrySetResult(true);
            if (process.HasExited) tcs.TrySetResult(true);
            return tcs.Task;
        }

        public async Task StartAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AcpSession));

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = AcpCommandBuilder.GetFileName(_options),
                Arguments = AcpCommandBuilder.GetArguments(_options),
                WorkingDirectory = workingDirectory ?? string.Empty
            };

            foreach (KeyValuePair<string, string> pair in _options.EnvironmentOverrides)
            {
                hostOptions.EnvironmentOverrides[pair.Key] = pair.Value;
            }

            Log($"launching: file='{hostOptions.FileName}' args='{hostOptions.Arguments}' " +
                $"cwd='{hostOptions.WorkingDirectory}' priming={_options.AnswerFirstRunPromptWithNo}");

            if (_options.AnswerFirstRunPromptWithNo)
            {
                await PrimeFirstRunPromptAsync(hostOptions, _options.DiagnosticLog == null
                    ? (Action<string>)null
                    : Log, cancellationToken);
            }

            _host = new JsonLineProcessHost(hostOptions);
            _host.LineReceived += OnLineReceived;
            _host.ErrorLineReceived += OnErrorLineReceived;
            _host.Exited += OnHostExited;

            await _host.StartAsync(cancellationToken);
            Log($"process started: pid={_host.ProcessId}");

            JToken init;
            try
            {
                init = await RequestAsync("initialize", new JObject
                {
                    ["protocolVersion"] = 1,
                    ["clientCapabilities"] = new JObject
                    {
                        // Declared false on purpose: the agent then reads and writes files itself instead
                        // of asking us to, which keeps this adapter free of file-system duties.
                        ["fs"] = new JObject { ["readTextFile"] = false, ["writeTextFile"] = false }
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                // The handshake is where a CLI that cannot run at all shows up, and the IOException the
                // write throws ("closed its input pipe") names none of the reasons why. Give the stderr
                // tail and the exit code, which is what actually identifies the failure. The drain wait
                // matters: the write fails the instant the pipe breaks, which is *before* the stderr
                // pump has surfaced the dying process's last words.
                await _host.WaitForOutputDrainAsync(1000);

                throw new InvalidOperationException(DescribeStartFailure(ex), ex);
            }

            string agentName = init?["agentInfo"]?["title"]?.ToString();
            if (string.IsNullOrEmpty(agentName))
            {
                agentName = init?["agentInfo"]?["name"]?.ToString() ?? _options.DisplayName;
            }
            string agentVersion = init?["agentInfo"]?["version"]?.ToString() ?? string.Empty;
            Model = string.IsNullOrEmpty(agentVersion) ? agentName : agentName + " " + agentVersion;

            var newSessionParams = new JObject
            {
                ["cwd"] = workingDirectory ?? string.Empty,
                ["mcpServers"] = new JArray()
            };

            JToken session = await RequestAsync("session/new", newSessionParams, cancellationToken);
            SessionId = session?["sessionId"]?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(SessionId))
            {
                throw new InvalidOperationException($"{_options.DisplayName} did not return a session id.");
            }

            await TrySetModeAsync(session, cancellationToken);
            await TrySetModelAsync(session, cancellationToken);

            Raise(AgentEvent.SessionStarted(SessionId, Model, ReadCommandNames(session), null));
        }

        /// <summary>
        /// Selects the configured session mode, when the agent offers one by that id.
        /// <para>
        /// Modes — not the permission channel — are how these agents actually gate edits: Devin
        /// defaults to <c>accept-edits</c> and in <c>ask</c> mode simply refuses to write rather than
        /// asking. A failure here is never fatal; the agent keeps its own default.
        /// </para>
        /// </summary>
        private async Task TrySetModeAsync(JToken session, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.ModeId))
            {
                return;
            }

            var available = session?["modes"]?["availableModes"] as JArray;
            if (available != null)
            {
                bool offered = false;
                foreach (JToken mode in available)
                {
                    if (string.Equals(mode?["id"]?.ToString(), _options.ModeId, StringComparison.OrdinalIgnoreCase))
                    {
                        offered = true;
                        break;
                    }
                }

                if (!offered)
                {
                    Debug.WriteLine($"ACP: {_options.DisplayName} does not offer mode '{_options.ModeId}'; keeping its default.");
                    return;
                }
            }

            try
            {
                await RequestAsync("session/set_mode", new JObject
                {
                    ["sessionId"] = SessionId,
                    ["modeId"] = _options.ModeId
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ACP: could not set mode '{_options.ModeId}': {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the configured model to the fresh session.
        /// <para>
        /// The model is not a launch flag here: <c>devin acp</c> takes no <c>--model</c>, and the
        /// agent instead publishes a <c>model</c> entry in the <c>configOptions</c> of
        /// <c>session/new</c> that is changed with <c>session/set_config_option</c>. Without this the
        /// picked model was only ever a caption — every native Devin turn ran on the CLI's default.
        /// </para>
        /// </summary>
        private async Task TrySetModelAsync(JToken session, CancellationToken cancellationToken)
        {
            _modelOption = FindModelOption(session?["configOptions"]);

            if (string.IsNullOrWhiteSpace(_options.ModelName))
            {
                return;
            }

            if (_modelOption == null)
            {
                Debug.WriteLine($"ACP: {_options.DisplayName} offers no model picker; keeping its default.");
                return;
            }

            await ApplyModelAsync(_options.ModelName, true, cancellationToken);
        }

        /// <summary>
        /// Switches the model of the running session. Returns false when the agent has no model picker
        /// or does not list this model, so the caller can fall back to restarting the agent.
        /// </summary>
        public async Task<bool> SetModelAsync(string model, CancellationToken cancellationToken)
        {
            if (_disposed || _host == null || string.IsNullOrEmpty(SessionId) || _modelOption == null)
            {
                return false;
            }

            return await ApplyModelAsync(model, false, cancellationToken);
        }

        private async Task<bool> ApplyModelAsync(string model, bool announceMismatch,
            CancellationToken cancellationToken)
        {
            string value = ResolveModelValue(_modelOption, model);

            if (string.IsNullOrEmpty(value))
            {
                Debug.WriteLine($"ACP: {_options.DisplayName} does not offer model '{model}'.");

                // Said out loud on launch: the composer keeps showing the model that was picked, so a
                // silent miss leaves the user reading answers from a model they did not choose.
                if (announceMismatch)
                {
                    Raise(AgentEvent.SessionError(
                        $"{_options.DisplayName} does not offer the model \"{model}\" — it is running on its " +
                        "default model. Pick another one in the model menu (\"Configure Models...\" edits the list)."));
                }

                return false;
            }

            try
            {
                await RequestAsync("session/set_config_option", new JObject
                {
                    ["sessionId"] = SessionId,
                    ["configId"] = _modelOption["id"]?.ToString() ?? "model",
                    ["value"] = value
                }, cancellationToken);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ACP: could not set model '{model}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The model picker out of a <c>configOptions</c> array, or null when the agent publishes none.
        /// Matched on the id first and the category second — Devin and OpenCode both publish one;
        /// Reasonix publishes neither and takes its model as a launch flag instead.
        /// </summary>
        public static JToken FindModelOption(JToken configOptions)
        {
            var options = configOptions as JArray;
            if (options == null) return null;

            foreach (JToken option in options)
            {
                if (string.Equals(option?["id"]?.ToString(), "model", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(option?["category"]?.ToString(), "model", StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }

            return null;
        }

        /// <summary>
        /// Turns a configured model into the value the agent expects, or empty when it lists no such
        /// model. Both the id and the display name are accepted, and everything that is not a letter or
        /// a digit is ignored while comparing, so the "Claude Opus 4.8 High" stored by the model menu
        /// matches the "claude-opus-4-8-high" the protocol wants.
        /// </summary>
        public static string ResolveModelValue(JToken modelOption, string model)
        {
            if (modelOption == null || string.IsNullOrWhiteSpace(model)) return string.Empty;

            string wanted = NormalizeModelKey(model);
            var candidates = modelOption["options"] as JArray;
            if (candidates == null) return string.Empty;

            foreach (JToken candidate in candidates)
            {
                string value = candidate?["value"]?.ToString();
                if (string.IsNullOrEmpty(value)) continue;

                if (NormalizeModelKey(value) == wanted ||
                    NormalizeModelKey(candidate["name"]?.ToString()) == wanted)
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string NormalizeModelKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AcpSession));
            if (_host == null || string.IsNullOrEmpty(SessionId))
            {
                throw new InvalidOperationException("The ACP session is not started.");
            }
            if (string.IsNullOrWhiteSpace(text)) return;

            Interlocked.Exchange(ref _busy, 1);

            try
            {
                JToken result = await RequestAsync("session/prompt", new JObject
                {
                    ["sessionId"] = SessionId,
                    ["prompt"] = new JArray(new JObject { ["type"] = "text", ["text"] = text })
                }, cancellationToken);

                CompleteTurn(result);
            }
            catch (Exception ex)
            {
                Raise(AgentEvent.SessionError($"The turn failed: {ex.Message}"));
                Raise(AgentEvent.TurnCompleted(null, null, false));
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private void CompleteTurn(JToken result)
        {
            string stopReason = result?["stopReason"]?.ToString() ?? string.Empty;

            AgentUsage usage = null;
            JToken usageToken = result?["usage"];
            if (usageToken != null)
            {
                usage = new AgentUsage
                {
                    InputTokens = ReadInt(usageToken["inputTokens"]),
                    OutputTokens = ReadInt(usageToken["outputTokens"]),
                    CacheReadTokens = ReadInt(usageToken["cachedReadTokens"])
                };
            }

            bool cancelled = string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase);

            // "refusal" and "max_tokens" are ordinary endings the user needs to see; without this
            // line the answer would just stop mid-sentence with no explanation.
            if (string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase))
            {
                Raise(AgentEvent.SessionError("The agent ended the turn with an error."));
            }
            else if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                Raise(AgentEvent.SessionError("The answer was cut short by the model's output limit."));
            }
            else if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                Raise(AgentEvent.SessionError("The agent refused to complete this request."));
            }

            Raise(AgentEvent.TurnCompleted(usage, null, cancelled));
        }

        public async Task InterruptAsync(CancellationToken cancellationToken)
        {
            if (_host == null || !_host.IsRunning || string.IsNullOrEmpty(SessionId))
            {
                return;
            }

            // session/cancel is a notification: the pending session/prompt request is what returns,
            // with stopReason "cancelled". Nothing is awaited here.
            await NotifyAsync("session/cancel", new JObject { ["sessionId"] = SessionId }, cancellationToken);
        }

        #endregion

        #region JSON-RPC plumbing

        private async Task<JToken> RequestAsync(string method, JObject parameters, CancellationToken cancellationToken)
        {
            long id = Interlocked.Increment(ref _nextRequestId);
            var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            var message = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };

            try
            {
                await _host.WriteLineAsync(message.ToString(Formatting.None), cancellationToken);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            // A turn can legitimately run for many minutes, so there is no timeout here. The session
            // is unblocked instead by the process exiting, which faults every pending request.
            using (cancellationToken.Register(() => completion.TrySetCanceled()))
            {
                return await completion.Task;
            }
        }

        private Task NotifyAsync(string method, JObject parameters, CancellationToken cancellationToken)
        {
            var message = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters ?? new JObject()
            };

            return _host.WriteLineAsync(message.ToString(Formatting.None), cancellationToken);
        }

        private void OnLineReceived(object sender, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            JObject message;
            try
            {
                message = JObject.Parse(line);
            }
            catch (Exception ex)
            {
                // Some CLIs print a startup banner on stdout before the first JSON-RPC frame.
                // Dropping the line is right: one stray banner must not end the session.
                Debug.WriteLine($"ACP: ignoring non-JSON output ({ex.Message}): {Truncate(line, 200)}");
                return;
            }

            try
            {
                DispatchMessage(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ACP: failed to handle message: {ex}");
            }
        }

        private void DispatchMessage(JObject message)
        {
            string method = message["method"]?.ToString();
            JToken idToken = message["id"];

            if (!string.IsNullOrEmpty(method))
            {
                if (idToken == null || idToken.Type == JTokenType.Null)
                {
                    HandleNotification(method, message["params"]);
                }
                else
                {
                    HandleServerRequest(method, idToken, message["params"]);
                }
                return;
            }

            // Response to one of our requests.
            if (idToken == null) return;

            long id;
            if (!long.TryParse(idToken.ToString(), out id)) return;

            TaskCompletionSource<JToken> completion;
            if (!_pending.TryRemove(id, out completion)) return;

            JToken error = message["error"];
            if (error != null && error.Type != JTokenType.Null)
            {
                string errorMessage = error["message"]?.ToString() ?? error.ToString(Formatting.None);
                completion.TrySetException(new InvalidOperationException(errorMessage));
            }
            else
            {
                completion.TrySetResult(message["result"]);
            }
        }

        private void HandleNotification(string method, JToken parameters)
        {
            if (!string.Equals(method, "session/update", StringComparison.Ordinal))
            {
                return;
            }

            JToken update = parameters?["update"];
            string kind = update?["sessionUpdate"]?.ToString();
            if (string.IsNullOrEmpty(kind)) return;

            switch (kind)
            {
                case "agent_message_chunk":
                    Raise(AgentEvent.AssistantText(ReadContentText(update["content"])));
                    break;

                case "agent_thought_chunk":
                    Raise(AgentEvent.Thinking(ReadContentText(update["content"])));
                    break;

                case "tool_call":
                    HandleToolCall(update);
                    break;

                case "tool_call_update":
                    HandleToolCallUpdate(update);
                    break;

                default:
                    // plan / available_commands_update / current_mode_update / usage_update and
                    // vendor extensions: inventory chatter with no place in the transcript.
                    break;
            }
        }

        private void HandleToolCall(JToken update)
        {
            string id = update["toolCallId"]?.ToString() ?? string.Empty;

            // ACP names the call in "title" ("Wrote .\acp.txt"); the raw tool name only shows up in
            // vendor metadata, so the title is the honest label to show.
            string title = update["title"]?.ToString();
            if (string.IsNullOrEmpty(title))
            {
                title = update["kind"]?.ToString() ?? "tool";
            }

            if (!string.IsNullOrEmpty(id))
            {
                _toolNames[id] = title;
            }

            JToken rawInput = update["rawInput"];
            string inputJson = rawInput != null && rawInput.Type != JTokenType.Null
                ? rawInput.ToString(Formatting.Indented)
                : string.Empty;

            Raise(AgentEvent.ToolCallStarted(id, title, inputJson));

            // Some agents deliver a completed call in one notification, with no follow-up update.
            string status = update["status"]?.ToString();
            if (IsTerminalStatus(status))
            {
                RaiseToolCompletion(id, update, status);
            }
        }

        private void HandleToolCallUpdate(JToken update)
        {
            string status = update["status"]?.ToString();
            if (!IsTerminalStatus(status))
            {
                // pending / in_progress are progress ticks; the card is already on screen.
                return;
            }

            string id = update["toolCallId"]?.ToString() ?? string.Empty;
            RaiseToolCompletion(id, update, status);
        }

        private void RaiseToolCompletion(string id, JToken update, string status)
        {
            bool isError = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
            string text = ReadToolContent(update["content"]);

            _toolNames.TryRemove(id, out _);
            Raise(AgentEvent.ToolCallCompleted(id, text, isError));
        }

        private static bool IsTerminalStatus(string status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Answers a request the agent sent us. Every request must get a reply — an unanswered one
        /// leaves the agent blocked forever, which looks exactly like a hung CLI.
        /// </summary>
        private void HandleServerRequest(string method, JToken idToken, JToken parameters)
        {
            if (string.Equals(method, "session/request_permission", StringComparison.Ordinal))
            {
                HandlePermissionRequest(idToken, parameters);
                return;
            }

            // Unknown and vendor-prefixed requests (e.g. "_cognition.ai/agent_stopped") are
            // acknowledged with an empty result. Verified against Devin: replying this way keeps the
            // turn moving, whereas a "method not found" error risks aborting it over a notification
            // we never needed.
            RespondAsync(idToken, new JObject()).Forget();
        }

        private void HandlePermissionRequest(JToken idToken, JToken parameters)
        {
            var options = new List<AgentPermissionOption>();
            var optionsArray = parameters?["options"] as JArray;
            if (optionsArray != null)
            {
                foreach (JToken option in optionsArray)
                {
                    options.Add(new AgentPermissionOption
                    {
                        OptionId = option?["optionId"]?.ToString() ?? string.Empty,
                        Name = option?["name"]?.ToString() ?? string.Empty,
                        Kind = option?["kind"]?.ToString() ?? string.Empty
                    });
                }
            }

            JToken toolCall = parameters?["toolCall"];
            string toolCallId = toolCall?["toolCallId"]?.ToString() ?? string.Empty;
            string title = toolCall?["title"]?.ToString() ?? string.Empty;

            string key = Guid.NewGuid().ToString("N");
            var request = new AgentPermissionRequest(optionId => AnswerPermission(key, idToken, optionId))
            {
                ToolCallId = toolCallId,
                ToolName = title,
                Description = string.IsNullOrEmpty(title) ? "The agent is asking for permission." : title,
                Options = options
            };

            _openPermissions[key] = request;

            if (options.Count == 0)
            {
                // Nothing to choose from — refuse rather than show an empty dialog.
                request.Cancel();
                return;
            }

            Raise(AgentEvent.PermissionRequested(request));
        }

        private void AnswerPermission(string key, JToken idToken, string optionId)
        {
            _openPermissions.TryRemove(key, out _);

            JObject outcome = string.IsNullOrEmpty(optionId)
                ? new JObject { ["outcome"] = new JObject { ["outcome"] = "cancelled" } }
                : new JObject
                {
                    ["outcome"] = new JObject
                    {
                        ["outcome"] = "selected",
                        ["optionId"] = optionId
                    }
                };

            RespondAsync(idToken, outcome).Forget();
        }

        private Task RespondAsync(JToken idToken, JObject result)
        {
            JsonLineProcessHost host = _host;
            if (host == null || !host.IsRunning)
            {
                return Task.FromResult(0);
            }

            var message = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken,
                ["result"] = result ?? new JObject()
            };

            return host.WriteLineAsync(message.ToString(Formatting.None), CancellationToken.None);
        }

        private void OnErrorLineReceived(object sender, string line)
        {
            // stderr is the agents' human-facing log channel (Reasonix prints its MCP handshake
            // there), never protocol. Recorded for diagnosis, never shown as an agent error.
            Debug.WriteLine($"ACP stderr: {Truncate(line, 400)}");

            if (string.IsNullOrWhiteSpace(line)) return;

            lock (_stderrTail)
            {
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
            }
        }

        /// <summary>
        /// Builds the message for a handshake that never completed, folding in what the process
        /// actually did — exit code and stderr tail — instead of the bare pipe-level IOException.
        /// </summary>
        private string DescribeStartFailure(Exception cause)
        {
            var detail = new StringBuilder();
            detail.Append($"{_options.DisplayName} did not complete the ACP handshake: ")
                  .Append(cause?.Message ?? "unknown error");

            JsonLineProcessHost host = _host;
            try
            {
                if (host != null && !host.IsRunning)
                {
                    // WaitForExitAsync is not available here; the host already saw the exit, and its
                    // pumps need a moment to surface the last stderr line the CLI wrote on its way out.
                    detail.Append($" (process {host.ProcessId} is no longer running)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ACP: could not read process state: {ex.Message}");
            }

            string stderr = GetStderrTail();
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                detail.Append(Environment.NewLine).Append("stderr: ").Append(stderr);
            }

            string message = detail.ToString();
            Log("start FAILED: " + message);

            return message;
        }

        /// <summary>The recent stderr lines as one block, or empty when the agent said nothing.</summary>
        private string GetStderrTail()
        {
            lock (_stderrTail)
            {
                if (_stderrTail.Count == 0) return string.Empty;

                return Truncate(string.Join(Environment.NewLine, _stderrTail.ToArray()), StderrExcerptLength);
            }
        }

        /// <summary>Reports to the diagnostic sink, if the caller wired one up. Never throws.</summary>
        private void Log(string message)
        {
            Action<string> sink = _options.DiagnosticLog;
            if (sink == null) return;

            try
            {
                sink($"ACP[{_options.DisplayName}]: {message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ACP: diagnostic sink threw: {ex.Message}");
            }
        }

        private void OnHostExited(object sender, int exitCode)
        {
            Log($"process exited: code={exitCode}, pendingRequests={_pending.Count}" +
                (string.IsNullOrWhiteSpace(GetStderrTail()) ? "" : ", stderr=" + GetStderrTail()));

            foreach (KeyValuePair<long, TaskCompletionSource<JToken>> entry in _pending)
            {
                entry.Value.TrySetException(new InvalidOperationException(
                    $"{_options.DisplayName} exited with code {exitCode} before answering."));
            }
            _pending.Clear();

            CancelOpenPermissions();

            if (_disposed) return;

            Interlocked.Exchange(ref _busy, 0);
            Raise(AgentEvent.SessionError($"{_options.DisplayName} exited with code {exitCode}."));
        }

        private void CancelOpenPermissions()
        {
            foreach (KeyValuePair<string, AgentPermissionRequest> entry in _openPermissions)
            {
                try
                {
                    entry.Value.Cancel();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ACP: failed to cancel a pending permission: {ex.Message}");
                }
            }
            _openPermissions.Clear();
        }

        #endregion

        #region Helpers

        private static IReadOnlyList<string> ReadCommandNames(JToken session)
        {
            var array = session?["availableCommands"] as JArray;
            if (array == null) return null;

            var names = new List<string>();
            foreach (JToken command in array)
            {
                string name = command?["name"]?.ToString();
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        private static string ReadContentText(JToken content)
        {
            if (content == null) return string.Empty;

            if (content.Type == JTokenType.String) return content.ToString();

            return content["text"]?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Flattens a tool-call content array into display text. Diff blocks are summarized rather
        /// than dumped: the transcript card is a summary, and the Changes view already shows diffs.
        /// </summary>
        private static string ReadToolContent(JToken content)
        {
            var array = content as JArray;
            if (array == null) return ReadContentText(content);

            var parts = new List<string>();
            foreach (JToken item in array)
            {
                string type = item?["type"]?.ToString();

                if (string.Equals(type, "diff", StringComparison.Ordinal))
                {
                    string path = item["path"]?.ToString();
                    parts.Add(string.IsNullOrEmpty(path) ? "(diff)" : "Edited " + path);
                }
                else if (string.Equals(type, "content", StringComparison.Ordinal))
                {
                    parts.Add(ReadContentText(item["content"]));
                }
                else
                {
                    parts.Add(ReadContentText(item));
                }
            }

            return string.Join(Environment.NewLine, parts.FindAll(p => !string.IsNullOrEmpty(p)));
        }

        private static int ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;

            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
            return value.Substring(0, max) + "...";
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
                // A throwing subscriber must never take the reader thread down with it.
                Debug.WriteLine($"ACP: subscriber threw on {agentEvent.Kind}: {ex}");
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelOpenPermissions();

            JsonLineProcessHost host = _host;
            _host = null;

            if (host != null)
            {
                host.LineReceived -= OnLineReceived;
                host.ErrorLineReceived -= OnErrorLineReceived;
                host.Exited -= OnHostExited;

                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ACP: host dispose failed: {ex.Message}");
                }
            }
        }
    }

    internal static class AcpTaskExtensions
    {
        /// <summary>
        /// Marks a task as deliberately unobserved. Writing a reply is fire-and-forget: the caller is
        /// the reader thread, and blocking it on the write would deadlock the moment the pipe fills.
        /// </summary>
        public static void Forget(this Task task)
        {
            if (task == null) return;

#pragma warning disable VSTHRD110 // The continuation is the observation; awaiting it is the very thing being avoided
            task.ContinueWith(
                t => Debug.WriteLine($"ACP: background write failed: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
#pragma warning restore VSTHRD110
        }
    }
}
