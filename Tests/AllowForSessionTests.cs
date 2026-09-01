/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for "Allow for the rest of this session" on native-mode tool approvals (issue #149)
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class AllowForSessionTests
    {
        private static ClaudeStreamParser NewParser(out List<KeyValuePair<string, object>> captured)
        {
            var sink = new List<KeyValuePair<string, object>>();
            captured = sink;
            return new ClaudeStreamParser(expectDeltas: true)
            {
                ControlResponder = (requestId, payload) =>
                    sink.Add(new KeyValuePair<string, object>(requestId, payload))
            };
        }

        private static AgentInteractionRequest ParseToolApproval(ClaudeStreamParser parser, string toolName)
        {
            string line =
                "{\"type\":\"control_request\",\"request_id\":\"req-1\",\"request\":{\"subtype\":\"can_use_tool\"," +
                "\"tool_name\":\"" + toolName + "\",\"tool_use_id\":\"tu-1\",\"input\":{\"command\":\"ls\"}}}";

            foreach (AgentEvent evt in parser.Parse(line))
            {
                if (evt.Kind == AgentEventKind.InteractionRequested)
                {
                    return evt.Interaction;
                }
            }

            Assert.Fail("no interaction was produced");
            return null;
        }

        [TestMethod]
        public void AllowForSession_RunsTheBookkeepingCallbackThenAllowsTheCall()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParseToolApproval(parser, "Bash");

            var order = new List<string>();
            interaction.OnAllowForSession = () => order.Add("remembered");

            interaction.AllowForSession();

            CollectionAssert.AreEqual(new[] { "remembered" }, order);

            Assert.AreEqual(1, sent.Count);
            string json = JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(json, "\"behavior\":\"allow\"");
            Assert.IsTrue(interaction.IsAnswered);
        }

        [TestMethod]
        public void AllowForSession_WithoutACallbackStillAllowsTheCall()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParseToolApproval(parser, "Bash");
            Assert.IsNull(interaction.OnAllowForSession);

            interaction.AllowForSession();

            Assert.AreEqual(1, sent.Count);
            string json = JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(json, "\"behavior\":\"allow\"");
        }

        [TestMethod]
        public void PlainAllow_DoesNotTriggerTheSessionCallback()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParseToolApproval(parser, "Bash");

            bool remembered = false;
            interaction.OnAllowForSession = () => remembered = true;

            interaction.Allow(null);

            Assert.IsFalse(remembered, "a one-off Allow must not pre-approve the tool for the session");
        }

        [TestMethod]
        public void AllowForSession_IsOneShot_LikeAllowAndDeny()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParseToolApproval(parser, "Bash");

            int callbackRuns = 0;
            interaction.OnAllowForSession = () => callbackRuns++;

            interaction.AllowForSession();
            interaction.AllowForSession();
            interaction.Deny("late");

            Assert.AreEqual(1, sent.Count, "only the first answer reaches the CLI");
            Assert.AreEqual(1, callbackRuns, "the bookkeeping hook is one-shot too");
        }
    }
}
