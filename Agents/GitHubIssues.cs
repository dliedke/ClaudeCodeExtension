/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "GitHub Issues" — the pure part of the issue list / fix-in-a-worktree flow: GitHub remote URL parsing,
 *          issue JSON parsing (gh CLI and REST API shapes), git worktree listing, branch/worktree naming, and the
 *          prompts handed to the agent. No process, network or UI here, so all of it is unit-tested.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>One comment on a GitHub issue.</summary>
    public sealed class GitHubIssueComment
    {
        public string Author { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime? CreatedAt { get; set; }
    }

    /// <summary>One GitHub issue, as listed by <c>gh issue list</c> / <c>gh issue view</c> or the REST API.</summary>
    public sealed class GitHubIssue
    {
        public int Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new List<string>();
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string Url { get; set; } = string.Empty;
        public int CommentCount { get; set; }

        /// <summary>Null until the issue's comments were fetched (the list call does not include them).</summary>
        public List<GitHubIssueComment> Comments { get; set; }

        public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A piece of an issue body or comment: markdown text, or one image to show inline.</summary>
    public sealed class IssueBodySegment
    {
        /// <summary>Markdown text; null for an image segment.</summary>
        public string Text { get; set; }

        /// <summary>http(s) URL of the image; null for a text segment.</summary>
        public string ImageUrl { get; set; }

        public string AltText { get; set; } = string.Empty;

        /// <summary>Width from an HTML <c>width="…"</c> attribute (pixels), when the author set one.</summary>
        public double? Width { get; set; }

        public bool IsImage => ImageUrl != null;
    }

    /// <summary>Where an issue's fix stands, as shown in the GitHub Issues list.</summary>
    public enum IssueFixStage
    {
        /// <summary>No fix branch, worktree or merge for the issue.</summary>
        None,

        /// <summary>The agent is working on it, or the worktree has no changes yet.</summary>
        Fixing,

        /// <summary>The worktree has uncommitted changes, ready for review and Commit &amp; Push.</summary>
        Fixed,

        /// <summary>Committed on the fix branch but not pushed (or the push is behind).</summary>
        Committed,

        /// <summary>Committed and pushed, ready to merge into the base branch.</summary>
        Pushed,

        /// <summary>The fix branch was merged into a base branch.</summary>
        Merged
    }

    /// <summary>One entry of <c>git worktree list --porcelain</c>.</summary>
    public sealed class GitWorktreeEntry
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>Short branch name ("fix/issue-12"), or empty for a detached HEAD / bare entry.</summary>
        public string Branch { get; set; } = string.Empty;

        public bool IsDetached { get; set; }
    }

    /// <summary>Pure helpers behind the GitHub Issues window.</summary>
    public static class GitHubIssues
    {
        /// <summary>Prefix of every branch the Fix flow creates; the issue number follows it.</summary>
        public const string BranchPrefix = "fix/issue-";

        /// <summary>
        /// Per-branch git config key remembering which branch a fix started from, so Merge targets the
        /// same branch after a Visual Studio restart. Read and written as <c>branch.&lt;name&gt;.issuebase</c>.
        /// </summary>
        public const string BaseBranchConfigKey = "issuebase";

        /// <summary>Issue comments beyond this many characters (in total) are cut from the fix prompt.</summary>
        public const int MaxCommentCharsInPrompt = 20000;

        /// <summary>Fields requested from <c>gh issue list</c>.</summary>
        public const string GhListFields = "number,title,state,author,labels,createdAt,updatedAt,url,body";

        private static readonly Regex GitHubRemoteRegex = new Regex(
            @"^(?:https?://(?:[^@/]+@)?github\.com/|ssh://git@github\.com(?::\d+)?/|git@github\.com:|git://github\.com/)" +
            @"(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// An image in an issue body: GitHub's pasted-screenshot HTML (<c>&lt;img width=".." src=".."&gt;</c>,
        /// optionally wrapped in a link) or markdown <c>![alt](url "title")</c>.
        /// </summary>
        private static readonly Regex ImageRegex = new Regex(
            @"(?:<a\b[^>]*>\s*)?(?<html><img\b[^>]*>)(?:\s*</a>)?" +
            @"|!\[(?<alt>[^\]]*)\]\(\s*<?(?<src>[^)\s>]+)>?(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HtmlAttributeRegex = new Regex(
            @"\b(?<name>src|alt|width)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>HTML comments — issue templates are full of them and GitHub does not render them.</summary>
        private static readonly Regex HtmlCommentRegex = new Regex(@"<!--.*?-->", RegexOptions.Singleline);

        /// <summary>
        /// Extracts owner and repository from a GitHub remote URL (https, ssh or scp-like form).
        /// Returns false for anything that is not github.com.
        /// </summary>
        public static bool TryParseGitHubRemote(string remoteUrl, out string owner, out string repo)
        {
            owner = null;
            repo = null;

            if (string.IsNullOrWhiteSpace(remoteUrl))
                return false;

            Match match = GitHubRemoteRegex.Match(remoteUrl.Trim());
            if (!match.Success)
                return false;

            owner = match.Groups["owner"].Value;
            repo = match.Groups["repo"].Value;
            return owner.Length > 0 && repo.Length > 0;
        }

        /// <summary>Branch name the Fix flow uses for an issue.</summary>
        public static string BuildBranchName(int issueNumber)
            => BranchPrefix + issueNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>The issue number a fix branch belongs to, or 0 when the branch is not one of ours.</summary>
        public static int ParseIssueNumberFromBranch(string branch)
        {
            if (string.IsNullOrEmpty(branch) || !branch.StartsWith(BranchPrefix, StringComparison.OrdinalIgnoreCase))
                return 0;

            return int.TryParse(branch.Substring(BranchPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? number
                : 0;
        }

        /// <summary>
        /// Folder for an issue's worktree: a sibling of the repository named "&lt;repo&gt;-worktrees",
        /// one subfolder per issue. Kept outside the repository so the main working copy never
        /// sees the worktree's files as untracked changes.
        /// </summary>
        public static string BuildWorktreePath(string repoRoot, int issueNumber)
        {
            string trimmed = repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed);
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(parent))
                parent = trimmed;

            return Path.Combine(parent, name + "-worktrees", "issue-" + issueNumber.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Parses <c>git worktree list --porcelain</c>.</summary>
        public static List<GitWorktreeEntry> ParseWorktreeList(string porcelain)
        {
            var entries = new List<GitWorktreeEntry>();
            if (string.IsNullOrEmpty(porcelain))
                return entries;

            GitWorktreeEntry current = null;
            foreach (string rawLine in porcelain.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    current = new GitWorktreeEntry { Path = NormalizeGitPath(line.Substring("worktree ".Length)) };
                    entries.Add(current);
                }
                else if (current != null && line.StartsWith("branch ", StringComparison.Ordinal))
                {
                    string reference = line.Substring("branch ".Length).Trim();
                    const string headsPrefix = "refs/heads/";
                    current.Branch = reference.StartsWith(headsPrefix, StringComparison.Ordinal)
                        ? reference.Substring(headsPrefix.Length)
                        : reference;
                }
                else if (current != null && line == "detached")
                {
                    current.IsDetached = true;
                }
            }

            return entries;
        }

        /// <summary>git prints forward slashes on Windows; turn them back into a normal Windows path.</summary>
        public static string NormalizeGitPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path.Trim().Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Parses a JSON array of issues in either the gh CLI shape (<c>author.login</c>, <c>createdAt</c>,
        /// <c>labels[].name</c>, <c>comments[]</c>) or the REST API shape (<c>user.login</c>, <c>created_at</c>,
        /// <c>html_url</c>, <c>comments</c> as a count). REST entries that are pull requests are skipped —
        /// the REST issues endpoint returns both.
        /// </summary>
        public static List<GitHubIssue> ParseIssueList(string json)
        {
            var issues = new List<GitHubIssue>();
            if (string.IsNullOrWhiteSpace(json))
                return issues;

            JToken root = JToken.Parse(json);
            if (!(root is JArray array))
                return issues;

            foreach (JToken item in array)
            {
                if (!(item is JObject obj) || obj["pull_request"] != null)
                    continue;

                GitHubIssue issue = ParseIssueObject(obj);
                if (issue != null)
                    issues.Add(issue);
            }

            return issues;
        }

        /// <summary>Parses a single issue object (gh CLI <c>issue view --json</c> output).</summary>
        public static GitHubIssue ParseIssue(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JToken.Parse(json) is JObject obj ? ParseIssueObject(obj) : null;
        }

        /// <summary>
        /// Parses issue comments from the REST API array (<c>GET /repos/o/r/issues/N/comments</c>) or the
        /// gh CLI object (<c>gh issue view N --json comments</c> → <c>{"comments":[...]}</c>).
        /// </summary>
        public static List<GitHubIssueComment> ParseComments(string json)
        {
            var comments = new List<GitHubIssueComment>();
            if (string.IsNullOrWhiteSpace(json))
                return comments;

            JToken root = JToken.Parse(json);
            if (root is JObject wrapper)
                root = wrapper["comments"];

            if (root is JArray array)
            {
                foreach (JToken item in array)
                {
                    if (item is JObject obj)
                        comments.Add(ParseComment(obj));
                }
            }

            return comments;
        }

        private static GitHubIssue ParseIssueObject(JObject obj)
        {
            int number = obj.Value<int?>("number") ?? 0;
            if (number <= 0)
                return null;

            var issue = new GitHubIssue
            {
                Number = number,
                Title = obj.Value<string>("title") ?? string.Empty,
                Body = obj.Value<string>("body") ?? string.Empty,
                State = (obj.Value<string>("state") ?? string.Empty).ToLowerInvariant(),
                Author = ReadLogin(obj["author"]) ?? ReadLogin(obj["user"]) ?? string.Empty,
                CreatedAt = ReadDate(obj, "createdAt", "created_at"),
                UpdatedAt = ReadDate(obj, "updatedAt", "updated_at"),
                Url = obj.Value<string>("url") is string url && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                    ? url
                    : (obj.Value<string>("html_url") ?? obj.Value<string>("url") ?? string.Empty)
            };

            if (obj["labels"] is JArray labels)
            {
                foreach (JToken label in labels)
                {
                    string name = label is JObject labelObj ? labelObj.Value<string>("name") : label.Type == JTokenType.String ? label.Value<string>() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                        issue.Labels.Add(name);
                }
            }

            JToken comments = obj["comments"];
            if (comments is JArray commentArray)
            {
                issue.Comments = commentArray.OfType<JObject>().Select(ParseComment).ToList();
                issue.CommentCount = issue.Comments.Count;
            }
            else if (comments != null && comments.Type == JTokenType.Integer)
            {
                issue.CommentCount = comments.Value<int>();
                if (issue.CommentCount == 0)
                    issue.Comments = new List<GitHubIssueComment>();
            }

            return issue;
        }

        private static GitHubIssueComment ParseComment(JObject obj)
        {
            return new GitHubIssueComment
            {
                Author = ReadLogin(obj["author"]) ?? ReadLogin(obj["user"]) ?? string.Empty,
                Body = obj.Value<string>("body") ?? string.Empty,
                CreatedAt = ReadDate(obj, "createdAt", "created_at")
            };
        }

        private static string ReadLogin(JToken token)
            => token is JObject obj ? obj.Value<string>("login") : null;

        private static DateTime? ReadDate(JObject obj, params string[] names)
        {
            foreach (string name in names)
            {
                JToken token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                if (token.Type == JTokenType.Date)
                    return token.Value<DateTime>().ToUniversalTime();

                if (DateTime.TryParse(token.Value<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                    return parsed;
            }

            return null;
        }

        /// <summary>
        /// List filter: "12" or "#12" matches issue numbers starting with 12 (and titles containing it);
        /// any other text matches the title, author or a label, case-insensitively. Empty matches all.
        /// </summary>
        public static bool MatchesFilter(GitHubIssue issue, string query)
        {
            if (issue == null)
                return false;

            query = (query ?? string.Empty).Trim();
            if (query.Length == 0)
                return true;

            string digits = query.TrimStart('#');
            if (digits.Length > 0 && digits.All(char.IsDigit) &&
                issue.Number.ToString(CultureInfo.InvariantCulture).StartsWith(digits, StringComparison.Ordinal))
                return true;

            return (issue.Title ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (issue.Author ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   issue.Labels.Any(l => l.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Splits an issue body or comment into markdown text and the images in it, in order, so the
        /// details pane can show each image inline. HTML comments are dropped; an image whose source is
        /// not http(s) (data URI, relative path) stays in the text as written.
        /// </summary>
        public static List<IssueBodySegment> SplitImages(string markdown)
        {
            var segments = new List<IssueBodySegment>();
            string text = HtmlCommentRegex.Replace(markdown ?? string.Empty, string.Empty);

            int start = 0;
            Action<int> addText = end =>
            {
                if (end > start)
                {
                    string piece = text.Substring(start, end - start);
                    if (!string.IsNullOrWhiteSpace(piece))
                        segments.Add(new IssueBodySegment { Text = piece.Trim('\r', '\n') });
                }
            };

            foreach (Match match in ImageRegex.Matches(text))
            {
                string src;
                string alt;
                double? width = null;

                if (match.Groups["html"].Success)
                {
                    var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match attribute in HtmlAttributeRegex.Matches(match.Groups["html"].Value))
                        attributes[attribute.Groups["name"].Value] = WebUtilityDecode(attribute.Groups["value"].Value);

                    attributes.TryGetValue("src", out src);
                    attributes.TryGetValue("alt", out alt);
                    if (attributes.TryGetValue("width", out string widthText) &&
                        double.TryParse(widthText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
                    {
                        width = parsed;
                    }
                }
                else
                {
                    src = match.Groups["src"].Value;
                    alt = match.Groups["alt"].Value;
                }

                if (string.IsNullOrEmpty(src) ||
                    !(src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                addText(match.Index);
                segments.Add(new IssueBodySegment { ImageUrl = src, AltText = alt ?? string.Empty, Width = width });
                start = match.Index + match.Length;
            }

            addText(text.Length);
            return segments;
        }

        private static string WebUtilityDecode(string value)
            => System.Net.WebUtility.HtmlDecode(value ?? string.Empty);

        /// <summary>
        /// Merge commit subjects naming a fix branch: the Merge action's own
        /// <c>Merge branch 'fix/issue-N'…</c> (also git's default <c>… into base</c>) and GitHub's
        /// <c>Merge pull request #X from owner/fix/issue-N</c>. The reverse merge the conflict flow makes
        /// (<c>Merge remote-tracking branch 'origin/base' into fix/issue-N</c>) does not match.
        /// </summary>
        private static readonly Regex MergedFixRegex = new Regex(
            @"^(?:Merge branch '" + Regex.Escape(BranchPrefix) + @"(?<n>\d+)'|Merge pull request #\d+ from \S+?/" + Regex.Escape(BranchPrefix) + @"(?<n>\d+)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

        /// <summary>Issue numbers whose fix branch was merged, from <c>git log --merges --format=%s</c> output.</summary>
        public static HashSet<int> ParseMergedIssueNumbers(string mergeSubjects)
        {
            var numbers = new HashSet<int>();
            foreach (Match match in MergedFixRegex.Matches(mergeSubjects ?? string.Empty))
            {
                if (int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0)
                    numbers.Add(number);
            }

            return numbers;
        }

        /// <summary>
        /// Where a fix stands. A running agent turn always reads as Fixing; otherwise the git state
        /// decides — uncommitted changes, then unpushed or pushed commits, then a merge on record.
        /// </summary>
        public static IssueFixStage ResolveFixStage(bool hasBranchOrWorktree, bool agentWorking, bool dirty, int ahead, bool pushed, bool merged)
        {
            if (agentWorking)
                return IssueFixStage.Fixing;

            if (hasBranchOrWorktree)
            {
                if (dirty) return IssueFixStage.Fixed;
                if (ahead > 0) return pushed ? IssueFixStage.Pushed : IssueFixStage.Committed;
                return merged ? IssueFixStage.Merged : IssueFixStage.Fixing;
            }

            return merged ? IssueFixStage.Merged : IssueFixStage.None;
        }

        /// <summary>Short label for a stage, as shown in the list and the details.</summary>
        public static string DescribeFixStage(IssueFixStage stage, bool agentWorking)
        {
            switch (stage)
            {
                case IssueFixStage.Fixing: return agentWorking ? "🔧 Fixing…" : "🔧 Fixing";
                case IssueFixStage.Fixed: return "📝 Fixed — review & commit";
                case IssueFixStage.Committed: return "⬆ Committed — push pending";
                case IssueFixStage.Pushed: return "🚀 Pushed — ready to merge";
                case IssueFixStage.Merged: return "✅ Merged";
                default: return string.Empty;
            }
        }

        /// <summary>Short relative age for the list ("5m", "3h", "12d", "2mo", "1y").</summary>
        public static string FormatAge(DateTime? utc, DateTime nowUtc)
        {
            if (!utc.HasValue)
                return string.Empty;

            TimeSpan age = nowUtc - utc.Value;
            if (age.TotalMinutes < 1) return "now";
            if (age.TotalHours < 1) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
            if (age.TotalDays < 1) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
            if (age.TotalDays < 60) return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
            if (age.TotalDays < 365) return ((int)(age.TotalDays / 30)).ToString(CultureInfo.InvariantCulture) + "mo";
            return ((int)(age.TotalDays / 365)).ToString(CultureInfo.InvariantCulture) + "y";
        }

        /// <summary>
        /// The prompt that opens an issue's fix chat. The agent works inside the issue's worktree on its
        /// own branch, and is told not to commit: Commit &amp; Push in the Issues window does that once the
        /// user has reviewed the change.
        /// </summary>
        public static string BuildFixPrompt(GitHubIssue issue, string branch, string baseBranch, bool resumingExistingWork)
        {
            if (issue == null)
                throw new ArgumentNullException(nameof(issue));

            var sb = new StringBuilder();
            sb.Append("Fix GitHub issue #").Append(issue.Number.ToString(CultureInfo.InvariantCulture))
              .Append(": ").AppendLine(issue.Title.Trim());
            if (!string.IsNullOrEmpty(issue.Url))
                sb.AppendLine(issue.Url);
            sb.AppendLine();

            sb.Append("You are working in a dedicated git worktree on branch `").Append(branch)
              .Append("`, created from `").Append(baseBranch).AppendLine("`.");
            if (resumingExistingWork)
            {
                sb.AppendLine("This worktree may already contain earlier work on this issue — check `git status` and `git log` first and continue from there.");
            }
            sb.AppendLine("Investigate the cause, make the fix, and build or test it if you can.");
            sb.AppendLine("Do not commit, push or switch branches — the user reviews the change and commits it from Visual Studio.");
            sb.AppendLine("If the issue is unclear, cannot be reproduced, or is not a bug, explain why instead of changing code.");
            sb.AppendLine("When you are done, summarize what you changed and why.");
            sb.AppendLine();

            sb.Append("Reported by @").Append(string.IsNullOrEmpty(issue.Author) ? "unknown" : issue.Author);
            if (issue.Labels.Count > 0)
                sb.Append(" · labels: ").Append(string.Join(", ", issue.Labels));
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("--- Issue description ---");
            sb.AppendLine(string.IsNullOrWhiteSpace(issue.Body) ? "(no description)" : issue.Body.Trim());

            if (issue.Comments != null && issue.Comments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Comments ---");

                int used = 0;
                int shown = 0;
                foreach (GitHubIssueComment comment in issue.Comments)
                {
                    string body = (comment.Body ?? string.Empty).Trim();
                    if (used + body.Length > MaxCommentCharsInPrompt)
                        break;

                    used += body.Length;
                    shown++;
                    sb.Append("@").Append(string.IsNullOrEmpty(comment.Author) ? "unknown" : comment.Author);
                    if (comment.CreatedAt.HasValue)
                        sb.Append(" (").Append(comment.CreatedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(")");
                    sb.AppendLine(":");
                    sb.AppendLine(body);
                    sb.AppendLine();
                }

                if (shown < issue.Comments.Count)
                {
                    sb.Append("(").Append((issue.Comments.Count - shown).ToString(CultureInfo.InvariantCulture))
                      .AppendLine(" more comments omitted — see the issue page.)");
                }
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        /// <summary>Fallback commit message when no agent is available to draft one.</summary>
        public static string BuildDefaultCommitMessage(GitHubIssue issue)
        {
            string title = (issue?.Title ?? string.Empty).Trim();
            string summary = "Fix #" + (issue?.Number ?? 0).ToString(CultureInfo.InvariantCulture) + (title.Length > 0 ? ": " + title : string.Empty);
            if (summary.Length > 72)
                summary = summary.Substring(0, 71).TrimEnd() + "…";

            return summary;
        }

        /// <summary>
        /// Appends a "Fixes #N" trailer unless the message already references the issue with one of
        /// GitHub's closing keywords.
        /// </summary>
        public static string EnsureIssueReference(string message, int issueNumber)
        {
            string text = (message ?? string.Empty).TrimEnd();
            string number = issueNumber.ToString(CultureInfo.InvariantCulture);

            var closing = new Regex(@"\b(close[sd]?|fix(e[sd])?|resolve[sd]?)\s+#" + number + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (closing.IsMatch(text))
                return text;

            return text + "\n\nFixes #" + number;
        }

        /// <summary>Merge commit message for merging a fix branch into its base.</summary>
        public static string BuildMergeCommitMessage(GitHubIssue issue, string branch)
        {
            string title = (issue?.Title ?? string.Empty).Trim();
            string number = (issue?.Number ?? 0).ToString(CultureInfo.InvariantCulture);
            return "Merge branch '" + branch + "'" + (title.Length > 0 ? ": " + title : string.Empty) + " (#" + number + ")";
        }

        /// <summary>Prompt asking the fix chat's agent to draft the reply posted on the issue after the merge.</summary>
        public static string BuildReplyDraftPrompt(GitHubIssue issue, string baseBranch)
        {
            return "Write a short reply to post on GitHub issue #" + (issue?.Number ?? 0).ToString(CultureInfo.InvariantCulture) +
                   " now that your fix is merged into `" + baseBranch + "`. Address the reporter directly, in 2–4 sentences: " +
                   "say what was wrong and what changed, from the user's point of view. Plain GitHub markdown. " +
                   "Do not credit the AI in any way — no signature, no \"Generated with\" line, no model name. " +
                   "Reply with ONLY the comment text — no code fences around it, no preamble.";
        }

        /// <summary>Reply used when no agent is available to draft one.</summary>
        public static string BuildDefaultReply(GitHubIssue issue, string baseBranch)
        {
            return "Thanks for reporting this. A fix has been merged into `" + baseBranch + "`.";
        }

        /// <summary>Appends the "merged in" line (branch and short commit) to the reply that is posted.</summary>
        public static string AppendMergeFootnote(string reply, string baseBranch, string commitSha)
        {
            string text = (reply ?? string.Empty).TrimEnd();
            if (string.IsNullOrEmpty(commitSha))
                return text;

            string shortSha = commitSha.Length > 7 ? commitSha.Substring(0, 7) : commitSha;
            return text + "\n\nMerged into `" + baseBranch + "` in " + shortSha + ".";
        }

        /// <summary>
        /// The conflict hand-off sent to the fix chat when merging the base branch into the fix branch
        /// leaves conflicts. The merge is left in progress for the agent to resolve and commit.
        /// </summary>
        public static string BuildConflictPrompt(string baseBranch, IList<string> conflictedFiles)
        {
            var sb = new StringBuilder();
            sb.Append("Merging the latest `").Append(baseBranch)
              .AppendLine("` into this branch produced conflicts. The merge is in progress in this worktree.");
            if (conflictedFiles != null && conflictedFiles.Count > 0)
            {
                sb.AppendLine("Conflicted files:");
                foreach (string file in conflictedFiles)
                    sb.Append("- ").AppendLine(file);
            }
            sb.AppendLine("Resolve every conflict so both this fix and the incoming changes keep working, then stage the files and run `git commit --no-edit` to finish the merge.");
            sb.AppendLine("Do not push and do not switch branches.");
            return sb.ToString();
        }
    }
}
