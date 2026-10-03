/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the "Track agent changes for review" bookkeeping: which files become pending, which
 *          baseline each keeps across turns, Keep/Undo, overlapping turns, and the git output parsers.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class PendingReviewTrackerTests
    {
        private const string Root = @"C:\repo";
        private static readonly string A = Root + @"\a.cs";
        private static readonly string B = Root + @"\b.cs";
        private static readonly string C = Root + @"\c.cs";

        /// <summary>In-memory repository: named commits, a HEAD pointer and a working tree.</summary>
        private sealed class FakeRepository : IPendingReviewSource
        {
            public readonly Dictionary<string, Dictionary<string, string>> Commits =
                new Dictionary<string, Dictionary<string, string>>();
            public readonly Dictionary<string, string> Working =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Head;

            public FakeRepository Commit(string name, params string[] pathContentPairs)
            {
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pathContentPairs.Length; i += 2)
                {
                    files[pathContentPairs[i]] = pathContentPairs[i + 1];
                    Working[pathContentPairs[i]] = pathContentPairs[i + 1];
                }
                Commits[name] = files;
                Head = name;
                return this;
            }

            public string RepositoryRoot => Root;

            public bool IsTrackable(string fullPath) => !fullPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

            public bool TryGetHeadCommit(out string commit)
            {
                commit = Head;
                return true;
            }

            public IList<string> ListDirtyPaths()
            {
                return Head == null ? Working.Keys.ToList() : ListPathsChangedSince(Head).Keys.ToList();
            }

            public IDictionary<string, bool> ListPathsChangedSince(string commit)
            {
                Dictionary<string, string> committed = Commits[commit];
                var changed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                foreach (string path in committed.Keys.Union(Working.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    committed.TryGetValue(path, out string before);
                    Working.TryGetValue(path, out string now);
                    if (before != now)
                    {
                        changed[path] = before != null;
                    }
                }
                return changed;
            }

            public FileSnapshot ReadWorkingFile(string fullPath)
            {
                return Working.TryGetValue(fullPath, out string text) ? Snap(text) : FileSnapshot.Missing;
            }

            public FileSnapshot ReadCommittedFile(string commit, string fullPath)
            {
                return Commits[commit].TryGetValue(fullPath, out string text) ? Snap(text) : FileSnapshot.Missing;
            }
        }

        private static FileSnapshot Snap(string text) => new FileSnapshot(Encoding.UTF8.GetBytes(text));

        private static string Text(FileSnapshot snapshot) => snapshot.Exists ? snapshot.DecodeText() : null;

        private static FakeRepository NewRepo() => new FakeRepository().Commit("c1", A, "a1", B, "b1");

        [TestMethod]
        public void EditDuringTurn_BecomesPendingWithCommittedBaseline()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.EndTurn("t");

            var baselines = tracker.GetBaselines();
            Assert.AreEqual(1, baselines.Count);
            Assert.AreEqual("a1", Text(baselines[A]));
            Assert.IsFalse(tracker.IsTurnOpen);
        }

        [TestMethod]
        public void FileDirtyBeforeTurn_UsesUserVersionAsBaseline_AndUntouchedDirtyFileIsNotPending()
        {
            var repo = NewRepo();
            repo.Working[A] = "a-user";
            repo.Working[B] = "b-user";
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a-agent";
            tracker.EndTurn("t");

            var baselines = tracker.GetBaselines();
            Assert.AreEqual(1, baselines.Count);
            Assert.AreEqual("a-user", Text(baselines[A]));
        }

        [TestMethod]
        public void SecondTurn_KeepsOriginalBaseline()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.EndTurn("t");

            tracker.BeginTurn("t");
            repo.Working[A] = "a3";
            repo.Working[B] = "b2";
            tracker.EndTurn("t");

            var baselines = tracker.GetBaselines();
            Assert.AreEqual(2, baselines.Count);
            Assert.AreEqual("a1", Text(baselines[A]));
            Assert.AreEqual("b1", Text(baselines[B]));
        }

        [TestMethod]
        public void EditsOutsideAnyTurn_AreNotTracked()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            tracker.EndTurn("t");
            repo.Working[A] = "a-user";
            tracker.Reconcile();

            Assert.AreEqual(0, tracker.GetBaselines().Count);
        }

        [TestMethod]
        public void CreatedAndDeletedFiles_HaveMissingAndCommittedBaselines()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[C] = "new";
            repo.Working.Remove(B);
            tracker.EndTurn("t");

            var baselines = tracker.GetBaselines();
            Assert.AreEqual(2, baselines.Count);
            Assert.IsFalse(baselines[C].Exists);
            Assert.AreEqual("b1", Text(baselines[B]));
        }

        [TestMethod]
        public void UntrackableFiles_AreIgnored()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[Root + @"\data.bin"] = "x";
            tracker.EndTurn("t");

            Assert.AreEqual(0, tracker.GetBaselines().Count);
        }

        [TestMethod]
        public void RevertedByHand_DropsOutOnReconcile()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.EndTurn("t");

            repo.Working[A] = "a1";
            tracker.Reconcile();

            Assert.IsNull(tracker.GetBaseline(A));
        }

        [TestMethod]
        public void Keep_RemovesFile_AndRunningTurnComparesAgainstKeptContent()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.Reconcile();
            Assert.IsNotNull(tracker.GetBaseline(A));

            tracker.Keep(A);
            tracker.Reconcile();
            Assert.IsNull(tracker.GetBaseline(A));

            // The same turn edits the file again: the kept content is the new baseline
            repo.Working[A] = "a3";
            tracker.EndTurn("t");
            Assert.AreEqual("a2", Text(tracker.GetBaseline(A)));
        }

        [TestMethod]
        public void Keep_AfterTurnEnded_FileOnlyReturnsWhenChangedAgain()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.EndTurn("t");
            tracker.Keep(A);

            tracker.BeginTurn("t");
            tracker.EndTurn("t");
            Assert.AreEqual(0, tracker.GetBaselines().Count);

            tracker.BeginTurn("t");
            repo.Working[A] = "a3";
            tracker.EndTurn("t");
            Assert.AreEqual("a2", Text(tracker.GetBaseline(A)));
        }

        [TestMethod]
        public void MarkUndone_RemovesFile_AndRunningTurnComparesAgainstRestoredContent()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.Reconcile();

            FileSnapshot baseline = tracker.GetBaseline(A);
            repo.Working[A] = Text(baseline);
            tracker.MarkUndone(A, baseline);
            tracker.Reconcile();
            Assert.IsNull(tracker.GetBaseline(A));

            repo.Working[A] = "a4";
            tracker.EndTurn("t");
            Assert.AreEqual("a1", Text(tracker.GetBaseline(A)));
        }

        [TestMethod]
        public void OverlappingTurns_KeepEarliestBaselines()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("chat1");
            repo.Working[A] = "a2";

            tracker.BeginTurn("chat2");
            repo.Working[A] = "a3";
            repo.Working[B] = "b2";

            tracker.EndTurn("chat1");
            Assert.IsTrue(tracker.IsTurnOpen);

            repo.Working[C] = "c-new";
            tracker.EndTurn("chat2");

            var baselines = tracker.GetBaselines();
            Assert.AreEqual(3, baselines.Count);
            Assert.AreEqual("a1", Text(baselines[A]));
            Assert.AreEqual("b1", Text(baselines[B]));
            Assert.IsFalse(baselines[C].Exists);
            Assert.IsFalse(tracker.IsTurnOpen);
        }

        [TestMethod]
        public void NewCommitDuringTurns_UsesDirtyContentFromSnapshot()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.EndTurn("t");

            // The user commits the agent's work, then the agent edits again
            repo.Commit("c2", A, "a2", B, "b1");
            tracker.Keep(A);

            tracker.BeginTurn("t");
            repo.Working[A] = "a3";
            tracker.EndTurn("t");

            Assert.AreEqual("a2", Text(tracker.GetBaseline(A)));
        }

        [TestMethod]
        public void UnbornHead_TreatsEveryFileAsCreated()
        {
            var repo = new FakeRepository();
            repo.Working[A] = "a-user";
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a-agent";
            repo.Working[B] = "b-new";
            tracker.EndTurn("t");

            var baselines = tracker.GetBaselines();
            Assert.AreEqual(2, baselines.Count);
            Assert.AreEqual("a-user", Text(baselines[A]));
            Assert.IsFalse(baselines[B].Exists);
        }

        [TestMethod]
        public void EndTurnsWhere_ClosesDeadTurnsAndFoldsTheirChanges()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("dead");
            tracker.BeginTurn("alive");
            repo.Working[A] = "a2";

            tracker.EndTurnsWhere(key => (string)key == "dead");
            Assert.IsTrue(tracker.IsTurnOpen);
            Assert.AreEqual("a1", Text(tracker.GetBaseline(A)));

            tracker.EndTurnsWhere(key => (string)key == "alive");
            Assert.IsFalse(tracker.IsTurnOpen);
        }

        [TestMethod]
        public void EndTurn_UnknownKeyIsIgnored()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            tracker.EndTurn("other");

            Assert.IsTrue(tracker.IsTurnOpen);
        }

        [TestMethod]
        public void Clear_ForgetsBaselinesAndTurns()
        {
            var repo = NewRepo();
            var tracker = new PendingReviewTracker(repo);

            tracker.BeginTurn("t");
            repo.Working[A] = "a2";
            tracker.Reconcile();
            tracker.Clear();

            Assert.AreEqual(0, tracker.GetBaselines().Count);
            Assert.IsFalse(tracker.IsTurnOpen);
        }

        [TestMethod]
        public void FileSnapshot_SameAs_ComparesBytesAndExistence()
        {
            Assert.IsTrue(Snap("x").SameAs(Snap("x")));
            Assert.IsFalse(Snap("x").SameAs(Snap("y")));
            Assert.IsFalse(Snap("").SameAs(FileSnapshot.Missing));
            Assert.IsTrue(FileSnapshot.Missing.SameAs(FileSnapshot.Missing));
        }

        [TestMethod]
        public void FileSnapshot_DecodeText_StripsUtf8Bom()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };
            Assert.AreEqual("hi", new FileSnapshot(bytes).DecodeText());
        }

        [TestMethod]
        public void ParseStatusPaths_ReturnsBothSidesOfRenames()
        {
            var paths = GitPendingReviewSource.ParseStatusPaths(" M src/a.cs\0R  new.cs\0old.cs\0?? notes.txt\0").ToList();

            CollectionAssert.AreEqual(new[] { "src/a.cs", "new.cs", "old.cs", "notes.txt" }, paths);
        }

        [TestMethod]
        public void ParseNameStatus_FlagsAdditionsAsNotInCommit()
        {
            var entries = GitPendingReviewSource.ParseNameStatus("M\0a.cs\0A\0b.cs\0D\0c.cs\0")
                .ToDictionary(p => p.Key, p => p.Value);

            Assert.AreEqual(3, entries.Count);
            Assert.IsTrue(entries["a.cs"]);
            Assert.IsFalse(entries["b.cs"]);
            Assert.IsTrue(entries["c.cs"]);
        }

        [TestMethod]
        public void QuoteArgument_FollowsWindowsCommandLineRules()
        {
            Assert.AreEqual("\"HEAD:src/a b.cs\"", GitPendingReviewSource.QuoteArgument("HEAD:src/a b.cs"));
            Assert.AreEqual("\"say \\\"hi\\\"\"", GitPendingReviewSource.QuoteArgument("say \"hi\""));
            Assert.AreEqual("\"dir\\\\\"", GitPendingReviewSource.QuoteArgument("dir\\"));
            Assert.AreEqual("\"a\\b\"", GitPendingReviewSource.QuoteArgument("a\\b"));
        }
    }
}
