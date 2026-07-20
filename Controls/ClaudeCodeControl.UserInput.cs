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

        /// <summary>
        /// Handles send button click - sends the prompt to the terminal
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void SendButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
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

                _isSendingPrompt = true;
                if (SendPromptButton != null) SendPromptButton.IsEnabled = false;

                StringBuilder fullPrompt = new StringBuilder();

                // Check if CURRENTLY RUNNING provider is WSL-based (not CodexNative, CursorAgentNative).
                // Hoisted out of the hasFiles branch so the large-prompt-as-file path can use it too.
                bool isWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                     _currentRunningProvider == AiProvider.ClaudeCodeWSL ||
                                     _currentRunningProvider == AiProvider.CursorAgent ||
                                     _currentRunningProvider == AiProvider.Devin;

                // If files are attached, include their paths in the prompt
                if (hasFiles)
                {

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

                    fullPrompt.AppendLine("Files attached:");
                    foreach (string filePath in attachedImagePaths)
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
                                displayPath = isWSLProvider ? ConvertToWslPath(tempPath) : tempPath;
                            }
                            else
                            {
                                // Use original path if copy fails or file doesn't exist
                                displayPath = isWSLProvider ? ConvertToWslPath(filePath) : filePath;
                            }

                            fullPrompt.AppendLine($"  - {displayPath}");
                            Debug.WriteLine($"File attached to prompt: {filePath}");
                        }
                        catch (Exception ex)
                        {
                            // Always include the file path even if copy fails
                            Debug.WriteLine($"Error processing file {filePath}: {ex.Message}");
                            try
                            {
                                string displayPath = isWSLProvider ? ConvertToWslPath(filePath) : filePath;
                                fullPrompt.AppendLine($"  - {displayPath}");
                            }
                            catch
                            {
                                // Last resort: use the raw path
                                fullPrompt.AppendLine($"  - {filePath}");
                            }
                        }
                    }
                    fullPrompt.AppendLine();
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

                // Auto-open changes view if enabled and project is in git
                if (_settings != null && _settings.AutoOpenChangesOnPrompt && !string.IsNullOrEmpty(_gitRepositoryRoot))
                {
                    await AutoOpenChangesViewAsync();
                }

                // Send to terminal
                string finalPrompt = fullPrompt.ToString();
                string textToSend = finalPrompt;

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

                // Arm the "On Agent Finish" watcher (Claude Code only; no-op when disabled)
                _ = ArmAgentCompletionWatcherAsync();
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
            // Force cursor visible — "Hide pointer while typing" calls SetCursor(NULL) which WPF
            // only counteracts via WM_SETCURSOR (i.e. on mouse move). While typing non-stop with
            // the mouse stationary, WM_SETCURSOR never fires, so we must call SetCursor directly.
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IDC_IBEAM)));

            // Note prompt typing so the "On Agent Finish" watcher pauses its console read (its
            // AttachConsole can bounce focus out of the prompt mid-keystroke), and keep the WPF
            // focus guard alive so the cross-process terminal can't steal focus while the user
            // types the next prompt during active generation.
            _lastPromptKeyUtc = DateTime.UtcNow;
            EnsureWpfPromptFocusGuardRunning();

            // When the "@" file/folder picker is open, let it consume navigation/commit keys
            // (Up/Down/Enter/Tab/Esc) before history navigation or send-on-Enter runs.
            if (HandleAtMentionKey(e)) return;

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
        /// Navigates up in the prompt history (to older prompts)
        /// </summary>
        private void NavigateHistoryUp()
        {
            if (_settings?.PromptHistory == null || _settings.PromptHistory.Count == 0)
                return;

            // First time navigating? Save current text and files
            if (_historyIndex == -1)
            {
                _tempCurrentText = PromptTextBox.Text;
                _tempCurrentFiles = attachedImagePaths.ToList();
                _historyIndex = _settings.PromptHistory.Count;
            }

            // Move to previous item (if possible)
            if (_historyIndex > 0)
            {
                _historyIndex--;
                var entry = _settings.PromptHistory[_historyIndex];
                PromptTextBox.Text = entry.Text;
                PromptTextBox.SelectionStart = PromptTextBox.Text.Length;
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
                PromptTextBox.Text = _tempCurrentText;
                RestoreFilesFromHistory(_tempCurrentFiles);
                _historyIndex = -1;
                _tempCurrentText = string.Empty;
                _tempCurrentFiles = new List<string>();
            }
            else
            {
                var entry = _settings.PromptHistory[_historyIndex];
                PromptTextBox.Text = entry.Text;
                RestoreFilesFromHistory(entry.FilePaths);
            }

            PromptTextBox.SelectionStart = PromptTextBox.Text.Length;
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
