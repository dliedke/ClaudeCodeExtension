/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native chat tab status glyph (working / waiting for the user / done / error) and automatic tab
 *          title from the conversation's topic (first prompt, or a low-cost AI call for Claude Code)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Chat Tab Status & Auto Title

        /// <summary>Longest the title call is given; a slow answer just leaves the prompt-based title.</summary>
        private const int SessionTitleTimeoutMs = 60000;

        // The default session's counterparts of NativeChatSessionState.TabStatus / AutoTitle /
        // AutoTitleRequest (the default session is not in _nativeSessions).
        private ChatTabStatus _defaultTabStatus;
        private string _defaultAutoTitle;
        private object _defaultAutoTitleRequest;
        private string _defaultPendingSessionName;

        /// <summary>
        /// Sets what the tab of <paramref name="session"/> (null = the default session) shows in front of
        /// its caption, and refreshes the caption when it changed.
        /// </summary>
        private void SetChatTabStatus(NativeChatSessionState session, ChatTabStatus status)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session != null)
            {
                if (session.TabStatus == status)
                {
                    return;
                }

                session.TabStatus = status;
                UpdateSessionTabCaption(session);
                return;
            }

            if (_defaultTabStatus == status)
            {
                return;
            }

            _defaultTabStatus = status;
            UpdateChatTabCaption();
        }

        private ChatTabStatus GetChatTabStatus(NativeChatSessionState session)
        {
            return session != null ? session.TabStatus : _defaultTabStatus;
        }

        /// <summary>A question, permission or plan card was answered: back to "working" while the turn runs.</summary>
        private void ResumeWorkingChatTabStatus(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool turnInFlight = session != null ? session.TurnInFlight : _nativeTurnInFlight;
            if (turnInFlight && GetChatTabStatus(session) == ChatTabStatus.WaitingForUser)
            {
                SetChatTabStatus(session, ChatTabStatus.Working);
            }
        }

        /// <summary>The turn ended. An error reported during it stays visible until the next prompt.</summary>
        private void CompleteChatTabStatus(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (GetChatTabStatus(session) != ChatTabStatus.Error)
            {
                SetChatTabStatus(session, ChatTabStatus.Done);
            }
        }

        /// <summary>
        /// A relaunch that resumes the same conversation (model/effort/permission switch, interrupt) ends
        /// whatever turn was running, so a tab still showing "working" or "waiting" would never clear.
        /// </summary>
        private void SettleChatTabStatusAfterRelaunch(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ChatTabStatus status = GetChatTabStatus(session);
            if (status == ChatTabStatus.Working || status == ChatTabStatus.WaitingForUser)
            {
                SetChatTabStatus(session, ChatTabStatus.Done);
            }
        }

        /// <summary>
        /// A different conversation starts in this tab (new chat, resume from history): its status and
        /// automatic title belong to the previous one. A title call still running is ignored when it lands.
        /// </summary>
        private void ResetChatTabTopic(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session != null)
            {
                session.AutoTitle = null;
                session.AutoTitleRequest = null;
                session.PendingSessionName = null;
            }
            else
            {
                _defaultAutoTitle = null;
                _defaultAutoTitleRequest = null;
                _defaultPendingSessionName = null;
            }

            SetChatTabStatus(session, ChatTabStatus.None);
        }

        /// <summary>The automatic title for the tab, or empty when there is none or the feature is off.</summary>
        private string GetAutoChatTitle(NativeChatSessionState session)
        {
            if (_settings?.AutoTitleChatTabs != true)
            {
                return string.Empty;
            }

            return (session != null ? session.AutoTitle : _defaultAutoTitle) ?? string.Empty;
        }

        /// <summary>The caption text for a tab: the status glyph (when on) in front of the base caption.</summary>
        private string DecorateChatTabCaption(NativeChatSessionState session, string caption)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings?.ShowChatTabStatus == false)
            {
                return caption;
            }

            ChatTabStatus status = GetChatTabStatus(session);
            if (status == ChatTabStatus.Working)
            {
                EnsureWorkingTabAnimation();
            }

            string glyph = ChatTabCaption.Glyph(
                status,
                _settings?.ChatIconStyle ?? ChatIconStyle.Classic,
                _settings?.ChatWorkingTabIcon ?? ChatWorkingTabIcon.Clock,
                _workingTabFrame);
            return ChatTabCaption.WithGlyph(caption, glyph);
        }

        /// <summary>Time each frame of an animated "working" tab icon stays up.</summary>
        private static readonly TimeSpan WorkingTabFrameInterval = TimeSpan.FromMilliseconds(250);

        private System.Windows.Threading.DispatcherTimer _workingTabTimer;
        private int _workingTabFrame;

        private bool IsWorkingTabIconAnimated
        {
            get
            {
                return _settings?.ShowChatTabStatus != false
                       && _settings?.ChatIconStyle == ChatIconStyle.Modern
                       && ChatTabCaption.WorkingFrames(_settings.ChatWorkingTabIcon).Length > 1;
            }
        }

        /// <summary>
        /// Starts the shared frame clock of the animated "working" tab icon. One timer for every tab:
        /// each tick re-applies the caption of the tabs still working, and the timer stops itself once
        /// none is.
        /// </summary>
        private void EnsureWorkingTabAnimation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsWorkingTabIconAnimated)
            {
                return;
            }

            if (_workingTabTimer == null)
            {
                _workingTabTimer = new System.Windows.Threading.DispatcherTimer { Interval = WorkingTabFrameInterval };
                // DispatcherTimer ticks on the thread that created it: the UI thread.
                _workingTabTimer.Tick += delegate
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    AdvanceWorkingTabAnimation();
                };
            }

            if (!_workingTabTimer.IsEnabled)
            {
                _workingTabTimer.Start();
            }
        }

        private void AdvanceWorkingTabAnimation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _workingTabFrame = (_workingTabFrame + 1) % 10080; // a multiple of every frame count

            bool anyWorking = false;

            if (_defaultTabStatus == ChatTabStatus.Working)
            {
                anyWorking = true;
                UpdateChatTabCaption();
            }

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = new List<NativeChatSessionState>(_nativeSessions.Values);
            }

            foreach (NativeChatSessionState session in sessions)
            {
                if (session.TabStatus == ChatTabStatus.Working)
                {
                    anyWorking = true;
                    UpdateSessionTabCaption(session);
                }
            }

            if (!anyWorking || !IsWorkingTabIconAnimated)
            {
                _workingTabTimer.Stop();
            }
        }

        /// <summary>
        /// Names an untitled tab after the first prompt sent in it: the first words at once, then — for
        /// Claude Code with the AI option on — a short title from the low-cost model when it answers.
        /// Does nothing once the tab has a title of any kind (renamed, GitHub issue, automatic).
        /// </summary>
        private void BeginAutoChatTitle(NativeChatSessionState session, AiProvider? provider, string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings?.AutoTitleChatTabs != true || string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            // A slash command says nothing about the topic; wait for a real prompt.
            if (prompt.TrimStart().StartsWith("/", StringComparison.Ordinal))
            {
                return;
            }

            string agentSessionId = session != null ? session.AgentSession?.SessionId : _agentSession?.SessionId;
            bool hasTitle = !string.IsNullOrWhiteSpace(GetNativeSessionTitle(agentSessionId))
                            || (session != null && session.IssueNumber > 0)
                            || !string.IsNullOrEmpty(session != null ? session.AutoTitle : _defaultAutoTitle)
                            || (session != null ? session.AutoTitleRequest : _defaultAutoTitleRequest) != null;
            if (hasTitle)
            {
                return;
            }

            string fromPrompt = ChatTabCaption.TitleFromPrompt(prompt);
            if (fromPrompt.Length == 0)
            {
                return;
            }

            SetAutoChatTitle(session, fromPrompt);

            if (!_settings.AutoTitleChatTabsWithAi || !IsClaudeProvider(provider))
            {
                return;
            }

            var request = new object();
            if (session != null)
            {
                session.AutoTitleRequest = request;
            }
            else
            {
                _defaultAutoTitleRequest = request;
            }

#pragma warning disable VSSDK007, VSTHRD110
            // Only a conversation this prompt opens is named after it; a resumed one keeps its history's
            // topic, which the new prompt may not describe.
            bool newConversation = IsNewConversation(session != null ? session.ChatTranscript : ChatTranscript);
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => GenerateAiChatTitleAsync(session, provider.Value, prompt, request, newConversation));
#pragma warning restore VSSDK007, VSTHRD110
        }

        private void SetAutoChatTitle(NativeChatSessionState session, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session != null)
            {
                session.AutoTitle = title;
                UpdateSessionTabCaption(session);
            }
            else
            {
                _defaultAutoTitle = title;
                UpdateChatTabCaption();
            }
        }

        /// <summary>
        /// Asks the low-cost model for a title and puts it on the tab, unless the tab moved on to another
        /// conversation (<paramref name="request"/> no longer current) or was closed meanwhile. Any failure
        /// (not logged in, quota, timeout) just keeps the prompt-based title.
        /// </summary>
        private async Task GenerateAiChatTitleAsync(NativeChatSessionState session, AiProvider provider, string prompt, object request,
            bool nameSession)
        {
            string title = string.Empty;

            try
            {
                (string output, string error) = await RunClaudeOneShotAsync(
                    provider,
                    ClaudeCommandBuilder.GetSessionTitleArguments,
                    SessionTitleGenerator.BuildRequest(prompt),
                    SessionTitleTimeoutMs,
                    CancellationToken.None);

                title = SessionTitleGenerator.ParseOutput(output);
                if (title.Length == 0)
                {
                    Debug.WriteLine($"AI chat title: no title ({error}).");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AI chat title failed: {ex.Message}");
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            object current = session != null ? session.AutoTitleRequest : _defaultAutoTitleRequest;
            if (!ReferenceEquals(current, request))
            {
                return;
            }

            if (session != null)
            {
                session.AutoTitleRequest = null;
            }
            else
            {
                _defaultAutoTitleRequest = null;
            }

            bool sessionClosed;
            lock (_sessionLock)
            {
                sessionClosed = session != null && !_nativeSessions.ContainsKey(session.SessionId);
            }

            if (title.Length > 0 && !sessionClosed)
            {
                SetAutoChatTitle(session, title);

                // The AI title also names the session, as a rename would: Session History, the
                // welcome card's recent list and Claude Code's own /resume picker all show it.
                if (!nameSession)
                {
                    return;
                }

                if (session != null)
                {
                    session.PendingSessionName = title;
                }
                else
                {
                    _defaultPendingSessionName = title;
                }

                SaveAutoSessionName(session, writeTranscript: true);
            }
        }

        /// <summary>
        /// True when the transcript holds nothing but the prompt just sent: no earlier prompt and no reply,
        /// so the conversation starts here rather than continuing a resumed one.
        /// </summary>
        private static bool IsNewConversation(ChatTranscriptView transcript)
        {
            if (transcript == null)
            {
                return false;
            }

            int prompts = 0;
            foreach (ChatMessageViewModel message in transcript.Messages)
            {
                if (message.Kind == ChatMessageKind.Assistant || (message.Kind == ChatMessageKind.User && ++prompts > 1))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Turns the AI title waiting in <paramref name="session"/> (null = the default session) into
        /// the session's name: saved with the renamed titles and appended to the Claude Code transcript.
        /// Waits — called again at the end of each turn — while the agent has no session id yet, and
        /// gives up silently when the user named the session meanwhile. <paramref name="writeTranscript"/>
        /// is false at the end of a turn, where <c>SyncClaudeTranscript</c> writes the transcript anyway.
        /// </summary>
        private void SaveAutoSessionName(NativeChatSessionState session, bool writeTranscript)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string title = session != null ? session.PendingSessionName : _defaultPendingSessionName;
            if (string.IsNullOrEmpty(title) || _settings == null)
            {
                return;
            }

            AiProvider? provider = session != null
                ? session.SelectedProvider
                : _currentRunningProvider ?? _settings.SelectedProvider;
            string sessionId = session != null ? session.AgentSession?.SessionId : _agentSession?.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            if (session != null)
            {
                session.PendingSessionName = null;
            }
            else
            {
                _defaultPendingSessionName = null;
            }

            if (!IsClaudeCodeSessionHistoryProvider(provider) || !string.IsNullOrWhiteSpace(GetNativeSessionTitle(sessionId)))
            {
                return;
            }

            if (_settings.SessionCustomTitles == null)
            {
                _settings.SessionCustomTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            _settings.SessionCustomTitles[sessionId] = title;
            SetSessionAutoTitle(sessionId, true);
            SaveSettings();

            if (writeTranscript)
            {
                StartSessionHistoryTask(() => KeepClaudeTitleInTranscriptAsync(provider.Value, sessionId),
                    "claudecode/nativechat/autosessionname");
            }
        }

        /// <summary>
        /// Applies the Classic/Modern icon style to every chat transcript (the default one and each
        /// parallel tab's) and their tab captions. Idempotent, so it is safe from every appearance pass.
        /// </summary>
        /// <summary>
        /// Re-applies the composer state of the main chat and every parallel tab — e.g. after the
        /// enabled agents changed, which decides whether the agent selector shows at all.
        /// </summary>
        private void RefreshAllChatComposers()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsNativeModeActive)
            {
                return;
            }

            UpdateChatComposerState();

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = new List<NativeChatSessionState>(_nativeSessions.Values);
            }

            foreach (NativeChatSessionState session in sessions)
            {
                UpdateChatComposerState(session);
            }
        }

        private void ApplyChatIconStyle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool modern = _settings?.ChatIconStyle == ChatIconStyle.Modern;
            bool compactToolbar = _settings?.CompactChatToolbar == true;

            if (ChatTranscript != null)
            {
                ChatTranscript.UseModernIcons = modern;
                ChatTranscript.CompactToolbar = compactToolbar;
            }

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = new List<NativeChatSessionState>(_nativeSessions.Values);
            }

            foreach (NativeChatSessionState session in sessions)
            {
                if (session.ChatTranscript != null)
                {
                    session.ChatTranscript.UseModernIcons = modern;
                    session.ChatTranscript.CompactToolbar = compactToolbar;
                }
            }

            // The single chat window's "+".
            _chatContainer?.SetModernIcons(modern);
        }

        /// <summary>Re-applies the captions of every chat tab, after a tab status or title setting changed.</summary>
        private void RefreshAllChatTabCaptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            UpdateChatTabCaption();
            RefreshNativeSessionColors();
        }

        #endregion
    }
}
