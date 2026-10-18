/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the "Show active subagent count" transcript tracker: foreground and background subagent
 *          start/finish, interruptions, and lines that must not count. Line shapes follow Claude Code 2.1.294 transcripts.
 *
 * *******************************************************************************************************************/

using System.Linq;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SubagentActivityTests
    {
        private static string AgentCall(string id, string type = "Explore", string description = "Explore services", string name = "Agent")
        {
            return "{\"type\":\"assistant\",\"timestamp\":\"2026-10-08T10:20:38.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[" +
                   "{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"input\":{\"subagent_type\":\"" + type +
                   "\",\"description\":\"" + description + "\",\"prompt\":\"look around\"}}]}}";
        }

        private static string Result(string id, string toolUseResult = "{\"status\":\"completed\"}")
        {
            return "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"" + id +
                   "\",\"type\":\"tool_result\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}]},\"toolUseResult\":" + toolUseResult + "}";
        }

        private const string AsyncLaunched = "{\"isAsync\":true,\"status\":\"async_launched\",\"agentId\":\"a45a3c4867fa78a23\"}";

        private static string Notification(string id, string status = "completed")
        {
            return "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"2026-10-08T10:20:47.237Z\",\"content\":" +
                   "\"<task-notification>\\n<task-id>a45a3c4867fa78a23</task-id>\\n<tool-use-id>" + id + "</tool-use-id>\\n<status>" + status +
                   "</status>\\n<summary>Agent finished</summary>\\n</task-notification>\"}";
        }

        [TestMethod]
        public void ForegroundAgent_CountsUntilItsToolResult()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));

            Assert.AreEqual(1, tracker.Running.Count);
            RunningSubagent agent = tracker.Running.Single();
            Assert.AreEqual("Explore", agent.AgentType);
            Assert.AreEqual("Explore services", agent.Description);
            Assert.AreEqual(2026, agent.StartedUtc.Year);
            Assert.AreEqual(10, agent.StartedUtc.Hour);
            Assert.IsFalse(agent.IsBackground);

            tracker.ProcessLine(Result("toolu_1"));
            Assert.AreEqual(0, tracker.Running.Count);
        }

        [TestMethod]
        public void BackgroundAgent_CountsUntilItsTaskNotification()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));
            tracker.ProcessLine(Result("toolu_1", AsyncLaunched));

            Assert.AreEqual(1, tracker.Running.Count);
            Assert.IsTrue(tracker.Running.Single().IsBackground);

            tracker.ProcessLine(Notification("toolu_1"));
            Assert.AreEqual(0, tracker.Running.Count);
        }

        [TestMethod]
        public void BackgroundAgent_NotificationNamingOnlyTheTaskId_EndsIt()
        {
            // CLI 2.1.296 (native mode) left <tool-use-id> out of the notification; the task id is the agent id.
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));
            tracker.ProcessLine(Result("toolu_1", AsyncLaunched));
            Assert.AreEqual("a45a3c4867fa78a23", tracker.Running.Single().AgentId);

            tracker.ProcessLine("{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"<task-notification>\\n<task-id>a45a3c4867fa78a23</task-id>\\n<status>completed</status>\\n</task-notification>\"}");
            Assert.AreEqual(0, tracker.Running.Count);
        }

        [TestMethod]
        public void RunningStatusNotification_DoesNotEndTheAgent()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));
            tracker.ProcessLine(Result("toolu_1", AsyncLaunched));
            tracker.ProcessLine(Notification("toolu_1", "running"));

            Assert.AreEqual(1, tracker.Running.Count);
        }

        [TestMethod]
        public void InterruptedAgent_EndsWithItsErrorResult()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));
            tracker.ProcessLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\"," +
                                "\"is_error\":true,\"content\":\"[Request interrupted by user for tool use]\"}]},\"toolUseResult\":\"Error: interrupted\"}");

            Assert.AreEqual(0, tracker.Running.Count);
        }

        [TestMethod]
        public void ParallelAgents_AndLegacyTaskName_AreCountedSeparately()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));
            tracker.ProcessLine(AgentCall("toolu_2", "general-purpose", "Review", name: "Task"));
            tracker.ProcessLine(AgentCall("toolu_3"));
            Assert.AreEqual(3, tracker.Running.Count);

            tracker.ProcessLine(Result("toolu_2"));
            CollectionAssert.AreEquivalent(new[] { "toolu_1", "toolu_3" }, tracker.Running.Select(a => a.ToolUseId).ToArray());
        }

        [TestMethod]
        public void OtherToolsAndSidechainLines_AreIgnored()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"Bash\",\"input\":{\"command\":\"dir\"}}]}}");
            tracker.ProcessLine("{\"type\":\"assistant\",\"isSidechain\":true,\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_8\",\"name\":\"Agent\",\"input\":{}}]}}");
            tracker.ProcessLine("not json \"tool_use\"");
            tracker.ProcessLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"plain prompt\"}}");
            tracker.ProcessLine("");

            Assert.AreEqual(0, tracker.Running.Count);
        }

        [TestMethod]
        public void NotificationDeliveredAsUserMessage_AlsoEndsTheAgent()
        {
            var tracker = new SubagentActivityTracker();
            tracker.ProcessLine(AgentCall("toolu_1"));
            tracker.ProcessLine(Result("toolu_1", AsyncLaunched));
            tracker.ProcessLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<task-notification>\\n<tool-use-id>toolu_1</tool-use-id>\\n<status>killed</status>\\n</task-notification>\"}}");

            Assert.AreEqual(0, tracker.Running.Count);
        }
    }
}
