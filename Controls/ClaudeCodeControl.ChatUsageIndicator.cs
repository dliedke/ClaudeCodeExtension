/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native mode composer's usage button (ring + popup):
 *          - Context window figures come from the last completed turn's status line, per chat tab
 *          - Plan limits come from the cached Claude Usage snapshot, the same numbers the Claude Usage tab shows
 *          - Opening the popup refreshes the usage data in the background; the tab itself is only shown
 *            by "See detailed breakdown"
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        // Status line of the panel's own chat's last completed turn. Parallel tabs keep theirs on NativeChatSessionState.
        private ClaudeStatusLineInput _mainStatusLine;

        /// <summary>Pushes the usage figures for one chat tab. <paramref name="session"/> is null for the panel's own chat.</summary>
        private void UpdateChatUsageIndicator(ChatTranscriptView transcript, NativeChatSessionState session, AiProvider? provider)
        {
            if (transcript == null)
            {
                return;
            }

            ClaudeStatusLineInput status = session != null ? session.LastStatusLine : _mainStatusLine;
            transcript.SetUsageIndicator(BuildChatUsageIndicatorData(provider, status));
        }

        /// <summary>
        /// Re-pushes the figures to every chat tab. Called when a new usage snapshot arrives, so an open popup
        /// updates in place, and when a chat is cleared.
        /// </summary>
        private void RefreshChatUsageIndicators()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (ChatTranscript != null)
                {
                    UpdateChatUsageIndicator(ChatTranscript, null, GetActiveOrSelectedProvider());
                }

                NativeChatSessionState[] sessions;
                lock (_sessionLock)
                {
                    sessions = _nativeSessions.Values.ToArray();
                }

                foreach (NativeChatSessionState session in sessions)
                {
                    UpdateChatUsageIndicator(session.ChatTranscript, session, session.SelectedProvider);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("RefreshChatUsageIndicators failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The popup's contents. The button is only offered for Claude providers, the only ones with a context
        /// window in the status line and a Claude Usage snapshot.
        /// </summary>
        private ChatUsageIndicatorData BuildChatUsageIndicatorData(AiProvider? provider, ClaudeStatusLineInput status)
        {
            var data = new ChatUsageIndicatorData { Visible = IsClaudeProvider(provider) };
            if (!data.Visible)
            {
                return data;
            }

            if (status != null && status.HasCurrentUsage && status.ContextWindowSize > 0)
            {
                // The CLI measures the window from the input side only; the last turn's output does not count.
                long tokens = (long)status.InputTokens + status.CacheCreationTokens + status.CacheReadTokens;
                data.HasContext = true;
                data.ContextTokens = tokens;
                data.ContextWindow = status.ContextWindowSize;
                data.ContextPercent = ChatUsageIndicator.ContextPercent(tokens, status.ContextWindowSize);
            }

            UsageSnapshot snap = ReadCachedUsageSnapshot();
            if (snap != null)
            {
                data.HasPlanUsage = true;
                data.SessionLabel = snap.SessionLabel ?? string.Empty;
                data.SessionPercent = ChatUsageIndicator.ClampPercent(snap.SessionPercent);
                data.SessionReset = snap.SessionReset ?? string.Empty;
                data.ShowWeekly = !snap.NoWeeklyLimit;
                data.WeeklyPercent = ChatUsageIndicator.ClampPercent(snap.WeeklyPercent);
                data.WeeklyReset = snap.WeeklyReset ?? string.Empty;
                data.ShowExtra = snap.HasExtraUsage && !string.IsNullOrEmpty(snap.ExtraUsageSpent);
                data.ExtraPercent = ChatUsageIndicator.ClampPercent(snap.ExtraUsagePercent);
                data.ExtraSpent = snap.ExtraUsageSpent ?? string.Empty;
                data.ExtraReset = snap.ExtraUsageReset ?? string.Empty;
            }

            data.UpdatedText = FormatUsageUpdatedText(snap != null ? _settings?.LastUsageTimestamp : null);
            return data;
        }

        private UsageSnapshot ReadCachedUsageSnapshot()
        {
            if (string.IsNullOrEmpty(_settings?.LastUsageJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<UsageSnapshot>(_settings.LastUsageJson);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ReadCachedUsageSnapshot failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>"Updated 2:15 PM", or "Not updated since 2:15 PM" once the snapshot is stale; empty without one.</summary>
        private static string FormatUsageUpdatedText(string lastUsageTimestamp)
        {
            if (string.IsNullOrEmpty(lastUsageTimestamp))
            {
                return string.Empty;
            }

            if (!DateTime.TryParse(lastUsageTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime stamp))
            {
                return string.Empty;
            }

            string time = stamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            return (IsUsageSnapshotStale(lastUsageTimestamp, DateTime.UtcNow) ? "Not updated since " : "Updated ") + time;
        }

        /// <summary>
        /// Ring click: the popup opens and the usage data refreshes behind it. The account guard comes first,
        /// as in the background timer. A visible tab is reloaded in place; a hidden one is scraped off-screen,
        /// so the tab is never shown by this path.
        /// </summary>
        private void OnComposerUsageRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
            ThreadHelper.JoinableTaskFactory.RunAsync(RefreshUsageForIndicatorAsync).FileAndForget("claudecode/usage/indicator");
#pragma warning restore VSSDK007
        }

        private async Task RefreshUsageForIndicatorAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                // A switch handled here means the page was signed out and the user is asked to sign in again.
                if (await SyncUsageWithClaudeAccountAsync())
                {
                    return;
                }

                var control = _usageToolWindow?.UsageControl;
                if (_usageToolWindow?.IsWindowVisible == true && control != null)
                {
                    control.Reload();
                    return;
                }

                await EnsureUsageToolWindowAsync(showWindow: false, updateWindowState: false, activate: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("RefreshUsageForIndicatorAsync failed: " + ex);
            }
        }

        /// <summary>"See detailed breakdown": shows the Claude Usage tab, exactly as the inline bars' click does.</summary>
        private void OnComposerUsageDetailsRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
            ThreadHelper.JoinableTaskFactory.RunAsync(() => EnsureUsageToolWindowAsync(showWindow: true)).FileAndForget("claudecode/usage/details");
#pragma warning restore VSSDK007
        }
    }
}
