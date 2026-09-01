/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the native-mode "CLI updated since last time" changelog notice
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class CliUpdateNoticeTests
    {
        [TestMethod]
        public void FirstRun_WithNoRecordedVersion_IsSilent()
        {
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", null, "2.1.252"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "", "2.1.252"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "   ", "2.1.252"));
        }

        [TestMethod]
        public void SameVersion_IsSilent()
        {
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.252", "2.1.252"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", " 2.1.252 ", "2.1.252"));
        }

        [TestMethod]
        public void Downgrade_IsSilent()
        {
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.252", "2.1.100"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.2.0", "2.1.999"));
        }

        [TestMethod]
        public void Upgrade_ProducesANoticeNamingBothVersions()
        {
            string notice = ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.100", "2.1.252");

            Assert.IsNotNull(notice);
            StringAssert.Contains(notice, "Claude Code");
            StringAssert.Contains(notice, "v2.1.252");
            StringAssert.Contains(notice, "v2.1.100");
            StringAssert.Contains(notice, "https://code.claude.com/docs/en/changelog");
        }

        [TestMethod]
        public void Upgrade_ComparesComponentsNumerically_NotLexically()
        {
            // "2.1.9" -> "2.1.10": lexical string compare would call this a downgrade.
            Assert.IsNotNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.9", "2.1.10"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.10", "2.1.9"));
        }

        [TestMethod]
        public void Upgrade_IgnoresPreReleaseAndBuildSuffixes()
        {
            // Numeric parts are equal once the suffix is stripped -> not "newer".
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.252", "2.1.252-beta.1"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.252+build7", "2.1.252"));

            // Suffix present but the dotted numbers still moved forward.
            Assert.IsNotNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.252", "2.1.253-rc.1"));
        }

        [TestMethod]
        public void DifferentComponentCount_ComparesWithMissingPartsAsZero()
        {
            Assert.IsNotNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1", "2.1.1"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.0", "2.1"));
        }

        [TestMethod]
        public void Unparseable_Version_IsSilent()
        {
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "not-a-version", "2.1.252"));
            Assert.IsNull(ClaudeCodeControl.BuildCliUpdateNotice("Claude Code", "2.1.252", "latest"));
        }

        [TestMethod]
        public void BlankProviderName_FallsBackToAGenericLabel()
        {
            string notice = ClaudeCodeControl.BuildCliUpdateNotice(null, "2.1.100", "2.1.252");

            Assert.IsNotNull(notice);
            StringAssert.Contains(notice, "The agent CLI");
        }
    }
}
