/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Translates Claude Code stream-json lines into provider-agnostic AgentEvents
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Parses one line of Claude Code's <c>--output-format stream-json</c> output into zero or more
    /// <see cref="AgentEvent"/>.
    /// <para>
    /// Split out from the session so it can be unit-tested against recorded transcripts with no process
    /// involved. Holds a little state (whether deltas have been seen) and so is not thread-safe — the
    /// session feeds it from a single reader loop.
    /// </para>
    /// <para>
    /// The stream says everything twice on purpose: <c>stream_event</c> carries token-level deltas, and
    /// a complete <c>assistant</c> message follows with the same content assembled. Text and thinking
    /// are taken from the deltas (that is what makes the UI stream) and skipped on the assistant
    /// message; tool calls are taken from the assistant message, because the deltas only carry
    /// fragments of the input JSON. Emitting both would duplicate every answer.
    /// </para>
    /// </summary>
    public class ClaudeStreamParser
    {
        private static readonly AgentEvent[] Empty = new AgentEvent[0];

        private readonly bool _expectDeltas;
        private bool _sawDeltas;

        /// <param name="expectDeltas">
        /// True when the CLI was launched with <c>--include-partial-messages</c>. When false, text and
        /// thinking come from the complete assistant messages instead.
        /// </param>
        public ClaudeStreamParser(bool expectDeltas)
        {
            _expectDeltas = expectDeltas;
        }

        /// <summary>Session id seen on the last <c>system/init</c>, if any.</summary>
        public string SessionId { get; private set; } = string.Empty;

        /// <summary>Model reported by the CLI, if any.</summary>
        public string Model { get; private set; } = string.Empty;

        /// <summary>
        /// Writes a <c>control_response</c> back to the CLI. Set by the session; the parser only reads
        /// the stream, so answering a permission prompt has to go back out through the owner.
        /// <para>
        /// Arguments: the request id, and the inner <c>response</c> payload the parser has already
        /// shaped (<c>behavior: allow</c> with the echoed-and-patched tool input, or
        /// <c>behavior: deny</c> with a reason).
        /// </para>
        /// </summary>
        public Action<string, object> ControlResponder { get; set; }

        public IReadOnlyList<AgentEvent> Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return Empty;
            }

            JObject root;
            try
            {
                root = JObject.Parse(line);
            }
            catch (JsonException ex)
            {
                // Not fatal: CLIs occasionally interleave a plain-text warning on stdout, and one bad
                // line must not tear down a working session.
                Debug.WriteLine($"ClaudeStreamParser: ignoring non-JSON line ({ex.Message}): {Truncate(line, 200)}");
                return Empty;
            }

            string type = (string)root["type"] ?? string.Empty;

            switch (type)
            {
                case "system":
                    return ParseSystem(root);
                case "stream_event":
                    return ParseStreamEvent(root);
                case "assistant":
                    return ParseAssistant(root);
                case "user":
                    return ParseUser(root);
                case "rate_limit_event":
                    return ParseRateLimit(root);
                case "result":
                    return ParseResult(root);
                case "control_request":
                    return ParseControlRequest(root);
                default:
                    // control_response, control_cancel_request, and anything a future CLI version adds.
                    return Empty;
            }
        }

        private IReadOnlyList<AgentEvent> ParseSystem(JObject root)
        {
            // "status" and "thinking_tokens" are progress chatter with no UI counterpart yet.
            // "init" arrives once per turn rather than once per session — see AgentEventKind.SessionStarted.
            if ((string)root["subtype"] != "init")
            {
                return Empty;
            }

            SessionId = (string)root["session_id"] ?? string.Empty;
            Model = (string)root["model"] ?? string.Empty;

            return One(AgentEvent.SessionStarted(
                SessionId,
                Model,
                ToStringList(root["tools"]),
                ToStringList(root["slash_commands"])));
        }

        private IReadOnlyList<AgentEvent> ParseStreamEvent(JObject root)
        {
            var inner = root["event"] as JObject;
            if (inner == null)
            {
                return Empty;
            }

            if ((string)inner["type"] != "content_block_delta")
            {
                return Empty;
            }

            var delta = inner["delta"] as JObject;
            if (delta == null)
            {
                return Empty;
            }

            switch ((string)delta["type"])
            {
                case "text_delta":
                    _sawDeltas = true;
                    return One(AgentEvent.AssistantText((string)delta["text"] ?? string.Empty));

                case "thinking_delta":
                    _sawDeltas = true;
                    return One(AgentEvent.Thinking((string)delta["thinking"] ?? string.Empty));

                default:
                    // input_json_delta (tool input arrives complete on the assistant message) and
                    // signature_delta (the thinking-block cryptographic signature, never displayed).
                    return Empty;
            }
        }

        private IReadOnlyList<AgentEvent> ParseAssistant(JObject root)
        {
            var content = root["message"]?["content"] as JArray;
            if (content == null)
            {
                return Empty;
            }

            // Text and thinking are skipped once deltas have actually arrived, not merely because they
            // were requested: if --include-partial-messages is silently ignored by an older CLI, this
            // falls back to the complete messages instead of showing nothing at all.
            bool skipStreamedContent = _expectDeltas && _sawDeltas;

            var events = new List<AgentEvent>();

            foreach (JToken block in content)
            {
                switch ((string)block["type"])
                {
                    case "text":
                        if (!skipStreamedContent)
                        {
                            events.Add(AgentEvent.AssistantText((string)block["text"] ?? string.Empty));
                        }
                        break;

                    case "thinking":
                        if (!skipStreamedContent)
                        {
                            events.Add(AgentEvent.Thinking((string)block["thinking"] ?? string.Empty));
                        }
                        break;

                    case "tool_use":
                        events.Add(AgentEvent.ToolCallStarted(
                            (string)block["id"] ?? string.Empty,
                            (string)block["name"] ?? string.Empty,
                            block["input"] != null ? JsonConvert.SerializeObject(block["input"]) : string.Empty));
                        break;
                }
            }

            return events;
        }

        private IReadOnlyList<AgentEvent> ParseUser(JObject root)
        {
            // "user" on the way out is the CLI echoing tool results back into the conversation — the
            // prompts we send never come back to us.
            var content = root["message"]?["content"] as JArray;
            if (content == null)
            {
                return Empty;
            }

            var events = new List<AgentEvent>();

            foreach (JToken block in content)
            {
                if ((string)block["type"] != "tool_result")
                {
                    continue;
                }

                events.Add(AgentEvent.ToolCallCompleted(
                    (string)block["tool_use_id"] ?? string.Empty,
                    FlattenResultContent(block["content"]),
                    (bool?)block["is_error"] ?? false));
            }

            return events;
        }

        private IReadOnlyList<AgentEvent> ParseRateLimit(JObject root)
        {
            var info = root["rate_limit_info"] as JObject;
            if (info == null)
            {
                return Empty;
            }

            return One(AgentEvent.RateLimitUpdated(new AgentRateLimit
            {
                Status = (string)info["status"] ?? string.Empty,
                LimitType = (string)info["rateLimitType"] ?? string.Empty,
                ResetsAtUnix = (long?)info["resetsAt"] ?? 0,
                IsUsingOverage = (bool?)info["isUsingOverage"] ?? false
            }));
        }

        private IReadOnlyList<AgentEvent> ParseResult(JObject root)
        {
            string subtype = (string)root["subtype"] ?? string.Empty;
            string terminalReason = (string)root["terminal_reason"] ?? string.Empty;

            // An interrupted turn reports itself as an error; treating it as one would put a red banner
            // in the transcript every time the user clicks stop.
            bool wasInterrupted = terminalReason == "aborted_streaming" ||
                                  terminalReason == "aborted" ||
                                  subtype == "error_during_execution";

            var usageNode = root["usage"] as JObject;
            var usage = new AgentUsage
            {
                InputTokens = (int?)usageNode?["input_tokens"] ?? 0,
                OutputTokens = (int?)usageNode?["output_tokens"] ?? 0,
                CacheReadTokens = (int?)usageNode?["cache_read_input_tokens"] ?? 0,
                CacheCreationTokens = (int?)usageNode?["cache_creation_input_tokens"] ?? 0,
                CostUsd = (double?)root["total_cost_usd"] ?? 0d,
                DurationMs = (int?)root["duration_ms"] ?? 0
            };

            var denials = new List<AgentPermissionDenial>();
            var denialArray = root["permission_denials"] as JArray;
            if (denialArray != null)
            {
                foreach (JToken denial in denialArray)
                {
                    JToken input = denial["tool_input"] ?? denial["input"];
                    denials.Add(new AgentPermissionDenial
                    {
                        ToolName = (string)(denial["tool_name"] ?? denial["name"]) ?? string.Empty,
                        ToolUseId = (string)(denial["tool_use_id"] ?? denial["id"]) ?? string.Empty,
                        ToolInputJson = input != null ? JsonConvert.SerializeObject(input) : string.Empty
                    });
                }
            }

            var events = new List<AgentEvent>();

            // A hard failure (auth, quota, bad flag) has no terminal_reason and carries the message in
            // "result"; surface it before closing the turn so the transcript explains itself.
            if (subtype != "success" && !wasInterrupted)
            {
                string message = (string)root["result"] ?? (string)root["error"] ?? subtype;
                events.Add(AgentEvent.SessionError(string.IsNullOrWhiteSpace(message)
                    ? "The agent ended the turn with an error."
                    : message));
            }

            events.Add(AgentEvent.TurnCompleted(usage, denials, wasInterrupted));

            return events;
        }

        /// <summary>
        /// Turns the CLI's <c>can_use_tool</c> control request into an interaction the chat can render.
        /// <para>
        /// The wire shape was captured from the CLI, not guessed:
        /// <c>{"type":"control_request","request_id":R,"request":{"subtype":"can_use_tool",
        /// "tool_name":T,"input":I,"tool_use_id":U,"requires_user_interaction":B}}</c>.
        /// </para>
        /// </summary>
        private IReadOnlyList<AgentEvent> ParseControlRequest(JObject root)
        {
            var request = root["request"] as JObject;
            if (request == null || (string)request["subtype"] != "can_use_tool")
            {
                // initialize / other subtypes are handled by the session, or simply ignored.
                return Empty;
            }

            string requestId = (string)root["request_id"] ?? string.Empty;
            string toolName = (string)request["tool_name"] ?? string.Empty;
            var input = request["input"] as JObject;

            Action<string, object> responder = ControlResponder;

            var interaction = new AgentInteractionRequest((allow, denyMessage, answers) =>
            {
                Action<string, object> target = responder;
                if (target == null)
                {
                    return;
                }

                if (!allow)
                {
                    target(requestId, new
                    {
                        behavior = "deny",
                        message = string.IsNullOrWhiteSpace(denyMessage)
                            ? "The user declined this action."
                            : denyMessage
                    });
                    return;
                }

                // "allow" has to echo the tool input back. For AskUserQuestion the answers ride along
                // in an "answers" member keyed by the question text — measured: allowing without it
                // makes the tool return "The user did not answer the questions."
                JObject updated = input != null ? (JObject)input.DeepClone() : new JObject();

                if (answers != null && answers.Count > 0)
                {
                    var map = new JObject();
                    foreach (KeyValuePair<string, string> pair in answers)
                    {
                        map[pair.Key] = pair.Value ?? string.Empty;
                    }
                    updated["answers"] = map;
                }

                target(requestId, new { behavior = "allow", updatedInput = updated });
            })
            {
                ToolName = toolName,
                ToolUseId = (string)request["tool_use_id"] ?? string.Empty,
                ToolInputJson = input != null ? JsonConvert.SerializeObject(input, Formatting.Indented) : string.Empty
            };

            if (toolName == "AskUserQuestion")
            {
                interaction.Kind = AgentInteractionKind.Question;
                interaction.Questions = ParseQuestions(input);
            }
            else if (toolName == "ExitPlanMode")
            {
                interaction.Kind = AgentInteractionKind.PlanReview;
                interaction.PlanText = (string)input?["plan"] ?? string.Empty;
            }
            else
            {
                interaction.Kind = AgentInteractionKind.ToolApproval;
            }

            // A question with no options is unanswerable; fall back to a plain approval card rather
            // than showing an empty form the user cannot submit.
            if (interaction.Kind == AgentInteractionKind.Question &&
                (interaction.Questions == null || interaction.Questions.Count == 0))
            {
                interaction.Kind = AgentInteractionKind.ToolApproval;
            }

            return One(AgentEvent.InteractionRequested(interaction));
        }

        private static IReadOnlyList<AgentQuestion> ParseQuestions(JObject input)
        {
            var array = input?["questions"] as JArray;
            if (array == null)
            {
                return new AgentQuestion[0];
            }

            var questions = new List<AgentQuestion>(array.Count);

            foreach (JToken item in array)
            {
                string text = (string)item["question"];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var options = new List<AgentQuestionOption>();
                var optionArray = item["options"] as JArray;
                if (optionArray != null)
                {
                    foreach (JToken option in optionArray)
                    {
                        string label = (string)option["label"];
                        if (string.IsNullOrWhiteSpace(label))
                        {
                            continue;
                        }

                        options.Add(new AgentQuestionOption
                        {
                            Label = label,
                            Description = (string)option["description"] ?? string.Empty
                        });
                    }
                }

                questions.Add(new AgentQuestion
                {
                    Header = (string)item["header"] ?? string.Empty,
                    Question = text,
                    MultiSelect = (bool?)item["multiSelect"] ?? false,
                    Options = options
                });
            }

            return questions;
        }

        /// <summary>
        /// Tool results are a string for simple tools and an array of content blocks for the ones that
        /// return images or structured output.
        /// </summary>
        private static string FlattenResultContent(JToken content)
        {
            if (content == null)
            {
                return string.Empty;
            }

            if (content.Type == JTokenType.String)
            {
                return (string)content;
            }

            var array = content as JArray;
            if (array == null)
            {
                return content.ToString();
            }

            var parts = new List<string>();
            foreach (JToken block in array)
            {
                if ((string)block["type"] == "text")
                {
                    parts.Add((string)block["text"] ?? string.Empty);
                }
                else if (block["type"] != null)
                {
                    parts.Add($"[{(string)block["type"]}]");
                }
            }

            return string.Join("\n", parts);
        }

        private static IReadOnlyList<string> ToStringList(JToken token)
        {
            var array = token as JArray;
            if (array == null)
            {
                return new string[0];
            }

            var list = new List<string>(array.Count);
            foreach (JToken item in array)
            {
                string value = (string)item;
                if (!string.IsNullOrEmpty(value))
                {
                    list.Add(value);
                }
            }

            return list;
        }

        private static IReadOnlyList<AgentEvent> One(AgentEvent agentEvent)
        {
            return new[] { agentEvent };
        }

        private static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
