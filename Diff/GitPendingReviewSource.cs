/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Git + file-system implementation of IPendingReviewSource for the "pending review" tracker
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Answers the <see cref="PendingReviewTracker"/>'s questions with git commands run in the repository
    /// root. The git runner is injected (it returns raw stdout, or null on failure/timeout/non-zero exit)
    /// so the -z output parsing here can be unit-tested without a repository.
    /// </summary>
    public sealed class GitPendingReviewSource : IPendingReviewSource
    {
        /// <summary>Largest file read into a snapshot; matches the diff viewer's own limit.</summary>
        internal const int MaxFileBytes = 4 * 1024 * 1024;

        private readonly Func<string, byte[]> _runGit;
        private readonly Func<string, bool> _isTrackable;

        /// <param name="repositoryRoot">Full path of the repository root (the git working directory).</param>
        /// <param name="runGit">Runs git with the given arguments in the repository root; raw stdout or null.</param>
        /// <param name="isTrackable">Whether the diff viewer shows a given full path.</param>
        public GitPendingReviewSource(string repositoryRoot, Func<string, byte[]> runGit, Func<string, bool> isTrackable)
        {
            if (string.IsNullOrEmpty(repositoryRoot)) throw new ArgumentNullException(nameof(repositoryRoot));
            RepositoryRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _runGit = runGit ?? throw new ArgumentNullException(nameof(runGit));
            _isTrackable = isTrackable;
        }

        public string RepositoryRoot { get; }

        public bool IsTrackable(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (!fullPath.StartsWith(RepositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            return _isTrackable == null || _isTrackable(fullPath);
        }

        public bool TryGetHeadCommit(out string commit)
        {
            commit = null;

            byte[] head = _runGit("rev-parse --verify -q HEAD^{commit}");
            if (head != null)
            {
                string sha = Encoding.UTF8.GetString(head).Trim();
                if (sha.Length > 0)
                {
                    commit = sha;
                    return true;
                }
            }

            // rev-parse --verify fails both when git is broken and when HEAD is unborn (no commits yet);
            // a repository that still answers --git-dir is the unborn case.
            return _runGit("rev-parse --git-dir") != null;
        }

        public IList<string> ListDirtyPaths()
        {
            byte[] output = _runGit("--no-optional-locks status --porcelain=v1 -z --untracked-files=all");
            if (output == null) return null;

            var paths = new List<string>();
            foreach (string relative in ParseStatusPaths(Encoding.UTF8.GetString(output)))
            {
                string full = ToFullPath(relative);
                if (full != null) paths.Add(full);
            }
            return paths;
        }

        public IDictionary<string, bool> ListPathsChangedSince(string commit)
        {
            if (string.IsNullOrEmpty(commit)) return null;

            byte[] diff = _runGit("--no-optional-locks diff --name-status -z --no-renames --no-ext-diff " + commit);
            if (diff == null) return null;

            byte[] untracked = _runGit("--no-optional-locks ls-files --others --exclude-standard -z");
            if (untracked == null) return null;

            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, bool> entry in ParseNameStatus(Encoding.UTF8.GetString(diff)))
            {
                string full = ToFullPath(entry.Key);
                if (full != null) result[full] = entry.Value;
            }

            foreach (string relative in Encoding.UTF8.GetString(untracked).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string full = ToFullPath(relative);
                if (full != null && !result.ContainsKey(full)) result[full] = false;
            }

            return result;
        }

        public FileSnapshot ReadWorkingFile(string fullPath)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    // A directory in its place is not something the review can restore around.
                    return Directory.Exists(fullPath) ? null : FileSnapshot.Missing;
                }

                if (info.Length > MaxFileBytes) return null;

                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var buffer = new MemoryStream((int)info.Length))
                {
                    stream.CopyTo(buffer);
                    return new FileSnapshot(buffer.ToArray());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PendingReview: could not read {fullPath}: {ex.Message}");
                return null;
            }
        }

        public FileSnapshot ReadCommittedFile(string commit, string fullPath)
        {
            string relative = ToRelativePath(fullPath);
            if (relative == null || string.IsNullOrEmpty(commit)) return null;

            // --filters applies the eol/smudge conversion a checkout would, so restoring these bytes
            // gives back exactly the file the user had (CRLF on Windows), not the raw blob.
            byte[] content = _runGit("cat-file --filters " + QuoteArgument(commit + ":" + relative));
            if (content == null || content.Length > MaxFileBytes) return null;
            return new FileSnapshot(content);
        }

        /// <summary>Paths from <c>git status --porcelain=v1 -z</c>; a rename contributes both its old and new path.</summary>
        internal static IEnumerable<string> ParseStatusPaths(string output)
        {
            if (string.IsNullOrEmpty(output)) yield break;

            string[] parts = output.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length < 4) continue;

                string status = part.Substring(0, 2);
                yield return part.Substring(3);

                bool isRenameOrCopy = status.IndexOf('R') >= 0 || status.IndexOf('C') >= 0;
                if (isRenameOrCopy && i + 1 < parts.Length)
                {
                    string source = parts[++i];
                    if (source.Length > 0) yield return source;
                }
            }
        }

        /// <summary>
        /// Entries from <c>git diff --name-status -z --no-renames</c> (status and path alternate), mapped to
        /// whether the path exists in the compared commit — everything but an addition does.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, bool>> ParseNameStatus(string output)
        {
            if (string.IsNullOrEmpty(output)) yield break;

            string[] parts = output.Split('\0');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string status = parts[i];
                string path = parts[i + 1];
                if (status.Length == 0 || path.Length == 0) continue;

                yield return new KeyValuePair<string, bool>(path, status[0] != 'A');
            }
        }

        /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
        internal static string QuoteArgument(string value)
        {
            var quoted = new StringBuilder();
            quoted.Append('"');

            int backslashes = 0;
            foreach (char c in value ?? string.Empty)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                }
                else
                {
                    quoted.Append('\\', backslashes);
                    quoted.Append(c);
                }
                backslashes = 0;
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private string ToFullPath(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;

            try
            {
                return Path.GetFullPath(Path.Combine(RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return null;
            }
        }

        private string ToRelativePath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;

            string prefix = RepositoryRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            return fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
