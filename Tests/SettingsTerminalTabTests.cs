/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the Settings → Terminal tab in native mode (v292.0): the panel layout settings stay
 *          live while the chat is docked in the panel, the rest is terminal-only, and the banner can
 *          dock/undock the chat.
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SettingsTerminalTabTests
    {
        [TestMethod]
        public void TerminalMode_GreenBannerWithTheTerminalSwitchOnly_EverythingLive()
        {
            var state = ClaudeCodeControl.ResolveTerminalTabState(nativeMode: false, chatInOwnTab: false, chatRunning: false);

            Assert.IsFalse(state.ChatDocked);
            Assert.IsTrue(state.BannerActive, "Green: the terminal is what this tab is about.");
            Assert.IsTrue(state.TerminalSwitchVisible);
            Assert.IsFalse(state.ToggleVisible, "Attach/detach means nothing while the terminal is in use.");
            Assert.IsTrue(state.LayoutEnabled);
            Assert.IsTrue(state.TerminalOnlyEnabled);
            Assert.IsFalse(state.NoticeVisible);
        }

        [TestMethod]
        public void NativeMode_ChatInItsOwnTab_AmberBannerWithAttachAndTerminalSwitch_NothingLive()
        {
            var state = ClaudeCodeControl.ResolveTerminalTabState(nativeMode: true, chatInOwnTab: true, chatRunning: true);

            Assert.IsFalse(state.ChatDocked);
            Assert.IsFalse(state.BannerActive, "Amber: nothing on this tab applies.");
            Assert.IsTrue(state.TerminalSwitchVisible, "*Use the terminal* stays in the banner (there is no notice to hold it).");
            Assert.IsTrue(state.ToggleVisible, "The banner offers to attach the chat to the panel.");
            Assert.IsFalse(state.LayoutEnabled, "With the chat in its own tab the panel's layout is not on screen.");
            Assert.IsFalse(state.TerminalOnlyEnabled);
            Assert.IsFalse(state.NoticeVisible, "Only the amber banner on top — no second notice.");
        }

        /// <summary>
        /// The reported case: the prompt panel position and toolbar alignment affect the panel while the
        /// chat is docked in it, yet the whole tab was greyed out as "native mode, does not apply".
        /// </summary>
        [TestMethod]
        public void NativeMode_ChatDockedInThePanel_GreenBannerWithDetachOnly_LayoutLive_RestFencedOff()
        {
            var state = ClaudeCodeControl.ResolveTerminalTabState(nativeMode: true, chatInOwnTab: false, chatRunning: true);

            Assert.IsTrue(state.ChatDocked);
            Assert.IsTrue(state.BannerActive, "Green: the panel layout on this tab is in use.");
            Assert.IsFalse(state.TerminalSwitchVisible, "*Use the terminal* moves to the notice below; the banner keeps only the way out.");
            Assert.IsTrue(state.ToggleVisible, "The banner offers to detach the chat to its tab — the exception that keeps this state escapable.");
            Assert.IsTrue(state.LayoutEnabled);
            Assert.IsFalse(state.TerminalOnlyEnabled, "Prompt box hiding, terminal type, fonts and encoding still need the terminal.");
            Assert.IsTrue(state.NoticeVisible, "An amber notice below the layout settings says the rest is terminal-only.");
        }

        [TestMethod]
        public void NativeMode_WithoutARunningChat_HidesTheToggleButKeepsTheLayoutLive()
        {
            var state = ClaudeCodeControl.ResolveTerminalTabState(nativeMode: true, chatInOwnTab: false, chatRunning: false);

            Assert.IsTrue(state.LayoutEnabled, "No chat tab exists, so the panel is what is on screen.");
            Assert.IsFalse(state.ToggleVisible, "There is no chat to attach or detach yet.");
            Assert.IsTrue(state.NoticeVisible, "The terminal switch is still reachable through the notice.");
        }

        /// <summary>The notice is never green: what sits below it needs the terminal, and with the terminal in use the notice is gone.</summary>
        [TestMethod]
        public void TheNoticeOnlyExistsInNativeModeWithTheChatDocked()
        {
            Assert.IsFalse(ClaudeCodeControl.ResolveTerminalTabState(false, false, true).NoticeVisible);
            Assert.IsFalse(ClaudeCodeControl.ResolveTerminalTabState(false, true, true).NoticeVisible);
            Assert.IsFalse(ClaudeCodeControl.ResolveTerminalTabState(true, true, true).NoticeVisible);
            Assert.IsTrue(ClaudeCodeControl.ResolveTerminalTabState(true, false, true).NoticeVisible);
        }

        [TestMethod]
        public void TerminalTab_SplitsIntoLayoutNoticePromptAndTerminalSections()
        {
            string cs = SettingsDialogSource;

            int layout = cs.IndexOf("var terminalPanelSection = AddSection(terminalStack);", StringComparison.Ordinal);
            int notice = cs.IndexOf("var terminalOnlySection = AddSection(terminalStack);", StringComparison.Ordinal);
            int prompt = cs.IndexOf("var terminalPromptSection = AddSection(terminalStack);", StringComparison.Ordinal);
            int type = cs.IndexOf("var terminalTypeSection = AddSection(terminalStack);", StringComparison.Ordinal);
            Assert.IsTrue(layout >= 0 && layout < notice && notice < prompt && prompt < type,
                "Section order on the tab: panel layout, the terminal-only notice, prompt box, terminal type.");

            StringAssert.Contains(cs, "terminalPromptSection.Children.Add(hidePromptPanelCheck);",
                "Hiding the prompt box is terminal-only, so its checkbox sits below the notice.");
            StringAssert.Contains(cs, "terminalPanelSection.Children.Add(toolbarRightAlignCheck);",
                "Toolbar alignment applies to the docked chat too, so it stays above the notice.");
        }

        [TestMethod]
        public void TerminalTab_EnablesSectionsFromTheResolvedStateNotTheWholeStack()
        {
            string body = ExtractMethodBody(SettingsDialogSource, "void SyncModeBanners()");

            StringAssert.Contains(body, "ResolveTerminalTabState(native, _chatIsInTab, IsNativeModeActive)");
            StringAssert.Contains(body, "terminalPanelSection.IsEnabled = state.LayoutEnabled;");
            StringAssert.Contains(body, "terminalPromptSection.IsEnabled = state.TerminalOnlyEnabled;");
            StringAssert.Contains(body, "terminalTypeSection.IsEnabled = state.TerminalOnlyEnabled;");
            StringAssert.Contains(body, "terminalOnlySection.Visibility = state.NoticeVisible ? Visibility.Visible : Visibility.Collapsed;");
            Assert.IsFalse(body.Contains("terminalStack.IsEnabled"),
                "Greying the whole stack is what hid the live layout settings behind the native-mode banner.");
        }

        [TestMethod]
        public void TerminalTab_BannerDocksAndUndocksTheChatAndResyncs()
        {
            string cs = SettingsDialogSource;

            StringAssert.Contains(cs, "secondaryCaption: L(\"Attach chat to Panel\")",
                "The Terminal tab's banner needs the second button.");
            StringAssert.Contains(cs, "terminalBanner.Secondary.Content = state.ChatDocked ? L(\"Detach chat to Tab\") : L(\"Attach chat to Panel\");");
            StringAssert.Contains(cs, "terminalBanner.Switch.Visibility = state.TerminalSwitchVisible ? Visibility.Visible : Visibility.Collapsed;",
                "*Use the terminal* leaves the banner while the chat is docked (the notice has it).");
            StringAssert.Contains(cs, "terminalBanner.Show(state.BannerActive,", "The banner colour follows the resolved state, not just the mode.");

            string banner = ExtractMethodBody(cs, "private ModeBanner MakeModeBanner(");
            StringAssert.Contains(banner, "Orientation = Orientation.Vertical", "The two buttons stack vertically so the message keeps its width.");
            int secondaryAdd = banner.IndexOf("buttons.Children.Add(secondaryButton);", StringComparison.Ordinal);
            int switchAdd = banner.IndexOf("buttons.Children.Add(switchButton);", StringComparison.Ordinal);
            Assert.IsTrue(secondaryAdd >= 0 && switchAdd > secondaryAdd, "Attach/detach on top, *Use the terminal* below.");

            int click = cs.IndexOf("terminalBanner.Secondary.Click += (s, e) =>", StringComparison.Ordinal);
            Assert.IsTrue(click >= 0, "The second button must be wired.");
            int toggle = cs.IndexOf("await ToggleChatTabAsync();", click, StringComparison.Ordinal);
            int resync = cs.IndexOf("SyncModeBanners();", click, StringComparison.Ordinal);
            Assert.IsTrue(toggle > click && resync > toggle,
                "The button reuses the ⧉ toggle and redoes the banner for the new state afterwards.");
        }

        [TestMethod]
        public void TerminalTab_NoticeOffersTheTerminalSwitch()
        {
            string cs = SettingsDialogSource;

            StringAssert.Contains(cs, "ModeBanner terminalOnlyNotice = MakeModeBanner(L(\"Use the terminal\"), null, dialog, themeBg, themeFg);");
            StringAssert.Contains(cs, "terminalOnlyNotice.Switch.Click += (s, e) => nativeModeCheck.IsChecked = false;");
            StringAssert.Contains(cs, "L(\"The settings below apply to the embedded terminal only.\")");
        }

        private static string SettingsDialogSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs");

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Method not found; update this guard with the rename: {signature}");

            int open = source.IndexOf('{', start + signature.Length);
            Assert.IsTrue(open >= 0, $"No body found for: {signature}");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces in: {signature}");
            return null;
        }
    }
}
