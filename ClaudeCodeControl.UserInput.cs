/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: User input handling - keyboard events, send button, and prompt submission
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Send Button and Prompt Submission

        /// <summary>
        /// Handles send button click - sends the prompt to the terminal
        /// </summary>
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string prompt = PromptTextBox.Text.Trim();
                if (string.IsNullOrEmpty(prompt))
                {
                    MessageBox.Show("Please enter a prompt.", "No Prompt", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                StringBuilder fullPrompt = new StringBuilder();

                // If images are attached, copy them to a unique directory and include paths
                if (attachedImagePaths.Any())
                {
                    // Create a unique ClaudeCodeVS directory for this prompt with images
                    string promptDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(promptDirectory);

                    fullPrompt.AppendLine("Images attached:");
                    foreach (string imagePath in attachedImagePaths)
                    {
                        try
                        {
                            string fileName = Path.GetFileName(imagePath);
                            string tempPath = Path.Combine(promptDirectory, fileName);

                            File.Copy(imagePath, tempPath, true);

                            fullPrompt.AppendLine($"  - {tempPath}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error copying image {imagePath}: {ex.Message}");
                            fullPrompt.AppendLine($"  - {imagePath}");
                        }
                    }
                    fullPrompt.AppendLine();
                }

                fullPrompt.AppendLine(prompt);

                // Send to terminal
                SendTextToTerminal(fullPrompt.ToString());

                // Clear prompt and images
                PromptTextBox.Clear();
                ClearAttachedImages();

                // Reset image counter after sending prompt
                imageCounter = 1;
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

                Debug.WriteLine($"Enter pressed - SendWithEnter: {sendWithEnter}, Modifiers: {Keyboard.Modifiers}");

                if (sendWithEnter)
                {
                    // When SendWithEnter is enabled:
                    // - Enter sends the prompt
                    // - Shift+Enter or Ctrl+Enter creates new line
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        // Allow newline insertion with modifiers
                        Debug.WriteLine("Allowing newline with modifier key");
                        return;
                    }
                    else
                    {
                        // Plain Enter sends the prompt
                        Debug.WriteLine("Sending prompt with Enter");
                        e.Handled = true; // Prevent default newline behavior
                        SendButton_Click(sender, null);
                    }
                }
                else
                {
                    // When SendWithEnter is disabled, let default behavior handle Enter (newlines)
                    Debug.WriteLine("SendWithEnter disabled - allowing newline");
                }
            }
        }

        /// <summary>
        /// Handles PreviewKeyDown event for the prompt textbox
        /// Catches Enter before TextBox processes it, and handles Ctrl+V for image paste
        /// </summary>
        private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle SendWithEnter functionality in PreviewKeyDown to catch it before TextBox handles it
            if (e.Key == Key.Enter)
            {
                bool sendWithEnter = SendWithEnterCheckBox.IsChecked == true;

                Debug.WriteLine($"PreviewKeyDown Enter pressed - SendWithEnter: {sendWithEnter}, Modifiers: {Keyboard.Modifiers}");

                if (sendWithEnter)
                {
                    // When SendWithEnter is enabled:
                    // - Enter sends the prompt
                    // - Shift+Enter or Ctrl+Enter creates new line
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        // Allow newline insertion with modifiers
                        Debug.WriteLine("PreviewKeyDown: Allowing newline with modifier key");
                        return;
                    }
                    else
                    {
                        // Plain Enter sends the prompt
                        Debug.WriteLine("PreviewKeyDown: Sending prompt with Enter");
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
            SaveSettings();
        }

        /// <summary>
        /// Handles SendWithEnter checkbox unchecked event
        /// Shows the send button when disabled
        /// </summary>
        private void SendWithEnterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SendPromptButton.Visibility = Visibility.Visible;
            SaveSettings();
        }

        #endregion
    }
}