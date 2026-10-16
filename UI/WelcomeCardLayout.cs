/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Layout rule for the native-mode welcome card in a narrow chat tab (side by side vs. stacked)
 *
 * *******************************************************************************************************************/

using System;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Decides whether the welcome card keeps the tips beside the mascot/facts column or moves them
    /// underneath. Pure arithmetic, kept out of the code-behind so the rule can be unit-tested
    /// (<c>Tests/WelcomeCardLayoutTests.cs</c>).
    /// </summary>
    public static class WelcomeCardLayout
    {
        /// <summary>
        /// Width the vertical divider takes between the two columns: the 1 px rule plus its 14 px
        /// right margin (<c>WelcomeDivider</c> in <c>ChatTranscriptView.xaml</c>).
        /// </summary>
        public const double DividerWidth = 15;

        /// <summary>Width left for the tips beside a left column of <paramref name="leftWidth"/>.</summary>
        public static double TipsWidth(double cardWidth, double leftWidth)
        {
            return cardWidth - leftWidth - DividerWidth;
        }

        /// <summary>
        /// How much taller than the left column is wide the tips may grow beside it before they move
        /// underneath. A little over 1, so a tip or two wrapping does not already flip the card.
        /// </summary>
        public const double StackHeightRatio = 1.2;

        /// <summary>
        /// True when the tips should go under the facts: beside the left column they would be more
        /// than <see cref="StackHeightRatio"/> times as tall as that column is wide, or there is no room
        /// beside it at all. Judged on the side-by-side height rather than the current one — stacked,
        /// the tips are shorter, and testing that would flip the card back and forth.
        /// </summary>
        public static bool ShouldStack(double cardWidth, double leftWidth, double sideBySideTipsHeight)
        {
            if (cardWidth <= 0 || leftWidth <= 0)
            {
                return false;
            }

            return TipsWidth(cardWidth, leftWidth) <= 0 ||
                sideBySideTipsHeight > leftWidth * StackHeightRatio;
        }

        /// <summary>
        /// Puts a trailing parenthesised email on its own line — <c>Signed in as Jane (jane@x.com)</c>
        /// is the card's longest fact and, unbroken, decides how wide the left column wants to be.
        /// Text without a trailing <c>(…@…)</c> (a bare email, a plan name) is returned unchanged.
        /// </summary>
        public static string BreakBeforeEmail(string fact)
        {
            if (string.IsNullOrEmpty(fact) || !fact.EndsWith(")", StringComparison.Ordinal))
            {
                return fact;
            }

            int open = fact.LastIndexOf(" (", StringComparison.Ordinal);
            if (open <= 0 || fact.IndexOf('@', open) < 0)
            {
                return fact;
            }

            return fact.Substring(0, open) + "\n" + fact.Substring(open + 1);
        }
    }
}
