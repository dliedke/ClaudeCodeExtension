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
        private long _nextRequestId;
        private int _busy;
        private volatile bool _disposed;

        public AcpSession(AcpSessionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public string SessionId { get; private set; } = string.Empty;

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

            _host = new JsonLineProcessHost(hostOptions);
            _host.LineReceived += OnLineReceived;
            _host.ErrorLineReceived += OnErrorLineReceived;
            _host.Exited += OnHostExited;

            await _host.StartAsync(cancellationToken);

            JToken init = await RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JObject
                {
                    // Declared false on purpose: the agent then reads and writes files itself instead
                    // of asking us to, which keeps this adapter free of file-system duties.
                    ["fs"] = new JObject { ["readTextFile"] = false, ["writeTextFile"] = false }
                }
            }, cancellationToken);

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
        }

        private void OnHostExited(object sender, int exitCode)
        {
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
