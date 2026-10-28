/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the file name Session History gives an exported transcript, which is also the
 *          caption of the document tab it opens in.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SessionTranscriptFileNameTests
    {
        [TestMethod]
        public void BuildTranscriptFileName_UsesTheTitleAndTheStartOfTheId()
        {
            var session = new SessionInfo { SessionId = "1a2b3c4d-5e6f-7a8b-9c0d-112233445566", Preview = "Navbar per Step workflow" };

            Assert.AreEqual("Navbar per Step workflow (1a2b3c4d).txt", ClaudeCodeControl.BuildTranscriptFileName(session));
        }

        [TestMethod]
        public void BuildTranscriptFileName_ReplacesCharactersWindowsRejects()
        {
            var session = new SessionInfo { SessionId = "abc", CustomTitle = "Fix: a/b \"quoted\"? *" };

            Assert.AreEqual("Fix_ a_b _quoted__ _ (abc).txt", ClaudeCodeControl.BuildTranscriptFileName(session));
        }

        [TestMethod]
        public void BuildTranscriptFileName_ShortensLongTitlesAndNamesUntitledSessions()
        {
            var longTitle = new SessionInfo { SessionId = "12345678abcd", Preview = new string('x', 100) };
            var untitled = new SessionInfo { SessionId = "12345678abcd", Preview = "   " };

            Assert.AreEqual(new string('x', 60) + " (12345678).txt", ClaudeCodeControl.BuildTranscriptFileName(longTitle));
            Assert.AreEqual("session (12345678).txt", ClaudeCodeControl.BuildTranscriptFileName(untitled));
        }
    }
}
