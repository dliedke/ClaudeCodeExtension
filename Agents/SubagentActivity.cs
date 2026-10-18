/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Show active subagent count" — reads a Claude Code session transcript (JSONL) line by line and keeps the
 *          set of subagents (Agent/Task tool calls) that have started but not finished yet. No file IO or UI here,
 *          so the start/finish rules are unit-tested.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>One subagent the main conversation launched that has not reported back yet.</summary>
    public sealed class RunningSubagent
    {
        /// <summary>The Agent/Task tool_use id; it ties the start, the result and the task notification together.</summary>
        public string ToolUseId { get; set; }

        /// <summary>subagent_type from the tool input ("Explore", "general-purpose", ...); may be empty.</summary>
        public string AgentType { get; set; }

        /// <summary>The short description the model gave the task; may be empty.</summary>
        public string Description { get; set; }

        /// <summary>When the tool call was written to the transcript (UTC).</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>True once the tool result said the agent went to the background ("async_launched").</summary>
        public bool IsBackground { get; set; }

        /// <summary>The background agent's id from its launch result; newer notifications name only this.</summary>
        public string AgentId { get; set; }
    }

    /// <summary>
    /// Tracks running subagents from the lines of one main-session transcript. Start: an assistant
    /// <c>tool_use</c> named <c>Agent</c> (or the older <c>Task</c>). Finish: its <c>tool_result</c>, unless that
    /// result only says the agent was launched in the background — then the finish is the later
    /// <c>&lt;task-notification&gt;</c> naming the same tool_use id, or (CLI 2.1.296+) only the agent id as its
    /// task id. Interrupted calls also get a
    /// tool_result, so they end the count too. Not thread-safe; feed it from one thread.
    /// </summary>
    public sealed class SubagentActivityTracker
    {
        private static readonly Regex NotificationToolUseId =
            new Regex(@"<tool-use-id>\s*([^<\s]+)\s*</tool-use-id>", RegexOptions.Compiled);

        private static readonly Regex NotificationTaskId =
            new Regex(@"<task-id>\s*([^<\s]+)\s*</task-id>", RegexOptions.Compiled);

        private static readonly Regex NotificationStatus =
            new Regex(@"<status>\s*([^<\s]+)\s*</status>", RegexOptions.Compiled);

        private readonly Dictionary<string, RunningSubagent> _running =
            new Dictionary<string, RunningSubagent>(StringComparer.Ordinal);

        /// <summary>The subagents started and not finished so far.</summary>
        public IReadOnlyCollection<RunningSubagent> Running => _running.Values;

        /// <summary>Feeds one transcript line. Blank, partial or unrelated lines are ignored.</summary>
        public void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Cheap filter first: most lines are plain messages, and a long session is megabytes of JSON.
            bool mayStart = line.IndexOf("\"tool_use\"", StringComparison.Ordinal) >= 0;
            bool mayFinish = line.IndexOf("\"tool_result\"", StringComparison.Ordinal) >= 0;
            bool mayNotify = line.IndexOf("task-notification", StringComparison.Ordinal) >= 0;
            if (!mayStart && !mayFinish && !mayNotify) return;

            JObject entry;
            try
            {
                // DateParseHandling.None keeps "timestamp" a string instead of a locale-formatted DateTime.
                using (var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None })
                {
                    entry = JObject.Load(reader);
                }
            }
            catch { return; }

            // Lines written by a subagent itself (sidechain) never belong to the main conversation's count.
            if (entry.Value<bool?>("isSidechain") == true) return;

            string type = entry.Value<string>("type");
            if (type == "assistant" && mayStart)
            {
                ProcessAssistant(entry);
            }
            else if (type == "user")
            {
                if (mayFinish) ProcessToolResults(entry);
                if (mayNotify) ProcessNotificationText(GetUserText(entry));
            }
            else if (type == "queue-operation" && mayNotify)
            {
                ProcessNotificationText(entry.Value<string>("content"));
            }
        }

        private void ProcessAssistant(JObject entry)
        {
            if (!(entry["message"]?["content"] is JArray content)) return;

            foreach (JToken block in content)
            {
                if (block.Type != JTokenType.Object || block.Value<string>("type") != "tool_use") continue;

                string name = block.Value<string>("name");
                if (name != "Agent" && name != "Task") continue;

                string id = block.Value<string>("id");
                if (string.IsNullOrEmpty(id)) continue;

                JToken input = block["input"];
                _running[id] = new RunningSubagent
                {
                    ToolUseId = id,
                    AgentType = input?.Value<string>("subagent_type") ?? string.Empty,
                    Description = input?.Value<string>("description") ?? string.Empty,
                    StartedUtc = ParseTimestamp(entry.Value<string>("timestamp")),
                };
            }
        }

        private void ProcessToolResults(JObject entry)
        {
            if (!(entry["message"]?["content"] is JArray content)) return;

            foreach (JToken block in content)
            {
                if (block.Type != JTokenType.Object || block.Value<string>("type") != "tool_result") continue;

                string id = block.Value<string>("tool_use_id");
                if (string.IsNullOrEmpty(id) || !_running.TryGetValue(id, out RunningSubagent agent)) continue;

                // A background launch answers at once; the agent is still working until its notification.
                if (IsAsyncLaunch(entry["toolUseResult"], block))
                {
                    agent.IsBackground = true;
                    agent.AgentId = (entry["toolUseResult"] as JObject)?.Value<string>("agentId");
                    continue;
                }

                _running.Remove(id);
            }
        }

        private static bool IsAsyncLaunch(JToken toolUseResult, JToken block)
        {
            if (toolUseResult != null && toolUseResult.Type == JTokenType.Object)
            {
                if (toolUseResult.Value<bool?>("isAsync") == true) return true;
                if (string.Equals(toolUseResult.Value<string>("status"), "async_launched", StringComparison.Ordinal)) return true;
                return false;
            }

            // Older transcripts without the structured result: fall back to the result text.
            string text = block["content"] is JArray parts
                ? string.Concat(TextOf(parts))
                : block.Value<string>("content");
            return text != null && text.IndexOf("Async agent launched", StringComparison.Ordinal) >= 0;
        }

        private void ProcessNotificationText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("<task-notification>", StringComparison.Ordinal) < 0) return;

            Match status = NotificationStatus.Match(text);
            if (status.Success && string.Equals(status.Groups[1].Value, "running", StringComparison.OrdinalIgnoreCase)) return;

            foreach (Match match in NotificationToolUseId.Matches(text))
            {
                _running.Remove(match.Groups[1].Value);
            }

            foreach (Match match in NotificationTaskId.Matches(text))
            {
                string taskId = match.Groups[1].Value;
                string toolUseId = _running.Values.FirstOrDefault(a => string.Equals(a.AgentId, taskId, StringComparison.Ordinal))?.ToolUseId;
                if (toolUseId != null) _running.Remove(toolUseId);
            }
        }

        private static string GetUserText(JObject entry)
        {
            JToken content = entry["message"]?["content"];
            if (content == null) return null;
            if (content.Type == JTokenType.String) return (string)content;
            return content is JArray parts ? string.Concat(TextOf(parts)) : null;
        }

        private static IEnumerable<string> TextOf(JArray parts)
        {
            foreach (JToken part in parts)
            {
                if (part.Type == JTokenType.String) yield return (string)part;
                else if (part.Type == JTokenType.Object && part.Value<string>("type") == "text") yield return part.Value<string>("text");
            }
        }

        private static DateTime ParseTimestamp(string value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                ? parsed
                : DateTime.UtcNow;
        }
    }
}
