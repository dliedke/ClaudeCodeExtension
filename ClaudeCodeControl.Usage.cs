/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Wires the Claude usage tool window and the inline mini usage bars into the main control:
 *          - Toolbar/menu entry points open the embedded claude.ai/settings/usage tool window
 *          - Cached snapshot is restored on startup so bars render immediately with stale data
 *          - Startup show-hide initializes WebView2 and waits for actual scrape data before hiding
 *          - Periodic background timer re-shows the tab briefly every N minutes so bars stay fresh
 *          - "Window was open last session" state is persisted and restored
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        private ClaudeUsageToolWindow _usageToolWindow;

        // Completes when HandleScrapedSnapshot runs during a background show-hide cycle,
        // signalling that real data was received and the tab can be hidden safely.
        private TaskCompletionSource<bool> _backgroundScrapeCompletionTcs;

        // Periodically shows the tab briefly so the WebView2 scraper can deliver fresh data
        // to the inline bars while the tab is kept hidden.
        private DispatcherTimer _usageBackgroundRefreshTimer;

        /// <summary>
        /// Restores the cached usage snapshot (if any) so the inline bars
        /// render immediately, then kicks off a background refresh.
        /// Safe to call multiple times.
        /// </summary>
        private void InitializeUsageMonitoring()
        {
            try
            {
                if (_settings == null) return;

                if (!string.IsNullOrEmpty(_settings.LastUsageJson))
                {
                    try
                    {
                        var snap = JsonConvert.DeserializeObject<UsageSnapshot>(_settings.LastUsageJson);
                        if (snap != null) ApplyUsageSnapshot(snap);
                    }
                    catch { }
                }

                UpdateInlineUsagePanelVisibility();

                bool wasWindowOpen = _settings.UsageWindowOpened;
                bool shouldRefresh = wasWindowOpen ||
                    (_settings.ShowInlineUsageBars && IsClaudeProviderSelected());

                if (shouldRefresh)
                {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
                    ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await Task.Delay(2000);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        await EnsureUsageToolWindowAsync(showWindow: wasWindowOpen, updateWindowState: wasWindowOpen);
                    }).FileAndForget("claudecode/usage/auto-reopen");
#pragma warning restore VSSDK007
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("InitializeUsageMonitoring failed: " + ex);
            }
        }

        private bool IsClaudeProviderSelected()
        {
            return _settings?.SelectedProvider == AiProvider.ClaudeCode ||
                   _settings?.SelectedProvider == AiProvider.ClaudeCodeWSL;
        }

        /// <summary>
        /// Shows or hides the inline usage panel based on the active provider,
        /// the user setting, and whether we have any data to show.
        /// </summary>
        private void UpdateInlineUsagePanelVisibility()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (InlineUsagePanel == null) return;

                bool isClaude = IsClaudeProviderSelected();
                bool enabled = _settings?.ShowInlineUsageBars != false;
                bool hasData = !string.IsNullOrEmpty(_settings?.LastUsageJson);

                InlineUsagePanel.Visibility = (isClaude && enabled && hasData)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch { }
        }

        private void ApplyUsageSnapshot(UsageSnapshot snap)
        {
            if (snap == null) return;
            try
            {
                if (!string.IsNullOrEmpty(snap.SessionLabel)) InlineSessionLabel.Text = snap.SessionLabel;
                if (snap.SessionReset != null) InlineSessionReset.Text = snap.SessionReset;
                InlineSessionBar.Value = ClampPercent(snap.SessionPercent);
                InlineSessionPct.Text = ClampPercent(snap.SessionPercent) + "%";

                if (!string.IsNullOrEmpty(snap.WeeklyLabel)) InlineWeeklyLabel.Text = snap.WeeklyLabel;
                if (snap.WeeklyReset != null) InlineWeeklyReset.Text = snap.WeeklyReset;
                InlineWeeklyBar.Value = ClampPercent(snap.WeeklyPercent);
                InlineWeeklyPct.Text = ClampPercent(snap.WeeklyPercent) + "%";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ApplyUsageSnapshot failed: " + ex);
            }
        }

        private static int ClampPercent(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);

        private Task RefreshInlineUsageAsync() => Task.CompletedTask;

        private void HandleScrapedSnapshot(UsageSnapshot snap)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                ApplyUsageSnapshot(snap);
                if (_settings != null)
                {
                    _settings.LastUsageJson = JsonConvert.SerializeObject(snap);
                    _settings.LastUsageTimestamp = DateTime.UtcNow.ToString("o");
                    SaveSettings();
                }
                UpdateInlineUsagePanelVisibility();

                // Signal any in-progress background show-hide that real data arrived —
                // the tab can now be safely hidden.
                _backgroundScrapeCompletionTcs?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("HandleScrapedSnapshot failed: " + ex);
            }
        }

        /// <summary>
        /// Shows the tab briefly, waits for the WebView2 scraper to deliver actual usage data
        /// (or times out), then hides the tab. Bars are updated by HandleScrapedSnapshot before
        /// the tab disappears.  SetBackgroundInitMode prevents Focus() from being stolen.
        /// </summary>
        private async Task ShowHideForScrapeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (_usageToolWindow?.Frame == null) return;
                if (_usageToolWindow.IsWindowVisible) return; // already visible, scraper is live

                var frame = (IVsWindowFrame)_usageToolWindow.Frame;

                _usageToolWindow.UsageControl?.SetBackgroundInitMode(true);
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

                _backgroundScrapeCompletionTcs = new TaskCompletionSource<bool>();
                _usageToolWindow.UsageControl?.Reload();
                // Wait for the JS scraper to post real data (max 10 s)
                await Task.WhenAny(_backgroundScrapeCompletionTcs.Task, Task.Delay(10000));
                _backgroundScrapeCompletionTcs = null;

                _usageToolWindow.UsageControl?.SetBackgroundInitMode(false);
                frame.Hide();
                _usageToolWindow.UsageControl?.MarkNeedsReloadOnShow();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ShowHideForScrapeAsync failed: " + ex);
            }
        }

        /// <summary>
        /// Starts (or restarts) the background refresh timer that periodically calls
        /// ShowHideForScrapeAsync so inline bars stay up to date while the tab is hidden.
        /// The interval uses UsageAutoRefreshSeconds when set (minimum 60 s), or 5 minutes.
        /// Stops and nulls itself if bars are disabled or provider is not Claude.
        /// </summary>
        private void StartUsageBackgroundRefreshTimer()
        {
            _usageBackgroundRefreshTimer?.Stop();
            _usageBackgroundRefreshTimer = null;

            if (_settings?.ShowInlineUsageBars != true || !IsClaudeProviderSelected()) return;
            if (_settings?.UsageWindowOpened == true) return; // tab is visible, no timer needed

            int intervalSeconds = (_settings?.UsageAutoRefreshSeconds > 0)
                ? Math.Max(60, _settings.UsageAutoRefreshSeconds)
                : 300; // 5 minutes default

            _usageBackgroundRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            _usageBackgroundRefreshTimer.Tick += OnUsageBackgroundRefreshTimerTick;
            _usageBackgroundRefreshTimer.Start();
        }

#pragma warning disable VSTHRD100 // async void is required for DispatcherTimer.Tick
        private async void OnUsageBackgroundRefreshTimerTick(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                if (_usageToolWindow?.IsWindowVisible == true) return;
                if (_settings?.ShowInlineUsageBars != true || !IsClaudeProviderSelected()) return;
                await ShowHideForScrapeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OnUsageBackgroundRefreshTimerTick failed: " + ex);
            }
        }

        /// <summary>
        /// Finds (and optionally creates) the Claude usage tool window and shows it.
        /// When showWindow is false and WebView2 is not yet initialized, shows the tab
        /// briefly (BackgroundInitMode) to satisfy WebView2's parent-HWND requirement,
        /// waits for a real scrape to complete, then hides it again.
        /// </summary>
        private async Task EnsureUsageToolWindowAsync(bool showWindow, bool updateWindowState = true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var package = await GetPackageAsync();
                if (package == null) return;

                // Always create (true) so the tool window object exists; showWindow controls
                // whether the tab is actually made visible.
                _usageToolWindow = package.FindToolWindow(typeof(ClaudeUsageToolWindow), 0, true) as ClaudeUsageToolWindow;
                if (_usageToolWindow?.Frame == null) return;

                if (_usageToolWindow.UsageControl != null)
                {
                    _usageToolWindow.UsageControl.UsageDataReceived -= OnUsageToolWindowDataReceived;
                    _usageToolWindow.UsageControl.UsageDataReceived += OnUsageToolWindowDataReceived;
                    _usageToolWindow.UsageControl.AutoRefreshChanged -= OnUsageAutoRefreshChanged;
                    _usageToolWindow.UsageControl.AutoRefreshChanged += OnUsageAutoRefreshChanged;
                    _usageToolWindow.UsageControl.ApplyAutoRefreshSeconds(_settings?.UsageAutoRefreshSeconds ?? 0);
                }

                _usageToolWindow.ClosedByUser -= OnUsageToolWindowClosed;
                _usageToolWindow.ClosedByUser += OnUsageToolWindowClosed;

                var frame = (IVsWindowFrame)_usageToolWindow.Frame;

                if (showWindow)
                {
                    // Stop background timer — tab is visible, scraper runs normally.
                    _usageBackgroundRefreshTimer?.Stop();
                    _usageBackgroundRefreshTimer = null;

                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

                    if (updateWindowState && _settings != null && _settings.UsageWindowOpened != true)
                    {
                        _settings.UsageWindowOpened = true;
                        SaveSettings();
                    }
                    UpdateInlineUsagePanelVisibility();
                }
                else if (_usageToolWindow.UsageControl?.IsWebViewInitialized != true)
                {
                    // WebView2 EnsureCoreWebView2Async needs the control in a visible WPF visual
                    // tree to get a parent HWND and fire the Loaded event. Show the tab,
                    // wait for first navigation (so the rendering surface is established), then
                    // wait for the JS scraper to deliver actual data before hiding.
                    _usageToolWindow.UsageControl.SetBackgroundInitMode(true);
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

                    await _usageToolWindow.UsageControl.WaitForFirstNavigationAsync(15000);

                    _backgroundScrapeCompletionTcs = new TaskCompletionSource<bool>();
                    // Wait up to 10 s for the React components to render and the JS scraper to fire.
                    await Task.WhenAny(_backgroundScrapeCompletionTcs.Task, Task.Delay(10000));
                    _backgroundScrapeCompletionTcs = null;

                    _usageToolWindow.UsageControl.SetBackgroundInitMode(false);
                    frame.Hide();
                    // Mark so the next explicit open re-navigates to rebuild the rendering surface.
                    _usageToolWindow.UsageControl.MarkNeedsReloadOnShow();

                    // Start periodic timer to keep bars fresh while tab stays hidden.
                    StartUsageBackgroundRefreshTimer();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("EnsureUsageToolWindowAsync failed: " + ex);
            }
        }

        private void OnUsageToolWindowDataReceived(object sender, UsageSnapshot snap)
        {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                HandleScrapedSnapshot(snap);
            }).FileAndForget("claudecode/usage/snapshot");
#pragma warning restore VSSDK007
        }

        private void OnUsageAutoRefreshChanged(object sender, int seconds)
        {
            try
            {
                if (_settings == null) return;
                _settings.UsageAutoRefreshSeconds = seconds;
                SaveSettings();
            }
            catch { }
        }

        private void OnUsageToolWindowClosed(object sender, EventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_settings != null && _settings.UsageWindowOpened)
                {
                    _settings.UsageWindowOpened = false;
                    SaveSettings();
                }
                UpdateInlineUsagePanelVisibility();
                // Tab was closed by user — start background timer to keep bars fresh.
                StartUsageBackgroundRefreshTimer();
            }
            catch { }
        }

#pragma warning disable VSTHRD100
        private async void ShowUsageButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ToggleUsageToolWindowAsync();
        }

        /// <summary>
        /// Toolbar button toggle:
        /// - OFF: ForceClose destroys the window (WebView2 disposed), hides bars, stops timer.
        /// - ON: re-enables bars and opens the tab.
        /// X-button close is intercepted by the tool window → frame.Hide() so the
        /// background timer can resume scraping without destroying the WebView2.
        /// </summary>
        private async Task ToggleUsageToolWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var package = await GetPackageAsync();
                if (package == null) return;

                var existing = package.FindToolWindow(typeof(ClaudeUsageToolWindow), 0, false) as ClaudeUsageToolWindow;
                if (existing?.Frame is IVsWindowFrame frame &&
                    frame.IsVisible() == Microsoft.VisualStudio.VSConstants.S_OK)
                {
                    // Button-OFF: stop timer, hide bars, destroy window
                    _usageBackgroundRefreshTimer?.Stop();
                    _usageBackgroundRefreshTimer = null;

                    if (_settings != null)
                    {
                        _settings.ShowInlineUsageBars = false;
                        SaveSettings();
                    }
                    UpdateInlineUsagePanelVisibility();
                    existing.ForceClose();
                    return;
                }

                // Button-ON: re-enable bars then open tab
                if (_settings != null && !_settings.ShowInlineUsageBars)
                {
                    _settings.ShowInlineUsageBars = true;
                    SaveSettings();
                }
                await EnsureUsageToolWindowAsync(showWindow: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ToggleUsageToolWindowAsync failed: " + ex);
            }
        }

#pragma warning disable VSTHRD100
        private async void InlineUsagePanel_Click(object sender, MouseButtonEventArgs e)
#pragma warning restore VSTHRD100
        {
            await EnsureUsageToolWindowAsync(showWindow: true);
        }

        private void DisposeUsageMonitoring()
        {
            _usageBackgroundRefreshTimer?.Stop();
            _usageBackgroundRefreshTimer = null;
            _backgroundScrapeCompletionTcs = null;
        }
    }
}
