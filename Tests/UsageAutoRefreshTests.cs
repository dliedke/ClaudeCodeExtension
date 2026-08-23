/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the timer handoff that keeps Claude usage refreshing behind another VS tab.
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class UsageAutoRefreshTests
    {
        private static string HostSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Usage.cs");

        private static string ToolWindowSource =>
            RepositoryLayout.ReadText("ToolWindows", "ClaudeUsageToolWindow.cs");

        private static string ControlSource =>
            RepositoryLayout.ReadText("UI", "ClaudeUsageControl.xaml.cs");

        [TestMethod]
        public void BackgroundTimer_UsesLiveVisibility_NotPersistedOpenState()
        {
            StringAssert.Contains(HostSource,
                "if (_usageToolWindow?.IsWindowVisible == true) return;");
            Assert.IsFalse(HostSource.Contains(
                "if (_settings?.UsageWindowOpened == true) return; // tab is visible"),
                "UsageWindowOpened stays true for a deactivated tab and cannot gate polling.");
        }

        [TestMethod]
        public void TabVisibility_HandsPollingBetweenVisibleAndOffscreenHosts()
        {
            StringAssert.Contains(HostSource,
                "_usageToolWindow.VisibilityChanged += OnUsageToolWindowVisibilityChanged;");
            StringAssert.Contains(HostSource,
                "private void OnUsageToolWindowVisibilityChanged(object sender, bool isVisible)");
            StringAssert.Contains(ToolWindowSource,
                "_control?.SetHostVisibility(false);");
            StringAssert.Contains(ToolWindowSource,
                "_control?.SetHostVisibility(true);");
        }

        [TestMethod]
        public void PageTimer_RunsOnlyWhileItsToolWindowHostIsActive()
        {
            StringAssert.Contains(ControlSource,
                "RestartAutoRefreshTimer(_isHostVisible ? normalized : 0);");
            StringAssert.Contains(ControlSource,
                "RestartAutoRefreshTimer(visible ? _autoRefreshSeconds : 0);");
        }
    }
}
