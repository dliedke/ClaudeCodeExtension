/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the native-mode usage button's formatting and ring geometry (UI/ChatUsageIndicator.cs)
 *
 * *******************************************************************************************************************/

using System;
using System.Windows;
using System.Windows.Media;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatUsageIndicatorTests
    {
        [TestMethod]
        public void FormatTokensShortensTheWayTheCliDoes()
        {
            Assert.AreEqual("950", ChatUsageIndicator.FormatTokens(950));
            Assert.AreEqual("0", ChatUsageIndicator.FormatTokens(0));
            Assert.AreEqual("0", ChatUsageIndicator.FormatTokens(-5));
            Assert.AreEqual("413.9k", ChatUsageIndicator.FormatTokens(413900));
            Assert.AreEqual("1.2M", ChatUsageIndicator.FormatTokens(1200000));
            Assert.AreEqual("1M", ChatUsageIndicator.FormatTokens(1000000));
        }

        [TestMethod]
        public void FormatTokensNeverShowsAThousandThousand()
        {
            // 999,999 rounds to 1000.0k; it must read as the million unit, not "1000k".
            Assert.AreEqual("1M", ChatUsageIndicator.FormatTokens(999999));
        }

        [TestMethod]
        public void ContextPercentIsTheShareOfTheWindowRoundedAndCapped()
        {
            Assert.AreEqual(41, ChatUsageIndicator.ContextPercent(413900, 1000000));
            Assert.AreEqual(0, ChatUsageIndicator.ContextPercent(0, 200000));
            Assert.AreEqual(0, ChatUsageIndicator.ContextPercent(5000, 0), "unknown window");
            Assert.AreEqual(100, ChatUsageIndicator.ContextPercent(1500000, 1000000), "over the window is capped");
        }

        [TestMethod]
        public void ContextLineNamesTheWindowWhenKnown()
        {
            Assert.AreEqual("413.9k / 1M (41%)", ChatUsageIndicator.ContextLine(413900, 1000000));
            Assert.AreEqual("413.9k", ChatUsageIndicator.ContextLine(413900, 0));
        }

        [TestMethod]
        public void ClampPercentKeepsTheBarInRange()
        {
            Assert.AreEqual(0, ChatUsageIndicator.ClampPercent(-3));
            Assert.AreEqual(57, ChatUsageIndicator.ClampPercent(57));
            Assert.AreEqual(100, ChatUsageIndicator.ClampPercent(180));
        }

        [TestMethod]
        public void PlanRowTextSkipsBlankDetails()
        {
            Assert.AreEqual("41% used · Resets in 2 h", ChatUsageIndicator.PlanRowText(41, "Resets in 2 h"));
            Assert.AreEqual("41% used", ChatUsageIndicator.PlanRowText(41, "", " "));
            Assert.AreEqual("41% used", ChatUsageIndicator.PlanRowText(41, null));
            Assert.AreEqual("100% used · R$110.71 spent · Resets May 1",
                ChatUsageIndicator.PlanRowText(120, "R$110.71 spent", "Resets May 1"));
        }

        [TestMethod]
        public void ArcPointStartsAtTwelveAndRunsClockwise()
        {
            Point quarter = ChatUsageIndicator.ArcPoint(0.25, 7, 6);
            Assert.AreEqual(13.0, quarter.X, 1e-9, "3 o'clock");
            Assert.AreEqual(7.0, quarter.Y, 1e-9);

            Point half = ChatUsageIndicator.ArcPoint(0.5, 7, 6);
            Assert.AreEqual(7.0, half.X, 1e-9, "6 o'clock");
            Assert.AreEqual(13.0, half.Y, 1e-9);
        }

        [TestMethod]
        public void RingArcIsNothingForEmptyOrInvalidFractions()
        {
            Assert.IsNull(ChatUsageIndicator.RingArc(0, 7, 6));
            Assert.IsNull(ChatUsageIndicator.RingArc(-0.2, 7, 6));
            Assert.IsNull(ChatUsageIndicator.RingArc(double.NaN, 7, 6));
        }

        [TestMethod]
        public void RingArcStartsAtTwelveAndIsFrozen()
        {
            PathGeometry geometry = ChatUsageIndicator.RingArc(0.5, 7, 6);

            Assert.IsNotNull(geometry);
            Assert.IsTrue(geometry.IsFrozen);
            Assert.AreEqual(1, geometry.Figures.Count);
            Assert.AreEqual(1.0, geometry.Figures[0].StartPoint.Y, 1e-9, "top of the ring");
            Assert.IsInstanceOfType(geometry.Figures[0].Segments[0], typeof(ArcSegment));
        }

        [TestMethod]
        public void AFullRingStopsShortOfClosing()
        {
            // An arc whose start and end coincide draws nothing, so a full ring ends just before 12 o'clock.
            PathGeometry geometry = ChatUsageIndicator.RingArc(1.0, 7, 6);
            var arc = (ArcSegment)geometry.Figures[0].Segments[0];

            Assert.AreNotEqual(geometry.Figures[0].StartPoint.X, arc.Point.X, 1e-3);
        }
    }
}
