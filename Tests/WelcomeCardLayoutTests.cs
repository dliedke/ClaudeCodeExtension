/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the welcome card's narrow-tab layout rules (stacking rule, account email line break)
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class WelcomeCardLayoutTests
    {
        [TestMethod]
        public void TipsGetWhatTheLeftColumnAndDividerLeave()
        {
            Assert.AreEqual(600 - 220 - WelcomeCardLayout.DividerWidth, WelcomeCardLayout.TipsWidth(600, 220), 0.001);
        }

        [TestMethod]
        public void StaysSideBySideUpTo120PercentOfTheLeftColumnWidth()
        {
            Assert.IsFalse(WelcomeCardLayout.ShouldStack(600, 200, 180));
            Assert.IsFalse(WelcomeCardLayout.ShouldStack(600, 200, 230), "Taller than wide, but under the 120 % allowance.");
            Assert.IsFalse(WelcomeCardLayout.ShouldStack(600, 200, 240), "Exactly 120 % is still side by side.");
        }

        [TestMethod]
        public void StacksOnceTheTipsOutgrow120PercentOfTheLeftColumnWidth()
        {
            Assert.IsTrue(WelcomeCardLayout.ShouldStack(300, 200, 241));
        }

        [TestMethod]
        public void StacksWhenThereIsNoRoomBesideTheLeftColumn()
        {
            Assert.IsTrue(WelcomeCardLayout.ShouldStack(230, 220, 100));
        }

        [TestMethod]
        public void RecentConversationsStayBesideTheFactsWhateverTheirHeight()
        {
            Assert.IsFalse(WelcomeCardLayout.ShouldStack(600, 200, 400, recentListShown: true));
            Assert.IsTrue(WelcomeCardLayout.ShouldStack(230, 220, 100, recentListShown: true), "No room beside still stacks.");
        }

        [TestMethod]
        public void UnmeasuredCardKeepsTheDefaultLayout()
        {
            Assert.IsFalse(WelcomeCardLayout.ShouldStack(0, 220, 500));
            Assert.IsFalse(WelcomeCardLayout.ShouldStack(600, 0, 500));
        }

        [TestMethod]
        public void AccountEmailMovesToItsOwnLine()
        {
            Assert.AreEqual("Signed in as Jane Doe\n(jane@example.com)",
                WelcomeCardLayout.BreakBeforeEmail("Signed in as Jane Doe (jane@example.com)"));
        }

        [TestMethod]
        public void FactsWithoutATrailingEmailAreLeftAlone()
        {
            Assert.AreEqual("Signed in as jane@example.com",
                WelcomeCardLayout.BreakBeforeEmail("Signed in as jane@example.com"));
            Assert.AreEqual("Opus 4.8 (1M context)",
                WelcomeCardLayout.BreakBeforeEmail("Opus 4.8 (1M context)"));
            Assert.IsNull(WelcomeCardLayout.BreakBeforeEmail(null));
        }
    }
}
