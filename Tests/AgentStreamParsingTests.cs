/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers native mode's stream parsers — JSON transcript lines in, normalized AgentEvents out.
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// The parsers are the one part of native mode that can be tested without a process, a shell or a
    /// running Visual Studio — which is why they were split out of the sessions in the first place.
    /// The JSON here mirrors lines captured from the real CLIs.
    /// </summary>
    [TestClass]
    public class AgentStreamParsingTests
    {
        #region Claude Code (stream-json)

        private static List<AgentEvent> ParseAll(ClaudeStreamParser parser, params string[] lines)
        {
            var events = new List<AgentEvent>();
            foreach (string line in lines)
            {
                events.AddRange(parser.Parse(line));
            }
            return events;
        }

        [TestMethod]
        public void ClaudeParser_InitLineStartsTheSessionAndExposesIdAndModel()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc-123\",\"model\":\"claude-opus-5\"," +
                "\"tools\":[\"Read\",\"Edit\"],\"slash_commands\":[\"/model\"]}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(AgentEventKind.SessionStarted, events[0].Kind);
            Assert.AreEqual("abc-123", events[0].SessionId);
            Assert.AreEqual("claude-opus-5", events[0].Model);
            Assert.AreEqual("abc-123", parser.SessionId);
            Assert.AreEqual("claude-opus-5", parser.Model);
        }

        [TestMethod]
        public void ClaudeParser_NonInitSystemLinesAreIgnored()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            Assert.AreEqual(0, parser.Parse("{\"type\":\"system\",\"subtype\":\"status\"}").Count);
        }

        [TestMethod]
        public void ClaudeParser_TextAndThinkingArriveAsSeparateStreamingChunks()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"weighing it\"}}}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"PO\"}}}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"NG\"}}}");

            Assert.AreEqual(3, events.Count);
            Assert.AreEqual(AgentEventKind.Thinking, events[0].Kind);
            Assert.AreEqual("weighing it", events[0].Text);
            Assert.AreEqual("PONG", string.Concat(events.Skip(1).Select(e => e.Text)));
            Assert.IsTrue(events.Skip(1).All(e => e.Kind == AgentEventKind.AssistantText));
        }

        [TestMethod]
        public void ClaudeParser_SignatureAndInputJsonDeltasProduceNothing()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"signature_delta\",\"signature\":\"abcdef\"}}}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"pa\"}}}",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\"}}");

            Assert.AreEqual(0, events.Count);
        }

        [TestMethod]
        public void ClaudeParser_CompleteAssistantMessageDoesNotRepeatAlreadyStreamedText()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"PONG\"}}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"PONG\"}]}}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("PONG", events[0].Text);
        }

        [TestMethod]
        public void ClaudeParser_FallsBackToTheCompleteMessageWhenNoDeltaEverArrives()
        {
            // An older CLI can ignore --include-partial-messages. Skipping the complete message on the
            // strength of the request alone would leave the panel blank.
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"PONG\"}]}}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(AgentEventKind.AssistantText, events[0].Kind);
            Assert.AreEqual("PONG", events[0].Text);
        }

        [TestMethod]
        public void ClaudeParser_ToolUseAlwaysComesFromTheCompleteMessage()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"reading\"}}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[" +
                "{\"type\":\"text\",\"text\":\"reading\"}," +
                "{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Read\",\"input\":{\"file_path\":\"App.cs\"}}]}}");

            AgentEvent toolCall = events.Single(e => e.Kind == AgentEventKind.ToolCallStarted);
            Assert.AreEqual("toolu_1", toolCall.ToolCallId);
            Assert.AreEqual("Read", toolCall.ToolName);
            StringAssert.Contains(toolCall.ToolInputJson, "App.cs");
        }

        [TestMethod]
        public void ClaudeParser_ToolResultCarriesItsErrorFlag()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\"," +
                "\"tool_use_id\":\"toolu_1\",\"is_error\":true," +
                "\"content\":[{\"type\":\"text\",\"text\":\"permission not granted\"}]}]}}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(AgentEventKind.ToolCallCompleted, events[0].Kind);
            Assert.AreEqual("toolu_1", events[0].ToolCallId);
            Assert.IsTrue(events[0].IsError);
            StringAssert.Contains(events[0].ToolResult, "permission not granted");
        }

        [TestMethod]
        public void ClaudeParser_ResultEndsTheTurnWithUsageAndCost()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":4200,\"total_cost_usd\":0.0123," +
                "\"usage\":{\"input_tokens\":11,\"output_tokens\":22,\"cache_read_input_tokens\":33," +
                "\"cache_creation_input_tokens\":44}}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(AgentEventKind.TurnCompleted, events[0].Kind);
            Assert.IsFalse(events[0].WasInterrupted);
            Assert.AreEqual(11, events[0].Usage.InputTokens);
            Assert.AreEqual(22, events[0].Usage.OutputTokens);
            Assert.AreEqual(33, events[0].Usage.CacheReadTokens);
            Assert.AreEqual(44, events[0].Usage.CacheCreationTokens);
            Assert.AreEqual(4200, events[0].Usage.DurationMs);
            Assert.AreEqual(0.0123d, events[0].Usage.CostUsd, 0.00001d);
        }

        [TestMethod]
        public void ClaudeParser_AnAbortedTurnIsInterruptedRatherThanFailed()
        {
            // Clicking stop must not paint a red error banner in the transcript.
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"result\",\"subtype\":\"error_during_execution\"," +
                "\"terminal_reason\":\"aborted_streaming\",\"usage\":{}}");

            Assert.AreEqual(AgentEventKind.TurnCompleted, events[0].Kind);
            Assert.IsTrue(events[0].WasInterrupted);
        }

        [TestMethod]
        public void ClaudeParser_PermissionDenialsSurviveOnTheFinalResult()
        {
            // stream-json auto-denies without asking, so this is the only place a blocked tool is
            // reported — losing it makes the agent look like it ignored the request.
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"result\",\"subtype\":\"success\",\"usage\":{}," +
                "\"permission_denials\":[{\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"App.cs\"}}]}");

            Assert.AreEqual(1, events[0].PermissionDenials.Count);
            Assert.AreEqual("Write", events[0].PermissionDenials[0].ToolName);
        }

        [TestMethod]
        public void ClaudeParser_RateLimitEventIsNormalized()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed_warning\"," +
                "\"rateLimitType\":\"five_hour\",\"resetsAt\":1750000000,\"isUsingOverage\":true}}");

            Assert.AreEqual(AgentEventKind.RateLimitUpdated, events[0].Kind);
            Assert.AreEqual("allowed_warning", events[0].RateLimit.Status);
            Assert.AreEqual("five_hour", events[0].RateLimit.LimitType);
            Assert.AreEqual(1750000000L, events[0].RateLimit.ResetsAtUnix);
            Assert.IsTrue(events[0].RateLimit.IsUsingOverage);
        }

        [TestMethod]
        public void ClaudeParser_MalformedAndUnknownLinesAreSurvivable()
        {
            // A CLI occasionally interleaves a plain-text warning on stdout. One bad line must not tear
            // down a working session, so the parser has to keep going afterwards.
            var parser = new ClaudeStreamParser(expectDeltas: true);

            List<AgentEvent> events = ParseAll(parser,
                "npm warn deprecated something@1.0.0",
                "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\"}}",
                "{ this is not json",
                string.Empty,
                "   ",
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"still alive\"}}}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("still alive", events[0].Text);
        }

        [TestMethod]
        public void ClaudeParser_HandlesAVeryLargeLine()
        {
            // Thinking blocks with their signature routinely pass 100 KB; nothing may assume a bounded
            // line length.
            var parser = new ClaudeStreamParser(expectDeltas: true);
            string huge = new string('x', 400000);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
                "\"delta\":{\"type\":\"text_delta\",\"text\":\"" + huge + "\"}}}");

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(huge.Length, events[0].Text.Length);
        }

        #endregion

        #region Claude Code (interactive control channel)

        /// <summary>
        /// Wires a parser to a responder that records the payloads it is asked to send back, which is
        /// the only observable side of the control channel without a live process.
        /// </summary>
        private static ClaudeStreamParser NewParserWithResponder(out List<KeyValuePair<string, object>> sent)
        {
            var captured = new List<KeyValuePair<string, object>>();
            sent = captured;

            return new ClaudeStreamParser(expectDeltas: true)
            {
                ControlResponder = (requestId, payload) =>
                    captured.Add(new KeyValuePair<string, object>(requestId, payload))
            };
        }

        private const string AskQuestionRequest =
            "{\"type\":\"control_request\",\"request_id\":\"req-1\",\"request\":{\"subtype\":\"can_use_tool\"," +
            "\"tool_name\":\"AskUserQuestion\",\"tool_use_id\":\"tu-9\",\"input\":{\"questions\":[{" +
            "\"header\":\"Flavor\",\"question\":\"Which flavor?\",\"multiSelect\":false,\"options\":[" +
            "{\"label\":\"Chocolate\",\"description\":\"Classic\"},{\"label\":\"Vanilla\"}]}]}}}";

        [TestMethod]
        public void ClaudeParser_AskUserQuestionBecomesAQuestionInteraction()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParserWithResponder(out sent);

            List<AgentEvent> events = ParseAll(parser, AskQuestionRequest);

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(AgentEventKind.InteractionRequested, events[0].Kind);

            AgentInteractionRequest interaction = events[0].Interaction;
            Assert.IsNotNull(interaction);
            Assert.AreEqual(AgentInteractionKind.Question, interaction.Kind);
            Assert.AreEqual("tu-9", interaction.ToolUseId);
            Assert.AreEqual(1, interaction.Questions.Count);
            Assert.AreEqual("Flavor", interaction.Questions[0].Header);
            Assert.AreEqual("Which flavor?", interaction.Questions[0].Question);
            Assert.IsFalse(interaction.Questions[0].MultiSelect);
            Assert.AreEqual(2, interaction.Questions[0].Options.Count);
            Assert.AreEqual("Chocolate", interaction.Questions[0].Options[0].Label);
            Assert.AreEqual("Classic", interaction.Questions[0].Options[0].Description);
        }

        [TestMethod]
        public void ClaudeParser_AnsweringAQuestionSendsTheAnswersInsideUpdatedInput()
        {
            // Measured against the real CLI: allowing without an "answers" map makes it report that the
            // user answered nothing, so the map is the whole point of the round trip.
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParserWithResponder(out sent);

            AgentInteractionRequest interaction = ParseAll(parser, AskQuestionRequest)[0].Interaction;
            interaction.Allow(new Dictionary<string, string> { { "Which flavor?", "Chocolate" } });

            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual("req-1", sent[0].Key);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(json, "\"behavior\":\"allow\"");
            StringAssert.Contains(json, "\"Which flavor?\":\"Chocolate\"");

            // One-shot: a second answer must not reach the CLI, which is no longer waiting for one.
            Assert.IsTrue(interaction.IsAnswered);
            interaction.Deny("late");
            Assert.AreEqual(1, sent.Count);
        }

        [TestMethod]
        public void ClaudeParser_DenyingAnInteractionSendsTheReason()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParserWithResponder(out sent);

            ParseAll(parser, AskQuestionRequest)[0].Interaction.Deny("Not now.");

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(json, "\"behavior\":\"deny\"");
            StringAssert.Contains(json, "Not now.");
        }

        [TestMethod]
        public void ClaudeParser_ExitPlanModeBecomesAPlanReviewCarryingThePlan()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParserWithResponder(out sent);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"control_request\",\"request_id\":\"req-2\",\"request\":{\"subtype\":\"can_use_tool\"," +
                "\"tool_name\":\"ExitPlanMode\",\"input\":{\"plan\":\"1. Read the file\\n2. Fix it\"}}}");

            Assert.AreEqual(1, events.Count);
            AgentInteractionRequest interaction = events[0].Interaction;
            Assert.AreEqual(AgentInteractionKind.PlanReview, interaction.Kind);
            StringAssert.Contains(interaction.PlanText, "2. Fix it");
        }

        [TestMethod]
        public void ClaudeParser_AnyOtherToolBecomesAPlainApproval()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParserWithResponder(out sent);

            List<AgentEvent> events = ParseAll(parser,
                "{\"type\":\"control_request\",\"request_id\":\"req-3\",\"request\":{\"subtype\":\"can_use_tool\"," +
                "\"tool_name\":\"Bash\",\"input\":{\"command\":\"git status\"}}}");

            Assert.AreEqual(AgentInteractionKind.ToolApproval, events[0].Interaction.Kind);
            Assert.AreEqual("Bash", events[0].Interaction.ToolName);
        }

        [TestMethod]
        public void ClaudeCommandBuilder_InteractivePermissionsAddTheStdioPromptTool()
        {
            // The flag is undocumented in --help but is what makes the CLI offer AskUserQuestion and
            // ExitPlanMode at all; losing it silently disables both features.
            string args = ClaudeCommandBuilder.GetArguments(new ClaudeSessionOptions
            {
                PermissionMode = "plan",
                InteractivePermissions = true
            });

            StringAssert.Contains(args, "--permission-mode \"plan\"");
            StringAssert.Contains(args, "--permission-prompt-tool stdio");
        }

        [TestMethod]
        public void ClaudeCommandBuilder_SkippingPermissionsDropsThePromptToolAndTheMode()
        {
            string args = ClaudeCommandBuilder.GetArguments(new ClaudeSessionOptions
            {
                DangerouslySkipPermissions = true,
                PermissionMode = "plan",
                InteractivePermissions = true
            });

            StringAssert.Contains(args, "--dangerously-skip-permissions");
            Assert.IsFalse(args.Contains("--permission-mode"));
            Assert.IsFalse(args.Contains("--permission-prompt-tool"));
        }

        [TestMethod]
        public void ClaudeCommandBuilder_SideQuestionForksTheResumedSession()
        {
            // --fork-session is what makes "/btw" safe: the question reads the whole conversation for
            // context but writes to a new session id, so the live session's transcript is untouched.
            // Resuming without forking would put a second writer on the file the running turn owns.
            string args = ClaudeCommandBuilder.GetSideQuestionArguments(
                new ClaudeSessionOptions { Model = "opus" }, "abc-123");

            StringAssert.Contains(args, "--print");
            StringAssert.Contains(args, "--resume \"abc-123\"");
            StringAssert.Contains(args, "--fork-session");
            StringAssert.Contains(args, "--model \"opus\"");

            // The answer is plain text on stdout; asking for the event stream would mean parsing it.
            Assert.IsFalse(args.Contains("--output-format"));
        }

        [TestMethod]
        public void ClaudeCommandBuilder_SideQuestionWithoutASessionDoesNotFork()
        {
            // --fork-session is only valid with --resume. Before the first turn there is no transcript
            // to resume, so the question is asked without context rather than failing the launch.
            string args = ClaudeCommandBuilder.GetSideQuestionArguments(new ClaudeSessionOptions(), null);

            StringAssert.Contains(args, "--print");
            Assert.IsFalse(args.Contains("--resume"));
            Assert.IsFalse(args.Contains("--fork-session"));
        }

        [TestMethod]
        public void ClaudeCommandBuilder_SideQuestionUnderWslRunsInTheWorkspace()
        {
            string args = ClaudeCommandBuilder.GetSideQuestionArguments(new ClaudeSessionOptions
            {
                UseWsl = true,
                WslWorkingDirectory = "/mnt/c/work"
            }, "abc-123");

            StringAssert.Contains(args, "bash -lic");
            StringAssert.Contains(args, "cd '/mnt/c/work' && claude --print");
            StringAssert.Contains(args, "--resume 'abc-123' --fork-session");
        }

        [TestMethod]
        public void ClaudeCommandBuilder_EffortIsALaunchFlag()
        {
            // Effort used to be sent as the "/effort" command, which the CLI applies "for this session
            // only" — so every relaunch silently dropped it while the composer still showed the level.
            string args = ClaudeCommandBuilder.GetArguments(new ClaudeSessionOptions
            {
                Effort = "xhigh"
            });

            StringAssert.Contains(args, "--effort \"xhigh\"");
        }

        [TestMethod]
        public void ClaudeCommandBuilder_NoEffortMeansNoFlag()
        {
            // "Auto" is the extension's own "say nothing" and is the one value the CLI rejects, so it
            // has to arrive here as an empty string and produce no flag at all.
            string args = ClaudeCommandBuilder.GetArguments(new ClaudeSessionOptions());

            Assert.IsFalse(args.Contains("--effort"));
        }

        [TestMethod]
        public void ClaudeCommandBuilder_EffortIsBashQuotedUnderWsl()
        {
            string args = ClaudeCommandBuilder.GetArguments(new ClaudeSessionOptions
            {
                UseWsl = true,
                WslWorkingDirectory = "/mnt/c/repo",
                Effort = "ultracode"
            });

            StringAssert.Contains(args, "--effort 'ultracode'");
        }

        [TestMethod]
        public void ClaudeSession_AFreshSessionHasNothingToResumeUntilATurnRuns()
        {
            // The CLI creates the transcript only when the first turn runs, so the seeded id names
            // nothing yet. A relaunch that resumed it would fail with "No conversation found".
            using (var session = new ClaudeStreamJsonSession(new ClaudeSessionOptions()))
            {
                Assert.AreNotEqual(string.Empty, session.SessionId, "the seed still has to be sent as --session-id");
                Assert.AreEqual(string.Empty, session.ResumableSessionId);
            }
        }

        [TestMethod]
        public void ClaudeSession_ARelaunchKeepsResumingTheIdItWasHandedOver()
        {
            // Two composer changes in a row: the second relaunch must resume the same real transcript
            // as the first, not the throwaway seed the first relaunch was created with.
            using (var session = new ClaudeStreamJsonSession(new ClaudeSessionOptions
            {
                ResumeSessionId = "11111111-2222-3333-4444-555555555555"
            }))
            {
                Assert.AreNotEqual("11111111-2222-3333-4444-555555555555", session.SessionId);
                Assert.AreEqual("11111111-2222-3333-4444-555555555555", session.ResumableSessionId);
            }
        }

        #endregion

        #region Cursor Agent (one-shot + resume)

        /// <summary>Builds a sink that records everything the protocol emits, in order.</summary>
        private static OneShotTurnSink NewSink(out List<AgentEvent> events)
        {
            var captured = new List<AgentEvent>();
            events = captured;
            return new OneShotTurnSink(captured.Add);
        }

        [TestMethod]
        public void CursorProtocol_FirstTurnHasNoResumeFlagAndTheSecondCarriesTheChatId()
        {
            var protocol = new CursorAgentProtocol();
            var options = new OneShotSessionOptions { Model = "auto", SkipApprovals = true };

            string first = protocol.BuildArguments(options, string.Empty);
            string second = protocol.BuildArguments(options, "chat-77");

            StringAssert.Contains(first, "--print --output-format stream-json --stream-partial-output");
            StringAssert.Contains(first, "--trust");
            StringAssert.Contains(first, "--force");
            StringAssert.Contains(first, "--model auto");
            Assert.IsFalse(first.Contains("--resume"));
            StringAssert.Contains(second, "--resume chat-77");
        }

        [TestMethod]
        public void CursorProtocol_ApprovalsFlagIsOmittedWhenNotSkipping()
        {
            var protocol = new CursorAgentProtocol();

            string args = protocol.BuildArguments(new OneShotSessionOptions(), string.Empty);

            Assert.IsFalse(args.Contains("--force"));
        }

        [TestMethod]
        public void CursorProtocol_InitRecordsTheChatIdTheNextTurnResumesFrom()
        {
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CursorAgentProtocol();

            protocol.HandleLine("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"chat-77\"," +
                                "\"model\":\"auto\"}", sink);

            Assert.AreEqual("chat-77", sink.SessionId);
            Assert.AreEqual("auto", sink.Model);
            Assert.AreEqual(AgentEventKind.SessionStarted, events.Single().Kind);
        }

        [TestMethod]
        public void CursorProtocol_TheWholeAnswerRecapIsDroppedWhenChunksAlreadyArrived()
        {
            // With --stream-partial-output the CLI sends the answer twice: chunks carrying timestamp_ms,
            // then the whole thing without it. Keeping both prints every answer twice.
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CursorAgentProtocol();

            protocol.HandleLine("{\"type\":\"assistant\",\"timestamp_ms\":1,\"message\":{\"content\":" +
                                "[{\"type\":\"text\",\"text\":\"PO\"}]}}", sink);
            protocol.HandleLine("{\"type\":\"assistant\",\"timestamp_ms\":2,\"message\":{\"content\":" +
                                "[{\"type\":\"text\",\"text\":\"NG\"}]}}", sink);
            protocol.HandleLine("{\"type\":\"assistant\",\"message\":{\"content\":" +
                                "[{\"type\":\"text\",\"text\":\"PONG\"}]}}", sink);

            Assert.AreEqual(2, events.Count);
            Assert.AreEqual("PONG", string.Concat(events.Select(e => e.Text)));
        }

        [TestMethod]
        public void CursorProtocol_TheRecapIsKeptWhenItIsTheOnlyCopyOfTheAnswer()
        {
            // Resumed turns arrive without chunks; dropping the recap there would show nothing at all.
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CursorAgentProtocol();

            protocol.HandleLine("{\"type\":\"assistant\",\"message\":{\"content\":" +
                                "[{\"type\":\"text\",\"text\":\"PONG\"}]}}", sink);

            Assert.AreEqual("PONG", events.Single().Text);
        }

        [TestMethod]
        public void CursorProtocol_ToolCallsAreNamedFromTheirPayloadProperty()
        {
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CursorAgentProtocol();

            protocol.HandleLine("{\"type\":\"tool_call\",\"subtype\":\"started\",\"call_id\":\"c1\"," +
                                "\"tool_call\":{\"readToolCall\":{\"args\":{\"path\":\"App.cs\"}}}}", sink);
            protocol.HandleLine("{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"c1\"," +
                                "\"tool_call\":{\"readToolCall\":{\"result\":{\"error\":\"not found\"}}}}", sink);

            Assert.AreEqual("Read", events[0].ToolName);
            Assert.AreEqual("c1", events[0].ToolCallId);
            StringAssert.Contains(events[0].ToolInputJson, "App.cs");
            Assert.AreEqual(AgentEventKind.ToolCallCompleted, events[1].Kind);
            Assert.IsTrue(events[1].IsError);
        }

        [TestMethod]
        public void CursorProtocol_ResultEndsTheTurnAndAFailureIsReported()
        {
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CursorAgentProtocol();

            protocol.HandleLine("{\"type\":\"result\",\"is_error\":true,\"result\":\"model unavailable\"," +
                                "\"duration_ms\":900,\"usage\":{\"inputTokens\":5,\"outputTokens\":6}}", sink);

            Assert.IsTrue(sink.TurnEnded);
            Assert.AreEqual(5, sink.Usage.InputTokens);
            Assert.AreEqual(6, sink.Usage.OutputTokens);
            Assert.AreEqual(900, sink.Usage.DurationMs);
            Assert.AreEqual(AgentEventKind.SessionError, events.Single().Kind);
            StringAssert.Contains(events.Single().Text, "model unavailable");
        }

        #endregion

        #region Codex (one-shot + resume)

        [TestMethod]
        public void CodexProtocol_ResumeSwitchesTheSubcommandAndAppendsTheThreadId()
        {
            var protocol = new CodexExecProtocol();
            var options = new OneShotSessionOptions();

            string first = protocol.BuildArguments(options, string.Empty);
            string second = protocol.BuildArguments(options, "thread-9");

            StringAssert.StartsWith(first, "exec --json");
            StringAssert.StartsWith(second, "exec resume --json");
            StringAssert.Contains(second, "thread-9");

            // "-" (prompt from stdin) has to stay last on both, or the thread id is read as the prompt.
            StringAssert.EndsWith(first.Trim(), "-");
            StringAssert.EndsWith(second.Trim(), "-");
        }

        [TestMethod]
        public void CodexProtocol_SandboxIsSetThroughConfigBecauseResumeHasNoSandboxFlag()
        {
            var protocol = new CodexExecProtocol();

            string guarded = protocol.BuildArguments(new OneShotSessionOptions(), "thread-9");
            string skipping = protocol.BuildArguments(new OneShotSessionOptions { SkipApprovals = true }, "thread-9");

            StringAssert.Contains(guarded, "sandbox_mode='workspace-write'");
            StringAssert.Contains(skipping, "--dangerously-bypass-approvals-and-sandbox");
            Assert.IsFalse(skipping.Contains("sandbox_mode"));
        }

        [TestMethod]
        public void CodexProtocol_ThreadStartedRecordsTheIdResumeNeeds()
        {
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CodexExecProtocol();

            protocol.HandleLine("{\"type\":\"thread.started\",\"thread_id\":\"thread-9\"}", sink);

            Assert.AreEqual("thread-9", sink.SessionId);
            Assert.AreEqual(AgentEventKind.SessionStarted, events.Single().Kind);
        }

        [TestMethod]
        public void CodexProtocol_OnlyCompletedItemsProduceText()
        {
            // item.updated carries the same message mid-flight; rendering it too duplicates every answer.
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CodexExecProtocol();

            protocol.HandleLine("{\"type\":\"item.started\",\"item\":{\"id\":\"i1\",\"type\":\"agent_message\"," +
                                "\"text\":\"PON\"}}", sink);
            protocol.HandleLine("{\"type\":\"item.updated\",\"item\":{\"id\":\"i1\",\"type\":\"agent_message\"," +
                                "\"text\":\"PONG\"}}", sink);
            protocol.HandleLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"i1\",\"type\":\"agent_message\"," +
                                "\"text\":\"PONG\"}}", sink);

            Assert.AreEqual("PONG", events.Single().Text);
        }

        [TestMethod]
        public void CodexProtocol_CommandExecutionOpensAndClosesOneToolCard()
        {
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CodexExecProtocol();

            protocol.HandleLine("{\"type\":\"item.started\",\"item\":{\"id\":\"i2\",\"type\":\"command_execution\"," +
                                "\"command\":\"git status\"}}", sink);
            protocol.HandleLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"i2\",\"type\":\"command_execution\"," +
                                "\"command\":\"git status\",\"aggregated_output\":\"clean\",\"exit_code\":0}}", sink);

            Assert.AreEqual(AgentEventKind.ToolCallStarted, events[0].Kind);
            Assert.AreEqual("i2", events[0].ToolCallId);
            Assert.AreEqual(AgentEventKind.ToolCallCompleted, events[1].Kind);
            Assert.IsFalse(events[1].IsError);
            StringAssert.Contains(events[1].ToolResult, "clean");
        }

        [TestMethod]
        public void CodexProtocol_TurnCompletedCarriesUsageAndTurnFailedReportsTheReason()
        {
            List<AgentEvent> completedEvents;
            OneShotTurnSink completed = NewSink(out completedEvents);
            var protocol = new CodexExecProtocol();

            protocol.HandleLine("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":7,\"output_tokens\":8," +
                                "\"cached_input_tokens\":9}}", completed);

            Assert.IsTrue(completed.TurnEnded);
            Assert.AreEqual(7, completed.Usage.InputTokens);
            Assert.AreEqual(8, completed.Usage.OutputTokens);
            Assert.AreEqual(9, completed.Usage.CacheReadTokens);
            Assert.AreEqual(0, completedEvents.Count);

            List<AgentEvent> failedEvents;
            OneShotTurnSink failed = NewSink(out failedEvents);

            protocol.HandleLine("{\"type\":\"turn.failed\",\"error\":{\"message\":\"token expired\"}}", failed);

            Assert.IsTrue(failed.TurnEnded);
            Assert.AreEqual(AgentEventKind.SessionError, failedEvents.Single().Kind);
            StringAssert.Contains(failedEvents.Single().Text, "token expired");
        }

        [TestMethod]
        public void CodexProtocol_UnknownAndMalformedLinesAreIgnoredWithoutEndingTheTurn()
        {
            List<AgentEvent> events;
            OneShotTurnSink sink = NewSink(out events);
            var protocol = new CodexExecProtocol();

            protocol.HandleLine("not json at all", sink);
            protocol.HandleLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"i3\",\"type\":\"todo_list\"}}", sink);
            protocol.HandleLine("{\"type\":\"turn.started\"}", sink);

            Assert.AreEqual(0, events.Count);
            Assert.IsFalse(sink.TurnEnded);
        }

        #endregion
    }
}
