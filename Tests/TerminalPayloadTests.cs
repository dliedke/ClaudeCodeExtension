/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers what is delivered to the embedded terminal — trailing-newline stripping (issue
 *          #108) and the lenient clipboard read-back comparison (issue #59).
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class TerminalPayloadTests
    {
        /// <summary>
        /// Issue #108: every trailing LF that reached the CLI outside a paste burst left a permanent
        /// blank line in its input box, accumulating into a huge blank prefix over a session.
        /// </summary>
        [TestMethod]
        public void StripTrailingNewlines_RemovesTrailingCarriageReturnsAndLineFeeds()
        {
            Assert.AreEqual("fix the bug", ClaudeCodeControl.StripTrailingNewlines("fix the bug\r\n"));
            Assert.AreEqual("fix the bug", ClaudeCodeControl.StripTrailingNewlines("fix the bug\n"));
            Assert.AreEqual("fix the bug", ClaudeCodeControl.StripTrailingNewlines("fix the bug\r"));
        }

        /// <summary>
        /// Multi-line prompts go to Claude Code as one explicit bracketed paste. Without the markers
        /// conhost's paste turns every line break into Enter: short prompts were split into several
        /// messages, longer ones were held back with "review and press Enter to send".
        /// </summary>
        [TestMethod]
        public void WrapMultilineAsBracketedPaste_WrapsAndNormalizesLineBreaksToLf()
        {
            Assert.AreEqual("\u001b[200~a\nb\u001b[201~", ClaudeCodeControl.WrapMultilineAsBracketedPaste("a\r\nb"));
            Assert.AreEqual("\u001b[200~a\nb\u001b[201~", ClaudeCodeControl.WrapMultilineAsBracketedPaste("a\nb"));
            Assert.AreEqual("\u001b[200~a\nb\u001b[201~", ClaudeCodeControl.WrapMultilineAsBracketedPaste("a\rb"),
                "A lone CR is what Claude Code counts as a hidden character, so it must not survive.");
            Assert.AreEqual("\u001b[200~a\n\nb\u001b[201~", ClaudeCodeControl.WrapMultilineAsBracketedPaste("a\r\n\r\nb"));
        }

        [TestMethod]
        public void WrapMultilineAsBracketedPaste_LeavesSingleLineAndEmptyTextAlone()
        {
            Assert.AreEqual("/model opus", ClaudeCodeControl.WrapMultilineAsBracketedPaste("/model opus"),
                "Slash commands and other one-liners keep the plain paste they always used.");
            Assert.AreEqual("", ClaudeCodeControl.WrapMultilineAsBracketedPaste(""));
            Assert.IsNull(ClaudeCodeControl.WrapMultilineAsBracketedPaste(null));
        }

        [TestMethod]
        public void WrapMultilineAsBracketedPaste_DropsEscSoTheTextCannotEndThePasteEarly()
        {
            string wrapped = ClaudeCodeControl.WrapMultilineAsBracketedPaste("a\u001b[201~\nb");
            Assert.AreEqual("\u001b[200~a[201~\nb\u001b[201~", wrapped);
            Assert.AreEqual(2, wrapped.Split('\u001b').Length - 1, "Only the two markers may carry an ESC.");
        }

        [TestMethod]
        public void StripTrailingNewlines_RemovesRepeatedTrailingNewlines()
        {
            Assert.AreEqual("prompt", ClaudeCodeControl.StripTrailingNewlines("prompt\r\n\r\n\r\n"));
        }

        /// <summary>
        /// Multi-line prompts must survive intact — only the trailing newlines are the problem.
        /// </summary>
        [TestMethod]
        public void StripTrailingNewlines_PreservesInteriorNewlines()
        {
            Assert.AreEqual(
                "line one\nline two",
                ClaudeCodeControl.StripTrailingNewlines("line one\nline two\n"));
        }

        [TestMethod]
        public void StripTrailingNewlines_HandlesNullAndEmpty()
        {
            Assert.IsNull(ClaudeCodeControl.StripTrailingNewlines(null));
            Assert.AreEqual(string.Empty, ClaudeCodeControl.StripTrailingNewlines(string.Empty));
            Assert.AreEqual(string.Empty, ClaudeCodeControl.StripTrailingNewlines("\r\n"));
        }

        [TestMethod]
        public void ClipboardTextMatches_AcceptsIdenticalText()
        {
            Assert.IsTrue(ClaudeCodeControl.ClipboardTextMatches("hello", "hello"));
        }

        /// <summary>
        /// Issue #59: the clipboard round-trip normalizes line endings, and rejecting that made the
        /// verify step refuse content that would have pasted correctly.
        /// </summary>
        [TestMethod]
        public void ClipboardTextMatches_ToleratesLineEndingNormalization()
        {
            Assert.IsTrue(ClaudeCodeControl.ClipboardTextMatches("a\r\nb", "a\nb"));
            Assert.IsTrue(ClaudeCodeControl.ClipboardTextMatches("a\rb", "a\nb"));
        }

        [TestMethod]
        public void ClipboardTextMatches_ToleratesTrailingNulTerminator()
        {
            Assert.IsTrue(ClaudeCodeControl.ClipboardTextMatches("hello\0", "hello"));
        }

        [TestMethod]
        public void ClipboardTextMatches_ToleratesAnAppendedTrailingNewline()
        {
            Assert.IsTrue(ClaudeCodeControl.ClipboardTextMatches("hello\n", "hello"));
            Assert.IsTrue(ClaudeCodeControl.ClipboardTextMatches("hello\r\n", "hello"));
        }

        [TestMethod]
        public void ClipboardTextMatches_RejectsGenuinelyDifferentText()
        {
            Assert.IsFalse(ClaudeCodeControl.ClipboardTextMatches("hello", "goodbye"));
            Assert.IsFalse(ClaudeCodeControl.ClipboardTextMatches("hello world", "hello"));
        }

        [TestMethod]
        public void ClipboardTextMatches_RejectsNulls()
        {
            Assert.IsFalse(ClaudeCodeControl.ClipboardTextMatches(null, "hello"));
            Assert.IsFalse(ClaudeCodeControl.ClipboardTextMatches("hello", null));
            Assert.IsFalse(ClaudeCodeControl.ClipboardTextMatches(null, null));
        }
    }
}
