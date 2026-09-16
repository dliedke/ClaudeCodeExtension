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

        /// <summary>
        /// Issue #115: the retired TerminalZoomDelta (a Ctrl+Scroll notch count, 2px per notch from the
        /// 16px/12pt default) migrates to the equivalent Console font size. A zero delta must map to the
        /// default 12pt, a zoom-out to a smaller size, and a zoom-in to a larger one.
        /// </summary>
        [TestMethod]
        public void LegacyZoomDeltaToConsoleFontPt_MapsNotchesToPoints()
        {
            Assert.AreEqual(12, ClaudeCodeControl.LegacyZoomDeltaToConsoleFontPt(0), "default (no zoom) should be 12pt");
            Assert.AreEqual(8, ClaudeCodeControl.LegacyZoomDeltaToConsoleFontPt(-3), "zoom-out 3 notches → 10px → 8pt");
            Assert.AreEqual(15, ClaudeCodeControl.LegacyZoomDeltaToConsoleFontPt(2), "zoom-in 2 notches → 20px → 15pt");
        }

        /// <summary>
        /// An extreme saved delta must still land inside the [6..36]pt range the Settings drop-down offers,
        /// so the migrated value is always displayable and usable.
        /// </summary>
        [TestMethod]
        public void LegacyZoomDeltaToConsoleFontPt_ClampsExtremes()
        {
            Assert.AreEqual(6, ClaudeCodeControl.LegacyZoomDeltaToConsoleFontPt(-100));
            Assert.AreEqual(36, ClaudeCodeControl.LegacyZoomDeltaToConsoleFontPt(100));
        }

        /// <summary>
        /// A console default whose buffer is no taller than the window leaves the embedded terminal with
        /// no scrollbar; the launch raises the height and keeps the width, and leaves a tall one alone.
        /// </summary>
        [TestMethod]
        public void EnsureConsoleScrollback_RaisesOnlyAShortBufferHeight()
        {
            Assert.AreEqual((9001 << 16) | 120, ClaudeCodeControl.EnsureConsoleScrollback((30 << 16) | 120));
            Assert.AreEqual((9001 << 16) | 200, ClaudeCodeControl.EnsureConsoleScrollback((50 << 16) | 200));
            Assert.AreEqual((9001 << 16) | 120, ClaudeCodeControl.EnsureConsoleScrollback(null));
            Assert.IsNull(ClaudeCodeControl.EnsureConsoleScrollback(0x23290078));
            Assert.IsNull(ClaudeCodeControl.EnsureConsoleScrollback((32766 << 16) | 120));
        }
    }
}
