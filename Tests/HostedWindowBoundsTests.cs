/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the stale-window check behind the Claude Usage WebView2 (UI/HostedWindowBounds.cs)
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class HostedWindowBoundsTests
    {
        [TestMethod]
        public void WindowWhereWpfPlacedItIsAligned()
        {
            var expected = new PixelRect(20, 190, 1830, 880);

            Assert.IsFalse(HostedWindowBounds.IsMisaligned(expected, expected));
        }

        [TestMethod]
        public void RoundingDifferenceWithinToleranceIsIgnored()
        {
            var expected = new PixelRect(20, 190, 1830, 880);
            var actual = new PixelRect(21, 190, 1830, 882);

            Assert.IsFalse(HostedWindowBounds.IsMisaligned(actual, expected));
        }

        [TestMethod]
        public void WindowLeftTallerThanItsPaneIsMisaligned()
        {
            // The docked pane shrank to 880 when the Terminal docked below it; the window kept 970.
            var expected = new PixelRect(20, 190, 1830, 880);
            var stale = new PixelRect(20, 190, 1830, 970);

            Assert.IsTrue(HostedWindowBounds.IsMisaligned(stale, expected));
        }

        [TestMethod]
        public void WindowLeftNarrowerThanItsPaneIsMisaligned()
        {
            var expected = new PixelRect(20, 190, 1830, 880);
            var stale = new PixelRect(20, 190, 1400, 880);

            Assert.IsTrue(HostedWindowBounds.IsMisaligned(stale, expected));
        }

        [TestMethod]
        public void MovedWindowIsMisaligned()
        {
            var expected = new PixelRect(20, 190, 1830, 880);
            var moved = new PixelRect(20, 260, 1830, 950);

            Assert.IsTrue(HostedWindowBounds.IsMisaligned(moved, expected));
        }

        [TestMethod]
        public void ElementWithNoAreaIsNeverRepaired()
        {
            // Not laid out yet (or collapsed): there is nowhere to put the window, so leave it alone.
            var collapsed = new PixelRect(20, 190, 20, 190);
            var anywhere = new PixelRect(0, 0, 1000, 1000);

            Assert.IsFalse(HostedWindowBounds.IsMisaligned(anywhere, collapsed));
        }

        [TestMethod]
        public void PixelRectReportsItsSize()
        {
            var rect = new PixelRect(20, 190, 1830, 880);

            Assert.AreEqual(1810, rect.Width);
            Assert.AreEqual(690, rect.Height);
        }
    }
}
