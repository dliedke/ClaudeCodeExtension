/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the DPI scaling of the Windows Terminal tab bar offset, which is re-evaluated on
 *          every display change so an RDP reconnect at another resolution cannot leave it stale.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class WtTabBarHeightTests
    {
        /// <summary>
        /// The offset hides the tab bar by pushing it above the visible area, so it has to track the
        /// DPI the terminal is actually drawn at - at 150% a 48px offset clips a third too little and
        /// leaves a strip of tab bar visible at the top.
        /// </summary>
        [TestMethod]
        public void WtTabBarHeightForDpi_ScalesWithDpi()
        {
            Assert.AreEqual(48, ClaudeCodeControl.WtTabBarHeightForDpi(96), "100%");
            Assert.AreEqual(60, ClaudeCodeControl.WtTabBarHeightForDpi(120), "125%");
            Assert.AreEqual(72, ClaudeCodeControl.WtTabBarHeightForDpi(144), "150%");
            Assert.AreEqual(96, ClaudeCodeControl.WtTabBarHeightForDpi(192), "200%");
        }

        /// <summary>
        /// GetDpiForWindow returns 0 on failure; treating that as 0 pixels would stop hiding the tab
        /// bar altogether, so it falls back to the unscaled height.
        /// </summary>
        [TestMethod]
        public void WtTabBarHeightForDpi_FallsBackToUnscaledHeightWhenDpiIsUnknown()
        {
            Assert.AreEqual(ClaudeCodeControl.WtTabBarHeightAt96Dpi, ClaudeCodeControl.WtTabBarHeightForDpi(0));
        }

        /// <summary>
        /// A downscaled session (RDP can hand out less than 96 DPI) must still leave a positive
        /// offset - a zero would be indistinguishable from Command Prompt mode, which is what
        /// _wtTabBarHeight == 0 means everywhere else in the terminal code.
        /// </summary>
        [TestMethod]
        public void WtTabBarHeightForDpi_StaysPositiveBelow96Dpi()
        {
            Assert.IsTrue(ClaudeCodeControl.WtTabBarHeightForDpi(72) > 0);
        }
    }
}
