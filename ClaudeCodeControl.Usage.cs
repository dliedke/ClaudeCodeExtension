/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Wires the Claude usage tool window and the inline mini usage bars into the main control:
 *          - Toolbar/menu entry points open the embedded claude.ai/settings/usage tool window
 *          - Cached snapshot is restored on startup so bars render immediately with stale data
 *          - Inline bars refresh from the visible tool window's scraper (no background WebView2 to avoid focus contention)
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
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        private ClaudeUsageToolWindow _usageToolWindow;

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

                if (_settings.UsageWindowOpened)
                {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
                    ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await Task.Delay(2000);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        await EnsureUsageToolWindowAsync(showWindow: true);
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
                bool windowOpened = _settings?.UsageWindowOpened == true;

                InlineUsagePanel.Visibility = (isClaude && enabled && hasData && windowOpened)
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

        /// <summary>
        /// No-op kept for callers that previously kicked the hidden scraper.
        /// Inline bars now update from the visible tool window only — opening
        /// the usage window refreshes the cached snapshot. Trying to host a
        /// second WebView2 in the background fights with the visible one for
        /// focus and causes click drops in the tool window.
        /// </summary>
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine("HandleScrapedSnapshot failed: " + ex);
            }
        }

        /// <summary>
        /// Finds (and optionally creates) the Claude usage tool window and shows it.
        /// </summary>
        private async Task EnsureUsageToolWindowAsync(bool showWindow)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var package = await GetPackageAsync();
                if (package == null) return;

                _usageToolWindow = package.FindToolWindow(typeof(ClaudeUsageToolWindow), 0, showWindow) as ClaudeUsageToolWindow;
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

                if (showWindow)
                {
                    var frame = (IVsWindowFrame)_usageToolWindow.Frame;
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

                    if (_settings != null && _settings.UsageWindowOpened != true)
                    {
                        _settings.UsageWindowOpened = true;
                        SaveSettings();
                    }
                    UpdateInlineUsagePanelVisibility();
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
        /// Toolbar button is a toggle: if the usage tool window is currently
        /// visible, hide it (and the inline bars); otherwise open it. The
        /// hidden tool window is kept in memory so re-opening is instant —
        /// the WebView2 instance and its login cookies survive the toggle.
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
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Hide());
                    if (_settings != null && _settings.UsageWindowOpened)
                    {
                        _settings.UsageWindowOpened = false;
                        SaveSettings();
                    }
                    UpdateInlineUsagePanelVisibility();
                    return;
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
        /// Cleanup hook. Currently no-op — the visible tool window owns its
        /// own WebView2 and disposes it via <see cref="ClaudeUsageControl.Cleanup"/>.
        /// </summary>
        private void DisposeUsageMonitoring()
        {
        }
    }
}
