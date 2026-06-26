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

                // Cloud usage tracking only applies to stock Claude providers — skip entirely
                // for custom launchers (e.g. Ollama-backed local models).
                if (!IsClaudeProviderSelected())
                {
                    return;
                }

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
                        await EnsureUsageToolWindowAsync(showWindow: wasWindowOpen, updateWindowState: wasWindowOpen, activate: false);
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
            return IsClaudeProvider(GetActiveOrSelectedProvider());
        }

        /// <summary>
        /// Re-tints the inline usage progress bar backgrounds and borders so they
        /// remain readable on both dark and light Visual Studio themes. The
        /// hard-coded dark track from the XAML defaults makes the unfilled portion
        /// of each bar look like a black slab on light themes; this picks a soft
        /// tone that matches the surrounding tool window background instead.
        /// </summary>
        internal void UpdateInlineUsageBarColors()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (InlineSessionBar == null) return;

                bool isDark = IsDarkThemeActive();

                System.Windows.Media.Brush trackBrush;
                System.Windows.Media.Brush borderBrush;

                if (isDark)
                {
                    trackBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x2A));
                    borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
                }
                else
                {
                    // Light theme: soft grey track that contrasts with the blue fill
                    // but doesn't punch a dark hole into the panel background.
                    trackBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xDC, 0xE0));
                    borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB8, 0xB8, 0xBE));
                }

                (trackBrush as System.Windows.Media.SolidColorBrush)?.Freeze();
                (borderBrush as System.Windows.Media.SolidColorBrush)?.Freeze();

                System.Windows.Controls.ProgressBar[] bars =
                {
                    InlineSessionBar, InlineWeeklyBar, InlineExtraUsageBar
                };

                foreach (var bar in bars)
                {
                    if (bar == null) continue;
                    bar.Background = trackBrush;
                    bar.BorderBrush = borderBrush;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UpdateInlineUsageBarColors failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Detects whether the effective theme is dark by inspecting the resolved
        /// VS WindowKey brush brightness. Honors a forced ThemePreference override
        /// when set.
        /// </summary>
        private bool IsDarkThemeActive()
        {
            try
            {
                var pref = _settings?.SelectedThemePreference ?? ThemePreference.Automatic;
                if (pref == ThemePreference.Dark) return true;
                if (pref == ThemePreference.Light) return false;

                var brush = FindResource(Microsoft.VisualStudio.Shell.VsBrushes.WindowKey) as System.Windows.Media.SolidColorBrush;
                if (brush == null) return true;
                var c = brush.Color;
                // ITU-R BT.601 luma
                double luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                return luma < 128.0;
            }
            catch
            {
                return true;
            }
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

                InlineWeeklyLabel.Text = "Weekly limit";
                if (snap.WeeklyReset != null) InlineWeeklyReset.Text = snap.WeeklyReset;
                InlineWeeklyBar.Value = ClampPercent(snap.WeeklyPercent);
                InlineWeeklyPct.Text = ClampPercent(snap.WeeklyPercent) + "%";

                bool showExtra = snap.HasExtraUsage && !string.IsNullOrEmpty(snap.ExtraUsageSpent);
                var extraVis = showExtra ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                InlineExtraUsageStack.Visibility = extraVis;
                InlineExtraUsageBar.Visibility = extraVis;
                InlineExtraUsagePct.Visibility = extraVis;
                if (showExtra)
                {
                    InlineExtraUsageSpent.Text = snap.ExtraUsageSpent;
                    if (snap.ExtraUsageReset != null) InlineExtraUsageReset.Text = snap.ExtraUsageReset;
                    InlineExtraUsageBar.Value = ClampPercent(snap.ExtraUsagePercent);
                    InlineExtraUsagePct.Text = snap.ExtraUsagePercent + "%";
                }
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
        /// Refreshes usage data for the inline bars without showing the tab.
        /// When WebView2 is already initialized, reloads the hidden page directly —
        /// CoreWebView2 processes navigation and JS messaging even when the frame is
        /// hidden, so the tab never needs to become visible. Only falls back to a
        /// show-hide cycle if WebView2 has not been initialized yet.
        /// </summary>
        private async Task ShowHideForScrapeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (_usageToolWindow?.Frame == null) return;
                if (_usageToolWindow.IsWindowVisible) return; // already visible, scraper is live

                // WebView2 already initialized: reload the hidden control without activating
                // the frame — avoids the tab blinking in the VS tab strip on every refresh.
                if (_usageToolWindow.UsageControl?.IsWebViewInitialized == true)
                {
                    _backgroundScrapeCompletionTcs = new TaskCompletionSource<bool>();
                    _usageToolWindow.UsageControl?.Reload();
                    await Task.WhenAny(_backgroundScrapeCompletionTcs.Task, Task.Delay(10000));
                    _backgroundScrapeCompletionTcs = null;
                    // Mark so the rendering surface is rebuilt when the user explicitly opens the tab.
                    _usageToolWindow.UsageControl?.MarkNeedsReloadOnShow();
                    return;
                }

                // WebView2 not yet initialized — must show the frame so it can acquire a
                // parent HWND. ShowNoActivate keeps VS focus on the user's active editor/app.
                var frame = (IVsWindowFrame)_usageToolWindow.Frame;
                _usageToolWindow.UsageControl?.SetBackgroundInitMode(true);
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.ShowNoActivate());

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
        /// Stops and nulls itself if bars are disabled, provider is not Claude,
        /// or the tab is already visible. UsageAutoRefreshSeconds=0 ("Off" in the combo)
        /// only suppresses the page-visible reload — background bar refresh still runs
        /// at a 60s default so inline bars never go stale forever.
        /// </summary>
        private void StartUsageBackgroundRefreshTimer()
        {
            _usageBackgroundRefreshTimer?.Stop();
            _usageBackgroundRefreshTimer = null;

            if (_settings?.ShowInlineUsageBars != true || !IsClaudeProviderSelected()) return;
            if (_settings?.UsageWindowOpened == true) return; // tab is visible, no timer needed

            // Combo "Off" (0) → 60s background floor; otherwise honor user's interval (min 60s).
            int intervalSeconds = (_settings?.UsageAutoRefreshSeconds ?? 0) <= 0
                ? 60
                : Math.Max(60, _settings.UsageAutoRefreshSeconds);

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
        private async Task EnsureUsageToolWindowAsync(bool showWindow, bool updateWindowState = true, bool activate = true)
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

                    // ShowNoActivate when the tab is being restored automatically (e.g.
                    // after a solution reload) so the editor / agent terminal keeps focus.
                    if (activate)
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
                    else
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.ShowNoActivate());

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
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.ShowNoActivate());

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
                // Restart (or stop) the background timer to match the new setting immediately.
                if (_usageToolWindow?.IsWindowVisible != true)
                    StartUsageBackgroundRefreshTimer();
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

#pragma warning disable VSTHRD100
        private async void ShowUsageViewMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            var usageProvider = GetActiveOrSelectedProvider();
            if (usageProvider == AiProvider.Devin || usageProvider == AiProvider.DevinNative)
                System.Diagnostics.Process.Start("https://windsurf.com/subscription/usage?referrer=windsurf");
            else
                await ToggleUsageToolWindowAsync();
        }

        /// <summary>
        /// Syncs the Show Usage menu item's checkmark to reflect whether the
        /// usage tool window is currently open. Devin and Devin are link-only
        /// (no embedded window) so the check is suppressed for those providers.
        /// Called from ProviderContextMenu_Opened (the "⚙" menu now hosts this item).
        /// </summary>
        private void SyncShowUsageMenuCheckState()
        {
            if (ShowUsageViewMenuItem == null) return;
            var usageProvider = GetActiveOrSelectedProvider();
            bool isLinkOnly = usageProvider == AiProvider.Devin || usageProvider == AiProvider.DevinNative;
            ShowUsageViewMenuItem.IsChecked = !isLinkOnly && _settings?.UsageWindowOpened == true;
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

        /// <summary>
        /// Signs out the usage page when changing accounts: clears the cached snapshot
        /// (hiding the inline bars immediately), deletes the shared cookie file, and
        /// clears WebView2 cookies + reloads if the WebView is already initialized.
        /// </summary>
        private async Task SignOutUsageWindowIfActiveAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                bool usageActive = _settings?.ShowInlineUsageBars == true ||
                                   _settings?.UsageWindowOpened == true;
                if (!usageActive) return;

                // Clear cached snapshot so bars disappear immediately
                if (_settings != null)
                {
                    _settings.LastUsageJson = null;
                    _settings.LastUsageTimestamp = null;
                    SaveSettings();
                }
                UpdateInlineUsagePanelVisibility();

                // Clear WebView2 cookies (and cookie file) — works even if WebView not yet shown
                var control = _usageToolWindow?.UsageControl;
                if (control != null)
                    await control.SignOutAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SignOutUsageWindowIfActiveAsync failed: " + ex);
            }
        }

        private void DisposeUsageMonitoring()
        {
            _usageBackgroundRefreshTimer?.Stop();
            _usageBackgroundRefreshTimer = null;
            _backgroundScrapeCompletionTcs = null;
        }
    }
}
