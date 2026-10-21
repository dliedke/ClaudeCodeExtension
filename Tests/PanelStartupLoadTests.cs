/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the startup load of a panel restored behind another tab (issue #204) and the restore of a chat tab
 *          left open with the panel closed (issue #209)
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Visual Studio does not load the panel's content while it sits behind another tab, and the agent
    /// and the native chat tab only start once it loads (issue #204). The package now loads it in the
    /// background. The frame states involved exist only in a running IDE, so this is guarded at the
    /// source level like <see cref="DebugVisibilityTests"/>.
    /// </summary>
    [TestClass]
    public class PanelStartupLoadTests
    {
        private static string Source => RepositoryLayout.ReadText("Package", "ClaudeCodeExtensionPackage.cs");

        [TestMethod]
        public void PackageLoad_SchedulesThePanelBackgroundLoad()
        {
            StringAssert.Contains(Source, "SchedulePanelBackgroundLoad(cancellationToken);",
                "InitializeAsync must schedule the background load, or a panel behind another tab waits for a click.");
        }

        [TestMethod]
        public void AClosedOrOnScreenPanelIsLeftAlone()
        {
            string source = Source;

            // Frame-only lookup: never creates a panel the user closed.
            StringAssert.Contains(source, "(uint)__VSFINDTOOLWIN.FTW_fFrameOnly");
            StringAssert.Contains(source, "panelFrame.IsVisible() != VSConstants.S_OK",
                "A panel the user closed must stay closed.");
            StringAssert.Contains(source, "IsFrameOnScreen(panelFrame))",
                "A panel already on screen loads by itself and must not be touched.");
        }

        [TestMethod]
        public void DisplacedTabsAndFocusAreRestored()
        {
            string source = Source;

            StringAssert.Contains(source, "var onScreenBefore = GetFramesOnScreen(shell);",
                "The tabs on screen before the panel was brought forward must be recorded so they can be put back.");
            StringAssert.Contains(source, "panelFrame.ShowNoActivate();",
                "The panel must be brought forward without taking focus from the editor.");
            StringAssert.Contains(source, "activeFrame.Show();",
                "If a fallback Show moved focus, it must go back to the window that had it.");
        }

        [TestMethod]
        public void OnlyTheChatWithoutPanelLayoutIsRebuilt()
        {
            // Issue #209: an open panel is restored by VS and reopens the chat itself; a closed chat
            // stays closed. Only "chat open, panel closed" is lost by VS and must be rebuilt.
            Assert.IsTrue(ClaudeCodeVS.ClaudeCodeControl.ShouldRestoreChatWithoutPanel(chatTabOpen: true, panelOpen: false));
            Assert.IsFalse(ClaudeCodeVS.ClaudeCodeControl.ShouldRestoreChatWithoutPanel(chatTabOpen: true, panelOpen: true));
            Assert.IsFalse(ClaudeCodeVS.ClaudeCodeControl.ShouldRestoreChatWithoutPanel(chatTabOpen: false, panelOpen: false));
            Assert.IsFalse(ClaudeCodeVS.ClaudeCodeControl.ShouldRestoreChatWithoutPanel(chatTabOpen: false, panelOpen: true));
        }

        [TestMethod]
        public void ChatWithoutPanel_IsRecordedAtShutdownAndRestoredAtStartup()
        {
            string source = Source;

            StringAssert.Contains(source, "protected override int QueryClose(out bool canClose)",
                "The layout must be recorded while every frame is still up, before VS closes them.");
            StringAssert.Contains(source, "?.RecordChatLayoutForRestart();");
            StringAssert.Contains(source, "LoadSettingsForStartup()?.RestoreChatWithoutPanel == true",
                "A closed panel must only be reopened when the chat tab was open on its own at shutdown.");
            StringAssert.Contains(source, "await RestoreChatWithoutPanelAsync(shell, cancellationToken);");
            StringAssert.Contains(source, "panelFrame.Hide();",
                "The panel the user had closed must be closed again once the chat tab is back.");
            StringAssert.Contains(source, "if (!panel.IsChatTabOpen)",
                "The panel must stay open when the chat did not come back, or the user is left with nothing.");
        }
    }
}
