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
        /// Flag to show Claude Code (WSL) installation notification only once per session
        /// </summary>
        private static bool _claudeCodeWSLNotificationShown = false;

        /// <summary>
        /// Flag to show Codex installation notification only once per session
        /// </summary>
        private static bool _codexNotificationShown = false;

        /// <summary>
        /// Flag to show Cursor Agent installation notification only once per session
        /// </summary>
        private static bool _cursorAgentNotificationShown = false;

        /// <summary>
        /// Flag to show Qwen Code installation notification only once per session
        /// </summary>
        private static bool _qwenCodeNotificationShown = false;

        #endregion

        #region Provider Detection

        /// <summary>
        /// Checks if Claude Code CLI is available (native or NPM installation)
        /// Prioritizes native installation at %USERPROFILE%\.local\bin\claude.exe
        /// Falls back to NPM installation (claude.cmd in PATH)
        /// </summary>
        /// <returns>True if claude is available, false otherwise</returns>
        private async Task<bool> IsClaudeCmdAvailableAsync()
        {
            try
            {
                // First, check for native installation at %USERPROFILE%\.local\bin\claude.exe
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string nativeClaudePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");

                Debug.WriteLine($"Checking for native Claude installation at: {nativeClaudePath}");

                if (File.Exists(nativeClaudePath))
                {
                    Debug.WriteLine("Native Claude installation found");
                    return true;
                }

                Debug.WriteLine("Native Claude installation not found, checking NPM installation...");

                // If native not found, check for NPM installation (claude.cmd in PATH)
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
                        Debug.WriteLine("Claude NPM check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Claude NPM check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Claude NPM check - Output: {output}");
                    Debug.WriteLine($"Claude NPM check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Claude NPM availability result: {isAvailable}");

                    return isAvailable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Claude: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if Claude Code CLI is available in WSL
        /// Uses retry logic to handle WSL initialization delays after boot
        /// </summary>
        /// <returns>True if claude is available in WSL, false otherwise</returns>
        private async Task<bool> IsClaudeCodeWSLAvailableAsync()
        {
            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync();
                if (!wslInstalled)
                {
                    Debug.WriteLine("WSL is not installed, Claude Code in WSL not available");
                    return false;
                }

                // Retry logic for WSL commands (handles WSL initialization after boot)
                int[] timeouts = { 5000, 8000, 12000 }; // Progressive timeouts: 5s, 8s, 12s
                int maxRetries = 3;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    Debug.WriteLine($"Claude Code WSL check attempt {attempt}/{maxRetries}");

                    // Check if claude is available in WSL using 'which claude'
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -ic \"which claude\"",
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
                            return process.WaitForExit(timeouts[attempt - 1]);
                        });

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }
                            Debug.WriteLine($"Claude Code WSL check timed out on attempt {attempt}");

                            // If not the last attempt, wait before retrying
                            if (attempt < maxRetries)
                            {
                                Debug.WriteLine($"Waiting 2 seconds before retry (WSL may be initializing)...");
                                await Task.Delay(2000);
                                continue;
                            }
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();

                        Debug.WriteLine($"Claude Code WSL check - Exit code: {process.ExitCode}");
                        Debug.WriteLine($"Claude Code WSL check - Output: {output}");
                        Debug.WriteLine($"Claude Code WSL check - Error: {error}");

                        // Check if output contains a path to claude
                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("claude");

                        if (isAvailable)
                        {
                            Debug.WriteLine($"Claude Code in WSL found on attempt {attempt}");
                            return true;
                        }

                        // If we got a response but agent not found, no need to retry
                        if (process.ExitCode == 0 || !string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(error))
                        {
                            Debug.WriteLine($"Claude Code in WSL not found (WSL responded, agent not installed)");
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            Debug.WriteLine($"WSL didn't respond properly, waiting 2 seconds before retry...");
                            await Task.Delay(2000);
                        }
                    }
                }

                Debug.WriteLine($"Claude Code in WSL not available after {maxRetries} attempts");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for claude in WSL: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if Codex CLI is available in WSL
        /// Uses retry logic to handle WSL initialization delays after boot
        /// </summary>
        /// <returns>True if codex is available in WSL, false otherwise</returns>
        private async Task<bool> IsCodexCmdAvailableAsync()
        {
            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync();
                if (!wslInstalled)
                {
                    Debug.WriteLine("WSL is not installed, Codex in WSL not available");
                    return false;
                }

                // Retry logic for WSL commands (handles WSL initialization after boot)
                int[] timeouts = { 5000, 8000, 12000 }; // Progressive timeouts: 5s, 8s, 12s
                int maxRetries = 3;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    Debug.WriteLine($"Codex WSL check attempt {attempt}/{maxRetries}");

                    // Check if codex is available in WSL using 'which codex' with interactive shell
                    // We need -i flag because codex is installed via nvm which requires interactive shell
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -ic \"which codex\"",
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
                            return process.WaitForExit(timeouts[attempt - 1]);
                        });

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }
                            Debug.WriteLine($"Codex check in WSL timed out on attempt {attempt}");

                            // If not the last attempt, wait before retrying
                            if (attempt < maxRetries)
                            {
                                Debug.WriteLine($"Waiting 2 seconds before retry (WSL may be initializing)...");
                                await Task.Delay(2000);
                                continue;
                            }
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();

                        Debug.WriteLine($"Codex WSL check - Exit code: {process.ExitCode}");
                        Debug.WriteLine($"Codex WSL check - Output: {output}");
                        Debug.WriteLine($"Codex WSL check - Error: {error}");

                        // Check if output contains a path to codex (like /home/user/.nvm/versions/node/v22.20.0/bin/codex)
                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("codex");

                        if (isAvailable)
                        {
                            Debug.WriteLine($"Codex in WSL found on attempt {attempt}");
                            return true;
                        }

                        // If we got a response but agent not found, no need to retry
                        if (process.ExitCode == 0 || !string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(error))
                        {
                            Debug.WriteLine($"Codex in WSL not found (WSL responded, agent not installed)");
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            Debug.WriteLine($"WSL didn't respond properly, waiting 2 seconds before retry...");
                            await Task.Delay(2000);
                        }
                    }
                }

                Debug.WriteLine($"Codex in WSL not available after {maxRetries} attempts");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for codex in WSL: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if WSL is installed on the system
        /// </summary>
        /// <returns>True if WSL is installed, false otherwise</returns>
        private async Task<bool> IsWslInstalledAsync()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c wsl --status",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var completed = await Task.Run(() =>
                    {
                        return process.WaitForExit(3000); // 3 second timeout
                    });

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine("WSL check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"WSL check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"WSL check - Output: {output}");
                    Debug.WriteLine($"WSL check - Error: {error}");

                    bool isInstalled = process.ExitCode == 0;
                    Debug.WriteLine($"WSL installed: {isInstalled}");

                    return isInstalled;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for WSL: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if cursor-agent is installed inside WSL by checking for the symlink at ~/.local/bin/cursor-agent
        /// Uses retry logic to handle WSL initialization delays after boot
        /// </summary>
        /// <returns>True if cursor-agent is available in WSL, false otherwise</returns>
        private async Task<bool> IsCursorAgentInstalledInWslAsync()
        {
            try
            {
                // Retry logic for WSL commands (handles WSL initialization after boot)
                int[] timeouts = { 5000, 8000, 12000 }; // Progressive timeouts: 5s, 8s, 12s
                int maxRetries = 3;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    Debug.WriteLine($"Cursor Agent WSL check attempt {attempt}/{maxRetries}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -c \"test -L ~/.local/bin/cursor-agent && echo 'exists' || echo 'notfound'\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        var completed = await Task.Run(() =>
                        {
                            return process.WaitForExit(timeouts[attempt - 1]);
                        });

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }
                            Debug.WriteLine($"Cursor agent check in WSL timed out on attempt {attempt}");

                            // If not the last attempt, wait before retrying
                            if (attempt < maxRetries)
                            {
                                Debug.WriteLine($"Waiting 2 seconds before retry (WSL may be initializing)...");
                                await Task.Delay(2000);
                                continue;
                            }
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();

                        Debug.WriteLine($"Cursor agent WSL check - Exit code: {process.ExitCode}");
                        Debug.WriteLine($"Cursor agent WSL check - Output: {output}");
                        Debug.WriteLine($"Cursor agent WSL check - Error: {error}");

                        bool isInstalled = output.Trim().Equals("exists", StringComparison.OrdinalIgnoreCase);

                        if (isInstalled)
                        {
                            Debug.WriteLine($"Cursor agent found on attempt {attempt}");
                            return true;
                        }

                        // If we got "notfound" response, agent is not installed, no need to retry
                        if (output.Trim().Equals("notfound", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.WriteLine($"Cursor agent not found (WSL responded, agent not installed)");
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            Debug.WriteLine($"WSL didn't respond properly, waiting 2 seconds before retry...");
                            await Task.Delay(2000);
                        }
                    }
                }

                Debug.WriteLine($"Cursor agent not available after {maxRetries} attempts");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for cursor-agent in WSL: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if Qwen Code CLI is available (native or NPM installation)
        /// Prioritizes NPM installation (qwen in PATH) but also checks for other possible installations
        /// </summary>
        /// <returns>True if qwen is available, false otherwise</returns>
        private async Task<bool> IsQwenCodeAvailableAsync()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where qwen",
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
                        Debug.WriteLine("Qwen Code check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Qwen Code check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Qwen Code check - Output: {output}");
                    Debug.WriteLine($"Qwen Code check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Qwen Code availability result: {isAvailable}");

                    return isAvailable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Qwen Code: {ex.Message}");
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
            const string instructions = @"Claude Code is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

RECOMMENDED: Native Installation (Windows)

Open cmd as administrator and run:

curl -fsSL https://claude.ai/install.cmd -o install.cmd && install.cmd && del install.cmd

Then add claude.exe to the PATH environment variable:
C:\Users\%username%\.local\bin

ALTERNATIVE: NPM Installation

If you prefer using NPM, you can install it with:

npm install -g @anthropic-ai/claude-code

For more details, visit: https://docs.claude.com/en/docs/claude-code/setup";

            MessageBox.Show(instructions, "Claude Code Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Claude Code CLI in WSL
        /// </summary>
        private void ShowClaudeCodeWSLInstallationInstructions()
        {
            const string instructions = @"To use Claude Code (WSL), you need to install WSL and Claude Code inside WSL.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

# Start a shell inside of Windows Subsystem for Linux
wsl

# https://learn.microsoft.com/en-us/windows/dev-environment/javascript/nodejs-on-wsl
# Install Node.js in WSL
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/master/install.sh | bash

# In a new tab or after exiting and running `wsl` again to install Node.js
nvm install 22

# Install and run Claude Code in WSL
npm i -g @anthropic-ai/claude-code
claude";

            MessageBox.Show(instructions, "Claude Code (WSL) Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Codex CLI in WSL
        /// </summary>
        private void ShowCodexInstallationInstructions()
        {
            const string instructions = @"To use Codex, you need to install WSL and Codex inside WSL.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

# Start a shell inside of Windows Subsystem for Linux
wsl

# https://learn.microsoft.com/en-us/windows/dev-environment/javascript/nodejs-on-wsl
# Install Node.js in WSL
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/master/install.sh | bash

# In a new tab or after exiting and running `wsl` again to install Node.js
nvm install 22

# Install and run Codex in WSL
npm i -g @openai/codex
codex";

            MessageBox.Show(instructions, "Codex Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Cursor Agent (requires WSL)
        /// </summary>
        private void ShowCursorAgentInstallationInstructions()
        {
            const string instructions = @"To use Cursor Agent, you need to install WSL and cursor-agent.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

Install cursor agent inside WSL:

wsl 

curl https://cursor.com/install -fsS | bash

Copy and paste the 2 suggested commands to add cursor to path:

echo 'export PATH=""$HOME/.local/bin:$PATH""' >> ~/.bashrc
source ~/.bashrc

Start cursor-agent to login:

cursor-agent";

            MessageBox.Show(instructions, "Cursor Agent Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Qwen Code CLI
        /// </summary>
        private void ShowQwenCodeInstallationInstructions()
        {
            const string instructions = @"Qwen Code is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

INSTALLATION: NPM Installation (Recommended)

Open cmd and run:

npm install -g @qwen-code/qwen-code@latest

Alternatively, you can install from source:

git clone https://github.com/QwenLM/qwen-code.git
cd qwen-code
npm install
npm install -g .

Requirements:
- Node.js version 20 or higher

For more details, visit: https://github.com/QwenLM/qwen-code";

            MessageBox.Show(instructions, "Qwen Code Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Provider Switching

        /// <summary>
        /// Handles Qwen Code menu item click - switches to Qwen Code provider
        /// </summary>
        private void QwenCodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool qwenCodeAvailable = await IsQwenCodeAvailableAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.QwenCode;
                UpdateProviderSelection();
                SaveSettings();

                if (!qwenCodeAvailable)
                {
                    ShowQwenCodeInstallationInstructions();
                    await StartEmbeddedTerminalAsync(null); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(AiProvider.QwenCode);
                }
            });
        }

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
                    await StartEmbeddedTerminalAsync(null); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(AiProvider.ClaudeCode);
                }
            });
        }

        /// <summary>
        /// Handles Claude Code (WSL) menu item click - switches to Claude Code (WSL) provider
        /// </summary>
        private void ClaudeCodeWSLMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool claudeWSLAvailable = await IsClaudeCodeWSLAvailableAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.ClaudeCodeWSL;
                UpdateProviderSelection();
                SaveSettings();

                if (!claudeWSLAvailable)
                {
                    ShowClaudeCodeWSLInstallationInstructions();
                    await StartEmbeddedTerminalAsync(null); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(AiProvider.ClaudeCodeWSL);
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
                    await StartEmbeddedTerminalAsync(null); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(AiProvider.Codex);
                }
            });
        }

        /// <summary>
        /// Handles Cursor Agent menu item click - switches to Cursor Agent provider
        /// </summary>
        private void CursorAgentMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool wslInstalled = await IsWslInstalledAsync();
                bool cursorAgentInstalled = false;

                if (wslInstalled)
                {
                    cursorAgentInstalled = await IsCursorAgentInstalledInWslAsync();
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.CursorAgent;
                UpdateProviderSelection();
                SaveSettings();

                if (!wslInstalled || !cursorAgentInstalled)
                {
                    ShowCursorAgentInstallationInstructions();
                    await StartEmbeddedTerminalAsync(null); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(AiProvider.CursorAgent);
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
            ClaudeCodeWSLMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.ClaudeCodeWSL;
            CodexMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.Codex;
            CursorAgentMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.CursorAgent;
            QwenCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.QwenCode;

            // Update GroupBox header to show selected provider (not necessarily running yet)
            string providerName = _settings.SelectedProvider == AiProvider.ClaudeCode ? "Claude Code" :
                                  _settings.SelectedProvider == AiProvider.ClaudeCodeWSL ? "Claude Code" :
                                  _settings.SelectedProvider == AiProvider.Codex ? "Codex" :
                                  _settings.SelectedProvider == AiProvider.QwenCode ? "Qwen Code" :
                                  "Cursor Agent";
            TerminalGroupBox.Header = providerName;

            // Note: Tool window title will be updated after the terminal actually starts
            // in StartEmbeddedTerminalAsync to reflect what's actually running
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
                                $"Provides seamless integration with Claude Code, Codex and Cursor Agent AI assistants directly within Visual Studio 2022 IDE.";

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