/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Agent session over PI's `--mode rpc` protocol
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Launch settings for PI's RPC mode.
    /// </summary>
    public class PiSessionOptions
    {
        /// <summary>Executable, unquoted. May be a bare name resolved through PATH.</summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>Model pattern to request, or empty for whatever PI is configured to use.</summary>
        public string Model { get; set; } = string.Empty;

        public string DisplayName { get; set; } = "PI";

        public IDictionary<string, string> EnvironmentOverrides { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drives PI over the protocol it exposes with <c>--mode rpc</c>.
    /// <para>
    /// It is neither ACP nor Claude's stream-json: commands go in as JSON lines keyed by a <c>type</c>
    /// field (<c>prompt</c>, <c>abort</c>, <c>get_state</c>), and what comes back is the agent's own
    /// event stream — <c>agent_start</c>, <c>turn_start</c>, <c>message_update</c>, <c>message_end</c>,
    /// <c>turn_end</c>, <c>agent_end</c>, <c>agent_settled</c>. The process is persistent, so the
    /// conversation costs nothing to keep alive between turns.
    /// </para>
    /// <para>
    /// The envelope was captured live from the installed CLI; the streaming payloads
    /// (<c>text_delta</c>, <c>thinking_delta</c>) come from the protocol types PI ships, because the
    /// machine it was written on had no API key configured for PI's provider and no turn could produce
    /// text.
    /// </para>
    /// </summary>
    public class PiRpcSession : IAgentSession
    {
        private readonly PiSessionOptions _options;

        private JsonLineProcessHost _host;
        private TaskCompletionSource<bool> _turn;
        private TaskCompletionSource<JToken> _pendingState;

        /// <summary>Usage accumulated across the assistant messages of the turn in flight.</summary>
        private AgentUsage _turnUsage;

        /// <summary>
        /// True once a text delta has been seen for the message being received. Without it there is no
        /// way to tell a streaming provider from one that only delivers the finished message, and
        /// rendering both would print every answer twice.
        /// </summary>
        private bool _sawTextDelta;

        private bool _sawThinkingDelta;
        private long _nextCommandId;
        private int _busy;
        private volatile bool _disposed;
        private volatile bool _interrupted;

        public PiRpcSession(PiSessionOptions options)
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

        public async Task StartAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PiRpcSession));
            if (_host != null) throw new InvalidOperationException("Session already started.");

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = PiCommandBuilder.GetFileName(_options),
                Arguments = PiCommandBuilder.GetArguments(_options),
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

            // PI has no handshake, so the session's identity has to be asked for. A failure here is not
            // fatal: it only costs the model name in the header.
            try
            {
                JToken state = await RequestStateAsync(cancellationToken);
                if (state != null)
                {
                    SessionId = state["sessionId"]?.ToString() ?? string.Empty;
                    Model = state["model"]?["name"]?.ToString()
                        ?? state["model"]?["id"]?.ToString()
                        ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PI: could not read session state: {ex.Message}");
            }

            Raise(AgentEvent.SessionStarted(SessionId, Model, null, null));
        }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PiRpcSession));
            if (_host == null) throw new InvalidOperationException("Session is not started.");
            if (string.IsNullOrWhiteSpace(text)) return;

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("A turn is already running.");
            }

            _interrupted = false;
            _turnUsage = new AgentUsage();
            _sawTextDelta = false;
            _sawThinkingDelta = false;

            var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _turn = turn;

            try
            {
                var command = new JObject
                {
                    ["id"] = NextCommandId(),
                    ["type"] = "prompt",
                    ["message"] = text
                };

                await _host.WriteLineAsync(JsonConvert.SerializeObject(command), cancellationToken);

                // The command is acknowledged at once; the turn is over only when the agent says so.
                await turn.Task;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PI: turn failed: {ex}");
                Raise(AgentEvent.SessionError($"The turn failed: {ex.Message}"));
                Raise(AgentEvent.TurnCompleted(_turnUsage, null, _interrupted));
                throw;
            }
            finally
            {
                _turn = null;
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        public async Task InterruptAsync(CancellationToken cancellationToken)
        {
            JsonLineProcessHost host = _host;
            if (host == null || !host.IsRunning || !IsBusy)
            {
                return;
            }

            _interrupted = true;

            try
            {
                var command = new JObject
                {
                    ["id"] = NextCommandId(),
                    ["type"] = "abort"
                };

                await host.WriteLineAsync(JsonConvert.SerializeObject(command), cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PI: interrupt failed: {ex.Message}");
            }
        }

        private string NextCommandId()
        {
            return Interlocked.Increment(ref _nextCommandId).ToString(CultureInfo.InvariantCulture);
        }

        private async Task<JToken> RequestStateAsync(CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingState = pending;

            var command = new JObject
            {
                ["id"] = NextCommandId(),
                ["type"] = "get_state"
            };

            await _host.WriteLineAsync(JsonConvert.SerializeObject(command), cancellationToken);

            // Bounded, unlike a turn: this is a local query that either answers immediately or is not
            // supported, and startup must not hang on it.
            Task completed = await Task.WhenAny(pending.Task, Task.Delay(5000, cancellationToken));
            if (!ReferenceEquals(completed, pending.Task))
            {
                return null;
            }

            return await pending.Task;
        }

        private void OnLineReceived(object sender, string line)
        {
            try
            {
                JObject message = JObject.Parse(line);
                Dispatch(message);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"PI: ignoring non-JSON output ({ex.Message})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PI: failed to handle line: {ex}");
            }
        }

        private void OnErrorLineReceived(object sender, string line)
        {
            Debug.WriteLine($"PI stderr: {line}");
        }

        private void Dispatch(JObject message)
        {
            string type = message["type"]?.ToString();

            switch (type)
            {
                case "response":
                    HandleResponse(message);
                    break;

                case "message_update":
                    HandleMessageUpdate(message["assistantMessageEvent"]);
                    break;

                case "message_end":
                    HandleMessageEnd(message["message"]);
                    break;

                case "tool_execution_start":
                    Raise(AgentEvent.ToolCallStarted(
                        message["toolCallId"]?.ToString(),
                        message["toolName"]?.ToString(),
                        Describe(message["args"])));
                    break;

                case "tool_execution_end":
                    Raise(AgentEvent.ToolCallCompleted(
                        message["toolCallId"]?.ToString(),
                        Describe(message["result"]),
                        message["isError"] != null && message["isError"].Type == JTokenType.Boolean &&
                            message["isError"].Value<bool>()));
                    break;

                case "agent_end":
                    // An auto-retry keeps the run alive, so this is only the end when PI says it is.
                    if (message["willRetry"] == null || message["willRetry"].Type != JTokenType.Boolean ||
                        !message["willRetry"].Value<bool>())
                    {
                        CompleteTurn();
                    }
                    break;

                case "agent_settled":
                    // Safety net: whatever happened, the agent is idle again.
                    CompleteTurn();
                    break;

                case "auto_retry_start":
                    Raise(AgentEvent.SessionError(
                        $"Retrying ({message["attempt"]}/{message["maxAttempts"]}): {message["errorMessage"]}"));
                    break;
            }
        }

        private void HandleResponse(JObject message)
        {
            bool success = message["success"] != null && message["success"].Type == JTokenType.Boolean &&
                message["success"].Value<bool>();

            if (string.Equals(message["command"]?.ToString(), "get_state", StringComparison.Ordinal))
            {
                TaskCompletionSource<JToken> pending = _pendingState;
                _pendingState = null;
                pending?.TrySetResult(success ? message["data"] : null);
                return;
            }

            if (!success)
            {
                Raise(AgentEvent.SessionError(message["error"]?.ToString() ?? "The command was refused."));

                // A refused prompt produces no agent events at all, so nothing else would ever end the turn.
                if (string.Equals(message["command"]?.ToString(), "prompt", StringComparison.Ordinal))
                {
                    CompleteTurn();
                }
            }
        }

        private void HandleMessageUpdate(JToken update)
        {
            if (update == null) return;

            switch (update["type"]?.ToString())
            {
                case "text_delta":
                    _sawTextDelta = true;
                    Raise(AgentEvent.AssistantText(update["delta"]?.ToString()));
                    break;

                case "thinking_delta":
                    _sawThinkingDelta = true;
                    Raise(AgentEvent.Thinking(update["delta"]?.ToString()));
                    break;
            }
        }

        /// <summary>
        /// Reads a finished message: its usage always, its text only when nothing was streamed.
        /// </summary>
        private void HandleMessageEnd(JToken agentMessage)
        {
            if (agentMessage == null) return;
            if (!string.Equals(agentMessage["role"]?.ToString(), "assistant", StringComparison.Ordinal))
            {
                // PI echoes the user message back; the transcript already shows it.
                return;
            }

            AccumulateUsage(agentMessage["usage"]);

            string errorMessage = agentMessage["errorMessage"]?.ToString();
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Raise(AgentEvent.SessionError(errorMessage));
            }

            var content = agentMessage["content"] as JArray;
            if (content == null) return;

            foreach (JToken block in content)
            {
                string blockType = block?["type"]?.ToString();

                if (string.Equals(blockType, "text", StringComparison.Ordinal) && !_sawTextDelta)
                {
                    Raise(AgentEvent.AssistantText(block["text"]?.ToString()));
                }
                else if (string.Equals(blockType, "thinking", StringComparison.Ordinal) && !_sawThinkingDelta)
                {
                    Raise(AgentEvent.Thinking(block["thinking"]?.ToString() ?? block["text"]?.ToString()));
                }
            }

            // Each assistant message streams on its own; a turn with tool calls has several.
            _sawTextDelta = false;
            _sawThinkingDelta = false;
        }

        private void AccumulateUsage(JToken usage)
        {
            if (usage == null) return;

            AgentUsage total = _turnUsage;
            if (total == null) return;

            total.InputTokens += ReadInt(usage["input"]);
            total.OutputTokens += ReadInt(usage["output"]);
            total.CacheReadTokens += ReadInt(usage["cacheRead"]);
            total.CacheCreationTokens += ReadInt(usage["cacheWrite"]);
            total.CostUsd += ReadDouble(usage["cost"]?["total"]);
        }

        private void CompleteTurn()
        {
            TaskCompletionSource<bool> turn = _turn;
            if (turn == null || turn.Task.IsCompleted) return;

            Raise(AgentEvent.TurnCompleted(_turnUsage, null, _interrupted));
            turn.TrySetResult(true);
        }

        private void OnHostExited(object sender, int exitCode)
        {
            TaskCompletionSource<JToken> pendingState = _pendingState;
            _pendingState = null;
            pendingState?.TrySetResult(null);

            if (!_disposed)
            {
                Raise(AgentEvent.SessionError($"{_options.DisplayName} exited with code {exitCode}."));
            }

            TaskCompletionSource<bool> turn = _turn;
            if (turn != null && !turn.Task.IsCompleted)
            {
                Raise(AgentEvent.TurnCompleted(_turnUsage, null, _interrupted));
                turn.TrySetResult(false);
            }
        }

        private static string Describe(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token.Type == JTokenType.String) return token.ToString();

            return token.ToString(Formatting.Indented);
        }

        private static int ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;

            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static double ReadDouble(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;

            double value;
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
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
                Debug.WriteLine($"PI: subscriber threw on {agentEvent.Kind}: {ex}");
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
                host.LineReceived -= OnLineReceived;
                host.ErrorLineReceived -= OnErrorLineReceived;
                host.Exited -= OnHostExited;

                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PI: dispose failed: {ex.Message}");
                }
            }

            TaskCompletionSource<bool> turn = _turn;
            turn?.TrySetResult(false);

            TaskCompletionSource<JToken> pendingState = _pendingState;
            pendingState?.TrySetResult(null);
        }
    }

    /// <summary>
    /// Builds the command line that puts PI into RPC mode.
    /// </summary>
    public static class PiCommandBuilder
    {
        public static string GetFileName(PiSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            return IsBatchScript(options.ExecutablePath) ? "cmd.exe" : options.ExecutablePath;
        }

        public static string GetArguments(PiSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            string arguments = "--mode rpc";

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                arguments += " --model " + options.Model;
            }

            if (IsBatchScript(options.ExecutablePath))
            {
                return "/c \"" + options.ExecutablePath + " " + arguments + "\"";
            }

            return arguments;
        }

        private static bool IsBatchScript(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }
    }
}
