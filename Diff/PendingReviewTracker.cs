/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Pending review" of agent changes (issue #183) - one baseline per file the agent touched,
 *          accumulated across turns and chat tabs until the user keeps or undoes it. Pure logic: all
 *          file-system and git access goes through IPendingReviewSource so it is unit-testable.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Raw content of a file at one moment, or the fact that it did not exist. Bytes, not text, so
    /// Undo restores the file exactly (encoding, BOM, line endings).
    /// </summary>
    public sealed class FileSnapshot
    {
        /// <summary>The file did not exist.</summary>
        public static readonly FileSnapshot Missing = new FileSnapshot(null);

        public FileSnapshot(byte[] content)
        {
            Content = content;
        }

        /// <summary>File bytes; null when the file did not exist.</summary>
        public byte[] Content { get; }

        public bool Exists => Content != null;

        public bool SameAs(FileSnapshot other)
        {
            if (other == null) return false;
            if (Content == null || other.Content == null) return Content == null && other.Content == null;
            if (Content.Length != other.Content.Length) return false;
            for (int i = 0; i < Content.Length; i++)
            {
                if (Content[i] != other.Content[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Decodes the bytes the same way the diff viewer reads files from disk (UTF-8, BOM detected),
        /// so a baseline and the current file compare as text on equal terms. Null when missing.
        /// </summary>
        public string DecodeText()
        {
            if (Content == null) return null;
            using (var reader = new StreamReader(new MemoryStream(Content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                return reader.ReadToEnd();
            }
        }
    }

    /// <summary>
    /// File-system and git access needed by <see cref="PendingReviewTracker"/>. Every method returns
    /// null when the information can't be obtained (git failed, file locked, file too large), and the
    /// tracker then leaves the path alone rather than guessing.
    /// </summary>
    public interface IPendingReviewSource
    {
        /// <summary>Full path of the repository root.</summary>
        string RepositoryRoot { get; }

        /// <summary>Whether a path is a source file the diff viewer shows at all.</summary>
        bool IsTrackable(string fullPath);

        /// <summary>
        /// Resolves the commit HEAD points at. Returns false when git can't be queried; returns true with
        /// a null <paramref name="commit"/> for a repository that has no commits yet.
        /// </summary>
        bool TryGetHeadCommit(out string commit);

        /// <summary>Full paths with uncommitted changes, untracked files included. Null on failure.</summary>
        IList<string> ListDirtyPaths();

        /// <summary>
        /// Full paths whose working-tree state may differ from <paramref name="commit"/> (tracked changes
        /// and untracked files), mapped to whether the path exists in that commit. Null on failure.
        /// </summary>
        IDictionary<string, bool> ListPathsChangedSince(string commit);

        /// <summary>Current content, <see cref="FileSnapshot.Missing"/> if absent, null if unreadable.</summary>
        FileSnapshot ReadWorkingFile(string fullPath);

        /// <summary>Content of the path in <paramref name="commit"/> as it would be checked out; null on failure.</summary>
        FileSnapshot ReadCommittedFile(string commit, string fullPath);
    }

    /// <summary>
    /// Tracks which files the agent changed that the user has not reviewed yet.
    /// <para>
    /// While at least one agent turn is open (any chat tab, or the terminal) the tracker holds a
    /// reference snapshot of the working tree, taken when the latest turn started: the content of every
    /// dirty file, plus the HEAD commit for everything else (read lazily, so nothing is copied up front).
    /// Reconciling compares the working tree with that reference; a file that differs and isn't pending
    /// yet gets the reference content as its baseline. A file that already has a baseline keeps it, so
    /// later turns accumulate on the same diff until the user keeps or undoes it.
    /// </para>
    /// <para>
    /// Each new turn first reconciles against the previous reference and then takes a fresh one. That
    /// keeps baselines correct when turns overlap: anything changed before the new snapshot is already
    /// pending with its older baseline, and anything changed after it was untouched at snapshot time.
    /// </para>
    /// Thread-safe; every public method may run git and file IO, so callers keep it off the UI thread.
    /// </summary>
    public sealed class PendingReviewTracker
    {
        /// <summary>Most dirty files whose content a single snapshot reads; the rest are left unreviewable.</summary>
        internal const int MaxSnapshotFiles = 2000;

        private readonly object _lock = new object();
        private readonly IPendingReviewSource _source;
        private readonly Dictionary<string, FileSnapshot> _baselines =
            new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<object> _openTurns = new HashSet<object>();
        private TurnReference _reference;

        /// <summary>
        /// The working tree as it was when the latest open turn started. <see cref="Files"/> holds the
        /// paths whose state is known explicitly (dirty at snapshot time, or kept/undone since); a null
        /// value means "unknown", and such a path is never made pending. Every other path is taken to
        /// match <see cref="Commit"/>.
        /// </summary>
        private sealed class TurnReference
        {
            public string Commit;
            public readonly Dictionary<string, FileSnapshot> Files =
                new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, FileSnapshot> CommittedCache =
                new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        public PendingReviewTracker(IPendingReviewSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public string RepositoryRoot => _source.RepositoryRoot;

        /// <summary>True while at least one agent turn is open.</summary>
        public bool IsTurnOpen
        {
            get { lock (_lock) { return _openTurns.Count > 0; } }
        }

        /// <summary>
        /// Starts tracking a turn identified by <paramref name="turnKey"/> (one key per chat session, one
        /// for the terminal). Must complete before the prompt reaches the agent. Starting a key that is
        /// already open is fine: it simply re-snapshots.
        /// </summary>
        public void BeginTurn(object turnKey)
        {
            if (turnKey == null) throw new ArgumentNullException(nameof(turnKey));

            lock (_lock)
            {
                ReconcileLocked();

                TurnReference fresh = TakeReferenceLocked();
                if (fresh != null)
                {
                    _reference = fresh;
                }

                _openTurns.Add(turnKey);
            }
        }

        /// <summary>Ends a turn: folds its changes into the pending set and, if it was the last open turn, drops the reference.</summary>
        public void EndTurn(object turnKey)
        {
            if (turnKey == null) return;

            lock (_lock)
            {
                if (!_openTurns.Remove(turnKey)) return;
                CloseTurnsLocked();
            }
        }

        /// <summary>
        /// Ends every open turn whose key is no longer alive — a chat tab closed or its agent relaunched
        /// mid-turn never reports the end of that turn.
        /// </summary>
        public void EndTurnsWhere(Func<object, bool> isDead)
        {
            if (isDead == null) return;

            lock (_lock)
            {
                int removed = _openTurns.RemoveWhere(k => isDead(k));
                if (removed > 0)
                {
                    CloseTurnsLocked();
                }
            }
        }

        /// <summary>
        /// Brings the pending set up to date: adds files changed since the reference (while a turn is
        /// open) and drops pending files that are back to their baseline.
        /// </summary>
        public void Reconcile()
        {
            lock (_lock)
            {
                ReconcileLocked();
            }
        }

        /// <summary>Copy of the pending baselines, keyed by full path.</summary>
        public Dictionary<string, FileSnapshot> GetBaselines()
        {
            lock (_lock)
            {
                return new Dictionary<string, FileSnapshot>(_baselines, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>The baseline of a pending file, or null when the file is not pending.</summary>
        public FileSnapshot GetBaseline(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;

            lock (_lock)
            {
                return _baselines.TryGetValue(fullPath, out FileSnapshot baseline) ? baseline : null;
            }
        }

        /// <summary>
        /// Accepts the agent's changes to a file: its current content becomes the new starting point, so
        /// it leaves the pending list and only comes back if the agent changes it again.
        /// </summary>
        public void Keep(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            lock (_lock)
            {
                _baselines.Remove(fullPath);

                // A turn still running must compare against the kept content from now on, not against
                // the older reference — otherwise its next reconcile would put the file straight back.
                if (_reference != null)
                {
                    _reference.Files[fullPath] = _source.ReadWorkingFile(fullPath);
                }
            }
        }

        /// <summary>
        /// Records that the caller restored a pending file to <paramref name="restored"/> (its baseline),
        /// removing it from the pending list.
        /// </summary>
        public void MarkUndone(string fullPath, FileSnapshot restored)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            lock (_lock)
            {
                _baselines.Remove(fullPath);

                if (_reference != null)
                {
                    _reference.Files[fullPath] = restored;
                }
            }
        }

        /// <summary>Forgets everything: pending baselines, open turns and the reference.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _baselines.Clear();
                _openTurns.Clear();
                _reference = null;
            }
        }

        private void CloseTurnsLocked()
        {
            ReconcileLocked();

            if (_openTurns.Count == 0)
            {
                _reference = null;
            }
        }

        private TurnReference TakeReferenceLocked()
        {
            if (!_source.TryGetHeadCommit(out string commit)) return null;

            IList<string> dirty = _source.ListDirtyPaths();
            if (dirty == null) return null;

            var reference = new TurnReference { Commit = commit };
            int read = 0;

            foreach (string path in dirty)
            {
                if (string.IsNullOrEmpty(path) || reference.Files.ContainsKey(path) || !_source.IsTrackable(path))
                {
                    continue;
                }

                // Past the cap the content is unknown, which keeps the file out of the pending list
                // instead of reporting it as changed against HEAD.
                reference.Files[path] = read < MaxSnapshotFiles ? _source.ReadWorkingFile(path) : null;
                read++;
            }

            return reference;
        }

        private void ReconcileLocked()
        {
            DropResolvedLocked();

            TurnReference reference = _reference;
            if (reference == null) return;

            // Candidates: whatever git sees as different from the snapshot commit, plus every path whose
            // reference state is explicit (a file dirty before the turn is not "changed" to git once the
            // agent edits it again, but it may still differ from what it was at snapshot time).
            var candidates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (reference.Commit != null)
            {
                IDictionary<string, bool> changed = _source.ListPathsChangedSince(reference.Commit);
                if (changed != null)
                {
                    foreach (KeyValuePair<string, bool> pair in changed)
                    {
                        candidates[pair.Key] = pair.Value;
                    }
                }
            }
            else
            {
                IList<string> dirty = _source.ListDirtyPaths();
                if (dirty != null)
                {
                    foreach (string path in dirty)
                    {
                        candidates[path] = false;
                    }
                }
            }

            foreach (string path in reference.Files.Keys)
            {
                if (!candidates.ContainsKey(path))
                {
                    candidates[path] = false;
                }
            }

            foreach (KeyValuePair<string, bool> candidate in candidates)
            {
                string path = candidate.Key;
                if (string.IsNullOrEmpty(path) || _baselines.ContainsKey(path) || !_source.IsTrackable(path))
                {
                    continue;
                }

                FileSnapshot before = ResolveReferenceLocked(reference, path, candidate.Value);
                if (before == null) continue;

                FileSnapshot now = _source.ReadWorkingFile(path);
                if (now == null) continue;

                if (!now.SameAs(before))
                {
                    _baselines[path] = before;
                }
            }
        }

        private FileSnapshot ResolveReferenceLocked(TurnReference reference, string path, bool existsInCommit)
        {
            if (reference.Files.TryGetValue(path, out FileSnapshot known))
            {
                return known;
            }

            if (reference.Commit == null || !existsInCommit)
            {
                return FileSnapshot.Missing;
            }

            if (!reference.CommittedCache.TryGetValue(path, out FileSnapshot committed))
            {
                committed = _source.ReadCommittedFile(reference.Commit, path);
                reference.CommittedCache[path] = committed;
            }

            return committed;
        }

        /// <summary>Drops pending files whose content is back to the baseline (reverted by hand or by the agent).</summary>
        private void DropResolvedLocked()
        {
            if (_baselines.Count == 0) return;

            foreach (string path in _baselines.Keys.ToList())
            {
                FileSnapshot now = _source.ReadWorkingFile(path);
                if (now != null && now.SameAs(_baselines[path]))
                {
                    _baselines.Remove(path);
                }
            }
        }
    }
}
