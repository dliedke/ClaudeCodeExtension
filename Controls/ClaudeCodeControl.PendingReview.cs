/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Track agent changes for review" (issue #183) - per-turn snapshots, pending list in the Changes view,
 *          per-file and bulk Keep / Undo
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Pending Review Fields

        /// <summary>Pending agent changes of the current repository; null until the first tracked turn.</summary>
        private PendingReviewTracker _pendingReview;

        /// <summary>Turns the pending baselines into <see cref="ChangedFile"/> rows for the Changes view.</summary>
        private FileChangeTracker _pendingChangeTracker;

        /// <summary>Turn key for prompts sent to the embedded terminal (native turns use their IAgentSession).</summary>
        private static readonly object PendingReviewTerminalTurnKey = new object();

        /// <summary>
        /// Serializes every tracker operation on one background chain, so a turn's end queued just before
        /// the next prompt's begin can never run after it (both are keyed by the same session).
        /// </summary>
        private readonly object _pendingReviewQueueLock = new object();
        private Task _pendingReviewQueue = Task.CompletedTask;

        private bool _diffViewerReviewSubscribed;
        private bool _pendingReviewActionRunning;

        #endregion

        #region Pending Review State

        private bool IsPendingReviewEnabled => _settings != null && _settings.PendingReviewEnabled;

        /// <summary>True when the Changes view should list pending agent changes instead of git's uncommitted ones.</summary>
        private bool IsPendingScopeShown => IsPendingReviewEnabled && _settings.ChangesViewPendingScope;

        private Task RunPendingReviewWorkAsync(Action work)
        {
            lock (_pendingReviewQueueLock)
            {
                Task next = _pendingReviewQueue.ContinueWith(
                    _ =>
                    {
                        try
                        {
                            work();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"PendingReview: {ex}");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                _pendingReviewQueue = next;
                return next;
            }
        }

        private PendingReviewTracker GetOrCreatePendingReview(string repoRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_pendingChangeTracker == null)
            {
                _pendingChangeTracker = new FileChangeTracker();
            }

            PendingReviewTracker current = _pendingReview;
            if (current != null && string.Equals(
                    current.RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar),
                    repoRoot.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            // A different repository (solution switched): the old review list no longer applies.
            FileChangeTracker display = _pendingChangeTracker;
            string root = repoRoot;
            var source = new GitPendingReviewSource(
                root,
                args => RunGitCommandBytes(root, args, GitShowTimeoutMs),
                path => display.IsTrackablePath(path));

            _pendingReview = new PendingReviewTracker(source);
            return _pendingReview;
        }

        /// <summary>
        /// A turn left open by a chat tab that closed, an agent that relaunched or a send that failed
        /// without an end-of-turn event would otherwise keep the snapshot window open forever. Captured
        /// on the UI thread; the returned predicate only compares references.
        /// </summary>
        private Func<object, bool> CapturePendingReviewDeadTurns()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var alive = new List<object> { PendingReviewTerminalTurnKey };

            if (_agentSession != null && _nativeTurnInFlight)
            {
                alive.Add(_agentSession);
            }

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = new List<NativeChatSessionState>(_nativeSessions.Values);
            }

            foreach (NativeChatSessionState state in sessions)
            {
                if (state?.AgentSession != null && state.TurnInFlight)
                {
                    alive.Add(state.AgentSession);
                }
            }

            return key => !alive.Any(a => ReferenceEquals(a, key));
        }

        #endregion

        #region Turn Boundaries

        /// <summary>
        /// Snapshots the working tree before a prompt reaches the agent. Must be awaited before sending:
        /// whatever the agent writes before the snapshot would otherwise be taken as the starting point.
        /// No-op unless "Track agent changes for review" is on and the workspace is in a git repository.
        /// </summary>
        private async Task BeginPendingReviewTurnAsync(object turnKey)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!IsPendingReviewEnabled || turnKey == null)
                    return;

                string workspaceDir = await GetEffectiveWorkspaceDirectoryAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string repoRoot = FindGitRepositoryRoot(workspaceDir);
                if (string.IsNullOrEmpty(repoRoot))
                    return;

                PendingReviewTracker tracker = GetOrCreatePendingReview(repoRoot);
                Func<object, bool> isDead = CapturePendingReviewDeadTurns();

                await RunPendingReviewWorkAsync(() =>
                {
                    tracker.EndTurnsWhere(key => !ReferenceEquals(key, turnKey) && isDead(key));
                    tracker.BeginTurn(turnKey);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: begin turn failed: {ex.Message}");
            }
        }

        /// <summary>Folds a finished turn's changes into the pending list. Fire-and-forget, safe from any path.</summary>
        private void EndPendingReviewTurn(object turnKey)
        {
            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null || turnKey == null)
                return;

            Task work = RunPendingReviewWorkAsync(() => tracker.EndTurn(turnKey));

#pragma warning disable VSSDK007, VSTHRD003 // Deliberately detached; `work` runs on the thread pool and never needs the UI thread
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await work;
                await RefreshPendingReviewViewAsync(false);
            }).FileAndForget("claudecode/pendingreview/endturn");
#pragma warning restore VSSDK007, VSTHRD003
        }

        #endregion

        #region Changes View

        /// <summary>Keeps the Changes view's scope bar in step with the setting.</summary>
        private void SyncPendingReviewViewerMode()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DiffViewerControl control = _diffViewerWindow?.DiffViewerControl;
            if (control == null)
                return;

            if (!_diffViewerReviewSubscribed)
            {
                control.ScopeChanged += OnDiffViewerScopeChanged;
                control.KeepRequested += OnDiffViewerKeepRequested;
                control.UndoRequested += OnDiffViewerUndoRequested;
                control.KeepAllRequested += OnDiffViewerKeepAllRequested;
                control.UndoAllRequested += OnDiffViewerUndoAllRequested;
                _diffViewerReviewSubscribed = true;
            }

            control.SetPendingReviewMode(IsPendingReviewEnabled, _settings?.ChangesViewPendingScope ?? true);
        }

        /// <summary>Called by the Settings dialog when "Track agent changes for review" is toggled.</summary>
        private void OnPendingReviewSettingChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsPendingReviewEnabled)
            {
                // Off means off: nothing stays in memory.
                PendingReviewTracker tracker = _pendingReview;
                _pendingReview = null;
                if (tracker != null)
                {
                    _ = RunPendingReviewWorkAsync(tracker.Clear);
                }
            }

            if (_diffViewerWindow?.DiffViewerControl == null)
                return;

            SyncPendingReviewViewerMode();

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(RefreshDiffViewAsync)
                .FileAndForget("claudecode/pendingreview/settingchanged");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Shows the pending list in the Changes view (pending scope only). With <paramref name="reconcile"/>
        /// the tracker first picks up changes made since the last look and drops files reverted by hand.
        /// </summary>
        private async Task RefreshPendingReviewViewAsync(bool reconcile)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!IsPendingScopeShown || _diffViewerWindow?.DiffViewerControl == null)
                    return;

                PendingReviewTracker tracker = _pendingReview;
                if (_pendingChangeTracker == null)
                {
                    _pendingChangeTracker = new FileChangeTracker();
                }
                FileChangeTracker display = _pendingChangeTracker;

                var files = new List<ChangedFile>();
                if (tracker != null)
                {
                    await RunPendingReviewWorkAsync(() =>
                    {
                        if (reconcile)
                        {
                            tracker.Reconcile();
                        }

                        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var created = new List<string>();
                        foreach (KeyValuePair<string, FileSnapshot> pending in tracker.GetBaselines())
                        {
                            if (pending.Value.Exists)
                                originals[pending.Key] = pending.Value.DecodeText();
                            else
                                created.Add(pending.Key);
                        }

                        display.SetBaseline(tracker.RepositoryRoot, originals, created, null);
                        List<ChangedFile> changed = display.GetChangedFiles();
                        DiffComputer.ComputeDiffs(changed);
                        files = changed;
                    });
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // The user may have switched scope or turned the feature off while this ran.
                DiffViewerControl control = _diffViewerWindow?.DiffViewerControl;
                if (!IsPendingScopeShown || control == null)
                    return;

                control.SetPendingReviewMode(true, true);
                control.UpdateChangedFiles(files);
                control.SetResetBaselineVisible(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: refresh failed: {ex.Message}");
            }
        }

        private void OnDiffViewerScopeChanged(object sender, bool pendingScope)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null)
                return;

            _settings.ChangesViewPendingScope = pendingScope;
            SaveSettings();

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (pendingScope)
                {
                    await RefreshPendingReviewViewAsync(true);
                }
                else
                {
                    // Re-read git so the uncommitted list is current, not whatever it was before the switch.
                    await ResetDiffBaselineAsync(true, false, false, false, null, true);
                }
            }).FileAndForget("claudecode/pendingreview/scope");
#pragma warning restore VSSDK007
        }

        #endregion

        #region Keep / Undo

        private void OnDiffViewerKeepRequested(object sender, ChangedFile file)
        {
            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null || file == null || _pendingReviewActionRunning)
                return;

            string path = file.FilePath;
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await RunPendingReviewWorkAsync(() => tracker.Keep(path));
                await RefreshPendingReviewViewAsync(false);
            }).FileAndForget("claudecode/pendingreview/keep");
#pragma warning restore VSSDK007
        }

        private void OnDiffViewerKeepAllRequested(object sender, EventArgs e)
        {
            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null || _pendingReviewActionRunning)
                return;

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await RunPendingReviewWorkAsync(() =>
                {
                    foreach (string path in tracker.GetBaselines().Keys)
                    {
                        tracker.Keep(path);
                    }
                });
                await RefreshPendingReviewViewAsync(false);
            }).FileAndForget("claudecode/pendingreview/keepall");
#pragma warning restore VSSDK007
        }

        private void OnDiffViewerUndoRequested(object sender, ChangedFile file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null || file == null || _pendingReviewActionRunning)
                return;

            string question = file.Type == ChangeType.Created
                ? $"Delete {file.FileName}?\n\nThe agent created this file. Undo removes it from disk."
                : file.Type == ChangeType.Deleted
                    ? $"Restore {file.FileName}?\n\nThe agent deleted this file. Undo puts it back as it was."
                    : $"Undo the agent's changes to {file.FileName}?\n\nThe file goes back to how it was before the agent changed it.";

            if (MessageBox.Show(question, "Undo Agent Changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string path = file.FilePath;
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(() => UndoPendingFilesAsync(tracker, new[] { path }))
                .FileAndForget("claudecode/pendingreview/undo");
#pragma warning restore VSSDK007
        }

        private void OnDiffViewerUndoAllRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null || _pendingReviewActionRunning)
                return;

            int count = _diffViewerWindow?.DiffViewerControl?.GetStats().fileCount ?? 0;
            string question =
                $"Undo the agent's changes to all {count} pending file{(count != 1 ? "s" : "")}?\n\n" +
                "Each file goes back to how it was before the agent changed it. Files the agent created are deleted.";

            if (MessageBox.Show(question, "Undo All Agent Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(() => UndoPendingFilesAsync(tracker, null))
                .FileAndForget("claudecode/pendingreview/undoall");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Restores pending files to their baselines (<paramref name="paths"/> null = every pending file)
        /// and takes them off the list. Files are written on the UI thread because a file the agent
        /// created may be open in an editor, which is closed first.
        /// </summary>
        private async Task UndoPendingFilesAsync(PendingReviewTracker tracker, IList<string> paths)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _pendingReviewActionRunning = true;
            try
            {
                Dictionary<string, FileSnapshot> baselines = null;
                await RunPendingReviewWorkAsync(() => baselines = tracker.GetBaselines());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (baselines == null)
                    return;

                IEnumerable<string> targets = paths ?? baselines.Keys.ToList();
                var restored = new List<KeyValuePair<string, FileSnapshot>>();
                var failures = new List<string>();

                foreach (string path in targets)
                {
                    if (!baselines.TryGetValue(path, out FileSnapshot baseline))
                        continue;

                    if (TryRestorePendingFile(path, baseline, out string error))
                    {
                        restored.Add(new KeyValuePair<string, FileSnapshot>(path, baseline));
                    }
                    else
                    {
                        failures.Add($"{Path.GetFileName(path)}: {error}");
                    }
                }

                await RunPendingReviewWorkAsync(() =>
                {
                    foreach (KeyValuePair<string, FileSnapshot> done in restored)
                    {
                        tracker.MarkUndone(done.Key, done.Value);
                    }
                });

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (failures.Count > 0)
                {
                    MessageBox.Show(
                        "Some files could not be restored:\n\n" + string.Join("\n", failures.Take(10)) +
                        (failures.Count > 10 ? $"\n... and {failures.Count - 10} more" : string.Empty),
                        "Undo Agent Changes",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                _pendingReviewActionRunning = false;
            }

            await RefreshPendingReviewViewAsync(false);
        }

        /// <summary>
        /// Writes a baseline back to disk byte-for-byte, or deletes the file when the baseline says it
        /// did not exist. An editor showing the file reloads it the same way it reloads the agent's edits.
        /// </summary>
        private bool TryRestorePendingFile(string path, FileSnapshot baseline, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;

            try
            {
                if (!baseline.Exists)
                {
                    CloseUnmodifiedDocument(path);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    return true;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(path, baseline.Content);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: restore of {path} failed: {ex.Message}");
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Closes the editor tab of a file about to be deleted, unless it holds unsaved edits.</summary>
        private static void CloseUnmodifiedDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Documents == null)
                    return;

                foreach (EnvDTE.Document document in dte.Documents)
                {
                    if (document != null &&
                        string.Equals(document.FullName, path, StringComparison.OrdinalIgnoreCase) &&
                        document.Saved)
                    {
                        document.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: could not close {path}: {ex.Message}");
            }
        }

        #endregion
    }
}
