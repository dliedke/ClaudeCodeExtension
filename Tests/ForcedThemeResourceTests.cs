/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: A forced Dark / Light / Custom theme only recolors what it overrides. Every VS brush key the panel
 *          and its chat draw with must therefore be on the override list, or that part keeps the VS theme's
 *          color, and the dialogs the panel opens in its theme colors must not draw on them with IDE colors.
 *
 * *******************************************************************************************************************/

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ForcedThemeResourceTests
    {
        /// <summary>Hosted inside the panel, so a forced theme reaches them through its resources.</summary>
        private static readonly string[] PanelXamlFiles =
        {
            "ChatStyles.xaml", "ClaudeCodeControl.xaml", "ChatTranscriptView.xaml"
        };

        /// <summary>
        /// Used only as a TextBox SelectionBrush, which WPF draws translucent over the box's own
        /// background: readable with the IDE's color in either theme.
        /// </summary>
        private static readonly string[] SelectionOnlyKeys = { "HighlightKey" };

        /// <summary>
        /// With the extension forced to Dark while VS ran its light theme, the dropdown menus kept black
        /// text on a near-black background and the native chat's prompt bubble kept its light background
        /// under light text: both drew with VS brush keys a forced theme did not override.
        /// </summary>
        [TestMethod]
        public void EveryVsBrushUsedInThePanel_IsOverriddenByAForcedTheme()
        {
            string theme = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Theme.cs");

            var used = PanelXamlFiles
                .SelectMany(f => Regex.Matches(RepositoryLayout.ReadText("UI", f), @"VsBrushes\.(\w+Key)").Cast<Match>())
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .Except(SelectionOnlyKeys)
                .ToList();
            Assert.IsTrue(used.Count > 0, "The panel XAML should reference VS brush keys.");

            int listStart = theme.IndexOf("_themeResourceKeys = new object[]", StringComparison.Ordinal);
            int listEnd = theme.IndexOf("};", listStart, StringComparison.Ordinal);
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

        /// <summary>
        /// The Session History, Rename and Resume dialogs paint their background in the panel's theme
        /// colors. Their title and ✕ used the IDE's caption colors, so a forced Dark theme in a light IDE
        /// drew a near-black title on a near-black title bar.
        /// </summary>
        [TestMethod]
        public void ThemedTitleBar_FollowsTheDialogsOwnForeground()
        {
            RunOnSta(() =>
            {
                var light = new SolidColorBrush(Color.FromRgb(241, 241, 241));
                var dialog = new Window
                {
                    Title = "Session History",
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    Foreground = light,
                    Content = new Grid()
                };

                ThemedDialog.ApplyChrome(dialog);

                var title = FindTitle(dialog);
                Assert.IsNotNull(title, "Title TextBlock not found in the drawn title bar.");
                Assert.AreSame(light, title.Foreground, "The title must use the dialog's Foreground.");
                Assert.AreSame(light, dialog.Resources[EnvironmentColors.MainWindowButtonActiveGlyphBrushKey],
                    "The ✕ glyph must use the dialog's Foreground.");
            });
        }

        [TestMethod]
        public void ThemedTitleBar_KeepsIdeColors_WhenTheDialogSetsNoForeground()
        {
            RunOnSta(() =>
            {
                var dialog = new Window { Title = "Session History", Content = new Grid() };

                ThemedDialog.ApplyChrome(dialog);

                Assert.IsFalse(dialog.Resources.Contains(EnvironmentColors.MainWindowActiveCaptionTextBrushKey));
                Assert.IsFalse(dialog.Resources.Contains(EnvironmentColors.MainWindowButtonActiveGlyphBrushKey));
            });
        }

        private static TextBlock FindTitle(Window dialog)
        {
            var layout = (dialog.Content as Border)?.Child as DockPanel;
            return layout?.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault();
        }

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new AssertFailedException(failure.Message, failure);
            }
        }
    }
}
