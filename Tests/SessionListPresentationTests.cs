/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for how past conversations read in Session History and the welcome card (preview cleanup, date groups)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using ClaudeCodeVS;
using ClaudeCodeVS.Diff;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SessionListPresentationTests
    {
        [TestMethod]
        public void CleanPromptPreview_DropsTheAttachmentList()
        {
            string prompt = "Files attached:\n  - C:\\Temp\\ClaudeCodeVS_Session\\x\\image_1.png\n\nWhy is this button misaligned?";

            Assert.AreEqual("Why is this button misaligned?", SessionListPresentation.CleanPromptPreview(prompt));
        }

        [TestMethod]
        public void CleanPromptPreview_DropsThePullConflictHandOff()
        {
            var outcome = new GitPullOutcome
            {
                Kind = GitPullOutcomeKind.Conflicts,
                ConflictedFiles = new List<string> { "Controls/A.cs" },
                MergeInProgress = true
            };
            string prompt = ClaudeCodeControl.BuildConflictPromptBlock(outcome, hasUserRequest: true) + "Add a logout button";

            Assert.AreEqual("Add a logout button", SessionListPresentation.CleanPromptPreview(prompt));
        }

        [TestMethod]
        public void CleanPromptPreview_ConflictOnlyPromptLeavesNothing()
        {
            var outcome = new GitPullOutcome
            {
                Kind = GitPullOutcomeKind.Conflicts,
                ConflictedFiles = new List<string> { "Controls/A.cs" },
                MergeInProgress = false
            };
            string prompt = ClaudeCodeControl.BuildConflictPromptBlock(outcome, hasUserRequest: false);

            Assert.AreEqual(string.Empty, SessionListPresentation.CleanPromptPreview(prompt));
        }

        [TestMethod]
        public void CleanPromptPreview_DropsTheReviewUndoNote()
        {
            string prompt = PendingReviewMessages.BuildUndoneFilesNote(new[] { "A.cs" }) + "Try again without the cache";

            Assert.AreEqual("Try again without the cache", SessionListPresentation.CleanPromptPreview(prompt));
        }

        [TestMethod]
        public void CleanPromptPreview_AttachmentsOnlyLeaveNothing()
        {
            Assert.AreEqual(string.Empty,
                SessionListPresentation.CleanPromptPreview("Files attached:\n  - C:\\Temp\\a.png\n"));
        }

        [TestMethod]
        public void CleanPromptPreview_CollapsesWhitespaceAndTruncates()
        {
            Assert.AreEqual("one two three", SessionListPresentation.CleanPromptPreview("  one\r\n\r\ntwo \t three  "));

            string longPrompt = new string('x', SessionListPresentation.PreviewMaxChars + 30);
            string preview = SessionListPresentation.CleanPromptPreview(longPrompt);
            Assert.AreEqual(SessionListPresentation.PreviewMaxChars + 1, preview.Length);
            Assert.IsTrue(preview.EndsWith("…"));
        }

        [TestMethod]
        public void GetDateGroup_UsesCalendarDays()
        {
            var now = new DateTime(2026, 10, 25, 9, 0, 0);

            Assert.AreEqual(SessionDateGroup.Today, SessionListPresentation.GetDateGroup(new DateTime(2026, 10, 25, 0, 5, 0), now));
            Assert.AreEqual(SessionDateGroup.Today, SessionListPresentation.GetDateGroup(now.AddMinutes(5), now), "Clock skew counts as today.");
            Assert.AreEqual(SessionDateGroup.Yesterday, SessionListPresentation.GetDateGroup(new DateTime(2026, 10, 24, 23, 59, 0), now));
            Assert.AreEqual(SessionDateGroup.Last7Days, SessionListPresentation.GetDateGroup(new DateTime(2026, 10, 19, 12, 0, 0), now));
            Assert.AreEqual(SessionDateGroup.Last30Days, SessionListPresentation.GetDateGroup(new DateTime(2026, 10, 18, 12, 0, 0), now));
            Assert.AreEqual(SessionDateGroup.Older, SessionListPresentation.GetDateGroup(new DateTime(2026, 9, 25, 12, 0, 0), now));
        }
    }
}
