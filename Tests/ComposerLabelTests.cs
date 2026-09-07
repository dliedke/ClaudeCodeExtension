/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the composer selector captions used when the chat tab is too narrow for the full ones
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ComposerLabelTests
    {
        [TestMethod]
        public void KnownNamesAreReplacedEvenWhenTheyWouldFit()
        {
            // "Claude Code" is 11 characters and the compact tier allows 14 — the table still applies,
            // because the point of the tier is to buy room for the selectors that follow it.
            Assert.AreEqual("Claude", ComposerLabels.Shorten("Claude Code", ComposerLabels.CompactMaxChars));
            Assert.AreEqual("Skip", ComposerLabels.Shorten("Skip permissions", ComposerLabels.CompactMaxChars));
            Assert.AreEqual("Ask", ComposerLabels.Shorten("Ask permission", ComposerLabels.CompactMaxChars));
            Assert.AreEqual("Plan", ComposerLabels.Shorten("Plan mode", ComposerLabels.CompactMaxChars));
            Assert.AreEqual("XHigh", ComposerLabels.Shorten("Extra High", ComposerLabels.CompactMaxChars));
        }

        [TestMethod]
        public void WslSuffixSurvivesTheTightestTier()
        {
            // Losing "WSL" would leave the WSL agent and the Windows one with the same caption, which
            // is the one distinction this suffix exists to make.
            Assert.AreEqual("Claude WSL", ComposerLabels.Shorten("Claude Code (WSL)", ComposerLabels.TightMaxChars));
            Assert.AreEqual("Cursor WSL", ComposerLabels.Shorten("Cursor Agent (WSL)", ComposerLabels.TightMaxChars));
        }

        [TestMethod]
        public void NativeSuffixIsDropped()
        {
            // "(native)" only reads as a contrast with the WSL twin, so it carries nothing on its own.
            Assert.AreEqual("Codex", ComposerLabels.Shorten("Codex (native)", ComposerLabels.CompactMaxChars));
            Assert.AreEqual("Devin", ComposerLabels.Shorten("Devin (native)", ComposerLabels.TightMaxChars));
        }

        [TestMethod]
        public void UnknownLabelsFallBackToTruncation()
        {
            Assert.AreEqual("gpt-5-cod…", ComposerLabels.Shorten("gpt-5-codex-high", ComposerLabels.TightMaxChars));
            Assert.AreEqual("claude-opus-3…", ComposerLabels.Shorten("claude-opus-3-20260514", ComposerLabels.CompactMaxChars));
        }

        [TestMethod]
        public void ShortLabelsAreLeftAlone()
        {
            Assert.AreEqual("Opus", ComposerLabels.Shorten("Opus", ComposerLabels.TightMaxChars));
            Assert.AreEqual("High", ComposerLabels.Shorten("High", ComposerLabels.TightMaxChars));
            Assert.AreEqual("Sonnet", ComposerLabels.Shorten("Sonnet", ComposerLabels.TightMaxChars));
        }

        [TestMethod]
        public void EmptyInputIsReturnedUnchanged()
        {
            Assert.IsNull(ComposerLabels.Shorten(null, ComposerLabels.TightMaxChars));
            Assert.AreEqual("", ComposerLabels.Shorten("", ComposerLabels.TightMaxChars));
        }

        [TestMethod]
        public void EveryTierProducesACaptionThatFits()
        {
            string[] labels =
            {
                "Claude Code", "Claude Code (WSL)", "Codex (native)", "Cursor Agent (WSL)",
                "Open Code", "Antigravity", "Reasonix", "Skip permissions", "Ask permission",
                "Plan mode", "Extra High", "Ultracode", "Medium", "Opus Plan",
                "claude-sonnet-4-5-20260101"
            };

            foreach (string label in labels)
            {
                Assert.IsTrue(
                    ComposerLabels.Shorten(label, ComposerLabels.CompactMaxChars).Length <= ComposerLabels.CompactMaxChars,
                    "Compact caption too long for " + label);

                Assert.IsTrue(
                    ComposerLabels.Shorten(label, ComposerLabels.TightMaxChars).Length <= ComposerLabels.TightMaxChars,
                    "Tight caption too long for " + label);
            }
        }
    }
}
