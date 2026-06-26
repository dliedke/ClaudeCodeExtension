/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Claude Code session history — lists JSONL transcripts under
 *          ~/.claude/projects/<encoded-cwd>/ for the active workspace,
 *          and resumes one via `claude --resume <id>` or `claude --continue`.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Path Encoding & Session Directory Discovery

        /// <summary>
        /// Replicates Claude Code's filesystem-safe encoding of a working directory: every
        /// character outside ASCII <c>[a-zA-Z0-9]</c> becomes a single dash. Example:
        /// <c>C:\Users\Daniel_Liedke</c> → <c>C--Users-Daniel-Liedke</c>.
        /// Non-ASCII letters/digits (e.g. Japanese) are also turned into dashes, matching
        /// the CLI — so <c>char.IsLetterOrDigit</c> (Unicode-aware) must NOT be used here.
        /// </summary>
        private static string EncodeClaudeProjectPath(string workspacePath)
        {
            if (string.IsNullOrEmpty(workspacePath)) return string.Empty;

            var sb = new StringBuilder(workspacePath.Length + 4);
            foreach (char c in workspacePath)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('-');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Resolves the Claude Code projects directory for the active provider and workspace.
        /// For Windows-native Claude Code returns a direct local path; for the WSL provider
        /// shells out to <c>wslpath -w</c> so the directory can still be enumerated through
        /// regular .NET file IO via the <c>\\wsl.localhost\</c> share.
        /// </summary>
        private async Task<string> ResolveSessionDirectoryAsync(AiProvider provider, string workspaceDir)
        {
            if (string.IsNullOrEmpty(workspaceDir)) return null;

            if (provider == AiProvider.ClaudeCode)
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string encoded = EncodeClaudeProjectPath(workspaceDir);
                return Path.Combine(userProfile, ".claude", "projects", encoded);
            }

            if (provider == AiProvider.ClaudeCodeWSL)
            {
                string wslWorkspace = ConvertToWslPath(workspaceDir);
                string encoded = EncodeClaudeProjectPath(wslWorkspace);
                return await ResolveWslSessionDirectoryAsync(encoded);
            }

            return null;
        }

        /// <summary>
        /// Runs <c>wslpath -w "$HOME/.claude/projects/&lt;encoded&gt;"</c> so the WSL session
        /// folder is reachable from native .NET file IO. Returns null on any failure (no WSL,
        /// command not found, empty stdout) and the dialog falls back to "no sessions found".
        /// </summary>
        private async Task<string> ResolveWslSessionDirectoryAsync(string encoded)
        {
            try
            {
                string args = $"bash -lic \"wslpath -w \\\"$HOME/.claude/projects/{encoded}\\\"\"";
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();
                    Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                    bool exited = await Task.Run(() => p.WaitForExit(5000));
                    if (!exited)
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                            // Ignore failures on kill; the caller will fall back to no sessions.
                        }

                        return null;
                    }

                    string stdout = await stdoutTask;
                    await stderrTask;
                    stdout = stdout?.Trim();
                    return string.IsNullOrEmpty(stdout) ? null : stdout;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResolveWslSessionDirectoryAsync error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region JSONL Parsing

        /// <summary>
        /// Lists every <c>*.jsonl</c> transcript under <paramref name="sessionDir"/> and parses
        /// each in parallel on the thread-pool. Files that fail to parse are silently skipped
        /// so one corrupt transcript can't blank out the entire history list.
        /// </summary>
        private async Task<List<SessionInfo>> LoadSessionsAsync(string sessionDir, AiProvider provider)
        {
            var result = new List<SessionInfo>();
            if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir))
            {
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(sessionDir, "*.jsonl", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadSessionsAsync enumerate error: {ex.Message}");
                return result;
            }

            var parsed = await Task.Run(() =>
            {
                var bag = new List<SessionInfo>();
                foreach (string f in files)
                {
                    try
                    {
                        var info = ParseSessionFile(f, provider);
                        if (info != null) bag.Add(info);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Skipping unreadable session file {f}: {ex.Message}");
                    }
                }
                return bag;
            });

            // Apply any user-assigned custom titles (issue #95) — done on the UI-bound
            // result rather than in the thread-pool parse so settings access stays simple.
            var titles = _settings?.SessionCustomTitles;
            if (titles != null && titles.Count > 0)
            {
                foreach (var info in parsed)
                {
                    if (info.SessionId != null &&
                        titles.TryGetValue(info.SessionId, out string title) &&
                        !string.IsNullOrWhiteSpace(title))
                    {
                        info.CustomTitle = title;
                    }
                }
            }

            result.AddRange(parsed.OrderByDescending(s => s.LastModified));
            return result;
        }

        /// <summary>
        /// Reads a single JSONL transcript and extracts the metadata shown in the dialog:
        /// first user-typed prompt (preview), user/assistant turn count, total token usage,
        /// and the originating cwd. Skips system/snapshot/attachment lines because they
        /// inflate counts without representing real conversation turns.
        /// </summary>
        private SessionInfo ParseSessionFile(string filePath, AiProvider provider)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return null;

            string sessionId = Path.GetFileNameWithoutExtension(filePath);
            string preview = string.Empty;
            string cwd = string.Empty;
            int messageCount = 0;
            int tokenCount = 0;

            // Open with FileShare.ReadWrite so we can also peek at the active session
            // that Claude Code itself currently has open for writing.
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    string type = (string)obj["type"];
                    if (string.IsNullOrEmpty(type)) continue;

                    if (string.IsNullOrEmpty(cwd))
                    {
                        cwd = (string)obj["cwd"] ?? string.Empty;
                    }

                    if (type == "user")
                    {
                        // Skip "user" lines whose content is actually a tool_result attachment —
                        // we only want what the human actually typed in the prompt box.
                        var msg = obj["message"];
                        if (msg == null) continue;

                        string text = ExtractUserText(msg["content"]);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        messageCount++;
                        if (string.IsNullOrEmpty(preview))
                        {
                            preview = text.Length > 120 ? text.Substring(0, 120) + "…" : text;
                            preview = preview.Replace("\r", " ").Replace("\n", " ").Trim();
                        }
                    }
                    else if (type == "assistant")
                    {
                        messageCount++;
                        var usage = obj["message"]?["usage"];
                        if (usage != null)
                        {
                            int input = (int?)usage["input_tokens"] ?? 0;
                            int output = (int?)usage["output_tokens"] ?? 0;
                            tokenCount += input + output;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(preview))
            {
                preview = "(no user messages)";
            }

            return new SessionInfo
            {
                SessionId = sessionId,
                FilePath = filePath,
                Preview = preview,
                MessageCount = messageCount,
                TokenCount = tokenCount,
                LastModified = fi.LastWriteTime,
                Cwd = cwd,
                Provider = provider
            };
        }

        /// <summary>
        /// Claude Code transcripts store user content either as a plain string or as an
        /// array of typed parts (text + image refs). This collapses both shapes to a single
        /// preview string while ignoring tool-result re-injections that would otherwise
        /// surface as the "first" user message.
        /// </summary>
        private static string ExtractUserText(JToken content)
        {
            if (content == null) return string.Empty;

            if (content.Type == JTokenType.String)
            {
                return ((string)content) ?? string.Empty;
            }

            if (content.Type == JTokenType.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.Children())
                {
                    string partType = (string)part["type"];
                    if (partType == "text")
                    {
                        string text = (string)part["text"];
                        if (!string.IsNullOrEmpty(text)) sb.Append(text);
                    }
                    // Skip "tool_result" / "image" parts — not human input.
                }
                return sb.ToString();
            }

            return string.Empty;
        }

        #endregion

        #region Toolbar Button + Dialog

        /// <summary>
        /// Shows or hides the History button based on the currently selected provider.
        /// Only Windows-native Claude Code and WSL Claude Code persist resumable sessions;
        /// Codex / Cursor / Devin use different storage and aren't supported here.
        /// </summary>
        private void RefreshSessionHistoryButton()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Session History visibility (button vs "⚙" menu entry, gated to Claude Code
                // providers) is handled centrally so it stays in sync with the other features.
                RefreshToolbarLayout();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing session history button: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true only for Claude Code providers that store transcripts in Claude's
        /// resumable session-history format.
        /// </summary>
        private static bool IsClaudeCodeSessionHistoryProvider(AiProvider? provider)
        {
            return provider == AiProvider.ClaudeCode || provider == AiProvider.ClaudeCodeWSL;
        }

        /// <summary>
        /// Builds the two-line row content for a session. Renamed sessions (issue #95) get a
        /// distinct layout: a colored left accent bar plus a bold, accented title line with a
        /// pencil marker. Un-renamed sessions show the plain auto-generated preview. The dim
        /// metadata line (date + counts) sits above either way.
        /// </summary>
        private static FrameworkElement CreateSessionItemContent(SessionInfo s, Brush themeFg, Brush accentBrush)
        {
            bool renamed = !string.IsNullOrWhiteSpace(s.CustomTitle);

            string ts = s.LastModified.ToString("yyyy-MM-dd HH:mm");
            string tokens = s.TokenCount > 1000
                ? $"{s.TokenCount / 1000.0:0.#}k tokens"
                : $"{s.TokenCount} tokens";
            string meta = $"{ts}   ({s.MessageCount} msgs, {tokens})";

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left accent bar — colored for renamed rows, transparent otherwise (keeps text aligned).
            var accentBar = new Border
            {
                Width = 3,
                Background = renamed ? accentBrush : Brushes.Transparent,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 1, 8, 1)
            };
            Grid.SetColumn(accentBar, 0);
            root.Children.Add(accentBar);

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            Grid.SetColumn(stack, 1);

            stack.Children.Add(new TextBlock
            {
                Text = meta,
                Foreground = themeFg,
                Opacity = 0.6,
                FontSize = 11
            });

            stack.Children.Add(new TextBlock
            {
                Text = renamed ? "✎ " + s.CustomTitle : s.Preview,
                Foreground = renamed ? accentBrush : themeFg,
                FontWeight = renamed ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            });

            root.Children.Add(stack);
            return root;
        }

        /// <summary>
        /// Builds the hover tooltip for a session list item. Shows the original auto-generated
        /// preview alongside the ids when a custom title is in use so it isn't lost.
        /// </summary>
        private static string BuildSessionTooltip(SessionInfo s)
        {
            string tip = $"Session: {s.SessionId}\nFile: {s.FilePath}\ncwd: {s.Cwd}";
            if (!string.IsNullOrWhiteSpace(s.CustomTitle))
            {
                tip += $"\nOriginal: {s.Preview}";
            }
            return tip;
        }

        /// <summary>
        /// Starts intentional fire-and-forget session-history work from void WPF event handlers.
        /// </summary>
        private static void StartSessionHistoryTask(Func<Task> asyncAction, string errorHandlerName)
        {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional for WPF event handlers
            ThreadHelper.JoinableTaskFactory.RunAsync(asyncAction).FileAndForget(errorHandlerName);
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Toolbar button click handler — opens the session-history dialog.
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void SessionHistoryButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ShowSessionHistoryDialogAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening session history: {ex.Message}");
                MessageBox.Show($"Error opening session history: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Builds and shows the session-history modal. The list is loaded asynchronously
        /// after the dialog is visible so the user gets a "Loading…" indicator instead
        /// of a frozen UI on cold reads (network shares, large numbers of transcripts).
        /// </summary>
        private async Task ShowSessionHistoryDialogAsync()
        {
            AiProvider? selected = _settings?.SelectedProvider;
            if (!IsClaudeCodeSessionHistoryProvider(selected))
            {
                MessageBox.Show(
                    "Session history is only available for Claude Code (native or WSL). " +
                    "Switch the active code agent to Claude Code first.",
                    "Session History",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string workspaceDir = await GetWorkspaceDirectoryAsync();
            if (string.IsNullOrEmpty(workspaceDir))
            {
                MessageBox.Show("Could not determine the current workspace directory.",
                    "Session History", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = $"Claude Code Session History — {workspaceDir}",
                Width = 760,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 560,
                MinHeight = 320,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = Application.Current?.MainWindow; } catch { }

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // filter bar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

            // Accent used to make renamed sessions stand out (a frozen green that reads on
            // both dark and light themes). Shared by the row layout and the accent bar.
            var accentBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
            accentBrush.Freeze();

            // --- Row 0: filter bar (search + "Renamed only" toggle) ---
            var filterBar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var filterLabel = new TextBlock
            {
                Text = "Filter:",
                Foreground = themeFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(filterLabel, 0);
            filterBar.Children.Add(filterLabel);

            var searchBox = new TextBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 26,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Type to filter by title, preview text, or date. Double-click a session to resume; " +
                          "right-click or press F2 to rename."
            };
            Grid.SetColumn(searchBox, 1);
            filterBar.Children.Add(searchBox);

            var renamedOnlyCheck = new CheckBox
            {
                Content = "Renamed only",
                Foreground = themeFg,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _settings?.SessionHistoryRenamedOnly == true,
                ToolTip = "Show only sessions you have given a custom title."
            };
            Grid.SetColumn(renamedOnlyCheck, 2);
            filterBar.Children.Add(renamedOnlyCheck);

            Grid.SetRow(filterBar, 0);
            grid.Children.Add(filterBar);

            // --- Row 1: session list ---
            var listBox = new ListBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            // No horizontal scrolling — rows wrap to the panel width instead of overflowing.
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
            // Give each entry breathing room and let the row content stretch so long
            // titles/previews ellipsize instead of overflowing.
            var itemContainerStyle = new Style(typeof(ListBoxItem));
            itemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 5, 6, 6)));
            itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 4)));
            itemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            listBox.ItemContainerStyle = itemContainerStyle;
            Grid.SetRow(listBox, 1);
            grid.Children.Add(listBox);

            var loading = new TextBlock
            {
                Text = "Loading sessions…",
                Foreground = themeFg,
                FontStyle = FontStyles.Italic,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75
            };
            Grid.SetRow(loading, 1);
            grid.Children.Add(loading);

            // --- Row 2: button row (Refresh left, actions right) ---
            Style buttonStyle = GetDialogButtonStyle();
            Func<string, Button> mkBtn = (text) =>
            {
                var b = new Button
                {
                    Content = text,
                    MinWidth = 88,
                    Height = 28,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                if (buttonStyle != null) b.Style = buttonStyle;
                else { b.Background = themeBg; b.Foreground = themeFg; b.BorderBrush = themeFg; }
                return b;
            };

            var resumeButton = mkBtn("Resume");
            var continueButton = mkBtn("Resume Last Session");
            var renameButton = mkBtn("Rename");
            var deleteButton = mkBtn("Delete");
            var refreshButton = mkBtn("Refresh");
            var closeButton = mkBtn("Close");
            closeButton.IsCancel = true;

            var buttonBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(buttonBar, 2);

            // Refresh sits apart on the left; the destructive/launch actions group on the right.
            refreshButton.Margin = new Thickness(0);
            DockPanel.SetDock(refreshButton, Dock.Left);
            buttonBar.Children.Add(refreshButton);

            var rightActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(rightActions, Dock.Right);
            rightActions.Children.Add(renameButton);
            rightActions.Children.Add(deleteButton);
            rightActions.Children.Add(continueButton);
            rightActions.Children.Add(resumeButton);
            rightActions.Children.Add(closeButton);
            buttonBar.Children.Add(rightActions);

            grid.Children.Add(buttonBar);

            dialog.Content = grid;

            // Full set of sessions for the workspace; the visible list is a filtered view of this.
            var loadedSessions = new List<SessionInfo>();

            // Rebuilds the visible list from loadedSessions, applying the search text and the
            // "Renamed only" toggle, and preserves the current selection where possible.
            Action applyFilter = () =>
            {
                string previouslySelectedId = (listBox.SelectedItem as ListBoxItem)?.Tag is SessionInfo psi
                    ? psi.SessionId : null;

                string query = (searchBox.Text ?? string.Empty).Trim();
                bool renamedOnly = renamedOnlyCheck.IsChecked == true;

                listBox.Items.Clear();

                int reselectIndex = -1;
                foreach (var s in loadedSessions)
                {
                    bool isRenamed = !string.IsNullOrWhiteSpace(s.CustomTitle);
                    if (renamedOnly && !isRenamed) continue;

                    if (query.Length > 0)
                    {
                        string haystack = $"{s.CustomTitle}\n{s.Preview}\n{s.LastModified:yyyy-MM-dd HH:mm}";
                        if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }

                    var lbi = new ListBoxItem
                    {
                        Content = CreateSessionItemContent(s, themeFg, accentBrush),
                        Tag = s,
                        Background = themeBg,
                        Foreground = themeFg,
                        ToolTip = BuildSessionTooltip(s)
                    };
                    if (s.SessionId == previouslySelectedId) reselectIndex = listBox.Items.Count;
                    listBox.Items.Add(lbi);
                }

                if (listBox.Items.Count == 0)
                {
                    loading.Visibility = Visibility.Visible;
                    loading.Text = loadedSessions.Count == 0
                        ? loading.Text  // keep the "no sessions on disk" message set by the loader
                        : "No sessions match the current filter.";
                }
                else
                {
                    loading.Visibility = Visibility.Collapsed;
                    listBox.SelectedIndex = reselectIndex >= 0 ? reselectIndex : 0;
                }
            };

            // Loader — populates loadedSessions once parsing finishes, then renders via the filter.
            Func<Task> loadAsync = async () =>
            {
                loadedSessions.Clear();
                listBox.Items.Clear();
                loading.Visibility = Visibility.Visible;
                loading.Text = "Loading sessions…";

                string sessionDir = await ResolveSessionDirectoryAsync(selected.Value, workspaceDir);
                List<SessionInfo> sessions = await LoadSessionsAsync(sessionDir, selected.Value);

                if (sessions.Count == 0)
                {
                    loading.Visibility = Visibility.Visible;
                    loading.Text = string.IsNullOrEmpty(sessionDir)
                        ? "WSL not available or claude project folder unreachable."
                        : $"No sessions found in:\n{sessionDir}";
                    return;
                }

                loadedSessions.AddRange(sessions);
                applyFilter();
            };

            // Persist the "Renamed only" toggle so it survives across sessions (issue #95).
            Action saveRenamedOnly = () =>
            {
                bool on = renamedOnlyCheck.IsChecked == true;
                if (_settings != null && _settings.SessionHistoryRenamedOnly != on)
                {
                    _settings.SessionHistoryRenamedOnly = on;
                    SaveSettings();
                }
                applyFilter();
            };

            searchBox.TextChanged += (s, args) => applyFilter();
            renamedOnlyCheck.Checked += (s, args) => saveRenamedOnly();
            renamedOnlyCheck.Unchecked += (s, args) => saveRenamedOnly();

            // Wire up buttons.
            Func<ListBoxItem> currentSelectionItem = () => listBox.SelectedItem as ListBoxItem;
            Func<SessionInfo> currentSelection = () => currentSelectionItem()?.Tag as SessionInfo;

            // Rename the selected session (issue #95). Persisted in settings keyed by
            // session UUID so it survives restarts; an empty title clears the override.
            Action renameSelected = () =>
            {
                var sel = currentSelection();
                if (sel == null) return;

                string newTitle = ShowRenameSessionDialog(sel, dialog);
                if (newTitle == null) return; // cancelled

                newTitle = newTitle.Trim();
                if (_settings.SessionCustomTitles == null)
                {
                    _settings.SessionCustomTitles =
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                if (string.IsNullOrEmpty(newTitle))
                {
                    _settings.SessionCustomTitles.Remove(sel.SessionId);
                    sel.CustomTitle = string.Empty;
                }
                else
                {
                    _settings.SessionCustomTitles[sel.SessionId] = newTitle;
                    sel.CustomTitle = newTitle;
                }

                SaveSettings();
                applyFilter(); // re-render so the renamed-row layout (and any filter) updates
            };

            // Delete the selected transcript (and any custom title it carried).
            Action deleteSelected = () =>
            {
                var sel = currentSelection();
                if (sel == null) return;

                var confirm = MessageBox.Show(
                    $"Delete this session transcript?\n\n{sel.FilePath}\n\nThis cannot be undone.",
                    "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                try
                {
                    File.Delete(sel.FilePath);
                    if (_settings.SessionCustomTitles != null &&
                        _settings.SessionCustomTitles.Remove(sel.SessionId))
                    {
                        SaveSettings();
                    }
                    loadedSessions.Remove(sel);
                    applyFilter();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            // Right-click context menu on the list (issue #95): Rename / Delete.
            var listContextMenu = new ContextMenu();
            var renameMenuItem = new MenuItem { Header = "Rename…" };
            renameMenuItem.Click += (s, args) => renameSelected();
            var deleteMenuItem = new MenuItem { Header = "Delete" };
            deleteMenuItem.Click += (s, args) => deleteSelected();
            listContextMenu.Items.Add(renameMenuItem);
            listContextMenu.Items.Add(deleteMenuItem);
            // Only enable the items when a row is selected.
            listContextMenu.Opened += (s, args) =>
            {
                bool hasSel = currentSelection() != null;
                renameMenuItem.IsEnabled = hasSel;
                deleteMenuItem.IsEnabled = hasSel;
            };
            listBox.ContextMenu = listContextMenu;

            // WPF Click handlers must be void-returning, so async work is fired off via
            // JoinableTaskFactory rather than `async (s, args) =>` lambdas (which would
            // swallow exceptions and trip VSTHRD101).
            resumeButton.Click += (s, args) =>
            {
                var sel = currentSelection();
                if (sel == null) return;
                dialog.DialogResult = true;
                StartSessionHistoryTask(() => ResumeSessionAsync(sel), "claudecode/sessionhistory");
            };

            listBox.MouseDoubleClick += (s, args) =>
            {
                var sel = currentSelection();
                if (sel == null) return;
                dialog.DialogResult = true;
                StartSessionHistoryTask(() => ResumeSessionAsync(sel), "claudecode/sessionhistory");
            };

            // F2 renames the selected session (issue #95) — the familiar shell rename key.
            listBox.PreviewKeyDown += (s, args) =>
            {
                if (args.Key == Key.F2)
                {
                    args.Handled = true;
                    renameSelected();
                }
            };

            continueButton.Click += (s, args) =>
            {
                dialog.DialogResult = true;
                StartSessionHistoryTask(() => ContinueLastSessionAsync(), "claudecode/sessionhistory");
            };

            renameButton.Click += (s, args) => renameSelected();

            deleteButton.Click += (s, args) => deleteSelected();

            refreshButton.Click += (s, args) =>
            {
                StartSessionHistoryTask(loadAsync, "claudecode/sessionhistory/refresh");
            };
            closeButton.Click += (s, args) => { dialog.DialogResult = false; };

            dialog.Loaded += (s, args) =>
            {
                StartSessionHistoryTask(loadAsync, "claudecode/sessionhistory/load");
            };
            dialog.ShowDialog();
        }

        /// <summary>
        /// Shows a small themed prompt for renaming a session (issue #95). Returns the new
        /// title (which may be empty to clear the override) or null if the user cancelled.
        /// </summary>
        private string ShowRenameSessionDialog(SessionInfo session, Window owner)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = "Rename Session",
                Width = 480,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Owner = owner
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Custom title (leave empty to restore the auto-generated preview):",
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var titleBox = new TextBox
            {
                Text = session.CustomTitle ?? string.Empty,
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(titleBox, 1);
            grid.Children.Add(titleBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 3);

            Style editorButtonStyle = GetDialogButtonStyle();
            Func<string, bool, bool, Button> mkBtn = (text, isDefault, isCancel) =>
            {
                var b = new Button
                {
                    Content = text,
                    Width = 75,
                    Height = 25,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsDefault = isDefault,
                    IsCancel = isCancel
                };
                if (editorButtonStyle != null) b.Style = editorButtonStyle;
                else { b.Background = themeBg; b.Foreground = themeFg; b.BorderBrush = themeFg; }
                return b;
            };

            var okButton = mkBtn("OK", true, false);
            var cancelButton = mkBtn("Cancel", false, true);
            cancelButton.Margin = new Thickness(0);
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            grid.Children.Add(buttonPanel);

            okButton.Click += (s, args) => { dialog.DialogResult = true; };

            dialog.Content = grid;
            dialog.Loaded += (s, args) => { titleBox.SelectAll(); titleBox.Focus(); };

            bool? ok = dialog.ShowDialog();
            return ok == true ? (titleBox.Text ?? string.Empty) : null;
        }

        #endregion

        #region Resume Integration

        /// <summary>
        /// Stops the current terminal (if any), arms the one-shot resume token consumed by
        /// <c>GetClaudeCommand</c>, and restarts the terminal with the matching provider.
        /// Switching native↔WSL is intentional — a session created in WSL must be resumed
        /// in WSL because <c>--resume</c> looks up the transcript by the encoded cwd.
        /// </summary>
        private async Task ResumeSessionAsync(SessionInfo session)
        {
            if (session == null) return;

            _settings.SelectedProvider = session.Provider;
            SaveSettings();

            _pendingResumeSessionId = session.SessionId;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            UpdateProviderSelection();

            await RestartTerminalWithSelectedProviderAsync();
        }

        /// <summary>
        /// Resumes the most recent session in the current cwd via <c>claude --continue</c>.
        /// Honors whichever Claude variant (native vs WSL) is currently selected.
        /// </summary>
        private async Task ContinueLastSessionAsync()
        {
            AiProvider? selected = _settings?.SelectedProvider;
            if (!IsClaudeCodeSessionHistoryProvider(selected))
            {
                return;
            }

            _pendingResumeSessionId = "-c";
            await RestartTerminalWithSelectedProviderAsync();
        }

        #endregion
    }
}
