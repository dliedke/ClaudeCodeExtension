/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Terminal window initialization, embedding, and process management
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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

        /// <summary>
        /// Tracks the currently running AI provider (before any new selection)
        /// </summary>
        private AiProvider? _currentRunningProvider = null;

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
                bool useClaudeCodeWSL = _settings?.SelectedProvider == AiProvider.ClaudeCodeWSL;
                bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                bool useCursorAgent = _settings?.SelectedProvider == AiProvider.CursorAgent;
                bool useQwenCode = _settings?.SelectedProvider == AiProvider.QwenCode;
                bool useOpenCode = _settings?.SelectedProvider == AiProvider.OpenCode;
                bool providerAvailable = false;


                if (useCursorAgent)
                {
                    bool wslInstalled = await IsWslInstalledAsync();
                    if (wslInstalled)
                    {
                        providerAvailable = await IsCursorAgentInstalledInWslAsync();
                    }
                }
                else if (useCodex)
                {
                    providerAvailable = await IsCodexCmdAvailableAsync();
                }
                else if (useClaudeCodeWSL)
                {
                    providerAvailable = await IsClaudeCodeWSLAvailableAsync();
                }
                else if (useQwenCode)
                {
                    providerAvailable = await IsQwenCodeAvailableAsync();
                }
                else if (useOpenCode)
                {
                    providerAvailable = await IsOpenCodeAvailableAsync();
                }
                else
                {
                    providerAvailable = await IsClaudeCmdAvailableAsync();
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
                    await Task.Delay(100);
                }

                terminalPanel.Resize += (s, e) => ResizeEmbeddedTerminal();

                // Wait for panel to be properly sized (not just created) - reduced timeout
                int maxWaitMs = 2000; // Reduced from 5 seconds to 2 seconds
                int waitedMs = 0;
                while ((terminalPanel.Width < 100 || terminalPanel.Height < 100) && waitedMs < maxWaitMs)
                {
                    await Task.Delay(50); // Reduced poll interval from 100ms to 50ms
                    waitedMs += 50;
                }


                // Start the selected provider if available, otherwise show message and use regular CMD
                if (useCursorAgent)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.CursorAgent);
                    }
                    else
                    {
                        if (!_cursorAgentNotificationShown)
                        {
                            _cursorAgentNotificationShown = true;
                            ShowCursorAgentInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (useCodex)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.Codex);
                    }
                    else
                    {
                        if (!_codexNotificationShown)
                        {
                            _codexNotificationShown = true;
                            ShowCodexInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (useClaudeCodeWSL)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.ClaudeCodeWSL);
                    }
                    else
                    {
                        if (!_claudeCodeWSLNotificationShown)
                        {
                            _claudeCodeWSLNotificationShown = true;
                            ShowClaudeCodeWSLInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (useQwenCode)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.QwenCode);
                    }
                    else
                    {
                        if (!_qwenCodeNotificationShown)
                        {
                            _qwenCodeNotificationShown = true;
                            ShowQwenCodeInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (useOpenCode)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.OpenCode);
                    }
                    else
                    {
                        if (!_openCodeNotificationShown)
                        {
                            _openCodeNotificationShown = true;
                            ShowOpenCodeInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.ClaudeCode);
                    }
                    else
                    {
                        if (!_claudeNotificationShown)
                        {
                            _claudeNotificationShown = true;
                            ShowClaudeInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
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
        /// Starts and embeds the terminal process (Claude Code, Claude Code WSL, Codex, Cursor Agent, or regular CMD)
        /// </summary>
        /// <param name="provider">The AI provider to start (null for regular CMD)</param>
        private async Task StartEmbeddedTerminalAsync(AiProvider? provider)
        {
            try
            {
                string workspaceDir = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrEmpty(workspaceDir))
                {
                    workspaceDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                _lastWorkspaceDirectory = workspaceDir;

                // Kill existing process if running
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    try
                    {
                        if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                        {
                            // Check if CURRENTLY RUNNING provider is Codex (requires CTRL+C instead of exit)
                            bool isCodex = _currentRunningProvider == AiProvider.Codex;

                            if (isCodex)
                            {
                                // For Codex, send CTRL+C twice to exit
                                SendCtrlC();
                                await Task.Delay(400); // Reduced from 500ms
                                SendCtrlC();
                            }
                            else
                            {
                                // For other agents including QwenCode, send appropriate exit command
                                if (_currentRunningProvider == AiProvider.QwenCode)
                                {
                                    await SendTextToTerminalAsync("/quit");
                                }
                                else
                                {
                                    await SendTextToTerminalAsync("exit");
                                }
                            }

                            // Give it time to exit - reduced delay
                            await Task.Delay(1000);
                        }

                        // Force kill
                        cmdProcess.Kill();
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
                // Use cls to clear initial Windows banner for clean appearance
                string terminalCommand;
                switch (provider)
                {
                    case AiProvider.CursorAgent:
                        string wslPathCursor = ConvertToWslPath(workspaceDir);
                        terminalCommand = $"/k cls && wsl bash -ic \"cd {wslPathCursor} && cursor-agent\"";
                        break;

                    case AiProvider.Codex:
                        string wslPathCodex = ConvertToWslPath(workspaceDir);
                        terminalCommand = $"/k cls && wsl bash -ic \"cd {wslPathCodex} && codex\"";
                        break;

                    case AiProvider.ClaudeCodeWSL:
                        string wslPathClaude = ConvertToWslPath(workspaceDir);
                        terminalCommand = $"/k cls && wsl bash -ic \"cd {wslPathClaude} && claude\"";
                        break;

                    case AiProvider.ClaudeCode:
                        string claudeCommand = GetClaudeCommand();
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {claudeCommand}";
                        break;

                    case AiProvider.QwenCode:
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && qwen";
                        break;

                    case AiProvider.OpenCode:
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && opencode";
                        break;

                    default: // null or any other value = regular CMD
                        terminalCommand = $"/k cd /d \"{workspaceDir}\"";
                        break;
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

                // Find and embed the terminal window (using async version with reduced timeout)
                var hwnd = await FindMainWindowHandleByPidAsync(cmdProcess.Id, timeoutMs: 5000, pollIntervalMs: 50);
                terminalHandle = hwnd;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    if (terminalPanel?.Handle == null || terminalPanel.Handle == IntPtr.Zero)
                    {
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

                        // Track the currently running provider
                        _currentRunningProvider = provider;

                        string providerTitle;
                        switch (provider)
                        {
                            case AiProvider.CursorAgent:
                                providerTitle = "Cursor Agent";
                                break;
                            case AiProvider.Codex:
                                providerTitle = "Codex";
                                break;
                            case AiProvider.ClaudeCodeWSL:
                                providerTitle = "Claude Code";
                                break;
                            case AiProvider.ClaudeCode:
                                providerTitle = "Claude Code";
                                break;
                            case AiProvider.QwenCode:
                                providerTitle = "Qwen Code";
                                break;
                            case AiProvider.OpenCode:
                                providerTitle = "Open Code";
                                break;
                            default:
                                providerTitle = "CMD";
                                break;
                        }

                        UpdateToolWindowTitle(providerTitle);
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
        /// Finds the main window handle for a process by its process ID (async version)
        /// </summary>
        /// <param name="targetPid">The process ID to search for</param>
        /// <param name="timeoutMs">Timeout in milliseconds</param>
        /// <param name="pollIntervalMs">Polling interval in milliseconds</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The window handle, or IntPtr.Zero if not found</returns>
        private static async Task<IntPtr> FindMainWindowHandleByPidAsync(int targetPid, int timeoutMs = 5000, int pollIntervalMs = 50, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

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

                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Finds the main window handle for a process by its process ID (sync version for backward compat)
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

                Thread.Sleep(pollIntervalMs);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Handles the update agent button click event
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void UpdateAgentButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    MessageBox.Show("Terminal is not running. Please restart the terminal first.",
                                  "Update Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Use CURRENTLY RUNNING provider (not the next one being set)
                // Send exit, wait, then update command based on provider
                switch (_currentRunningProvider)
                {
                    case AiProvider.Codex:
                        // Codex requires CTRL+C to exit
                        SendCtrlC();
                        await Task.Delay(400); // Reduced from 500ms
                        SendCtrlC();
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("wsl bash -ic \"npm install -g @openai/codex@latest\"");
                        break;

                    case AiProvider.CursorAgent:
                        // CursorAgent: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("wsl bash -ic \"cursor-agent update\"");
                        break;

                    case AiProvider.ClaudeCodeWSL:
                        // Claude Code WSL: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("wsl bash -ic \"claude update\"");
                        break;

                    case AiProvider.ClaudeCode:
                        // Claude Code Windows: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("claude update");
                        break;

                    case AiProvider.QwenCode:
                        // Qwen Code: send /quit command to exit
                        await SendTextToTerminalAsync("/quit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("npm install -g @qwen-code/qwen-code@latest");
                        break;

                    case AiProvider.OpenCode:
                        // Open Code: send exit command
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("npm i -g opencode-ai");
                        break;

                    default:
                        // Regular CMD - just try to update Claude if available
                        await SendTextToTerminalAsync("claude update");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in UpdateAgentButton_Click: {ex.Message}");
                MessageBox.Show($"Failed to update agent: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Sends CTRL+C to the terminal window using multiple methods
        /// </summary>
        private void SendCtrlC()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                SetForegroundWindow(terminalHandle);
                SetFocus(terminalHandle);
                Thread.Sleep(50); // Reduced from 100ms

                // Clear clipboard before copying new text to prevent stale content
                Clipboard.Clear();

                // Click center
                RightClickTerminalCenter();

                // Method 1: Try using keybd_event (simulates global keyboard input)
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero); // CTRL down
                Thread.Sleep(30); // Reduced from 50ms
                keybd_event(VK_C, 0, 0, UIntPtr.Zero); // C down
                Thread.Sleep(30); // Reduced from 50ms
                keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // C up
                Thread.Sleep(30); // Reduced from 50ms
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // CTRL up
            }
        }

        /// <summary>
        /// Alternative method to send CTRL+C using SendInput API
        /// </summary>
        private void SendCtrlCWithSendInput()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                SetForegroundWindow(terminalHandle);
                SetFocus(terminalHandle);
                Thread.Sleep(50); // Reduced from 100ms

                // Clear clipboard before copying new text to prevent stale content
                Clipboard.Clear();

                // Click center
                RightClickTerminalCenter();

                // Create input array for CTRL down, C down, C up, CTRL up
                INPUT[] inputs = new INPUT[4];

                // CTRL down
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = (ushort)VK_CONTROL;
                inputs[0].u.ki.dwFlags = 0;

                // C down
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].u.ki.wVk = (ushort)VK_C;
                inputs[1].u.ki.dwFlags = 0;

                // C up
                inputs[2].type = INPUT_KEYBOARD;
                inputs[2].u.ki.wVk = (ushort)VK_C;
                inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;

                // CTRL up
                inputs[3].type = INPUT_KEYBOARD;
                inputs[3].u.ki.wVk = (ushort)VK_CONTROL;
                inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

                SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        /// <summary>
        /// Alternative method to send CTRL+C using PostMessage
        /// </summary>
        private void SendCtrlCWithPostMessage()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                SetForegroundWindow(terminalHandle);
                SetFocus(terminalHandle);
                Thread.Sleep(50); // Reduced from 100ms

                // Clear clipboard before copying new text to prevent stale content
                Clipboard.Clear();

                // Click center
                RightClickTerminalCenter();

                // Send CTRL down
                PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_CONTROL), IntPtr.Zero);
                Thread.Sleep(30); // Reduced from 50ms

                // Send C down
                PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_C), IntPtr.Zero);
                Thread.Sleep(30); // Reduced from 50ms

                // Send C up
                PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_C), IntPtr.Zero);
                Thread.Sleep(30); // Reduced from 50ms

                // Send CTRL up
                PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_CONTROL), IntPtr.Zero);
            }
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
                // Get the selected provider from settings
                AiProvider? selectedProvider = _settings?.SelectedProvider;
                bool providerAvailable = false;

                // Check if the selected provider is available
                switch (selectedProvider)
                {
                    case AiProvider.CursorAgent:
                        bool wslAvailable = await IsWslInstalledAsync();
                        if (wslAvailable)
                        {
                            providerAvailable = await IsCursorAgentInstalledInWslAsync();
                        }
                        break;

                    case AiProvider.Codex:
                        providerAvailable = await IsCodexCmdAvailableAsync();
                        break;

                    case AiProvider.ClaudeCodeWSL:
                        providerAvailable = await IsClaudeCodeWSLAvailableAsync();
                        break;

                    case AiProvider.ClaudeCode:
                        providerAvailable = await IsClaudeCmdAvailableAsync();
                        break;

                    case AiProvider.QwenCode:
                        providerAvailable = await IsQwenCodeAvailableAsync();
                        break;

                    case AiProvider.OpenCode:
                        providerAvailable = await IsOpenCodeAvailableAsync();
                        break;
                }

                // Start the terminal with the selected provider if available, otherwise regular CMD
                await StartEmbeddedTerminalAsync(providerAvailable ? selectedProvider : null);
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

        /// <summary>
        /// Gets the appropriate Claude Code command to use
        /// Prioritizes native installation (%USERPROFILE%\.local\bin\claude.exe) over NPM installation (claude.cmd)
        /// </summary>
        /// <returns>The claude command to execute</returns>
        private string GetClaudeCommand()
        {
            // Check for native installation first
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string nativeClaudePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");

            if (File.Exists(nativeClaudePath))
            {
                return $"\"{nativeClaudePath}\"";
            }

            // Fall back to NPM installation
            return "claude.cmd";
        }

        #endregion
    }
}
