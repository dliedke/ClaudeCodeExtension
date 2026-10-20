/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for Qwen Code's ask_user_question over ACP shown as a question card (issue #203)
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Issue #203: Qwen Code asks its questions through <c>session/request_permission</c> with only
    /// "Submit" and "Cancel" as options, so the plain permission dialog showed two buttons and no
    /// question. Frames below follow Qwen Code's ACP integration (<c>permissionUtils.ts</c>,
    /// <c>Session.ts</c>) and its own ACP bridge, which keys answers by question index.
    /// </summary>
    [TestClass]
    public class AcpUserQuestionTests
    {
        private const string QuestionsJson =
            "[{\"question\":\"Which database?\",\"header\":\"Database\",\"multiSelect\":false," +
            "\"options\":[{\"label\":\"Postgres\",\"description\":\"Relational\"},{\"label\":\"Mongo\",\"description\":\"Document\"}]}," +
            "{\"question\":\"Which features?\",\"header\":\"Features\",\"multiSelect\":true," +
            "\"options\":[{\"label\":\"Auth\",\"description\":\"Login\"},{\"label\":\"Search\",\"description\":\"Full text\"}]}]";

        [TestMethod]
        public void ReadUserQuestions_TaggedRequest_ReturnsTheMetaQuestions()
        {
            JToken toolCall = JToken.Parse(
                "{\"toolCallId\":\"c1\",\"title\":\"Please answer\",\"kind\":\"other\"," +
                "\"_meta\":{\"toolName\":\"ask_user_question\",\"qwenInteractionKind\":\"user_question\",\"qwenQuestions\":" + QuestionsJson + "}}");

            JArray questions = AcpSession.ReadUserQuestions(toolCall);

            Assert.IsNotNull(questions);
            Assert.AreEqual(2, questions.Count);
            Assert.AreEqual("Which database?", (string)questions[0]["question"]);
        }

        [TestMethod]
        public void ReadUserQuestions_UntaggedBuild_FallsBackToToolNameAndRawInput()
        {
            JToken toolCall = JToken.Parse(
                "{\"toolCallId\":\"c1\",\"rawInput\":{\"questions\":" + QuestionsJson + "}," +
                "\"_meta\":{\"toolName\":\"ask_user_question\"}}");

            JArray questions = AcpSession.ReadUserQuestions(toolCall);

            Assert.IsNotNull(questions);
            Assert.AreEqual(2, questions.Count);
        }

        [TestMethod]
        public void ReadUserQuestions_OrdinaryToolApproval_ReturnsNull()
        {
            JToken toolCall = JToken.Parse(
                "{\"toolCallId\":\"c1\",\"title\":\"rm -rf build\",\"kind\":\"execute\"," +
                "\"rawInput\":{\"command\":\"rm -rf build\"},\"_meta\":{\"toolName\":\"run_shell_command\"}}");

            Assert.IsNull(AcpSession.ReadUserQuestions(toolCall));
            Assert.IsNull(AcpSession.ReadUserQuestions(null));
        }

        [TestMethod]
        public void ParseQuestions_QwenShape_ProducesAnswerableQuestions()
        {
            IReadOnlyList<AgentQuestion> questions =
                ClaudeStreamParser.ParseQuestions(new JObject { ["questions"] = JArray.Parse(QuestionsJson) });

            Assert.AreEqual(2, questions.Count);
            Assert.AreEqual("Database", questions[0].Header);
            Assert.AreEqual(2, questions[0].Options.Count);
            Assert.IsTrue(questions[1].MultiSelect);
        }

        [TestMethod]
        public void BuildUserQuestionAnswers_KeysByQuestionIndex()
        {
            var answers = new Dictionary<string, string>
            {
                { "Which database?", "Postgres" },
                { "Which features?", "Auth, Search" }
            };

            JObject map = AcpSession.BuildUserQuestionAnswers(JArray.Parse(QuestionsJson), answers);

            Assert.AreEqual("Postgres", (string)map["0"]);
            Assert.AreEqual("Auth, Search", (string)map["1"]);
            Assert.AreEqual(2, map.Count);
        }

        [TestMethod]
        public void BuildUserQuestionAnswers_KeepsOriginalIndexWhenAQuestionIsSkipped()
        {
            // The card drops a question with no text, so its index must still come from the
            // original array, never from the card's position.
            JArray array = JArray.Parse(
                "[{\"question\":\"\",\"header\":\"x\",\"options\":[]}," +
                "{\"question\":\"Which database?\",\"header\":\"Database\",\"options\":[{\"label\":\"Postgres\",\"description\":\"d\"}]}]");

            JObject map = AcpSession.BuildUserQuestionAnswers(array,
                new Dictionary<string, string> { { "Which database?", "Postgres" } });

            Assert.AreEqual(1, map.Count);
            Assert.AreEqual("Postgres", (string)map["1"]);
        }

        [TestMethod]
        public void BuildUserQuestionAnswers_BlankAnswersAreOmitted()
        {
            JObject map = AcpSession.BuildUserQuestionAnswers(JArray.Parse(QuestionsJson),
                new Dictionary<string, string> { { "Which database?", "" } });

            Assert.AreEqual(0, map.Count);
            Assert.AreEqual(0, AcpSession.BuildUserQuestionAnswers(JArray.Parse(QuestionsJson), null).Count);
        }

        [TestMethod]
        public void FindOptionId_PicksSubmitAndCancelByKind()
        {
            var options = new List<AgentPermissionOption>
            {
                new AgentPermissionOption { OptionId = "proceed_once", Name = "Submit", Kind = "allow_once" },
                new AgentPermissionOption { OptionId = "cancel", Name = "Cancel", Kind = "reject_once" }
            };

            Assert.AreEqual("proceed_once", AcpSession.FindOptionId(options, "allow"));
            Assert.AreEqual("cancel", AcpSession.FindOptionId(options, "reject"));
            Assert.IsNull(AcpSession.FindOptionId(new List<AgentPermissionOption>(), "allow"));
        }
    }
}
