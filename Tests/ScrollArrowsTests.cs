/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the ◀/▶ scroll arrow overflow rule (panel toolbar, composer action row)
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ScrollArrowsTests
    {
        [TestMethod]
        public void ContentWiderThanTheRowNeedsArrows()
        {
            Assert.IsTrue(ScrollArrows.ContentOverflows(500, 400, 0));
        }

        [TestMethod]
        public void ContentThatFitsNeedsNoArrows()
        {
            Assert.IsFalse(ScrollArrows.ContentOverflows(400, 400, 0));
            Assert.IsFalse(ScrollArrows.ContentOverflows(380, 400, 0));
        }

        [TestMethod]
        public void ShownArrowsDoNotKeepThemselvesShown()
        {
            // Row of 446 px with both arrows shown (46 px): the viewport is 400, the content 430. The
            // content fits the 446 px the row has without the arrows, so they must go away.
            Assert.IsFalse(ScrollArrows.ContentOverflows(430, 400, 46));
        }

        [TestMethod]
        public void ShownArrowsStayWhileTheContentStillOverflowsWithoutThem()
        {
            Assert.IsTrue(ScrollArrows.ContentOverflows(450, 400, 46));
        }

        [TestMethod]
        public void SubPixelRoundingIsNotAnOverflow()
        {
            Assert.IsFalse(ScrollArrows.ContentOverflows(400.4, 400, 0));
        }
    }
}
