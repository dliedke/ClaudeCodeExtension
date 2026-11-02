/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the theme the embedded terminal launches Claude Code with. Claude Code colors its text from its
 *          own theme setting and cannot see the terminal's background, so a light theme in the dark terminal drew
 *          a multiple-choice question black on black.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ClaudeThemeArgumentTests
    {
        /// <summary>
        /// A theme that already suits the background is the user's choice and is left alone; no
        /// configured theme is the CLI's default, dark.
        /// </summary>
        [DataTestMethod]
        [DataRow("dark", false)]
        [DataRow("light", true)]
        [DataRow("dark-daltonized", false)]
        [DataRow("light-ansi", true)]
        [DataRow(null, false)]
        [DataRow("", false)]
        public void ResolveClaudeThemeForTerminal_LeavesASuitingThemeAlone(string configured, bool lightBackground)
        {
            Assert.IsNull(ClaudeCodeControl.ResolveClaudeThemeForTerminal(configured, lightBackground));
        }

        [DataTestMethod]
        [DataRow("light", false, "dark")]
        [DataRow("dark", true, "light")]
        [DataRow(null, true, "light")]
        [DataRow(" Light ", false, "dark")]
        public void ResolveClaudeThemeForTerminal_SwitchesToTheThemeThatSuitsTheBackground(
            string configured, bool lightBackground, string expected)
        {
            Assert.AreEqual(expected, ClaudeCodeControl.ResolveClaudeThemeForTerminal(configured, lightBackground));
        }

        /// <summary>
        /// The colorblind-friendly and ANSI themes are a deliberate choice; only their dark/light half
        /// follows the background.
        /// </summary>
        [DataTestMethod]
        [DataRow("light-daltonized", false, "dark-daltonized")]
        [DataRow("dark-daltonized", true, "light-daltonized")]
        [DataRow("light-ansi", false, "dark-ansi")]
        [DataRow("dark-ansi", true, "light-ansi")]
        public void ResolveClaudeThemeForTerminal_KeepsTheVariant(string configured, bool lightBackground, string expected)
        {
            Assert.AreEqual(expected, ClaudeCodeControl.ResolveClaudeThemeForTerminal(configured, lightBackground));
        }

        /// <summary>A theme this does not know ("auto", one added later) is left to the CLI.</summary>
        [DataTestMethod]
        [DataRow("auto")]
        [DataRow("dark-highcontrast")]
        [DataRow("solarized")]
        public void ResolveClaudeThemeForTerminal_LeavesUnknownThemesToTheCli(string configured)
        {
            Assert.IsNull(ClaudeCodeControl.ResolveClaudeThemeForTerminal(configured, false));
            Assert.IsNull(ClaudeCodeControl.ResolveClaudeThemeForTerminal(configured, true));
        }

        /// <summary>
        /// The JSON goes through the .cmd launch script on Windows and through bash under WSL; both
        /// must hand the CLI exactly <c>{"theme":"dark"}</c>.
        /// </summary>
        [TestMethod]
        public void BuildClaudeThemeArgument_QuotesTheJsonForTheOwningShell()
        {
            Assert.AreEqual(
                "claude --settings \"{\\\"theme\\\":\\\"dark\\\"}\"",
                ClaudeCodeControl.BuildClaudeThemeArgument("claude", "dark", isWsl: false));

            Assert.AreEqual(
                "claude --settings '{\"theme\":\"light-daltonized\"}'",
                ClaudeCodeControl.BuildClaudeThemeArgument("claude", "light-daltonized", isWsl: true));
        }

        [TestMethod]
        public void BuildClaudeThemeArgument_AddsNothingWithoutATheme()
        {
            Assert.AreEqual("claude", ClaudeCodeControl.BuildClaudeThemeArgument("claude", null, isWsl: false));
            Assert.AreEqual("claude", ClaudeCodeControl.BuildClaudeThemeArgument("claude", "", isWsl: true));
        }

        [TestMethod]
        public void ReadClaudeThemeSetting_ReadsTheTopLevelTheme()
        {
            Assert.AreEqual("light", ClaudeCodeControl.ReadClaudeThemeSetting("{ \"model\": \"opus\", \"theme\": \"light\" }"));
            Assert.IsNull(ClaudeCodeControl.ReadClaudeThemeSetting("{ \"model\": \"opus\" }"));
            Assert.IsNull(ClaudeCodeControl.ReadClaudeThemeSetting("{ \"theme\": \"\" }"));
            Assert.IsNull(ClaudeCodeControl.ReadClaudeThemeSetting("{ not json"));
            Assert.IsNull(ClaudeCodeControl.ReadClaudeThemeSetting(null));
        }
    }
}
