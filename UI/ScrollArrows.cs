/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Overflow rule for the ◀/▶ scroll arrows of the panel toolbar and the composer action row
 *
 * *******************************************************************************************************************/

using System.Windows;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Decides whether a scrolled toolbar row needs its ◀/▶ arrows. The arrows take their room from
    /// the scroller itself, so reading the scroller's own viewport would count them against the
    /// content: once shown they kept themselves shown until the row was widened by their width, and a
    /// row first laid out narrow (VS start, a new chat tab) kept them for good. The rule is unit-tested
    /// in <c>Tests/ScrollArrowsTests.cs</c>.
    /// </summary>
    public static class ScrollArrows
    {
        /// <summary>
        /// True when the content overflows the room the row would have with no arrows at all:
        /// <paramref name="viewport"/> plus the width the currently shown arrows take
        /// (<paramref name="shownArrowsWidth"/>, margins included).
        /// </summary>
        public static bool ContentOverflows(double extent, double viewport, double shownArrowsWidth)
        {
            return extent > viewport + shownArrowsWidth + 0.5;
        }

        /// <summary>Width an arrow currently takes from its row, margins included; 0 while collapsed.</summary>
        public static double ShownWidth(FrameworkElement arrow)
        {
            return arrow.Visibility == Visibility.Visible
                ? arrow.ActualWidth + arrow.Margin.Left + arrow.Margin.Right
                : 0;
        }
    }
}
