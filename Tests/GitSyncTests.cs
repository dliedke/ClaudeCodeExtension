/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the pre-prompt "git pull" helpers — porcelain status parsing, pull-result
 *          classification, and the conflict block prepended to the prompt.
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class GitSyncTests
    {
        #region ParseUnmergedPaths

        [TestMethod]
        public void ParseUnmergedPaths_PicksOnlyTheConflictedEntries()
        {
            // -z output: NUL-separated entries, no quoting, no trailing newline.
            string status = string.Join("\0", new[]
            {
                " M Controls/Edited.cs",
                "UU Controls/Conflicted.cs",
                "?? new-file.txt",
                "AA Models/BothAdded.cs",
                "M  Staged.cs",
                ""
            });

            List<string> paths = ClaudeCodeControl.ParseUnmergedPaths(status);

            CollectionAssert.AreEqual(
                new[] { "Controls/Conflicted.cs", "Models/BothAdded.cs" },
                paths);
        }

        [TestMethod]
        public void ParseUnmergedPaths_CoversEveryUnmergedStatusCode()
        {
            // The seven codes git-status(1) documents as unmerged. A renamed entry ("R ") is not one
            // of them and must not be picked up just because it starts with a letter pair.
            string status = string.Join("\0", new[]
            {
                "DD dd.cs", "AU au.cs", "UD ud.cs", "UA ua.cs", "DU du.cs", "AA aa.cs", "UU uu.cs",
                "R  renamed.cs"
            });

            List<string> paths = ClaudeCodeControl.ParseUnmergedPaths(status);

            Assert.AreEqual(7, paths.Count);
            CollectionAssert.DoesNotContain(paths, "renamed.cs");
        }

        [TestMethod]
        public void ParseUnmergedPaths_EmptyOrCleanTreeYieldsNothing()
        {
            Assert.AreEqual(0, ClaudeCodeControl.ParseUnmergedPaths(null).Count);
            Assert.AreEqual(0, ClaudeCodeControl.ParseUnmergedPaths(string.Empty).Count);
            Assert.AreEqual(0, ClaudeCodeControl.ParseUnmergedPaths("\0\0").Count);
        }

        #endregion

        #region ClassifyPullResult

        [TestMethod]
        public void ClassifyPullResult_ConflictsWinOverTheExitCode()
        {
            // A conflicted merge pull also exits non-zero. Leaving the merge in place for the agent to
            // finish is the point of the feature, so it must not be reported as a plain failure.
            var conflicts = new List<string> { "Controls/Conflicted.cs" };

            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                exitCode: 1,
                stdOut: "Auto-merging Controls/Conflicted.cs",
                stdErr: "CONFLICT (content): Merge conflict in Controls/Conflicted.cs",
                conflictedFiles: conflicts,
                headMoved: false,
                upstreamName: "origin/master",
                mergeInProgress: true,
                hasMergeAutoStash: true,
                hasLeftoverAutoStash: false);

            Assert.AreEqual(GitPullOutcomeKind.Conflicts, outcome.Kind);
            Assert.IsTrue(outcome.MergeInProgress);
            Assert.IsTrue(outcome.HasMergeAutoStash);
            CollectionAssert.AreEqual(conflicts, outcome.ConflictedFiles);
            StringAssert.Contains(outcome.Notice, "1 file");
        }

        [TestMethod]
        public void ClassifyPullResult_AutoStashRestoreConflictIsAConflictEvenThoughThePullSucceeded()
        {
            // Measured with git 2.55: a dirty tree plus a conflicting upstream change makes
            // `pull --no-rebase --autostash` exit 0 with UU paths, no MERGE_HEAD, and the autostash
            // moved into the ordinary stash list. Exit code 0 must not be read as "nothing to do".
            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                exitCode: 0,
                stdOut: "Updating abc1234..def5678\nFast-forward",
                stdErr: "CONFLICT (content): Merge conflict in f.txt\n"
                        + "error: could not restore untracked files from stash",
                conflictedFiles: new List<string> { "f.txt" },
                headMoved: true,
                upstreamName: "origin/master",
                mergeInProgress: false,
                hasMergeAutoStash: false,
                hasLeftoverAutoStash: true);

            Assert.AreEqual(GitPullOutcomeKind.Conflicts, outcome.Kind);
            Assert.IsFalse(outcome.MergeInProgress);
            Assert.IsTrue(outcome.HasLeftoverAutoStash);
            StringAssert.Contains(outcome.Notice, "uncommitted");
        }

        [TestMethod]
        public void ClassifyPullResult_LeftoverConflictsFromAnEarlierTurnSaySo()
        {
            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                1, string.Empty, string.Empty, new List<string> { "a.cs" }, false, null,
                mergeInProgress: true, hasMergeAutoStash: false, hasLeftoverAutoStash: false, pulled: false);

            Assert.AreEqual(GitPullOutcomeKind.Conflicts, outcome.Kind);
            StringAssert.Contains(outcome.Notice, "still open");
            Assert.IsFalse(outcome.PullExecuted,
                "No pull ran — the session's once-per-repository pull must stay available for the next prompt.");
        }

        [TestMethod]
        public void ClassifyPullResult_MarksThePullAsExecutedWheneverItActuallyRan()
        {
            // Drives the once-per-session guard: a pull that ran is not repeated on later prompts,
            // and a failed one is not repeated either (it would re-pay the full timeout every send).
            Assert.IsTrue(ClaudeCodeControl.ClassifyPullResult(
                0, "Already up to date.", "", new List<string>(), false, "origin/master", false, false, false).PullExecuted);
            Assert.IsTrue(ClaudeCodeControl.ClassifyPullResult(
                0, "Updating a..b", "", new List<string>(), true, "origin/master", false, false, false).PullExecuted);
            Assert.IsTrue(ClaudeCodeControl.ClassifyPullResult(
                128, "", "fatal: unable to access remote", new List<string>(), false, "origin/master", false, false, false).PullExecuted);
            Assert.IsTrue(ClaudeCodeControl.ClassifyPullResult(
                1, "", "", new List<string> { "a.cs" }, false, "origin/master", true, false, false).PullExecuted);
        }

        [TestMethod]
        public void ClassifyPullResult_UpToDateSaysNothing()
        {
            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                0, "Already up to date.", string.Empty, new List<string>(), false, "origin/master",
                false, false, false);

            Assert.AreEqual(GitPullOutcomeKind.UpToDate, outcome.Kind);
            Assert.IsNull(outcome.Notice, "An up-to-date pull happens on most prompts; a notice each time would be noise.");
        }

        [TestMethod]
        public void ClassifyPullResult_MovedHeadReportsTheUpstream()
        {
            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                0, "Updating abc1234..def5678", string.Empty, new List<string>(), true, "origin/master",
                false, false, false);

            Assert.AreEqual(GitPullOutcomeKind.Updated, outcome.Kind);
            StringAssert.Contains(outcome.Notice, "origin/master");
        }

        [TestMethod]
        public void ClassifyPullResult_FailureReportsGitsOwnFirstLineWithoutItsPrefix()
        {
            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                exitCode: 128,
                stdOut: string.Empty,
                stdErr: "fatal: could not read Username for 'https://github.com': terminal prompts disabled\n"
                        + "hint: see git-config(1)",
                conflictedFiles: new List<string>(),
                headMoved: false,
                upstreamName: "origin/master",
                mergeInProgress: false,
                hasMergeAutoStash: false,
                hasLeftoverAutoStash: false);

            Assert.AreEqual(GitPullOutcomeKind.Failed, outcome.Kind);
            StringAssert.StartsWith(outcome.Notice, "Auto-pull skipped: could not read Username");
            Assert.IsFalse(outcome.Notice.Contains("hint:"));
        }

        [TestMethod]
        public void ClassifyPullResult_FailureWithNoOutputStillExplainsItself()
        {
            GitPullOutcome outcome = ClaudeCodeControl.ClassifyPullResult(
                1, string.Empty, string.Empty, null, false, null, false, false, false);

            Assert.AreEqual(GitPullOutcomeKind.Failed, outcome.Kind);
            StringAssert.Contains(outcome.Notice, "did not complete");
        }

        #endregion

        #region StashListMentionsAutoStash

        [TestMethod]
        public void StashListMentionsAutoStash_SpotsTheEntryGitLeavesBehind()
        {
            Assert.IsTrue(ClaudeCodeControl.StashListMentionsAutoStash(
                "stash@{0}: autostash\n"));
            Assert.IsFalse(ClaudeCodeControl.StashListMentionsAutoStash(
                "stash@{0}: WIP on master: abc1234 some work\n"));
            Assert.IsFalse(ClaudeCodeControl.StashListMentionsAutoStash(string.Empty));
            Assert.IsFalse(ClaudeCodeControl.StashListMentionsAutoStash(null));
        }

        #endregion

        #region MentionsUnsupportedAutoStash

        [TestMethod]
        public void MentionsUnsupportedAutoStash_DetectsOldGitRejectingTheOption()
        {
            Assert.IsTrue(ClaudeCodeControl.MentionsUnsupportedAutoStash(
                "fatal: --[no-]autostash option is only valid with --rebase."));
            Assert.IsTrue(ClaudeCodeControl.MentionsUnsupportedAutoStash(
                "error: unknown option `autostash'"));
        }

        [TestMethod]
        public void MentionsUnsupportedAutoStash_IgnoresUnrelatedFailures()
        {
            // A conflict message must not send the pull down the "retry without --autostash" path.
            Assert.IsFalse(ClaudeCodeControl.MentionsUnsupportedAutoStash(
                "CONFLICT (content): Merge conflict in Controls/Conflicted.cs"));
            Assert.IsFalse(ClaudeCodeControl.MentionsUnsupportedAutoStash(null));
            Assert.IsFalse(ClaudeCodeControl.MentionsUnsupportedAutoStash("Applied autostash."));
        }

        #endregion

        #region BuildConflictPromptBlock

        private static GitPullOutcome MergeConflict(bool hasMergeAutoStash = false,
            params string[] files)
        {
            return new GitPullOutcome
            {
                Kind = GitPullOutcomeKind.Conflicts,
                ConflictedFiles = new List<string>(files.Length > 0 ? files : new[] { "Controls/A.cs" }),
                MergeInProgress = true,
                HasMergeAutoStash = hasMergeAutoStash
            };
        }

        private static GitPullOutcome AutoStashConflict(bool hasLeftoverAutoStash = true,
            params string[] files)
        {
            return new GitPullOutcome
            {
                Kind = GitPullOutcomeKind.Conflicts,
                ConflictedFiles = new List<string>(files.Length > 0 ? files : new[] { "Controls/A.cs" }),
                MergeInProgress = false,
                HasLeftoverAutoStash = hasLeftoverAutoStash
            };
        }

        [TestMethod]
        public void BuildConflictPromptBlock_MergeInProgressListsTheFilesAndForbidsDiscardingWork()
        {
            string block = ClaudeCodeControl.BuildConflictPromptBlock(
                MergeConflict(false, "Controls/A.cs", "Models/B.cs"), hasUserRequest: true);

            StringAssert.Contains(block, "git pull");
            StringAssert.Contains(block, "  - Controls/A.cs");
            StringAssert.Contains(block, "  - Models/B.cs");
            StringAssert.Contains(block, "complete the merge");
            StringAssert.Contains(block, "merge --abort");
            StringAssert.Contains(block, "continue with the request below");
        }

        [TestMethod]
        public void BuildConflictPromptBlock_AutoStashRestoreConflictNeverAsksForAMergeCommit()
        {
            // The pull already succeeded here — the user's uncommitted work is what failed to reapply.
            // Committing it as a "merge" would hand them a commit they never asked for.
            string block = ClaudeCodeControl.BuildConflictPromptBlock(
                AutoStashConflict(), hasUserRequest: true);

            StringAssert.Contains(block, "no merge in progress");
            StringAssert.Contains(block, "uncommitted working-tree changes");
            Assert.IsFalse(block.Contains("complete the merge"),
                "There is no merge to complete; that instruction would commit the user's work-in-progress.");
            StringAssert.Contains(block, "reset --hard");
        }

        [TestMethod]
        public void BuildConflictPromptBlock_MentionsTheAutoStashOnlyWhenThereIsOne()
        {
            string withStash = ClaudeCodeControl.BuildConflictPromptBlock(MergeConflict(true), true);
            string withoutStash = ClaudeCodeControl.BuildConflictPromptBlock(MergeConflict(false), true);

            StringAssert.Contains(withStash, "stash list");
            Assert.IsFalse(withoutStash.Contains("stash list"));
        }

        [TestMethod]
        public void BuildConflictPromptBlock_TellsTheAgentWhenToDropTheLeftoverStash()
        {
            string withStash = ClaudeCodeControl.BuildConflictPromptBlock(AutoStashConflict(true), true);
            string withoutStash = ClaudeCodeControl.BuildConflictPromptBlock(AutoStashConflict(false), true);

            StringAssert.Contains(withStash, "stash drop");
            Assert.IsFalse(withoutStash.Contains("stash drop"));
        }

        [TestMethod]
        public void BuildConflictPromptBlock_OmitsTheHandoffWhenThePromptIsOnlyTheConflict()
        {
            // Attachment-free, text-free sends exist (an empty prompt is rejected earlier), but a
            // conflict block with nothing after it must not promise a request that isn't there.
            string block = ClaudeCodeControl.BuildConflictPromptBlock(MergeConflict(), hasUserRequest: false);

            Assert.IsFalse(block.Contains("continue with the request below"));
            StringAssert.Contains(block, "Controls/A.cs");
        }

        [TestMethod]
        public void BuildConflictPromptBlock_NullOutcomeAddsNothing()
        {
            Assert.AreEqual(string.Empty, ClaudeCodeControl.BuildConflictPromptBlock(null, true));
        }

        #endregion
    }
}
