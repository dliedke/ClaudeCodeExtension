/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers Codex App Server history parsing and first-turn resume command construction
 *
 * *******************************************************************************************************************/

using System.Linq;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class CodexSessionHistoryTests
    {
        [TestMethod]
        public void ThreadListParser_ReadsSummaryAndFlattensPreview()
        {
            JObject result = JObject.Parse(@"{
              'data': [{
                'id': 'thread-123',
                'name': 'Named session',
                'preview': 'fix the build\nthen run tests',
                'cwd': 'C:\\repo',
                'path': 'C:\\Users\\me\\.codex\\sessions\\rollout.jsonl',
                'createdAt': 1767225600,
                'updatedAt': 1767225660
              }]
            }");

            CodexThreadSummary summary = CodexAppServerClient.ParseThreadListResult(result).Single();

            Assert.AreEqual("thread-123", summary.Id);
            Assert.AreEqual("Named session", summary.Name);
            Assert.AreEqual("fix the build then run tests", summary.Preview);
            Assert.AreEqual(@"C:\repo", summary.Cwd);
            Assert.AreNotEqual(default(System.DateTime), summary.LastModified);
        }

        [TestMethod]
        public void ThreadReadParser_ExtractsConversationAndToolMarkers()
        {
            JObject result = JObject.Parse(@"{
              'thread': {
                'id': 'thread-123',
                'preview': 'hello',
                'cwd': '/mnt/c/repo',
                'updatedAt': 1767225660,
                'turns': [{
                  'startedAt': 1767225600,
                  'items': [
                    {'type':'userMessage','content':[{'type':'text','text':'hello'},{'type':'localImage','path':'/tmp/x.png'}]},
                    {'type':'commandExecution','command':'dotnet test'},
                    {'type':'agentMessage','text':'done','phase':'final_answer'}
                  ]
                }]
              }
            }");

            CodexThreadTranscript transcript = CodexAppServerClient.ParseThreadReadResult(result);

            Assert.AreEqual(3, transcript.Messages.Count);
            Assert.IsTrue(transcript.Messages[0].IsUser);
            StringAssert.Contains(transcript.Messages[0].Text, "hello");
            StringAssert.Contains(transcript.Messages[0].Text, "[image]");
            Assert.IsTrue(transcript.Messages[1].IsTool);
            Assert.AreEqual("[tool: command]", transcript.Messages[1].Text);
            Assert.AreEqual("done", transcript.Messages[2].Text);
        }

        [TestMethod]
        public void RolloutParser_RestoresMessagesWhenAppServerTurnsAreEmpty()
        {
            string[] lines =
            {
                "{\"timestamp\":\"2026-06-19T12:02:25Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context>ignored</environment_context>\"}]}}",
                "{\"timestamp\":\"2026-06-19T12:02:25Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Hello\"}}",
                "{\"timestamp\":\"2026-06-19T12:02:26Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"Hi there\"}}",
                "not json"
            };

            var messages = CodexAppServerClient.ParseRolloutLines(lines);

            Assert.AreEqual(2, messages.Count);
            Assert.IsTrue(messages[0].IsUser);
            Assert.AreEqual("Hello", messages[0].Text);
            Assert.IsFalse(messages[1].IsUser);
            Assert.AreEqual("Hi there", messages[1].Text);
        }

        [TestMethod]
        public void OneShotSession_UsesHistoryIdOnItsFirstTurn()
        {
            var options = new OneShotSessionOptions
            {
                ResumeSessionId = "thread-123"
            };

            var session = new OneShotResumeSession(options, new CodexExecProtocol());

            Assert.AreEqual("thread-123", session.SessionId);
            Assert.AreEqual("thread-123", session.ResumableSessionId);
        }

        [TestMethod]
        public void AppServerCommandBuilder_UsesTheOwningWslWorkspace()
        {
            JsonLineProcessOptions process = CodexAppServerClient.BuildProcessOptions(new CodexAppServerOptions
            {
                UseWsl = true,
                ExecutablePath = "/home/me/bin/codex",
                WorkingDirectory = @"C:\repo",
                WslWorkingDirectory = "/mnt/c/repo"
            });

            Assert.AreEqual("wsl.exe", process.FileName);
            StringAssert.Contains(process.Arguments, "cd '/mnt/c/repo'");
            StringAssert.Contains(process.Arguments, "'/home/me/bin/codex' app-server --stdio");
        }
    }
}
