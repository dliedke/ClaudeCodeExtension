/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Reopen chat tabs when the solution opens": remembers the open native chat tabs per workspace and resumes them
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.Shell;
using static ClaudeCodeVS.UI.Loc;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Chat Tab Restore Fields

        /// <summary>
        /// Workspace whose saved tabs were already handed to a native start, or null. A tab list is
        /// restored once per workspace opening, not again on every relaunch (provider, model, restart),
        /// and nothing is recorded for a workspace before its own saved list was read.
        /// </summary>
        private string _chatTabsRestoredWorkspaceKey;

        /// <summary>
        /// Set once the list was recorded for a closing solution or a closing Visual Studio: the tabs that
        /// close after that are being torn down, not closed by the user, and must stay in the list.
        /// </summary>
        private bool _chatTabsSnapshotFrozen;

        #endregion

        #region Chat Tab Restore

        /// <summary>Dictionary key of a workspace in <see cref="ClaudeCodeSettings.WorkspaceChatTabs"/>.</summary>
        private static string GetChatTabsKey(string workspace)
        {
            string normalized = NormalizeWorkspaceDirectory(workspace);
            return string.IsNullOrEmpty(normalized) ? null : normalized.ToLowerInvariant();
        }

        /// <summary>The agents whose conversation a reopened tab can resume.</summary>
        private static bool CanReopenChatTab(AiProvider provider)
        {
            return IsClaudeProvider(provider) || IsCodexSessionHistoryProvider(provider) || IsDevinProvider(provider);
        }

        /// <summary>
        /// The saved tabs of <paramref name="workspace"/>, the first time a native start runs for it;
        /// null on every later start for the same workspace, or when the feature is off. Also ends a
        /// freeze left by the previous solution closing.
        /// </summary>
        private List<SavedChatTab> TakeChatTabsToRestore(string workspace)
        {
            string key = GetChatTabsKey(workspace);
            if (key == null || string.Equals(key, _chatTabsRestoredWorkspaceKey, StringComparison.Ordinal))
            {
                return null;
            }

            _chatTabsRestoredWorkspaceKey = key;
            _chatTabsSnapshotFrozen = false;

            if (_settings?.ReopenChatTabsOnSolutionOpen != true || _settings.WorkspaceChatTabs == null ||
                !_settings.WorkspaceChatTabs.TryGetValue(key, out List<SavedChatTab> saved) || saved == null)
            {
                return null;
            }

            return saved.Where(tab => tab != null && !string.IsNullOrEmpty(tab.SessionId) && CanReopenChatTab(tab.Provider)).ToList();
        }

        /// <summary>
        /// Whether a saved conversation can still be resumed. Claude keeps one JSONL per conversation and
        /// deletes old ones (cleanupPeriodDays), and resuming a missing id fails the launch outright, so
        /// its file is checked. Codex and Devin are trusted: they report a missing thread themselves.
        /// </summary>
        private async Task<bool> SavedChatSessionExistsAsync(AiProvider provider, string workspace, string sessionId)
        {
            if (!IsClaudeProvider(provider))
            {
                return true;
            }

            try
            {
                string directory = await ResolveSessionDirectoryAsync(provider, workspace);
                return !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, sessionId + ".jsonl"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat tab restore: could not check session {sessionId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Arms the main chat's resume for its saved conversation, called by the native start before it
        /// builds the agent. Only when the main chat runs the same agent it ran then and no resume from
        /// Session History is already waiting. Returns the saved tab when armed, so its title can follow.
        /// </summary>
        private async Task<SavedChatTab> ArmDefaultChatRestoreAsync(List<SavedChatTab> saved, AiProvider provider, string workspace)
        {
            SavedChatTab main = saved?.FirstOrDefault(tab => tab.IsDefault);
            if (main == null || main.Provider != provider || Volatile.Read(ref _pendingResumeSessionId) != null)
            {
                return null;
            }

            if (!await SavedChatSessionExistsAsync(provider, workspace, main.SessionId))
            {
                return null;
            }

            Interlocked.CompareExchange(ref _pendingResumeSessionId, main.SessionId, null);
            return main;
        }

        /// <summary>
        /// Reopens the saved parallel tabs after the main chat started. Each tab shows its earlier
        /// messages right away; a Claude tab launches its CLI only with the first prompt sent to it
        /// (<see cref="NativeChatSessionState.DeferAgentLaunch"/>). A conversation already open in some
        /// tab, or no longer on disk, is skipped.
        /// </summary>
        private async Task RestoreParallelChatTabsAsync(List<SavedChatTab> saved, string workspace)
        {
            if (saved == null)
            {
                return;
            }

            foreach (SavedChatTab tab in saved.Where(t => !t.IsDefault))
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (IsChatSessionOpen(tab.SessionId) ||
                        !await SavedChatSessionExistsAsync(tab.Provider, workspace, tab.SessionId))
                    {
                        continue;
                    }

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await RestoreParallelChatTabAsync(tab, workspace);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Chat tab restore: {tab.Provider} session {tab.SessionId} failed: {ex}");
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RecordOpenChatTabs();
        }

        private async Task RestoreParallelChatTabAsync(SavedChatTab tab, string workspace)
        {
            await OpenResumedChatTabAsync(tab.Provider, workspace, tab.SessionId, focus: false, configure: state =>
            {
                state.SelectedModel = tab.SelectedModel;
                state.SelectedClaudeModel = tab.SelectedClaudeModel;
                state.SelectedEffortLevel = tab.SelectedEffortLevel;
                state.SelectedCodexReasoningLevel = tab.SelectedCodexReasoningLevel;
                state.SkipPermissions = tab.SkipPermissions;
                state.PlanMode = tab.PlanMode;
                state.AutoPermissions = tab.AutoPermissions;
                state.ManualMode = tab.ManualMode;
                state.AutoTitle = string.IsNullOrWhiteSpace(tab.Title) ? null : tab.Title;
            });
        }

        /// <summary>
        /// Same steps as <see cref="StartParallelSessionAsync"/>, with a resume and the transcript replay:
        /// opens <paramref name="sessionId"/> in a new chat tab. Used to reopen tabs with the solution and
        /// by Session History's "New Tab". <paramref name="focus"/> brings the tab forward with the
        /// composer focused. Returns null when the agent session could not be created.
        /// </summary>
        private async Task<NativeChatSessionState> OpenResumedChatTabAsync(AiProvider provider, string workspace, string sessionId,
            bool focus, Action<NativeChatSessionState> configure)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            NativeChatSessionState session = CreateAndRegisterSession(provider, workspace, null, state =>
            {
                configure?.Invoke(state);
                state.DeferAgentLaunch = true;
            }, sessionId);

            if (session?.AgentSession == null)
            {
                return null;
            }

            session.ChatTranscript.Clear();
            session.ChatTranscript.SetStatus(L("Starting the agent..."));

            await ShowSessionInTabAsync(session, focusComposer: focus);
            if (focus)
            {
                _lastFocusedNativeSessionId = session.SessionId;
            }

            UpdateChatComposerState(session);
            if (_settings != null)
            {
                session.ChatTranscript.SetChatFont(_settings.NativeChatFontFaceName, _settings.NativeChatFontSizePt);
                session.ChatTranscript.ComposerHeight = _settings.NativeChatComposerHeight;
            }
            session.ChatTranscript.Zoom = _settings?.NativeChatZoom ?? 1.0;

            // Claude returns at once (deferred launch); Codex launches nothing until a prompt either;
            // Devin loads the conversation over ACP and streams it into the chat during the start.
            session.SessionCts = new CancellationTokenSource();
            IAgentSession agent = session.AgentSession;
            await agent.StartAsync(workspace, session.SessionCts.Token);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            session.ChatTranscript.SetStatus(L("Ready."));

            bool replayed = (agent as AcpSession)?.WasResumedFromLoad == true ||
                            await TryReplayResumedTranscriptAsync(provider, workspace, sessionId, session);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!replayed)
            {
                ShowChatWelcome(session.ChatTranscript, workspace);
            }

            UpdateSessionTabCaption(session);
            return session;
        }

        /// <summary>True when a tab — the main chat or a parallel one — already shows this conversation.</summary>
        private bool IsChatSessionOpen(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            if (string.Equals(_agentSession?.ResumableSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            lock (_sessionLock)
            {
                return _nativeSessions.Values.Any(state =>
                    string.Equals(state.AgentSession?.ResumableSessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Saves which chat tabs of the current workspace are open, main chat first, then the parallel
        /// tabs in the order they were opened. Called when a conversation id becomes known, when the
        /// user closes a tab, and once more when the solution or Visual Studio closes (then frozen).
        /// GitHub issue tabs are left out: their worktree is usually gone by the next opening.
        /// </summary>
        private void RecordOpenChatTabs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_chatTabsSnapshotFrozen || _settings?.ReopenChatTabsOnSolutionOpen != true || !IsNativeModeActive)
                {
                    return;
                }

                string key = GetChatTabsKey(_lastWorkspaceDirectory);
                if (key == null || !string.Equals(key, _chatTabsRestoredWorkspaceKey, StringComparison.Ordinal))
                {
                    return;
                }

                var tabs = new List<SavedChatTab>();

                AiProvider mainProvider = _currentRunningProvider ?? _settings.SelectedProvider;
                string mainId = _agentSession?.ResumableSessionId;
                if (!string.IsNullOrEmpty(mainId) && !IsChatTabClosed && CanReopenChatTab(mainProvider))
                {
                    tabs.Add(new SavedChatTab
                    {
                        Provider = mainProvider,
                        SessionId = mainId,
                        IsDefault = true,
                        Title = _defaultAutoTitle ?? string.Empty
                    });
                }

                List<NativeChatSessionState> parallel;
                lock (_sessionLock)
                {
                    parallel = _nativeSessions.Values.OrderBy(state => state.WindowId).ToList();
                }

                foreach (NativeChatSessionState state in parallel)
                {
                    string id = state.AgentSession?.ResumableSessionId;
                    if (string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(state.WorkspaceOverride) ||
                        !CanReopenChatTab(state.SelectedProvider) ||
                        !string.Equals(GetChatTabsKey(state.StartedWorkspace), key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    tabs.Add(new SavedChatTab
                    {
                        Provider = state.SelectedProvider,
                        SessionId = id,
                        Title = state.AutoTitle ?? string.Empty,
                        SelectedModel = state.SelectedModel ?? string.Empty,
                        SelectedClaudeModel = state.SelectedClaudeModel,
                        SelectedEffortLevel = state.SelectedEffortLevel,
                        SelectedCodexReasoningLevel = state.SelectedCodexReasoningLevel,
                        SkipPermissions = state.SkipPermissions,
                        PlanMode = state.PlanMode,
                        AutoPermissions = state.AutoPermissions,
                        ManualMode = state.ManualMode
                    });
                }

                if (_settings.WorkspaceChatTabs == null)
                {
                    _settings.WorkspaceChatTabs = new Dictionary<string, List<SavedChatTab>>();
                }

                if (tabs.Count == 0)
                {
                    if (!_settings.WorkspaceChatTabs.Remove(key))
                    {
                        return;
                    }
                }
                else
                {
                    // SessionStarted comes again with every Claude turn: only write when something changed.
                    if (_settings.WorkspaceChatTabs.TryGetValue(key, out List<SavedChatTab> current) &&
                        Newtonsoft.Json.JsonConvert.SerializeObject(current) == Newtonsoft.Json.JsonConvert.SerializeObject(tabs))
                    {
                        return;
                    }

                    _settings.WorkspaceChatTabs[key] = tabs;
                }

                SaveSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RecordOpenChatTabs error: {ex.Message}");
            }
        }

        /// <summary>
        /// The solution (or Visual Studio) is closing: records the open tabs one last time, then stops
        /// recording, so the tabs being torn down afterwards are not taken for tabs the user closed.
        /// The next native start for a workspace lifts the freeze and restores its list — the same
        /// solution included, hence the key reset.
        /// </summary>
        internal void RecordChatTabsBeforeClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            RecordOpenChatTabs();
            _chatTabsSnapshotFrozen = true;
            _chatTabsRestoredWorkspaceKey = null;
        }

        /// <summary>
        /// The same solution was reopened while its chat kept running (native mode is not restarted for
        /// an unchanged workspace): its tabs never closed, so there is nothing to restore — only the
        /// recording stopped by <see cref="RecordChatTabsBeforeClose"/> resumes.
        /// </summary>
        private void ResumeChatTabRecording(string workspace)
        {
            if (_chatTabsRestoredWorkspaceKey == null)
            {
                _chatTabsRestoredWorkspaceKey = GetChatTabsKey(workspace);
                _chatTabsSnapshotFrozen = false;
            }
        }

        #endregion
    }
}
