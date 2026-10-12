/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the pure helpers behind the GitHub Issues window — remote parsing, issue JSON in the gh CLI and
 *          REST shapes, worktree listing, branch naming, the list filter and the texts handed to git and the agent.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class GitHubIssuesTests
    {
        #region Remote URL parsing

        [DataTestMethod]
        [DataRow("https://github.com/dliedke/ClaudeCodeExtension.git")]
        [DataRow("https://github.com/dliedke/ClaudeCodeExtension")]
        [DataRow("https://github.com/dliedke/ClaudeCodeExtension/")]
        [DataRow("https://user@github.com/dliedke/ClaudeCodeExtension.git")]
        [DataRow("git@github.com:dliedke/ClaudeCodeExtension.git")]
        [DataRow("ssh://git@github.com/dliedke/ClaudeCodeExtension.git")]
        [DataRow("ssh://git@github.com:443/dliedke/ClaudeCodeExtension")]
        [DataRow("  https://GitHub.com/dliedke/ClaudeCodeExtension.git  ")]
        public void TryParseGitHubRemote_AcceptsGitHubForms(string url)
        {
            Assert.IsTrue(GitHubIssues.TryParseGitHubRemote(url, out string owner, out string repo));
            Assert.AreEqual("dliedke", owner);
            Assert.AreEqual("ClaudeCodeExtension", repo);
        }

        [TestMethod]
        public void TryParseGitHubRemote_KeepsDotsInRepoName()
        {
            Assert.IsTrue(GitHubIssues.TryParseGitHubRemote("https://github.com/owner/my.repo.git", out _, out string repo));
            Assert.AreEqual("my.repo", repo);
        }

        [DataTestMethod]
        [DataRow("https://gitlab.com/owner/repo.git")]
        [DataRow("https://dev.azure.com/org/project/_git/repo")]
        [DataRow("https://github.com.evil.example/owner/repo")]
        [DataRow("https://github.com/owner")]
        [DataRow("C:\\repos\\local")]
        [DataRow("")]
        [DataRow(null)]
        public void TryParseGitHubRemote_RejectsOthers(string url)
        {
            Assert.IsFalse(GitHubIssues.TryParseGitHubRemote(url, out _, out _));
        }

        #endregion

        #region Issue JSON

        [TestMethod]
        public void ParseIssueList_GhShape()
        {
            const string json = @"[{
                ""number"": 42, ""title"": ""Crash on open"", ""state"": ""OPEN"",
                ""author"": { ""login"": ""alice"" },
                ""labels"": [ { ""name"": ""bug"" }, { ""name"": ""ui"" } ],
                ""createdAt"": ""2026-10-01T10:00:00Z"", ""updatedAt"": ""2026-10-02T12:30:00Z"",
                ""url"": ""https://github.com/o/r/issues/42"", ""body"": ""Steps...""
            }]";

            List<GitHubIssue> issues = GitHubIssues.ParseIssueList(json);

            Assert.AreEqual(1, issues.Count);
            GitHubIssue issue = issues[0];
            Assert.AreEqual(42, issue.Number);
            Assert.AreEqual("Crash on open", issue.Title);
            Assert.AreEqual("open", issue.State);
            Assert.IsTrue(issue.IsOpen);
            Assert.AreEqual("alice", issue.Author);
            CollectionAssert.AreEqual(new[] { "bug", "ui" }, issue.Labels.ToArray());
            Assert.AreEqual(new DateTime(2026, 10, 2, 12, 30, 0, DateTimeKind.Utc), issue.UpdatedAt);
            Assert.AreEqual("https://github.com/o/r/issues/42", issue.Url);
            Assert.IsNull(issue.Comments, "gh list does not include comments, so they must still be fetched.");
        }

        [TestMethod]
        public void ParseIssueList_RestShape_SkipsPullRequests()
        {
            const string json = @"[
                { ""number"": 7, ""title"": ""Real issue"", ""state"": ""closed"", ""user"": { ""login"": ""bob"" },
                  ""labels"": [ { ""name"": ""enhancement"" } ], ""comments"": 3,
                  ""created_at"": ""2026-09-01T00:00:00Z"", ""updated_at"": ""2026-09-05T00:00:00Z"",
                  ""url"": ""https://api.github.com/repos/o/r/issues/7"", ""html_url"": ""https://github.com/o/r/issues/7"", ""body"": null },
                { ""number"": 8, ""title"": ""A pull request"", ""state"": ""open"", ""user"": { ""login"": ""carol"" },
                  ""pull_request"": { ""url"": ""https://api.github.com/repos/o/r/pulls/8"" } },
                { ""number"": 9, ""title"": ""No comments"", ""state"": ""open"", ""comments"": 0 }
            ]";

            List<GitHubIssue> issues = GitHubIssues.ParseIssueList(json);

            CollectionAssert.AreEqual(new[] { 7, 9 }, issues.Select(i => i.Number).ToArray());
            GitHubIssue issue = issues[0];
            Assert.AreEqual("bob", issue.Author);
            Assert.IsFalse(issue.IsOpen);
            Assert.AreEqual(3, issue.CommentCount);
            Assert.IsNull(issue.Comments, "Comments counted but not loaded yet.");
            Assert.AreEqual("https://github.com/o/r/issues/7", issue.Url, "The web URL, not the API URL.");
            Assert.AreEqual(string.Empty, issue.Body);
            Assert.IsNotNull(issues[1].Comments, "Zero comments needs no fetch.");
            Assert.AreEqual(0, issues[1].Comments.Count);
        }

        [TestMethod]
        public void ParseIssueList_EmptyOrNonArray_ReturnsEmpty()
        {
            Assert.AreEqual(0, GitHubIssues.ParseIssueList("").Count);
            Assert.AreEqual(0, GitHubIssues.ParseIssueList("{\"message\":\"Not Found\"}").Count);
        }

        [TestMethod]
        public void ParseComments_GhObjectAndRestArray()
        {
            const string gh = @"{ ""comments"": [
                { ""author"": { ""login"": ""alice"" }, ""body"": ""Same here"", ""createdAt"": ""2026-10-03T08:00:00Z"" } ] }";
            const string rest = @"[
                { ""user"": { ""login"": ""bob"" }, ""body"": ""Repro attached"", ""created_at"": ""2026-10-04T09:00:00Z"" },
                { ""user"": { ""login"": ""carol"" }, ""body"": ""+1"" } ]";

            List<GitHubIssueComment> fromGh = GitHubIssues.ParseComments(gh);
            List<GitHubIssueComment> fromRest = GitHubIssues.ParseComments(rest);

            Assert.AreEqual(1, fromGh.Count);
            Assert.AreEqual("alice", fromGh[0].Author);
            Assert.AreEqual("Same here", fromGh[0].Body);
            Assert.AreEqual(new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc), fromGh[0].CreatedAt);

            CollectionAssert.AreEqual(new[] { "bob", "carol" }, fromRest.Select(c => c.Author).ToArray());
            Assert.IsNull(fromRest[1].CreatedAt);
        }

        #endregion

        #region Worktrees and branches

        [TestMethod]
        public void ParseWorktreeList_ReadsBranchesAndDetached()
        {
            const string porcelain =
                "worktree C:/GitLab/Repo\r\nHEAD 1111111111111111111111111111111111111111\r\nbranch refs/heads/master\r\n\r\n" +
                "worktree C:/GitLab/Repo-worktrees/issue-12\r\nHEAD 2222222222222222222222222222222222222222\r\nbranch refs/heads/fix/issue-12\r\n\r\n" +
                "worktree C:/GitLab/Repo-worktrees/issue-13\r\nHEAD 3333333333333333333333333333333333333333\r\ndetached\r\n";

            List<GitWorktreeEntry> entries = GitHubIssues.ParseWorktreeList(porcelain);

            Assert.AreEqual(3, entries.Count);
            Assert.AreEqual(@"C:\GitLab\Repo", entries[0].Path);
            Assert.AreEqual("master", entries[0].Branch);
            Assert.AreEqual(@"C:\GitLab\Repo-worktrees\issue-12", entries[1].Path);
            Assert.AreEqual("fix/issue-12", entries[1].Branch);
            Assert.IsFalse(entries[1].IsDetached);
            Assert.AreEqual(string.Empty, entries[2].Branch);
            Assert.IsTrue(entries[2].IsDetached);
        }

        [TestMethod]
        public void ParseWorktreeList_Empty()
        {
            Assert.AreEqual(0, GitHubIssues.ParseWorktreeList(null).Count);
            Assert.AreEqual(0, GitHubIssues.ParseWorktreeList("").Count);
        }

        [TestMethod]
        public void BranchName_RoundTrips()
        {
            Assert.AreEqual("fix/issue-183", GitHubIssues.BuildBranchName(183));
            Assert.AreEqual(183, GitHubIssues.ParseIssueNumberFromBranch("fix/issue-183"));
        }

        [DataTestMethod]
        [DataRow("master")]
        [DataRow("fix/issue-")]
        [DataRow("fix/issue-12a")]
        [DataRow("fix/issue--3")]
        [DataRow("feature/issue-12")]
        [DataRow(null)]
        public void ParseIssueNumberFromBranch_NotOurs(string branch)
        {
            Assert.AreEqual(0, GitHubIssues.ParseIssueNumberFromBranch(branch));
        }

        [TestMethod]
        public void BuildWorktreePath_IsSiblingOfRepository()
        {
            Assert.AreEqual(@"C:\GitLab\Repo-worktrees\issue-5", GitHubIssues.BuildWorktreePath(@"C:\GitLab\Repo", 5));
            Assert.AreEqual(@"C:\GitLab\Repo-worktrees\issue-5", GitHubIssues.BuildWorktreePath(@"C:\GitLab\Repo\", 5));
        }

        #endregion

        #region Filter and age

        [TestMethod]
        public void MatchesFilter_NumberTitleAuthorLabel()
        {
            var issue = new GitHubIssue { Number = 123, Title = "Crash when pasting images", Author = "alice" };
            issue.Labels.Add("bug");

            Assert.IsTrue(GitHubIssues.MatchesFilter(issue, ""));
            Assert.IsTrue(GitHubIssues.MatchesFilter(issue, "12"));
            Assert.IsTrue(GitHubIssues.MatchesFilter(issue, "#123"));
            Assert.IsFalse(GitHubIssues.MatchesFilter(issue, "#23"));
            Assert.IsTrue(GitHubIssues.MatchesFilter(issue, "PASTING"));
            Assert.IsTrue(GitHubIssues.MatchesFilter(issue, "ali"));
            Assert.IsTrue(GitHubIssues.MatchesFilter(issue, "Bug"));
            Assert.IsFalse(GitHubIssues.MatchesFilter(issue, "enhancement"));
        }

        [TestMethod]
        public void FormatAge_Buckets()
        {
            var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(string.Empty, GitHubIssues.FormatAge(null, now));
            Assert.AreEqual("5m", GitHubIssues.FormatAge(now.AddMinutes(-5), now));
            Assert.AreEqual("3h", GitHubIssues.FormatAge(now.AddHours(-3), now));
            Assert.AreEqual("12d", GitHubIssues.FormatAge(now.AddDays(-12), now));
            Assert.AreEqual("3mo", GitHubIssues.FormatAge(now.AddDays(-95), now));
            Assert.AreEqual("2y", GitHubIssues.FormatAge(now.AddDays(-800), now));
        }

        #endregion

        #region Texts for git and the agent

        [TestMethod]
        public void EnsureIssueReference_AppendsUnlessAlreadyClosing()
        {
            Assert.AreEqual("Fix crash\n\nFixes #12", GitHubIssues.EnsureIssueReference("Fix crash\n", 12));
            Assert.AreEqual("Fix crash\n\nCloses #12", GitHubIssues.EnsureIssueReference("Fix crash\n\nCloses #12", 12));
            Assert.AreEqual("resolved #12 by guarding null", GitHubIssues.EnsureIssueReference("resolved #12 by guarding null", 12));
            Assert.AreEqual("Fixes #123\n\nFixes #12", GitHubIssues.EnsureIssueReference("Fixes #123", 12), "#123 is not #12.");
            Assert.AreEqual("See #12\n\nFixes #12", GitHubIssues.EnsureIssueReference("See #12", 12), "A mention is not a closing keyword.");
        }

        [TestMethod]
        public void BuildDefaultCommitMessage_CapsLength()
        {
            Assert.AreEqual("Fix #4: Short", GitHubIssues.BuildDefaultCommitMessage(new GitHubIssue { Number = 4, Title = "Short" }));

            string longMessage = GitHubIssues.BuildDefaultCommitMessage(new GitHubIssue { Number = 4, Title = new string('x', 200) });
            Assert.IsTrue(longMessage.Length <= 72);
            Assert.IsTrue(longMessage.EndsWith("…"));
        }

        [TestMethod]
        public void BuildFixPrompt_ContainsIssueAndForbidsCommitting()
        {
            var issue = new GitHubIssue
            {
                Number = 12,
                Title = "Crash on open",
                Body = "It crashes.",
                Author = "alice",
                Url = "https://github.com/o/r/issues/12",
                Comments = new List<GitHubIssueComment> { new GitHubIssueComment { Author = "bob", Body = "Me too" } }
            };

            string prompt = GitHubIssues.BuildFixPrompt(issue, "fix/issue-12", "master", resumingExistingWork: false);

            StringAssert.Contains(prompt, "#12: Crash on open");
            StringAssert.Contains(prompt, "https://github.com/o/r/issues/12");
            StringAssert.Contains(prompt, "`fix/issue-12`");
            StringAssert.Contains(prompt, "`master`");
            StringAssert.Contains(prompt, "Do not commit, push or switch branches");
            StringAssert.Contains(prompt, "It crashes.");
            StringAssert.Contains(prompt, "@bob");
            StringAssert.Contains(prompt, "Me too");
            Assert.IsFalse(prompt.Contains("git status"), "Only a resumed fix is told to look at earlier work.");

            StringAssert.Contains(GitHubIssues.BuildFixPrompt(issue, "fix/issue-12", "master", resumingExistingWork: true), "git status");
        }

        [TestMethod]
        public void BuildFixPrompt_CapsComments()
        {
            var comments = Enumerable.Range(1, 10)
                .Select(i => new GitHubIssueComment { Author = "user" + i, Body = new string('c', GitHubIssues.MaxCommentCharsInPrompt / 4) })
                .ToList();
            var issue = new GitHubIssue { Number = 1, Title = "t", Comments = comments };

            string prompt = GitHubIssues.BuildFixPrompt(issue, "fix/issue-1", "main", false);

            StringAssert.Contains(prompt, "@user4");
            Assert.IsFalse(prompt.Contains("@user5"));
            StringAssert.Contains(prompt, "6 more comments omitted");
        }

        [TestMethod]
        public void AppendMergeFootnote_UsesShortSha()
        {
            Assert.AreEqual("Thanks!\n\nMerged into `master` in abcdef1.",
                GitHubIssues.AppendMergeFootnote("Thanks!\n", "master", "abcdef1234567890"));
            Assert.AreEqual("Thanks!", GitHubIssues.AppendMergeFootnote("Thanks!", "master", null));
        }

        [TestMethod]
        public void BuildMergeCommitMessage_NamesBranchAndIssue()
        {
            Assert.AreEqual("Merge branch 'fix/issue-9': Typo (#9)",
                GitHubIssues.BuildMergeCommitMessage(new GitHubIssue { Number = 9, Title = "Typo" }, "fix/issue-9"));
        }

        [TestMethod]
        public void BuildConflictPrompt_ListsFiles()
        {
            string prompt = GitHubIssues.BuildConflictPrompt("dev", new[] { "src/a.cs", "src/b.cs" });

            StringAssert.Contains(prompt, "`dev`");
            StringAssert.Contains(prompt, "- src/a.cs");
            StringAssert.Contains(prompt, "- src/b.cs");
            StringAssert.Contains(prompt, "Do not push");
        }

        #endregion

        #region Fix stages

        [DataTestMethod]
        [DataRow(false, false, false, 0, false, false, IssueFixStage.None)]
        [DataRow(false, true, false, 0, false, false, IssueFixStage.Fixing)]   // agent working, worktree not created yet
        [DataRow(true, true, true, 2, true, false, IssueFixStage.Fixing)]      // a running turn wins over git state
        [DataRow(true, false, false, 0, false, false, IssueFixStage.Fixing)]   // worktree with no changes yet
        [DataRow(true, false, true, 0, false, false, IssueFixStage.Fixed)]
        [DataRow(true, false, true, 1, true, false, IssueFixStage.Fixed)]      // new changes on top of a pushed fix
        [DataRow(true, false, false, 1, false, false, IssueFixStage.Committed)]
        [DataRow(true, false, false, 1, true, false, IssueFixStage.Pushed)]
        [DataRow(true, false, false, 0, false, true, IssueFixStage.Merged)]    // merged, branch kept
        [DataRow(false, false, false, 0, false, true, IssueFixStage.Merged)]   // merged and cleaned up
        public void ResolveFixStage_FollowsTheFixLifecycle(bool hasFix, bool working, bool dirty, int ahead, bool pushed, bool merged, IssueFixStage expected)
        {
            Assert.AreEqual(expected, GitHubIssues.ResolveFixStage(hasFix, working, dirty, ahead, pushed, merged));
        }

        [TestMethod]
        public void ParseMergedIssueNumbers_ReadsMergeAndPullRequestSubjects()
        {
            string log = string.Join("\n",
                "Merge branch 'fix/issue-191': Issue 2 - review README.md (#191)",
                "Merge branch 'fix/issue-12' into dev",
                "Merge pull request #40 from dliedke/fix/issue-33",
                "Merge remote-tracking branch 'origin/master' into fix/issue-190",
                "Merge branch 'feature/x'",
                "Merge branch 'fix/issue-abc'");

            HashSet<int> merged = GitHubIssues.ParseMergedIssueNumbers(log);

            CollectionAssert.AreEquivalent(new[] { 191, 12, 33 }, merged.ToList());
        }

        [TestMethod]
        public void ParseMergedIssueNumbers_EmptyInput()
        {
            Assert.AreEqual(0, GitHubIssues.ParseMergedIssueNumbers(null).Count);
        }

        [TestMethod]
        public void DescribeFixStage_HasLabelsForEveryStageButNone()
        {
            Assert.AreEqual(string.Empty, GitHubIssues.DescribeFixStage(IssueFixStage.None, false));
            StringAssert.Contains(GitHubIssues.DescribeFixStage(IssueFixStage.Fixing, true), "Fixing…");
            StringAssert.Contains(GitHubIssues.DescribeFixStage(IssueFixStage.Fixed, false), "Fixed");
            StringAssert.Contains(GitHubIssues.DescribeFixStage(IssueFixStage.Committed, false), "Committed");
            StringAssert.Contains(GitHubIssues.DescribeFixStage(IssueFixStage.Pushed, false), "Pushed");
            StringAssert.Contains(GitHubIssues.DescribeFixStage(IssueFixStage.Merged, false), "Merged");
        }

        #endregion

        #region Body images

        [TestMethod]
        public void SplitImages_HtmlImage_KeepsTextOrderAndWidth()
        {
            List<IssueBodySegment> segments = GitHubIssues.SplitImages(
                "Before\r\n\r\n<img width=\"847\" height=\"736\" alt=\"Image\" src=\"https://github.com/user-attachments/assets/abc\" />\r\n\r\nAfter");

            Assert.AreEqual(3, segments.Count);
            Assert.AreEqual("Before", segments[0].Text);
            Assert.IsTrue(segments[1].IsImage);
            Assert.AreEqual("https://github.com/user-attachments/assets/abc", segments[1].ImageUrl);
            Assert.AreEqual("Image", segments[1].AltText);
            Assert.AreEqual(847d, segments[1].Width);
            Assert.AreEqual("After", segments[2].Text);
        }

        [TestMethod]
        public void SplitImages_MarkdownImageWithTitle()
        {
            List<IssueBodySegment> segments = GitHubIssues.SplitImages("See ![crash dialog](https://example.com/a.png \"The dialog\") here");

            Assert.AreEqual(3, segments.Count);
            Assert.AreEqual("See ", segments[0].Text);
            Assert.AreEqual("https://example.com/a.png", segments[1].ImageUrl);
            Assert.AreEqual("crash dialog", segments[1].AltText);
            Assert.IsNull(segments[1].Width);
            Assert.AreEqual(" here", segments[2].Text);
        }

        [TestMethod]
        public void SplitImages_LinkWrappedImage_DropsTheAnchor()
        {
            List<IssueBodySegment> segments = GitHubIssues.SplitImages(
                "<a href=\"https://example.com/full.png\"><img src='https://example.com/thumb.png' alt=\"a &amp; b\"></a>");

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual("https://example.com/thumb.png", segments[0].ImageUrl);
            Assert.AreEqual("a & b", segments[0].AltText);
        }

        [TestMethod]
        public void SplitImages_NonHttpSource_StaysText()
        {
            List<IssueBodySegment> segments = GitHubIssues.SplitImages("![local](docs/shot.png)");

            Assert.AreEqual(1, segments.Count);
            Assert.IsFalse(segments[0].IsImage);
            Assert.AreEqual("![local](docs/shot.png)", segments[0].Text);
        }

        [TestMethod]
        public void SplitImages_StripsHtmlComments()
        {
            List<IssueBodySegment> segments = GitHubIssues.SplitImages("<!-- template hint\r\n![x](https://example.com/x.png) -->Real text");

            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual("Real text", segments[0].Text);
        }

        [TestMethod]
        public void SplitImages_EmptyBody_ReturnsNothing()
        {
            Assert.AreEqual(0, GitHubIssues.SplitImages(null).Count);
            Assert.AreEqual(0, GitHubIssues.SplitImages("  \r\n ").Count);
        }

        #endregion
    }
}
