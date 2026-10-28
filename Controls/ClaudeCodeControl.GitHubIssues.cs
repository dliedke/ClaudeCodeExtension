/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "GitHub Issues" (toolbar/☰ Tools, 🐞): lists the repository's GitHub issues and closes the loop on each one —
 *          Fix with Agent creates a fix/issue-N branch in its own git worktree and opens a native chat tab working
 *          there; Commit & Push commits the fix ("Fixes #N") and pushes the fix branch; Merge merges it into the base
 *          branch (main/master/dev), pushes, posts a reply on the issue, closes it and removes the worktree/branch.
 *          Uses the GitHub CLI (gh) when installed, the public REST API for read-only listing otherwise.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Constants and types

        /// <summary>Local git commands (status, worktree, branch, merge).</summary>
        private const int IssueGitTimeoutMs = 30000;

        /// <summary>git fetch / push and gh calls — network round trips.</summary>
        private const int IssueNetworkTimeoutMs = 90000;

        /// <summary>Most issues listed per refresh.</summary>
        private const int IssueListLimit = 200;

        /// <summary>The open GitHub Issues window, so a second click brings it forward instead of opening another.</summary>
        private Window _gitHubIssuesWindow;

        /// <summary>The repository the Issues window works on.</summary>
        private sealed class GitHubRepoContext
        {
            /// <summary>Main working copy (not a worktree), where worktrees and branches are managed.</summary>
            public string RepoRoot;
            public string RemoteName;
            public string Owner;
            public string Repo;
            public string Slug => Owner + "/" + Repo;
        }

        /// <summary>Local git state of one issue's fix, loaded when the issue is selected.</summary>
        private sealed class IssueLocalInfo
        {
            public string Branch;
            public bool BranchExists;
            public string WorktreePath;
            public bool WorktreeExists;
            public bool Dirty;
            public int Ahead;
            public bool RemoteBranchExists;
            public bool Pushed;
            public string BaseBranch;
        }

        #endregion

        #region Entry point

#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void GitHubIssuesButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ShowGitHubIssuesWindowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHubIssuesButton_Click error: {ex.Message}");
                MessageBox.Show($"Could not open GitHub Issues: {ex.Message}", "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ShowGitHubIssuesWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_gitHubIssuesWindow != null)
            {
                if (_gitHubIssuesWindow.WindowState == WindowState.Minimized)
                    _gitHubIssuesWindow.WindowState = WindowState.Normal;
                _gitHubIssuesWindow.Activate();
                return;
            }

            GitHubRepoContext context = await ResolveGitHubRepoContextAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (context == null)
            {
                MessageBox.Show(
                    "No GitHub repository was found for the open solution. GitHub Issues needs a git repository with a github.com remote.",
                    "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _gitHubIssuesWindow = BuildGitHubIssuesWindow(context);
            _gitHubIssuesWindow.Closed += (s, e) => _gitHubIssuesWindow = null;
            _gitHubIssuesWindow.Show();
        }

        /// <summary>
        /// Finds the solution's repository, its main working copy (when the solution itself was opened
        /// from a worktree) and the first remote pointing at github.com — origin first.
        /// </summary>
        private async Task<GitHubRepoContext> ResolveGitHubRepoContextAsync()
        {
            string workspace = await GetWorkspaceDirectoryAsync();

            return await Task.Run(() =>
            {
                string root = FindGitRepositoryRoot(workspace);
                if (string.IsNullOrEmpty(root))
                    return null;

                string mainRoot = root;
                GitCommandResult common = RunGitDetailed(root, "rev-parse --git-common-dir", IssueGitTimeoutMs);
                if (common != null && common.ExitCode == 0)
                {
                    string commonDir = GitHubIssues.NormalizeGitPath(common.StdOut.Trim());
                    if (!Path.IsPathRooted(commonDir))
                        commonDir = Path.GetFullPath(Path.Combine(root, commonDir));
                    if (string.Equals(Path.GetFileName(commonDir.TrimEnd('\\')), ".git", StringComparison.OrdinalIgnoreCase))
                        mainRoot = Path.GetDirectoryName(commonDir.TrimEnd('\\'));
                }

                GitCommandResult remotes = RunGitDetailed(mainRoot, "remote", IssueGitTimeoutMs);
                if (remotes == null || remotes.ExitCode != 0)
                    return null;

                var names = remotes.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(n => n.Trim())
                    .OrderBy(n => n == "origin" ? 0 : n == "upstream" ? 1 : 2)
                    .ToList();

                foreach (string name in names)
                {
                    GitCommandResult url = RunGitDetailed(mainRoot, "remote get-url " + name, IssueGitTimeoutMs);
                    if (url != null && url.ExitCode == 0 &&
                        GitHubIssues.TryParseGitHubRemote(url.StdOut.Trim(), out string owner, out string repo))
                    {
                        return new GitHubRepoContext { RepoRoot = mainRoot, RemoteName = name, Owner = owner, Repo = repo };
                    }
                }

                return null;
            });
        }

        #endregion

        #region Window

        private Window BuildGitHubIssuesWindow(GitHubRepoContext context)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);
            var comboRes = BuildThemedComboResources(themeBg, themeFg);
            Style buttonStyle = GetDialogButtonStyle();

            var window = new Window
            {
                Title = $"GitHub Issues — {context.Slug}",
                Width = 1040,
                Height = 640,
                MinWidth = 720,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { window.Owner = Application.Current?.MainWindow; } catch { }

            // The shared button template ignores Padding, so the caption is padded through its template
            // instead (survives the captions changed later, e.g. "Open Fix Chat" / "Merge into …").
            var captionFactory = new FrameworkElementFactory(typeof(TextBlock));
            captionFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            captionFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 0, 14, 0));
            var paddedCaption = new DataTemplate { VisualTree = captionFactory };

            Func<string, Button> mkBtn = text =>
            {
                var b = new Button { Content = text, MinWidth = 88, Height = 30, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0), ContentTemplate = paddedCaption };
                if (buttonStyle != null) b.Style = buttonStyle;
                else { b.Background = themeBg; b.Foreground = themeFg; b.BorderBrush = themeFg; }
                return b;
            };
            Func<string, TextBlock> mkLabel = text => new TextBlock
            {
                Text = text,
                Foreground = themeFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 6, 0)
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // --- Row 0: base branch, state, search, refresh ---
            var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };

            var leftTop = new StackPanel { Orientation = Orientation.Horizontal };
            var baseLabel = mkLabel("Base branch:");
            baseLabel.Margin = new Thickness(0, 0, 6, 0);
            leftTop.Children.Add(baseLabel);
            var baseCombo = MakeThemedComboBox(comboRes, themeFg);
            baseCombo.MinWidth = 140;
            baseCombo.ToolTip = "Branch a new fix starts from, and the branch Merge merges it into. A fix keeps the branch it started from.";
            leftTop.Children.Add(baseCombo);

            leftTop.Children.Add(mkLabel("Show:"));
            var stateCombo = MakeThemedComboBox(comboRes, themeFg);
            stateCombo.MinWidth = 90;
            foreach (string state in new[] { "Open", "Closed", "All" })
            {
                var item = new ComboBoxItem { Content = state, Tag = state.ToLowerInvariant(), Foreground = themeFg };
                if (comboRes["cbi"] is Style cbi) item.Style = cbi;
                stateCombo.Items.Add(item);
            }
            stateCombo.SelectedIndex = 0;
            leftTop.Children.Add(stateCombo);
            leftTop.Children.Add(mkLabel("Filter:"));
            DockPanel.SetDock(leftTop, Dock.Left);
            topBar.Children.Add(leftTop);

            var refreshButton = mkBtn("Refresh");
            DockPanel.SetDock(refreshButton, Dock.Right);
            topBar.Children.Add(refreshButton);

            var searchBox = new TextBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Filter by number, title, label or author."
            };
            topBar.Children.Add(searchBox);

            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // --- Row 1: issue list | details ---
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 300 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });

            var listBox = new ListBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 5)));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            listBox.ItemContainerStyle = itemStyle;
            Grid.SetColumn(listBox, 0);
            body.Children.Add(listBox);

            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(splitter, 1);
            body.Children.Add(splitter);

            // The chat's markdown viewer, given a document built here (issue + comments + images)
            // instead of its Markdown property. Its style properties are set before any document is
            // assigned: each one rebuilds the (empty) Markdown and would replace the issue's document.
            bool darkTheme = IsDarkBrush(themeBg);
            Color fgColor = (themeFg as SolidColorBrush)?.Color ?? Colors.Gray;
            var detailView = new MarkdownBlock
            {
                Foreground = themeFg,
                AccentBrush = new SolidColorBrush(darkTheme ? Color.FromRgb(0x4D, 0xAA, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8)),
                CodeBackground = new SolidColorBrush(Color.FromArgb(0x18, fgColor.R, fgColor.G, fgColor.B)),
                CodeBorderBrush = new SolidColorBrush(Color.FromArgb(0x40, fgColor.R, fgColor.G, fgColor.B)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(4)
            };
            detailView.LinkClicked += (s, url) =>
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    OpenUrl(url);
            };
            var detailBorder = new Border { BorderBrush = themeFg, BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = detailView };
            Grid.SetColumn(detailBorder, 2);
            body.Children.Add(detailBorder);

            // Downloaded images by URL, shared by every render in this window (the details re-render
            // when comments arrive, and re-selecting an issue must not download again).
            var imageCache = new Dictionary<string, AsyncLazy<BitmapSource>>(StringComparer.Ordinal);
            Brush mutedBrush = new SolidColorBrush(Color.FromArgb(0xA8, fgColor.R, fgColor.G, fgColor.B));
            Brush ruleBrush = new SolidColorBrush(Color.FromArgb(0x40, fgColor.R, fgColor.G, fgColor.B));

            var loading = new TextBlock
            {
                Text = "Loading issues…",
                Foreground = themeFg,
                FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75
            };
            Grid.SetColumn(loading, 0);
            body.Children.Add(loading);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // --- Row 2: selected issue's local state / progress ---
            var statusText = new TextBlock
            {
                Foreground = themeFg,
                Opacity = 0.85,
                Margin = new Thickness(0, 8, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(statusText, 2);
            root.Children.Add(statusText);

            // --- Row 3: actions ---
            var openOnGitHubButton = mkBtn("Open on GitHub");
            var fixButton = mkBtn("Fix with Agent");
            var openInVsButton = mkBtn("Open in VS");
            var commitButton = mkBtn("Commit & Push");
            var mergeButton = mkBtn("Merge…");
            var discardButton = mkBtn("Discard Fix");
            var closeButton = mkBtn("Close");
            closeButton.IsCancel = true;

            fixButton.ToolTip = "Create branch fix/issue-N in its own worktree and open a chat tab that works on the issue there.";
            openInVsButton.ToolTip = "Open the fix's worktree in a new Visual Studio window to build and test it.";
            commitButton.ToolTip = "Commit the fix (with \"Fixes #N\") and push the fix branch.";
            mergeButton.ToolTip = "Merge the fix into its base branch, push, reply on the issue, close it and clean up.";
            discardButton.ToolTip = "Close the issue's chat tab and delete its worktree and fix branch.";

            var buttonBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
            openOnGitHubButton.Margin = new Thickness(0);
            DockPanel.SetDock(openOnGitHubButton, Dock.Left);
            buttonBar.Children.Add(openOnGitHubButton);
            var rightActions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(rightActions, Dock.Right);
            rightActions.Children.Add(fixButton);
            rightActions.Children.Add(openInVsButton);
            rightActions.Children.Add(commitButton);
            rightActions.Children.Add(mergeButton);
            rightActions.Children.Add(discardButton);
            rightActions.Children.Add(closeButton);
            buttonBar.Children.Add(rightActions);
            Grid.SetRow(buttonBar, 3);
            root.Children.Add(buttonBar);

            window.Content = root;

            // --- State ---
            var issues = new List<GitHubIssue>();
            List<GitWorktreeEntry> worktrees = new List<GitWorktreeEntry>();
            var localBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IssueLocalInfo selectedInfo = null;
            bool busy = false;
            int selectionVersion = 0;
            string ghPath = ResolveGhPath();

            // Local fix state of every issue that has a fix branch or worktree, and the issues whose fix
            // branch shows up in a merge commit — together they give each row its stage.
            var fixInfos = new Dictionary<int, IssueLocalInfo>();
            var mergedIssues = new HashSet<int>();
            var rowMeta = new Dictionary<int, TextBlock>();
            int pendingSelection = 0;     // issue to re-select when the list is rebuilt
            int shownIssueNumber = 0;     // issue in the details pane (a re-render of it keeps the scroll position)
            bool rebuildingList = false;
            string lastSessionSignature = string.Empty;
            DateTime lastLocalCheck = DateTime.MinValue;

            Func<GitHubIssue> selectedIssue = () => (listBox.SelectedItem as ListBoxItem)?.Tag as GitHubIssue;
            Func<string> selectedBase = () => (baseCombo.SelectedItem as ComboBoxItem)?.Tag as string;

            Action updateButtons = () =>
            {
                GitHubIssue issue = selectedIssue();
                IssueLocalInfo info = selectedInfo;
                bool has = issue != null && !busy;
                bool hasWorktree = has && info != null && info.WorktreeExists;
                bool hasBranch = has && info != null && info.BranchExists;

                openOnGitHubButton.IsEnabled = issue != null && !string.IsNullOrEmpty(issue.Url);
                fixButton.IsEnabled = has && !string.IsNullOrEmpty(selectedBase());
                fixButton.Content = hasWorktree ? "Open Fix Chat" : "Fix with Agent";
                openInVsButton.IsEnabled = hasWorktree;
                commitButton.IsEnabled = hasWorktree && (info.Dirty || (info.Ahead > 0 && !info.Pushed));
                mergeButton.IsEnabled = hasWorktree && !info.Dirty && info.Ahead > 0;
                mergeButton.Content = info != null && !string.IsNullOrEmpty(info.BaseBranch)
                    ? "Merge into " + info.BaseBranch + "…"
                    : "Merge…";
                discardButton.IsEnabled = hasWorktree || hasBranch;
                refreshButton.IsEnabled = !busy;
                baseCombo.IsEnabled = !busy;
            };

            Func<Dictionary<int, IssueLocalInfo>, HashSet<int>, int, IssueFixStage> computeStage = (infos, merged, number) =>
            {
                infos.TryGetValue(number, out IssueLocalInfo info);
                bool working = FindIssueSession(number)?.TurnInFlight == true;
                bool hasFix = info != null && (info.BranchExists || info.WorktreeExists);
                return GitHubIssues.ResolveFixStage(hasFix, working, info?.Dirty == true, info?.Ahead ?? 0, info?.Pushed == true, merged.Contains(number));
            };

            Func<GitHubIssue, string> stageLabel = issue =>
            {
                NativeChatSessionState session = FindIssueSession(issue.Number);
                bool working = session?.TurnInFlight == true;
                string label = GitHubIssues.DescribeFixStage(computeStage(fixInfos, mergedIssues, issue.Number), working);
                if (session != null && !working)
                    label = label.Length > 0 ? label + "  ·  💬 chat open" : "💬 chat open";
                return label;
            };

            // Which issue chats exist and whether their agent is mid-turn: a change here (a turn
            // starting or finishing, a tab opened or closed) means the fix stages may have moved.
            Func<string> sessionSignature = () => string.Join(";", issues
                .Select(i => new { i.Number, Session = FindIssueSession(i.Number) })
                .Where(x => x.Session != null)
                .Select(x => x.Number.ToString(CultureInfo.InvariantCulture) + (x.Session.TurnInFlight ? "w" : "i")));

            Func<GitHubIssue, string> metaText = issue =>
            {
                var metaParts = new List<string>();
                if (!issue.IsOpen) metaParts.Add("closed");
                if (issue.Labels.Count > 0) metaParts.Add(string.Join(", ", issue.Labels));
                if (!string.IsNullOrEmpty(issue.Author)) metaParts.Add("@" + issue.Author);
                string age = GitHubIssues.FormatAge(issue.UpdatedAt, DateTime.UtcNow);
                if (age.Length > 0) metaParts.Add("updated " + age);
                if (issue.CommentCount > 0) metaParts.Add(issue.CommentCount + " 💬");
                string status = stageLabel(issue);
                if (status.Length > 0) metaParts.Add(status);
                return string.Join("  ·  ", metaParts);
            };

            Action applyFilter = () =>
            {
                int keep = selectedIssue()?.Number ?? pendingSelection;
                pendingSelection = 0;
                string query = (searchBox.Text ?? string.Empty).Trim();

                rebuildingList = true;
                listBox.Items.Clear();
                rowMeta.Clear();
                foreach (GitHubIssue issue in issues)
                {
                    if (!GitHubIssues.MatchesFilter(issue, query))
                        continue;

                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var number = new TextBlock
                    {
                        Text = "#" + issue.Number.ToString(CultureInfo.InvariantCulture),
                        Foreground = themeFg,
                        FontWeight = FontWeights.SemiBold,
                        Opacity = issue.IsOpen ? 1.0 : 0.6,
                        Margin = new Thickness(0, 0, 8, 0),
                        MinWidth = 44
                    };
                    row.Children.Add(number);

                    var title = new TextBlock
                    {
                        Text = issue.Title,
                        Foreground = themeFg,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Opacity = issue.IsOpen ? 1.0 : 0.6
                    };
                    Grid.SetColumn(title, 1);
                    row.Children.Add(title);

                    var meta = new TextBlock
                    {
                        Text = metaText(issue),
                        Foreground = themeFg,
                        Opacity = 0.65,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    Grid.SetColumn(meta, 1);
                    Grid.SetRow(meta, 1);
                    row.Children.Add(meta);
                    rowMeta[issue.Number] = meta;

                    var item = new ListBoxItem { Content = row, Tag = issue, Foreground = themeFg };
                    listBox.Items.Add(item);
                    if (issue.Number == keep)
                        listBox.SelectedItem = item;
                }
                rebuildingList = false;

                loading.Visibility = listBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (listBox.Items.Count == 0 && issues.Count > 0)
                    loading.Text = "No issues match the filter.";
            };

            Func<string, Paragraph> mutedParagraph = text => new Paragraph(new Run(text)) { Foreground = mutedBrush, Margin = new Thickness(0, 0, 0, 6) };

            // Markdown text through the chat renderer, images as scaled inline pictures.
            Action<FlowDocument, string> appendBody = (document, markdown) =>
            {
                var options = new MarkdownStyleOptions
                {
                    BaseFontSize = detailView.FontSize,
                    CodeFontFamily = detailView.CodeFontFamily,
                    CodeBackground = detailView.CodeBackground,
                    CodeBorderBrush = detailView.CodeBorderBrush,
                    AccentBrush = detailView.AccentBrush,
                    MutedBrush = mutedBrush,
                    TextBrush = themeFg
                };

                foreach (IssueBodySegment segment in GitHubIssues.SplitImages(markdown))
                {
                    if (segment.IsImage)
                    {
                        document.Blocks.Add(BuildIssueImageBlock(segment, imageCache, mutedBrush, detailView.AccentBrush));
                        continue;
                    }

                    FlowDocument part = MarkdownFlowRenderer.Build(segment.Text, options);
                    foreach (Block block in part.Blocks.ToList())
                    {
                        part.Blocks.Remove(block);
                        document.Blocks.Add(block);
                    }
                }
            };

            Func<GitHubIssue, FlowDocument> renderDetail = issue =>
            {
                var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = detailView.FontSize, Foreground = themeFg };

                var title = new Paragraph { Margin = new Thickness(0, 0, 0, 2), FontSize = detailView.FontSize * 1.25 };
                title.Inlines.Add(new Bold(new Run("#" + issue.Number.ToString(CultureInfo.InvariantCulture) + "  " + issue.Title)));
                document.Blocks.Add(title);

                var meta = new List<string> { issue.IsOpen ? "Open" : "Closed" };
                if (!string.IsNullOrEmpty(issue.Author)) meta.Add("@" + issue.Author);
                if (issue.CreatedAt.HasValue) meta.Add("opened " + issue.CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (issue.Labels.Count > 0) meta.Add(string.Join(", ", issue.Labels));
                string stage = stageLabel(issue);
                if (stage.Length > 0) meta.Add(stage);
                Paragraph metaParagraph = mutedParagraph(string.Join("  ·  ", meta));
                metaParagraph.Margin = new Thickness(0, 0, 0, 10);
                document.Blocks.Add(metaParagraph);

                if (string.IsNullOrWhiteSpace(issue.Body))
                    document.Blocks.Add(mutedParagraph("(no description)"));
                else
                    appendBody(document, issue.Body);

                if (issue.Comments == null)
                {
                    document.Blocks.Add(mutedParagraph("Loading comments…"));
                }
                else
                {
                    foreach (GitHubIssueComment comment in issue.Comments)
                    {
                        var header = new Paragraph
                        {
                            Margin = new Thickness(0, 12, 0, 4),
                            Padding = new Thickness(0, 8, 0, 0),
                            BorderBrush = ruleBrush,
                            BorderThickness = new Thickness(0, 1, 0, 0)
                        };
                        header.Inlines.Add(new Bold(new Run("@" + (string.IsNullOrEmpty(comment.Author) ? "unknown" : comment.Author))));
                        if (comment.CreatedAt.HasValue)
                            header.Inlines.Add(new Run("  ·  " + comment.CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) { Foreground = mutedBrush });
                        document.Blocks.Add(header);
                        appendBody(document, comment.Body);
                    }
                }

                return document;
            };

            // A different issue starts at the top; a re-render of the one on screen (comments arrived,
            // stage changed, the form refreshed) keeps the reader's place.
            Action<int, FlowDocument> showDetail = (number, document) =>
            {
                bool same = number != 0 && number == shownIssueNumber;
                double offset = detailView.VerticalOffset;
                shownIssueNumber = number;
                detailView.Document = document;
                if (same) detailView.ScrollToVerticalOffset(offset);
                else detailView.ScrollToHome();
            };

            Action<IssueLocalInfo> renderStatus = info =>
            {
                if (info == null)
                {
                    statusText.Text = string.Empty;
                    return;
                }

                if (!info.BranchExists && !info.WorktreeExists)
                {
                    statusText.Text = "No fix yet. Fix with Agent starts one from " + (selectedBase() ?? "the base branch") + ".";
                    return;
                }

                var parts = new List<string> { "Branch " + info.Branch + (string.IsNullOrEmpty(info.BaseBranch) ? string.Empty : " (from " + info.BaseBranch + ")") };
                parts.Add(info.WorktreeExists ? "worktree " + info.WorktreePath : "no worktree");
                if (info.WorktreeExists) parts.Add(info.Dirty ? "uncommitted changes" : "clean");
                parts.Add(info.Ahead + " commit" + (info.Ahead == 1 ? string.Empty : "s") + " ahead");
                parts.Add(info.Pushed ? "pushed" : info.RemoteBranchExists ? "push pending" : "not pushed");
                statusText.Text = string.Join("  ·  ", parts);
            };

            Func<Task> refreshSelection = async () =>
            {
                int version = ++selectionVersion;
                GitHubIssue issue = selectedIssue();
                selectedInfo = null;
                updateButtons();
                if (issue == null)
                {
                    var empty = new FlowDocument { PagePadding = new Thickness(0), Foreground = themeFg };
                    empty.Blocks.Add(mutedParagraph("Select an issue."));
                    showDetail(0, empty);
                    statusText.Text = string.Empty;
                    return;
                }

                showDetail(issue.Number, renderDetail(issue));
                statusText.Text = "Checking local state…";

                string defaultBase = selectedBase();
                IssueLocalInfo info = await Task.Run(() => LoadIssueLocalInfo(context, issue.Number, defaultBase));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (version != selectionVersion) return;
                selectedInfo = info;
                renderStatus(info);
                updateButtons();

                // The fresh local state may move this issue's stage (e.g. the user committed in the
                // worktree by hand): update its row and the details header in place.
                string stageBefore = stageLabel(issue);
                if (info.BranchExists || info.WorktreeExists) fixInfos[issue.Number] = info;
                else fixInfos.Remove(issue.Number);
                bool stageChanged = stageLabel(issue) != stageBefore;
                if (stageChanged && rowMeta.TryGetValue(issue.Number, out TextBlock rowText))
                    rowText.Text = metaText(issue);

                if (issue.Comments == null)
                {
                    List<GitHubIssueComment> comments = await FetchIssueCommentsAsync(context, ghPath, issue.Number);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    issue.Comments = comments ?? new List<GitHubIssueComment>();
                    stageChanged = true;
                }

                if (stageChanged && version == selectionVersion)
                    showDetail(issue.Number, renderDetail(issue));
            };

            // Reads the local fix state of every issue with a fix branch or worktree, plus the merge
            // history (any local or remote-tracking branch). Does not touch the UI.
            Func<Task<(Dictionary<int, IssueLocalInfo> Infos, HashSet<int> Merged)>> readFixStates = async () =>
            {
                string defaultBase = selectedBase();
                string worktreesFolder = Path.GetDirectoryName(GitHubIssues.BuildWorktreePath(context.RepoRoot, 0));
                var numbers = new HashSet<int>();
                foreach (string b in localBranches)
                {
                    int n = GitHubIssues.ParseIssueNumberFromBranch(b);
                    if (n > 0) numbers.Add(n);
                }
                foreach (GitWorktreeEntry w in worktrees)
                {
                    int n = GitHubIssues.ParseIssueNumberFromBranch(w.Branch);
                    // A worktree left detached by an interrupted merge is recognized by its folder.
                    if (n == 0 && string.Equals(Path.GetDirectoryName(w.Path.TrimEnd('\\')), worktreesFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileName(w.Path.TrimEnd('\\'));
                        if (name.StartsWith("issue-", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(name.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out n);
                    }
                    if (n > 0) numbers.Add(n);
                }

                return await Task.Run(() =>
                {
                    var infos = numbers.ToDictionary(n => n, n => LoadIssueLocalInfo(context, n, defaultBase));
                    GitCommandResult log = RunGitDetailed(context.RepoRoot,
                        $"log --branches --remotes={context.RemoteName} --merges --format=%s -n 2000", IssueGitTimeoutMs);
                    return (infos, GitHubIssues.ParseMergedIssueNumbers(log != null && log.ExitCode == 0 ? log.StdOut : null));
                });
            };

            Func<Task> loadBranches = async () =>
            {
                var result = await Task.Run(() => new
                {
                    Branches = RunGitDetailed(context.RepoRoot, "for-each-ref --format=%(refname:short) refs/heads/", IssueGitTimeoutMs),
                    Current = RunGitDetailed(context.RepoRoot, "rev-parse --abbrev-ref HEAD", IssueGitTimeoutMs)
                });
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string previous = selectedBase();
                List<string> branches = (result.Branches?.StdOut ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(b => b.Trim())
                    .Where(b => b.Length > 0)
                    .ToList();

                localBranches.Clear();
                foreach (string b in branches) localBranches.Add(b);

                List<string> bases = branches.Where(b => GitHubIssues.ParseIssueNumberFromBranch(b) == 0).ToList();
                string current = result.Current?.ExitCode == 0 ? result.Current.StdOut.Trim() : null;
                string preferred = previous;
                if (string.IsNullOrEmpty(preferred) || !bases.Contains(preferred))
                {
                    preferred = !string.IsNullOrEmpty(current) && bases.Contains(current)
                        ? current
                        : new[] { "main", "master", "dev", "develop" }.FirstOrDefault(bases.Contains) ?? bases.FirstOrDefault();
                }

                baseCombo.Items.Clear();
                foreach (string b in bases)
                {
                    var item = new ComboBoxItem { Content = b, Tag = b, Foreground = themeFg };
                    if (comboRes["cbi"] is Style cbi) item.Style = cbi;
                    baseCombo.Items.Add(item);
                    if (b == preferred) baseCombo.SelectedItem = item;
                }
            };

            // fetchIssues: list the issues from GitHub again (fresh state, labels, comments).
            // keepSelected: an automatic refresh after an action or a stage change — the list stays on
            // screen while it reloads, and the selected issue stays listed even when it left the current
            // view (closed while showing "Open") so its final state and the reply can be read.
            Func<bool, bool, Task> reload = async (fetchIssues, keepSelected) =>
            {
                if (busy) return;
                busy = true;
                updateButtons();
                int keepNumber = selectedIssue()?.Number ?? 0;
                lastSessionSignature = sessionSignature();
                try
                {
                    await loadBranches();
                    worktrees = await Task.Run(() =>
                    {
                        GitCommandResult list = RunGitDetailed(context.RepoRoot, "worktree list --porcelain", IssueGitTimeoutMs);
                        return list != null && list.ExitCode == 0
                            ? GitHubIssues.ParseWorktreeList(list.StdOut)
                            : new List<GitWorktreeEntry>();
                    });

                    if (fetchIssues)
                    {
                        if (!keepSelected || issues.Count == 0)
                        {
                            loading.Text = "Loading issues…";
                            loading.Visibility = Visibility.Visible;
                            rebuildingList = true;
                            listBox.Items.Clear();
                            rebuildingList = false;
                        }

                        string state = (stateCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "open";
                        var (loaded, error) = await FetchIssuesAsync(context, ghPath, state);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        List<GitHubIssue> fresh = loaded ?? new List<GitHubIssue>();

                        if (keepSelected && keepNumber > 0 && error == null)
                        {
                            GitHubIssue kept = fresh.FirstOrDefault(i => i.Number == keepNumber);
                            if (kept == null)
                            {
                                kept = await FetchSingleIssueAsync(context, ghPath, keepNumber);
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                                if (kept != null)
                                {
                                    int at = issues.FindIndex(i => i.Number == keepNumber);
                                    fresh.Insert(Math.Max(0, Math.Min(at < 0 ? 0 : at, fresh.Count)), kept);
                                }
                            }

                            // Comments now, so the selected issue is shown complete in one render.
                            if (kept != null && kept.Comments == null)
                            {
                                kept.Comments = await FetchIssueCommentsAsync(context, ghPath, keepNumber);
                                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            }
                        }

                        issues = fresh;
                        if (error != null)
                        {
                            loading.Text = error;
                            loading.Visibility = Visibility.Visible;
                        }
                        else if (issues.Count == 0)
                        {
                            loading.Text = "No " + (state == "all" ? string.Empty : state + " ") + "issues.";
                        }
                    }

                    var states = await readFixStates();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    fixInfos = states.Infos;
                    mergedIssues = states.Merged;

                    pendingSelection = keepNumber;
                    applyFilter();
                    if (issues.Count > 0 && loading.Visibility == Visibility.Visible && listBox.Items.Count > 0)
                        loading.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitHub Issues reload error: {ex.Message}");
                    loading.Text = "Could not load issues: " + ex.Message;
                    loading.Visibility = Visibility.Visible;
                }
                finally
                {
                    busy = false;
                }

                await refreshSelection();
            };

            // Runs one action with the buttons disabled and progress in the status line; any exception
            // ends up in a message box instead of escaping a void event handler.
            Func<string, Func<Action<string>, Task>, Task> runAction = async (caption, action) =>
            {
                if (busy) return;
                busy = true;
                updateButtons();
                Action<string> progress = text => statusText.Text = text;
                try
                {
                    await action(progress);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitHub Issues {caption} error: {ex}");
                    MessageBox.Show(window, $"{caption} failed: {ex.Message}", "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    busy = false;
                }

                // An action can change everything — the issue's state, its comments (the reply), the
                // fix stage — so the whole form is refreshed from GitHub and git.
                await reload(true, true);
            };

            // Re-reads the local fix state quietly; only a stage that actually moved (a commit made by
            // hand, a worktree changed outside the window) refreshes the form.
            Func<Task> checkLocalChanges = async () =>
            {
                if (busy || issues.Count == 0) return;
                lastLocalCheck = DateTime.UtcNow;
                var before = issues.ToDictionary(i => i.Number, i => computeStage(fixInfos, mergedIssues, i.Number));
                var states = await readFixStates();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (busy) return;
                if (issues.Any(i => !before.TryGetValue(i.Number, out IssueFixStage old) || old != computeStage(states.Infos, states.Merged, i.Number)))
                    await reload(true, true);
            };

            // An agent turn starting or finishing in an issue's chat (or the chat opening/closing)
            // moves that issue's stage: refresh the form when it happens.
            var watchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            watchTimer.Tick += (s, e) =>
            {
                if (busy || !window.IsLoaded || issues.Count == 0) return;
                if (sessionSignature() != lastSessionSignature)
                    StartSessionHistoryTask(() => reload(true, true), "claudecode/githubissues/watch");
            };
            window.Closed += (s, e) => watchTimer.Stop();
            window.Activated += (s, e) =>
            {
                if (window.IsLoaded && !busy && (DateTime.UtcNow - lastLocalCheck).TotalSeconds >= 3)
                    StartSessionHistoryTask(checkLocalChanges, "claudecode/githubissues/activated");
            };

            // --- Wiring ---
            listBox.SelectionChanged += (s, e) =>
            {
                if (!rebuildingList) StartSessionHistoryTask(refreshSelection, "claudecode/githubissues/select");
            };
            searchBox.TextChanged += (s, e) =>
            {
                int before = selectedIssue()?.Number ?? 0;
                applyFilter();
                if ((selectedIssue()?.Number ?? 0) != before)
                    StartSessionHistoryTask(refreshSelection, "claudecode/githubissues/filter");
            };
            stateCombo.SelectionChanged += (s, e) =>
            {
                if (window.IsLoaded) StartSessionHistoryTask(() => reload(true, false), "claudecode/githubissues/state");
            };
            baseCombo.SelectionChanged += (s, e) =>
            {
                if (!busy && window.IsLoaded) StartSessionHistoryTask(refreshSelection, "claudecode/githubissues/base");
            };
            refreshButton.Click += (s, e) => StartSessionHistoryTask(() => reload(true, false), "claudecode/githubissues/refresh");
            closeButton.Click += (s, e) => window.Close();

            openOnGitHubButton.Click += (s, e) =>
            {
                GitHubIssue issue = selectedIssue();
                if (!string.IsNullOrEmpty(issue?.Url)) OpenUrl(issue.Url);
            };

            fixButton.Click += (s, e) =>
            {
                GitHubIssue issue = selectedIssue();
                string baseBranch = selectedInfo?.BranchExists == true && !string.IsNullOrEmpty(selectedInfo.BaseBranch)
                    ? selectedInfo.BaseBranch
                    : selectedBase();
                if (issue == null || string.IsNullOrEmpty(baseBranch)) return;
                StartSessionHistoryTask(() => runAction("Fix with Agent", p => FixIssueWithAgentAsync(window, context, ghPath, issue, baseBranch, p)),
                    "claudecode/githubissues/fix");
            };

            listBox.MouseDoubleClick += (s, e) =>
            {
                if (fixButton.IsEnabled && listBox.SelectedItem != null)
                    fixButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };

            openInVsButton.Click += (s, e) =>
            {
                if (selectedInfo?.WorktreeExists == true)
                    OpenWorktreeInNewVisualStudio(window, context, selectedInfo.WorktreePath);
            };

            commitButton.Click += (s, e) =>
            {
                GitHubIssue issue = selectedIssue();
                IssueLocalInfo info = selectedInfo;
                if (issue == null || info == null) return;
                StartSessionHistoryTask(() => runAction("Commit & Push", p => CommitAndPushIssueFixAsync(window, context, issue, info, p)),
                    "claudecode/githubissues/commit");
            };

            mergeButton.Click += (s, e) =>
            {
                GitHubIssue issue = selectedIssue();
                IssueLocalInfo info = selectedInfo;
                if (issue == null || info == null) return;
                StartSessionHistoryTask(() => runAction("Merge", p => MergeIssueFixAsync(window, context, ghPath, issue, info, p)),
                    "claudecode/githubissues/merge");
            };

            discardButton.Click += (s, e) =>
            {
                GitHubIssue issue = selectedIssue();
                IssueLocalInfo info = selectedInfo;
                if (issue == null || info == null) return;
                StartSessionHistoryTask(() => runAction("Discard Fix", p => DiscardIssueFixAsync(window, context, issue, info, p)),
                    "claudecode/githubissues/discard");
            };

            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F5)
                {
                    e.Handled = true;
                    StartSessionHistoryTask(() => reload(true, false), "claudecode/githubissues/f5");
                }
            };

            window.Loaded += (s, e) =>
            {
                if (string.IsNullOrEmpty(ghPath))
                {
                    statusText.Text = "GitHub CLI (gh) not found — issues are listed read-only from the public API; replies and closing need gh.";
                }
                StartSessionHistoryTask(() => reload(true, false), "claudecode/githubissues/load");
                watchTimer.Start();
                searchBox.Focus();
            };

            updateButtons();
            return window;
        }

        #endregion

        #region Fix

        /// <summary>
        /// Opens (or brings back) the fix chat for an issue: creates <c>fix/issue-N</c> from the latest
        /// base in its own worktree when it doesn't exist yet, then starts a parallel native chat tab
        /// running in that worktree and hands it the issue.
        /// </summary>
        private async Task FixIssueWithAgentAsync(Window owner, GitHubRepoContext context, string ghPath, GitHubIssue issue, string baseBranch, Action<string> progress)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            NativeChatSessionState existing = FindIssueSession(issue.Number);
            if (existing != null)
            {
                BringSessionTabToFront(existing);
                return;
            }

            if (!IsNativeModeActive)
            {
                MessageBox.Show(owner,
                    "Fixing an issue opens a chat tab for it, which needs Native Mode. Enable it in ⚙ Settings... → Terminal → Use native mode.",
                    "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (issue.Comments == null)
            {
                progress("Loading the issue's comments…");
                issue.Comments = await FetchIssueCommentsAsync(context, ghPath, issue.Number);
            }

            // Asked before the worktree exists, so Cancel leaves nothing behind. The advisor sizes the
            // prompt the agent will actually get.
            AiProvider provider = _currentRunningProvider ?? _settings.SelectedProvider;
            progress("Choosing the model…");
            IssueAgentChoice agentChoice = ShowIssueAgentDialog(owner, provider, issue,
                GitHubIssues.BuildFixPrompt(issue, GitHubIssues.BuildBranchName(issue.Number), baseBranch, false));
            if (agentChoice == null)
                return;

            progress("Preparing the worktree…");
            var prepared = await Task.Run(() => PrepareIssueWorktree(context, issue.Number, baseBranch));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (prepared.Error != null)
            {
                MessageBox.Show(owner, prepared.Error, "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            progress("Starting the agent…");
            string worktree = prepared.Path;
            NativeChatSessionState session = await StartParallelSessionAsync(provider, worktree, null, s =>
            {
                s.WorkspaceOverride = worktree;
                s.IssueNumber = issue.Number;
                s.IssueTitle = issue.Title;
                agentChoice.ApplyTo(s);
            });

            if (session == null)
            {
                MessageBox.Show(owner, "The agent session could not be created.", "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string prompt = GitHubIssues.BuildFixPrompt(issue, GitHubIssues.BuildBranchName(issue.Number), prepared.BaseBranch, prepared.Resumed);
            StartIssuePrompt(session, prompt);
        }

        /// <summary>
        /// Makes sure the issue's worktree exists on <c>fix/issue-N</c>. A new branch starts from the
        /// freshly fetched remote base (local base when there is no remote copy) and records that base
        /// in git config for Merge. Background thread.
        /// </summary>
        private (string Path, string BaseBranch, bool Resumed, string Error) PrepareIssueWorktree(GitHubRepoContext context, int issueNumber, string baseBranch)
        {
            string repo = context.RepoRoot;
            string branch = GitHubIssues.BuildBranchName(issueNumber);

            RunGitDetailed(repo, "worktree prune", IssueGitTimeoutMs);

            GitCommandResult list = RunGitDetailed(repo, "worktree list --porcelain", IssueGitTimeoutMs);
            List<GitWorktreeEntry> worktrees = list != null && list.ExitCode == 0 ? GitHubIssues.ParseWorktreeList(list.StdOut) : new List<GitWorktreeEntry>();

            string storedBase = GitConfigGet(repo, $"branch.{branch}.{GitHubIssues.BaseBranchConfigKey}");
            string effectiveBase = string.IsNullOrEmpty(storedBase) ? baseBranch : storedBase;

            GitWorktreeEntry existing = worktrees.FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return (existing.Path, effectiveBase, true, null);

            string path = GitHubIssues.BuildWorktreePath(repo, issueNumber);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                return (null, null, false,
                    $"The folder for this issue's worktree already exists and is not a git worktree:\n\n{path}\n\nDelete or rename it, then try again.");
            }

            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); } catch { }

            bool branchExists = GitRefExists(repo, "refs/heads/" + branch);
            GitCommandResult add;
            if (branchExists)
            {
                add = RunGitDetailed(repo, $"worktree add {QuoteForWindowsCommandArgument(path)} {branch}", IssueGitTimeoutMs);
            }
            else
            {
                RunGitDetailed(repo, $"fetch {context.RemoteName} {baseBranch}", IssueNetworkTimeoutMs);
                string startPoint = GitRefExists(repo, $"refs/remotes/{context.RemoteName}/{baseBranch}")
                    ? context.RemoteName + "/" + baseBranch
                    : baseBranch;
                add = RunGitDetailed(repo, $"worktree add --no-track -b {branch} {QuoteForWindowsCommandArgument(path)} {startPoint}", IssueGitTimeoutMs);
                if (add != null && add.ExitCode == 0)
                {
                    RunGitDetailed(repo, $"config branch.{branch}.{GitHubIssues.BaseBranchConfigKey} {baseBranch}", IssueGitTimeoutMs);
                    effectiveBase = baseBranch;
                }
            }

            if (add == null || add.ExitCode != 0)
                return (null, null, false, "Could not create the worktree.\n\n" + (add?.StdErr ?? "git did not respond."));

            return (path, effectiveBase, branchExists, null);
        }

        #endregion

        #region Commit & Push

        private async Task CommitAndPushIssueFixAsync(Window owner, GitHubRepoContext context, GitHubIssue issue, IssueLocalInfo info, Action<string> progress)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string worktree = info.WorktreePath;
            string branch = info.Branch;

            progress("Staging changes…");
            var staged = await Task.Run(() =>
            {
                GitCommandResult add = RunGitDetailed(worktree, "add -A", IssueGitTimeoutMs);
                if (add == null || add.ExitCode != 0)
                    return (Error: "Could not stage the changes.\n\n" + add?.StdErr, Diff: (string)null);
                GitCommandResult diff = RunGitDetailed(worktree, "diff --cached", IssueGitTimeoutMs);
                return (Error: (string)null, Diff: diff?.StdOut ?? string.Empty);
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (staged.Error != null)
            {
                MessageBox.Show(owner, staged.Error, "Commit & Push", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(staged.Diff))
            {
                string message = null;
                NativeChatSessionState session = FindIssueSession(issue.Number);
                if (session != null && !session.TurnInFlight)
                {
                    progress("Asking the agent for a commit message…");
                    message = CleanCommitMessage(await AskIssueSessionAsync(session, BuildCommitMessagePrompt(staged.Diff)));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }

                if (string.IsNullOrWhiteSpace(message))
                    message = GitHubIssues.BuildDefaultCommitMessage(issue);
                message = GitHubIssues.EnsureIssueReference(message, issue.Number);

                progress("Waiting for confirmation…");
                if (!ShowIssueTextDialog(owner, "Commit & Push",
                        $"Commit every change in the worktree to {branch} and push it to {context.RemoteName}.",
                        ref message))
                {
                    progress("Commit cancelled.");
                    return;
                }

                message = GitHubIssues.EnsureIssueReference(message, issue.Number);
                string messageFile = WriteCommitMessageToTempFile(message);
                if (messageFile == null)
                {
                    MessageBox.Show(owner, "Could not write the commit message to a temp file.", "Commit & Push", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                progress("Committing…");
                GitCommandResult commit = await Task.Run(() => RunGitDetailed(worktree, "commit -F " + QuoteForWindowsCommandArgument(messageFile), IssueGitTimeoutMs));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (commit == null || commit.ExitCode != 0)
                {
                    MessageBox.Show(owner, "Commit failed.\n\n" + (commit?.StdErr ?? string.Empty) + commit?.StdOut, "Commit & Push", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            progress($"Pushing {branch}…");
            GitCommandResult push = await Task.Run(() => RunGitDetailed(worktree, $"push -u {context.RemoteName} {branch}", IssueNetworkTimeoutMs));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (push == null || push.ExitCode != 0)
            {
                MessageBox.Show(owner, "The commit was created, but the push failed.\n\n" + (push?.StdErr ?? "git did not respond."),
                    "Commit & Push", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            progress($"Pushed {branch}.");
        }

        #endregion

        #region Merge

        /// <summary>
        /// Merges the fix into its base branch inside the issue's worktree (detached on the latest
        /// remote base, so neither the main working copy nor the fix branch moves), pushes the result,
        /// then — after the user confirmed the reply — comments on and closes the issue and cleans up.
        /// Conflicts are handed to the issue's chat instead: the base is merged into the fix branch and
        /// the agent resolves it; Merge is run again afterwards.
        /// </summary>
        private async Task MergeIssueFixAsync(Window owner, GitHubRepoContext context, string ghPath, GitHubIssue issue, IssueLocalInfo info, Action<string> progress)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string worktree = info.WorktreePath;
            string branch = info.Branch;
            string baseBranch = info.BaseBranch;
            string remote = context.RemoteName;

            progress($"Fetching {baseBranch}…");
            var merged = await Task.Run(() =>
            {
                GitCommandResult status = RunGitDetailed(worktree, "status --porcelain", IssueGitTimeoutMs);
                if (status == null || status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.StdOut))
                    return (Kind: "dirty", Detail: (string)null, Conflicts: (List<string>)null);

                RunGitDetailed(worktree, $"fetch {remote} {baseBranch}", IssueNetworkTimeoutMs);
                string baseRef = GitRefExists(worktree, $"refs/remotes/{remote}/{baseBranch}") ? remote + "/" + baseBranch : baseBranch;

                GitCommandResult detach = RunGitDetailed(worktree, "checkout --detach " + baseRef, IssueGitTimeoutMs);
                if (detach == null || detach.ExitCode != 0)
                    return (Kind: "error", Detail: "Could not check out " + baseRef + ".\n\n" + detach?.StdErr, Conflicts: (List<string>)null);

                string messageFile = WriteCommitMessageToTempFile(GitHubIssues.BuildMergeCommitMessage(issue, branch));
                GitCommandResult merge = RunGitDetailed(worktree, $"merge --no-ff -F {QuoteForWindowsCommandArgument(messageFile)} {branch}", IssueGitTimeoutMs);
                if (merge != null && merge.ExitCode == 0)
                    return (Kind: "merged", Detail: baseRef, Conflicts: (List<string>)null);

                // Conflict (or failure): undo, put the fix branch back, and merge the base into it so
                // the agent can resolve the conflicts on its own branch.
                RunGitDetailed(worktree, "merge --abort", IssueGitTimeoutMs);
                RunGitDetailed(worktree, "checkout " + branch, IssueGitTimeoutMs);
                GitCommandResult reverse = RunGitDetailed(worktree, "merge --no-edit " + baseRef, IssueGitTimeoutMs);
                if (reverse != null && reverse.ExitCode == 0)
                    return (Kind: "retry", Detail: merge?.StdErr + merge?.StdOut, Conflicts: (List<string>)null);

                GitCommandResult unmerged = RunGitDetailed(worktree, "diff --name-only --diff-filter=U", IssueGitTimeoutMs);
                List<string> files = (unmerged?.StdOut ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (files.Count == 0)
                {
                    RunGitDetailed(worktree, "merge --abort", IssueGitTimeoutMs);
                    return (Kind: "error", Detail: "The merge failed.\n\n" + merge?.StdErr + reverse?.StdErr, Conflicts: (List<string>)null);
                }

                return (Kind: "conflict", Detail: baseRef, Conflicts: files);
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            switch (merged.Kind)
            {
                case "dirty":
                    MessageBox.Show(owner, "The worktree has uncommitted changes. Commit & Push them first.", "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                case "error":
                    MessageBox.Show(owner, merged.Detail, "Merge", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                case "retry":
                    MessageBox.Show(owner,
                        $"The latest {baseBranch} was merged into {branch} first. Run Merge again.",
                        "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                case "conflict":
                    await HandOffMergeConflictsAsync(owner, context, ghPath, issue, baseBranch, merged.Conflicts);
                    return;
            }

            // Merged locally (detached HEAD in the worktree). Nothing is pushed yet.
            string reply = null;
            NativeChatSessionState session = FindIssueSession(issue.Number);
            if (session != null && !session.TurnInFlight)
            {
                progress("Asking the agent to draft the reply…");
                reply = CleanCommitMessage(await AskIssueSessionAsync(session, GitHubIssues.BuildReplyDraftPrompt(issue, baseBranch)));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            if (string.IsNullOrWhiteSpace(reply))
                reply = GitHubIssues.BuildDefaultReply(issue, baseBranch);

            bool hasGh = !string.IsNullOrEmpty(ghPath);
            var postReply = new CheckBox { Content = hasGh ? "Post this reply on the issue" : "Copy this reply and open the issue (GitHub CLI not installed)", IsChecked = true };
            var closeIssue = new CheckBox { Content = "Close the issue", IsChecked = issue.IsOpen && hasGh, IsEnabled = issue.IsOpen && hasGh };
            var cleanUp = new CheckBox { Content = $"Delete {branch}, its worktree and chat tab", IsChecked = true };

            progress("Waiting for confirmation…");
            if (!ShowIssueTextDialog(owner, "Merge",
                    $"Merge {branch} into {baseBranch} and push {baseBranch} to {remote}. Reply on issue #{issue.Number}:",
                    ref reply, postReply, closeIssue, cleanUp))
            {
                await Task.Run(() => RunGitDetailed(worktree, "checkout " + branch, IssueGitTimeoutMs));
                progress("Merge cancelled.");
                return;
            }

            progress($"Pushing {baseBranch}…");
            var pushed = await Task.Run(() =>
            {
                GitCommandResult push = RunGitDetailed(worktree, $"push {remote} HEAD:refs/heads/{baseBranch}", IssueNetworkTimeoutMs);
                GitCommandResult sha = RunGitDetailed(worktree, "rev-parse HEAD", IssueGitTimeoutMs);
                return (Push: push, Sha: sha?.StdOut.Trim());
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (pushed.Push == null || pushed.Push.ExitCode != 0)
            {
                await Task.Run(() => RunGitDetailed(worktree, "checkout " + branch, IssueGitTimeoutMs));
                MessageBox.Show(owner,
                    $"Pushing {baseBranch} failed, so nothing was merged.\n\n" + (pushed.Push?.StdErr ?? "git did not respond."),
                    "Merge", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var notes = new List<string> { $"Merged {branch} into {baseBranch} and pushed it." };

            progress($"Updating local {baseBranch}…");
            string localNote = await Task.Run(() => FastForwardLocalBase(context, baseBranch, pushed.Sha, worktree));
            if (!string.IsNullOrEmpty(localNote)) notes.Add(localNote);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (postReply.IsChecked == true)
            {
                string bodyText = GitHubIssues.AppendMergeFootnote(reply, baseBranch, pushed.Sha);
                if (hasGh)
                {
                    progress("Posting the reply…");
                    string bodyFile = WriteCommitMessageToTempFile(bodyText);
                    GitCommandResult comment = await Task.Run(() => RunProcessDetailed(ghPath, context.RepoRoot,
                        $"issue comment {issue.Number} -R {context.Slug} --body-file {QuoteForWindowsCommandArgument(bodyFile)}", IssueNetworkTimeoutMs));
                    notes.Add(comment != null && comment.ExitCode == 0
                        ? "Posted the reply on the issue."
                        : "Could not post the reply: " + (comment?.StdErr.Trim() ?? "gh did not respond."));
                }
                else
                {
                    await ClipboardRetryAsync(() => Clipboard.SetText(bodyText));
                    OpenUrl(issue.Url);
                    notes.Add("Copied the reply to the clipboard and opened the issue.");
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }

            if (closeIssue.IsChecked == true && hasGh)
            {
                progress("Closing the issue…");
                GitCommandResult close = await Task.Run(() => RunProcessDetailed(ghPath, context.RepoRoot,
                    $"issue close {issue.Number} -R {context.Slug} --reason completed", IssueNetworkTimeoutMs));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // A "Fixes #N" commit landing on the default branch can close it first — not an error.
                bool alreadyClosed = close != null && (close.StdErr + close.StdOut).IndexOf("already closed", StringComparison.OrdinalIgnoreCase) >= 0;
                notes.Add(close != null && (close.ExitCode == 0 || alreadyClosed)
                    ? "Closed the issue."
                    : "Could not close the issue: " + (close?.StdErr.Trim() ?? "gh did not respond."));
                if (close != null && (close.ExitCode == 0 || alreadyClosed)) issue.State = "closed";
            }

            if (cleanUp.IsChecked == true)
            {
                progress("Cleaning up…");
                notes.AddRange(await RemoveIssueFixAsync(context, issue.Number, worktree, branch, deleteRemoteBranch: true));
            }
            else
            {
                await Task.Run(() => RunGitDetailed(worktree, "checkout " + branch, IssueGitTimeoutMs));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            progress($"Merged into {baseBranch}.");
            MessageBox.Show(owner, string.Join("\n", notes), "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Sends the conflicts to the issue's chat (opening it when needed) for the agent to resolve.</summary>
        private async Task HandOffMergeConflictsAsync(Window owner, GitHubRepoContext context, string ghPath, GitHubIssue issue, string baseBranch, List<string> conflicts)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string prompt = GitHubIssues.BuildConflictPrompt(baseBranch, conflicts);

            NativeChatSessionState session = FindIssueSession(issue.Number);
            if (session == null && IsNativeModeActive)
            {
                string worktree = GitHubIssues.BuildWorktreePath(context.RepoRoot, issue.Number);
                GitCommandResult list = await Task.Run(() => RunGitDetailed(context.RepoRoot, "worktree list --porcelain", IssueGitTimeoutMs));
                string branch = GitHubIssues.BuildBranchName(issue.Number);
                GitWorktreeEntry entry = GitHubIssues.ParseWorktreeList(list?.StdOut).FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
                if (entry != null) worktree = entry.Path;

                AiProvider provider = _currentRunningProvider ?? _settings.SelectedProvider;
                session = await StartParallelSessionAsync(provider, worktree, null, s =>
                {
                    s.WorkspaceOverride = worktree;
                    s.IssueNumber = issue.Number;
                    s.IssueTitle = issue.Title;
                });
            }

            if (session == null)
            {
                MessageBox.Show(owner,
                    $"Merging the latest {baseBranch} produced conflicts in:\n\n{string.Join("\n", conflicts)}\n\nThe merge is in progress in the issue's worktree — resolve it there, then run Merge again.",
                    "Merge", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BringSessionTabToFront(session);
            StartIssuePrompt(session, prompt);
            MessageBox.Show(owner,
                $"Merging the latest {baseBranch} produced conflicts. They were sent to the issue's chat to resolve — run Merge again once the agent has finished.",
                "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Moves the local base branch up to the pushed merge: fast-forwards it where it is checked out
        /// (only when that working copy has no tracked changes), or just moves the ref when it is not
        /// checked out anywhere. Returns a note for the summary, or null. Background thread.
        /// </summary>
        private string FastForwardLocalBase(GitHubRepoContext context, string baseBranch, string mergeSha, string issueWorktree)
        {
            if (string.IsNullOrEmpty(mergeSha) || !GitRefExists(context.RepoRoot, "refs/heads/" + baseBranch))
                return null;

            GitCommandResult list = RunGitDetailed(context.RepoRoot, "worktree list --porcelain", IssueGitTimeoutMs);
            GitWorktreeEntry checkedOut = GitHubIssues.ParseWorktreeList(list?.StdOut)
                .FirstOrDefault(w => string.Equals(w.Branch, baseBranch, StringComparison.OrdinalIgnoreCase));

            if (checkedOut != null)
            {
                GitCommandResult status = RunGitDetailed(checkedOut.Path, "status --porcelain --untracked-files=no", IssueGitTimeoutMs);
                if (status == null || status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.StdOut))
                    return $"Your local {baseBranch} has uncommitted changes, so it was not updated — pull it when ready.";

                GitCommandResult ff = RunGitDetailed(checkedOut.Path, "merge --ff-only " + mergeSha, IssueGitTimeoutMs);
                return ff != null && ff.ExitCode == 0
                    ? $"Updated your local {baseBranch}."
                    : $"Your local {baseBranch} could not be fast-forwarded — pull it when ready.";
            }

            GitCommandResult ancestor = RunGitDetailed(context.RepoRoot, $"merge-base --is-ancestor {baseBranch} {mergeSha}", IssueGitTimeoutMs);
            if (ancestor == null || ancestor.ExitCode != 0)
                return $"Your local {baseBranch} has commits that are not on {context.RemoteName}, so it was not updated.";

            GitCommandResult move = RunGitDetailed(context.RepoRoot, $"branch -f {baseBranch} {mergeSha}", IssueGitTimeoutMs);
            return move != null && move.ExitCode == 0 ? $"Updated your local {baseBranch}." : null;
        }

        #endregion

        #region Discard / clean up

        private async Task DiscardIssueFixAsync(Window owner, GitHubRepoContext context, GitHubIssue issue, IssueLocalInfo info, Action<string> progress)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string text = $"Discard the fix for issue #{issue.Number}?\n\n" +
                          $"This closes its chat tab and deletes the worktree and the local branch {info.Branch}" +
                          (info.Dirty ? ", including uncommitted changes" : string.Empty) + ".";
            var deleteRemote = new CheckBox { Content = $"Also delete {info.Branch} on {context.RemoteName}", IsChecked = false, IsEnabled = info.RemoteBranchExists };

            string unused = null;
            if (!ShowIssueTextDialog(owner, "Discard Fix", text, ref unused, info.RemoteBranchExists ? new[] { deleteRemote } : new CheckBox[0]))
                return;

            progress("Removing the fix…");
            List<string> notes = await RemoveIssueFixAsync(context, issue.Number, info.WorktreeExists ? info.WorktreePath : null, info.Branch, deleteRemote.IsChecked == true);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            progress(notes.Count == 0 ? "Fix discarded." : string.Join("  ·  ", notes));
        }

        /// <summary>
        /// Closes the issue's chat tab (ending its agent, which may hold files open in the worktree),
        /// removes the worktree and deletes the fix branch — locally, and on the remote when asked.
        /// Returns notes for anything that did not work.
        /// </summary>
        private async Task<List<string>> RemoveIssueFixAsync(GitHubRepoContext context, int issueNumber, string worktree, string branch, bool deleteRemoteBranch)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CloseIssueSessionTabs(issueNumber);

            return await Task.Run(async () =>
            {
                var notes = new List<string>();
                string repo = context.RepoRoot;

                if (!string.IsNullOrEmpty(worktree))
                {
                    GitCommandResult remove = null;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        remove = RunGitDetailed(repo, "worktree remove --force " + QuoteForWindowsCommandArgument(worktree), IssueGitTimeoutMs);
                        if (remove != null && remove.ExitCode == 0) break;
                        await Task.Delay(1000);
                    }

                    if (remove == null || remove.ExitCode != 0)
                        notes.Add("Could not remove the worktree (a file may still be open): " + worktree);
                    RunGitDetailed(repo, "worktree prune", IssueGitTimeoutMs);

                    try
                    {
                        string parent = Path.GetDirectoryName(worktree.TrimEnd('\\'));
                        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                            Directory.Delete(parent);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"GitHub Issues: could not remove empty worktrees folder: {ex.Message}");
                    }
                }

                if (GitRefExists(repo, "refs/heads/" + branch))
                {
                    GitCommandResult delete = RunGitDetailed(repo, "branch -D " + branch, IssueGitTimeoutMs);
                    if (delete == null || delete.ExitCode != 0)
                        notes.Add("Could not delete the local branch " + branch + ".");
                }

                if (deleteRemoteBranch)
                {
                    GitCommandResult remoteHead = RunGitDetailed(repo, $"ls-remote --exit-code --heads {context.RemoteName} {branch}", IssueNetworkTimeoutMs);
                    if (remoteHead != null && remoteHead.ExitCode == 0)
                    {
                        GitCommandResult deleteRemote = RunGitDetailed(repo, $"push {context.RemoteName} --delete {branch}", IssueNetworkTimeoutMs);
                        notes.Add(deleteRemote != null && deleteRemote.ExitCode == 0
                            ? $"Deleted {branch} on {context.RemoteName}."
                            : $"Could not delete {branch} on {context.RemoteName}.");
                    }
                }

                return notes;
            });
        }

        #endregion

        #region Sessions

        /// <summary>The open chat tab fixing <paramref name="issueNumber"/>, or null.</summary>
        private NativeChatSessionState FindIssueSession(int issueNumber)
        {
            lock (_sessionLock)
            {
                return _nativeSessions.Values.FirstOrDefault(s =>
                    s.IssueNumber == issueNumber && !string.IsNullOrEmpty(s.WorkspaceOverride) && s.AgentSession != null);
            }
        }

        private void CloseIssueSessionTabs(int issueNumber)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = _nativeSessions.Values.Where(s => s.IssueNumber == issueNumber && !string.IsNullOrEmpty(s.WorkspaceOverride)).ToList();
            }

            foreach (NativeChatSessionState session in sessions)
            {
                try
                {
                    session.Window?.Close();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitHub Issues: could not close the issue tab: {ex.Message}");
                }

                // Closing the frame normally ends the session through its Closed handler; make sure
                // the agent is gone either way, since it may hold files open in the worktree.
                if (GetSession(session.SessionId) != null)
                    OnSessionWindowClosed(session.SessionId);
            }
        }

        /// <summary>Asks an issue's chat a side question (commit message, reply draft) and returns the cleaned-up answer source.</summary>
        private async Task<string> AskIssueSessionAsync(NativeChatSessionState session, string prompt)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (session?.AgentSession == null || session.TurnInFlight)
                return null;

            // A behind-the-scenes question: the On Agent Finish sound/info bar must not fire for it.
            _suppressNextNativeAgentFinish = true;
            Task<string> capture = CaptureNextAssistantTurnAsync(session.AgentSession, TimeSpan.FromMilliseconds(SideQuestionTimeoutMs));
            StartIssuePrompt(session, prompt);
            return await capture;
        }

        /// <summary>
        /// Hands an issue's chat a prompt and returns once its turn has started, not when it ends. Only
        /// Claude Code's send completes as soon as the prompt is written; every other agent's completes
        /// with the whole turn, which held the window on "Starting the agent…" with every button
        /// disabled until the agent was done. The turn is followed through
        /// <see cref="NativeChatSessionState.TurnInFlight"/> instead, by the window's session watcher.
        /// </summary>
        private void StartIssuePrompt(NativeChatSessionState session, string prompt)
        {
            StartSessionHistoryTask(() => SendPromptToNativeAgentAsync(prompt, session.SessionId), "claudecode/githubissues/prompt");
        }

        #endregion

        #region Git / GitHub helpers

        private IssueLocalInfo LoadIssueLocalInfo(GitHubRepoContext context, int issueNumber, string defaultBase)
        {
            string repo = context.RepoRoot;
            string branch = GitHubIssues.BuildBranchName(issueNumber);
            var info = new IssueLocalInfo { Branch = branch };

            info.BranchExists = GitRefExists(repo, "refs/heads/" + branch);

            GitCommandResult list = RunGitDetailed(repo, "worktree list --porcelain", IssueGitTimeoutMs);
            GitWorktreeEntry entry = GitHubIssues.ParseWorktreeList(list?.StdOut)
                .FirstOrDefault(w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                // A worktree left detached by an interrupted merge still belongs to the issue.
                string expected = GitHubIssues.BuildWorktreePath(repo, issueNumber);
                entry = GitHubIssues.ParseWorktreeList(list?.StdOut)
                    .FirstOrDefault(w => string.Equals(w.Path.TrimEnd('\\'), expected, StringComparison.OrdinalIgnoreCase));
            }

            info.WorktreeExists = entry != null && Directory.Exists(entry.Path);
            info.WorktreePath = entry?.Path;

            string storedBase = GitConfigGet(repo, $"branch.{branch}.{GitHubIssues.BaseBranchConfigKey}");
            info.BaseBranch = string.IsNullOrEmpty(storedBase) ? defaultBase : storedBase;

            if (info.WorktreeExists)
            {
                GitCommandResult status = RunGitDetailed(entry.Path, "status --porcelain", IssueGitTimeoutMs);
                info.Dirty = status != null && status.ExitCode == 0 && !string.IsNullOrWhiteSpace(status.StdOut);
            }

            if (info.BranchExists && !string.IsNullOrEmpty(info.BaseBranch))
            {
                string baseRef = GitRefExists(repo, $"refs/remotes/{context.RemoteName}/{info.BaseBranch}")
                    ? context.RemoteName + "/" + info.BaseBranch
                    : info.BaseBranch;
                GitCommandResult ahead = RunGitDetailed(repo, $"rev-list --count {baseRef}..{branch}", IssueGitTimeoutMs);
                if (ahead != null && ahead.ExitCode == 0 && int.TryParse(ahead.StdOut.Trim(), out int count))
                    info.Ahead = count;
            }

            string remoteRef = $"refs/remotes/{context.RemoteName}/{branch}";
            info.RemoteBranchExists = GitRefExists(repo, remoteRef);
            if (info.RemoteBranchExists && info.BranchExists)
            {
                GitCommandResult local = RunGitDetailed(repo, "rev-parse refs/heads/" + branch, IssueGitTimeoutMs);
                GitCommandResult remote = RunGitDetailed(repo, "rev-parse " + remoteRef, IssueGitTimeoutMs);
                info.Pushed = local != null && remote != null && local.ExitCode == 0 &&
                              string.Equals(local.StdOut.Trim(), remote.StdOut.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return info;
        }

        private bool GitRefExists(string directory, string reference)
        {
            GitCommandResult result = RunGitDetailed(directory, "rev-parse --verify --quiet " + reference, IssueGitTimeoutMs);
            return result != null && result.ExitCode == 0;
        }

        private string GitConfigGet(string directory, string key)
        {
            GitCommandResult result = RunGitDetailed(directory, "config --get " + key, IssueGitTimeoutMs);
            return result != null && result.ExitCode == 0 ? result.StdOut.Trim() : null;
        }

        /// <summary>The GitHub CLI executable: PATH first, then its default install folders. Null when not installed.</summary>
        private static string ResolveGhPath()
        {
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim().Trim('"'), "gh.exe");
                    if (File.Exists(candidate)) return candidate;
                }

                foreach (string candidate in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GitHub CLI", "gh.exe")
                })
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResolveGhPath error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Lists issues through gh when installed (works for private repositories with the user's gh
        /// login), falling back to the REST API (public repositories; GH_TOKEN/GITHUB_TOKEN if set).
        /// </summary>
        private async Task<(List<GitHubIssue> Issues, string Error)> FetchIssuesAsync(GitHubRepoContext context, string ghPath, string state)
        {
            string ghError = null;
            if (!string.IsNullOrEmpty(ghPath))
            {
                GitCommandResult result = await Task.Run(() => RunProcessDetailed(ghPath, context.RepoRoot,
                    $"issue list -R {context.Slug} --state {state} --limit {IssueListLimit} --json {GitHubIssues.GhListFields}", IssueNetworkTimeoutMs));
                if (result != null && result.ExitCode == 0)
                {
                    try
                    {
                        return (GitHubIssues.ParseIssueList(result.StdOut), null);
                    }
                    catch (Exception ex)
                    {
                        ghError = "Could not read gh's output: " + ex.Message;
                    }
                }
                else
                {
                    ghError = result == null ? "gh did not respond." : result.StdErr.Trim();
                }
            }

            try
            {
                string json = await GitHubApiGetAsync($"repos/{context.Slug}/issues?state={state}&per_page=100&sort=updated");
                return (GitHubIssues.ParseIssueList(json), null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub Issues REST fallback failed: {ex.Message}");
                string message = ghError != null
                    ? "Could not list issues with the GitHub CLI: " + ghError + (ghError.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 ? " (run \"gh auth login\")" : string.Empty)
                    : "Could not list issues: " + ex.Message + " — install the GitHub CLI (gh) and run \"gh auth login\" for private repositories.";
                return (null, message);
            }
        }

        /// <summary>
        /// One issue with its current state (and comments, through gh) — used to keep showing an issue
        /// that just left the listed view, e.g. closed by Merge while the window shows "Open".
        /// </summary>
        private async Task<GitHubIssue> FetchSingleIssueAsync(GitHubRepoContext context, string ghPath, int issueNumber)
        {
            try
            {
                if (!string.IsNullOrEmpty(ghPath))
                {
                    GitCommandResult result = await Task.Run(() => RunProcessDetailed(ghPath, context.RepoRoot,
                        $"issue view {issueNumber} -R {context.Slug} --json {GitHubIssues.GhListFields},comments", IssueNetworkTimeoutMs));
                    if (result != null && result.ExitCode == 0)
                        return GitHubIssues.ParseIssue(result.StdOut);
                }

                return GitHubIssues.ParseIssue(await GitHubApiGetAsync($"repos/{context.Slug}/issues/{issueNumber}"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub Issues: could not load issue #{issueNumber}: {ex.Message}");
                return null;
            }
        }

        private async Task<List<GitHubIssueComment>> FetchIssueCommentsAsync(GitHubRepoContext context, string ghPath, int issueNumber)
        {
            try
            {
                if (!string.IsNullOrEmpty(ghPath))
                {
                    GitCommandResult result = await Task.Run(() => RunProcessDetailed(ghPath, context.RepoRoot,
                        $"issue view {issueNumber} -R {context.Slug} --json comments", IssueNetworkTimeoutMs));
                    if (result != null && result.ExitCode == 0)
                        return GitHubIssues.ParseComments(result.StdOut);
                }

                string json = await GitHubApiGetAsync($"repos/{context.Slug}/issues/{issueNumber}/comments?per_page=100");
                return GitHubIssues.ParseComments(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub Issues: could not load comments for #{issueNumber}: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> GitHubApiGetAsync(string relativeUrl)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeExtension");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrWhiteSpace(token))
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

                using (HttpResponseMessage response = await client.GetAsync("https://api.github.com/" + relativeUrl).ConfigureAwait(false))
                {
                    string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}");
                    return content;
                }
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub Issues: could not open {url}: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts another Visual Studio on the worktree: the same solution file (same path relative to
        /// the repository) when it exists there, else the worktree folder.
        /// </summary>
        private void OpenWorktreeInNewVisualStudio(Window owner, GitHubRepoContext context, string worktree)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                string target = worktree;
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string solution = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(solution))
                {
                    string solutionRepo = FindGitRepositoryRoot(Path.GetDirectoryName(solution));
                    if (!string.IsNullOrEmpty(solutionRepo) && solution.StartsWith(solutionRepo, StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = solution.Substring(solutionRepo.Length).TrimStart('\\', '/');
                        string candidate = Path.Combine(worktree, relative);
                        if (File.Exists(candidate)) target = candidate;
                    }
                }

                string devenv = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(devenv) || !File.Exists(devenv))
                {
                    OpenUrl(worktree);
                    return;
                }

                Process.Start(new ProcessStartInfo(devenv, QuoteForWindowsCommandArgument(target)) { UseShellExecute = false })?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub Issues: could not open the worktree in Visual Studio: {ex.Message}");
                MessageBox.Show(owner, "Could not start Visual Studio: " + ex.Message, "GitHub Issues", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Issue images

        private const long MaxIssueImageBytes = 25L * 1024 * 1024;
        private static HttpClient _issueImageClient;

        /// <summary>
        /// Inline picture for an image in an issue or comment: a "Loading" line while it downloads,
        /// then the image scaled down to the details pane width (never up past its natural size or the
        /// width the author gave it). On failure, a link that opens it in the browser.
        /// </summary>
        private static BlockUIContainer BuildIssueImageBlock(IssueBodySegment segment, Dictionary<string, AsyncLazy<BitmapSource>> cache,
            Brush mutedBrush, Brush accentBrush)
        {
            string url = segment.ImageUrl;
            string label = string.IsNullOrWhiteSpace(segment.AltText) || string.Equals(segment.AltText, "Image", StringComparison.OrdinalIgnoreCase)
                ? "image" : "\"" + segment.AltText.Trim() + "\"";

            var host = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 8),
                Child = new TextBlock { Text = "🖼 Loading " + label + "…", Foreground = mutedBrush, FontStyle = FontStyles.Italic }
            };

            if (!cache.TryGetValue(url, out AsyncLazy<BitmapSource> download))
            {
                // AsyncLazy (not a bare Task) so a later render awaiting the same download can't deadlock the UI thread.
                download = new AsyncLazy<BitmapSource>(async () =>
                {
                    await TaskScheduler.Default;
                    return await DownloadIssueImageAsync(url).ConfigureAwait(false);
                }, ThreadHelper.JoinableTaskFactory);
                cache[url] = download;
            }

            StartSessionHistoryTask(async () =>
            {
                BitmapSource bitmap = null;
                try
                {
                    bitmap = await download.GetValueAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitHub Issues: image {url} failed: {ex.Message}");
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (bitmap == null)
                {
                    cache.Remove(url); // a later render may try again
                    var link = new TextBlock
                    {
                        Text = "🖼 " + char.ToUpperInvariant(label[0]) + label.Substring(1) + " could not be loaded — open it in the browser",
                        Foreground = accentBrush,
                        TextDecorations = TextDecorations.Underline,
                        Cursor = Cursors.Hand,
                        TextWrapping = TextWrapping.Wrap
                    };
                    // The RichTextBox captures the mouse on button down, so a click handler must act there.
                    link.MouseLeftButtonDown += (s, e) => { e.Handled = true; OpenUrl(url); };
                    host.Child = link;
                    return;
                }

                double maxWidth = bitmap.PixelWidth;
                if (segment.Width.HasValue && segment.Width.Value > 0) maxWidth = Math.Min(maxWidth, segment.Width.Value);

                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.Both,
                    MaxWidth = maxWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = Cursors.Hand
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                image.MouseLeftButtonDown += (s, e) => { e.Handled = true; OpenUrl(url); };
                AttachIssueImagePreview(image, bitmap);

                // Stretch so the image can take the pane width; MaxWidth keeps small images at natural size.
                host.HorizontalAlignment = HorizontalAlignment.Stretch;
                host.Child = image;
            }, "claudecode/githubissues/image");

            return new BlockUIContainer(host) { Margin = new Thickness(0) };
        }

        /// <summary>
        /// Hovering an issue image shows it larger (natural size, capped to most of the screen) in a
        /// tooltip centered on it. The tooltip window is made click-through: a big preview usually lands
        /// under the cursor, and without that the image would lose the mouse, close the tooltip and
        /// reopen it in a loop; it also lets a click still reach the image and open it in the browser.
        /// </summary>
        private static void AttachIssueImagePreview(Image image, BitmapSource bitmap)
        {
            Rect workArea = SystemParameters.WorkArea;
            var preview = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = Math.Min(bitmap.PixelWidth, workArea.Width * 0.85),
                MaxHeight = Math.Min(bitmap.PixelHeight, workArea.Height * 0.8)
            };
            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);

            var content = new StackPanel();
            content.Children.Add(preview);
            content.Children.Add(new TextBlock { Text = "Click to open in the browser", Margin = new Thickness(0, 4, 0, 0), FontStyle = FontStyles.Italic });

            var tip = new ToolTip { Content = content, Padding = new Thickness(4), Placement = PlacementMode.Center, PlacementTarget = image };
            tip.Opened += (s, e) =>
            {
                try
                {
                    if (PresentationSource.FromVisual(tip) is HwndSource source && source.Handle != IntPtr.Zero)
                    {
                        int exStyle = GetWindowLong(source.Handle, GWL_EXSTYLE);
                        SetWindowLong(source.Handle, GWL_EXSTYLE, exStyle | IssuePreviewTransparentStyle);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitHub Issues: could not make the image preview click-through: {ex.Message}");
                }
            };

            image.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(image, 350);
            ToolTipService.SetShowDuration(image, int.MaxValue);
            ToolTipService.SetBetweenShowDelay(image, 0);
        }

        private const int IssuePreviewTransparentStyle = 0x00000020 /* WS_EX_TRANSPARENT */ | 0x00080000 /* WS_EX_LAYERED */;

        private static async Task<BitmapSource> DownloadIssueImageAsync(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (_issueImageClient == null)
            {
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeExtension");
                _issueImageClient = client;
            }

            using (HttpResponseMessage response = await _issueImageClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                long? length = response.Content.Headers.ContentLength;
                if (length.HasValue && length.Value > MaxIssueImageBytes)
                    throw new InvalidOperationException("image too large");

                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.LongLength > MaxIssueImageBytes)
                    throw new InvalidOperationException("image too large");

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return bitmap;
            }
        }

        private static bool IsDarkBrush(Brush brush)
        {
            if (!(brush is SolidColorBrush solid)) return false;
            Color c = solid.Color;
            return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;
        }

        #endregion

        #region Model and effort

        /// <summary>
        /// The model and effort an issue's fix chat starts with. Only the fields the agent offers are
        /// set; <see cref="ApplyTo"/> writes them to that chat's own session snapshot, never to Settings.
        /// </summary>
        private sealed class IssueAgentChoice
        {
            public ClaudeModel? Model;
            public EffortLevel? Effort;
            public string ProviderModelId;          // empty = the agent's default
            public CodexReasoningLevel? Reasoning;

            public void ApplyTo(NativeChatSessionState session)
            {
                if (Model.HasValue) session.SelectedClaudeModel = Model.Value;
                if (Effort.HasValue) session.SelectedEffortLevel = Effort.Value;
                if (ProviderModelId != null) session.SelectedModel = ProviderModelId;
                if (Reasoning.HasValue) session.SelectedCodexReasoningLevel = Reasoning.Value;
            }
        }

        /// <summary>
        /// Asks which model and effort the issue's fix chat runs with, preselected to the panel's current
        /// pair. For Claude Code, Recommend asks the same advisor as the toolbar's Recommend AI Model about
        /// <paramref name="fixPrompt"/>. Returns null when cancelled. An agent with neither a model list
        /// nor an effort level gets no dialog and an empty choice.
        /// </summary>
        private IssueAgentChoice ShowIssueAgentDialog(Window owner, AiProvider provider, GitHubIssue issue, string fixPrompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool isClaude = IsClaudeProvider(provider);
            bool isCodex = IsCodexProvider(provider);
            bool hasCatalog = ProviderHasModelCatalog(provider);
            if (!isClaude && !isCodex && !hasCatalog)
                return new IssueAgentChoice();

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);
            ResourceDictionary comboRes = BuildThemedComboResources(themeBg, themeFg);

            var dialog = new Window
            {
                Title = $"Fix Issue #{issue.Number}",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = owner ?? Application.Current?.MainWindow; } catch { }

            var root = new StackPanel { Margin = new Thickness(14) };
            dialog.Content = root;

            root.Children.Add(new TextBlock
            {
                Text = $"#{issue.Number}  {issue.Title}",
                FontWeight = FontWeights.SemiBold,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(new TextBlock
            {
                Text = $"Agent: {GetChatProviderDisplayName(provider)} — the choice applies to this issue's chat only.",
                Foreground = themeFg,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

            // ---- Model / effort pickers ----
            var pickers = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(pickers);

            Func<string, int, TextBlock> makeLabel = (text, column) =>
            {
                var label = new TextBlock
                {
                    Text = text,
                    Foreground = themeFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(column == 0 ? 0 : 14, 0, 8, 0)
                };
                Grid.SetColumn(label, column);
                pickers.Children.Add(label);
                return label;
            };
            Action<ComboBox, string, object> addItem = (combo, caption, tag) =>
            {
                var item = new ComboBoxItem { Content = caption, Tag = tag, Foreground = themeFg };
                if (comboRes["cbi"] is Style cbiStyle) item.Style = cbiStyle;
                combo.Items.Add(item);
            };
            Action<ComboBox, Func<object, bool>> selectWhere = (combo, match) =>
            {
                foreach (ComboBoxItem item in combo.Items)
                {
                    if (match(item.Tag)) { combo.SelectedItem = item; return; }
                }
            };

            makeLabel("Model:", 0);
            var modelCombo = MakeThemedComboBox(comboRes, themeFg);
            Grid.SetColumn(modelCombo, 1);
            pickers.Children.Add(modelCombo);

            TextBlock effortLabel = makeLabel(isCodex ? "Reasoning:" : "Effort:", 2);
            var effortCombo = MakeThemedComboBox(comboRes, themeFg);
            Grid.SetColumn(effortCombo, 3);
            pickers.Children.Add(effortCombo);

            // Fable needs usage credits — left out on a zero balance unless it is the current pick.
            ClaudeModel currentClaudeModel = _settings.SelectedClaudeModel;
            bool offerFable = currentClaudeModel == ClaudeModel.Fable || IsFableModelOffered();

            if (isClaude)
            {
                // Same order as the composer's model menu.
                foreach (ClaudeModel model in new[] { ClaudeModel.Fable, ClaudeModel.Opus, ClaudeModel.Sonnet, ClaudeModel.Haiku, ClaudeModel.OpusPlan })
                {
                    if (model == ClaudeModel.Fable && !offerFable) continue;
                    addItem(modelCombo, GetClaudeModelDisplayName(model), model);
                }
                selectWhere(modelCombo, tag => tag is ClaudeModel m && m == currentClaudeModel);

                foreach (EffortLevel level in _effortSliderOrder)
                    addItem(effortCombo, GetChatEffortLabel(level), level);
                effortCombo.SelectedIndex = EffortToSliderIndex(_settings.SelectedEffortLevel);
            }
            else
            {
                // The agent's own list from the model catalog, re-read in the background when stale.
                Action<List<ModelOption>, string> fillModels = (models, selected) =>
                {
                    modelCombo.Items.Clear();
                    addItem(modelCombo, "Agent default", string.Empty);
                    if (!string.IsNullOrWhiteSpace(selected) && FindProviderModel(models, selected) == null)
                    {
                        ModelOption current = GetSelectedModelOption(provider, models, selected);
                        addItem(modelCombo, current.BuildMenuCaption(), current.Id);
                    }
                    foreach (ModelOption model in models)
                        addItem(modelCombo, model.BuildMenuCaption(), model.Id);
                    selectWhere(modelCombo, tag => string.Equals(tag as string, selected ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                    if (modelCombo.SelectedIndex < 0) modelCombo.SelectedIndex = 0;
                };

                fillModels(GetCachedProviderModels(provider), GetSelectedProviderModelId(provider));

                if (ShouldRefreshProviderModels(provider))
                {
#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
                    ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        List<ModelOption> fresh = await RefreshProviderModelsAsync(provider);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!dialog.IsVisible || fresh == null || fresh.Count == 0) return;
                        fillModels(fresh, (modelCombo.SelectedItem as ComboBoxItem)?.Tag as string);
                    }).FileAndForget("claudecode/githubissues/models");
#pragma warning restore VSSDK007, VSTHRD110
                }

                if (isCodex)
                {
                    foreach (CodexReasoningLevel level in _codexReasoningSliderOrder)
                        addItem(effortCombo, GetCodexReasoningLabel(level), level);
                    effortCombo.SelectedIndex = CodexReasoningToSliderIndex(_settings.SelectedCodexReasoningLevel);
                }
                else
                {
                    effortLabel.Visibility = Visibility.Collapsed;
                    effortCombo.Visibility = Visibility.Collapsed;
                }
            }

            // ---- Recommend (Claude Code only) ----
            var recommendRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            var status = new TextBlock
            {
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Text = "Not sure? Recommend asks Claude which model and effort suit this issue."
            };
            var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            var reason = new TextBlock
            {
                Foreground = themeFg,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };

            Style buttonStyle = GetDialogButtonStyle();
            Func<string, Button> makeButton = text =>
            {
                var button = new Button { Content = text, MinWidth = 96, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
                if (buttonStyle != null) button.Style = buttonStyle;
                else { button.Background = themeBg; button.Foreground = themeFg; button.BorderBrush = themeFg; }
                return button;
            };

            var recommendButton = makeButton("💡 Recommend");
            recommendButton.Margin = new Thickness(0);
            recommendButton.ToolTip = $"Ask {GetClaudeModelDisplayName(ClaudeModel.Opus)} ({GetChatEffortLabel(EffortLevel.XHigh)}) which model and effort suit this issue. Nothing starts until Start Fix.";
            DockPanel.SetDock(recommendButton, Dock.Left);
            recommendRow.Children.Add(recommendButton);
            recommendRow.Children.Add(status);

            if (isClaude)
            {
                root.Children.Add(recommendRow);
                root.Children.Add(progress);
                root.Children.Add(reason);
            }

            // ---- Buttons ----
            var startButton = makeButton("Start Fix");
            startButton.IsDefault = true;
            startButton.ToolTip = "Create the fix branch and worktree and open the issue's chat with this model and effort.";
            var cancelButton = makeButton("Cancel");
            cancelButton.IsCancel = true;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(startButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(buttons);

            var cts = new System.Threading.CancellationTokenSource();
            dialog.Closed += delegate { cts.Cancel(); };

            recommendButton.Click += delegate
            {
                recommendButton.IsEnabled = false;
                progress.Visibility = Visibility.Visible;
                reason.Visibility = Visibility.Collapsed;
                status.FontWeight = FontWeights.Normal;
                status.Text = $"Asking {GetClaudeModelDisplayName(ClaudeModel.Opus)} ({GetChatEffortLabel(EffortLevel.XHigh)}) which model and effort suit this issue…";

#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    (ModelRecommendation recommendation, string error) =
                        await RequestModelRecommendationAsync(provider, fixPrompt, cts.Token);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (cts.IsCancellationRequested) return;

                    progress.Visibility = Visibility.Collapsed;
                    recommendButton.IsEnabled = true;

                    if (recommendation != null && TryMapRecommendation(recommendation, out ClaudeModel model, out EffortLevel effort))
                    {
                        // No usage credits: Fable is not on offer, so its nearest, Opus, is proposed.
                        bool fableUnavailable = model == ClaudeModel.Fable && !offerFable;
                        if (fableUnavailable) model = ClaudeModel.Opus;

                        selectWhere(modelCombo, tag => tag is ClaudeModel m && m == model);
                        effortCombo.SelectedIndex = EffortToSliderIndex(effort);

                        status.Text = $"Recommended: {GetClaudeModelDisplayName(model)} · {GetChatEffortLabel(effort)}";
                        status.FontWeight = FontWeights.SemiBold;

                        string reasonText = recommendation.Reason?.Trim() ?? string.Empty;
                        if (fableUnavailable)
                            reasonText = (reasonText + " Fable was recommended, but it needs usage credits and the balance is zero.").Trim();
                        if (reasonText.Length > 0)
                        {
                            reason.Text = reasonText;
                            reason.Visibility = Visibility.Visible;
                        }
                        startButton.Focus();
                    }
                    else
                    {
                        status.Text = "No recommendation: " + (error ?? "the answer could not be read.") + " You can still pick a model and effort yourself.";
                    }
                }).FileAndForget("claudecode/githubissues/recommend");
#pragma warning restore VSSDK007, VSTHRD110
            };

            IssueAgentChoice result = null;
            startButton.Click += delegate
            {
                result = new IssueAgentChoice();
                object modelTag = (modelCombo.SelectedItem as ComboBoxItem)?.Tag;
                object effortTag = (effortCombo.SelectedItem as ComboBoxItem)?.Tag;
                if (modelTag is ClaudeModel claudeModel) result.Model = claudeModel;
                else if (modelTag is string modelId) result.ProviderModelId = modelId;
                if (effortTag is EffortLevel effortLevel) result.Effort = effortLevel;
                else if (effortTag is CodexReasoningLevel reasoningLevel) result.Reasoning = reasoningLevel;
                dialog.DialogResult = true;
            };

            dialog.ShowDialog();
            return result;
        }

        #endregion

        #region Confirmation dialog

        /// <summary>
        /// Small themed modal: an intro line, an optional editable text (shown when <paramref name="text"/>
        /// is not null) and optional checkboxes. Returns true on OK; <paramref name="text"/> receives the
        /// edited value.
        /// </summary>
        private bool ShowIssueTextDialog(Window owner, string title, string intro, ref string text, params CheckBox[] options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);
            Style buttonStyle = GetDialogButtonStyle();
            bool hasText = text != null;

            var dialog = new Window
            {
                Title = title,
                Width = 620,
                SizeToContent = hasText ? SizeToContent.Manual : SizeToContent.Height,
                Height = hasText ? 420 : double.NaN,
                MinWidth = 420,
                MinHeight = hasText ? 260 : 0,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = hasText ? ResizeMode.CanResize : ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Owner = owner
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = hasText ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var introBlock = new TextBlock { Text = intro, Foreground = themeFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            grid.Children.Add(introBlock);

            TextBox editor = null;
            if (hasText)
            {
                editor = new TextBox
                {
                    Text = text,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = themeBg,
                    Foreground = themeFg,
                    BorderBrush = themeFg,
                    Padding = new Thickness(4)
                };
                Grid.SetRow(editor, 1);
                grid.Children.Add(editor);
            }

            var optionsPanel = new StackPanel { Margin = new Thickness(0, options.Length > 0 ? 8 : 0, 0, 0) };
            foreach (CheckBox option in options)
            {
                option.Foreground = themeFg;
                option.Margin = new Thickness(0, 2, 0, 2);
                optionsPanel.Children.Add(option);
            }
            Grid.SetRow(optionsPanel, 2);
            grid.Children.Add(optionsPanel);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", MinWidth = 88, Height = 28, IsDefault = !hasText, Margin = new Thickness(8, 0, 0, 0) };
            var cancel = new Button { Content = "Cancel", MinWidth = 88, Height = 28, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            foreach (Button b in new[] { ok, cancel })
            {
                if (buttonStyle != null) b.Style = buttonStyle;
                else { b.Background = themeBg; b.Foreground = themeFg; b.BorderBrush = themeFg; }
                buttons.Children.Add(b);
            }
            ok.Click += (s, e) =>
            {
                if (editor != null && string.IsNullOrWhiteSpace(editor.Text))
                {
                    editor.Focus();
                    return;
                }
                dialog.DialogResult = true;
            };
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);

            dialog.Content = grid;
            dialog.Loaded += (s, e) => { if (editor != null) editor.Focus(); else ok.Focus(); };

            bool accepted = dialog.ShowDialog() == true;
            if (accepted && editor != null)
                text = editor.Text.Trim();

            // The checkboxes outlive the dialog for the caller to read; detach them so they can be reused.
            optionsPanel.Children.Clear();
            return accepted;
        }

        #endregion
    }
}
