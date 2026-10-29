/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Which thinking row the native chat opens with a question card, and the setting's default
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using ClaudeCodeVS;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatThinkingRowsTests
    {
        private static ChatMessageViewModel Row(ChatMessageKind kind, bool running = false)
        {
            return new ChatMessageViewModel(kind) { IsRunning = running };
        }

        [TestMethod]
        public void ThinkingRightBeforeTheQuestionToolIsFound()
        {
            ChatMessageViewModel thinking = Row(ChatMessageKind.Thinking);
            var messages = new List<ChatMessageViewModel>
            {
                Row(ChatMessageKind.User),
                thinking,
                Row(ChatMessageKind.ToolCall, running: true)
            };

            Assert.AreSame(thinking, ChatThinkingRows.FindBeforeQuestion(messages));
        }

        [TestMethod]
        public void ThinkingAsTheLastRowIsFound()
        {
            ChatMessageViewModel thinking = Row(ChatMessageKind.Thinking);
            var messages = new List<ChatMessageViewModel> { Row(ChatMessageKind.User), thinking };

            Assert.AreSame(thinking, ChatThinkingRows.FindBeforeQuestion(messages));
        }

        [TestMethod]
        public void VisibleTextBeforeTheQuestionLeavesTheThinkingClosed()
        {
            var messages = new List<ChatMessageViewModel>
            {
                Row(ChatMessageKind.Thinking),
                Row(ChatMessageKind.Assistant),
                Row(ChatMessageKind.ToolCall, running: true)
            };

            Assert.IsNull(ChatThinkingRows.FindBeforeQuestion(messages));
        }

        [TestMethod]
        public void AFinishedToolInBetweenLeavesTheEarlierThinkingClosed()
        {
            var messages = new List<ChatMessageViewModel>
            {
                Row(ChatMessageKind.Thinking),
                Row(ChatMessageKind.ToolCall),
                Row(ChatMessageKind.ToolCall, running: true)
            };

            Assert.IsNull(ChatThinkingRows.FindBeforeQuestion(messages));
        }

        [TestMethod]
        public void NoThinkingOrNoRowsFindsNothing()
        {
            Assert.IsNull(ChatThinkingRows.FindBeforeQuestion(new List<ChatMessageViewModel> { Row(ChatMessageKind.User) }));
            Assert.IsNull(ChatThinkingRows.FindBeforeQuestion(new List<ChatMessageViewModel>()));
            Assert.IsNull(ChatThinkingRows.FindBeforeQuestion(null));
        }

        [TestMethod]
        public void ThinkingStaysCollapsedByDefault()
        {
            Assert.AreEqual(ChatThinkingDisplay.Collapsed, new ClaudeCodeSettings().ChatThinkingDisplay,
                "Opening thinking rows is opt-in; without it the chat keeps them closed as before.");
        }
    }
}
