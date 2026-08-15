/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the effort level the embedded terminal launches Claude Code with — the "/effort"
 *          slash command only applies to the running session, so the level has to ride along as a
 *          launch flag or every restart silently drops back while the slider still shows it.
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ClaudeLaunchArgumentTests
    {
        /// <summary>
        /// "Auto" is the extension's own "leave it to the CLI" choice and the one value --effort
        /// rejects, so a user who never touched the slider must see an unchanged command line.
        /// </summary>
        [TestMethod]
        public void AppendClaudeEffortArgument_AddsNothingForAuto()
        {
            Assert.AreEqual(
                "claude",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.Auto));
        }

        [TestMethod]
        public void AppendClaudeEffortArgument_AppendsTheSelectedLevel()
        {
            Assert.AreEqual(
                "claude --effort low",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.Low));

            Assert.AreEqual(
                "claude --effort medium",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.Medium));

            Assert.AreEqual(
                "claude --effort high",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.High));
        }

        /// <summary>
        /// The enum spells these "XHigh" and "Ultracode"; the CLI only accepts them lowercased.
        /// </summary>
        [TestMethod]
        public void AppendClaudeEffortArgument_LowercasesCompoundLevelNames()
        {
            Assert.AreEqual(
                "claude --effort xhigh",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.XHigh));

            Assert.AreEqual(
                "claude --effort ultracode",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.Ultracode));

            Assert.AreEqual(
                "claude --effort max",
                ClaudeCodeControl.AppendClaudeEffortArgument("claude", EffortLevel.Max));
        }

        /// <summary>
        /// Every level the slider offers has to produce a flag — a level added to the enum without a
        /// mapping would otherwise launch at the CLI default while the slider claims otherwise.
        /// </summary>
        [TestMethod]
        public void AppendClaudeEffortArgument_CoversEveryLevelButAuto()
        {
            foreach (EffortLevel level in Enum.GetValues(typeof(EffortLevel)))
            {
                if (level == EffortLevel.Auto) continue;

                string command = ClaudeCodeControl.AppendClaudeEffortArgument("claude", level);

                StringAssert.StartsWith(command, "claude --effort ");
                Assert.AreEqual(command, command.ToLowerInvariant(), $"{level} was not lowercased");
            }
        }

        /// <summary>
        /// The flag is appended to whatever the caller built so far, so it must not disturb the
        /// arguments already on the command line.
        /// </summary>
        [TestMethod]
        public void AppendClaudeEffortArgument_PreservesEarlierArguments()
        {
            Assert.AreEqual(
                "claude --dangerously-skip-permissions --effort xhigh",
                ClaudeCodeControl.AppendClaudeEffortArgument(
                    "claude --dangerously-skip-permissions", EffortLevel.XHigh));
        }
    }
}
