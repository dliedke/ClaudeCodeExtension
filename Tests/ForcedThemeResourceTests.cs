/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: A forced Dark / Light / Custom theme only recolors what it overrides. Every VS brush key the panel
 *          and its chats draw with must therefore be on the override list, or that part keeps the VS theme's
 *          color; chats in their own tabs must get the overrides too, and the dialogs the panel opens in its
 *          theme colors must not draw on them with IDE colors.
 *
 * *******************************************************************************************************************/

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCodeVS;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ForcedThemeResourceTests
    {
        /// <summary>
        /// The panel, and the chats with the window that holds them all, which get a copy of the panel's
        /// overrides when they sit in their own VS tabs.
        /// </summary>
        private static readonly string[] ThemedXamlFiles =
        {
            "ChatStyles.xaml", "ClaudeCodeControl.xaml", "ChatTranscriptView.xaml", "ChatContainerView.xaml"
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
        public void EveryVsBrushUsedInThePanelAndChats_IsOverriddenByAForcedTheme()
        {
            string theme = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Theme.cs");

            var used = ThemedXamlFiles
                .SelectMany(f => Regex.Matches(RepositoryLayout.ReadText("UI", f), @"VsBrushes\.(\w+Key)").Cast<Match>())
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .Except(SelectionOnlyKeys)
                .ToList();
            Assert.IsTrue(used.Count > 0, "The themed XAML should reference VS brush keys.");

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
        /// A chat in its own VS tab is outside the panel, so the panel's resources never reached it: with
        /// the extension on Dark in a light IDE the chat tab stayed light. It now gets a copy of the
        /// overrides, and loses them again under Automatic.
        /// </summary>
        [TestMethod]
        public void ForcedThemeOverrides_AreCopiedToAChatOutsideThePanel_AndRemovedUnderAutomatic()
        {
            RunOnSta(() =>
            {
                var dark = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                var panel = new ResourceDictionary { [Microsoft.VisualStudio.Shell.VsBrushes.WindowKey] = dark };
                var chat = new Border();

                ClaudeCodeControl.CopyForcedThemeResources(panel, chat);
                Assert.AreSame(dark, chat.Resources[Microsoft.VisualStudio.Shell.VsBrushes.WindowKey]);

                ClaudeCodeControl.CopyForcedThemeResources(new ResourceDictionary(), chat);
                Assert.IsFalse(chat.Resources.Contains(Microsoft.VisualStudio.Shell.VsBrushes.WindowKey),
                    "Under Automatic the chat must fall back to the IDE theme.");
            });
        }

        /// <summary>Every place a chat can end up outside the panel gets the overrides.</summary>
        [TestMethod]
        public void EveryChatHost_ReceivesTheForcedTheme()
        {
            string theme = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Theme.cs");
            string nativeMode = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs");
            string nativeChat = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeChat.cs");

            Assert.IsTrue(Regex.IsMatch(nativeMode, @"new ChatTranscriptView\(\);\s+CopyForcedThemeResources\(transcript\);"),
                "A parallel chat's transcript must get the overrides when it is created.");
            StringAssert.Contains(nativeChat, "CopyForcedThemeResources(container.View);",
                "The window that holds all chats must get the overrides.");
            Assert.AreEqual(2, Regex.Matches(theme, @"ApplyForcedThemeToChats\(\);").Count,
                "Both the forced and the Automatic branch must pass the change on to the chats.");
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
