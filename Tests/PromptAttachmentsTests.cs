/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Splitting the "Files attached:" block out of a sent prompt for the chat bubble
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class PromptAttachmentsTests
    {
        [TestMethod]
        public void PromptWithoutAttachmentsIsUnchanged()
        {
            var split = PromptAttachments.Split("fix the build");

            Assert.AreEqual("fix the build", split.Body);
            Assert.AreEqual(0, split.Files.Count);
        }

        [TestMethod]
        public void AttachmentBlockIsLiftedOutOfTheText()
        {
            string prompt = "Files attached:\r\n  - C:\\Temp\\s\\image_1.png\r\n  - C:\\Temp\\s\\image_2.png\r\n\r\nwhat is wrong here?\r\n";

            var split = PromptAttachments.Split(prompt);

            Assert.AreEqual("what is wrong here?", split.Body);
            CollectionAssert.AreEqual(new[] { "C:\\Temp\\s\\image_1.png", "C:\\Temp\\s\\image_2.png" }, (System.Collections.ICollection)split.Files);
        }

        [TestMethod]
        public void AttachmentsOnlyLeaveAnEmptyBody()
        {
            var split = PromptAttachments.Split("Files attached:\n  - C:\\a.png\n\n");

            Assert.AreEqual(string.Empty, split.Body);
            Assert.AreEqual(1, split.Files.Count);
        }

        [TestMethod]
        public void BlockAfterAPrependedSectionIsFound()
        {
            string prompt = "Resolve the conflicts first.\n\nFiles attached:\n  - C:\\a.png\n\nthen this";

            var split = PromptAttachments.Split(prompt);

            Assert.AreEqual("Resolve the conflicts first.\n\nthen this", split.Body);
            Assert.AreEqual("C:\\a.png", split.Files[0]);
        }

        [TestMethod]
        public void HeaderWithoutAListStaysText()
        {
            var split = PromptAttachments.Split("Files attached:\nnothing here");

            Assert.AreEqual("Files attached:\nnothing here", split.Body);
            Assert.AreEqual(0, split.Files.Count);
        }

        [TestMethod]
        public void WslPathsComeBackAsWindowsPaths()
        {
            var split = PromptAttachments.Split("Files attached:\n  - /mnt/c/Users/me/image_1.png\n\nhi");

            Assert.AreEqual("C:\\Users\\me\\image_1.png", split.Files[0]);
            Assert.AreEqual("/home/me/a.png", PromptAttachments.ToWindowsPath("/home/me/a.png"));
        }

        [TestMethod]
        public void UserRowShowsTheSplitTextOtherRowsDoNot()
        {
            string prompt = "Files attached:\n  - C:\\a.png\n\nhi";

            var user = new ChatMessageViewModel(ChatMessageKind.User) { Text = prompt };
            var assistant = new ChatMessageViewModel(ChatMessageKind.Assistant) { Text = prompt };

            Assert.AreEqual("hi", user.DisplayText);
            Assert.AreEqual(1, user.AttachedFiles.Count);
            Assert.AreEqual(prompt, user.Text);
            Assert.AreEqual(prompt, assistant.DisplayText);
            Assert.AreEqual(0, assistant.AttachedFiles.Count);
        }

        [TestMethod]
        public void PlainWorkingStatusIsOffByDefault()
        {
            Assert.IsFalse(new ClaudeCodeSettings().PlainChatWorkingStatus);
        }
    }
}
