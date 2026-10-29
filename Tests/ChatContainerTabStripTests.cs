/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the single chat window's tab strip scrolling (UI/ChatContainerView.xaml.cs): the ◂ ▸
 *          arrows and the wheel step one whole tab, and bringing a tab into view stops on a tab edge.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatContainerTabStripTests
    {
        // Five tabs of 100, 150, 80, 120 and 100 px: left edges below, strip 550 wide.
        private static readonly double[] Edges = { 0, 100, 250, 330, 450 };
        private const double Viewport = 300;
        private const double Scrollable = 550 - Viewport;

        [TestMethod]
        public void ForwardStepLandsOnTheNextTabEdge()
        {
            Assert.AreEqual(100, ChatContainerView.GetOffsetOneTabFurther(Edges, 0, Scrollable, forward: true), 0.001);
            Assert.AreEqual(250, ChatContainerView.GetOffsetOneTabFurther(Edges, 100, Scrollable, forward: true), 0.001);
        }

        [TestMethod]
        public void ForwardStepFromBetweenEdgesSnapsToTheNextOne()
        {
            Assert.AreEqual(100, ChatContainerView.GetOffsetOneTabFurther(Edges, 40, Scrollable, forward: true), 0.001);
        }

        [TestMethod]
        public void ForwardStepIsClampedToTheScrollableWidth()
        {
            // The next edge (330) is past the end: the strip stops at its end instead.
            Assert.AreEqual(Scrollable, ChatContainerView.GetOffsetOneTabFurther(Edges, 250, Scrollable, forward: true), 0.001);
            Assert.AreEqual(Scrollable, ChatContainerView.GetOffsetOneTabFurther(Edges, Scrollable, Scrollable, forward: true), 0.001);
        }

        [TestMethod]
        public void BackwardStepLandsOnThePreviousTabEdge()
        {
            Assert.AreEqual(100, ChatContainerView.GetOffsetOneTabFurther(Edges, 250, Scrollable, forward: false), 0.001);
            // From the clamped end (between two edges) back to the edge before it, not a partial step.
            Assert.AreEqual(100, ChatContainerView.GetOffsetOneTabFurther(Edges, Scrollable, Scrollable, forward: false), 0.001);
            Assert.AreEqual(0, ChatContainerView.GetOffsetOneTabFurther(Edges, 100, Scrollable, forward: false), 0.001);
            Assert.AreEqual(0, ChatContainerView.GetOffsetOneTabFurther(Edges, 0, Scrollable, forward: false), 0.001);
        }

        [TestMethod]
        public void TabAlreadyInViewKeepsTheOffset()
        {
            Assert.AreEqual(100, ChatContainerView.GetOffsetToShowTab(Edges, 250, 330, 100, Viewport), 0.001);
        }

        [TestMethod]
        public void TabCutOnTheLeftIsShownFromItsLeftEdge()
        {
            Assert.AreEqual(100, ChatContainerView.GetOffsetToShowTab(Edges, 100, 250, 180, Viewport), 0.001);
        }

        [TestMethod]
        public void TabCutOnTheRightScrollsToAWholeTabEdge()
        {
            // Showing the tab at 330..450 needs an offset of at least 150; the minimal scroll would cut
            // the 100..250 tab, so the strip stops on the next edge (250) instead.
            Assert.AreEqual(250, ChatContainerView.GetOffsetToShowTab(Edges, 330, 450, 0, Viewport), 0.001);
        }

        [TestMethod]
        public void TabWiderThanTheRoomIsShownFromItsLeftEdge()
        {
            double[] edges = { 0, 100, 500 };
            Assert.AreEqual(100, ChatContainerView.GetOffsetToShowTab(edges, 100, 500, 0, Viewport), 0.001);
        }
    }
}
