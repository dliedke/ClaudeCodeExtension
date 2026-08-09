/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers Devin `list --format json` parsing and ACP session/load transcript reconstruction
 *
 * *******************************************************************************************************************/

using System;
using System.Linq;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class DevinSessionHistoryTests
    {
        // Real payload shape captured from `devin list --format json` run in this repo's own workspace.
        private const string SampleListPayload = @"[
          {
            ""id"": ""real-sweatpants"",
            ""short_id"": ""real-sweatpants"",
            ""working_directory"": ""/mnt/c/GitLab/Personal/ClaudeCodeExtension"",
            ""working_directory_display"": ""./"",
            ""last_activity_at"": 1780922432,
            ""last_activity_ago"": ""62d ago"",
            ""title"": ""ClaudeCodeExtension Issue #69 Implementation""
          },
          {
            ""id"": ""499f4bf5-dd42-47a1-adfb-330c2ab5a27f"",
            ""short_id"": ""499f4bf5"",
            ""working_directory"": ""/mnt/c/GitLab/Personal/ClaudeCodeExtension"",
            ""working_directory_display"": ""./"",
            ""last_activity_at"": 1775159940,
            ""title"": ""Oi""
          }
        ]";

        [TestMethod]
        public void ParseListResult_ReadsIdTitleCwdAndTimestamp()
        {
            var sessions = DevinSessionHistoryClient.ParseListResult(SampleListPayload);

            Assert.AreEqual(2, sessions.Count);
            DevinSessionSummary first = sessions[0];
            Assert.AreEqual("real-sweatpants", first.Id);
            Assert.AreEqual("ClaudeCodeExtension Issue #69 Implementation", first.Title);
            Assert.AreEqual("/mnt/c/GitLab/Personal/ClaudeCodeExtension", first.WorkingDirectory);
            Assert.AreEqual(
                DateTimeOffset.FromUnixTimeSeconds(1780922432).LocalDateTime,
                first.LastActivity);
        }

        [TestMethod]
        public void ParseListResult_ReturnsEmptyForBlankOrEmptyArray()
        {
            Assert.AreEqual(0, DevinSessionHistoryClient.ParseListResult(string.Empty).Count);
            Assert.AreEqual(0, DevinSessionHistoryClient.ParseListResult(null).Count);
            Assert.AreEqual(0, DevinSessionHistoryClient.ParseListResult("[]").Count);
        }

        [TestMethod]
        public void ParseListResult_SkipsMalformedInputWithoutThrowing()
        {
            Assert.AreEqual(0, DevinSessionHistoryClient.ParseListResult("not json").Count);
            // An array entry with no "id" is not a usable session row and must be dropped, not crash the list.
            Assert.AreEqual(0, DevinSessionHistoryClient.ParseListResult(@"[{""title"":""no id here""}]").Count);
        }

        [TestMethod]
        public void ParseListResult_ToleratesAWrappedSessionsObject()
        {
            var sessions = DevinSessionHistoryClient.ParseListResult(
                @"{""sessions"":[{""id"":""abc"",""title"":""Wrapped"",""last_activity_at"":1700000000}]}");

            Assert.AreEqual(1, sessions.Count);
            Assert.AreEqual("abc", sessions[0].Id);
        }

        [TestMethod]
        public void AppendUpdate_MergesConsecutiveChunksFromTheSameSpeaker()
        {
            var transcript = new DevinThreadTranscript();

            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""user_message_chunk"",""content"":{""type"":""text"",""text"":""Help with ""}}"));
            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""user_message_chunk"",""content"":{""type"":""text"",""text"":""this bug""}}"));
            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""agent_thought_chunk"",""content"":{""type"":""text"",""text"":""Let me look.""}}"));
            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""agent_message_chunk"",""content"":{""type"":""text"",""text"":""Fixed it.""}}"));

            Assert.AreEqual(3, transcript.Messages.Count);

            Assert.IsTrue(transcript.Messages[0].IsUser);
            Assert.AreEqual("Help with this bug", transcript.Messages[0].Text);

            Assert.IsTrue(transcript.Messages[1].IsThought);
            Assert.AreEqual("Let me look.", transcript.Messages[1].Text);

            Assert.IsFalse(transcript.Messages[2].IsUser);
            Assert.IsFalse(transcript.Messages[2].IsThought);
            Assert.AreEqual("Fixed it.", transcript.Messages[2].Text);
        }

        [TestMethod]
        public void AppendUpdate_IgnoresToolCallAndOtherInventoryUpdates()
        {
            var transcript = new DevinThreadTranscript();

            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""tool_call"",""toolCallId"":""1"",""title"":""Read file""}"));
            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""usage_update""}"));

            Assert.AreEqual(0, transcript.Messages.Count);
        }

        [TestMethod]
        public void AppendUpdate_IgnoresEmptyTextChunks()
        {
            var transcript = new DevinThreadTranscript();

            DevinSessionHistoryClient.AppendUpdate(transcript, JObject.Parse(
                @"{""sessionUpdate"":""agent_message_chunk"",""content"":{""type"":""text"",""text"":""""}}"));

            Assert.AreEqual(0, transcript.Messages.Count);
        }

        /// <summary>
        /// <see cref="AcpSessionOptions.ResumeSessionId"/> selects session/load vs session/new at
        /// runtime — it must never leak onto the launch command line the way a CLI flag would.
        /// </summary>
        [TestMethod]
        public void AcpCommandBuilder_DoesNotPutResumeSessionIdOnTheCommandLine()
        {
            var options = new AcpSessionOptions
            {
                ExecutablePath = "devin",
                ResumeSessionId = "real-sweatpants"
            };

            string arguments = AcpCommandBuilder.GetArguments(options);

            Assert.AreEqual("acp", arguments);
            StringAssert.DoesNotMatch(arguments, new System.Text.RegularExpressions.Regex("real-sweatpants"));
        }
    }
}
