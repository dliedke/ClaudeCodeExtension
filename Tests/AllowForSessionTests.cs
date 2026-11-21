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

        private static AgentInteractionRequest ParsePlanReview(ClaudeStreamParser parser)
        {
            string line =
                "{\"type\":\"control_request\",\"request_id\":\"req-2\",\"request\":{\"subtype\":\"can_use_tool\"," +
                "\"tool_name\":\"ExitPlanMode\",\"tool_use_id\":\"tu-2\",\"input\":{\"plan\":\"do it\"}}}";

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
        public void ApproveAndSkipPermissions_SendsAnAllowThatSwitchesTheSessionToBypass()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParsePlanReview(parser);
            bool applied = false;
            interaction.OnApproveAndSkipPermissions = () => applied = true;

            Assert.IsTrue(interaction.CanApproveAndSkipPermissions);
            interaction.AllowAndSkipPermissions();

            Assert.IsTrue(applied);
            Assert.AreEqual(1, sent.Count);
            string json = JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(json, "\"behavior\":\"allow\"");
            StringAssert.Contains(json, "\"type\":\"setMode\"");
            StringAssert.Contains(json, "\"mode\":\"bypassPermissions\"");
        }

        [TestMethod]
        public void ApproveAndSkipPermissions_WhenNotEligibleIsAPlainApproval()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParsePlanReview(parser);

            Assert.IsFalse(interaction.CanApproveAndSkipPermissions);
            interaction.AllowAndSkipPermissions();

            Assert.AreEqual(1, sent.Count);
            Assert.IsFalse(JsonConvert.SerializeObject(sent[0].Value).Contains("setMode"));
        }

        [TestMethod]
        public void ApprovePlan_RecordsTheChosenModelAndSkipChoice()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest request = ParsePlanReview(parser);
            request.OnApproveAndSkipPermissions = () => { };

            var card = new ClaudeCodeVS.UI.ChatInteractionViewModel(request);
            card.ApprovePlan(skipPermissions: true, model: ClaudeCodeVS.ClaudeModel.Sonnet);

            Assert.IsFalse(card.IsPending);
            Assert.IsTrue(card.WasAccepted);
            Assert.IsTrue(card.WasApprovedAndSkippedPermissions);
            Assert.AreEqual(ClaudeCodeVS.ClaudeModel.Sonnet, card.ApprovedModel);
            StringAssert.Contains(JsonConvert.SerializeObject(sent[0].Value), "bypassPermissions");
        }

        [TestMethod]
        public void KeepPlanning_SendsTheTypedFeedbackToTheAgent()
        {
            // Issue #221: the plan card's textbox tells the agent what to change.
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            var card = new ClaudeCodeVS.UI.ChatInteractionViewModel(ParsePlanReview(parser));
            Assert.AreEqual("Keep planning", card.RejectCaption);

            card.PlanFeedback = "  Use a background task instead of a timer.  ";
            Assert.AreEqual("Send feedback", card.RejectCaption);
            card.Reject();

            string payload = JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(payload, "\"behavior\":\"deny\"");
            StringAssert.Contains(payload, "Use a background task instead of a timer.");
            StringAssert.Contains(payload, "Revise the plan");
            Assert.IsFalse(card.IsPending);
            Assert.IsFalse(card.WasAccepted);
            Assert.AreEqual("Plan rejected — asked to change: Use a background task instead of a timer.", card.Outcome);
        }

        [TestMethod]
        public void KeepPlanning_WithoutFeedbackWaitsForTheUser()
        {
            // Issue #268: no question card after an empty "Keep planning" — the agent waits instead.
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            var card = new ClaudeCodeVS.UI.ChatInteractionViewModel(ParsePlanReview(parser));
            card.PlanFeedback = "   ";
            card.Reject();

            string payload = JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(payload, "Do not ask questions");
            StringAssert.Contains(payload, "wait for the user");
            Assert.AreEqual("Plan rejected.", card.Outcome);
        }

        [TestMethod]
        public void ApprovePlanInManualMode_CarriesManualAndDropsAPickMadeWhilePlanning()
        {
            // Issue #268: the plan card can approve into Manual mode (ask before every tool).
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);
            bool asked = false;
            parser.PlanExitModeProvider = () => { asked = true; return "auto"; };

            AgentInteractionRequest request = ParsePlanReview(parser);
            string applied = null;
            request.OnApproveWithMode = mode => applied = mode;

            var card = new ClaudeCodeVS.UI.ChatInteractionViewModel(request);
            Assert.IsTrue(card.CanApproveInManualMode);
            card.ApprovePlan(skipPermissions: false, manualMode: true, model: null);

            Assert.IsFalse(asked);
            Assert.AreEqual("manual", applied);
            Assert.IsTrue(card.WasApprovedInManualMode);
            Assert.IsFalse(card.WasApprovedAndSkippedPermissions);
            StringAssert.Contains(JsonConvert.SerializeObject(sent[0].Value), "\"mode\":\"manual\"");
        }

        [TestMethod]
        public void ApprovePlanInManualMode_WithoutTheSessionCallbackIsAPlainApproval()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            var card = new ClaudeCodeVS.UI.ChatInteractionViewModel(ParsePlanReview(parser));
            Assert.IsFalse(card.CanApproveInManualMode);
            card.ApprovePlan(skipPermissions: false, manualMode: true, model: null);

            Assert.IsFalse(card.WasApprovedInManualMode);
            Assert.IsFalse(JsonConvert.SerializeObject(sent[0].Value).Contains("setMode"));
        }

        [TestMethod]
        public void PlainPlanApproval_DoesNotChangeThePermissionMode()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);

            AgentInteractionRequest interaction = ParsePlanReview(parser);
            interaction.OnApproveAndSkipPermissions = () => { };
            interaction.Allow(null);

            Assert.IsFalse(JsonConvert.SerializeObject(sent[0].Value).Contains("setMode"));
        }

        [TestMethod]
        public void PlainPlanApproval_CarriesAModePickedWhilePlanning()
        {
            // Issue #193: a mode picked mid-plan waits for the approval and rides on it.
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);
            parser.PlanExitModeProvider = () => "auto";

            AgentInteractionRequest interaction = ParsePlanReview(parser);
            interaction.Allow(null);

            string json = JsonConvert.SerializeObject(sent[0].Value);
            StringAssert.Contains(json, "\"type\":\"setMode\"");
            StringAssert.Contains(json, "\"mode\":\"auto\"");
        }

        [TestMethod]
        public void ApproveAndSkipPermissions_WinsOverAModePickedWhilePlanning()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);
            bool asked = false;
            parser.PlanExitModeProvider = () => { asked = true; return "auto"; };

            AgentInteractionRequest interaction = ParsePlanReview(parser);
            interaction.OnApproveAndSkipPermissions = () => { };
            interaction.AllowAndSkipPermissions();

            Assert.IsFalse(asked);
            StringAssert.Contains(JsonConvert.SerializeObject(sent[0].Value), "\"mode\":\"bypassPermissions\"");
        }

        [TestMethod]
        public void ToolApproval_NeverCarriesAPlanExitMode()
        {
            List<KeyValuePair<string, object>> sent;
            ClaudeStreamParser parser = NewParser(out sent);
            parser.PlanExitModeProvider = () => "auto";

            AgentInteractionRequest interaction = ParseToolApproval(parser, "Bash");
            interaction.Allow(null);

            Assert.IsFalse(JsonConvert.SerializeObject(sent[0].Value).Contains("setMode"));
        }

        [TestMethod]
        public void SystemFrames_ReportThePermissionMode()
        {
            var parser = new ClaudeStreamParser(expectDeltas: true);
            var reported = new List<string>();
            parser.PermissionModeReported = reported.Add;

            parser.Parse("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"permissionMode\":\"plan\"}");
            parser.Parse("{\"type\":\"system\",\"subtype\":\"status\",\"permissionMode\":\"acceptEdits\"}");
            parser.Parse("{\"type\":\"system\",\"subtype\":\"thinking_tokens\"}");

            CollectionAssert.AreEqual(new[] { "plan", "acceptEdits" }, reported);
        }

        [TestMethod]
        public void LeavingPlanMode_WaitsOnlyWhileATurnIsRunning()
        {
            Assert.IsTrue(ClaudeCommandBuilder.ShouldWaitForPlanToEnd("plan", ClaudePermissionChoice.SkipPermissions, true));
            Assert.IsTrue(ClaudeCommandBuilder.ShouldWaitForPlanToEnd("plan", ClaudePermissionChoice.AcceptEdits, true));
            Assert.IsFalse(ClaudeCommandBuilder.ShouldWaitForPlanToEnd("plan", ClaudePermissionChoice.SkipPermissions, false));
            Assert.IsFalse(ClaudeCommandBuilder.ShouldWaitForPlanToEnd("plan", ClaudePermissionChoice.PlanMode, true));

            // Entering plan mode mid-turn is what the issue asked for, and applies at once.
            Assert.IsFalse(ClaudeCommandBuilder.ShouldWaitForPlanToEnd("auto", ClaudePermissionChoice.PlanMode, true));
            Assert.IsFalse(ClaudeCommandBuilder.ShouldWaitForPlanToEnd("acceptEdits", ClaudePermissionChoice.SkipPermissions, true));
        }

        [TestMethod]
        public void PlainPlanApproval_CarriesAcceptEditsOnlyOutOfAnExtensionChosenPlanMode()
        {
            // #210 follow-up: without a mode the CLI went back to "default" and asked before every
            // edit while the selector read Accept edits.
            Assert.AreEqual("acceptEdits", ClaudeCommandBuilder.ResolvePlanApprovalMode(null, "plan", false));

            // A pick made while planning wins (#193).
            Assert.AreEqual("manual", ClaudeCommandBuilder.ResolvePlanApprovalMode("manual", "plan", false));
            Assert.AreEqual("auto", ClaudeCommandBuilder.ResolvePlanApprovalMode("auto", "acceptEdits", false));

            // The agent entered plan mode on its own: the CLI returns to the mode the selector shows.
            Assert.IsNull(ClaudeCommandBuilder.ResolvePlanApprovalMode(null, "manual", false));
            Assert.IsNull(ClaudeCommandBuilder.ResolvePlanApprovalMode(null, "acceptEdits", false));

            // Bypass leaves a stale "plan" behind in the launch options; it must not count.
            Assert.IsNull(ClaudeCommandBuilder.ResolvePlanApprovalMode(null, "plan", true));
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
