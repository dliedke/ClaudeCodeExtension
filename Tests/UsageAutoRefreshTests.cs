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

        /// <summary>
        /// Issue #111 (second live user report, after an earlier fix regressed this): the bars
        /// only updated when the Claude Usage tab itself was made the active tab. That fix had
        /// switched the tick guard from the tool window's cached <c>IsWindowVisible</c> flag to a
        /// live <c>IVsWindowFrame.IsVisible()</c> COM read, on the theory that the cached flag
        /// could miss a notification — but <c>IsVisible()</c> reports S_OK for any tool window
        /// frame that is merely open, including a tab sitting inactive behind a sibling tab in the
        /// same dock group (exactly the "Claude Usage parked behind Claude Code" case the user
        /// hit). That made the guard see "visible" all the time and the background refresh never
        /// ran. <c>IsWindowVisible</c> tracks OnShow's FRAMESHOW_TabActivated/TabDeactivated
        /// specifically — "is this the foreground tab right now" — and must be used instead.
        /// </summary>
        [TestMethod]
        public void BackgroundTimer_UsesCachedTabActiveFlag_NotFrameIsVisible()
        {
            StringAssert.Contains(
                ExtractMethodBody(HostSource, "private async Task RefreshUsageInBackgroundAsync()"),
                "if (_usageToolWindow.IsWindowVisible) return;",
                "A live IVsWindowFrame.IsVisible() read cannot tell an inactive background tab from a closed one — it must use the cached tab-active flag.");
            StringAssert.Contains(
                ExtractMethodBody(HostSource, "private async void OnUsageBackgroundRefreshTimerTick(object sender, EventArgs e)"),
                "if (_usageToolWindow?.IsWindowVisible == true) return;",
                "Same requirement for the tick handler's own guard.");
            Assert.IsFalse(HostSource.Contains(
                "if (_settings?.UsageWindowOpened == true) return; // tab is visible"),
                "UsageWindowOpened stays true for a deactivated tab and cannot gate polling.");
        }

        /// <summary>
        /// Issue #111 (first live user report): the bars only updated when the Claude Usage tab
        /// itself was opened, because the heartbeat timer was only ever (re)started from
        /// VisibilityChanged and was explicitly stopped whenever the tab was shown — with nothing
        /// else to restart it if the matching "became hidden" notification never fired for a given
        /// tab-switch path. The timer must run unconditionally once bars are enabled and self-guard
        /// per tick instead.
        /// </summary>
        [TestMethod]
        public void BackgroundTimer_StartsUnconditionally_AndSelfGuardsPerTick()
        {
            string starter = ExtractMethodBody(HostSource, "private void StartUsageBackgroundRefreshTimer()");
            Assert.IsFalse(starter.Contains("IsWindowVisible"),
                "The timer must not refuse to start just because the tool window looks visible right now — every tick re-checks that live.");

            string tick = ExtractMethodBody(HostSource, "private async void OnUsageBackgroundRefreshTimerTick(object sender, EventArgs e)");
            StringAssert.Contains(tick, "IsWindowVisible",
                "Each tick must re-check tab-active state rather than trusting the timer's start-time state.");

            string visChanged = ExtractMethodBody(HostSource, "private void OnUsageToolWindowVisibilityChanged(object sender, bool isVisible)");
            StringAssert.Contains(visChanged, "StartUsageBackgroundRefreshTimer();",
                "Every visibility transition — not just 'became hidden' — must restart the timer so a missed notification can't strand the bars.");

            string autoRefreshChanged = ExtractMethodBody(HostSource, "private void OnUsageAutoRefreshChanged(object sender, int seconds)");
            StringAssert.Contains(autoRefreshChanged, "StartUsageBackgroundRefreshTimer();",
                "Changing the interval must restart the timer unconditionally, not only while the tab is hidden.");
        }

        /// <summary>
        /// Auto-refresh now runs every 1 minute (was 2). Every place that floors or hardcodes the
        /// checked-on interval must agree, or the checkbox, the settings dialog, and the background
        /// timer would silently disagree with each other again.
        /// </summary>
        [TestMethod]
        public void AutoRefreshInterval_IsOneMinute_Everywhere()
        {
            StringAssert.Contains(ControlSource, "int normalized = seconds <= 0 ? 0 : Math.Max(60, seconds);");
            StringAssert.Contains(ControlSource, "int seconds = AutoRefreshCheck?.IsChecked == true ? 60 : 0;");
            StringAssert.Contains(HostSource, ": Math.Max(60, _settings.UsageAutoRefreshSeconds);");

            string dialogSource = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs");
            StringAssert.Contains(dialogSource, "int newAutoRefresh = autoRefreshCheck.IsChecked == true ? 60 : 0;");
            StringAssert.Contains(dialogSource, "if (origAutoRefresh > 0 && origAutoRefresh < 60) origAutoRefresh = 60;");
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

        /// <summary>
        /// Issue #111: a manual Refresh click (or the auto-refresh timer) reloaded the page but kept
        /// showing minutes/hours-old percentages, because CoreWebView2.Reload() only guarantees a
        /// fresh *document* — it still honors HTTP cache validators for the fetch() calls claude.ai's
        /// own client code makes underneath it. This scraper profile has no reason to ever serve a
        /// cached response, so the HTTP cache is disabled outright for its WebView2 instance.
        /// </summary>
        [TestMethod]
        public void ScraperWebView_DisablesHttpCache_SoReloadAlwaysFetchesLiveData()
        {
            StringAssert.Contains(ControlSource, "Network.setCacheDisabled",
                "Reload() must bypass claude.ai's HTTP cache or a manual refresh can silently keep showing stale data.");
        }

        /// <summary>
        /// Issue #111: clicking Refresh while the live WebView2 had already died (issue #131) called
        /// Reload() on a null CoreWebView2 and silently did nothing — the button looked broken instead
        /// of recovering.
        /// </summary>
        [TestMethod]
        public void RefreshButton_RebuildsTheWebViewWhenReloadFindsNothingAlive()
        {
            string refreshClick = ExtractMethodBody(ControlSource, "private void RefreshButton_Click(object sender, RoutedEventArgs e)");

            StringAssert.Contains(refreshClick, "if (!Reload())",
                "A false return from Reload() means there was nothing alive to reload and must trigger a rebuild.");
            StringAssert.Contains(refreshClick, "ReviveOnShowAsync()",
                "The fallback must rebuild the WebView2 the same way an explicit tab-open recovery does.");
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, System.StringComparison.Ordinal);
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
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail($"Unbalanced braces while reading: {signature}");
            return string.Empty;
        }
    }
}
