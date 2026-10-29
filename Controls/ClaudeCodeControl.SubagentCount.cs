/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Show active subagent count" (Settings → Toolbar, Claude Code only, default off): polls Claude Code session
 *          transcripts and shows a live count of running subagents — in the panel toolbar for the terminal, and in
 *          each native chat tab's composer for that tab's own session.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;

using Microsoft.VisualStudio.Shell;

using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Subagent Count

        /// <summary>How often the transcripts are polled while the count is on.</summary>
        private static readonly TimeSpan SubagentPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// A running subagent that has not written anything for this long is assumed gone (its session was
        /// killed before it could report back). A subagent busy with one very long command drops out until
        /// it writes again.
        /// </summary>
        private static readonly TimeSpan SubagentStaleAfter = TimeSpan.FromMinutes(15);

        private DispatcherTimer _subagentCountTimer;
        private bool _subagentPollBusy;

        // Everything below is only touched by the poll, which never overlaps itself (_subagentPollBusy).
        private string _subagentSessionToken;
        private DateTime _subagentEpochUtc;

        /// <summary>provider|workspace → that workspace's Claude Code transcript directory.</summary>
        private readonly Dictionary<string, string> _subagentDirs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>When each native agent session was first seen: older leftovers of a relaunched CLI do not count.</summary>
        private readonly Dictionary<IAgentSession, DateTime> _subagentChatEpochs = new Dictionary<IAgentSession, DateTime>();

        /// <summary>The chat views showing a badge, so a tab that stops qualifying gets cleared.</summary>
        private readonly HashSet<ChatTranscriptView> _subagentChatViews = new HashSet<ChatTranscriptView>();

        private readonly Dictionary<string, SubagentTranscriptState> _subagentTranscripts =
            new Dictionary<string, SubagentTranscriptState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Read position and parse state of one main-session transcript.</summary>
        private sealed class SubagentTranscriptState
        {
            public long Offset;
            public byte[] Remainder = new byte[0];
            public SubagentActivityTracker Tracker = new SubagentActivityTracker();

            /// <summary>tool_use id → the subagent's own transcript, read from subagents/*.meta.json.</summary>
            public readonly Dictionary<string, string> AgentFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly HashSet<string> SeenMetaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>One native chat tab to count for: its view, its session's transcript ids and directory.</summary>
        private sealed class SubagentChatTarget
        {
            public ChatTranscriptView View;
            public string Directory;
            public string[] SessionIds;
            public DateTime EpochUtc;
            public List<RunningSubagent> Running = new List<RunningSubagent>();
        }

        /// <summary>
        /// Starts or stops the poll to match the setting. Idempotent; called after settings load/apply.
        /// </summary>
        private void RefreshSubagentCountMonitor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings?.ShowSubagentCount != true)
            {
                StopSubagentCountMonitor();
                return;
            }

            if (_subagentCountTimer != null) return;

            _subagentCountTimer = new DispatcherTimer { Interval = SubagentPollInterval };
            _subagentCountTimer.Tick += OnSubagentCountTimerTick;
            _subagentCountTimer.Start();
        }

        private void StopSubagentCountMonitor()
        {
            _subagentCountTimer?.Stop();
            _subagentCountTimer = null;
            _subagentSessionToken = null;
            _subagentDirs.Clear();
            _subagentChatEpochs.Clear();

            // A poll still running on the thread pool owns the parse state; it is dropped once that poll ends.
            if (!_subagentPollBusy) _subagentTranscripts.Clear();
            UpdateSubagentCountBadge(null);
            UpdateChatSubagentBadges(new List<SubagentChatTarget>());
        }

#pragma warning disable VSTHRD100 // async void is required for DispatcherTimer.Tick
        private async void OnSubagentCountTimerTick(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_subagentPollBusy) return;
            _subagentPollBusy = true;
            try
            {
                string workspace = await GetWorkspaceDirectoryAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DateTime now = DateTime.UtcNow;

                // Terminal mode: the panel badge counts the workspace's transcripts (the console's session id is
                // not known). Native mode has no console; every chat tab counts its own session instead.
                AiProvider? provider = _currentRunningProvider;
                bool terminalAlive = !IsNativeModeActive && IsClaudeProvider(provider) &&
                                     terminalHandle != IntPtr.Zero && IsWindow(terminalHandle);
                string terminalDir = null;
                if (terminalAlive)
                {
                    // A new terminal starts a new count: agents left pending by a killed session must not linger.
                    string token = terminalHandle.ToInt64().ToString();
                    if (!string.Equals(token, _subagentSessionToken, StringComparison.Ordinal))
                    {
                        _subagentSessionToken = token;
                        _subagentEpochUtc = now;
                    }

                    terminalDir = await GetSubagentDirectoryAsync(provider.Value, workspace);
                }
                else
                {
                    _subagentSessionToken = null;
                }

                List<SubagentChatTarget> chats = await CollectSubagentChatTargetsAsync(workspace, now);

                DateTime epoch = _subagentEpochUtc;
                List<RunningSubagent> terminalRunning = await Task.Run(() => PollRunningSubagents(terminalDir, epoch, chats, now));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_settings?.ShowSubagentCount != true) return;
                UpdateSubagentCountBadge(terminalRunning);
                UpdateChatSubagentBadges(chats);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OnSubagentCountTimerTick failed: " + ex.Message);
            }
            finally
            {
                _subagentPollBusy = false;
            }
        }

        /// <summary>The panel's own chat plus every parallel chat tab that runs Claude Code.</summary>
        private async Task<List<SubagentChatTarget>> CollectSubagentChatTargetsAsync(string workspace, DateTime nowUtc)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var candidates = new List<Tuple<ChatTranscriptView, IAgentSession, AiProvider?, string>>();
            if (_agentSession != null && ChatTranscript != null)
            {
                candidates.Add(Tuple.Create(ChatTranscript, _agentSession, GetActiveOrSelectedProvider(), workspace));
            }

            NativeChatSessionState[] sessions;
            lock (_sessionLock)
            {
                sessions = _nativeSessions.Values.ToArray();
            }

            foreach (NativeChatSessionState session in sessions)
            {
                if (session.AgentSession == null || session.ChatTranscript == null) continue;

                // GitHub issue tabs run in their worktree, whose transcripts live in a directory of their own.
                string sessionWorkspace = string.IsNullOrEmpty(session.WorkspaceOverride) ? workspace : session.WorkspaceOverride;
                candidates.Add(Tuple.Create(session.ChatTranscript, session.AgentSession, (AiProvider?)session.SelectedProvider, sessionWorkspace));
            }

            var targets = new List<SubagentChatTarget>();
            var liveSessions = new HashSet<IAgentSession>();
            foreach (var candidate in candidates)
            {
                if (!IsClaudeProvider(candidate.Item3)) continue;

                IAgentSession agent = candidate.Item2;
                liveSessions.Add(agent);
                if (!_subagentChatEpochs.TryGetValue(agent, out DateTime epoch))
                {
                    epoch = nowUtc;
                    _subagentChatEpochs[agent] = epoch;
                }

                string[] ids = new[] { agent.SessionId, agent.ResumableSessionId }
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (ids.Length == 0) continue;

                string dir = await GetSubagentDirectoryAsync(candidate.Item3.Value, candidate.Item4);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (string.IsNullOrEmpty(dir)) continue;

                targets.Add(new SubagentChatTarget { View = candidate.Item1, Directory = dir, SessionIds = ids, EpochUtc = epoch });
            }

            foreach (IAgentSession gone in _subagentChatEpochs.Keys.Where(k => !liveSessions.Contains(k)).ToList())
            {
                _subagentChatEpochs.Remove(gone);
            }

            return targets;
        }

        private async Task<string> GetSubagentDirectoryAsync(AiProvider provider, string workspace)
        {
            string key = provider + "|" + workspace;
            if (_subagentDirs.TryGetValue(key, out string dir)) return dir;

            dir = await ResolveSessionDirectoryAsync(provider, workspace);
            _subagentDirs[key] = dir;
            return dir;
        }

        /// <summary>
        /// Reads what the transcripts appended since the last poll. Returns the subagents still running in the
        /// terminal's workspace directory (only transcripts written recently, or still holding running agents,
        /// are looked at) and fills each chat target with the ones of its own session file.
        /// </summary>
        private List<RunningSubagent> PollRunningSubagents(string terminalDir, DateTime terminalEpochUtc,
            List<SubagentChatTarget> chats, DateTime nowUtc)
        {
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var terminalRunning = new List<RunningSubagent>();

            if (!string.IsNullOrEmpty(terminalDir) && Directory.Exists(terminalDir))
            {
                foreach (FileInfo file in new DirectoryInfo(terminalDir).GetFiles("*.jsonl"))
                {
                    terminalRunning.AddRange(PollTranscript(file, terminalDir, terminalEpochUtc, nowUtc, touched));
                }
            }

            foreach (SubagentChatTarget chat in chats)
            {
                foreach (string id in chat.SessionIds)
                {
                    var file = new FileInfo(Path.Combine(chat.Directory, id + ".jsonl"));
                    if (!file.Exists) continue;
                    chat.Running.AddRange(PollTranscript(file, chat.Directory, chat.EpochUtc, nowUtc, touched));
                }
                chat.Running.Sort((a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
            }

            foreach (string gone in _subagentTranscripts.Keys.Where(k => !touched.Contains(k)).ToList())
            {
                _subagentTranscripts.Remove(gone);
            }

            return terminalRunning.OrderBy(a => a.StartedUtc).ToList();
        }

        /// <summary>The running subagents of one main-session transcript that are newer than the epoch and not stale.</summary>
        private List<RunningSubagent> PollTranscript(FileInfo file, string dir, DateTime epochUtc, DateTime nowUtc, HashSet<string> touched)
        {
            var result = new List<RunningSubagent>();
            bool tracked = _subagentTranscripts.TryGetValue(file.FullName, out SubagentTranscriptState state);
            bool recent = nowUtc - file.LastWriteTimeUtc <= SubagentStaleAfter;

            if (!recent && (!tracked || state.Tracker.Running.Count == 0)) return result;

            touched.Add(file.FullName);
            if (!tracked)
            {
                state = new SubagentTranscriptState();
                _subagentTranscripts[file.FullName] = state;
            }

            ReadNewTranscriptLines(file.FullName, state);
            if (state.Tracker.Running.Count == 0) return result;

            string subagentsDir = Path.Combine(dir, Path.GetFileNameWithoutExtension(file.Name), "subagents");
            foreach (RunningSubagent agent in state.Tracker.Running)
            {
                DateTime lastActivity = agent.StartedUtc;
                string agentFile = FindSubagentTranscript(state, subagentsDir, agent.ToolUseId);
                if (agentFile != null)
                {
                    try
                    {
                        DateTime written = File.GetLastWriteTimeUtc(agentFile);
                        if (written > lastActivity) lastActivity = written;
                    }
                    catch
                    {
                        // Keep the start time; the file may be mid-rename.
                    }
                }

                if (lastActivity >= epochUtc && nowUtc - lastActivity <= SubagentStaleAfter)
                {
                    result.Add(agent);
                }
            }

            return result;
        }

        /// <summary>Feeds the complete lines appended since the last read; a trailing partial line waits.</summary>
        private static void ReadNewTranscriptLines(string path, SubagentTranscriptState state)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < state.Offset)
                    {
                        // Rewritten from scratch: start over.
                        state.Offset = 0;
                        state.Remainder = new byte[0];
                        state.Tracker = new SubagentActivityTracker();
                    }

                    if (fs.Length == state.Offset) return;

                    fs.Seek(state.Offset, SeekOrigin.Begin);

                    // Chunked: the first read of a long session is megabytes.
                    var chunk = new byte[1 << 20];
                    int n;
                    while ((n = fs.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        state.Offset += n;

                        int length = state.Remainder.Length + n;
                        byte[] data = chunk;
                        if (state.Remainder.Length > 0)
                        {
                            data = new byte[length];
                            Buffer.BlockCopy(state.Remainder, 0, data, 0, state.Remainder.Length);
                            Buffer.BlockCopy(chunk, 0, data, state.Remainder.Length, n);
                        }
                        int lineStart = 0;
                        for (int i = 0; i < length; i++)
                        {
                            if (data[i] != (byte)'\n') continue;
                            state.Tracker.ProcessLine(Encoding.UTF8.GetString(data, lineStart, i - lineStart));
                            lineStart = i + 1;
                        }

                        state.Remainder = new byte[length - lineStart];
                        Array.Copy(data, lineStart, state.Remainder, 0, state.Remainder.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Subagent count: reading {path} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The subagent's own transcript (subagents/agent-*.jsonl), found through the meta.json Claude Code writes
        /// next to it at launch. Its write time tells a working subagent from one whose session was killed.
        /// </summary>
        private static string FindSubagentTranscript(SubagentTranscriptState state, string subagentsDir, string toolUseId)
        {
            if (state.AgentFiles.TryGetValue(toolUseId, out string known)) return known;

            try
            {
                if (!Directory.Exists(subagentsDir)) return null;
                foreach (string meta in Directory.GetFiles(subagentsDir, "*.meta.json"))
                {
                    if (!state.SeenMetaFiles.Add(meta)) continue;
                    try
                    {
                        string id = (string)JObject.Parse(File.ReadAllText(meta))["toolUseId"];
                        if (string.IsNullOrEmpty(id)) continue;
                        state.AgentFiles[id] = meta.Substring(0, meta.Length - ".meta.json".Length) + ".jsonl";
                    }
                    catch
                    {
                        // Probably still being written: look at it again next poll.
                        state.SeenMetaFiles.Remove(meta);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Subagent count: scanning {subagentsDir} failed: {ex.Message}");
            }

            return state.AgentFiles.TryGetValue(toolUseId, out string found) ? found : null;
        }

        /// <summary>Shows the panel badge with the count and a per-agent tooltip, or hides it for null/zero.</summary>
        private void UpdateSubagentCountBadge(IReadOnlyList<RunningSubagent> running)
        {
            if (SubagentCountBadge == null) return;

            int count = running?.Count ?? 0;
            if (count == 0)
            {
                SubagentCountBadge.Visibility = Visibility.Collapsed;
                return;
            }

            SubagentCountText.Text = "👥 " + count;
            SubagentCountBadge.ToolTip = BuildSubagentTooltip(running);
            SubagentCountBadge.Visibility = Visibility.Visible;
        }

        /// <summary>Pushes each chat tab its own count; tabs no longer counted are cleared.</summary>
        private void UpdateChatSubagentBadges(List<SubagentChatTarget> chats)
        {
            var shown = new HashSet<ChatTranscriptView>();
            foreach (SubagentChatTarget chat in chats)
            {
                if (!shown.Add(chat.View)) continue;
                chat.View.SetSubagentCount(chat.Running.Count, chat.Running.Count == 0 ? null : BuildSubagentTooltip(chat.Running));
            }

            foreach (ChatTranscriptView view in _subagentChatViews.Where(v => !shown.Contains(v)).ToList())
            {
                try { view.SetSubagentCount(0, null); } catch { }
            }

            _subagentChatViews.Clear();
            _subagentChatViews.UnionWith(shown);
        }

        private static string BuildSubagentTooltip(IReadOnlyList<RunningSubagent> running)
        {
            var tip = new StringBuilder(running.Count == 1 ? Loc.L("1 active subagent") : Loc.L("{0} active subagents", running.Count));
            foreach (RunningSubagent agent in running)
            {
                string label = string.IsNullOrWhiteSpace(agent.AgentType) ? Loc.L("Agent") : agent.AgentType;
                if (!string.IsNullOrWhiteSpace(agent.Description)) label += " — " + agent.Description;
                if (agent.IsBackground) label += Loc.L(" (background)");
                tip.Append("\n• ").Append(label);
            }
            return tip.ToString();
        }

        #endregion
    }
}
