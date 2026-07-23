/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers Windows-to-WSL path conversion and WSL launch command building — the logic behind
 *          the workspace-path launch failures of issue #106.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class WslCommandTests
    {
        [TestMethod]
        public void ConvertToWslPath_MapsDriveLetterToMntAndLowercasesIt()
        {
            Assert.AreEqual("/mnt/c/GitLab/Project", ClaudeCodeControl.ConvertToWslPath(@"C:\GitLab\Project"));
            Assert.AreEqual("/mnt/d/work", ClaudeCodeControl.ConvertToWslPath(@"D:\work"));
        }

        [TestMethod]
        public void ConvertToWslPath_UnwrapsWslUncPaths()
        {
            Assert.AreEqual(
                "/home/user/Project",
                ClaudeCodeControl.ConvertToWslPath(@"\\wsl.localhost\Ubuntu\home\user\Project"));

            Assert.AreEqual(
                "/home/user/Project",
                ClaudeCodeControl.ConvertToWslPath(@"\\wsl$\Ubuntu\home\user\Project"));
        }

        [TestMethod]
        public void ConvertToWslPath_ReturnsEmptyForNullOrEmpty()
        {
            Assert.AreEqual(string.Empty, ClaudeCodeControl.ConvertToWslPath(null));
            Assert.AreEqual(string.Empty, ClaudeCodeControl.ConvertToWslPath(string.Empty));
        }

        /// <summary>
        /// Issue #106: a workspace directory containing a dash broke the launch. The path must ride
        /// inside single quotes so nothing in it can be read as an option or operator by bash.
        /// </summary>
        [TestMethod]
        public void BuildWslLaunchCommand_QuotesPathsThatCouldBeReadAsOptions()
        {
            string command = ClaudeCodeControl.BuildWslLaunchCommand("/mnt/c/my-dash-project", "claude");

            Assert.AreEqual("wsl bash -lic \"cd '/mnt/c/my-dash-project' && claude\"", command);
            StringAssert.Contains(command, "'/mnt/c/my-dash-project'");
        }

        [TestMethod]
        public void BuildWslLaunchCommand_QuotesPathsContainingSpaces()
        {
            string command = ClaudeCodeControl.BuildWslLaunchCommand("/mnt/c/My Projects/app", "codex");

            Assert.AreEqual("wsl bash -lic \"cd '/mnt/c/My Projects/app' && codex\"", command);
        }

        [TestMethod]
        public void QuoteForBash_EscapesEmbeddedSingleQuotes()
        {
            // The POSIX idiom: close the quote, emit an escaped quote, reopen. Anything less lets a
            // path with an apostrophe terminate the string and inject the rest as shell syntax.
            Assert.AreEqual(@"'it'\''s'", ClaudeCodeControl.QuoteForBash("it's"));
        }

        [TestMethod]
        public void QuoteForBash_ReturnsEmptyQuotedStringForNull()
        {
            Assert.AreEqual("''", ClaudeCodeControl.QuoteForBash(null));
        }

        [TestMethod]
        public void BuildWslLaunchCommand_PassesProviderArgumentsThrough()
        {
            string command = ClaudeCodeControl.BuildWslLaunchCommand(
                "/mnt/c/proj", "claude --dangerously-skip-permissions");

            StringAssert.EndsWith(command, "&& claude --dangerously-skip-permissions\"");
        }
    }
}
