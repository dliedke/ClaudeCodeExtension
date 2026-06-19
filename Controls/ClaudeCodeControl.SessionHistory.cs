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
        /// Codex / Cursor / Windsurf use different storage and aren't supported here.
        /// </summary>
        private void RefreshSessionHistoryButton()
        {
            try
            {
                if (SessionHistoryViewMenuItem == null) return;

                SessionHistoryViewMenuItem.Visibility = IsClaudeCodeSessionHistoryProvider(_settings?.SelectedProvider)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "Past Claude Code sessions for this workspace. Double-click or Resume to relaunch with " +
                       "claude --resume <id>; Resume Last Session sends claude --continue (most recent session in cwd). " +
                       "Active terminal will be restarted.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var listBox = new ListBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                FontFamily = new FontFamily("Cascadia Mono, Consolas")
            };
            Grid.SetRow(listBox, 1);
            grid.Children.Add(listBox);

            var loading = new TextBlock
            {
                Text = "Loading sessions…",
                Foreground = themeFg,
                FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75
            };
            Grid.SetRow(loading, 1);
            grid.Children.Add(loading);

            // Bottom button row
            var bottomPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(bottomPanel, 2);

            Style buttonStyle = GetDialogButtonStyle();
            Func<string, Button> mkBtn = (text) =>
            {
                var b = new Button
                {
                    Content = text,
                    MinWidth = 110,
                    Height = 28,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                if (buttonStyle != null) b.Style = buttonStyle;
                else { b.Background = themeBg; b.Foreground = themeFg; b.BorderBrush = themeFg; }
                return b;
            };

            var resumeButton = mkBtn("Resume");
            var continueButton = mkBtn("Resume Last Session");
            var deleteButton = mkBtn("Delete");
            var refreshButton = mkBtn("Refresh");
            var closeButton = mkBtn("Close");
            closeButton.IsCancel = true;
            closeButton.Margin = new Thickness(0);

            bottomPanel.Children.Add(refreshButton);
            bottomPanel.Children.Add(deleteButton);
            bottomPanel.Children.Add(continueButton);
            bottomPanel.Children.Add(resumeButton);
            bottomPanel.Children.Add(closeButton);
            grid.Children.Add(bottomPanel);

            dialog.Content = grid;

            // Loader — populates the listbox once parsing finishes.
            Func<Task> loadAsync = async () =>
            {
                listBox.Items.Clear();
                loading.Visibility = Visibility.Visible;
                loading.Text = "Loading sessions…";

                string sessionDir = await ResolveSessionDirectoryAsync(selected.Value, workspaceDir);
                List<SessionInfo> sessions = await LoadSessionsAsync(sessionDir, selected.Value);

                loading.Visibility = Visibility.Collapsed;

                if (sessions.Count == 0)
                {
                    loading.Visibility = Visibility.Visible;
                    loading.Text = string.IsNullOrEmpty(sessionDir)
                        ? "WSL not available or claude project folder unreachable."
                        : $"No sessions found in:\n{sessionDir}";
                    return;
                }

                foreach (var s in sessions)
                {
                    string ts = s.LastModified.ToString("yyyy-MM-dd HH:mm");
                    string tokens = s.TokenCount > 1000
                        ? $"{s.TokenCount / 1000.0:0.#}k tokens"
                        : $"{s.TokenCount} tokens";
                    string label = $"{ts}   ({s.MessageCount} msgs, {tokens})\n   {s.Preview}";

                    var lbi = new ListBoxItem
                    {
                        Content = label,
                        Tag = s,
                        Background = themeBg,
                        Foreground = themeFg,
                        ToolTip = $"Session: {s.SessionId}\nFile: {s.FilePath}\ncwd: {s.Cwd}"
                    };
                    listBox.Items.Add(lbi);
                }

                if (listBox.Items.Count > 0)
                {
                    listBox.SelectedIndex = 0;
                }
            };

            // Wire up buttons.
            Func<SessionInfo> currentSelection = () =>
            {
                var lbi = listBox.SelectedItem as ListBoxItem;
                return lbi?.Tag as SessionInfo;
            };

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

            continueButton.Click += (s, args) =>
            {
                dialog.DialogResult = true;
                StartSessionHistoryTask(() => ContinueLastSessionAsync(), "claudecode/sessionhistory");
            };

            deleteButton.Click += (s, args) =>
            {
                var sel = currentSelection();
                if (sel == null) return;

                var result = MessageBox.Show(
                    $"Delete this session transcript?\n\n{sel.FilePath}\n\nThis cannot be undone.",
                    "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;

                try
                {
                    File.Delete(sel.FilePath);
                    int idx = listBox.SelectedIndex;
                    listBox.Items.RemoveAt(idx);
                    if (listBox.Items.Count > 0)
                    {
                        listBox.SelectedIndex = Math.Min(idx, listBox.Items.Count - 1);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

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
