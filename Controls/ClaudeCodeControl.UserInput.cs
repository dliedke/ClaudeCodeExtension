/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: User input handling - keyboard events, send button, and prompt submission
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Prompt History Fields

        /// <summary>
        /// Current index in the prompt history (-1 means not navigating history)
        /// </summary>
        private int _historyIndex = -1;

        /// <summary>
        /// Temporary storage for current text when navigating history
        /// </summary>
        private string _tempCurrentText = string.Empty;

        /// <summary>
        /// Temporary storage for current attached file paths when navigating history
        /// </summary>
        private List<string> _tempCurrentFiles = new List<string>();

        /// <summary>
        /// Maximum number of prompts to keep in history
        /// </summary>
        private const int MaxHistorySize = 50;

        /// <summary>
        /// Re-entrancy guard for prompt submission. A single send takes ~2 seconds (focus +
        /// paste delays) and the prompt/attachments aren't cleared until it finishes, so a second
        /// click or Enter during that window would re-send the same prompt (and re-attach the same
        /// files). Set synchronously at the very top of SendButton_Click before any await and reset
        /// in finally, so a concurrent UI-thread invocation is rejected. See issue #63.
        /// </summary>
        private bool _isSendingPrompt;

        #endregion

        #region Send Button and Prompt Submission

        /// <summary>True for the providers that run inside WSL and therefore need /mnt/ paths.</summary>
        private static bool IsWslProvider(AiProvider? provider)
        {
            return provider == AiProvider.Codex
                || provider == AiProvider.ClaudeCodeWSL
                || provider == AiProvider.CursorAgent
                || provider == AiProvider.Devin;
        }

        /// <summary>
        /// Renders the "Files attached:" header the agent sees, copying each attachment into a
        /// per-prompt temp folder first so editing or deleting the original afterwards can't change
        /// what the agent reads. A file that fails to copy is still listed at its original path —
        /// dropping it silently would be worse than pointing at a volatile file.
        /// <para>
        /// Shared by the panel's send path and the per-session chat tabs, which stage their own
        /// attachment lists.
        /// </para>
        /// </summary>
        private string BuildAttachmentPromptBlock(IEnumerable<string> files, bool isWslProvider)
        {
            var block = new StringBuilder();

            // Create a unique directory under ClaudeCodeVS_Session for this prompt with files
            string promptDirectory = null;
            try
            {
                promptDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
                Directory.CreateDirectory(promptDirectory);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating temp directory: {ex.Message}");
                promptDirectory = null;
            }

            block.AppendLine("Files attached:");
            foreach (string filePath in files)
            {
                try
                {
                    string displayPath;

                    // Try to copy file to temp directory for persistence
                    if (promptDirectory != null && File.Exists(filePath))
                    {
                        string fileName = Path.GetFileName(filePath);
                        string tempPath = GetUniquePromptAttachmentPath(promptDirectory, fileName);
                        File.Copy(filePath, tempPath, false);
                        displayPath = isWslProvider ? ConvertToWslPath(tempPath) : tempPath;
                    }
                    else
                    {
                        // Use original path if copy fails or file doesn't exist
                        displayPath = isWslProvider ? ConvertToWslPath(filePath) : filePath;
                    }

                    block.AppendLine($"  - {displayPath}");
                    Debug.WriteLine($"File attached to prompt: {filePath}");
                }
                catch (Exception ex)
                {
                    // Always include the file path even if copy fails
                    Debug.WriteLine($"Error processing file {filePath}: {ex.Message}");
                    try
                    {
                        string displayPath = isWslProvider ? ConvertToWslPath(filePath) : filePath;
                        block.AppendLine($"  - {displayPath}");
                    }
                    catch
                    {
                        // Last resort: use the raw path
                        block.AppendLine($"  - {filePath}");
                    }
                }
            }
            block.AppendLine();

            return block.ToString();
        }

        /// <summary>
        /// Handles send button click - sends the prompt to the terminal
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void SendButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            // Reached only from the send button and the prompt box's key handlers, so this already runs
            // on the UI thread and the switch completes synchronously — it does not yield, which is what
            // keeps the re-entrancy guard below synchronous with the caller. Asserting instead would be
            // the wrong tool in an async method (VSTHRD109); this states the requirement to VSTHRD010
            // for every control member touched from here on.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string queuedPrompt = PromptTextBox.Text.Trim();
            bool queuedPromptHasFiles = attachedImagePaths.Any();

            // A running Codex native-chat turn owns its own FIFO. Accept a plain follow-up before the
            // generic submission guard: this is what keeps Enter/Send responsive while the CLI process
            // is still producing the previous answer.
            if (TryQueueActiveCodexNativeFollowUp(queuedPrompt, queuedPromptHasFiles))
            {
                AddToPromptHistory(queuedPrompt, attachedImagePaths.ToList());
                FinishPromptSubmission();
                return;
            }

            // Re-entrancy guard: reject a second click/Enter while a send is already in flight.
            // Checked and set synchronously before any await (UI thread), so a concurrent
            // invocation can't re-send the same prompt and re-attach the same files. See issue #63.
            if (_isSendingPrompt)
            {
                return;
            }

            try
            {
                string prompt = PromptTextBox.Text.Trim();
                bool hasFiles = attachedImagePaths.Any();

                // Allow sending if there's text OR attached files
                if (string.IsNullOrEmpty(prompt) && !hasFiles)
                {
                    MessageBox.Show("Please enter a prompt.", "No Prompt", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Native mode's typed selector commands (/plan, /model, /effort) drive the extension's
                // own pickers and are never sent on. Handled here, before the prompt is assembled, so
                // both input boxes get them — the chat composer routes its text through this same path.
                // Attachments mean the user meant to send something, so the command is left alone.
                if (IsNativeModeActive && !hasFiles && TryHandleNativeSelectorCommand(prompt))
                {
                    PromptTextBox.Clear();
                    return;
                }

                _isSendingPrompt = true;
                if (SendPromptButton != null) SendPromptButton.IsEnabled = false;

                StringBuilder fullPrompt = new StringBuilder();

                // Check if CURRENTLY RUNNING provider is WSL-based (not CodexNative, CursorAgentNative).
                // Hoisted out of the hasFiles branch so the large-prompt-as-file path can use it too.
                bool isWSLProvider = IsWslProvider(_currentRunningProvider);

                // If files are attached, include their paths in the prompt
                if (hasFiles)
                {
                    fullPrompt.Append(BuildAttachmentPromptBlock(attachedImagePaths, isWSLProvider));
                }

                // Add user's prompt text (if any)
                if (!string.IsNullOrEmpty(prompt))
                {
                    fullPrompt.AppendLine(prompt);
                }

                // Add to prompt history (before clearing) - only if there's text
                if (!string.IsNullOrEmpty(prompt))
                {
                    AddToPromptHistory(prompt, attachedImagePaths.ToList());
                }

                // Ensure tracking is active and reset baseline before sending prompt
                await EnsureDiffTrackingStartedAsync(false);

                // Everything from here on touches the control and the agent session, and the await
                // above is not guaranteed to have resumed on the UI thread. A no-op when it did.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Auto-open changes view if enabled and project is in git
                if (_settings != null && _settings.AutoOpenChangesOnPrompt && !string.IsNullOrEmpty(_gitRepositoryRoot))
                {
                    await AutoOpenChangesViewAsync();
                }

                // Send to terminal
                string finalPrompt = fullPrompt.ToString();
                string textToSend = finalPrompt;

                // Native mode delivers the prompt over the agent's structured channel. None of the
                // clipboard / keystroke / large-prompt-as-file handling below applies there — all of
                // it exists only because a console window is a lossy input device.
                if (IsNativeModeActive)
                {
                    // Clear the panel's prompt and attachments immediately: the native turn may take a
                    // long time and the user should not see the sent prompt stuck in the input box.
                    FinishPromptSubmission();

                    // Both Windows and WSL Codex accept follow-ups in native chat while their current
                    // one-shot process is running, and Devin's long-lived ACP session accepts them the
                    // same way since its agent only expects one outstanding turn at a time. Do not keep
                    // the prompt-submission guard held for the duration of that turn: the native queue
                    // owns serialization from here.
                    if (SupportsQueuedNativeFollowUps(_currentRunningProvider))
                    {
#pragma warning disable VSSDK007 // Deliberately detached: the native queue owns the long-running turn
                        ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                        {
                            await SendPromptToNativeAgentAsync(finalPrompt);
                        }).FileAndForget("claudecode/codexnative/send");
#pragma warning restore VSSDK007
                        return;
                    }

                    await SendPromptToNativeAgentAsync(finalPrompt);
                    return;
                }

                // "Disable clipboard" mode (issue #61): never touch the clipboard. Always write the
                // prompt to a temp file and inject only a short reference via simulated keystrokes, so
                // an app holding the clipboard can't break the send. Only available with conhost
                // (Command Prompt) — Windows Terminal (_wtTabBarHeight > 0) doesn't accept the posted
                // WM_CHAR keystrokes, so fall back to the normal clipboard paste path there.
                // PI is excluded: its TUI reacts to per-character WM_CHAR by flooding the input with
                // cursor-position responses and crashing (issue #82), so it must use the clipboard
                // paste path regardless — the prompt is still written to a file so the pasted
                // reference stays short.
                bool disableClipboardRequested = _settings != null
                    && _settings.DisableClipboardSend
                    && _wtTabBarHeight == 0;
                bool isPiProvider = _currentRunningProvider == AiProvider.Pi;
                // Reasonix is a TUI like PI — per-character keystrokes flood it, so keep it on the
                // clipboard/WM_COMMAND paste path even when "Disable clipboard" is on.
                bool isReasonix = _currentRunningProvider == AiProvider.Reasonix;
                // Devin (native) is the same `devin` TUI as Devin — keep it on the clipboard
                // paste path too so per-character keystrokes don't flood it.
                bool isDevinNative = _currentRunningProvider == AiProvider.DevinNative;
                // Windows 10's console host has no bracketed-paste support, so a TUI receives the
                // delivered text as ordinary keystrokes. Under that host the per-character WM_CHAR
                // path floods the agent's input the same way it did before the issue #83 fix — the
                // reporter saw the flood persist with "Disable clipboard" enabled. Keep Windows 10
                // on the handle-targeted conhost paste (one WM_COMMAND, no synthetic keystrokes),
                // and send the prompt as a short file reference so the burst stays minimal.
                bool legacyWin10Conhost = _wtTabBarHeight == 0 && IsLegacyWindows10Console();
                bool clipboardFree = disableClipboardRequested && !isPiProvider && !isReasonix
                                     && !isDevinNative && !legacyWin10Conhost;

                // Save the prompt to a temp file and send only a short reference when either:
                //   • "Disable clipboard" is on (always, so the keystroke/paste payload stays short), or
                //   • "Send large prompts as file" is on and the prompt exceeds the ~1 KB conhost
                //     paste-buffer threshold (avoids front-truncation of large pastes, see issue #48), or
                //   • the prompt is over that same threshold and the legacy Windows 10 console host
                //     is in use — there a paste reaches the agent as plain keystrokes, and a long
                //     one is what the CLI turns into a runaway series of pasted-text blocks
                //     (issue #83), so the file reference is forced regardless of the setting.
                // In all cases the "Files attached:" list is preserved by living inside the file.
                const int LargePromptThresholdChars = 1024;
                bool writeToFile = !string.IsNullOrEmpty(finalPrompt)
                    && (disableClipboardRequested
                        || (legacyWin10Conhost && finalPrompt.Length > LargePromptThresholdChars)
                        || (_settings != null
                            && _settings.SendLargePromptsAsFile
                            && finalPrompt.Length > LargePromptThresholdChars));

                if (writeToFile)
                {
                    try
                    {
                        string sessionDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
                        Directory.CreateDirectory(sessionDir);
                        string promptFile = Path.Combine(sessionDir, $"prompt-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                        File.WriteAllText(promptFile, finalPrompt, new UTF8Encoding(false));

                        string displayPath = isWSLProvider ? ConvertToWslPath(promptFile) : promptFile;
                        textToSend = $"Read and follow: {displayPath}";
                        Debug.WriteLine($"Prompt ({finalPrompt.Length} chars) saved to: {promptFile}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to save prompt to file, falling back to inline send: {ex.Message}");
                        // Fall back to inline send (keystrokes or paste, depending on mode)
                        textToSend = finalPrompt;
                    }
                }

                Debug.WriteLine($"Sending prompt to terminal ({textToSend.Length} chars): {textToSend.Substring(0, Math.Min(200, textToSend.Length))}...");
                if (clipboardFree)
                {
                    await SendTextViaKeystrokesAsync(textToSend);
                }
                else
                {
                    await SendTextToTerminalAsync(textToSend);
                }

                FinishPromptSubmission();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending prompt: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Always release the re-entrancy guard and re-enable the button, even on error.
                _isSendingPrompt = false;
                if (SendPromptButton != null) SendPromptButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Post-send housekeeping shared by the terminal and native-mode paths: clears the prompt box
        /// and attachments, resets history navigation and refreshes the usage bars.
        /// </summary>
        private void FinishPromptSubmission()
        {
            // Clear prompt and images
            PromptTextBox.Clear();
            ClearAttachedImages();

            // Reset image counter after sending prompt
            imageCounter = 1;

            // Reset history navigation
            _historyIndex = -1;
            _tempCurrentText = string.Empty;

            // Refresh inline usage bars (throttled internally)
            _ = RefreshInlineUsageAsync();

            // Arm the "On Agent Finish" watcher (Claude Code only; no-op when disabled).
            // Skipped in native mode: there the turn ends on an explicit protocol event, so the whole
            // AttachConsole/idle-heuristic machinery would only produce false positives.
            if (!IsNativeModeActive)
            {
                _ = ArmAgentCompletionWatcherAsync();
            }
        }

        #endregion

        #region Mouse Pointer Visibility

        /// <summary>
        /// Forces the mouse pointer visible again after Windows' "Hide pointer while typing"
        /// (<c>SPI_SETMOUSEVANISH</c>, on by default) has hidden it with <c>SetCursor(NULL)</c>.
        ///
        /// <para>
        /// Windows normally restores the pointer on the next mouse move, when the window under it
        /// handles <c>WM_SETCURSOR</c>. That fails here for two compounding reasons (issue #122):
        /// <c>SetParent</c> permanently joins the embedded terminal's input queue with the VS UI
        /// thread (see issue #65), so both share one pointer-visibility state; and while an agent
        /// streams output the terminal thread can stop pumping messages long enough that the
        /// <c>WM_SETCURSOR</c> which would restore the pointer is never processed. The pointer then
        /// stays invisible until the user happens to move over a VS window that is still pumping.
        /// Codex shows it most because its TUI repaints continuously, saturating the terminal thread
        /// for the whole reply.
        /// </para>
        ///
        /// <para>
        /// Calling <c>SetCursor</c> directly sidesteps <c>WM_SETCURSOR</c> entirely. The shape is
        /// chosen from what sits under the pointer so the correction is invisible to the user; any
        /// real mouse move afterwards re-asserts the proper cursor through the normal path anyway.
        /// </para>
        /// </summary>
        private void RestoreMousePointer()
        {
            try
            {
                IntPtr cursor = LoadCursor(IntPtr.Zero, new IntPtr(IsPointerOverTerminal() ? IDC_ARROW : IDC_IBEAM));
                if (cursor != IntPtr.Zero)
                {
                    SetCursor(cursor);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error restoring mouse pointer: {ex.Message}");
            }
        }

        /// <summary>
        /// True when the pointer sits over the embedded terminal, which wants the arrow rather than
        /// the prompt box's I-beam. Win32-only (no WPF hit test) so it is safe to call from the send
        /// path regardless of thread.
        /// </summary>
        private bool IsPointerOverTerminal()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return false;
            }

            if (!GetCursorPos(out POINT point))
            {
                return false;
            }

            return GetWindowRect(terminalHandle, out RECT rect)
                   && point.x >= rect.Left && point.x < rect.Right
                   && point.y >= rect.Top && point.y < rect.Bottom;
        }

        #endregion

        #region Keyboard Input Handling

        /// <summary>
        /// Handles KeyDown event for the prompt textbox.
        /// When Send-with-Enter is enabled, Enter sends the prompt;
        /// Shift+Enter or Ctrl+Enter inserts a newline.
        /// When disabled, Enter inserts a newline (default TextBox behavior).
        /// </summary>
        private void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (e.Key == Key.Enter && _settings?.SendWithEnter != false)
            {
                // Plain Enter sends the prompt (modifier cases handled in PreviewKeyDown)
                e.Handled = true;
                SendButton_Click(sender, null);
            }
        }

        /// <summary>
        /// Handles PreviewKeyDown event for the prompt textbox
        /// Catches Enter before TextBox processes it, and handles Ctrl+V for image paste, Ctrl+Up/Down for history
        /// </summary>
        private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            RestoreMousePointer();

            // Note prompt typing so the "On Agent Finish" watcher pauses its console read (its
            // AttachConsole can bounce focus out of the prompt mid-keystroke), and keep the WPF
            // focus guard alive so the cross-process terminal can't steal focus while the user
            // types the next prompt during active generation.
            _lastPromptKeyUtc = DateTime.UtcNow;
            EnsureWpfPromptFocusGuardRunning();

            // When the "@" file/folder picker is open, let it consume navigation/commit keys
            // (Up/Down/Enter/Tab/Esc) before history navigation or send-on-Enter runs.
            if (HandleAtMentionKey(GetAtMentionPanelTarget(), e)) return;

            // Handle Ctrl+Up/Down for prompt history navigation
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.Up)
                {
                    NavigateHistoryUp();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Down)
                {
                    NavigateHistoryDown();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter)
            {
                bool sendWithEnter = _settings?.SendWithEnter != false;
                bool sendWithCtrlEnter = _settings?.SendWithCtrlEnter == true;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                if (sendWithEnter)
                {
                    if (shift || ctrl)
                    {
                        // Shift+Enter or Ctrl+Enter: insert newline at caret
                        int caret = PromptTextBox.CaretIndex;
                        PromptTextBox.SelectedText = "\n";
                        PromptTextBox.CaretIndex = caret + 1;
                        e.Handled = true;
                        return;
                    }

                    // Plain Enter: send prompt
                    e.Handled = true;
                    SendButton_Click(sender, null);
                    return;
                }

                if (sendWithCtrlEnter && ctrl)
                {
                    // Ctrl+Enter sends; plain/Shift+Enter fall through to the default newline.
                    // Guards against accidentally sending an incomplete prompt with a stray Enter.
                    e.Handled = true;
                    SendButton_Click(sender, null);
                    return;
                }

                // Plain Enter (and Shift+Enter): let TextBox insert a newline by default.
            }

            // Preserve paste-image shortcut even with new behavior
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TryPasteImage())
                {
                    e.Handled = true;
                }
            }
        }

        #endregion

        #region Prompt Font Zoom

        /// <summary>
        /// Handles Ctrl+Scroll on the prompt textbox to increase/decrease font size
        /// </summary>
        private void PromptTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double newSize = PromptTextBox.FontSize + (e.Delta > 0 ? 1 : -1);
                newSize = Math.Max(8, Math.Min(24, newSize));
                PromptTextBox.FontSize = newSize;
                if (_settings != null)
                {
                    _settings.PromptFontSize = newSize;
                    SaveSettings();
                }
                e.Handled = true;
            }
        }

        #endregion

        #region Prompt History Navigation

        /// <summary>
        /// Adds a prompt to the history and saves settings
        /// </summary>
        /// <param name="prompt">The prompt text to add</param>
        /// <param name="filePaths">The file paths attached to this prompt</param>
        private void AddToPromptHistory(string prompt, List<string> filePaths)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            // Ensure settings and history are initialized
            if (_settings == null)
                _settings = new ClaudeCodeSettings();
            if (_settings.PromptHistory == null)
                _settings.PromptHistory = new System.Collections.Generic.List<PromptHistoryEntry>();

            // Remove duplicate if it exists (same text)
            _settings.PromptHistory.RemoveAll(e => e.Text == prompt);

            // Add to end (most recent)
            _settings.PromptHistory.Add(new PromptHistoryEntry
            {
                Text = prompt,
                FilePaths = filePaths != null ? new List<string>(filePaths) : new List<string>()
            });

            // Keep only the last MaxHistorySize items
            if (_settings.PromptHistory.Count > MaxHistorySize)
            {
                _settings.PromptHistory.RemoveAt(0);
            }

            // Save to settings file
            SaveSettings();
        }

        /// <summary>
        /// The prompt box history navigation writes to: the chat tab's composer while the user is
        /// typing there, the panel's prompt box otherwise. Without this the ↑ key in the chat tab
        /// would silently rewrite the hidden panel prompt instead.
        /// </summary>
        private bool HistoryTargetsComposer
        {
            get { return ChatTranscript != null && ChatTranscript.ComposerHasFocus; }
        }

        private string GetHistoryPromptText()
        {
            return HistoryTargetsComposer ? ChatTranscript.ComposerText : PromptTextBox.Text;
        }

        /// <summary>
        /// Shows a recalled prompt. Walking backwards puts the caret at the start in the composer, so a
        /// second ↑ is still "on the first line" and keeps going through older prompts rather than
        /// moving around inside the one just recalled.
        /// </summary>
        private void SetHistoryPromptText(string text, bool goingUp)
        {
            string value = text ?? string.Empty;

            if (HistoryTargetsComposer)
            {
                ChatTranscript.SetComposerText(value, caretAtStart: goingUp);
                return;
            }

            PromptTextBox.Text = value;
            PromptTextBox.SelectionStart = value.Length;
        }

        /// <summary>
        /// Navigates up in the prompt history (to older prompts)
        /// </summary>
        private void NavigateHistoryUp()
        {
            if (_settings?.PromptHistory == null || _settings.PromptHistory.Count == 0)
                return;

            // First time navigating? Save current text and files
            if (_historyIndex == -1)
            {
                _tempCurrentText = GetHistoryPromptText();
                _tempCurrentFiles = attachedImagePaths.ToList();
                _historyIndex = _settings.PromptHistory.Count;
            }

            // Move to previous item (if possible)
            if (_historyIndex > 0)
            {
                _historyIndex--;
                var entry = _settings.PromptHistory[_historyIndex];
                SetHistoryPromptText(entry.Text, goingUp: true);
                RestoreFilesFromHistory(entry.FilePaths);
            }
        }

        /// <summary>
        /// Navigates down in the prompt history (to newer prompts)
        /// </summary>
        private void NavigateHistoryDown()
        {
            if (_settings?.PromptHistory == null || _historyIndex == -1)
                return;

            // Move to next item
            _historyIndex++;

            // If we've gone past the end, restore the temp text and files
            if (_historyIndex >= _settings.PromptHistory.Count)
            {
                SetHistoryPromptText(_tempCurrentText, goingUp: false);
                RestoreFilesFromHistory(_tempCurrentFiles);
                _historyIndex = -1;
                _tempCurrentText = string.Empty;
                _tempCurrentFiles = new List<string>();
            }
            else
            {
                var entry = _settings.PromptHistory[_historyIndex];
                SetHistoryPromptText(entry.Text, goingUp: false);
                RestoreFilesFromHistory(entry.FilePaths);
            }
        }

        private static string GetUniquePromptAttachmentPath(string promptDirectory, string fileName)
        {
            string safeFileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName;
            string candidate = Path.Combine(promptDirectory, safeFileName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            string nameWithoutExtension = Path.GetFileNameWithoutExtension(safeFileName);
            string extension = Path.GetExtension(safeFileName);
            for (int index = 2; ; index++)
            {
                candidate = Path.Combine(promptDirectory, $"{nameWithoutExtension}_{index}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>
        /// Clears the prompt history
        /// </summary>
        private void ClearPromptHistory()
        {
            if (_settings == null)
                _settings = new ClaudeCodeSettings();

            _settings.PromptHistory?.Clear();
            _historyIndex = -1;
            _tempCurrentText = string.Empty;
            _tempCurrentFiles = new List<string>();

            SaveSettings();

            MessageBox.Show("Prompt history cleared.", "History Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Handles context menu click to clear prompt history
        /// </summary>
        private void ClearPromptHistoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ClearPromptHistory();
        }

        #endregion

        #region Editor Selection Integration

        /// <summary>
        /// Language identifier mapping from file extensions to markdown code fence language IDs
        /// </summary>
        private static readonly Dictionary<string, string> _languageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".cs", "csharp" }, { ".vb", "vb" }, { ".fs", "fsharp" },
            { ".py", "python" }, { ".js", "javascript" }, { ".ts", "typescript" },
            { ".jsx", "jsx" }, { ".tsx", "tsx" },
            { ".java", "java" }, { ".kt", "kotlin" }, { ".scala", "scala" },
            { ".cpp", "cpp" }, { ".cc", "cpp" }, { ".cxx", "cpp" },
            { ".c", "c" }, { ".h", "c" }, { ".hpp", "cpp" },
            { ".go", "go" }, { ".rs", "rust" }, { ".swift", "swift" },
            { ".rb", "ruby" }, { ".php", "php" }, { ".lua", "lua" },
            { ".r", "r" }, { ".m", "objectivec" }, { ".mm", "objectivec" },
            { ".html", "html" }, { ".htm", "html" }, { ".css", "css" },
            { ".scss", "scss" }, { ".less", "less" }, { ".sass", "sass" },
            { ".xml", "xml" }, { ".xaml", "xml" }, { ".json", "json" },
            { ".yaml", "yaml" }, { ".yml", "yaml" }, { ".toml", "toml" },
            { ".sql", "sql" }, { ".sh", "bash" }, { ".bash", "bash" },
            { ".ps1", "powershell" }, { ".psm1", "powershell" },
            { ".bat", "batch" }, { ".cmd", "batch" },
            { ".md", "markdown" }, { ".rst", "rst" },
            { ".dart", "dart" }, { ".ex", "elixir" }, { ".exs", "elixir" },
            { ".zig", "zig" }, { ".nim", "nim" }, { ".v", "v" },
        };

        /// <summary>
        /// Gets the markdown language identifier for a file extension
        /// </summary>
        private static string GetLanguageIdFromExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return string.Empty;

            return _languageMap.TryGetValue(extension, out string langId) ? langId : string.Empty;
        }

        private static bool TryGetRelativePathUnderDirectory(string fullPath, string directory, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(directory))
            {
                return false;
            }

            try
            {
                string normalizedFullPath = Path.GetFullPath(fullPath);
                string normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;

                if (!normalizedFullPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                relativePath = normalizedFullPath.Substring(normalizedDirectory.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Handles the grab selection toolbar button click.
        /// Gets the current editor selection and inserts it into the prompt.
        /// </summary>
        private void GrabSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;

                if (dte?.ActiveDocument == null)
                {
                    MessageBox.Show("No active document open in the editor.",
                        "No Document", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var selection = dte.ActiveDocument.Selection as EnvDTE.TextSelection;
                if (selection == null || string.IsNullOrEmpty(selection.Text))
                {
                    MessageBox.Show("No text selected in the active editor.\nPlease select some code first.",
                        "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string code = selection.Text;
                string filePath = dte.ActiveDocument.FullName;
                int startLine = selection.TopLine;
                int endLine = selection.BottomLine;

                InsertCodeSnippetIntoPrompt(code, filePath, startLine, endLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error grabbing editor selection: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the "Insert Active File Path" menu item. Drops an "@relative/path" reference to
        /// the file open in the active editor tab into the prompt, without any code or selection —
        /// the "Active Document" equivalent from other AI chat extensions (issue #127). Uses the same
        /// "@" token the file/folder picker in <see cref="ClaudeCodeControl.AtMention"/> writes, so the
        /// agent resolves it as a normal file reference.
        /// </summary>
        private void InsertActiveFilePathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;

                if (dte?.ActiveDocument == null)
                {
                    MessageBox.Show("No active document open in the editor.",
                        "No Document", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string filePath = dte.ActiveDocument.FullName;
                string displayPath = filePath;
                if (TryGetRelativePathUnderDirectory(filePath, _lastWorkspaceDirectory, out string relativePath))
                {
                    displayPath = relativePath;
                }
                displayPath = displayPath.Replace('\\', '/');

                string currentText = PromptTextBox.Text;
                string insert = "@" + displayPath + " ";

                int caretIndex = PromptTextBox.CaretIndex;
                if (caretIndex >= 0 && caretIndex <= currentText.Length)
                {
                    PromptTextBox.Text = currentText.Insert(caretIndex, insert);
                    PromptTextBox.CaretIndex = caretIndex + insert.Length;
                }
                else
                {
                    PromptTextBox.Text = currentText + insert;
                    PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
                }

                PromptTextBox.Focus();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error inserting active file path: {ex.Message}");
            }
        }

        /// <summary>
        /// Inserts a formatted code snippet into the prompt text box without sending.
        /// Called from the toolbar button and the editor context menu command.
        /// </summary>
        public void InsertCodeSnippetIntoPrompt(string code, string filePath, int startLine, int endLine)
        {
            try
            {
                // Make path relative to workspace if possible
                string displayPath = filePath;
                if (TryGetRelativePathUnderDirectory(filePath, _lastWorkspaceDirectory, out string relativePath))
                {
                    displayPath = relativePath;
                }

                // Get language identifier from file extension
                string extension = Path.GetExtension(filePath);
                string langId = GetLanguageIdFromExtension(extension);

                // Build the formatted snippet
                var snippet = new StringBuilder();

                // Add separator if prompt already has text
                string currentText = PromptTextBox.Text;
                if (!string.IsNullOrEmpty(currentText) && !currentText.EndsWith("\n") && !currentText.EndsWith("\r"))
                {
                    snippet.AppendLine();
                }

                // File header with line info
                if (startLine == endLine)
                {
                    snippet.AppendLine($"File: {displayPath} (line {startLine})");
                }
                else
                {
                    snippet.AppendLine($"File: {displayPath} (lines {startLine}-{endLine})");
                }

                // Code fence with language (skipped when reference-only mode is on)
                if (_settings?.SendSelectionReferenceOnly != true)
                {
                    snippet.AppendLine($"```{langId}");
                    snippet.AppendLine(code.TrimEnd('\r', '\n'));
                    snippet.AppendLine("```");
                    snippet.AppendLine();
                }

                // Insert at current cursor position or append
                int caretIndex = PromptTextBox.CaretIndex;
                if (caretIndex >= 0 && caretIndex < currentText.Length && !string.IsNullOrEmpty(currentText))
                {
                    PromptTextBox.Text = currentText.Insert(caretIndex, snippet.ToString());
                    PromptTextBox.CaretIndex = caretIndex + snippet.Length;
                }
                else
                {
                    PromptTextBox.Text = currentText + snippet.ToString();
                    PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
                }

                // Focus the prompt for the user to type their question
                PromptTextBox.Focus();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error inserting code snippet: {ex.Message}");
            }
        }

        /// <summary>
        /// Maintainer toggle for the optional "📥 Paste from Clipboard" entry in the attach
        /// dropdown menu. When false (default) the entry is hidden in <see cref="ClaudeCodeControl"/>'s
        /// constructor. Flip this to true to expose the feature to users.
        ///
        /// The entry pastes short clipboard text inline into the prompt, but for clipboard
        /// content above the conhost truncation threshold it saves the text to a temp file
        /// and attaches it instead — a workaround for the conhost INPUT_RECORD buffer
        /// overflow that drops the front of large pastes (see issue #48).
        /// </summary>
        private const bool EnablePasteFromClipboardMenu = false;

        /// <summary>
        /// Handles the "Paste from Clipboard" menu item.
        /// Short clipboard text is inserted into the prompt textbox at the caret;
        /// large text is written to a temp file and attached (same path as the
        /// regular Attach File flow), avoiding conhost paste-buffer truncation.
        /// </summary>
        private void PasteFromClipboardMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // 1) File-drop list: clipboard holds a list of file paths (e.g. files copied
                //    from Explorer). Attach them directly without copying.
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    int added = 0;
                    foreach (string path in files)
                    {
                        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                        {
                            attachedImagePaths.Add(path);
                            added++;
                        }
                    }
                    if (added > 0)
                    {
                        UpdateImageDropDisplay();
                        PromptTextBox.Focus();
                        Debug.WriteLine($"Attached {added} file(s) from clipboard file drop list");
                        return;
                    }
                }

                // 2) Image content: reuse the existing image-paste pipeline which saves the
                //    bitmap as PNG into the temp image directory and attaches it.
                //    TryPasteImage() intentionally skips when text is also present (Excel etc.),
                //    so falls through to the text branch below in that case.
                if (TryPasteImage())
                {
                    PromptTextBox.Focus();
                    return;
                }

                // 3) Text content: small text goes inline at the caret, large text is saved
                //    to a temp file and attached (avoids conhost paste-buffer truncation).
                if (Clipboard.ContainsText())
                {
                    string clipboardText;
                    try
                    {
                        clipboardText = Clipboard.GetText();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not read clipboard: {ex.Message}",
                            "Clipboard Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (string.IsNullOrEmpty(clipboardText))
                    {
                        MessageBox.Show("Clipboard text is empty.",
                            "Nothing to Paste", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    // Same threshold as SendButton_Click — below it inline pastes are safe,
                    // above it conhost would truncate the front.
                    const int LargePasteThresholdChars = 1024;

                    if (clipboardText.Length <= LargePasteThresholdChars)
                    {
                        // Short paste: insert at caret position.
                        string currentText = PromptTextBox.Text ?? string.Empty;
                        int caretIndex = PromptTextBox.CaretIndex;
                        if (caretIndex >= 0 && caretIndex < currentText.Length && !string.IsNullOrEmpty(currentText))
                        {
                            PromptTextBox.Text = currentText.Insert(caretIndex, clipboardText);
                            PromptTextBox.CaretIndex = caretIndex + clipboardText.Length;
                        }
                        else
                        {
                            PromptTextBox.Text = currentText + clipboardText;
                            PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
                        }
                        PromptTextBox.Focus();
                        return;
                    }

                    // Large paste: write to a session temp file and attach it.
                    try
                    {
                        string sessionDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
                        Directory.CreateDirectory(sessionDir);
                        string pasteFile = Path.Combine(sessionDir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                        File.WriteAllText(pasteFile, clipboardText, new UTF8Encoding(false));

                        attachedImagePaths.Add(pasteFile);
                        UpdateImageDropDisplay();
                        PromptTextBox.Focus();
                        Debug.WriteLine($"Pasted clipboard ({clipboardText.Length} chars) attached as file: {pasteFile}");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not save clipboard to file: {ex.Message}",
                            "Paste Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }

                // 4) Nothing usable.
                MessageBox.Show("Clipboard does not contain text, an image, or a file list.",
                    "Nothing to Paste", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in PasteFromClipboardMenuItem_Click: {ex.Message}");
            }
        }

        #endregion
    }
}
