/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Terminal window initialization, embedding, and process management
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

        /// <summary>
        /// Height of the Windows Terminal tab bar in pixels (0 for Command Prompt mode)
        /// </summary>
        private int _wtTabBarHeight = 0;

        /// <summary>
        /// Full resolved path to wt.exe (set by IsWindowsTerminalAvailableAsync)
        /// </summary>
        private string _wtExePath = null;

        /// <summary>
        /// Handle to the low-level mouse hook used for tracking Ctrl+Scroll zoom on the embedded terminal
        /// </summary>
        private IntPtr _mouseHookHandle = IntPtr.Zero;

        /// <summary>
        /// Prevent GC from collecting the hook callback delegate
        /// </summary>
        private LowLevelMouseProc _mouseHookProc;

        /// <summary>
        /// Debounce timer for saving zoom delta to settings after Ctrl+Scroll
        /// </summary>
        private System.Windows.Threading.DispatcherTimer _zoomSaveTimer;

        /// <summary>
        /// Mouse drag distance in pixels before Windows Terminal selection assist kicks in
        /// </summary>
        private const int WindowsTerminalSelectionDragThreshold = 4;

        /// <summary>
        /// Tracks a pending left-drag inside Windows Terminal
        /// </summary>
        private bool _windowsTerminalSelectionPending;

        /// <summary>
        /// Tracks when selection assist is active for the current drag
        /// </summary>
        private bool _windowsTerminalSelectionActive;

        /// <summary>
        /// True when this control injected SHIFT to force text selection in Windows Terminal
        /// </summary>
        private bool _windowsTerminalSelectionModifierInjected;

        /// <summary>
        /// Drag start point for Windows Terminal selection assist
        /// </summary>
        private POINT _windowsTerminalSelectionStartPoint;

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
                bool useCodexNative = _settings?.SelectedProvider == AiProvider.CodexNative;
                bool useCursorAgent = _settings?.SelectedProvider == AiProvider.CursorAgent;
                bool useCursorAgentNative = _settings?.SelectedProvider == AiProvider.CursorAgentNative;
                bool useQwenCode = _settings?.SelectedProvider == AiProvider.QwenCode;
                bool useOpenCode = _settings?.SelectedProvider == AiProvider.OpenCode;
                bool providerAvailable = false;


                if (useCursorAgentNative)
                {
                    providerAvailable = await IsCursorAgentNativeAvailableAsync();
                }
                else if (useCursorAgent)
                {
                    bool wslInstalled = await IsWslInstalledAsync();
                    if (wslInstalled)
                    {
                        providerAvailable = await IsCursorAgentInstalledInWslAsync();
                    }
                }
                else if (useCodexNative)
                {
                    providerAvailable = await IsCodexNativeAvailableAsync();
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

                // Install low-level mouse hook to track Ctrl+Scroll zoom on the embedded terminal
                // (WPF PreviewMouseWheel doesn't fire for embedded Win32 windows from other processes)
                InstallMouseHook();

                // Wait for panel to be properly sized (not just created) - reduced timeout
                int maxWaitMs = 2000; // Reduced from 5 seconds to 2 seconds
                int waitedMs = 0;
                while ((terminalPanel.Width < 100 || terminalPanel.Height < 100) && waitedMs < maxWaitMs)
                {
                    await Task.Delay(50); // Reduced poll interval from 100ms to 50ms
                    waitedMs += 50;
                }


                // Start the selected provider if available, otherwise show message and use regular CMD
                if (useCursorAgentNative)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.CursorAgentNative);
                    }
                    else
                    {
                        if (!_cursorAgentNativeNotificationShown)
                        {
                            _cursorAgentNativeNotificationShown = true;
                            ShowCursorAgentNativeInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (useCursorAgent)
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
                else if (useCodexNative)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.CodexNative);
                    }
                    else
                    {
                        if (!_codexNativeNotificationShown)
                        {
                            _codexNativeNotificationShown = true;
                            ShowCodexNativeInstallationInstructions();
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

                            if (isCodex || _currentRunningProvider == AiProvider.CodexNative)
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
                ResetWindowsTerminalSelectionTracking();
                terminalHandle = IntPtr.Zero;
                _wtTabBarHeight = 0;

                // Check if we should use Windows Terminal instead of Command Prompt
                bool useWindowsTerminal = _settings?.SelectedTerminalType == TerminalType.WindowsTerminal;

                if (useWindowsTerminal)
                {
                    // Windows Terminal mode: launch wt.exe with embedded cmd.exe
                    // Build the command for cmd.exe that will run inside WT
                    string cmdCommand;
                    switch (provider)
                    {
                        case AiProvider.CursorAgentNative:
                            string cursorAgentCommand = GetCursorAgentCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {cursorAgentCommand}";
                            break;

                        case AiProvider.CursorAgent:
                            string wslPathCursor = ConvertToWslPath(workspaceDir);
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -ic \"cd '{wslPathCursor}' && cursor-agent\"";
                            break;

                        case AiProvider.CodexNative:
                            string codexCommand = GetCodexCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {codexCommand}";
                            break;

                        case AiProvider.Codex:
                            string wslPathCodex = ConvertToWslPath(workspaceDir);
                            string codexWslCommand = GetCodexCommand(isWsl: true);
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -ic \"cd '{wslPathCodex}' && {codexWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            string wslPathClaude = ConvertToWslPath(workspaceDir);
                            string claudeWslCommand = GetClaudeCommand(isWsl: true);
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -ic \"cd '{wslPathClaude}' && {claudeWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCode:
                            string claudeCommand = GetClaudeCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {claudeCommand}";
                            break;

                        case AiProvider.QwenCode:
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && qwen";
                            break;

                        case AiProvider.OpenCode:
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && opencode";
                            break;

                        default: // null or any other value = regular CMD
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\"";
                            break;
                    }

                    // Snapshot existing WT windows before launching a new one
                    var existingWtWindows = SnapshotExistingWtWindows();

                    // Resolve wt.exe path if not already cached
                    if (string.IsNullOrEmpty(_wtExePath))
                    {
                        await IsWindowsTerminalAvailableAsync();
                    }
                    string wtFileName = !string.IsNullOrEmpty(_wtExePath) ? _wtExePath : "wt.exe";

                    // Start Windows Terminal with embedded cmd.exe
                    var wtStartInfo = new ProcessStartInfo
                    {
                        FileName = wtFileName,
                        Arguments = $"--window new -- cmd.exe {cmdCommand}",
                        UseShellExecute = false,
                        CreateNoWindow = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = workspaceDir
                    };

                    // Refresh PATH from registry
                    string freshPath = GetFreshPathFromRegistry();
                    if (!string.IsNullOrEmpty(freshPath))
                    {
                        wtStartInfo.EnvironmentVariables["PATH"] = freshPath;
                    }

                    await Task.Run(() =>
                    {
                        try
                        {
                            cmdProcess = new Process { StartInfo = wtStartInfo };
                            cmdProcess.Start();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error starting Windows Terminal process: {ex.Message}");
                            throw;
                        }
                    });

                    if (cmdProcess == null)
                    {
                        throw new InvalidOperationException("Failed to create Windows Terminal process");
                    }

                    // Find the new WT window (not in the existing snapshot)
                    var hwnd = await FindNewWtWindowAsync(existingWtWindows, timeoutMs: 15000);
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

                            // Remove window decorations (WT windows have minimal decorations by default)
                            SetWindowLong(terminalHandle, GWL_STYLE,
                                GetWindowLong(terminalHandle, GWL_STYLE) & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU));

                            // Calculate tab bar height
                            _wtTabBarHeight = GetWtTabBarHeight();

                            // Show and resize
                            ShowWindow(terminalHandle, SW_SHOW);
                            ResizeEmbeddedTerminal();

                            // Retry resize after a short delay to ensure it takes effect
                            await Task.Delay(500);
                            ResizeEmbeddedTerminal();

                            // Apply zoom out for Windows Terminal for better visibility
                            await Task.Delay(300);
                            await ApplyWindowsTerminalZoomOutAsync();

                            // Track the currently running provider
                            _currentRunningProvider = provider;

                            // If terminal should be detached, re-parent to detached panel
                            if (_isTerminalDetached && _detachedTerminalPanel != null)
                            {
                                SetParent(terminalHandle, _detachedTerminalPanel.Handle);
                                ShowWindow(terminalHandle, SW_SHOW);
                                ResizeEmbeddedTerminal();
                                string wtProviderName = GetCurrentProviderName();
                                _detachedTerminalWindow?.UpdateCaption(wtProviderName);
                            }

                            // Replay saved user zoom delta AFTER all re-parenting is done
                            // (SetParent can reset zoom state)
                            if (_settings?.TerminalZoomDelta != 0)
                            {
                                await ApplyTerminalZoomDeltaAsync(_settings.TerminalZoomDelta);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error embedding Windows Terminal: {ex.Message}");
                            throw;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Failed to find Windows Terminal window");
                    }
                }
                else
                {
                    // Command Prompt mode (original code path)
                    _wtTabBarHeight = 0;

                    // Build the terminal command based on provider
                    string terminalCommand;
                    switch (provider)
                    {
                        case AiProvider.CursorAgentNative:
                            string cursorAgentCommand = GetCursorAgentCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {cursorAgentCommand}";
                            break;

                        case AiProvider.CursorAgent:
                            string wslPathCursor = ConvertToWslPath(workspaceDir);
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -ic \"cd '{wslPathCursor}' && cursor-agent\"";
                            break;

                        case AiProvider.CodexNative:
                            string codexCommand = GetCodexCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {codexCommand}";
                            break;

                        case AiProvider.Codex:
                            string wslPathCodex = ConvertToWslPath(workspaceDir);
                            string codexWslCommand = GetCodexCommand(isWsl: true);
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -ic \"cd '{wslPathCodex}' && {codexWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            string wslPathClaude = ConvertToWslPath(workspaceDir);
                            string claudeWslCommand = GetClaudeCommand(isWsl: true);
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -ic \"cd '{wslPathClaude}' && {claudeWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCode:
                            string claudeCommand = GetClaudeCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {claudeCommand}";
                            break;

                        case AiProvider.QwenCode:
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && qwen";
                            break;

                        case AiProvider.OpenCode:
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && opencode";
                            break;

                        default: // null or any other value = regular CMD
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\"";
                            break;
                    }

                    // Configure and start the process.
                    // Use conhost.exe explicitly to bypass Windows Terminal delegation.
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "conhost.exe",
                        Arguments = "-- cmd.exe " + terminalCommand,
                        UseShellExecute = false,
                        CreateNoWindow = false,
                        WindowStyle = ProcessWindowStyle.Minimized,
                        WorkingDirectory = workspaceDir
                    };

                    // Refresh PATH from registry
                    string freshPath = GetFreshPathFromRegistry();
                    if (!string.IsNullOrEmpty(freshPath))
                    {
                        startInfo.EnvironmentVariables["PATH"] = freshPath;
                    }

                    // Enable Virtual Terminal Processing
                    startInfo.EnvironmentVariables["VIRTUAL_TERMINAL_LEVEL"] = "1";

                    // Temporarily set console font
                    SaveAndSetConsoleFontRegistry();

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

                    // Find and embed the terminal window
                    var hwnd = await FindMainWindowHandleByConhostAsync(cmdProcess.Id, timeoutMs: 5000, pollIntervalMs: 50);

                    // Restore original console font
                    RestoreConsoleFontRegistry();

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
                                case AiProvider.CursorAgentNative:
                                    providerTitle = "Cursor Agent";
                                    break;
                                case AiProvider.CursorAgent:
                                    providerTitle = "Cursor Agent";
                                    break;
                                case AiProvider.CodexNative:
                                    providerTitle = "Codex";
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

                            // If terminal should be detached, re-parent to detached panel
                            if (_isTerminalDetached && _detachedTerminalPanel != null)
                            {
                                SetParent(terminalHandle, _detachedTerminalPanel.Handle);
                                ShowWindow(terminalHandle, SW_SHOW);
                                ResizeEmbeddedTerminal();
                                _detachedTerminalWindow?.UpdateCaption(providerTitle);
                            }

                            // Replay saved user zoom delta AFTER all re-parenting is done
                            // (SetParent can reset zoom state)
                            if (_settings?.TerminalZoomDelta != 0)
                            {
                                await ApplyTerminalZoomDeltaAsync(_settings.TerminalZoomDelta);
                            }
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
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to start embedded terminal: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Saved original console FaceName from registry, for restoration after conhost starts
        /// </summary>
        private string _savedConsoleFaceName;

        /// <summary>
        /// Saved original console FontFamily from registry, for restoration after conhost starts
        /// </summary>
        private object _savedConsoleFontFamily;

        /// <summary>
        /// Whether we have saved console font values that need restoration
        /// </summary>
        private bool _consoleFontSaved;

        /// <summary>
        /// Temporarily sets the console default font in the registry to "Cascadia Mono".
        /// Conhost reads HKCU\Console when creating a new console window, so setting
        /// the font before starting conhost ensures the correct font is used.
        /// The original values are saved for restoration after conhost has started.
        /// </summary>
        private void SaveAndSetConsoleFontRegistry()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Console", writable: true))
                {
                    if (key == null) return;

                    _savedConsoleFaceName = key.GetValue("FaceName") as string;
                    _savedConsoleFontFamily = key.GetValue("FontFamily");
                    _consoleFontSaved = true;

                    key.SetValue("FaceName", "Cascadia Mono", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("FontFamily", 54, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveAndSetConsoleFontRegistry: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores the original console font registry values saved by SaveAndSetConsoleFontRegistry.
        /// Called after conhost has started and read its font settings from the registry.
        /// </summary>
        private void RestoreConsoleFontRegistry()
        {
            if (!_consoleFontSaved) return;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Console", writable: true))
                {
                    if (key == null) return;

                    if (_savedConsoleFaceName != null)
                        key.SetValue("FaceName", _savedConsoleFaceName, Microsoft.Win32.RegistryValueKind.String);
                    else
                        key.DeleteValue("FaceName", throwOnMissingValue: false);

                    if (_savedConsoleFontFamily != null)
                        key.SetValue("FontFamily", _savedConsoleFontFamily, Microsoft.Win32.RegistryValueKind.DWord);
                    else
                        key.DeleteValue("FontFamily", throwOnMissingValue: false);
                }

                _consoleFontSaved = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestoreConsoleFontRegistry: {ex.Message}");
            }
        }

        #endregion

        #region Terminal Window Management

        /// <summary>
        /// Takes a snapshot of all existing Windows Terminal windows before launching a new one
        /// </summary>
        /// <returns>A set of window handles that exist at snapshot time</returns>
        private System.Collections.Generic.HashSet<IntPtr> SnapshotExistingWtWindows()
        {
            var existing = new System.Collections.Generic.HashSet<IntPtr>();

            EnumWindows((hWnd, lParam) =>
            {
                // Look for Windows Terminal window class: "CASCADIA_HOSTING_WINDOW_CLASS"
                System.Text.StringBuilder className = new System.Text.StringBuilder(256);
                GetClassName(hWnd, className, className.Capacity);

                if (className.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS" && IsWindowVisible(hWnd))
                {
                    existing.Add(hWnd);
                }

                return true;
            }, IntPtr.Zero);

            return existing;
        }

        /// <summary>
        /// Finds a new Windows Terminal window that wasn't in the existing set (with timeout)
        /// </summary>
        private async Task<IntPtr> FindNewWtWindowAsync(System.Collections.Generic.HashSet<IntPtr> existingWindows, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr found = IntPtr.Zero;

                EnumWindows((hWnd, lParam) =>
                {
                    if (existingWindows.Contains(hWnd))
                    {
                        return true;
                    }

                    System.Text.StringBuilder className = new System.Text.StringBuilder(256);
                    GetClassName(hWnd, className, className.Capacity);

                    if (className.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS" && IsWindowVisible(hWnd))
                    {
                        found = hWnd;
                        return false; // Stop enumeration
                    }

                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    return found;
                }

                await Task.Delay(50);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Applies zoom out to Windows Terminal for better visibility
        /// Sends Ctrl+Minus multiple times to zoom out significantly
        /// </summary>
        private async Task ApplyWindowsTerminalZoomOutAsync()
        {
            if (_wtTabBarHeight > 0 && terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                try
                {
                    // Set focus to terminal
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);
                    await Task.Delay(100);

                    // Send Ctrl+Minus 3 times to zoom out for better visibility
                    for (int i = 0; i < 3; i++)
                    {
                        // Send Ctrl+Minus
                        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                        await Task.Delay(50);
                        keybd_event(0xBD, 0, 0, UIntPtr.Zero); // VK_MINUS (0xBD)
                        await Task.Delay(50);
                        keybd_event(0xBD, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        await Task.Delay(50);
                        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error applying zoom to Windows Terminal: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Replays the saved terminal zoom delta.
        /// Windows Terminal: uses keybd_event (Ctrl+= / Ctrl+-) — same proven approach as ApplyWindowsTerminalZoomOutAsync.
        /// Command Prompt: uses PostMessage WM_MOUSEWHEEL+MK_CONTROL — same mechanism as Ctrl+Scroll forwarding.
        /// </summary>
        private async Task ApplyTerminalZoomDeltaAsync(int delta)
        {
            if (delta == 0 || terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                return;

            try
            {
                // Give the terminal extra time to finish initializing before replaying zoom
                // Must wait long enough for the terminal shell to be fully loaded
                await Task.Delay(1500);

                if (!IsWindow(terminalHandle)) return;

                int steps = Math.Abs(delta);

                if (_wtTabBarHeight > 0)
                {
                    // Windows Terminal: use keyboard approach (matches ApplyWindowsTerminalZoomOutAsync)
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);
                    await Task.Delay(100);

                    // 0xBB = VK_OEM_PLUS (zoom in), 0xBD = VK_OEM_MINUS (zoom out)
                    int key = delta > 0 ? 0xBB : 0xBD;

                    for (int i = 0; i < steps; i++)
                    {
                        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                        await Task.Delay(50);
                        keybd_event(key, 0, 0, UIntPtr.Zero);
                        await Task.Delay(50);
                        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        await Task.Delay(50);
                        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        await Task.Delay(100);
                    }
                }
                else
                {
                    // Command Prompt: use PostMessage WM_MOUSEWHEEL+MK_CONTROL
                    // (same mechanism as native Ctrl+Scroll zoom)
                    var panel = ActiveTerminalPanel;
                    if (panel == null) return;

                    var screenPt = panel.PointToScreen(
                        new System.Drawing.Point(panel.Width / 2, panel.Height / 2));
                    int lParam = (screenPt.Y << 16) | (screenPt.X & 0xFFFF);

                    // WHEEL_DELTA = 120 per notch
                    int notch = delta > 0 ? 120 : -120;

                    for (int i = 0; i < steps; i++)
                    {
                        int wParam = (notch << 16) | 0x0008; // HIWORD=delta, LOWORD=MK_CONTROL
                        PostMessage(terminalHandle, WM_MOUSEWHEEL, (IntPtr)wParam, (IntPtr)lParam);
                        await Task.Delay(80);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying terminal zoom delta: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates the Windows Terminal tab bar height scaled by DPI
        /// </summary>
        private int GetWtTabBarHeight()
        {
            if (terminalHandle == IntPtr.Zero)
            {
                return 0;
            }

            uint dpi = GetDpiForWindow(terminalHandle);
            if (dpi == 0)
            {
                dpi = 96;
            }

            // Tab bar is approximately 48 pixels at 96 DPI, scale by actual DPI
            return (int)(48 * dpi / 96.0);
        }

        /// <summary>
        /// Installs a low-level mouse hook to detect Ctrl+Scroll zoom over the terminal.
        /// WPF PreviewMouseWheel doesn't fire for Win32 windows embedded via SetParent
        /// from other processes, so a system-wide hook is needed.
        /// </summary>
        private void InstallMouseHook()
        {
            if (_mouseHookHandle != IntPtr.Zero) return;

            _mouseHookProc = LowLevelMouseHookCallback;
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }

            // Debounce timer: saves settings 500ms after last scroll tick
            _zoomSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _zoomSaveTimer.Tick += (s, e) =>
            {
                _zoomSaveTimer.Stop();
                SaveSettings();
            };
        }

        /// <summary>
        /// Uninstalls the low-level mouse hook
        /// </summary>
        private void UninstallMouseHook()
        {
            ResetWindowsTerminalSelectionTracking();

            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }
            _mouseHookProc = null;
            _zoomSaveTimer?.Stop();
            _zoomSaveTimer = null;
        }

        /// <summary>
        /// Returns true when the supplied screen point is inside the active terminal panel.
        /// </summary>
        private bool IsScreenPointInsideActiveTerminalPanel(POINT screenPoint)
        {
            var panel = ActiveTerminalPanel;
            if (panel == null || panel.IsDisposed || terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return false;
            }

            var screenBounds = panel.RectangleToScreen(panel.ClientRectangle);
            return screenBounds.Contains(screenPoint.x, screenPoint.y);
        }

        /// <summary>
        /// Starts tracking a possible Windows Terminal text-selection drag.
        /// </summary>
        private void BeginWindowsTerminalSelectionTracking(POINT screenPoint)
        {
            if (_wtTabBarHeight <= 0 || !IsScreenPointInsideActiveTerminalPanel(screenPoint))
            {
                return;
            }

            _windowsTerminalSelectionPending = true;
            _windowsTerminalSelectionActive = false;
            _windowsTerminalSelectionStartPoint = screenPoint;
        }

        /// <summary>
        /// Converts a plain left-drag into SHIFT+drag so Windows Terminal enters selection mode
        /// even when the running TUI has mouse reporting enabled.
        /// </summary>
        private void UpdateWindowsTerminalSelectionTracking(POINT screenPoint)
        {
            if (_wtTabBarHeight <= 0 || !_windowsTerminalSelectionPending || _windowsTerminalSelectionActive)
            {
                return;
            }

            int deltaX = Math.Abs(screenPoint.x - _windowsTerminalSelectionStartPoint.x);
            int deltaY = Math.Abs(screenPoint.y - _windowsTerminalSelectionStartPoint.y);
            if (deltaX < WindowsTerminalSelectionDragThreshold &&
                deltaY < WindowsTerminalSelectionDragThreshold)
            {
                return;
            }

            _windowsTerminalSelectionActive = true;

            if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0)
            {
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                _windowsTerminalSelectionModifierInjected = true;
            }
        }

        /// <summary>
        /// Clears the temporary Windows Terminal selection tracking state.
        /// </summary>
        private void ResetWindowsTerminalSelectionTracking()
        {
            if (_windowsTerminalSelectionModifierInjected)
            {
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            _windowsTerminalSelectionPending = false;
            _windowsTerminalSelectionActive = false;
            _windowsTerminalSelectionModifierInjected = false;
        }

        /// <summary>
        /// Low-level mouse hook callback. Tracks Ctrl+Scroll over the terminal panel
        /// to persist the zoom delta for replay on terminal restart and enables SHIFT+drag
        /// selection assistance for embedded Windows Terminal.
        /// </summary>
        private IntPtr LowLevelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                uint message = unchecked((uint)wParam.ToInt64());

                if (message == WM_MOUSEWHEEL)
                {
                    if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 &&
                        IsScreenPointInsideActiveTerminalPanel(info.pt) &&
                        _settings != null)
                    {
                        int wheelDelta = (short)((info.mouseData >> 16) & 0xFFFF);
                        _settings.TerminalZoomDelta += wheelDelta > 0 ? 1 : -1;
                        _zoomSaveTimer?.Stop();
                        _zoomSaveTimer?.Start();
                    }
                }
                else if (message == WM_LBUTTONDOWN)
                {
                    BeginWindowsTerminalSelectionTracking(info.pt);
                }
                else if (message == WM_MOUSEMOVE)
                {
                    UpdateWindowsTerminalSelectionTracking(info.pt);
                }
                else if (message == WM_LBUTTONUP)
                {
                    ResetWindowsTerminalSelectionTracking();
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// Resizes the embedded terminal window to match the panel size
        /// For Windows Terminal, hides the tab bar by positioning it off-screen
        /// </summary>
        private void ResizeEmbeddedTerminal()
        {
            var panel = ActiveTerminalPanel;
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle) && panel != null)
            {
                if (_wtTabBarHeight > 0)
                {
                    // Windows Terminal: hide tab bar by positioning it above the visible area
                    // Set height to panel height + tab bar (so tab bar goes off-screen above)
                    SetWindowPos(terminalHandle, IntPtr.Zero,
                                0, -_wtTabBarHeight, panel.Width, panel.Height + _wtTabBarHeight,
                                SWP_NOZORDER | SWP_NOACTIVATE);
                }
                else
                {
                    // Command Prompt: use panel dimensions directly
                    SetWindowPos(terminalHandle, IntPtr.Zero, 0, 0,
                                panel.Width, panel.Height,
                                SWP_NOZORDER | SWP_NOACTIVATE);
                }
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
        /// Finds the console window handle for a conhost.exe process started with "-- cmd.exe ...".
        /// Searches by both the conhost PID and its cmd.exe child PIDs discovered via ToolHelp32 snapshot,
        /// because GetWindowThreadProcessId returns the console application's PID (cmd.exe) rather than
        /// conhost's PID due to Windows backward compatibility behavior.
        /// ToolHelp32 is a kernel snapshot API (sub-millisecond, no WMI dependency) and is safe to call
        /// on every poll iteration, ensuring child PIDs are found even on slow/busy VS launch paths.
        /// </summary>
        private static async Task<IntPtr> FindMainWindowHandleByConhostAsync(
            int conhostPid, int timeoutMs = 5000, int pollIntervalMs = 50,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var targetPids = new HashSet<uint> { (uint)conhostPid };

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Refresh child PIDs each iteration using ToolHelp32 snapshot (sub-ms, no WMI).
                // GetWindowThreadProcessId returns the console client's PID (cmd.exe), not conhost's
                // PID, due to Windows backward compatibility — so we need the cmd.exe child PID.
                foreach (uint childPid in GetChildProcessIds((uint)conhostPid))
                    targetPids.Add(childPid);

                IntPtr found = IntPtr.Zero;
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (targetPids.Contains(pid))
                    {
                        found = hWnd;
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
        /// Returns the set of direct child process IDs for the given parent PID using a ToolHelp32 snapshot.
        /// This is a kernel-level snapshot API that is sub-millisecond and has no dependency on the WMI service.
        /// </summary>
        private static HashSet<uint> GetChildProcessIds(uint parentPid)
        {
            var result = new HashSet<uint>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == new IntPtr(-1))
                return result;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32First(snap, ref entry))
                {
                    do
                    {
                        if (entry.th32ParentProcessID == parentPid)
                            result.Add(entry.th32ProcessID);
                    }
                    while (Process32Next(snap, ref entry));
                }
            }
            finally
            {
                CloseHandle(snap);
            }
            return result;
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
                    case AiProvider.CodexNative:
                        // Codex Native requires CTRL+C to exit
                        SendCtrlC();
                        await Task.Delay(400);
                        SendCtrlC();
                        await Task.Delay(1000);
                        await SendTextToTerminalAsync("npm install -g @openai/codex@latest");
                        break;

                    case AiProvider.Codex:
                        // Codex WSL requires CTRL+C to exit
                        SendCtrlC();
                        await Task.Delay(400); // Reduced from 500ms
                        SendCtrlC();
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("wsl bash -ic \"npm install -g @openai/codex@latest\"");
                        break;

                    case AiProvider.CursorAgentNative:
                        // CursorAgent Native: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000);
                        await SendTextToTerminalAsync("agent update");
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
        /// Restarts the terminal using the currently selected provider from settings.
        /// Falls back to regular CMD if the provider is unavailable.
        /// </summary>
        private async Task RestartTerminalWithSelectedProviderAsync()
        {
            // Get the selected provider from settings
            AiProvider? selectedProvider = _settings?.SelectedProvider;
            bool providerAvailable = false;

            // Check if the selected provider is available
            switch (selectedProvider)
            {
                case AiProvider.CursorAgentNative:
                    providerAvailable = await IsCursorAgentNativeAvailableAsync();
                    break;

                case AiProvider.CursorAgent:
                    bool wslAvailable = await IsWslInstalledAsync();
                    if (wslAvailable)
                    {
                        providerAvailable = await IsCursorAgentInstalledInWslAsync();
                    }
                    break;

                case AiProvider.CodexNative:
                    providerAvailable = await IsCodexNativeAvailableAsync();
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

        /// <summary>
        /// Handles the restart terminal button click event
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void RestartTerminalButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                await RestartTerminalWithSelectedProviderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in RestartTerminalButton_Click: {ex.Message}");
                MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Converts a Windows path to WSL path format
        /// Examples:
        ///   - C:\GitLab\Project -> /mnt/c/GitLab/Project
        ///   - \\wsl.localhost\Ubuntu\home\user\Project -> /home/user/Project
        ///   - \\wsl$\Ubuntu\home\user\Project -> /home/user/Project
        /// </summary>
        /// <param name="windowsPath">Windows path to convert</param>
        /// <returns>WSL-formatted path</returns>
        private string ConvertToWslPath(string windowsPath)
        {
            if (string.IsNullOrEmpty(windowsPath))
                return string.Empty;

            // Check if this is a WSL UNC path (\\wsl.localhost\<distro>\ or \\wsl$\<distro>\)
            if (windowsPath.StartsWith("\\\\wsl.localhost\\", StringComparison.OrdinalIgnoreCase) ||
                windowsPath.StartsWith("\\\\wsl$\\", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the path after the distro name
                // Format: \\wsl.localhost\<distro>\<actual-linux-path>
                // or:     \\wsl$\<distro>\<actual-linux-path>
                int firstSlash = windowsPath.IndexOf('\\', 2); // Skip the leading \\
                if (firstSlash > 0)
                {
                    int secondSlash = windowsPath.IndexOf('\\', firstSlash + 1); // Find the end of the prefix
                    if (secondSlash > 0)
                    {
                        int thirdSlash = windowsPath.IndexOf('\\', secondSlash + 1); // Find the end of the distro name
                        if (thirdSlash > 0)
                        {
                            // Extract everything after the distro name and convert backslashes to forward slashes
                            string linuxPath = windowsPath.Substring(secondSlash).Replace("\\", "/");
                            return linuxPath;
                        }
                    }
                }
                // If parsing failed, fall through to default behavior
            }

            // Check if this is a regular Windows drive path (e.g., C:\)
            if (windowsPath.Length >= 2 && windowsPath[1] == ':')
            {
                // Get the drive letter and convert to lowercase
                string driveLetter = windowsPath.Substring(0, 1).ToLower();

                // Remove the drive letter and colon, then replace backslashes with forward slashes
                string pathWithoutDrive = windowsPath.Substring(2).Replace("\\", "/");

                // Return the WSL path format
                return $"/mnt/{driveLetter}{pathWithoutDrive}";
            }

            // If it's not a recognized format, just replace backslashes with forward slashes
            return windowsPath.Replace("\\", "/");
        }

        /// <summary>
        /// Gets the appropriate Claude Code command to use for Windows or WSL
        /// Prioritizes native Windows installation (%USERPROFILE%\.local\bin\claude.exe) over NPM installation (claude.cmd)
        /// </summary>
        /// <returns>The claude command to execute</returns>
        private string GetClaudeCommand(bool isWsl = false)
        {
            string baseCommand;

            if (isWsl)
            {
                baseCommand = "claude";
            }
            else
            {
                // Check for native installation first
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string nativeClaudePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");

                if (File.Exists(nativeClaudePath))
                {
                    baseCommand = $"\"{nativeClaudePath}\"";
                }
                else
                {
                    // Fall back to NPM installation
                    baseCommand = "claude.cmd";
                }
            }

            if (_settings?.ClaudeDangerouslySkipPermissions == true)
            {
                return $"{baseCommand} --dangerously-skip-permissions";
            }

            return baseCommand;
        }

        /// <summary>
        /// Gets the appropriate Codex command to use for Windows or WSL
        /// Adds --full-auto flag if enabled in settings
        /// </summary>
        /// <returns>The codex command to execute</returns>
        private string GetCodexCommand(bool isWsl = false)
        {
            string baseCommand = "codex";

            if (_settings?.CodexFullAuto == true)
            {
                return $"{baseCommand} --full-auto";
            }

            return baseCommand;
        }

        /// <summary>
        /// Reads the fresh system and user PATH from the Windows registry
        /// This ensures the terminal has the latest PATH entries even if Visual Studio
        /// was launched before the user modified their PATH
        /// </summary>
        /// <returns>Combined system + user PATH string</returns>
        private static string GetFreshPathFromRegistry()
        {
            string systemPath = "";
            string userPath = "";

            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                {
                    systemPath = key?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                    // Expand any %VARIABLE% references
                    systemPath = Environment.ExpandEnvironmentVariables(systemPath);
                }
            }
            catch { }

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Environment"))
                {
                    userPath = key?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                    userPath = Environment.ExpandEnvironmentVariables(userPath);
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(systemPath) && !string.IsNullOrEmpty(userPath))
            {
                return systemPath.TrimEnd(';') + ";" + userPath.TrimEnd(';');
            }

            return !string.IsNullOrEmpty(systemPath) ? systemPath : userPath;
        }

        /// <summary>
        /// Gets the appropriate Cursor Agent command to use (native Windows)
        /// Prioritizes installation at %LOCALAPPDATA%\cursor-agent\agent.cmd, falls back to agent in PATH
        /// </summary>
        /// <returns>The agent command to execute</returns>
        private string GetCursorAgentCommand()
        {
            // Check for installation at %LOCALAPPDATA%\cursor-agent\agent.cmd
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string nativeAgentPath = Path.Combine(localAppData, "cursor-agent", "agent.cmd");

            if (File.Exists(nativeAgentPath))
            {
                return $"\"{nativeAgentPath}\"";
            }

            // Fall back to PATH installation (agent.cmd)
            return "agent";
        }

        #endregion
    }
}
