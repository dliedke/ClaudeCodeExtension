/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the native chat's replay of a resumed Claude transcript: tool cards paired with their results,
 *          the message cap, the tool card cap with its summary rows, and the diff built on first open.
 *
 * *******************************************************************************************************************/

using System.IO;
using System.Linq;
using System.Text;
using ClaudeCodeVS;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ResumedTranscriptReplayTests
    {
        private static string User(string text)
        {
            return @"{""type"":""user"",""message"":{""role"":""user"",""content"":""" + text + @"""}}";
        }

        private static string AssistantText(string text)
        {
            return @"{""type"":""assistant"",""message"":{""content"":[{""type"":""text"",""text"":""" + text + @"""}]}}";
        }

        private static string Read(string id, string file)
        {
            return @"{""type"":""assistant"",""message"":{""content"":[{""type"":""tool_use"",""id"":""" + id +
                   @""",""name"":""Read"",""input"":{""file_path"":""" + file + @"""}}]}}";
        }

        private static string Result(string id, string text, bool isError = false)
        {
            return @"{""type"":""user"",""message"":{""role"":""user"",""content"":[{""type"":""tool_result"",""tool_use_id"":""" +
                   id + @""",""content"":""" + text + @""",""is_error"":" + (isError ? "true" : "false") + "}]}}";
        }

        private static ClaudeCodeControl.ResumedTranscript Replay(int maxMessages, int maxToolCards, int maxSummaryLines,
            params string[] lines)
        {
            var jsonl = new StringBuilder();
            foreach (string line in lines)
            {
                jsonl.AppendLine(line);
            }

            using (var reader = new StringReader(jsonl.ToString()))
            {
                return ClaudeCodeControl.ReadResumedTranscript(reader, maxMessages, maxToolCards, maxSummaryLines);
            }
        }

        [TestMethod]
        public void ToolCallBecomesACardWithItsResult()
        {
            var replay = Replay(200, 500, 30,
                User("Read the file"),
                @"{""type"":""assistant"",""message"":{""content"":[" +
                    @"{""type"":""text"",""text"":""Let me look.""}," +
                    @"{""type"":""tool_use"",""id"":""t1"",""name"":""Read"",""input"":{""file_path"":""C:\\src\\Foo.cs""}}]}}",
                Result("t1", "class Foo {}"),
                AssistantText("Done."));

            CollectionAssert.AreEqual(
                new[] { ChatMessageKind.User, ChatMessageKind.Assistant, ChatMessageKind.ToolCall, ChatMessageKind.Assistant },
                replay.Rows.Select(row => row.Kind).ToArray());

            ClaudeCodeControl.ResumedTranscriptRow card = replay.Rows[2];
            Assert.AreEqual("t1", card.ToolCallId);
            Assert.AreEqual("Read", card.ToolName);
            StringAssert.Contains(card.ToolInputJson, "Foo.cs");
            Assert.AreEqual("class Foo {}", card.ToolResult);
            Assert.IsFalse(card.IsError);

            // The tool_result line is no user bubble, and cards do not count as messages.
            Assert.AreEqual(3, replay.TotalMessages);
            Assert.AreEqual(3, replay.ShownMessages);
        }

        [TestMethod]
        public void FailedToolResultIsKeptAsAnError()
        {
            var replay = Replay(200, 500, 30,
                User("Go"),
                Read("t1", "missing.cs"),
                Result("t1", "File does not exist.", isError: true));

            ClaudeCodeControl.ResumedTranscriptRow card = replay.Rows.Single(row => row.Kind == ChatMessageKind.ToolCall);
            Assert.IsTrue(card.IsError);
            Assert.AreEqual("File does not exist.", card.ToolResult);
        }

        [TestMethod]
        public void MessageCapCountsOnlyMessagesAndDropsTheCardsOfDroppedMessages()
        {
            var replay = Replay(2, 500, 30,
                User("first"),
                Read("t1", "a.cs"),
                Result("t1", "a"),
                User("second"),
                Read("t2", "b.cs"),
                Read("t3", "c.cs"),
                AssistantText("third"));

            // "first" falls out together with its card; the two cards after "second" stay.
            CollectionAssert.AreEqual(
                new[] { ChatMessageKind.User, ChatMessageKind.ToolCall, ChatMessageKind.ToolCall, ChatMessageKind.Assistant },
                replay.Rows.Select(row => row.Kind).ToArray());
            Assert.AreEqual("second", replay.Rows[0].Text);
            Assert.AreEqual(3, replay.TotalMessages);
            Assert.AreEqual(2, replay.ShownMessages);
        }

        [TestMethod]
        public void OlderToolCallsBeyondTheCardCapAreMergedIntoOneSummaryRow()
        {
            var replay = Replay(200, 2, 30,
                User("Go"),
                Read("t1", "a.cs"),
                Result("t1", "a"),
                Read("t2", "b.cs"),
                Read("t3", "c.cs"),
                Read("t4", "d.cs"));

            CollectionAssert.AreEqual(
                new[] { ChatMessageKind.User, ChatMessageKind.Notice, ChatMessageKind.ToolCall, ChatMessageKind.ToolCall },
                replay.Rows.Select(row => row.Kind).ToArray());

            string summary = replay.Rows[1].Text;
            StringAssert.StartsWith(summary, "2 earlier tool calls:");
            StringAssert.Contains(summary, "Read · a.cs");
            StringAssert.Contains(summary, "Read · b.cs");
            Assert.AreEqual(2, replay.SummarizedToolCalls);
            CollectionAssert.AreEqual(new[] { "t3", "t4" },
                replay.Rows.Where(row => row.Kind == ChatMessageKind.ToolCall).Select(row => row.ToolCallId).ToArray());
        }

        [TestMethod]
        public void SummaryRowsAreSplitByMessagesAndCappedInLength()
        {
            var replay = Replay(200, 1, 1,
                User("one"),
                Read("t1", "a.cs"),
                Read("t2", "b.cs"),
                Read("t3", "c.cs"),
                User("two"),
                Read("t4", "d.cs"),
                Read("t5", "e.cs"));

            CollectionAssert.AreEqual(
                new[] { ChatMessageKind.User, ChatMessageKind.Notice, ChatMessageKind.User, ChatMessageKind.Notice, ChatMessageKind.ToolCall },
                replay.Rows.Select(row => row.Kind).ToArray());

            Assert.AreEqual("3 earlier tool calls:\n▤ Read · a.cs\n… and 2 more", replay.Rows[1].Text);
            Assert.AreEqual("1 earlier tool call:\n▤ Read · d.cs", replay.Rows[3].Text);
        }

        [TestMethod]
        public void ResultOfASummarizedCallIsIgnored()
        {
            var replay = Replay(200, 1, 30,
                User("Go"),
                Read("t1", "a.cs"),
                Read("t2", "b.cs"),
                Result("t1", "late result"),
                Result("t2", "b"));

            ClaudeCodeControl.ResumedTranscriptRow card = replay.Rows.Single(row => row.Kind == ChatMessageKind.ToolCall);
            Assert.AreEqual("t2", card.ToolCallId);
            Assert.AreEqual("b", card.ToolResult);
            Assert.IsFalse(replay.Rows.Any(row => row.Text.Contains("late result")));
        }

        [TestMethod]
        public void ThinkingPartsAreNotReplayedButTextPartsAre()
        {
            var replay = Replay(200, 500, 30,
                User("Explain"),
                @"{""type"":""assistant"",""message"":{""content"":[" +
                    @"{""type"":""thinking"",""thinking"":""private reasoning""}," +
                    @"{""type"":""text"",""text"":""Visible answer.""}]}}",
                @"{""type"":""assistant"",""message"":{""content"":[" +
                    @"{""type"":""thinking"",""thinking"":""thinking only""}]}}");

            // Thinking dominates a real transcript's size, so it is not replayed. A text part in the
            // same message still is, and a thinking-only message adds no row at all.
            CollectionAssert.AreEqual(
                new[] { ChatMessageKind.User, ChatMessageKind.Assistant },
                replay.Rows.Select(row => row.Kind).ToArray());
            Assert.AreEqual("Visible answer.", replay.Rows[1].Text.Trim());
            Assert.IsFalse(replay.Rows.Any(row => row.Text.Contains("private reasoning")));
            Assert.IsFalse(replay.Rows.Any(row => row.Text.Contains("thinking only")));
        }

        [TestMethod]
        public void DescribeWithoutDiffMarksItDeferred()
        {
            const string input = @"{""file_path"":""C:\\src\\Foo.cs"",""old_string"":""a"",""new_string"":""b""}";

            ChatToolPresentation deferred = ChatToolPresenter.Describe("Edit", input, includeDiff: false);
            Assert.IsTrue(deferred.DiffDeferred);
            Assert.IsFalse(deferred.HasDiff);
            Assert.AreEqual(string.Empty, deferred.Badge);

            ChatToolPresentation full = ChatToolPresenter.Describe("Edit", input, includeDiff: true);
            Assert.IsFalse(full.DiffDeferred);
            Assert.IsTrue(full.HasDiff);
            Assert.AreEqual("+1 -1", full.Badge);
        }

        [TestMethod]
        public void DescribeWithoutDiffLeavesNonEditingToolsAlone()
        {
            ChatToolPresentation read = ChatToolPresenter.Describe("Read", @"{""file_path"":""a.cs""}", includeDiff: false);
            Assert.IsFalse(read.DiffDeferred);
        }

        [TestMethod]
        public void DeferredDiffIsBuiltWhenTheCardIsFirstOpened()
        {
            const string input = @"{""file_path"":""C:\\src\\Foo.cs"",""old_string"":""a"",""new_string"":""b""}";
            int builds = 0;

            var message = new ChatMessageViewModel(ChatMessageKind.ToolCall) { ToolInputJson = input };
            message.SetDeferredDiff(() =>
            {
                builds++;
                return ChatToolPresenter.Describe("Edit", input, includeDiff: true);
            });

            Assert.IsFalse(message.HasDiff);
            Assert.AreEqual(0, builds);

            message.IsExpanded = true;
            Assert.IsTrue(message.HasDiff);
            Assert.AreEqual("+1 -1", message.ToolBadge);

            message.IsExpanded = false;
            message.IsExpanded = true;
            Assert.AreEqual(1, builds);
        }
    }
}
