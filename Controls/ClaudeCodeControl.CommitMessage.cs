/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Generate Commit Message" feature. Asks the active native-mode agent to write a
 *          commit message from the current git diff, then fills it into Visual Studio's
 *          built-in Git Changes tool window via best-effort UI Automation (there is no public
 *          API to write into that window). Falls back to the clipboard when the window or its
 *          commit message box cannot be located.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Manual send (toolbar button)

        /// <summary>Same threshold the build-error prompt uses for its "send as file" fallback.</summary>
        private const int LargeCommitDiffThresholdChars = 1024;

        /// <summary>How long "git diff HEAD" is given before it is treated as failed.</summary>
        private const int GitDiffTimeoutMs = 15000;

        /// <summary>
        /// Toolbar/menu handler for "Generate Commit Message". Collects the current git diff, asks
        /// the active native-mode agent to write a commit message from it, and fills the result into
        /// the Git Changes tool window. Requires Native Mode: there is no reliable way to capture a
        /// clean text answer from the terminal path (only screen-scraping, already used as a
        /// last-resort heuristic elsewhere and unfit for feeding a UI text box).
        /// Plain <c>async void</c>, matching every other toolbar click handler in this codebase.
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void GenerateCommitMessageButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!IsNativeModeActive)
                {
                    MessageBox.Show(
                        "Generate Commit Message requires Native Mode. Enable it in ⚙ Settings... → Terminal → Use native mode, then start a session.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (!IsAgentAvailable)
                {
                    MessageBox.Show(
                        "No agent is running. Start an agent session before generating a commit message.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string workspace = await GetWorkspaceDirectoryAsync();
                string repoRoot = FindGitRepositoryRoot(workspace);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    MessageBox.Show(
                        "No git repository was found for the current workspace.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                string diff = RunGitCommand(repoRoot, "diff HEAD", GitDiffTimeoutMs);
                if (string.IsNullOrWhiteSpace(diff))
                {
                    MessageBox.Show(
                        "There are no changes to describe.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                string sessionId = ResolveFocusedNativeSessionId();
                IAgentSession session = string.IsNullOrEmpty(sessionId)
                    ? _agentSession
                    : GetSession(sessionId)?.AgentSession;
                bool turnInFlight = string.IsNullOrEmpty(sessionId)
                    ? _nativeTurnInFlight
                    : (GetSession(sessionId)?.TurnInFlight ?? false);

                if (session == null)
                {
                    MessageBox.Show(
                        "No native agent session is available.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (turnInFlight)
                {
                    MessageBox.Show(
                        "The agent is busy with another turn. Wait for it to finish before generating a commit message.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                string prompt = BuildCommitMessagePrompt(diff);

                Task<string> capture = CaptureNextAssistantTurnAsync(session, TimeSpan.FromMilliseconds(SideQuestionTimeoutMs));
                await SendTextToAgentAsync(prompt);
                string response = await capture;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string message = CleanCommitMessage(response);
                if (string.IsNullOrWhiteSpace(message))
                {
                    MessageBox.Show(
                        "The agent did not return a commit message.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!await TryFillGitChangesCommitMessageAsync(message))
                {
                    await ClipboardRetryAsync(() => Clipboard.SetText(message));
                    MessageBox.Show(
                        "Could not find the Git Changes commit message box. The generated message was copied to the clipboard — paste it with Ctrl+V.",
                        "Generate Commit Message",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GenerateCommitMessageButton_Click error: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the prompt sent to the agent. Diffs over <see cref="LargeCommitDiffThresholdChars"/>
        /// are written to a temp file and referenced instead of pasted inline, mirroring the build-error
        /// send path — avoids both a fragile large paste and flooding the chat transcript with the raw diff.
        /// </summary>
        private string BuildCommitMessagePrompt(string diff)
        {
            const string instructions =
                "Write a concise git commit message for the following diff. " +
                "Use a conventional-commit style summary line of 72 characters or fewer, " +
                "and an optional short body with additional context if needed. " +
                "Reply with ONLY the commit message text — no code fences, no markdown, no explanation.";

            string prompt = instructions + "\n\n" + diff;

            if (prompt.Length <= LargeCommitDiffThresholdChars)
            {
                return prompt;
            }

            try
            {
                string sessionDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
                Directory.CreateDirectory(sessionDir);
                string diffFile = Path.Combine(sessionDir, $"commit-diff-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                File.WriteAllText(diffFile, prompt, new UTF8Encoding(false));

                bool isWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                     _currentRunningProvider == AiProvider.ClaudeCodeWSL ||
                                     _currentRunningProvider == AiProvider.CursorAgent ||
                                     _currentRunningProvider == AiProvider.Devin;
                string displayPath = isWSLProvider ? ConvertToWslPath(diffFile) : diffFile;
                return $"Read the diff in {displayPath} and reply with only the commit message.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save commit-diff prompt to file, falling back to inline send: {ex.Message}");
                return prompt;
            }
        }

        /// <summary>Trims the response and strips a whole-message code fence, if the model added one despite being asked not to.</summary>
        private static string CleanCommitMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();

            if (trimmed.Length >= 6 &&
                trimmed.StartsWith("```", StringComparison.Ordinal) &&
                trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = trimmed.IndexOf('\n');
                int contentStart = firstNewline >= 0 ? firstNewline + 1 : 3;
                int contentEnd = trimmed.Length - 3;
                if (contentEnd > contentStart)
                {
                    trimmed = trimmed.Substring(contentStart, contentEnd - contentStart).Trim();
                }
            }

            return trimmed;
        }

        #endregion

        #region Response capture

        /// <summary>
        /// Taps <paramref name="session"/>'s live event stream in parallel with the normal chat-rendering
        /// subscriber (events are multicast, so this does not disturb that path) and resolves with the
        /// accumulated assistant text once the turn completes. Works for any provider without per-CLI
        /// code, unlike a Claude-specific <c>--print</c> subprocess. Resolves with null on session error
        /// or timeout.
        /// </summary>
        private Task<string> CaptureNextAssistantTurnAsync(IAgentSession session, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var buffer = new StringBuilder();
            var gate = new object();

            EventHandler<AgentEvent> handler = null;
            handler = (s, agentEvent) =>
            {
                if (agentEvent == null) return;

                switch (agentEvent.Kind)
                {
                    case AgentEventKind.AssistantText:
                        lock (gate) { buffer.Append(agentEvent.Text); }
                        break;

                    case AgentEventKind.SessionError:
                        session.Received -= handler;
                        tcs.TrySetResult(null);
                        break;

                    case AgentEventKind.TurnCompleted:
                        session.Received -= handler;
                        string text;
                        lock (gate) { text = buffer.ToString(); }
                        tcs.TrySetResult(text);
                        break;
                }
            };

            session.Received += handler;

            _ = Task.Delay(timeout).ContinueWith(delegate
            {
                if (tcs.TrySetResult(null))
                {
                    session.Received -= handler;
                }
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        #endregion

        #region Git Changes window automation (best-effort)

        private static readonly string[] GitChangesCommandCandidates =
        {
            "Team.Git.Changes",
            "View.GitChanges"
        };

        /// <summary>
        /// Best-effort fill of the Git Changes tool window's commit message box via UI Automation —
        /// there is no supported Visual Studio API to read or write it. Returns false on any failure
        /// (window/field not found, automation exception), letting the caller fall back to the
        /// clipboard rather than leaving the user with no way to get the generated message.
        /// </summary>
        private async Task<bool> TryFillGitChangesCommitMessageAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.MainWindow == null) return false;

                IntPtr mainHwnd = (IntPtr)dte.MainWindow.HWnd;

                if (!IsGitChangesWindowOpen(dte))
                {
                    if (!TryOpenGitChangesWindow(dte))
                    {
                        return false;
                    }

                    await Task.Delay(400);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }

                var root = System.Windows.Automation.AutomationElement.FromHandle(mainHwnd);
                if (root == null) return false;

                var container = FindDescendantByNameContains(root, "Git Changes", 6000) ?? root;
                var editElement = FindCommitMessageEditElement(container);
                if (editElement == null) return false;

                if (!(editElement.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)
                        is System.Windows.Automation.ValuePattern valuePattern))
                {
                    return false;
                }

                valuePattern.SetValue(message);
                SetForegroundWindow(mainHwnd);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryFillGitChangesCommitMessageAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Checks whether a Git Changes window is already open, and brings it to front if so.</summary>
        private static bool IsGitChangesWindowOpen(EnvDTE.DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                foreach (EnvDTE.Window window in dte.Windows)
                {
                    string caption;
                    try { caption = window.Caption; } catch { continue; }

                    if (!string.IsNullOrEmpty(caption) &&
                        caption.IndexOf("Git Changes", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { window.Visible = true; window.Activate(); } catch { }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsGitChangesWindowOpen error: {ex.Message}");
            }

            return false;
        }

        /// <summary>Tries each candidate command name until one opens the Git Changes window.</summary>
        private static bool TryOpenGitChangesWindow(EnvDTE.DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (string command in GitChangesCommandCandidates)
            {
                try
                {
                    dte.ExecuteCommand(command);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"TryOpenGitChangesWindow: '{command}' failed: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Breadth-first search for the first descendant whose Name contains <paramref name="substring"/>,
        /// bounded by <paramref name="maxNodes"/> so a miss on an unfamiliar VS version degrades to "not
        /// found" quickly instead of walking the entire main window's automation tree.
        /// </summary>
        private static System.Windows.Automation.AutomationElement FindDescendantByNameContains(
            System.Windows.Automation.AutomationElement root, string substring, int maxNodes)
        {
            if (root == null) return null;

            var queue = new System.Collections.Generic.Queue<System.Windows.Automation.AutomationElement>();
            queue.Enqueue(root);
            int visited = 0;

            while (queue.Count > 0 && visited < maxNodes)
            {
                System.Windows.Automation.AutomationElement current = queue.Dequeue();
                visited++;

                string name;
                try { name = current.Current.Name; } catch { name = null; }

                if (!string.IsNullOrEmpty(name) && name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return current;
                }

                System.Windows.Automation.AutomationElementCollection children;
                try { children = current.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition); }
                catch { continue; }

                foreach (System.Windows.Automation.AutomationElement child in children)
                {
                    queue.Enqueue(child);
                }
            }

            return null;
        }

        /// <summary>
        /// Picks the commit message box among the container's Edit descendants. WPF exposes no reliable
        /// "is multiline" automation property here, so the tallest Edit control is used as a heuristic —
        /// the multiline commit box is visually taller than the single-line file filter box in the same window.
        /// </summary>
        private static System.Windows.Automation.AutomationElement FindCommitMessageEditElement(
            System.Windows.Automation.AutomationElement container)
        {
            try
            {
                var condition = new System.Windows.Automation.AndCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Edit),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.IsValuePatternAvailableProperty, true));

                System.Windows.Automation.AutomationElementCollection candidates =
                    container.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);

                System.Windows.Automation.AutomationElement best = null;
                double bestHeight = -1;

                foreach (System.Windows.Automation.AutomationElement candidate in candidates)
                {
                    double height;
                    try { height = candidate.Current.BoundingRectangle.Height; }
                    catch { continue; }

                    if (height > bestHeight)
                    {
                        bestHeight = height;
                        best = candidate;
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FindCommitMessageEditElement error: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
