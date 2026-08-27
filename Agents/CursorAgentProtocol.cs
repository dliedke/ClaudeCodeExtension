/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Reads Cursor Agent's --output-format stream-json for the one-shot session adapter
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Turns Cursor Agent's JSON stream into <see cref="AgentEvent"/>s.
    /// <para>
    /// Captured live from <c>agent --print --output-format stream-json --stream-partial-output</c>:
    /// <c>system/init</c> (session id, model), <c>thinking</c> deltas, <c>assistant</c> chunks,
    /// <c>tool_call</c> started/completed and a final <c>result</c> with usage.
    /// </para>
    /// </summary>
    public class CursorAgentProtocol : IOneShotTurnProtocol
    {
        /// <summary>Per-turn bookkeeping kept on the sink; see <see cref="OneShotTurnSink.ProtocolState"/>.</summary>
        private class TurnState
        {
            /// <summary>True once a partial chunk has been seen, which makes the recap redundant.</summary>
            public bool SawPartialText;
        }

        public string BuildArguments(OneShotSessionOptions options, string resumeSessionId)
        {
            var arguments = new StringBuilder();

            // The prompt is not on the command line: it is written to stdin, so quoting and length
            // stop being a concern.
            arguments.Append("--print --output-format stream-json --stream-partial-output");

            // Without --trust the CLI stops on a workspace-trust prompt that headless mode can never
            // answer. The directory is the solution the user already opened in Visual Studio, and
            // trusting it does not by itself let the agent run anything — that is what --force does.
            arguments.Append(" --trust");

            if (options.SkipApprovals)
            {
                arguments.Append(" --force");
            }

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                arguments.Append(" --model ").Append(options.Model);
            }

            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                arguments.Append(" --resume ").Append(resumeSessionId);
            }

            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
            {
                arguments.Append(' ').Append(options.ExtraArguments.Trim());
            }

            return arguments.ToString();
        }

        public void HandleLine(string line, OneShotTurnSink sink)
        {
            if (string.IsNullOrWhiteSpace(line) || sink == null) return;

            JObject message;
            try
            {
                message = JObject.Parse(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cursor: ignoring non-JSON output ({ex.Message})");
                return;
            }

            var state = sink.ProtocolState as TurnState;
            if (state == null)
            {
                state = new TurnState();
                sink.ProtocolState = state;
            }

            string type = message["type"]?.ToString();

            switch (type)
            {
                case "system":
                    HandleSystem(message, sink);
                    break;

                case "thinking":
                    if (string.Equals(message["subtype"]?.ToString(), "delta", StringComparison.Ordinal))
                    {
                        sink.Emit(AgentEvent.Thinking(message["text"]?.ToString()));
                    }
                    break;

                case "assistant":
                    HandleAssistant(message, sink, state);
                    break;

                case "tool_call":
                    HandleToolCall(message, sink);
                    break;

                case "result":
                    HandleResult(message, sink);
                    break;

                case "user":
                    // The CLI echoes the prompt back; the transcript already shows it.
                    break;
            }
        }

        private static void HandleSystem(JObject message, OneShotTurnSink sink)
        {
            if (!string.Equals(message["subtype"]?.ToString(), "init", StringComparison.Ordinal))
            {
                return;
            }

            string sessionId = message["session_id"]?.ToString() ?? string.Empty;
            string model = message["model"]?.ToString() ?? string.Empty;

            sink.SessionId = sessionId;
            sink.Model = model;
            sink.Emit(AgentEvent.SessionStarted(sessionId, model, null, null));
        }

        /// <summary>
        /// Appends streamed text, skipping the recap.
        /// <para>
        /// Measured: with <c>--stream-partial-output</c> the CLI sends the answer twice — once as
        /// chunks carrying <c>timestamp_ms</c>, then once whole without it. Appending both would print
        /// every answer twice, so the recap is dropped — unless no chunk ever arrived, which is what
        /// happens on a resumed turn, where the recap is the only copy of the answer.
        /// </para>
        /// </summary>
        private static void HandleAssistant(JObject message, OneShotTurnSink sink, TurnState state)
        {
            bool isPartial = message["timestamp_ms"] != null;

            if (isPartial)
            {
                state.SawPartialText = true;
            }
            else if (state.SawPartialText)
            {
                return;
            }

            var content = message["message"]?["content"] as JArray;
            if (content == null) return;

            foreach (JToken block in content)
            {
                if (string.Equals(block?["type"]?.ToString(), "text", StringComparison.Ordinal))
                {
                    sink.Emit(AgentEvent.AssistantText(block["text"]?.ToString()));
                }
            }
        }

        private static void HandleToolCall(JObject message, OneShotTurnSink sink)
        {
            string id = message["call_id"]?.ToString() ?? string.Empty;
            var toolCall = message["tool_call"] as JObject;
            if (toolCall == null) return;

            // The tool is identified by the property name — "editToolCall", "readToolCall" — rather
            // than by a field of its own.
            JProperty kindProperty = null;
            foreach (JProperty property in toolCall.Properties())
            {
                if (property.Name.EndsWith("ToolCall", StringComparison.Ordinal))
                {
                    kindProperty = property;
                    break;
                }
            }

            string subtype = message["subtype"]?.ToString();
            string name = kindProperty != null ? FormatToolName(kindProperty.Name) : "Tool";
            var payload = kindProperty?.Value as JObject;

            if (string.Equals(subtype, "started", StringComparison.Ordinal))
            {
                JToken args = payload?["args"];
                sink.Emit(AgentEvent.ToolCallStarted(id, name,
                    args != null ? args.ToString(Formatting.Indented) : string.Empty));
            }
            else if (string.Equals(subtype, "completed", StringComparison.Ordinal))
            {
                JToken result = payload?["result"];
                bool isError = result?["error"] != null;

                sink.Emit(AgentEvent.ToolCallCompleted(id,
                    result != null ? result.ToString(Formatting.Indented) : string.Empty, isError));
            }
        }

        private static void HandleResult(JObject message, OneShotTurnSink sink)
        {
            sink.TurnEnded = true;

            JToken usage = message["usage"];
            if (usage != null)
            {
                sink.Usage = new AgentUsage
                {
                    InputTokens = ReadInt(usage["inputTokens"]),
                    OutputTokens = ReadInt(usage["outputTokens"]),
                    CacheReadTokens = ReadInt(usage["cacheReadTokens"]),
                    CacheCreationTokens = ReadInt(usage["cacheWriteTokens"]),
                    DurationMs = ReadInt(message["duration_ms"])
                };
            }

            if (message["is_error"] != null && message["is_error"].Type == JTokenType.Boolean &&
                message["is_error"].Value<bool>())
            {
                string text = message["result"]?.ToString();
                sink.Emit(AgentEvent.SessionError(string.IsNullOrEmpty(text) ? "The turn failed." : text));
            }
        }

        /// <summary>"editToolCall" → "Edit".</summary>
        private static string FormatToolName(string propertyName)
        {
            string name = propertyName.Substring(0, propertyName.Length - "ToolCall".Length);
            if (name.Length == 0) return "Tool";

            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private static int ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;

            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }
    }
}
