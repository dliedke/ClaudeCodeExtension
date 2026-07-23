/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the path logic that locates Claude Code session transcripts and that compares
 *          workspace directories coming from different Visual Studio sources.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SessionAndWorkspacePathTests
    {
        [TestMethod]
        public void EncodeClaudeProjectPath_ReplacesEveryNonAlphanumericWithASingleDash()
        {
            Assert.AreEqual("C--Users-Daniel-Liedke", ClaudeCodeControl.EncodeClaudeProjectPath(@"C:\Users\Daniel_Liedke"));
            Assert.AreEqual("C--GitLab-ClaudeCodeExtension", ClaudeCodeControl.EncodeClaudeProjectPath(@"C:\GitLab\ClaudeCodeExtension"));
        }

        /// <summary>
        /// The CLI encodes with an ASCII-only rule, so Unicode-aware char.IsLetterOrDigit must NOT be
        /// used here — accented and non-Latin characters become dashes just like punctuation. Getting
        /// this wrong points the session list at a directory that does not exist.
        /// </summary>
        [TestMethod]
        public void EncodeClaudeProjectPath_TreatsNonAsciiLettersAsSeparators()
        {
            // "ç" and "ã" each become one dash, exactly like the ":" and "\" around them.
            Assert.AreEqual("C--proje--o", ClaudeCodeControl.EncodeClaudeProjectPath(@"C:\projeção"));
            Assert.AreEqual("C-----", ClaudeCodeControl.EncodeClaudeProjectPath(@"C:\日本語"));
        }

        [TestMethod]
        public void EncodeClaudeProjectPath_ReturnsEmptyForNullOrEmpty()
        {
            Assert.AreEqual(string.Empty, ClaudeCodeControl.EncodeClaudeProjectPath(null));
            Assert.AreEqual(string.Empty, ClaudeCodeControl.EncodeClaudeProjectPath(string.Empty));
        }

        [TestMethod]
        public void NormalizeWorkspaceDirectory_StripsTrailingSeparatorsAndWhitespace()
        {
            Assert.AreEqual(@"C:\GitLab\Project", ClaudeCodeControl.NormalizeWorkspaceDirectory(@"C:\GitLab\Project\"));
            Assert.AreEqual(@"C:\GitLab\Project", ClaudeCodeControl.NormalizeWorkspaceDirectory(@"  C:\GitLab\Project  "));
            Assert.AreEqual(@"C:\GitLab\Project", ClaudeCodeControl.NormalizeWorkspaceDirectory(@"C:\GitLab\Project/"));
        }

        [TestMethod]
        public void NormalizeWorkspaceDirectory_ResolvesRelativeSegments()
        {
            Assert.AreEqual(@"C:\GitLab\Project", ClaudeCodeControl.NormalizeWorkspaceDirectory(@"C:\GitLab\Other\..\Project"));
        }

        [TestMethod]
        public void NormalizeWorkspaceDirectory_PassesThroughNullAndWhitespace()
        {
            Assert.IsNull(ClaudeCodeControl.NormalizeWorkspaceDirectory(null));
            Assert.AreEqual("   ", ClaudeCodeControl.NormalizeWorkspaceDirectory("   "));
        }

        /// <summary>
        /// DTE and IVsSolution hand back the same directory in different shapes; treating those as
        /// different workspaces is what triggers a needless terminal restart.
        /// </summary>
        [TestMethod]
        public void WorkspaceDirectoriesEqual_IgnoresCaseAndTrailingSeparator()
        {
            Assert.IsTrue(ClaudeCodeControl.WorkspaceDirectoriesEqual(@"C:\GitLab\Project", @"c:\gitlab\project\"));
            Assert.IsTrue(ClaudeCodeControl.WorkspaceDirectoriesEqual(@"C:\GitLab\Project\", @"C:\GitLab\Project"));
        }

        [TestMethod]
        public void WorkspaceDirectoriesEqual_SeparatesDifferentDirectories()
        {
            Assert.IsFalse(ClaudeCodeControl.WorkspaceDirectoriesEqual(@"C:\GitLab\ProjectA", @"C:\GitLab\ProjectB"));
        }
    }
}
