/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: A forced Dark / Light / Custom theme only recolors what it overrides. Every VS brush key the shared
 *          styles draw with must therefore be on the override list, or that part keeps the VS theme's color.
 *
 * *******************************************************************************************************************/

using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ForcedThemeResourceTests
    {
        /// <summary>
        /// The dropdown menus are styled in ChatStyles.xaml. With the extension forced to Dark while VS
        /// ran its light theme, the menu background was overridden but the text color was not, leaving
        /// black text on a near-black menu that only became readable under the mouse.
        /// </summary>
        [TestMethod]
        public void EveryVsBrushUsedBySharedStyles_IsOverriddenByAForcedTheme()
        {
            string styles = RepositoryLayout.ReadText("UI", "ChatStyles.xaml");
            string theme = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Theme.cs");

            var used = Regex.Matches(styles, @"VsBrushes\.(\w+Key)")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.IsTrue(used.Count > 0, "ChatStyles.xaml should reference VS brush keys.");

            int listStart = theme.IndexOf("_themeResourceKeys = new object[]", System.StringComparison.Ordinal);
            int listEnd = theme.IndexOf("};", listStart, System.StringComparison.Ordinal);
            Assert.IsTrue(listStart >= 0 && listEnd > listStart, "_themeResourceKeys not found.");
            string overrideList = theme.Substring(listStart, listEnd - listStart);

            var missing = used.Where(k => !overrideList.Contains("VsBrushes." + k)).ToList();
            Assert.AreEqual(0, missing.Count,
                "Not overridden by a forced theme, so these keep the VS theme's color: " + string.Join(", ", missing));

            foreach (string key in used)
            {
                StringAssert.Contains(theme, "this.Resources[VsBrushes." + key + "]",
                    key + " is on the override list but never assigned a forced color.");
            }
        }
    }
}
