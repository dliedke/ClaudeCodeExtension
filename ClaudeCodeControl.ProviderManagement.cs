/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: AI provider detection, switching, and installation instructions
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Provider Fields

        /// <summary>
        /// Flag to show Claude installation notification only once per session
        /// </summary>
        private static bool _claudeNotificationShown = false;

        /// <summary>
        /// Flag to show Codex installation notification only once per session
        /// </summary>
        private static bool _codexNotificationShown = false;

        #endregion

        #region Provider Detection

        /// <summary>
        /// Checks if Claude Code CLI is available in the system PATH
        /// </summary>
        /// <returns>True if claude.cmd is available, false otherwise</returns>
        private async Task<bool> IsClaudeCmdAvailableAsync()
        {
            try
            {
                // Check if claude.cmd is available in PATH using 'where' command
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where claude.cmd",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    // Use async wait to avoid blocking UI thread
                    var completed = await Task.Run(() =>
                    {
                        return process.WaitForExit(3000); // 3 second timeout
                    });

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine("Claude check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Claude check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Claude check - Output: {output}");
                    Debug.WriteLine($"Claude check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Claude availability result: {isAvailable}");

                    return isAvailable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for claude.cmd: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if Codex CLI is available in the system
        /// </summary>
        /// <returns>True if codex.cmd is available, false otherwise</returns>
        private async Task<bool> IsCodexCmdAvailableAsync()
        {
            try
            {
                // Check if codex.cmd is available in user's npm global directory
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string codexPath = Path.Combine(userProfile, "AppData", "Roaming", "npm", "codex.cmd");

                // First check the known path
                if (File.Exists(codexPath))
                {
                    Debug.WriteLine($"Codex found at: {codexPath}");
                    return true;
                }

                // Also check if codex.cmd is available in PATH using 'where' command
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where codex.cmd",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    // Use async wait to avoid blocking UI thread
                    var completed = await Task.Run(() =>
                    {
                        return process.WaitForExit(3000); // 3 second timeout
                    });

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine("Codex check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Codex check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Codex check - Output: {output}");
                    Debug.WriteLine($"Codex check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Codex availability result: {isAvailable}");

                    return isAvailable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for codex.cmd: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Installation Instructions

        /// <summary>
        /// Shows installation instructions for Claude Code CLI
        /// </summary>
        private void ShowClaudeInstallationInstructions()
        {
            const string setupUrl = "https://docs.claude.com/en/docs/claude-code/setup";
            const string message = "Claude Code is not installed. A regular CMD terminal will be used instead.\n\n" +
                                   "To get the full Claude Code experience, you can install it with:\n" +
                                   "npm install -g @anthropic-ai/claude-code\n\n" +
                                   "Would you like to open the setup documentation for more details?";

            var result = MessageBox.Show(message, "Claude Code Installation",
                                       MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = setupUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open setup URL: {ex.Message}");
                    MessageBox.Show($"Please visit: {setupUrl}", "Setup URL",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        /// <summary>
        /// Shows installation instructions for Codex CLI
        /// </summary>
        private void ShowCodexInstallationInstructions()
        {
            const string setupUrl = "https://developers.openai.com/codex/cli/";
            const string message = "Codex CLI is not installed. A regular CMD terminal will be used instead.\n\n" +
                                   "To get the full Codex experience, you can install it by following the instructions at:\n" +
                                   "https://developers.openai.com/codex/cli/\n\n" +
                                   "Would you like to open the setup documentation for more details?";

            var result = MessageBox.Show(message, "Codex CLI Installation",
                                       MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = setupUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open setup URL: {ex.Message}");
                    MessageBox.Show($"Please visit: {setupUrl}", "Setup URL",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        #endregion

        #region Provider Switching

        /// <summary>
        /// Handles Claude Code menu item click - switches to Claude Code provider
        /// </summary>
        private void ClaudeCodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool claudeAvailable = await IsClaudeCmdAvailableAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.ClaudeCode;
                UpdateProviderSelection();
                SaveSettings();

                if (!claudeAvailable)
                {
                    ShowClaudeInstallationInstructions();
                    await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(true, false); // Claude Code
                }
            });
        }

        /// <summary>
        /// Handles Codex menu item click - switches to Codex provider
        /// </summary>
        private void CodexMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool codexAvailable = await IsCodexCmdAvailableAsync();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.Codex;
                UpdateProviderSelection();
                SaveSettings();

                if (!codexAvailable)
                {
                    ShowCodexInstallationInstructions();
                    await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(false, true); // Codex
                }
            });
        }

        /// <summary>
        /// Updates UI to reflect the currently selected provider
        /// </summary>
        private void UpdateProviderSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            // Update menu item checkmarks
            ClaudeCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.ClaudeCode;
            CodexMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.Codex;

            // Update GroupBox header to show current provider
            string providerName = _settings.SelectedProvider == AiProvider.ClaudeCode ? "Claude Code" : "Codex";
            TerminalGroupBox.Header = providerName;

            // Update tool window title
            UpdateToolWindowTitle(providerName);
        }

        /// <summary>
        /// Updates the tool window title to reflect the current provider
        /// </summary>
        /// <param name="providerName">Name of the current provider</param>
        private void UpdateToolWindowTitle(string providerName)
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _toolWindow?.UpdateTitle(providerName);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating tool window title: {ex.Message}");
            }
        }

        #endregion

        #region Menu Handlers

        /// <summary>
        /// Handles About menu item click - displays extension information
        /// </summary>
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}";
            string aboutMessage = $"Claude Code Extension for Visual Studio\n\n" +
                                $"Version: {version}\n" +
                                $"Author: Daniel Liedke\n" +
                                $"Copyright © Daniel Liedke 2025\n\n" +
                                $"Provides seamless integration with Claude Code and Codex AI assistants directly within Visual Studio IDE.";

            MessageBox.Show(aboutMessage, "About Claude Code Extension",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Handles dropdown button click - shows the provider selection menu
        /// </summary>
        private void MenuDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the context menu when the dropdown button is clicked
            var button = sender as System.Windows.Controls.Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        #endregion
    }
}