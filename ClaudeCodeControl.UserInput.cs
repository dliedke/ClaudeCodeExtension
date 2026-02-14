/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
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
using System.Windows;
using System.Windows.Input;

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
        /// Maximum number of prompts to keep in history
        /// </summary>
        private const int MaxHistorySize = 50;

        #endregion

        #region Send Button and Prompt Submission

        /// <summary>
        /// Handles send button click - sends the prompt to the terminal
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void SendButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
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

                StringBuilder fullPrompt = new StringBuilder();

                // If files are attached, include their paths in the prompt
                if (hasFiles)
                {
                    // Check if CURRENTLY RUNNING provider is WSL-based
                    bool isWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                         _currentRunningProvider == AiProvider.ClaudeCodeWSL ||
                                         _currentRunningProvider == AiProvider.CursorAgent;

                    // Create a unique ClaudeCodeVS directory for this prompt with files
                    string promptDirectory = null;
                    try
                    {
                        promptDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", Guid.NewGuid().ToString());
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
                                string tempPath = Path.Combine(promptDirectory, fileName);
                                File.Copy(filePath, tempPath, true);
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
                    AddToPromptHistory(prompt);
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
                Debug.WriteLine($"Sending prompt to terminal ({finalPrompt.Length} chars): {finalPrompt.Substring(0, Math.Min(200, finalPrompt.Length))}...");
                await SendTextToTerminalAsync(finalPrompt);

                // Clear prompt and images
                PromptTextBox.Clear();
                ClearAttachedImages();

                // Reset image counter after sending prompt
                imageCounter = 1;

                // Reset history navigation
                _historyIndex = -1;
                _tempCurrentText = string.Empty;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending prompt: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Keyboard Input Handling

        /// <summary>
        /// Handles KeyDown event for the prompt textbox
        /// Implements Send-with-Enter functionality
        /// </summary>
        private void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool sendWithEnter = SendWithEnterCheckBox.IsChecked == true;


                if (sendWithEnter)
                {
                    // When SendWithEnter is enabled:
                    // - Enter sends the prompt
                    // - Shift+Enter or Ctrl+Enter creates new line
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        // Allow newline insertion with modifiers
                        return;
                    }
                    else
                    {
                        // Plain Enter sends the prompt
                        e.Handled = true; // Prevent default newline behavior
                        SendButton_Click(sender, null);
                    }
                }
                else
                {
                    // When SendWithEnter is disabled, let default behavior handle Enter (newlines)
                }
            }
        }

        /// <summary>
        /// Handles PreviewKeyDown event for the prompt textbox
        /// Catches Enter before TextBox processes it, and handles Ctrl+V for image paste, Ctrl+Up/Down for history
        /// </summary>
        private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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

            // Handle SendWithEnter functionality in PreviewKeyDown to catch it before TextBox handles it
            if (e.Key == Key.Enter)
            {
                bool sendWithEnter = SendWithEnterCheckBox.IsChecked == true;


                if (sendWithEnter)
                {
                    // When SendWithEnter is enabled:
                    // - Enter sends the prompt
                    // - Shift+Enter or Ctrl+Enter creates new line
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        // Allow newline insertion with modifiers
                        return;
                    }
                    else
                    {
                        // Plain Enter sends the prompt
                        e.Handled = true; // Prevent default newline behavior
                        SendButton_Click(sender, null);
                        return;
                    }
                }
                // When SendWithEnter is disabled, let default behavior handle Enter (newlines)
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

        #region Send-with-Enter Toggle

        /// <summary>
        /// Handles SendWithEnter checkbox checked event
        /// Hides the send button when enabled
        /// </summary>
        private void SendWithEnterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SendPromptButton.Visibility = Visibility.Collapsed;
            SendWithEnterCheckBox.ToolTip = "Automatically send prompt to code agent with enter key. Use Shift+Enter for new lines";
            SaveSettings();
        }

        /// <summary>
        /// Handles SendWithEnter checkbox unchecked event
        /// Shows the send button when disabled
        /// </summary>
        private void SendWithEnterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SendPromptButton.Visibility = Visibility.Visible;
            SendWithEnterCheckBox.ToolTip = "Use Send button to send prompt to code agent";
            SaveSettings();
        }

        #endregion

        #region Prompt History Navigation

        /// <summary>
        /// Adds a prompt to the history and saves settings
        /// </summary>
        /// <param name="prompt">The prompt text to add</param>
        private void AddToPromptHistory(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            // Ensure settings and history are initialized
            if (_settings == null)
                _settings = new ClaudeCodeSettings();
            if (_settings.PromptHistory == null)
                _settings.PromptHistory = new System.Collections.Generic.List<string>();

            // Remove duplicate if it exists
            _settings.PromptHistory.Remove(prompt);

            // Add to end (most recent)
            _settings.PromptHistory.Add(prompt);

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

            // First time navigating? Save current text
            if (_historyIndex == -1)
            {
                _tempCurrentText = PromptTextBox.Text;
                _historyIndex = _settings.PromptHistory.Count;
            }

            // Move to previous item (if possible)
            if (_historyIndex > 0)
            {
                _historyIndex--;
                PromptTextBox.Text = _settings.PromptHistory[_historyIndex];
                PromptTextBox.SelectionStart = PromptTextBox.Text.Length;
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

            // If we've gone past the end, restore the temp text
            if (_historyIndex >= _settings.PromptHistory.Count)
            {
                PromptTextBox.Text = _tempCurrentText;
                _historyIndex = -1;
                _tempCurrentText = string.Empty;
            }
            else
            {
                PromptTextBox.Text = _settings.PromptHistory[_historyIndex];
            }

            PromptTextBox.SelectionStart = PromptTextBox.Text.Length;
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
    }
}
