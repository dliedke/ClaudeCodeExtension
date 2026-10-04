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
 *          - Startup scrape initializes the WebView2 off-screen and waits for actual scrape data
 *          - Periodic background timer re-scrapes every N minutes so bars stay fresh, always
 *            off-screen — the usage tab is only ever shown when the user asks for it
 *          - "Window was open last session" state is persisted and restored
 *          - A Claude CLI account switch (/logout + /login) signs the usage page out so the bars
 *            never keep showing the previous account
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        // A healthy background refresh lands a snapshot every minute; a snapshot older than this
        // means the page could not be read and the bars are showing old numbers (issue #182).
        internal static readonly TimeSpan UsageSnapshotStaleAfter = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Claude CLI account the usage page was last paired with, one entry per side ("windows",
        /// "wsl" — each has its own <c>~/.claude.json</c> and may be signed in to a different
        /// account). A file of its own rather than a settings field: every Visual Studio window
        /// saves its whole in-memory settings on each scrape, so a second window would keep writing
        /// the old account back and then sign the shared page out a second time.
        /// </summary>
        private static readonly string UsageAccountPairingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension",
            "usage-account.json");

        private bool _usageAccountCheckRunning;

        // wslpath result for the distro's ~/.claude.json — resolving it spawns wsl.exe, so once per session.
        private string _usageWslClaudeJsonPath;

        // "side|account" this window saw on its previous check, to notice a switch another window handled.
        private string _usageAccountSeen;

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
                UpdateInlineUsageStaleNotice();

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
                        // An account switch made while Visual Studio was closed must not be
                        // scraped as the old account first.
                        await SyncUsageWithClaudeAccountAsync();
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

                // Spend-limit-only accounts have no weekly meter (issue #182) — hide the row
                // rather than show a stale or empty "Weekly limit".
                var weeklyVis = snap.NoWeeklyLimit ? Visibility.Collapsed : Visibility.Visible;
                InlineWeeklyStack.Visibility = weeklyVis;
                InlineWeeklyBar.Visibility = weeklyVis;
                InlineWeeklyPct.Visibility = weeklyVis;
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

        /// <summary>
        /// false when the last usage snapshot read a zero usage-credits balance — Fable needs
        /// credits, so the model menus and the Recommend AI Model dialog leave it out. No snapshot,
        /// no balance on it, or anything unparsable keeps Fable offered.
        /// </summary>
        private bool IsFableModelOffered()
        {
            try
            {
                if (string.IsNullOrEmpty(_settings?.LastUsageJson)) return true;
                var snap = JsonConvert.DeserializeObject<UsageSnapshot>(_settings.LastUsageJson);
                return snap == null || !snap.HasNoUsageCredits();
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// True when the cached snapshot was last refreshed longer ago than
        /// <see cref="UsageSnapshotStaleAfter"/>. A missing or unparsable timestamp is not
        /// treated as stale (nothing reliable to compare against).
        /// </summary>
        internal static bool IsUsageSnapshotStale(string lastUsageTimestamp, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(lastUsageTimestamp)) return false;
            if (!DateTime.TryParse(lastUsageTimestamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var last))
                return false;
            return nowUtc - last.ToUniversalTime() > UsageSnapshotStaleAfter;
        }

        /// <summary>
        /// Shows a "not updated since" line under the inline bars when the usage page could not
        /// be read for a while, so old numbers are never presented as current (issue #182).
        /// Suppressed while the usage tab is the foreground tab — the page there only posts on
        /// change, so the timestamp can legitimately age while the data is live.
        /// </summary>
        private void UpdateInlineUsageStaleNotice()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (InlineUsageStaleNotice == null) return;

                bool stale = _usageToolWindow?.IsWindowVisible != true &&
                             IsUsageSnapshotStale(_settings?.LastUsageTimestamp, DateTime.UtcNow);
                if (stale && DateTime.TryParse(_settings.LastUsageTimestamp, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var last))
                {
                    InlineUsageStaleNotice.Text = "Not updated since " + last.ToLocalTime().ToString("g") +
                                                  " — click to open the usage page";
                    InlineUsageStaleNotice.Visibility = Visibility.Visible;
                }
                else
                {
                    InlineUsageStaleNotice.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("UpdateInlineUsageStaleNotice failed: " + ex.Message);
            }
        }

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
                UpdateInlineUsageStaleNotice();

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
        /// Refreshes usage data for the inline bars without ever showing the tab.
        ///
        /// The scraper's WebView2 lives in the control's own hidden off-screen host window
        /// (<see cref="ClaudeUsageControl.EnsureAliveAsync"/>), not in the tool window frame, so
        /// it survives the frame being hidden and keeps processing navigation and JS messaging.
        /// After the first build every refresh is therefore a plain Reload() of the page already
        /// loaded there.
        ///
        /// The previous implementation parented the scraper in the frame itself, which meant a
        /// dead WebView2 could only be rebuilt by making the tab visible — and since the frame's
        /// Hide() kills the instance every time (issue #131), that show-hide cycle ran on every
        /// single refresh, popping the usage tab into view once a minute (issue #133).
        /// </summary>
        private async Task RefreshUsageInBackgroundAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var control = _usageToolWindow?.UsageControl;
            try
            {
                if (_usageToolWindow?.Frame == null || control == null) return;
                // _usageToolWindow.IsWindowVisible (not a live IVsWindowFrame.IsVisible() COM
                // read) on purpose: IsVisible() reports S_OK for a tool window frame that is
                // merely open — including a tab sitting inactive behind a sibling tab in the same
                // dock group (see RestoreFrameIfHiddenByVS's own comment on this). That made an
                // earlier version of this guard always see "visible" while the user was on, say,
                // the Claude Code tab with Claude Usage parked behind it, so the background
                // refresh never ran and the bars only ever updated when the tab was clicked
                // (issue #111 recurrence). IsWindowVisible tracks OnShow's
                // FRAMESHOW_TabActivated/TabDeactivated specifically, which is exactly "is this
                // the foreground tab right now" — already visible there really does mean the
                // scraper is live and rendering in the frame itself.
                if (_usageToolWindow.IsWindowVisible) return;

                // Keep the focus-priming paths quiet for the duration of the off-screen work.
                control.SetBackgroundInitMode(true);
                var scrapeCompleted = new TaskCompletionSource<bool>();
                _backgroundScrapeCompletionTcs = scrapeCompleted;

                // A live instance scrapes the page just as well wherever it happens to be
                // parented, so one sitting in the frame (tab open but not the foreground tab) is
                // reloaded instead of torn out and rebuilt off-screen — an HwndHost cannot be
                // reparented, so rebuilding it would blank that tab. Only when there is nothing
                // alive to reload is the off-screen instance built.
                bool builtOffscreen = false;
                if (!control.IsWebViewInitialized || !control.Reload())
                {
                    await control.EnsureAliveAsync(offscreen: true);
                    builtOffscreen = true;
                }

                // Wait for the JS scraper to post real data (max 10 s)
#pragma warning disable VSTHRD003 // scrapeCompleted is completed by our own data-received handler on the UI thread; no cross-context deadlock
                await Task.WhenAny(scrapeCompleted.Task, Task.Delay(10000));

                // Reloading a frame-hosted instance whose rendering host is already gone (issue
                // #131) reports success but never posts anything back, so a silent timeout there
                // means the off-screen instance has to be built after all.
                if (!builtOffscreen && !scrapeCompleted.Task.IsCompleted &&
                    _usageToolWindow?.IsWindowVisible != true)
                {
                    await control.EnsureAliveAsync(offscreen: true);
                    await Task.WhenAny(scrapeCompleted.Task, Task.Delay(10000));
                }
#pragma warning restore VSTHRD003
            }
            catch (Exception ex)
            {
                Debug.WriteLine("RefreshUsageInBackgroundAsync failed: " + ex);
            }
            finally
            {
                _backgroundScrapeCompletionTcs = null;
                // Must not stay set: OnWindowBecameVisible bails out entirely while background
                // init mode is on, so leaving it true would break the next explicit open.
                try { control?.SetBackgroundInitMode(false); } catch { }
            }
        }

        /// <summary>
        /// Starts (or restarts) the background refresh timer that periodically calls
        /// RefreshUsageInBackgroundAsync so inline bars stay up to date while the tab is hidden
        /// or behind another tab in the same dock group.
        /// Stops and nulls itself if bars are disabled or provider is not Claude.
        /// UsageAutoRefreshSeconds=0 ("Off" in the checkbox) only suppresses the page-visible
        /// reload — background bar refresh still runs at a 60s default so inline bars never go
        /// stale forever.
        ///
        /// Deliberately NOT gated on the tool window's current visibility at start time (unlike
        /// the tick handler below): this timer is meant to run continuously once bars are on, so a
        /// missed or mis-timed <see cref="ClaudeUsageToolWindow.VisibilityChanged"/> notification —
        /// e.g. VS not raising TabDeactivated for every way a sibling tab can become the active one
        /// in the same dock group — can never leave the inline bars with no live refresher at all
        /// (issue #111: bars stayed frozen for the whole session unless the user opened the Usage
        /// tab, which is the one path that always ends up doing a fresh reload). Each tick still
        /// re-checks visibility itself before doing any work.
        /// </summary>
        private void StartUsageBackgroundRefreshTimer()
        {
            _usageBackgroundRefreshTimer?.Stop();
            _usageBackgroundRefreshTimer = null;

            if (_settings?.ShowInlineUsageBars != true || !IsClaudeProviderSelected()) return;

            // Checkbox "Off" (0) → 60s background floor; otherwise honor user's interval (min
            // 1m — a legacy JSON value below that floors to it).
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
                if (_settings?.ShowInlineUsageBars != true || !IsClaudeProviderSelected()) return;
                // DispatcherTimer.Tick always fires on the UI thread, so the accesses below are
                // safe despite the analyzer not being able to see that (VSTHRD010).
#pragma warning disable VSTHRD010
                // Ahead of the visibility guard: a page showing in the foreground tab is on the
                // wrong account just the same.
                if (await SyncUsageWithClaudeAccountAsync()) return;
                // IsWindowVisible (not a live IVsWindowFrame.IsVisible() COM read) — see the
                // matching comment on the guard in RefreshUsageInBackgroundAsync for why.
                if (_usageToolWindow?.IsWindowVisible == true) return;
                await RefreshUsageInBackgroundAsync();
                UpdateInlineUsageStaleNotice();
#pragma warning restore VSTHRD010
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
                _usageToolWindow.VisibilityChanged -= OnUsageToolWindowVisibilityChanged;
                _usageToolWindow.VisibilityChanged += OnUsageToolWindowVisibilityChanged;

                var frame = (IVsWindowFrame)_usageToolWindow.Frame;

                if (showWindow)
                {
                    // Start the (self-guarding) background heartbeat here too, not just in the
                    // showWindow:false branch below. It no-ops on every tick while the tab is
                    // genuinely visible (page's own reload handles that), but keeping it running
                    // means the inline bars still get refreshed later even if VS never raises the
                    // VisibilityChanged notification that would otherwise be the only thing to
                    // (re)start it once the user switches away from this tab (issue #111).
                    StartUsageBackgroundRefreshTimer();

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

                    // Restoring the tab is not the same as rendering it: Visual Studio parks a
                    // restored tool window behind whichever sibling tab in its dock group is the
                    // active one, and a tab that never renders never loads its page and never
                    // scrapes. This call self-guards on the foreground-tab flag, so it does
                    // nothing when the tab really is showing — and is what keeps the inline bars
                    // from sitting on the previous session's numbers when it isn't.
                    if (!activate)
                    {
                        await RefreshUsageInBackgroundAsync();
                    }
                }
                else
                {
                    // Tab stays closed: scrape once through the control's off-screen host and
                    // then keep the bars fresh on a timer. WebView2's EnsureCoreWebView2Async
                    // still needs a rendered parent HWND, but that comes from the off-screen
                    // window now — the usage tab itself is never shown (issues #131 / #133).
                    await RefreshUsageInBackgroundAsync();
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
                // Restart the (self-guarding) background timer to match the new interval
                // immediately — see StartUsageBackgroundRefreshTimer for why this no longer
                // branches on the tool window's cached visibility flag.
                StartUsageBackgroundRefreshTimer();
            }
            catch { }
        }

        private void OnUsageToolWindowVisibilityChanged(object sender, bool isVisible)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                // Always (re)start rather than branching on isVisible: the heartbeat now
                // self-guards on a live visibility read every tick (see
                // StartUsageBackgroundRefreshTimer), so stopping it here on isVisible:true used to
                // rely on the matching TabDeactivated notification firing later to start it back up
                // — and VS does not raise that notification for every way a sibling tab in the same
                // dock group can become the active one, which left the inline bars frozen for the
                // rest of the session (issue #111). Restarting unconditionally means a missed
                // "became hidden" notification can no longer strand the bars with no refresher.
                StartUsageBackgroundRefreshTimer();
                UpdateInlineUsageStaleNotice();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OnUsageToolWindowVisibilityChanged failed: " + ex);
            }
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
            bool isDevin = usageProvider == AiProvider.Devin || usageProvider == AiProvider.DevinNative;

            // Usage reporting is only available for Claude Code (embedded window) and Devin (web
            // link). For any other agent, explain instead of opening an empty/irrelevant view
            // (issue #97). Re-sync the checkable menu item so the stray click doesn't leave a check.
            if (!IsClaudeProvider(usageProvider) && !isDevin)
            {
                SyncShowUsageMenuCheckState();
                MessageBox.Show(
                    "Usage information is only available for Claude Code and Devin.\n\n" +
                    "Switch the active code agent to Claude Code or Devin to view usage.",
                    "Show Usage",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (isDevin)
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
        /// (hiding the inline bars immediately), signs the page's claude.ai session out and
        /// tells the user to sign in there with the new account.
        /// </summary>
        /// <param name="pairedAccount">
        /// CLI account the page belongs to from now on (see <see cref="SyncUsageWithClaudeAccountAsync"/>).
        /// Null — the Change Account menu items, which sign out before the CLI has switched — forgets
        /// the pairing, so the next check adopts whatever account the CLI ends up on instead of
        /// treating it as a second switch.
        /// </param>
        private async Task SignOutUsageWindowIfActiveAsync(string pairedAccount = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                string side = GetUsageAccountSide(GetActiveOrSelectedProvider() == AiProvider.ClaudeCodeWSL);
                await Task.Run(() => WriteUsageAccountPairing(UsageAccountPairingPath, side, pairedAccount));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                bool usageActive = _settings?.ShowInlineUsageBars == true ||
                                   _settings?.UsageWindowOpened == true;
                if (!usageActive) return;

                ClearCachedUsageSnapshot();

                // Also when this session has not created the tool window yet: the claude.ai session
                // lives in the persistent WebView2 profile, so skipping the sign-out here let the next
                // build come straight back as the old account.
                var control = await GetUsageControlAsync();
                if (control != null)
                    await control.SignOutAsync();

                await ShowAgentFinishNotificationAsync(
                    "Claude account changed: the Claude Usage page was signed out. Sign in there with the new account to see its usage again.",
                    "Open Claude Usage",
                    () => EnsureUsageToolWindowAsync(showWindow: true),
                    InfoBarSlot.UsageAccount);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SignOutUsageWindowIfActiveAsync failed: " + ex);
            }
        }

        /// <summary>Drops the cached snapshot so the inline bars disappear immediately.</summary>
        private void ClearCachedUsageSnapshot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_settings != null)
            {
                _settings.LastUsageJson = null;
                _settings.LastUsageTimestamp = null;
                SaveSettings();
            }
            UpdateInlineUsagePanelVisibility();
        }

        /// <summary>
        /// The usage tool window's control, creating the (hidden) tool window when this session has
        /// not done so yet. Null only when the package is unavailable.
        /// </summary>
        private async Task<ClaudeUsageControl> GetUsageControlAsync()
        {
            if (_usageToolWindow?.UsageControl != null) return _usageToolWindow.UsageControl;
            var package = await GetPackageAsync();
            var window = package?.FindToolWindow(typeof(ClaudeUsageToolWindow), 0, true) as ClaudeUsageToolWindow;
            return window?.UsageControl;
        }

        /// <summary>
        /// Keeps the usage page on the account the Claude CLI is signed in to. The two logins are
        /// independent — the bars are scraped from the WebView2's own claude.ai session, not from
        /// the CLI's credentials — so <c>/logout</c> + <c>/login</c> typed into the terminal (or
        /// <c>claude auth login</c> anywhere else) moved the agent to another account while the bars
        /// went on showing, and refreshing, the old one. The CLI's current account is compared with
        /// the one this side was last paired with; on a change the page is signed out and the user
        /// is asked to sign in there again. Returns true when a switch was handled.
        /// </summary>
        private async Task<bool> SyncUsageWithClaudeAccountAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_usageAccountCheckRunning) return false;
            _usageAccountCheckRunning = true;
            try
            {
                bool isWsl = GetActiveOrSelectedProvider() == AiProvider.ClaudeCodeWSL;
                string current = await GetClaudeCliAccountIdAsync(isWsl);
                // Signed out (between /logout and /login) or unreadable: nothing to compare yet.
                if (string.IsNullOrEmpty(current)) return false;

                string side = GetUsageAccountSide(isWsl);
                string known = await Task.Run(() => ReadUsageAccountPairing(UsageAccountPairingPath, side));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string seen = _usageAccountSeen;
                _usageAccountSeen = side + "|" + current;

                if (string.IsNullOrEmpty(known))
                {
                    // First check on this side: nothing says the page is on another account.
                    await Task.Run(() => WriteUsageAccountPairing(UsageAccountPairingPath, side, current));
                    return false;
                }
                if (IsClaudeAccountSwitch(known, current))
                {
                    await SignOutUsageWindowIfActiveAsync(pairedAccount: current);
                    return true;
                }

                // Another Visual Studio window already handled this switch and signed the shared
                // page out; only this window's cached bars are still on the old account.
                if (seen != null && !string.Equals(seen, _usageAccountSeen, StringComparison.OrdinalIgnoreCase))
                {
                    ClearCachedUsageSnapshot();
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SyncUsageWithClaudeAccountAsync failed: " + ex);
                return false;
            }
            finally
            {
                _usageAccountCheckRunning = false;
            }
        }

        private async Task<string> GetClaudeCliAccountIdAsync(bool isWsl)
        {
            string path;
            if (isWsl)
            {
                if (string.IsNullOrEmpty(_usageWslClaudeJsonPath))
                {
                    _usageWslClaudeJsonPath = await ResolveWslPathAsync("${CLAUDE_CONFIG_DIR:-$HOME}/.claude.json");
                }
                path = _usageWslClaudeJsonPath;
            }
            else
            {
                path = GetClaudeJsonPath(
                    Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            if (string.IsNullOrEmpty(path)) return null;
            // ~/.claude.json grows to megabytes with per-project history — parse it off the UI thread.
            return await Task.Run(() => ReadClaudeAccountIdFromFile(path));
        }

        /// <summary>
        /// Where the CLI keeps its <c>.claude.json</c> on the Windows side: in the folder
        /// <c>CLAUDE_CONFIG_DIR</c> names when that is set, in the user profile otherwise. Reading
        /// the profile copy regardless would watch a file a relocated CLI never writes, and a
        /// switch would go unnoticed.
        /// </summary>
        internal static string GetClaudeJsonPath(string configDir, string userProfile)
        {
            string folder = string.IsNullOrWhiteSpace(configDir)
                ? userProfile
                : Environment.ExpandEnvironmentVariables(configDir.Trim());
            return Path.Combine(folder, ".claude.json");
        }

        private static string GetUsageAccountSide(bool isWsl) => isWsl ? "wsl" : "windows";

        internal static string ReadClaudeAccountIdFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return ParseClaudeAccountId(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ReadClaudeAccountIdFromFile failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Identifies the account the CLI is signed in to from the contents of its
        /// <c>~/.claude.json</c>: <c>accountUuid/organizationUuid</c> of <c>oauthAccount</c>, so
        /// switching to another organization of the same login counts too (usage limits are per
        /// organization); the e-mail address when the UUIDs are missing. Null when signed out or
        /// unreadable — including a half-written file caught mid-save by the CLI.
        /// </summary>
        internal static string ParseClaudeAccountId(string claudeJson)
        {
            if (string.IsNullOrWhiteSpace(claudeJson)) return null;
            try
            {
                if (!(JObject.Parse(claudeJson)["oauthAccount"] is JObject account)) return null;

                string accountUuid = (string)account["accountUuid"];
                if (!string.IsNullOrWhiteSpace(accountUuid))
                {
                    string organizationUuid = (string)account["organizationUuid"];
                    return string.IsNullOrWhiteSpace(organizationUuid) ? accountUuid : accountUuid + "/" + organizationUuid;
                }

                string email = (string)account["emailAddress"];
                return string.IsNullOrWhiteSpace(email) ? null : email;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when both accounts are known and differ.</summary>
        internal static bool IsClaudeAccountSwitch(string pairedAccount, string currentAccount)
        {
            return !string.IsNullOrEmpty(pairedAccount) &&
                   !string.IsNullOrEmpty(currentAccount) &&
                   !string.Equals(pairedAccount, currentAccount, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadUsageAccountPairing(string path, string side)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return (string)JObject.Parse(File.ReadAllText(path))[side];
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ReadUsageAccountPairing failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Records <paramref name="accountId"/> for <paramref name="side"/>; null or empty forgets it.</summary>
        internal static void WriteUsageAccountPairing(string path, string side, string accountId)
        {
            try
            {
                JObject root = null;
                if (File.Exists(path))
                {
                    try { root = JObject.Parse(File.ReadAllText(path)); } catch { }
                }
                root = root ?? new JObject();

                if (string.IsNullOrEmpty(accountId)) root.Remove(side);
                else root[side] = accountId;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(root));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("WriteUsageAccountPairing failed: " + ex.Message);
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
