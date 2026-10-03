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
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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

            int pendingCount = 0;
            Task work = RunPendingReviewWorkAsync(() =>
            {
                tracker.EndTurn(turnKey);
                pendingCount = tracker.GetBaselines().Count;
            });

#pragma warning disable VSSDK007, VSTHRD003 // Deliberately detached; `work` runs on the thread pool and never needs the UI thread
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await work;
                await RevealPendingReviewAfterTurnAsync(tracker, pendingCount);
                await RefreshPendingReviewViewAsync(false);
            }).FileAndForget("claudecode/pendingreview/endturn");
#pragma warning restore VSSDK007, VSTHRD003
        }

        /// <summary>
        /// "Auto-open Changes on Send" opens the Changes view when the prompt goes out, before the agent has
        /// changed anything. With review tracking on, the end of the turn is when there is something to look at,
        /// so the view is brought forward again whenever files are pending: it may have been closed, or left
        /// behind other tabs, while the agent worked. Without the auto-open setting nothing pops up.
        /// </summary>
        private async Task RevealPendingReviewAfterTurnAsync(PendingReviewTracker tracker, int pendingCount)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (pendingCount == 0 || !IsPendingScopeShown || _settings?.AutoOpenChangesOnPrompt != true)
                    return;

                // A solution switch during the turn replaced the tracker; its files are not this repository's.
                if (!ReferenceEquals(tracker, _pendingReview))
                    return;

                await EnsureDiffViewerWindowAsync(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: could not reveal the Changes view: {ex.Message}");
            }
        }

        /// <summary>Number of files pending review, read on the tracker's own queue (never blocks the UI thread on git).</summary>
        private async Task<int> GetPendingReviewCountAsync()
        {
            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null)
                return 0;

            int count = 0;
            await RunPendingReviewWorkAsync(() => count = tracker.GetBaselines().Count);
            return count;
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
                control.CompareRequested += OnDiffViewerCompareRequested;
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

            string path = file.FilePath;
            bool hasUnsavedEdits = FindOpenDocumentsWithUnsavedChanges(new[] { path }).Count > 0;
            string question = PendingReviewMessages.BuildUndoQuestion(file.FileName, file.Type, hasUnsavedEdits);

            // With unsaved edits at stake, No is the default so a reflexive Enter discards nothing.
            if (!ConfirmPendingReviewAction(question, "Undo Agent Changes", defaultNo: hasUnsavedEdits))
                return;

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

#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // The list comes from the tracker's queue, not from the view: the view can lag a turn behind.
                List<string> paths = null;
                await RunPendingReviewWorkAsync(() => paths = tracker.GetBaselines().Keys.ToList());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (paths == null || paths.Count == 0 || _pendingReviewActionRunning)
                    return;

                List<string> unsaved = FindOpenDocumentsWithUnsavedChanges(paths);
                string question = PendingReviewMessages.BuildUndoAllQuestion(
                    paths.Count, unsaved.Select(Path.GetFileName).ToList());

                if (!ConfirmPendingReviewAction(question, "Undo All Agent Changes", defaultNo: unsaved.Count > 0))
                    return;

                await UndoPendingFilesAsync(tracker, paths);
            }).FileAndForget("claudecode/pendingreview/undoall");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Opens a pending file in Visual Studio's own diff window: its baseline (before the agent) on the left,
        /// the file as it is now on the right. The right side is the real file, so it can be edited in place.
        /// </summary>
        private void OnDiffViewerCompareRequested(object sender, ChangedFile file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            PendingReviewTracker tracker = _pendingReview;
            if (tracker == null || file == null)
                return;

            string path = file.FilePath;
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                FileSnapshot baseline = null;
                await RunPendingReviewWorkAsync(() => baseline = tracker.GetBaseline(path));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Kept or undone in the meantime: nothing left to compare.
                if (baseline == null)
                    return;

                try
                {
                    OpenPendingReviewComparison(path, baseline);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PendingReview: compare of {path} failed: {ex}");
                    ShowPendingReviewWarning(
                        $"Could not open {Path.GetFileName(path)} in the diff window:\n\n{ex.Message}",
                        "Compare Agent Changes");
                }
            }).FileAndForget("claudecode/pendingreview/compare");
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
                    ShowPendingReviewWarning(
                        "Some files could not be restored:\n\n" + string.Join("\n", failures.Take(10)) +
                        (failures.Count > 10 ? $"\n... and {failures.Count - 10} more" : string.Empty),
                        "Undo Agent Changes");
                }
            }
            finally
            {
                _pendingReviewActionRunning = false;
            }

            await RefreshPendingReviewViewAsync(false);
        }

        /// <summary>
        /// Writes a baseline back byte-for-byte, or deletes the file when the baseline says it did not exist.
        /// Open editors are brought in line with the restored file: a created file's tab is closed and any other
        /// editor is reloaded from disk. The Undo confirmation already warned about unsaved edits in those
        /// editors, which is what makes discarding them here safe. Leaving them in place is how an Undo used
        /// to be lost: Visual Studio's own "reload?" prompt, or a later save, put the agent's change back on
        /// disk with nothing left on the review list.
        /// </summary>
        private bool TryRestorePendingFile(string path, FileSnapshot baseline, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;

            try
            {
                if (!baseline.Exists)
                {
                    CloseDocumentDiscardingChanges(path);
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

                RestoreFileContent(path, baseline.Content);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: restore of {path} failed: {ex.Message}");
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Closes the editor tab of a file about to be deleted, discarding any unsaved edits.</summary>
        private static void CloseDocumentDiscardingChanges(string path)
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
                        string.Equals(document.FullName, path, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="path"/> and, when the file is open, reloads its
        /// editor from disk so no unsaved edit survives to be saved back over the restored content. File-change
        /// notifications are suspended around the write, so Visual Studio does not also ask "the file has been
        /// changed outside the editor, reload?" about a change it was told to make.
        /// </summary>
        private static void RestoreFileContent(string path, byte[] content)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            object docData = FindOpenDocumentData(path);
            var changeControl = docData as IVsDocDataFileChangeControl;
            changeControl?.IgnoreFileChanges(1);
            try
            {
                File.WriteAllBytes(path, content);

                if (docData is IVsPersistDocData persist)
                {
                    try
                    {
                        ErrorHandler.ThrowOnFailure(persist.ReloadDocData((uint)_VSRELOADDOCDATA.RDD_IgnoreNextFileChange));
                    }
                    catch (Exception ex)
                    {
                        // The file itself is restored; a failed reload leaves the editor on its old text,
                        // and Visual Studio's own file-change prompt still catches the difference.
                        Debug.WriteLine($"PendingReview: reload of {path} failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                changeControl?.IgnoreFileChanges(0);
            }
        }

        #endregion

        #region Visual Studio Dialogs and Editors

        /// <summary>
        /// Yes/No question through Visual Studio's own message box. A WPF <c>MessageBox</c> blocks the UI thread
        /// in a way Visual Studio's responsiveness monitor reports as a hang: answering an Undo confirmation after
        /// ~20 seconds raised "Visual Studio stopped responding … disabling the extension might help".
        /// </summary>
        private static bool ConfirmPendingReviewAction(string message, string title, bool defaultNo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int result = VsShellUtilities.ShowMessageBox(
                ServiceProvider.GlobalProvider,
                message,
                title,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                defaultNo ? OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND : OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            return result == (int)VSConstants.MessageBoxResult.IDYES;
        }

        /// <summary>Warning with an OK button, through Visual Studio's own message box (see <see cref="ConfirmPendingReviewAction"/>).</summary>
        private static void ShowPendingReviewWarning(string message, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            VsShellUtilities.ShowMessageBox(
                ServiceProvider.GlobalProvider,
                message,
                title,
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        /// <summary>The document data of a file open in an editor, or null when it is not open.</summary>
        private static object FindOpenDocumentData(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                return new RunningDocumentTable(ServiceProvider.GlobalProvider).FindDocument(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: could not look up {path} in the running document table: {ex.Message}");
                return null;
            }
        }

        /// <summary>The files among <paramref name="paths"/> that are open in an editor with unsaved changes.</summary>
        private static List<string> FindOpenDocumentsWithUnsavedChanges(IEnumerable<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var unsaved = new List<string>();
            foreach (string path in paths)
            {
                if (FindOpenDocumentData(path) is IVsPersistDocData docData &&
                    ErrorHandler.Succeeded(docData.IsDocDataDirty(out int isDirty)) &&
                    isDirty != 0)
                {
                    unsaved.Add(path);
                }
            }
            return unsaved;
        }

        /// <summary>
        /// Opens Visual Studio's diff window for a pending file. The baseline is written to a temporary copy
        /// with the same file name (so the editor picks the right language), which Visual Studio deletes when
        /// the window closes. A file the agent created compares against an empty file, and one it deleted is
        /// shown against an empty right side.
        /// </summary>
        private static void OpenPendingReviewComparison(string path, FileSnapshot baseline)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(Package.GetGlobalService(typeof(SVsDifferenceService)) is IVsDifferenceService diffService))
                throw new InvalidOperationException("Visual Studio's diff service is not available.");

            string fileName = Path.GetFileName(path);
            string tempRoot = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_PendingReview", Guid.NewGuid().ToString("N"));

            string left = Path.Combine(tempRoot, "before", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(left));
            File.WriteAllBytes(left, baseline.Exists ? baseline.Content : new byte[0]);

            var options = __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary |
                          __VSDIFFSERVICEOPTIONS.VSDIFFOPT_DetectBinaryFiles;

            string right = path;
            if (!File.Exists(path))
            {
                right = Path.Combine(tempRoot, "now", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(right));
                File.WriteAllBytes(right, new byte[0]);
                options |= __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
            }

            diffService.OpenComparisonWindow2(
                left,
                right,
                $"{fileName} (pending review)",
                path,
                "Before the agent",
                "Now",
                null,
                null,
                (uint)options);
        }

        #endregion
    }
}
