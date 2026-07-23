/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the console font size conversion used to persist a Ctrl+Scroll zoom (Command Prompt),
 *          where the zoom works in cell-height pixels but the setting is stored in points.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ConsoleFontSizeTests
    {
        /// <summary>
        /// A size saved from a Ctrl+Scroll zoom is re-applied on the next launch through the pt→px
        /// conversion. If the two were not exact inverses, every session would shift the size by a
        /// notch, drifting away from what the user picked.
        /// </summary>
        [TestMethod]
        public void ConsoleFontSizeConversion_RoundTripsAcrossTheSettingsRange()
        {
            for (int pt = 6; pt <= 36; pt++)
            {
                int px = ClaudeCodeControl.ConsoleFontPtToCellHeightPx(pt);
                Assert.AreEqual(pt, ClaudeCodeControl.ConsoleCellHeightPxToFontPt(px),
                    $"round trip failed for {pt} pt (via {px} px)");
            }
        }

        [TestMethod]
        public void ConsoleFontPtToCellHeightPx_ConvertsAt96Dpi()
        {
            Assert.AreEqual(16, ClaudeCodeControl.ConsoleFontPtToCellHeightPx(12));
            Assert.AreEqual(8, ClaudeCodeControl.ConsoleFontPtToCellHeightPx(6));
        }

        /// <summary>
        /// The zoom's own pixel bounds are wider than the range the Settings drop-down offers, so a
        /// size captured at either extreme must be clamped — otherwise the drop-down would show no
        /// selection at all for the persisted value.
        /// </summary>
        [TestMethod]
        public void ConsoleCellHeightPxToFontPt_ClampsToTheSettingsDropDownRange()
        {
            Assert.AreEqual(6, ClaudeCodeControl.ConsoleCellHeightPxToFontPt(6));
            Assert.AreEqual(36, ClaudeCodeControl.ConsoleCellHeightPxToFontPt(60));
        }
    }
}
