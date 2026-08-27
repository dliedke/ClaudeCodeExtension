/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Reads Codex's `exec --json` event stream for the one-shot session adapter
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Turns Codex's <c>exec --json</c> JSONL into <see cref="AgentEvent"/>s.
    /// <para>
    /// The envelope — <c>thread.started</c>, <c>turn.started</c>, <c>turn.completed</c>,
    /// <c>turn.failed</c>, <c>item.started</c>/<c>item.updated</c>/<c>item.completed</c>, <c>error</c>
    /// — and the item types (<c>agent_message</c>, <c>reasoning</c>, <c>command_execution</c>,
    /// <c>file_change</c>, <c>mcp_tool_call</c>, <c>web_search</c>, <c>todo_list</c>) come from the
    /// CLI itself. The first four envelope events plus the error shape were captured live; the item
    /// payloads were read out of the binary's own serializer names, because the CLI's login had
    /// expired and no turn could be completed on this machine. Anything unexpected is ignored rather
    /// than treated as a failure, so a shape that differs slightly degrades instead of breaking.
    /// </para>
    /// </summary>
    public class CodexExecProtocol : IOneShotTurnProtocol
    {
        public string BuildArguments(OneShotSessionOptions options, string resumeSessionId)
        {
            var arguments = new StringBuilder("exec");

            bool isResume = !string.IsNullOrWhiteSpace(resumeSessionId);
            if (isResume)
            {
                arguments.Append(" resume");
            }

            arguments.Append(" --json --skip-git-repo-check");

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                arguments.Append(" -m ").Append(options.Model);
            }

            if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            {
                // The Codex CLI exposes reasoning as a configuration override rather than a
                // top-level flag. The value comes from the extension's closed enum.
                arguments.Append(" -c model_reasoning_effort='")
                    .Append(options.ReasoningEffort)
                    .Append('\'');
            }

            if (options.SkipApprovals)
            {
                arguments.Append(" --dangerously-bypass-approvals-and-sandbox");
            }
            else
            {
                // Set through -c rather than -s: `exec resume` has no --sandbox flag, and a turn must
                // not be allowed to edit under different rules depending on whether it is the first.
                // Single quotes are TOML string syntax and are not touched by Windows argument parsing.
                arguments.Append(" -c sandbox_mode='workspace-write'");
            }

            if (isResume)
            {
                arguments.Append(' ').Append(resumeSessionId);
            }

            // User-supplied flags go before the stdin marker so "-" stays last.
            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
            {
                arguments.Append(' ').Append(options.ExtraArguments.Trim());
            }

            // "-" means "read the prompt from stdin", which keeps quoting and length out of the picture.
            arguments.Append(" -");

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
                Debug.WriteLine($"Codex: ignoring non-JSON output ({ex.Message})");
                return;
            }

            string type = message["type"]?.ToString();

            switch (type)
            {
                case "thread.started":
                    // The thread id is what `exec resume` takes, so every later turn depends on it.
                    sink.SessionId = message["thread_id"]?.ToString() ?? string.Empty;
                    sink.Emit(AgentEvent.SessionStarted(sink.SessionId, string.Empty, null, null));
                    break;

                case "item.started":
                    HandleItem(message["item"], sink, started: true);
                    break;

                case "item.completed":
                    HandleItem(message["item"], sink, started: false);
                    break;

                case "turn.completed":
                    sink.TurnEnded = true;
                    sink.Usage = ReadUsage(message["usage"]);
                    break;

                case "turn.failed":
                    sink.TurnEnded = true;
                    sink.Emit(AgentEvent.SessionError(
                        message["error"]?["message"]?.ToString() ?? "The turn failed."));
                    break;

                case "error":
                    sink.Emit(AgentEvent.SessionError(
                        message["message"]?.ToString() ?? "The agent reported an error."));
                    break;

                case "turn.started":
                case "item.updated":
                    // item.updated carries the same item mid-flight; rendering it too would duplicate
                    // every message, so only the completed form is shown.
                    break;
            }
        }

        private static void HandleItem(JToken item, OneShotTurnSink sink, bool started)
        {
            if (item == null) return;

            string id = item["id"]?.ToString() ?? string.Empty;
            string itemType = item["type"]?.ToString() ?? string.Empty;

            switch (itemType)
            {
                case "agent_message":
                    if (!started)
                    {
                        sink.Emit(AgentEvent.AssistantText(item["text"]?.ToString()));
                    }
                    break;

                case "reasoning":
                    if (!started)
                    {
                        sink.Emit(AgentEvent.Thinking(item["text"]?.ToString()));
                    }
                    break;

                case "command_execution":
                    if (started)
                    {
                        sink.Emit(AgentEvent.ToolCallStarted(id, "Command", item["command"]?.ToString()));
                    }
                    else
                    {
                        int exitCode = ReadInt(item["exit_code"]);
                        sink.Emit(AgentEvent.ToolCallCompleted(id,
                            item["aggregated_output"]?.ToString() ?? string.Empty, exitCode != 0));
                    }
                    break;

                case "file_change":
                    if (started)
                    {
                        sink.Emit(AgentEvent.ToolCallStarted(id, "File change", Describe(item["changes"])));
                    }
                    else
                    {
                        sink.Emit(AgentEvent.ToolCallCompleted(id, Describe(item["changes"]), false));
                    }
                    break;

                case "mcp_tool_call":
                    if (started)
                    {
                        string name = item["server"] + "/" + item["tool"];
                        sink.Emit(AgentEvent.ToolCallStarted(id, name, Describe(item["arguments"])));
                    }
                    else
                    {
                        bool failed = string.Equals(item["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase);
                        sink.Emit(AgentEvent.ToolCallCompleted(id, Describe(item["result"]), failed));
                    }
                    break;

                case "web_search":
                    if (started)
                    {
                        sink.Emit(AgentEvent.ToolCallStarted(id, "Web search", item["query"]?.ToString()));
                    }
                    else
                    {
                        sink.Emit(AgentEvent.ToolCallCompleted(id, string.Empty, false));
                    }
                    break;

                default:
                    // todo_list and anything newer: progress bookkeeping, not transcript material.
                    break;
            }
        }

        private static AgentUsage ReadUsage(JToken usage)
        {
            if (usage == null) return null;

            return new AgentUsage
            {
                InputTokens = ReadInt(usage["input_tokens"]),
                OutputTokens = ReadInt(usage["output_tokens"]),
                CacheReadTokens = ReadInt(usage["cached_input_tokens"]),
                CacheCreationTokens = ReadInt(usage["cache_write_input_tokens"])
            };
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
    }
}
