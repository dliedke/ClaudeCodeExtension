/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Pull from git before sending a prompt" feature. When the setting is enabled (default),
 *          a user-initiated prompt first runs `git pull` in the workspace repository so the agent
 *          never edits a file that is already out of date on the remote. A pull that ends in
 *          conflicts is not rolled back: the conflicted files are described in a block prepended to
 *          the prompt, so the agent resolves them as the first part of the turn.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    /// <summary>
    /// What an auto-pull attempt did. Drives both the notice shown to the user and whether a
    /// conflict-resolution block is prepended to the outgoing prompt.
    /// </summary>
    public enum GitPullOutcomeKind
    {
        /// <summary>Nothing was attempted (setting off, no repo, no upstream, agent busy, git missing).</summary>
        Skipped,

        /// <summary>The pull ran and the branch was already current.</summary>
        UpToDate,

        /// <summary>The pull ran and brought new commits into the working tree.</summary>
        Updated,

        /// <summary>The pull left conflicted files in the working tree.</summary>
        Conflicts,

        /// <summary>The pull could not run to completion (offline, auth, timeout, dirty tree git refused).</summary>
        Failed
    }

    /// <summary>
    /// Result of one auto-pull attempt. Pure data so the classification logic stays unit-testable.
    /// </summary>
    public class GitPullOutcome
    {
        public GitPullOutcomeKind Kind { get; set; } = GitPullOutcomeKind.Skipped;

        /// <summary>Short, user-facing sentence for the chat notice. May be null when nothing is worth saying.</summary>
        public string Notice { get; set; }

        /// <summary>Repository-relative paths left in an unmerged state. Empty unless <see cref="Kind"/> is Conflicts.</summary>
        public List<string> ConflictedFiles { get; set; } = new List<string>();

        /// <summary>
        /// True when MERGE_HEAD exists, i.e. the conflict is a real unfinished merge that has to be
        /// resolved and committed. False means the merge (or fast-forward) already succeeded and the
        /// conflict came from restoring the autostash — there is nothing to commit in that case.
        /// </summary>
        public bool MergeInProgress { get; set; }

        /// <summary>True when the uncommitted work is parked in MERGE_AUTOSTASH, tied to the merge in progress.</summary>
        public bool HasMergeAutoStash { get; set; }

        /// <summary>
        /// True when the `git pull` command actually ran, whatever its result. False for a skip and
        /// for conflicts that were already in the tree before this prompt.
        /// </summary>
        public bool PullExecuted { get; set; }

        /// <summary>
        /// True when git gave up reapplying the autostash and left it as an ordinary `git stash list`
        /// entry. The user's uncommitted work then only exists there, so it must not be dropped blindly.
        /// </summary>
        public bool HasLeftoverAutoStash { get; set; }
    }

    public partial class ClaudeCodeControl
    {
        #region Constants

        /// <summary>Preflight queries (upstream lookup, status) are local and must be quick.</summary>
        private const int GitSyncPreflightTimeoutMs = 8000;

        /// <summary>
        /// The pull itself talks to the network. Long enough for a real fetch, short enough that an
        /// unreachable remote delays a prompt by seconds rather than blocking it indefinitely.
        /// </summary>
        private const int GitSyncPullTimeoutMs = 20000;

        #endregion

        #region Fields

        /// <summary>
        /// The repository the pre-prompt pull has already run for. The network pull happens once per
        /// repository per session, not on every send: by the second prompt the working tree normally
        /// holds the agent's own uncommitted edits, and pulling on top of those is exactly what
        /// produces autostash-restore conflicts — paid for on every prompt, in latency, for code that
        /// usually has not moved. Keyed by root rather than a plain bool so opening another solution
        /// re-arms it.
        /// </summary>
        private string _autoPulledRepositoryRoot;

        #endregion

        #region Public entry point

        /// <summary>
        /// Runs the pre-prompt `git pull` when the setting allows it. Safe to call on the UI thread:
        /// every git process runs on a background thread.
        /// </summary>
        /// <returns>
        /// The outcome. Never null; <see cref="GitPullOutcomeKind.Skipped"/> when nothing ran.
        /// </returns>
        private async Task<GitPullOutcome> TryAutoPullBeforePromptAsync()
        {
            var skipped = new GitPullOutcome();

            try
            {
                if (_settings == null || !_settings.AutoGitPullBeforePrompt)
                    return LogSkip(skipped, "setting is off");

                // Do NOT rely on _gitRepositoryRoot alone: that field belongs to diff tracking and is
                // only ever assigned by EnsureDiffTrackingStartedAsync, which on the send path runs
                // *after* this call. On the first prompt of a session it is still null, which silently
                // skipped every pull. Resolve the repository here when it isn't known yet.
                string repoRoot = _gitRepositoryRoot;
                if (string.IsNullOrEmpty(repoRoot))
                {
                    string workspaceDir = await GetWorkspaceDirectoryAsync();
                    repoRoot = FindGitRepositoryRoot(workspaceDir);
                }

                if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                    return LogSkip(skipped, "no git repository for the current workspace");

                // Pulling files out from under a turn that is already running would change the code
                // the agent is in the middle of editing. Follow-ups sent while the agent is busy
                // (Codex, Devin) therefore skip the pull; the next idle prompt picks it up.
                if (IsNativeAgentBusy())
                    return LogSkip(skipped, "the agent is still working on the previous message");

                bool alreadyPulled = !string.IsNullOrEmpty(_autoPulledRepositoryRoot)
                    && string.Equals(_autoPulledRepositoryRoot, repoRoot, StringComparison.OrdinalIgnoreCase);

                // The network pull can take seconds (up to the 20 s timeout offline), during which the
                // prompt just sits there. Say what is happening before it starts. Only the first prompt
                // per repository actually pulls, so only that one gets the line.
                ChatTranscriptView progress = null;
                if (!alreadyPulled)
                    progress = await ShowAutoPullProgressAsync();

                GitPullOutcome outcome;
                try
                {
                    outcome = await Task.Run(() => RunAutoPull(repoRoot, !alreadyPulled)).ConfigureAwait(false)
                              ?? skipped;
                }
                finally
                {
                    await HideAutoPullProgressAsync(progress);
                }

                // Remembered even when the pull failed: an offline or unauthenticated remote would
                // otherwise re-pay the full 20 s timeout on every prompt for the rest of the session.
                if (outcome.PullExecuted)
                    _autoPulledRepositoryRoot = repoRoot;

                return outcome;
            }
            catch (Exception ex)
            {
                // Auto-pull is a convenience — it must never be the reason a prompt fails to send.
                Debug.WriteLine($"Auto git pull failed: {ex.Message}");
                return skipped;
            }
        }

        /// <summary>
        /// Puts "Pulling latest changes from git…" on the chat's status line, with the same spinner and
        /// running clock a turn uses, so a slow fetch reads as progress rather than a frozen prompt.
        /// Native mode only — the terminal has no such line. Returns the transcript to clear afterwards,
        /// or null when nothing was shown.
        /// </summary>
        private async Task<ChatTranscriptView> ShowAutoPullProgressAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!IsNativeModeActive)
                    return null;

                ChatTranscriptView transcript = GetActiveSession()?.ChatTranscript ?? ChatTranscript;
                if (transcript == null)
                    return null;

                transcript.BeginActivity();
                transcript.SetActivityLabel("Pulling latest changes from git…");
                return transcript;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not show auto-pull progress: {ex.Message}");
                return null;
            }
        }

        /// <summary>Clears the status line set by <see cref="ShowAutoPullProgressAsync"/>. The prompt's own turn starts its own line right after.</summary>
        private async Task HideAutoPullProgressAsync(ChatTranscriptView transcript)
        {
            if (transcript == null)
                return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                transcript.SetStatus(string.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not clear auto-pull progress: {ex.Message}");
            }
        }

        /// <summary>
        /// True when a native session currently has a turn in flight. Terminal mode has no equivalent
        /// signal, and its sends are serialized by the prompt-submission guard, so it reports false.
        /// </summary>
        private bool IsNativeAgentBusy()
        {
            try
            {
                if (!IsNativeModeActive)
                    return false;

                if (_agentSession != null && _agentSession.IsBusy)
                    return true;

                var active = GetActiveSession();
                return active?.AgentSession != null && active.AgentSession.IsBusy;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Records why no pull happened. Every skip is silent by design, so the debug log is the only
        /// place that explains a prompt going out without a pull.
        /// </summary>
        private static GitPullOutcome LogSkip(GitPullOutcome skipped, string reason)
        {
            Debug.WriteLine($"Auto git pull skipped: {reason}.");
            return skipped;
        }

        /// <summary>
        /// Surfaces what the pull did. Native mode gets a transcript notice; terminal mode has no
        /// place to put one, so there it only reaches the debug log. An up-to-date pull says nothing:
        /// that is the common case and a notice on every prompt would be noise.
        /// </summary>
        private void ReportAutoPullOutcome(GitPullOutcome outcome)
        {
            if (outcome == null || outcome.Kind == GitPullOutcomeKind.Skipped)
                return;

            Debug.WriteLine($"Auto git pull: {outcome.Kind} {outcome.Notice}");

            if (string.IsNullOrWhiteSpace(outcome.Notice) || !IsNativeModeActive)
                return;

            try
            {
                AddNativeMessage(ChatMessageKind.Notice, outcome.Notice);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show auto-pull notice: {ex.Message}");
            }
        }

        #endregion

        #region Git execution

        /// <summary>
        /// Performs the preflight checks and the pull itself. Runs entirely off the UI thread.
        /// </summary>
        /// <param name="allowNetworkPull">
        /// False once this repository has already been pulled in this session; the local checks still
        /// run, the `git pull` does not.
        /// </param>
        private GitPullOutcome RunAutoPull(string repoRoot, bool allowNetworkPull)
        {
            var skipped = new GitPullOutcome();

            if (string.IsNullOrEmpty(ResolveGitPath()))
                return LogSkip(skipped, "git executable not found");

            // Conflicts left over from an earlier turn: never start another pull on top of them. Hand
            // them to the agent again so it finishes what is already open. This check stays on every
            // prompt even after the session's pull is done — it is a local `git status`, and a prompt
            // going out over an unresolved tree is worth interrupting regardless of who left it there.
            List<string> pendingConflicts = GetUnmergedPaths(repoRoot);
            if (pendingConflicts.Count > 0)
                return BuildConflictOutcome(repoRoot, pendingConflicts, pulled: false);

            if (!allowNetworkPull)
                return LogSkip(skipped, "this workspace was already pulled once in this session");

            // No tracking branch (detached HEAD, a local-only branch, a repo with no remote) means
            // there is nothing to pull from and `git pull` would only print an error.
            GitCommandResult upstream = RunGitDetailed(repoRoot,
                "rev-parse --abbrev-ref --symbolic-full-name @{u}", GitSyncPreflightTimeoutMs);
            if (upstream == null || upstream.ExitCode != 0 || string.IsNullOrWhiteSpace(upstream.StdOut))
                return LogSkip(skipped, "the current branch has no upstream to pull from");

            string upstreamName = upstream.StdOut.Trim();
            string headBefore = GetHeadCommit(repoRoot);

            // --no-rebase keeps a failed pull as a conflicted *merge*: conflict markers in the working
            // tree, `git status` listing both sides, and `git merge --abort` as the way out. That is
            // the state an agent can actually resolve — a half-finished rebase is not.
            // --autostash lets the pull run with uncommitted work in the tree instead of refusing.
            GitCommandResult pull = RunGitDetailed(repoRoot, "pull --no-rebase --autostash", GitSyncPullTimeoutMs);

            // --autostash for a merge pull needs git 2.27+. Older builds reject the option outright,
            // in which case retry the plain pull: it still succeeds whenever the tree is clean.
            if (pull != null && pull.ExitCode != 0 && MentionsUnsupportedAutoStash(pull.StdErr))
                pull = RunGitDetailed(repoRoot, "pull --no-rebase", GitSyncPullTimeoutMs);

            if (pull == null)
                return LogSkip(skipped, "git pull did not return within the timeout");

            // Measured with git 2.55: a pull whose *autostash restore* conflicts still exits 0, so the
            // unmerged paths — not the exit code — are what says the tree needs resolving.
            List<string> conflicts = GetUnmergedPaths(repoRoot);
            if (conflicts.Count > 0)
                return BuildConflictOutcome(repoRoot, conflicts, pulled: true);

            string headAfter = GetHeadCommit(repoRoot);
            bool moved = !string.IsNullOrEmpty(headBefore)
                         && !string.IsNullOrEmpty(headAfter)
                         && !string.Equals(headBefore, headAfter, StringComparison.OrdinalIgnoreCase);

            return ClassifyPullResult(pull.ExitCode, pull.StdOut, pull.StdErr, conflicts, moved,
                upstreamName, false, false, false);
        }

        /// <summary>
        /// Reads the repository state that decides which kind of conflict the agent is being handed:
        /// an unfinished merge, or an autostash that would not reapply.
        /// </summary>
        private GitPullOutcome BuildConflictOutcome(string repoRoot, List<string> conflicts, bool pulled)
        {
            return ClassifyPullResult(
                exitCode: 1,
                stdOut: string.Empty,
                stdErr: string.Empty,
                conflictedFiles: conflicts,
                headMoved: false,
                upstreamName: null,
                mergeInProgress: IsMergeInProgress(repoRoot),
                hasMergeAutoStash: HasMergeAutoStash(repoRoot),
                hasLeftoverAutoStash: HasLeftoverAutoStash(repoRoot),
                pulled: pulled);
        }

        /// <summary>Reads HEAD's commit id, or null when it can't be read (unborn branch, no git).</summary>
        private string GetHeadCommit(string repoRoot)
        {
            GitCommandResult result = RunGitDetailed(repoRoot, "rev-parse HEAD", GitSyncPreflightTimeoutMs);
            return result != null && result.ExitCode == 0 ? result.StdOut?.Trim() : null;
        }

        /// <summary>Repository-relative paths git reports as unmerged, i.e. conflicted.</summary>
        private List<string> GetUnmergedPaths(string repoRoot)
        {
            GitCommandResult status = RunGitDetailed(repoRoot, "status --porcelain=v1 -z", GitSyncPreflightTimeoutMs);
            if (status == null || status.ExitCode != 0)
                return new List<string>();

            return ParseUnmergedPaths(status.StdOut);
        }

        /// <summary>True when a merge is open and waiting to be resolved and committed.</summary>
        private bool IsMergeInProgress(string repoRoot)
        {
            return RefExists(repoRoot, "MERGE_HEAD");
        }

        /// <summary>
        /// True when git parked uncommitted work in MERGE_AUTOSTASH for the merge in progress. That
        /// stash is only reapplied when the merge is committed or aborted.
        /// </summary>
        private bool HasMergeAutoStash(string repoRoot)
        {
            return RefExists(repoRoot, "MERGE_AUTOSTASH");
        }

        private bool RefExists(string repoRoot, string refName)
        {
            GitCommandResult result = RunGitDetailed(repoRoot, "rev-parse --verify --quiet " + refName,
                GitSyncPreflightTimeoutMs);
            return result != null && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
        }

        /// <summary>
        /// True when an autostash entry is sitting in the ordinary stash list. Git moves it there when
        /// reapplying it conflicts — at which point the user's uncommitted work exists *only* there.
        /// </summary>
        private bool HasLeftoverAutoStash(string repoRoot)
        {
            GitCommandResult result = RunGitDetailed(repoRoot, "stash list", GitSyncPreflightTimeoutMs);
            if (result == null || result.ExitCode != 0)
                return false;

            return StashListMentionsAutoStash(result.StdOut);
        }

        /// <summary>Exit code plus both streams — <see cref="RunGitCommand"/> discards all three on failure.</summary>
        private class GitCommandResult
        {
            public int ExitCode { get; set; }
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
        }

        /// <summary>
        /// Runs git and returns the exit code and both output streams. Unlike the diff viewer's
        /// <see cref="RunGitCommand"/> this keeps stderr and the exit code, which is what tells a
        /// conflicted pull apart from an unreachable remote.
        /// </summary>
        private GitCommandResult RunGitDetailed(string workingDirectory, string arguments, int timeoutMs)
        {
            string gitPath = ResolveGitPath();
            if (string.IsNullOrEmpty(gitPath))
                return null;

            try
            {
                var processStart = new ProcessStartInfo
                {
                    FileName = gitPath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // A credential prompt would hang the pull behind an invisible dialog for the whole
                // timeout. Fail fast instead and leave the prompt to go out unpulled.
                processStart.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                processStart.EnvironmentVariables["GCM_INTERACTIVE"] = "never";

                using (var process = Process.Start(processStart))
                {
                    if (process == null)
                        return null;

                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); }
                        catch { /* Ignore failures on kill */ }
                        return null;
                    }

#pragma warning disable VSTHRD002 // Background-thread git helper; tasks only drain redirected process output.
                    return new GitCommandResult
                    {
                        ExitCode = process.ExitCode,
                        StdOut = outputTask.GetAwaiter().GetResult() ?? string.Empty,
                        StdErr = errorTask.GetAwaiter().GetResult() ?? string.Empty
                    };
#pragma warning restore VSTHRD002
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error running git command '{arguments}': {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Pure helpers (unit-tested)

        /// <summary>
        /// Extracts the unmerged (conflicted) paths from `git status --porcelain=v1 -z` output.
        /// Unmerged entries are the ones where both sides changed: DD, AU, UD, UA, DU, AA, UU.
        /// </summary>
        public static List<string> ParseUnmergedPaths(string porcelainStatusZ)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(porcelainStatusZ))
                return paths;

            foreach (string entry in porcelainStatusZ.Split('\0'))
            {
                // "XY <path>" — the status field is fixed width, so anything shorter is not an entry.
                if (entry.Length < 4)
                    continue;

                string code = entry.Substring(0, 2);
                if (!IsUnmergedStatusCode(code))
                    continue;

                string path = entry.Substring(3).Trim();
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }

            return paths;
        }

        /// <summary>The seven porcelain status codes that mean "unmerged", per git-status(1).</summary>
        private static bool IsUnmergedStatusCode(string code)
        {
            switch (code)
            {
                case "DD":
                case "AU":
                case "UD":
                case "UA":
                case "DU":
                case "AA":
                case "UU":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Turns a finished `git pull` into the outcome the caller acts on. Kept pure (no process,
        /// no UI) so every branch is unit-testable.
        /// </summary>
        /// <param name="exitCode">The pull's exit code.</param>
        /// <param name="stdOut">The pull's standard output.</param>
        /// <param name="stdErr">The pull's standard error.</param>
        /// <param name="conflictedFiles">Unmerged paths read from git status after the pull.</param>
        /// <param name="headMoved">True when HEAD changed, i.e. the pull actually brought commits in.</param>
        /// <param name="upstreamName">Tracking branch name, for the notice text.</param>
        /// <param name="mergeInProgress">True when MERGE_HEAD exists (a real unfinished merge).</param>
        /// <param name="hasMergeAutoStash">True when uncommitted work is parked in MERGE_AUTOSTASH.</param>
        /// <param name="hasLeftoverAutoStash">True when an autostash entry was left in `git stash list`.</param>
        /// <param name="pulled">False when the conflicts predate this prompt (left over from an earlier turn).</param>
        public static GitPullOutcome ClassifyPullResult(int exitCode, string stdOut, string stdErr,
            List<string> conflictedFiles, bool headMoved, string upstreamName,
            bool mergeInProgress, bool hasMergeAutoStash, bool hasLeftoverAutoStash, bool pulled = true)
        {
            var conflicts = conflictedFiles ?? new List<string>();

            // Conflicts win over the exit code, in both directions. A conflicted merge exits non-zero,
            // but a pull whose *autostash restore* conflicted exits ZERO — the merge itself succeeded
            // and only reapplying the local edits failed. Both leave unmerged paths behind, and both
            // are handed to the agent rather than rolled back.
            if (conflicts.Count > 0)
            {
                return new GitPullOutcome
                {
                    Kind = GitPullOutcomeKind.Conflicts,
                    PullExecuted = pulled,
                    ConflictedFiles = conflicts,
                    MergeInProgress = mergeInProgress,
                    HasMergeAutoStash = hasMergeAutoStash,
                    HasLeftoverAutoStash = hasLeftoverAutoStash,
                    Notice = BuildConflictNotice(conflicts, mergeInProgress, pulled)
                };
            }

            if (exitCode != 0)
            {
                return new GitPullOutcome
                {
                    Kind = GitPullOutcomeKind.Failed,
                    PullExecuted = true,
                    Notice = "Auto-pull skipped: " + SummarizeGitFailure(stdErr, stdOut)
                };
            }

            if (headMoved)
            {
                string target = string.IsNullOrWhiteSpace(upstreamName) ? "the remote" : upstreamName;
                return new GitPullOutcome
                {
                    Kind = GitPullOutcomeKind.Updated,
                    PullExecuted = true,
                    Notice = $"Pulled the latest changes from {target} before sending."
                };
            }

            return new GitPullOutcome { Kind = GitPullOutcomeKind.UpToDate, PullExecuted = true };
        }

        /// <summary>Condenses git's error output into one line for the notice.</summary>
        private static string SummarizeGitFailure(string stdErr, string stdOut)
        {
            string text = !string.IsNullOrWhiteSpace(stdErr) ? stdErr : stdOut;
            if (string.IsNullOrWhiteSpace(text))
                return "git pull did not complete.";

            // git's own first error line is the useful one; the rest is usually advice.
            string line = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("hint:", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(line))
                return "git pull did not complete.";

            // Strip git's "error: " / "fatal: " prefix so the notice reads as a sentence.
            foreach (string prefix in new[] { "error: ", "fatal: ", "warning: " })
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    line = line.Substring(prefix.Length);
                    break;
                }
            }

            const int MaxNoticeLength = 200;
            return line.Length > MaxNoticeLength ? line.Substring(0, MaxNoticeLength) + "…" : line;
        }

        /// <summary>Chat notice for a pull that ended in conflicts.</summary>
        private static string BuildConflictNotice(List<string> conflictedFiles, bool mergeInProgress, bool pulled)
        {
            int count = conflictedFiles?.Count ?? 0;
            string files = count == 1 ? "1 file" : $"{count} files";

            if (!pulled)
                return $"An unresolved conflict is still open in {files} — asking the agent to finish it first.";

            return mergeInProgress
                ? $"Auto-pull hit merge conflicts in {files} — asking the agent to resolve them first."
                : $"Auto-pull succeeded, but restoring your uncommitted changes conflicted in {files} — asking the agent to resolve them first.";
        }

        /// <summary>True when `git stash list` output contains an autostash entry.</summary>
        public static bool StashListMentionsAutoStash(string stashListOutput)
        {
            if (string.IsNullOrWhiteSpace(stashListOutput))
                return false;

            return stashListOutput.IndexOf("autostash", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True when git rejected --autostash as an unknown/invalid option, which is what git older
        /// than 2.27 does for a merge pull.
        /// </summary>
        public static bool MentionsUnsupportedAutoStash(string stdErr)
        {
            if (string.IsNullOrWhiteSpace(stdErr))
                return false;

            if (stdErr.IndexOf("autostash", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return stdErr.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0
                || stdErr.IndexOf("only valid with", StringComparison.OrdinalIgnoreCase) >= 0
                || stdErr.IndexOf("is not supported", StringComparison.OrdinalIgnoreCase) >= 0
                || stdErr.IndexOf("unrecognized", StringComparison.OrdinalIgnoreCase) >= 0
                || stdErr.IndexOf("usage: git pull", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Builds the block prepended to the prompt when the pre-prompt pull left conflicts. The
        /// user's own request follows it, so the conflicts are resolved as the first step of the turn.
        /// </summary>
        /// <remarks>
        /// The two conflict states need opposite instructions, which is why the outcome carries the
        /// repository state and not just the file list:
        ///   • MERGE_HEAD present — a real merge is open. Resolve, stage, and commit it.
        ///   • MERGE_HEAD absent — the merge already succeeded (the pull even exits 0) and the conflict
        ///     came from reapplying the autostash. There is nothing to commit; telling the agent to
        ///     "finish the merge" here would commit the user's work-in-progress behind their back.
        /// </remarks>
        public static string BuildConflictPromptBlock(GitPullOutcome outcome, bool hasUserRequest)
        {
            if (outcome == null)
                return string.Empty;

            var block = new StringBuilder();

            if (outcome.MergeInProgress)
            {
                block.AppendLine("A `git pull` ran automatically before this message and left the repository in a merge with conflicts.");
            }
            else
            {
                block.AppendLine("A `git pull` ran automatically before this message. The pull itself succeeded, but restoring "
                                 + "the uncommitted local changes it had stashed produced conflicts in the working tree. "
                                 + "**There is no merge in progress — do not create a merge commit.**");
            }

            block.AppendLine();
            block.AppendLine("Conflicted files:");

            if (outcome.ConflictedFiles != null)
            {
                foreach (string file in outcome.ConflictedFiles)
                    block.AppendLine("  - " + file);
            }

            block.AppendLine();

            if (outcome.MergeInProgress)
            {
                block.AppendLine("Please resolve these conflicts first: read both sides of each conflict, keep the intent of "
                                 + "both changes rather than discarding either one, remove the conflict markers, stage the "
                                 + "resolved files, and commit to complete the merge. Do not run `git merge --abort` or "
                                 + "otherwise throw away the incoming changes. If a conflict is genuinely ambiguous, stop and "
                                 + "ask instead of guessing.");

                if (outcome.HasMergeAutoStash || outcome.HasLeftoverAutoStash)
                {
                    block.AppendLine();
                    block.AppendLine("Note: uncommitted local changes were stashed automatically so the pull could run. Once "
                                     + "the merge is committed, make sure they are back in the working tree — git normally "
                                     + "restores them itself, but check `git stash list` and pop the autostash entry if one is "
                                     + "still there. Never drop that stash without restoring it: it is the only copy.");
                }
            }
            else
            {
                block.AppendLine("Please resolve these conflicts first: read both sides of each conflict, keep the intent of "
                                 + "both the incoming change and the local edit rather than discarding either one, and remove "
                                 + "the conflict markers. Leave the result as uncommitted working-tree changes — do not commit "
                                 + "unless I ask you to. Never run `git reset --hard` or `git checkout --` on these files: the "
                                 + "local edits are not committed anywhere. If a conflict is genuinely ambiguous, stop and ask "
                                 + "instead of guessing.");

                if (outcome.HasLeftoverAutoStash)
                {
                    block.AppendLine();
                    block.AppendLine("Git kept a safety copy of those local edits as an autostash entry in `git stash list`. "
                                     + "Drop it with `git stash drop` only once the conflicts above are resolved and the work "
                                     + "is back in the files.");
                }
            }

            if (hasUserRequest)
            {
                block.AppendLine();
                block.AppendLine("Once that is done, continue with the request below.");
                block.AppendLine();
                block.AppendLine("---");
            }

            block.AppendLine();
            return block.ToString();
        }

        #endregion
    }
}
