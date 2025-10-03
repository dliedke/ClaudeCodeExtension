/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Terminal window initialization, embedding, and process management
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
        #region Terminal Fields

        /// <summary>
        /// Windows Forms panel hosting the embedded terminal
        /// </summary>
        private System.Windows.Forms.Panel terminalPanel;

        /// <summary>
        /// The CMD process running the terminal
        /// </summary>
        private Process cmdProcess;

        /// <summary>
        /// Handle to the terminal window
        /// </summary>
        private IntPtr terminalHandle;

        #endregion

        #region Terminal Initialization

        /// <summary>
        /// Initializes the embedded terminal with the selected AI provider
        /// </summary>
        private async Task InitializeTerminalAsync()
        {
            try
            {
                // Determine which provider to use based on settings
                bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                bool useCursorAgent = _settings?.SelectedProvider == AiProvider.CursorAgent;
                bool providerAvailable = false;

                Debug.WriteLine($"User selected provider: {(useCursorAgent ? "Cursor Agent" : useCodex ? "Codex" : "Claude Code")}");

                if (useCursorAgent)
                {
                    Debug.WriteLine("Checking WSL and cursor-agent availability for Cursor Agent...");
                    bool wslInstalled = await IsWslInstalledAsync();
                    if (wslInstalled)
                    {
                        providerAvailable = await IsCursorAgentInstalledInWslAsync();
                    }
                    Debug.WriteLine($"WSL available: {wslInstalled}, cursor-agent available: {providerAvailable}");
                }
                else if (useCodex)
                {
                    Debug.WriteLine("Checking Codex availability...");
                    providerAvailable = await IsCodexCmdAvailableAsync();
                    Debug.WriteLine($"Codex available: {providerAvailable}");
                }
                else
                {
                    Debug.WriteLine("Checking Claude Code availability...");
                    providerAvailable = await IsClaudeCmdAvailableAsync();
                    Debug.WriteLine($"Claude Code available: {providerAvailable}");
                }

                // Switch to main thread for UI operations
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Ensure TerminalHost is available
                if (TerminalHost == null)
                {
                    Debug.WriteLine("Error: TerminalHost is null");
                    return;
                }

                // Create the terminal panel
                terminalPanel = new System.Windows.Forms.Panel
                {
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    BackColor = GetTerminalBackgroundColor()
                };

                TerminalHost.Child = terminalPanel;

                if (terminalPanel?.Handle == IntPtr.Zero)
                {
                    Debug.WriteLine("Warning: terminalPanel handle not yet created, waiting...");
                    await Task.Delay(100);
                }

                terminalPanel.Resize += (s, e) => ResizeEmbeddedTerminal();

                await Task.Delay(500);

                // Start the selected provider if available, otherwise show message and use regular CMD
                if (useCursorAgent)
                {
                    if (providerAvailable)
                    {
                        Debug.WriteLine("Starting Cursor Agent terminal...");
                        await StartEmbeddedTerminalAsync(false, false, true); // Cursor Agent
                        UpdateToolWindowTitle("Cursor Agent");
                    }
                    else
                    {
                        Debug.WriteLine("WSL not available, showing installation instructions...");
                        if (!_cursorAgentNotificationShown)
                        {
                            _cursorAgentNotificationShown = true;
                            ShowCursorAgentInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(false, false, false); // Regular CMD
                    }
                }
                else if (useCodex)
                {
                    if (providerAvailable)
                    {
                        Debug.WriteLine("Starting Codex terminal...");
                        await StartEmbeddedTerminalAsync(false, true, false); // Codex
                        UpdateToolWindowTitle("Codex");
                    }
                    else
                    {
                        Debug.WriteLine("Codex not available, showing installation instructions...");
                        if (!_codexNotificationShown)
                        {
                            _codexNotificationShown = true;
                            ShowCodexInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(false, false, false); // Regular CMD
                    }
                }
                else
                {
                    if (providerAvailable)
                    {
                        Debug.WriteLine("Starting Claude Code terminal...");
                        await StartEmbeddedTerminalAsync(true, false, false); // Claude Code
                        UpdateToolWindowTitle("Claude Code");
                    }
                    else
                    {
                        Debug.WriteLine("Claude Code not available, showing installation instructions...");
                        if (!_claudeNotificationShown)
                        {
                            _claudeNotificationShown = true;
                            ShowClaudeInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(false, false, false); // Regular CMD
                    }
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Debug.WriteLine($"Error in InitializeTerminalAsync: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Failed to initialize terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Starts and embeds the terminal process (Claude Code, Codex, Cursor Agent, or regular CMD)
        /// </summary>
        /// <param name="claudeAvailable">True if Claude Code should be started</param>
        /// <param name="useCodex">True if Codex should be started</param>
        /// <param name="useCursorAgent">True if Cursor Agent should be started</param>
        private async Task StartEmbeddedTerminalAsync(bool claudeAvailable = true, bool useCodex = false, bool useCursorAgent = false)
        {
            try
            {
                string workspaceDir = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrEmpty(workspaceDir))
                {
                    Debug.WriteLine("Warning: Workspace directory is null or empty");
                    workspaceDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                Debug.WriteLine($"StartEmbeddedTerminalAsync: Setting workspace directory to: {workspaceDir}");
                Debug.WriteLine($"StartEmbeddedTerminalAsync: Previous workspace directory was: {_lastWorkspaceDirectory}");
                _lastWorkspaceDirectory = workspaceDir;

                // Kill existing process if running
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    try
                    {
                        // Send exit command to gracefully close Claude Code before killing the process
                        Debug.WriteLine("Sending exit command to terminal before restarting...");

                        // Type "exit" by copying to clipboard and pasting
                        if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                        {
                            Clipboard.SetText("exit");
                            SetForegroundWindow(terminalHandle);
                            SetFocus(terminalHandle);
                            System.Threading.Thread.Sleep(100);

                            // Paste the exit command
                            GetWindowRect(terminalHandle, out RECT rect);
                            int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                            int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;
                            SendRightClick(centerX, centerY);

                            System.Threading.Thread.Sleep(200);

                            // Send Enter key
                            PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                        }

                        // Give it a moment to process the exit command
                        await Task.Delay(500);

                        // Force kill if still running
                        if (!cmdProcess.HasExited)
                        {
                            cmdProcess.Kill();
                        }
                        cmdProcess.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing previous process: {ex.Message}");
                    }
                    cmdProcess = null;
                }

                // Reset terminal handle when restarting
                terminalHandle = IntPtr.Zero;

                // Build the terminal command based on provider
                string terminalCommand;
                if (useCursorAgent)
                {
                    Debug.WriteLine($"Starting Cursor Agent via WSL in directory: {workspaceDir}");

                    // Convert Windows path to WSL path format (C:\GitLab\Project -> /mnt/c/GitLab/Project)
                    string wslPath = ConvertToWslPath(workspaceDir);
                    terminalCommand = $"/k wsl bash -ic \"cd {wslPath} && cursor-agent\"";
                }
                else if (useCodex)
                {
                    Debug.WriteLine($"Starting Codex in directory: {workspaceDir}");

                    // Try known Codex path first
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string codexPath = Path.Combine(userProfile, "AppData", "Roaming", "npm", "codex.cmd");

                    if (File.Exists(codexPath))
                    {
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && \"{codexPath}\"";
                    }
                    else
                    {
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && codex.cmd";
                    }
                }
                else if (claudeAvailable)
                {
                    Debug.WriteLine($"Starting Claude in directory: {workspaceDir}");
                    terminalCommand = "/k cd /d \"" + workspaceDir + "\" && claude.cmd";
                }
                else
                {
                    Debug.WriteLine($"Starting regular CMD in directory: {workspaceDir}");
                    terminalCommand = "/k cd /d \"" + workspaceDir + "\"";
                }

                // Configure and start the process
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = terminalCommand,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    WorkingDirectory = workspaceDir
                };

                await Task.Run(() =>
                {
                    try
                    {
                        cmdProcess = new Process { StartInfo = startInfo };
                        cmdProcess.Start();
                        Debug.WriteLine($"Process started with ID: {cmdProcess.Id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error starting process: {ex.Message}");
                        throw;
                    }
                });

                if (cmdProcess == null)
                {
                    throw new InvalidOperationException("Failed to create terminal process");
                }

                // Find and embed the terminal window
                var hwnd = FindMainWindowHandleByPid(cmdProcess.Id, timeoutMs: 7000, pollIntervalMs: 50);
                terminalHandle = hwnd;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    if (terminalPanel?.Handle == null || terminalPanel.Handle == IntPtr.Zero)
                    {
                        Debug.WriteLine("Warning: terminalPanel.Handle is null or invalid");
                        return;
                    }

                    try
                    {
                        // Hide the window immediately to prevent blinking
                        ShowWindow(terminalHandle, SW_HIDE);

                        // Embed the window
                        SetParent(terminalHandle, terminalPanel.Handle);

                        // Remove window decorations
                        SetWindowLong(terminalHandle, GWL_STYLE,
                            GetWindowLong(terminalHandle, GWL_STYLE) & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU));

                        // Now show it in the embedded context
                        ShowWindow(terminalHandle, SW_SHOW);
                        ResizeEmbeddedTerminal();

                        Debug.WriteLine("Terminal embedded successfully");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error embedding terminal window: {ex.Message}");
                        throw;
                    }
                }
                else
                {
                    Debug.WriteLine("Could not find CMD window to embed. Terminal may not be available.");
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to start embedded terminal: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Terminal Window Management

        /// <summary>
        /// Resizes the embedded terminal window to match the panel size
        /// </summary>
        private void ResizeEmbeddedTerminal()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle) && terminalPanel != null)
            {
                SetWindowPos(terminalHandle, IntPtr.Zero, 0, 0,
                            terminalPanel.Width, terminalPanel.Height,
                            SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        /// <summary>
        /// Finds the main window handle for a process by its process ID
        /// </summary>
        /// <param name="targetPid">The process ID to search for</param>
        /// <param name="timeoutMs">Timeout in milliseconds</param>
        /// <param name="pollIntervalMs">Polling interval in milliseconds</param>
        /// <returns>The window handle, or IntPtr.Zero if not found</returns>
        private static IntPtr FindMainWindowHandleByPid(int targetPid, int timeoutMs = 5000, int pollIntervalMs = 50)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr found = IntPtr.Zero;

                EnumWindows((hWnd, lParam) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == targetPid)
                    {
                        found = hWnd;
                        // Hide the window immediately to prevent any blinking
                        ShowWindow(hWnd, SW_HIDE);
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                    return found;

                System.Threading.Thread.Sleep(pollIntervalMs);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Handles the restart terminal button click event
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void RestartTerminalButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                // Determine which provider to use based on settings
                bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                bool useCursorAgent = _settings?.SelectedProvider == AiProvider.CursorAgent;
                bool claudeAvailable = false;
                bool codexAvailable = false;
                bool wslAvailable = false;

                if (useCursorAgent)
                {
                    wslAvailable = await IsWslInstalledAsync();
                    if (wslAvailable)
                    {
                        wslAvailable = await IsCursorAgentInstalledInWslAsync();
                    }
                }
                else if (useCodex)
                {
                    codexAvailable = await IsCodexCmdAvailableAsync();
                }
                else
                {
                    claudeAvailable = await IsClaudeCmdAvailableAsync();
                }

                await StartEmbeddedTerminalAsync(claudeAvailable, useCodex && codexAvailable, useCursorAgent && wslAvailable);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in RestartTerminalButton_Click: {ex.Message}");
                MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Converts a Windows path to WSL path format
        /// Example: C:\GitLab\Project -> /mnt/c/GitLab/Project
        /// </summary>
        /// <param name="windowsPath">Windows path to convert</param>
        /// <returns>WSL-formatted path</returns>
        private string ConvertToWslPath(string windowsPath)
        {
            if (string.IsNullOrEmpty(windowsPath))
                return string.Empty;

            // Get the drive letter and convert to lowercase
            string driveLetter = windowsPath.Substring(0, 1).ToLower();

            // Remove the drive letter and colon, then replace backslashes with forward slashes
            string pathWithoutDrive = windowsPath.Substring(2).Replace("\\", "/");

            // Return the WSL path format
            return $"/mnt/{driveLetter}{pathWithoutDrive}";
        }

        #endregion
    }
}