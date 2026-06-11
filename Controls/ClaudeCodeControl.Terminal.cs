/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
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
using Microsoft.VisualStudio.Shell.Interop;

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
        /// Session UUID to resume on the next <see cref="StartEmbeddedTerminalAsync"/> for a Claude
        /// Code provider. Consumed once by <see cref="GetClaudeCommand"/> and cleared so subsequent
        /// restarts behave normally. Set by the session-history dialog (Resume button); the special
        /// sentinel "-c" means "continue last session" (claude --continue).
        /// </summary>
        internal string _pendingResumeSessionId = null;

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
        /// Handle to the low-level keyboard hook used for intercepting F5/Ctrl+F5 on the embedded terminal
        /// </summary>
        private IntPtr _keyboardHookHandle = IntPtr.Zero;

        /// <summary>
        /// Prevent GC from collecting the keyboard hook callback delegate
        /// </summary>
        private LowLevelKeyboardProc _keyboardHookProc;

        /// <summary>
        /// Dedicated thread that owns and pumps the low-level keyboard/mouse hooks.
        /// Low-level hook callbacks are delivered on the thread that installed them and that
        /// thread must keep pumping messages, or Windows freezes input system-wide once the
        /// LowLevelHooksTimeout (~300ms) is exceeded. Running them here — instead of on the VS
        /// UI thread — keeps global keyboard/mouse responsive even when the UI thread stalls
        /// (e.g. on a contended clipboard during a large prompt send). See issue #61.
        /// </summary>
        private Thread _hookThread;

        /// <summary>
        /// Win32 thread id of <see cref="_hookThread"/>, used to PostThreadMessage(WM_QUIT) on shutdown.
        /// </summary>
        private uint _hookThreadId;

        /// <summary>
        /// Signaled by the hook thread once both hooks are installed (or installation failed),
        /// so the caller can proceed without racing the first input events.
        /// </summary>
        private ManualResetEventSlim _hookThreadReady;

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

        /// <summary>
        /// Serializes terminal stop/start transitions so provider or host switches cannot overlap.
        /// </summary>
        private readonly SemaphoreSlim _terminalLifecycleSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Monotonic ticket for terminal launch requests. A launch that finds a newer ticket
        /// after acquiring the lifecycle lock has been superseded and skips itself.
        /// </summary>
        private int _terminalLaunchTicket;

        /// <summary>
        /// Monotonic session identifier used to discard deferred startup work from stale terminal instances.
        /// </summary>
        private int _terminalStartupSessionId = 0;

        /// <summary>
        /// Monotonic request identifier used to debounce repaint passes after manual Ctrl+Scroll zoom.
        /// </summary>
        private int _manualZoomRefreshRequestId = 0;

        /// <summary>
        /// Enables automatic terminal zoom behavior after startup has settled.
        /// Manual Ctrl+Scroll zoom remains available regardless of this setting.
        /// </summary>
        private static readonly bool EnableStartupTerminalAutoZoom = true;

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
                bool useOpenCode = _settings?.SelectedProvider == AiProvider.OpenCode;
                bool useWindsurf = _settings?.SelectedProvider == AiProvider.Windsurf;
                bool usePi = _settings?.SelectedProvider == AiProvider.Pi;
                bool useAntigravity = _settings?.SelectedProvider == AiProvider.Antigravity;
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
                else if (useOpenCode)
                {
                    providerAvailable = await IsOpenCodeAvailableAsync();
                }
                else if (useWindsurf)
                {
                    bool wslInstalled = await IsWslInstalledAsync();
                    if (wslInstalled)
                    {
                        providerAvailable = await IsWindsurfAvailableAsync();
                    }
                }
                else if (usePi)
                {
                    providerAvailable = await IsPiAvailableAsync();
                }
                else if (useAntigravity)
                {
                    providerAvailable = await IsAntigravityAvailableAsync();
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

                // Install low-level keyboard hook to intercept F5/Ctrl+F5 on the embedded terminal
                // and forward them as VS debug commands (Start Debugging / Start Without Debugging)
                InstallKeyboardHook();

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
                else if (useWindsurf)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.Windsurf);
                    }
                    else
                    {
                        if (!_windsurfNotificationShown)
                        {
                            _windsurfNotificationShown = true;
                            ShowWindsurfInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (usePi)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.Pi);
                    }
                    else
                    {
                        if (!_piNotificationShown)
                        {
                            _piNotificationShown = true;
                            ShowPiInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }
                else if (useAntigravity)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.Antigravity);
                    }
                    else
                    {
                        if (!_antigravityNotificationShown)
                        {
                            _antigravityNotificationShown = true;
                            ShowAntigravityInstallationInstructions();
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
        /// Stops the currently embedded terminal, including stale window owners left behind by wt.exe delegation.
        /// </summary>
        private async Task StopExistingTerminalAsync()
        {
            IntPtr existingTerminalHandle = terminalHandle;
            Process existingProcess = cmdProcess;
            int existingTerminalWindowProcessId = 0;
            bool isWindowsTerminal = false;
            bool wasWslProvider = _currentRunningProvider == AiProvider.ClaudeCodeWSL ||
                                  _currentRunningProvider == AiProvider.Codex ||
                                  _currentRunningProvider == AiProvider.CursorAgent ||
                                  _currentRunningProvider == AiProvider.Windsurf;

            if (existingTerminalHandle != IntPtr.Zero && IsWindow(existingTerminalHandle))
            {
                GetWindowThreadProcessId(existingTerminalHandle, out uint existingTerminalWindowPid);
                existingTerminalWindowProcessId = (int)existingTerminalWindowPid;
                isWindowsTerminal = IsWindowsTerminalProcess(existingTerminalWindowProcessId);
            }

            if (existingTerminalHandle != IntPtr.Zero && IsWindow(existingTerminalHandle))
            {
                LogTerminalLaunch($"stopping existing terminal: hwnd=0x{existingTerminalHandle.ToInt64():X}, windowPid={existingTerminalWindowProcessId}, isWindowsTerminal={isWindowsTerminal}");
                PostMessage(existingTerminalHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                // Windows Terminal needs more time to close the window and clean up
                // its child processes (cmd.exe, claude, etc.) via CTRL_CLOSE_EVENT
                await Task.Delay(isWindowsTerminal ? 500 : 250);
            }

            var terminatedProcessIds = new HashSet<int>();

            // Skip killing the WindowsTerminal.exe process tree — it is a shared host
            // for ALL WT windows (across VS instances). WM_CLOSE above closes only our
            // specific window and lets WT terminate its child console processes.
            if (existingTerminalWindowProcessId > 0 &&
                existingTerminalWindowProcessId != Process.GetCurrentProcess().Id &&
                !isWindowsTerminal)
            {
                TryTerminateProcessTree(existingTerminalWindowProcessId, terminatedProcessIds);
            }

            if (existingProcess != null)
            {
                try
                {
                    int existingProcessId = 0;
                    try
                    {
                        existingProcessId = existingProcess.Id;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited
                    }

                    // Skip killing the launcher tree when it resolves to the shared
                    // WindowsTerminal.exe host. On Windows 11, the wt.exe App Execution
                    // Alias can activate the MSIX package such that the launched Process
                    // maps directly to WindowsTerminal.exe, and tearing down that tree
                    // would destroy unrelated WT windows (e.g. separate terminals the
                    // user opened manually).
                    if (existingProcessId > 0 && !IsWindowsTerminalProcess(existingProcessId))
                    {
                        TryTerminateProcessTree(existingProcessId, terminatedProcessIds);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error terminating previous terminal launcher: {ex.Message}");
                }
                finally
                {
                    existingProcess.Dispose();
                }
            }

            cmdProcess = null;
            ResetWindowsTerminalSelectionTracking();
            terminalHandle = IntPtr.Zero;
            _wtTabBarHeight = 0;
            _currentRunningProvider = null;

            // Relaunching while a just-killed `wsl bash` session is still tearing down is what
            // makes the fresh conhost exit instantly or lose its window mid-embed (issue #73).
            // Wait (bounded) for the killed processes to actually disappear before the caller
            // spawns the next terminal, so most launches never enter that contention window.
            if (wasWslProvider && terminatedProcessIds.Count > 0)
            {
                var teardownTimer = Stopwatch.StartNew();
                while (teardownTimer.ElapsedMilliseconds < 3000 && AnyProcessStillRunning(terminatedProcessIds))
                {
                    await Task.Delay(100);
                }

                if (teardownTimer.ElapsedMilliseconds >= 100)
                {
                    LogTerminalLaunch($"waited {teardownTimer.ElapsedMilliseconds}ms for WSL terminal teardown" +
                                      (AnyProcessStillRunning(terminatedProcessIds) ? " (old processes still running — proceeding anyway)" : ""));
                }
            }
        }

        /// <summary>
        /// True when any of the given process IDs still maps to a running process.
        /// </summary>
        private static bool AnyProcessStillRunning(HashSet<int> processIds)
        {
            foreach (int pid in processIds)
            {
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        if (!process.HasExited)
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // Process is gone (or unreadable) — treat as exited.
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a given process ID belongs to the Windows Terminal host (WindowsTerminal.exe).
        /// The WT host is a shared, single-instance process — killing it would destroy ALL WT windows
        /// system-wide, including those embedded by other VS instances.
        /// </summary>
        private static bool IsWindowsTerminalProcess(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return string.Equals(process.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kills a process tree once, ignoring already-terminated processes.
        /// </summary>
        /// <param name="processId">Root process ID to terminate</param>
        /// <param name="terminatedProcessIds">Process IDs already handled in the current shutdown pass</param>
        private void TryTerminateProcessTree(int processId, HashSet<int> terminatedProcessIds)
        {
            if (processId <= 0 ||
                processId == Process.GetCurrentProcess().Id ||
                terminatedProcessIds.Contains(processId))
            {
                return;
            }

            try
            {
                terminatedProcessIds.Add(processId);
                KillProcessAndChildren(processId);

                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error terminating process tree {processId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the window styles required for the embedded terminal host.
        /// </summary>
        /// <param name="forceChildWindowStyle">
        /// True for hosts that behave well as WS_CHILD windows.
        /// False for classic conhost/cmd.exe, which loses input/focus when forced into child style.
        /// </param>
        private void ApplyEmbeddedTerminalWindowStyle(bool forceChildWindowStyle)
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return;
            }

            int style = GetWindowLong(terminalHandle, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU);

            if (forceChildWindowStyle)
            {
                style &= ~WS_POPUP;
                style |= WS_CHILD;
            }

            SetWindowLong(terminalHandle, GWL_STYLE, style);

            // Remove from taskbar: clear WS_EX_APPWINDOW and add WS_EX_TOOLWINDOW
            int exStyle = GetWindowLong(terminalHandle, GWL_EXSTYLE);
            exStyle &= ~WS_EX_APPWINDOW;
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(terminalHandle, GWL_EXSTYLE, exStyle);
        }

        /// <summary>
        /// Forces the embedded terminal and its host panel to repaint after layout changes.
        /// </summary>
        private void RefreshEmbeddedTerminalWindow()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return;
            }

            var panel = ActiveTerminalPanel;
            panel?.Invalidate();
            panel?.Update();

            InvalidateRect(terminalHandle, IntPtr.Zero, true);
            RedrawWindow(terminalHandle, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            UpdateWindow(terminalHandle);
        }

        /// <summary>
        /// Returns true when the current embedded terminal still matches the deferred startup operation.
        /// </summary>
        private bool IsCurrentTerminalSession(int expectedSessionId, IntPtr expectedHandle)
        {
            return expectedSessionId == _terminalStartupSessionId &&
                   expectedHandle != IntPtr.Zero &&
                   terminalHandle == expectedHandle &&
                   IsWindow(expectedHandle);
        }

        /// <summary>
        /// Runs a few delayed resize passes so the embedded terminal catches up after startup or zoom changes.
        /// </summary>
        private async Task StabilizeEmbeddedTerminalLayoutAsync(int expectedSessionId, IntPtr expectedHandle)
        {
            int[] delays = { 120, 250, 500 };

            foreach (int delayMs in delays)
            {
                await Task.Delay(delayMs);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!IsCurrentTerminalSession(expectedSessionId, expectedHandle))
                {
                    return;
                }

                ResizeEmbeddedTerminal();
            }
        }

        /// <summary>
        /// Schedules delayed resize/repaint passes after a solution load or close event.
        /// VS re-layouts tool windows during solution transitions, which can leave the
        /// embedded terminal visually corrupted or incorrectly sized.
        /// </summary>
        private void SchedulePostSolutionLoadTerminalRefresh()
        {
            IntPtr expectedHandle = terminalHandle;

#pragma warning disable VSSDK007 // fire-and-forget is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                int[] delays = { 200, 500, 1000 };

                try
                {
                    foreach (int delayMs in delays)
                    {
                        await Task.Delay(delayMs);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                        if (expectedHandle != terminalHandle ||
                            terminalHandle == IntPtr.Zero ||
                            !IsWindow(terminalHandle))
                        {
                            return;
                        }

                        ResizeEmbeddedTerminal();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error refreshing terminal after solution load: {ex.Message}");
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Schedules a few delayed repaint/layout passes after manual Ctrl+Scroll zoom.
        /// Conhost and Windows Terminal both apply font zoom asynchronously, so an immediate resize
        /// is often too early and leaves stale black regions until another user action occurs.
        /// </summary>
        private void ScheduleManualZoomRefresh()
        {
            int requestId = Interlocked.Increment(ref _manualZoomRefreshRequestId);

#pragma warning disable VSSDK007 // fire-and-forget is intentional to keep wheel handling responsive
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                int[] delays = { 80, 180, 360 };

                try
                {
                    foreach (int delayMs in delays)
                    {
                        await Task.Delay(delayMs);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                        if (requestId != _manualZoomRefreshRequestId ||
                            terminalHandle == IntPtr.Zero ||
                            !IsWindow(terminalHandle))
                        {
                            return;
                        }

                        ResizeEmbeddedTerminal();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error refreshing terminal after manual zoom: {ex.Message}");
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Schedules startup-only terminal adjustments after the terminal host has fully settled.
        /// This keeps initial tool window load responsive while still restoring the expected zoom.
        /// </summary>
        private void SchedulePostStartupTerminalAdjustments()
        {
            int expectedSessionId = Interlocked.Increment(ref _terminalStartupSessionId);
            IntPtr expectedHandle = terminalHandle;

#pragma warning disable VSSDK007 // fire-and-forget is intentional to keep terminal startup responsive
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await StabilizeEmbeddedTerminalLayoutAsync(expectedSessionId, expectedHandle);

                    if (!EnableStartupTerminalAutoZoom || _settings?.DisableStartupAutoZoom == true)
                    {
                        return;
                    }

                    await Task.Delay(1200);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (!IsCurrentTerminalSession(expectedSessionId, expectedHandle))
                    {
                        return;
                    }

                    // Batch the WT zoom-out and the saved-delta replay into a single
                    // focus+SendInput pass so the synthesized keystrokes block input
                    // for tens of milliseconds rather than the better part of a second.
                    int defaultWtZoomOutSteps = _wtTabBarHeight > 0 ? 3 : 0;
                    int savedDelta = _settings?.TerminalZoomDelta ?? 0;
                    int netDelta = savedDelta - defaultWtZoomOutSteps;

                    if (netDelta != 0)
                    {
                        await ApplyTerminalZoomDeltaAsync(netDelta, initialDelayMs: 0);
                    }

                    if (!IsCurrentTerminalSession(expectedSessionId, expectedHandle))
                    {
                        return;
                    }

                    ResizeEmbeddedTerminal();
                    await Task.Delay(250);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (!IsCurrentTerminalSession(expectedSessionId, expectedHandle))
                    {
                        return;
                    }

                    ResizeEmbeddedTerminal();

                    // Return focus to the prompt input after all zoom adjustments are done
                    PromptTextBox?.Focus();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error applying deferred startup terminal adjustments: {ex.Message}");
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Starts and embeds the terminal process (Claude Code, Claude Code WSL, Codex, Cursor Agent, or regular CMD)
        /// </summary>
        /// <param name="provider">The AI provider to start (null for regular CMD)</param>
        private async Task StartEmbeddedTerminalAsync(AiProvider? provider)
        {
            bool terminalLifecycleLockHeld = false;
            int launchTicket = System.Threading.Interlocked.Increment(ref _terminalLaunchTicket);

            try
            {
                await _terminalLifecycleSemaphore.WaitAsync();
                terminalLifecycleLockHeld = true;

                // While this request waited for the lifecycle lock, a newer launch request was
                // queued behind it. Starting a terminal here would only have the queued request
                // immediately tear it down and relaunch — the WSL stop/start churn behind the
                // blank-panel failures (issue #73). Let the newest request do the work.
                if (launchTicket != System.Threading.Volatile.Read(ref _terminalLaunchTicket))
                {
                    LogTerminalLaunch($"skipping superseded launch request: provider={(provider?.ToString() ?? "CMD")}");
                    return;
                }

                string workspaceDir = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrEmpty(workspaceDir))
                {
                    workspaceDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                _lastWorkspaceDirectory = workspaceDir;

                // Stop the agent-finish watcher and clear any stale notification before tearing
                // the old terminal down. Its 1-second console-attach tick can otherwise overlap
                // the teardown/relaunch below and leave VS attached to the dying console — the
                // new conhost then fails to create its window and the panel comes up blank
                // (issue #73: "Restart code agent" showing nothing until VS is reopened).
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ResetAgentCompletionWatcher();

                await StopExistingTerminalAsync();

                // Defensively detach VS from any console the agent-finish watcher may have left
                // attached. A lingering attachment makes the conhost we spawn below fail to create
                // its own window, leaving the embedded terminal blank (notably after switching
                // solutions). No-op when VS has no console attached.
                EnsureNoConsoleAttached();

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
                            string cursorAgentWslCommand = GetCursorAgentWslCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathCursor}' && {cursorAgentWslCommand}\"";
                            break;

                        case AiProvider.CodexNative:
                            string codexCommand = GetCodexCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {codexCommand}";
                            break;

                        case AiProvider.Codex:
                            string wslPathCodex = ConvertToWslPath(workspaceDir);
                            string codexWslCommand = GetCodexCommand(isWsl: true);
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathCodex}' && {codexWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            string wslPathClaude = ConvertToWslPath(workspaceDir);
                            string claudeWslCommand = GetClaudeCommand(isWsl: true);
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathClaude}' && {claudeWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCode:
                            string claudeCommand = GetClaudeCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {claudeCommand}";
                            break;

                        case AiProvider.OpenCode:
                            string openCodeCommand = ResolveProviderExecutable(AiProvider.OpenCode, "opencode");
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {openCodeCommand}";
                            break;

                        case AiProvider.Windsurf:
                            string wslPathWindsurf = ConvertToWslPath(workspaceDir);
                            string windsurfWslCommand = GetWindsurfCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathWindsurf}' && {windsurfWslCommand}\"";
                            break;

                        case AiProvider.Pi:
                            string piCommand = ResolveProviderExecutable(AiProvider.Pi, "pi");
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {piCommand}";
                            break;

                        case AiProvider.Antigravity:
                            string antigravityCommand = GetAntigravityCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {antigravityCommand}";
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

                    LogTerminalLaunch($"Launching Windows Terminal: provider={(provider?.ToString() ?? "CMD")}, workspace={workspaceDir}");

                    await Task.Run(() =>
                    {
                        try
                        {
                            cmdProcess = new Process { StartInfo = wtStartInfo };
                            cmdProcess.Start();
                            LogTerminalLaunch($"wt.exe spawned: pid={cmdProcess.Id}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error starting Windows Terminal process: {ex.Message}");
                            LogTerminalLaunch($"wt.exe spawn FAILED: {ex.Message}");
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
                        if (!EnsureTerminalPanelReady())
                        {
                            LogTerminalLaunch("ABORT: terminal panel not ready — WT launch abandoned");
                            return;
                        }

                        try
                        {
                            // Hide the window immediately to prevent blinking
                            ShowWindow(terminalHandle, SW_HIDE);

                            ApplyEmbeddedTerminalWindowStyle(forceChildWindowStyle: true);

                            // Embed the window with retry — SetParent can fail transiently
                            // on busy systems or when the window is not yet ready. SetParent
                            // returning zero is ambiguous (a top-level window's previous parent
                            // is also zero), so confirm via GetParent before declaring failure.
                            bool wtEmbedSucceeded = false;
                            for (int spAttempt = 1; spAttempt <= 3; spAttempt++)
                            {
                                IntPtr wtPrevParent = SetParent(terminalHandle, terminalPanel.Handle);
                                int err = Marshal.GetLastWin32Error();
                                if (wtPrevParent != IntPtr.Zero || GetParent(terminalHandle) == terminalPanel.Handle)
                                {
                                    wtEmbedSucceeded = true;
                                    break;
                                }

                                Debug.WriteLine($"SetParent failed for WT (attempt {spAttempt}/3, win32 error {err}) -- retrying after 200ms");
                                LogTerminalLaunch($"SetParent failed for WT (attempt {spAttempt}/3, win32 error {err})");
                                await Task.Delay(200);
                                ApplyEmbeddedTerminalWindowStyle(forceChildWindowStyle: true);
                            }

                            if (!wtEmbedSucceeded)
                            {
                                // Don't continue as if embedded — that leaves a hidden, parentless
                                // window and a blank panel (issue #73). The next restart closes
                                // the orphan via terminalHandle.
                                LogTerminalLaunch("FAILED: SetParent never succeeded for WT — panel left blank");
                                MessageBox.Show(
                                    "The terminal started but could not be attached to the panel.\n\n" +
                                    "Please try \"Restart code agent\" again. If the problem persists, report it with the log file:\n" +
                                    TerminalLaunchLogPath,
                                    "Claude Code Extension", MessageBoxButton.OK, MessageBoxImage.Warning);
                                return;
                            }

                            LogTerminalLaunch($"embedded OK (WT): hwnd=0x{terminalHandle.ToInt64():X}");

                            // Calculate tab bar height
                            _wtTabBarHeight = GetWtTabBarHeight();

                            // Show and resize
                            ShowWindow(terminalHandle, SW_SHOW);
                            ResizeEmbeddedTerminal();

                            // Track the currently running provider
                            _currentRunningProvider = provider;

                            string wtProviderTitle = GetExtensionTitle(provider);
                            UpdateToolWindowTitle(wtProviderTitle);

                            // If terminal should be detached, re-parent to detached panel
                            if (_isTerminalDetached && _detachedTerminalPanel != null)
                            {
                                SetParent(terminalHandle, _detachedTerminalPanel.Handle);
                                ShowWindow(terminalHandle, SW_SHOW);
                                ResizeEmbeddedTerminal();
                                _detachedTerminalWindow?.UpdateCaption(wtProviderTitle);
                            }

                            // Snapshot the color the agent just launched with --
                            // theme-change prompts compare against this.
                            RecordTerminalAgentColor();

                            SchedulePostStartupTerminalAdjustments();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error embedding Windows Terminal: {ex.Message}");
                            throw;
                        }
                    }
                    else
                    {
                        LogTerminalLaunch("FAILED: no Windows Terminal window found to embed — panel left blank");
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
                            string cursorAgentWslCommand = GetCursorAgentWslCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathCursor}' && {cursorAgentWslCommand}\"";
                            break;

                        case AiProvider.CodexNative:
                            string codexCommand = GetCodexCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {codexCommand}";
                            break;

                        case AiProvider.Codex:
                            string wslPathCodex = ConvertToWslPath(workspaceDir);
                            string codexWslCommand = GetCodexCommand(isWsl: true);
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathCodex}' && {codexWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            string wslPathClaude = ConvertToWslPath(workspaceDir);
                            string claudeWslCommand = GetClaudeCommand(isWsl: true);
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathClaude}' && {claudeWslCommand}\"";
                            break;

                        case AiProvider.ClaudeCode:
                            string claudeCommand = GetClaudeCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {claudeCommand}";
                            break;

                        case AiProvider.OpenCode:
                            string openCodeTerminalCommand = ResolveProviderExecutable(AiProvider.OpenCode, "opencode");
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {openCodeTerminalCommand}";
                            break;

                        case AiProvider.Windsurf:
                            string wslPathWindsurf = ConvertToWslPath(workspaceDir);
                            string windsurfCmdCommand = GetWindsurfCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cls && wsl bash -lic \"cd '{wslPathWindsurf}' && {windsurfCmdCommand}\"";
                            break;

                        case AiProvider.Pi:
                            string piTerminalCommand = ResolveProviderExecutable(AiProvider.Pi, "pi");
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {piTerminalCommand}";
                            break;

                        case AiProvider.Antigravity:
                            string antigravityTerminalCommand = GetAntigravityCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {antigravityTerminalCommand}";
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

                    // Temporarily set console colors based on VS theme and font
                    // (must be on UI thread to access GetTerminalBackgroundColor)
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SaveAndSetConsoleColorsRegistry();
                    SaveAndSetConsoleFontRegistry();

                    LogTerminalLaunch($"Launching conhost terminal: provider={(provider?.ToString() ?? "CMD")}, workspace={workspaceDir}");

                    // The freshly-launched conhost/cmd can die within a few hundred ms when WSL is
                    // still tearing down a just-stopped session (issue #73): the console window
                    // either never appears ("exited immediately, exitCode=0") or dies right after, so
                    // SetParent then hits a dead window (win32 error 1400). Both clear on a retry a
                    // moment later, so relaunch the whole spawn->find->embed unit a few times with
                    // growing backoff before giving up and leaving the panel blank.
                    bool embedded = false;
                    bool superseded = false;
                    string lastFailureReason = "no console window to embed";
                    int[] relaunchBackoffMs = { 0, 750, 1500, 3000, 6000 };
                    for (int relaunch = 0; relaunch < relaunchBackoffMs.Length && !embedded; relaunch++)
                    {
                        if (relaunch > 0)
                        {
                            // A newer launch request queued up while this one was retrying —
                            // stop burning backoff time on a doomed session and let it run.
                            if (launchTicket != System.Threading.Volatile.Read(ref _terminalLaunchTicket))
                            {
                                superseded = true;
                                LogTerminalLaunch($"abandoning relaunch retries: superseded by a newer launch request (relaunch {relaunch + 1}/{relaunchBackoffMs.Length})");
                                break;
                            }

                            // Clean up the orphan window/process from the failed attempt so retries
                            // don't pile up live conhosts, then back off to let WSL teardown settle.
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            await StopExistingTerminalAsync();
                            LogTerminalLaunch($"relaunching after embed failure ({lastFailureReason}); relaunch {relaunch + 1}/{relaunchBackoffMs.Length} after {relaunchBackoffMs[relaunch]}ms backoff");
                            await Task.Delay(relaunchBackoffMs[relaunch]);
                        }

                        // Spawn the terminal and locate its console window. If the conhost dies right
                        // after launch (a console window flashes for a split second, closes itself,
                        // and the panel stays blank), re-spawn once before deferring to the relaunch.
                        IntPtr hwnd = IntPtr.Zero;
                        for (int spawnAttempt = 1; spawnAttempt <= 2 && hwnd == IntPtr.Zero; spawnAttempt++)
                        {
                            await Task.Run(() =>
                            {
                                // Serialize the spawn against the agent-finish console capture and detach
                                // VS from any console right before CreateProcess. A child spawned while VS
                                // is attached to a console inherits it instead of creating its own window,
                                // which leaves the embedded terminal permanently blank (issue #73). The
                                // bounded acquire keeps a pathological hung capture from blocking the
                                // launch forever — FreeConsole still runs so the spawn never inherits.
                                bool consoleLockTaken = false;
                                try
                                {
                                    Monitor.TryEnter(_consoleSnapshotLock, 5000, ref consoleLockTaken);
                                    bool vsHadConsole = GetConsoleWindow() != IntPtr.Zero;
                                    try { FreeConsole(); } catch { }

                                    cmdProcess = new Process { StartInfo = startInfo };
                                    cmdProcess.Start();
                                    LogTerminalLaunch($"conhost spawned: pid={cmdProcess.Id}, attempt={spawnAttempt}/2, vsHadConsoleAttached={vsHadConsole}, spawnLockTaken={consoleLockTaken}");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Error starting process: {ex.Message}");
                                    LogTerminalLaunch($"conhost spawn FAILED: {ex.Message}");
                                    throw;
                                }
                                finally
                                {
                                    if (consoleLockTaken) Monitor.Exit(_consoleSnapshotLock);
                                }
                            });

                            if (cmdProcess == null)
                            {
                                throw new InvalidOperationException("Failed to create terminal process");
                            }

                            // Find the terminal window with retry -- on busy systems the first
                            // attempt may time out before cmd.exe creates its window, leaving a floating
                            // external window. Retry with a longer timeout before giving up.
                            int[] findTimeouts = { 5000, 10000 }; // 5s, then 10s retry
                            for (int findAttempt = 0; findAttempt < findTimeouts.Length; findAttempt++)
                            {
                                hwnd = await FindMainWindowHandleByConhostAsync(cmdProcess.Id, timeoutMs: findTimeouts[findAttempt], pollIntervalMs: 50, launcherProcess: cmdProcess);
                                if (hwnd != IntPtr.Zero)
                                    break;
                                if (HasTerminalProcessExited(cmdProcess))
                                    break; // dead launcher — no point polling for its window
                                Debug.WriteLine($"FindMainWindowHandleByConhostAsync attempt {findAttempt + 1} timed out after {findTimeouts[findAttempt]}ms, retrying...");
                                LogTerminalLaunch($"console window not found after {findTimeouts[findAttempt]}ms (find attempt {findAttempt + 1}), process still alive");
                            }

                            if (hwnd == IntPtr.Zero)
                            {
                                if (!HasTerminalProcessExited(cmdProcess))
                                {
                                    // The launcher is alive but its window was never found — do not
                                    // re-spawn (that would pile up a second live terminal process).
                                    LogTerminalLaunch("giving up: conhost is running but its console window was never found");
                                    break;
                                }

                                int exitCode = 0;
                                try { exitCode = cmdProcess.ExitCode; } catch { }
                                LogTerminalLaunch($"conhost exited immediately after launch: exitCode={exitCode}, attempt={spawnAttempt}/2");
                                try { cmdProcess.Dispose(); } catch { }
                                cmdProcess = null;
                                if (spawnAttempt < 2)
                                {
                                    await Task.Delay(500);
                                }
                            }
                        }

                        terminalHandle = hwnd;

                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                        if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                        {
                            // No window to embed this attempt — relaunch (or report after the loop).
                            lastFailureReason = "no console window to embed";
                            LogTerminalLaunch($"no console window to embed (relaunch {relaunch + 1}/{relaunchBackoffMs.Length})");
                            continue;
                        }

                        if (!EnsureTerminalPanelReady())
                        {
                            LogTerminalLaunch("ABORT: terminal panel not ready — launch abandoned");
                            RestoreConsoleFontRegistry();
                            RestoreConsoleColorsRegistry();
                            return;
                        }

                        try
                        {
                            // Hide the window immediately to prevent blinking
                            ShowWindow(terminalHandle, SW_HIDE);

                            ApplyEmbeddedTerminalWindowStyle(forceChildWindowStyle: false);

                            // Embed the window with retry — SetParent can fail transiently
                            // on busy systems or when the window is not yet ready. SetParent
                            // returning zero is ambiguous (a top-level window's previous parent
                            // is also zero), so confirm via GetParent before declaring failure.
                            bool embedSucceeded = false;
                            bool windowDied = false;
                            for (int spAttempt = 1; spAttempt <= 3; spAttempt++)
                            {
                                // The conhost can die mid-embed when WSL is tearing down (issue #73);
                                // SetParent then fails with win32 error 1400 on a dead window. Detect
                                // the corpse up front so we relaunch at once instead of burning 600ms
                                // retrying SetParent against a window that will never come back.
                                if (!IsWindow(terminalHandle) || HasTerminalProcessExited(cmdProcess))
                                {
                                    windowDied = true;
                                    LogTerminalLaunch($"console window/process died before embed (relaunch {relaunch + 1}/{relaunchBackoffMs.Length})");
                                    break;
                                }

                                IntPtr prevParent = SetParent(terminalHandle, terminalPanel.Handle);
                                int err = Marshal.GetLastWin32Error();
                                if (prevParent != IntPtr.Zero || GetParent(terminalHandle) == terminalPanel.Handle)
                                {
                                    embedSucceeded = true;
                                    break;
                                }

                                Debug.WriteLine($"SetParent failed (attempt {spAttempt}/3, win32 error {err}) -- retrying after 200ms");
                                LogTerminalLaunch($"SetParent failed (attempt {spAttempt}/3, win32 error {err})");
                                await Task.Delay(200);
                                ApplyEmbeddedTerminalWindowStyle(forceChildWindowStyle: false);
                            }

                            if (!embedSucceeded)
                            {
                                // Don't claim success and show a hidden, parentless window — that is
                                // the blank panel with the agent running invisibly (issue #73). Defer
                                // to the relaunch loop; the orphan is cleaned up before the next try.
                                lastFailureReason = windowDied ? "console window died before embed" : "SetParent never succeeded";
                                continue;
                            }

                            LogTerminalLaunch($"embedded OK: hwnd=0x{terminalHandle.ToInt64():X}");

                            // Now show it in the embedded context
                            ShowWindow(terminalHandle, SW_SHOW);
                            ResizeEmbeddedTerminal();

                            // Track the currently running provider
                            _currentRunningProvider = provider;

                            string providerTitle = GetExtensionTitle(provider);
                            UpdateToolWindowTitle(providerTitle);

                            // If terminal should be detached, re-parent to detached panel
                            if (_isTerminalDetached && _detachedTerminalPanel != null)
                            {
                                SetParent(terminalHandle, _detachedTerminalPanel.Handle);
                                ShowWindow(terminalHandle, SW_SHOW);
                                ResizeEmbeddedTerminal();
                                _detachedTerminalWindow?.UpdateCaption(providerTitle);
                            }

                            // Snapshot the color the agent just launched with --
                            // theme-change prompts compare against this.
                            RecordTerminalAgentColor();

                            SchedulePostStartupTerminalAdjustments();
                            embedded = true;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error embedding terminal window: {ex.Message}");
                            throw;
                        }
                    }

                    // Restore original console font and colors. These are kept set across all
                    // relaunch attempts (saved once before the loop) so every conhost picks up the
                    // themed colors at startup — re-saving per attempt would clobber the originals.
                    RestoreConsoleFontRegistry();
                    RestoreConsoleColorsRegistry();

                    if (!embedded && !superseded)
                    {
                        // Every relaunch attempt left the panel blank (issue #73). Tell the user
                        // where the launch log is so the failure can be reported with details.
                        Debug.WriteLine("Could not embed CMD window after relaunch attempts. Terminal may not be available.");
                        LogTerminalLaunch("FAILED: panel left blank after all relaunch attempts — " + lastFailureReason);
                        MessageBox.Show(
                            "The terminal could not be attached to the panel.\n\n" +
                            "Please try \"Restart code agent\" again. If the problem persists, report it with the log file:\n" +
                            TerminalLaunchLogPath,
                            "Claude Code Extension", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                LogTerminalLaunch($"FAILED with exception: {ex.Message}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to start embedded terminal: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (terminalLifecycleLockHeld)
                {
                    _terminalLifecycleSemaphore.Release();
                }
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
        /// Saved original console CodePage from registry, for restoration after conhost starts.
        /// Stored in the conhost.exe-specific subkey (HKCU\Console\%SystemRoot%_System32_conhost.exe),
        /// because the parent HKCU\Console CodePage value is ignored in practice.
        /// </summary>
        private object _savedConsoleCodePage;

        /// <summary>
        /// Whether the conhost.exe-specific subkey existed before we touched it.
        /// If false, we created it ourselves and will delete it entirely on restore.
        /// </summary>
        private bool _cmdConhostSubkeyExisted;

        /// <summary>
        /// Registry subkey path conhost.exe reads for its per-executable console settings
        /// (overrides values at the parent HKCU\Console level).
        /// </summary>
        private const string ConsoleConhostSubkeyPath = @"Console\%SystemRoot%_System32_conhost.exe";

        /// <summary>
        /// Whether we have saved console font values that need restoration
        /// </summary>
        private bool _consoleFontSaved;

        /// <summary>
        /// Saved original console ScreenColors from registry, for restoration after conhost starts
        /// </summary>
        private object _savedConsoleScreenColors;

        /// <summary>
        /// Saved original console PopupColors from registry, for restoration after conhost starts
        /// </summary>
        private object _savedConsolePopupColors;

        /// <summary>
        /// Saved original console ColorTable00 (black) from registry
        /// </summary>
        private object _savedConsoleColorTable00;

        /// <summary>
        /// Saved original console ColorTable07 (white) from registry
        /// </summary>
        private object _savedConsoleColorTable07;

        /// <summary>
        /// Whether we have saved console color values that need restoration
        /// </summary>
        private bool _consoleColorsSaved;

        /// <summary>
        /// Temporarily sets the console default font in the registry to "Cascadia Mono".
        /// Conhost reads HKCU\Console when creating a new console window, so setting
        /// the font before starting conhost ensures the correct font is used.
        /// CodePage must be written to the conhost.exe-specific subkey
        /// (HKCU\Console\%SystemRoot%_System32_conhost.exe) because the parent
        /// HKCU\Console CodePage value is ignored in practice.
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

                // CodePage lives in the per-executable conhost.exe subkey; the parent Console\CodePage is ignored by conhost.exe.
                using (var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ConsoleConhostSubkeyPath, writable: false))
                {
                    _cmdConhostSubkeyExisted = existing != null;
                    _savedConsoleCodePage = existing?.GetValue("CodePage");
                }

                using (var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ConsoleConhostSubkeyPath))
                {
                    cmdKey?.SetValue("CodePage", 65001, Microsoft.Win32.RegistryValueKind.DWord);
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
                    if (key != null)
                    {
                        if (_savedConsoleFaceName != null)
                            key.SetValue("FaceName", _savedConsoleFaceName, Microsoft.Win32.RegistryValueKind.String);
                        else
                            key.DeleteValue("FaceName", throwOnMissingValue: false);

                        if (_savedConsoleFontFamily != null)
                            key.SetValue("FontFamily", _savedConsoleFontFamily, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                            key.DeleteValue("FontFamily", throwOnMissingValue: false);
                    }
                }

                if (_cmdConhostSubkeyExisted)
                {
                    using (var cmdKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ConsoleConhostSubkeyPath, writable: true))
                    {
                        if (cmdKey != null)
                        {
                            if (_savedConsoleCodePage != null)
                                cmdKey.SetValue("CodePage", _savedConsoleCodePage, Microsoft.Win32.RegistryValueKind.DWord);
                            else
                                cmdKey.DeleteValue("CodePage", throwOnMissingValue: false);
                        }
                    }
                }
                else
                {
                    // We created the subkey; remove it entirely to leave the registry as we found it.
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(ConsoleConhostSubkeyPath, throwOnMissingSubKey: false);
                }

                _consoleFontSaved = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestoreConsoleFontRegistry: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines if Visual Studio is using a light theme
        /// </summary>
        private bool IsVsInLightTheme()
        {
            try
            {
                // Check VS theme via environment or registry
                // Light theme typically has RGB > 180 for window background
                var bgColor = GetTerminalBackgroundColor();
                Debug.WriteLine($"Terminal BG Color: R={bgColor.R}, G={bgColor.G}, B={bgColor.B}");
                int brightness = (bgColor.R * 299 + bgColor.G * 587 + bgColor.B * 114) / 1000;
                Debug.WriteLine($"Brightness: {brightness}, Is Light: {brightness > 180}");
                return brightness > 180;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsVsInLightTheme error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Temporarily sets console colors based on VS theme before starting conhost.
        /// For light theme: sets white/light background with dark text.
        /// For dark theme: sets black background with light text.
        /// The original values are saved for restoration after conhost has started.
        /// </summary>
        private void SaveAndSetConsoleColorsRegistry()
        {
            try
            {
                // Custom theme: paint the console background with the user-chosen
                // color (bg = ColorTable00, fg = ColorTable07 black/white by brightness).
                bool isCustom = _settings?.SelectedThemePreference == ThemePreference.Custom;
                System.Drawing.Color customColor = isCustom ? GetCustomThemeColor() : System.Drawing.Color.Black;
                bool customIsLight = isCustom && Brightness(customColor) > 150;
                // Console color tables store BGR (0x00BBGGRR).
                uint customBgr = (uint)((customColor.B << 16) | (customColor.G << 8) | customColor.R);

                bool isLightTheme = isCustom ? customIsLight : IsVsInLightTheme();
                Debug.WriteLine($"SaveAndSetConsoleColorsRegistry - isCustom: {isCustom}, isLightTheme: {isLightTheme}");

                // For custom: bg index 0 (ColorTable00 = custom), fg index 7 (ColorTable07).
                uint screenColors = isCustom ? 0x07U : (isLightTheme ? 0xF0U : 0x0FU);
                uint popupColors = screenColors;

                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Console", writable: true))
                {
                    if (key == null)
                    {
                        Debug.WriteLine("Console registry key not found");
                        return;
                    }

                    // Save original colors
                    _savedConsoleScreenColors = key.GetValue("ScreenColors");
                    _savedConsolePopupColors = key.GetValue("PopupColors");
                    _savedConsoleColorTable00 = key.GetValue("ColorTable00");
                    _savedConsoleColorTable07 = key.GetValue("ColorTable07");
                    _consoleColorsSaved = true;
                    Debug.WriteLine($"Saved original ScreenColors: {_savedConsoleScreenColors}");

                    key.SetValue("ScreenColors", screenColors, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("PopupColors", popupColors, Microsoft.Win32.RegistryValueKind.DWord);

                    if (isCustom)
                    {
                        key.SetValue("ColorTable00", customBgr, Microsoft.Win32.RegistryValueKind.DWord); // custom background
                        key.SetValue("ColorTable07", customIsLight ? 0x00000000U : 0x00FFFFFFU, Microsoft.Win32.RegistryValueKind.DWord); // black/white text
                    }
                    else if (isLightTheme)
                    {
                        // Set explicit RGB values for color table entries
                        key.SetValue("ColorTable00", 0x00000000U, Microsoft.Win32.RegistryValueKind.DWord); // Black
                        key.SetValue("ColorTable07", 0x00FFFFFFU, Microsoft.Win32.RegistryValueKind.DWord); // White
                    }

                    Debug.WriteLine($"Set ScreenColors to: {screenColors:X}");
                }

                // Also set colors in the conhost-specific subkey to ensure they are applied
                using (var conhostKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ConsoleConhostSubkeyPath))
                {
                    if (conhostKey != null)
                    {
                        conhostKey.SetValue("ScreenColors", screenColors, Microsoft.Win32.RegistryValueKind.DWord);
                        conhostKey.SetValue("PopupColors", popupColors, Microsoft.Win32.RegistryValueKind.DWord);
                        if (isCustom)
                        {
                            conhostKey.SetValue("ColorTable00", customBgr, Microsoft.Win32.RegistryValueKind.DWord);
                            conhostKey.SetValue("ColorTable07", customIsLight ? 0x00000000U : 0x00FFFFFFU, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                        Debug.WriteLine($"Set conhost ScreenColors to: {screenColors:X}");
                    }
                }

                // Flush registry to ensure changes propagate before terminal reads them
                Microsoft.Win32.Registry.CurrentUser.Flush();
                Debug.WriteLine("Registry flushed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveAndSetConsoleColorsRegistry error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Restores the original console color registry values saved by SaveAndSetConsoleColorsRegistry.
        /// Called after conhost has started and read its color settings from the registry.
        /// </summary>
        private void RestoreConsoleColorsRegistry()
        {
            if (!_consoleColorsSaved) return;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Console", writable: true))
                {
                    if (key != null)
                    {
                        // Restore all saved values to their original state
                        if (_savedConsoleScreenColors != null)
                            key.SetValue("ScreenColors", _savedConsoleScreenColors, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                            key.DeleteValue("ScreenColors", throwOnMissingValue: false);

                        if (_savedConsolePopupColors != null)
                            key.SetValue("PopupColors", _savedConsolePopupColors, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                            key.DeleteValue("PopupColors", throwOnMissingValue: false);

                        // Restore color table entries (only for light theme which sets them)
                        if (_savedConsoleColorTable00 != null)
                            key.SetValue("ColorTable00", _savedConsoleColorTable00, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                            key.DeleteValue("ColorTable00", throwOnMissingValue: false);

                        if (_savedConsoleColorTable07 != null)
                            key.SetValue("ColorTable07", _savedConsoleColorTable07, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                            key.DeleteValue("ColorTable07", throwOnMissingValue: false);
                    }
                }

                // Also remove colors from conhost-specific subkey
                using (var conhostKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ConsoleConhostSubkeyPath, writable: true))
                {
                    if (conhostKey != null)
                    {
                        conhostKey.DeleteValue("ScreenColors", throwOnMissingValue: false);
                        conhostKey.DeleteValue("PopupColors", throwOnMissingValue: false);
                        conhostKey.DeleteValue("ColorTable00", throwOnMissingValue: false);
                        conhostKey.DeleteValue("ColorTable07", throwOnMissingValue: false);
                    }
                }

                _consoleColorsSaved = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestoreConsoleColorsRegistry: {ex.Message}");
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
        /// Ensures the VS tool window tab that hosts the terminal is the active tab.
        /// Uses the detached window frame when the terminal is detached, otherwise the main tool window frame.
        /// Must be called before keybd_event zoom so the keystrokes go to the terminal, not whatever
        /// VS tab the user was looking at.
        /// </summary>
        private async Task ActivateTerminalToolWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                IVsWindowFrame frame = null;
                if (_isTerminalDetached && _detachedTerminalWindow != null)
                {
                    frame = _detachedTerminalWindow.Frame as IVsWindowFrame;
                }
                else if (_toolWindow != null)
                {
                    frame = _toolWindow.Frame as IVsWindowFrame;
                }

                if (frame == null)
                {
                    return;
                }

                frame.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ActivateTerminalToolWindowAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Focuses the WinForms terminal panel and the embedded terminal child window so that
        /// keybd_event keystrokes land on the terminal rather than whatever VS tab is active.
        /// SetForegroundWindow(terminalHandle) silently fails on child windows, so we go through
        /// the panel's own focus APIs first.
        /// </summary>
        private void FocusTerminalPanel(System.Windows.Forms.Control panel)
        {
            panel.Select();
            panel.Focus();
            SetFocus(terminalHandle);
        }

        /// <summary>
        /// A left-click anywhere on the WPF surface re-asserts native keyboard focus on the WPF
        /// host window when the embedded terminal currently holds it.
        ///
        /// The terminal is a child window from a separate process re-parented into Visual Studio
        /// via SetParent, which permanently joins that process's input queue with the VS UI thread.
        /// Keyboard focus is therefore a single shared state: once it lands on the terminal window,
        /// WPF's own Focus() does not always move native focus back, so the prompt can't be typed
        /// into (the caret stops blinking) and the provider menu can't be navigated with the arrow
        /// keys until Visual Studio is restarted. Re-asserting native focus on the WPF host here
        /// restores typing with a single click instead of a restart. See issue #65.
        /// </summary>
        private void ClaudeCodeControl_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ReclaimWpfKeyboardFocusIfStuck();
        }

        /// <summary>
        /// Moves native keyboard focus to the WPF host window when it is currently held elsewhere
        /// in the shared input queue (the embedded terminal). No-op when WPF already owns it, so
        /// it adds no overhead to ordinary clicks.
        /// </summary>
        private void ReclaimWpfKeyboardFocusIfStuck()
        {
            try
            {
                var source = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                if (source == null || source.Handle == IntPtr.Zero)
                {
                    return;
                }

                // Any WPF element with keyboard focus reports the HwndSource window as the native
                // focus, so a mismatch means the terminal (or another joined window) holds it.
                if (GetFocus() != source.Handle)
                {
                    SetFocus(source.Handle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReclaimWpfKeyboardFocusIfStuck error: {ex.Message}");
            }
        }

        /// <summary>
        /// Replays the saved terminal zoom delta.
        /// Windows Terminal: batches Ctrl+= / Ctrl+- keystrokes through a single SendInput
        /// syscall. This is dramatically faster than the previous keybd_event loop (which
        /// took ~250 ms per step and suppressed the cursor between every press), so the
        /// startup auto-zoom no longer produces a visible input freeze.
        /// Command Prompt: uses PostMessage WM_MOUSEWHEEL+MK_CONTROL — same mechanism as Ctrl+Scroll forwarding.
        /// </summary>
        private async Task ApplyTerminalZoomDeltaAsync(int delta, int initialDelayMs = 1500)
        {
            if (delta == 0 || terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                return;

            try
            {
                if (initialDelayMs > 0)
                {
                    // Give the terminal extra time to finish initializing before replaying zoom.
                    await Task.Delay(initialDelayMs);
                }

                if (!IsWindow(terminalHandle)) return;

                int steps = Math.Abs(delta);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var panel = ActiveTerminalPanel;
                if (panel == null) return;

                if (_wtTabBarHeight > 0)
                {
                    // Activate the VS tab hosting the terminal before sending keystrokes,
                    // then re-acquire the main thread (Task.Delay inside may have left it).
                    await ActivateTerminalToolWindowAsync();
                    await Task.Delay(60);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    FocusTerminalPanel(panel);
                    await Task.Delay(80);

                    // Re-assert focus in case VS restored it to another control during the delay
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetFocus(terminalHandle);

                    SendBatchedCtrlChordToTerminal(delta > 0 ? (ushort)0xBB : (ushort)0xBD, steps);

                    // Trigger a WM_MOUSEMOVE so Windows re-shows the cursor after the
                    // synthesized keystrokes briefly suppressed it.
                    var curPos = System.Windows.Forms.Cursor.Position;
                    System.Windows.Forms.Cursor.Position = curPos;
                }
                else
                {
                    // Command Prompt: PostMessage WM_MOUSEWHEEL+MK_CONTROL directly to
                    // the terminal handle — no focus dependency needed.
                    var screenPt = panel.PointToScreen(
                        new System.Drawing.Point(panel.Width / 2, panel.Height / 2));
                    int lParam = (screenPt.Y << 16) | (screenPt.X & 0xFFFF);
                    int notch = delta > 0 ? 120 : -120;

                    for (int i = 0; i < steps; i++)
                    {
                        int wParam = (notch << 16) | 0x0008; // HIWORD=delta, LOWORD=MK_CONTROL
                        PostMessage(terminalHandle, WM_MOUSEWHEEL, (IntPtr)wParam, (IntPtr)lParam);
                        await Task.Delay(40);
                    }
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ResizeEmbeddedTerminal();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying terminal zoom delta: {ex.Message}");
            }
        }

        /// <summary>
        /// Synthesizes <paramref name="repeats"/> Ctrl+<paramref name="vkKey"/> presses
        /// through a single SendInput call. Replaces a loop of keybd_event + Task.Delay
        /// that was the dominant cost of the Windows Terminal zoom replay.
        /// </summary>
        private static void SendBatchedCtrlChordToTerminal(ushort vkKey, int repeats)
        {
            if (repeats <= 0) return;

            // Per repeat: Ctrl down, key down, key up, Ctrl up
            INPUT[] inputs = new INPUT[repeats * 4];
            for (int i = 0; i < repeats; i++)
            {
                int b = i * 4;

                inputs[b].type = INPUT_KEYBOARD;
                inputs[b].u.ki.wVk = (ushort)VK_CONTROL;
                inputs[b].u.ki.dwFlags = 0;

                inputs[b + 1].type = INPUT_KEYBOARD;
                inputs[b + 1].u.ki.wVk = vkKey;
                inputs[b + 1].u.ki.dwFlags = 0;

                inputs[b + 2].type = INPUT_KEYBOARD;
                inputs[b + 2].u.ki.wVk = vkKey;
                inputs[b + 2].u.ki.dwFlags = KEYEVENTF_KEYUP;

                inputs[b + 3].type = INPUT_KEYBOARD;
                inputs[b + 3].u.ki.wVk = (ushort)VK_CONTROL;
                inputs[b + 3].u.ki.dwFlags = KEYEVENTF_KEYUP;
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
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
            // Debounce timer: saves settings 500ms after last scroll tick.
            // Created on the UI thread (here) since DispatcherTimer is UI-thread-affined; the
            // mouse hook callback marshals back to the UI thread before touching it.
            if (_zoomSaveTimer == null)
            {
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

            StartHookThread();
        }

        /// <summary>
        /// Uninstalls the low-level mouse hook
        /// </summary>
        private void UninstallMouseHook()
        {
            ResetWindowsTerminalSelectionTracking();
            StopHookThread();
            _zoomSaveTimer?.Stop();
            _zoomSaveTimer = null;
        }

        /// <summary>
        /// Installs the low-level keyboard hook to intercept F5/Ctrl+F5 when the terminal has focus
        /// </summary>
        private void InstallKeyboardHook()
        {
            StartHookThread();
        }

        /// <summary>
        /// Uninstalls the low-level keyboard hook
        /// </summary>
        private void UninstallKeyboardHook()
        {
            StopHookThread();
        }

        /// <summary>
        /// Starts the dedicated hook thread (idempotent). The thread installs BOTH low-level
        /// hooks and runs its own Win32 message loop so they are serviced independently of the
        /// VS UI thread. See <see cref="_hookThread"/> and issue #61.
        /// </summary>
        private void StartHookThread()
        {
            if (_hookThread != null && _hookThread.IsAlive) return;

            _hookThreadReady = new ManualResetEventSlim(false);
            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "ClaudeCodeVS LL Input Hooks"
            };
            _hookThread.Start();

            // Wait briefly for the hooks to be installed so the first user input after a terminal
            // start is already intercepted. Bounded so a failure to install never hangs startup.
            _hookThreadReady.Wait(2000);
        }

        /// <summary>
        /// Stops the dedicated hook thread (idempotent) by posting WM_QUIT to its message loop;
        /// the thread unhooks both hooks in its finally block before exiting.
        /// </summary>
        private void StopHookThread()
        {
            var thread = _hookThread;
            if (thread == null) return;

            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            if (!thread.Join(2000))
            {
                Debug.WriteLine("Hook thread did not exit within the timeout.");
            }

            _hookThread = null;
            _hookThreadId = 0;
            _hookThreadReady?.Dispose();
            _hookThreadReady = null;
        }

        /// <summary>
        /// Entry point for the dedicated hook thread: installs both low-level hooks and pumps a
        /// Win32 message loop until WM_QUIT, then unhooks. Keeping the loop here means a stalled
        /// VS UI thread can never starve the hooks and freeze global input. See issue #61.
        /// </summary>
        private void HookThreadProc()
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();

                _mouseHookProc = LowLevelMouseHookCallback;
                _keyboardHookProc = LowLevelKeyboardHookCallback;

                using (var curProcess = Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    IntPtr hModule = GetModuleHandle(curModule.ModuleName);
                    _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, hModule, 0);
                    _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, hModule, 0);
                }

                // Signal the starter that installation is done (success or not) before pumping.
                _hookThreadReady?.Set();

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Hook thread error: {ex.Message}");
            }
            finally
            {
                if (_mouseHookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHookHandle);
                    _mouseHookHandle = IntPtr.Zero;
                }
                if (_keyboardHookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_keyboardHookHandle);
                    _keyboardHookHandle = IntPtr.Zero;
                }
                _mouseHookProc = null;
                _keyboardHookProc = null;

                // Ensure a starter waiting on readiness is released even if install threw early.
                _hookThreadReady?.Set();
            }
        }

        /// <summary>
        /// Checks if the embedded terminal currently has keyboard focus using GetGUIThreadInfo.
        /// Returns true when the focused window is the terminal handle or a child/descendant of it.
        /// </summary>
        private bool IsTerminalFocused()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return false;
            }

            var guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = (uint)Marshal.SizeOf(guiInfo);

            // Passing 0 retrieves info for the foreground thread
            if (!GetGUIThreadInfo(0, ref guiInfo))
            {
                return false;
            }

            IntPtr focusedHwnd = guiInfo.hwndFocus;
            if (focusedHwnd == IntPtr.Zero)
            {
                return false;
            }

            return focusedHwnd == terminalHandle || IsChild(terminalHandle, focusedHwnd);
        }

        /// <summary>
        /// Low-level keyboard hook callback. Intercepts F5 and Ctrl+F5 when the embedded terminal
        /// has focus and forwards them as Visual Studio debug commands (Debug.Start / Debug.StartWithoutDebugging).
        /// </summary>
        private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (uint)wParam.ToInt64() == WM_KEYDOWN)
            {
                var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                // Note when the user is typing into the terminal so the "On Agent Finish" watcher
                // can pause its console read mid-keystroke (computed once; reused for the F5 branch).
                bool terminalFocused = IsTerminalFocused();
                if (terminalFocused) _lastTerminalKeyUtc = DateTime.UtcNow;

                if (info.vkCode == VK_F5 && terminalFocused)
                {
                    bool ctrlHeld = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool shiftHeld = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                    // F5 = Debug.Start, Ctrl+F5 = Debug.StartWithoutDebugging, Shift+F5 = Debug.StopDebugging
                    string command = null;
                    if (!ctrlHeld && !shiftHeld)
                    {
                        command = "Debug.Start";
                    }
                    else if (ctrlHeld && !shiftHeld)
                    {
                        command = "Debug.StartWithoutDebugging";
                    }
                    else if (shiftHeld && !ctrlHeld)
                    {
                        command = "Debug.StopDebugging";
                    }

                    if (command != null)
                    {
                        string vsCommand = command;
#pragma warning disable VSSDK007, VSTHRD110
                        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            try
                            {
                                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                                dte?.ExecuteCommand(vsCommand);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error executing VS command '{vsCommand}': {ex.Message}");
                            }
                        });
#pragma warning restore VSSDK007, VSTHRD110

                        // Consume the keystroke so it doesn't reach the terminal
                        return new IntPtr(1);
                    }
                }
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// Reacts to a click on the embedded terminal panel: makes sure VS is on top of the
        /// Win32 Z-order and that our tool window is the active pane inside VS, then puts
        /// keyboard focus back on the terminal.
        ///
        /// The click itself focuses the terminal natively, but both activation steps below
        /// (SetForegroundWindow and IVsWindowFrame.Show) make VS move keyboard focus into the
        /// WPF tool-window content a moment later, silently stealing it from the terminal —
        /// answering an agent prompt then required a second click (issue #74). When either
        /// step actually ran, re-assert terminal focus after the activation settles.
        /// </summary>
        private void ActivateEmbeddedTerminalOnClick(POINT screenPoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsScreenPointInsideActiveTerminalPanel(screenPoint))
            {
                return;
            }

            // Verify the click really hit the terminal (not another app fully covering VS at the
            // same screen coordinates). Done once here so both activation steps below can trust it.
            if (WindowFromPoint(screenPoint) != terminalHandle)
            {
                return;
            }

            bool vsActivated = BringVisualStudioToForegroundIfNeeded();
            bool paneNeedsActivation = !IsTerminalToolWindowActive();

            if (!vsActivated && !paneNeedsActivation)
            {
                // Nothing stole focus from the click; stay a no-op on ordinary clicks.
                return;
            }

#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (paneNeedsActivation)
                {
                    await ActivateTerminalToolWindowAsync();
                }

                // Let VS finish its activation focus shuffle before taking focus back.
                await Task.Delay(80);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var panel = ActiveTerminalPanel;
                if (panel == null || panel.IsDisposed ||
                    terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    return;
                }

                FocusTerminalPanel(panel);

                // VS can restore focus to another control once more shortly after pane
                // activation (same reason the zoom replay re-asserts), so assert again.
                await Task.Delay(80);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (IsWindow(terminalHandle))
                {
                    SetFocus(terminalHandle);
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Brings the VS top-level window to the front of the Win32 Z-order if it isn't already.
        /// Uses AttachThreadInput to bypass Windows' focus-stealing protection so the activation
        /// works even when another application currently owns the foreground.
        /// Returns true when it actually activated VS (so the caller knows keyboard focus moved).
        /// </summary>
        private bool BringVisualStudioToForegroundIfNeeded()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == terminalHandle)
            {
                return false;
            }

            IntPtr root = GetAncestor(terminalHandle, GA_ROOT);
            if (root == IntPtr.Zero || !IsWindow(root) || foreground == root)
            {
                return false;
            }

            // Plain SetForegroundWindow is denied when our process isn't the foreground app,
            // so attach our input queue to the foreground window's thread for the duration of
            // the call. Windows then treats the activation as originating from the foreground app.
            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = foreground != IntPtr.Zero
                ? GetWindowThreadProcessId(foreground, out _)
                : 0;

            bool attached = false;
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            try
            {
                BringWindowToTop(root);
                SetForegroundWindow(root);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when the embedded terminal's tool window is the currently active pane inside VS.
        /// </summary>
        private bool IsTerminalToolWindowActive()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var myFrame = ((_isTerminalDetached && _detachedTerminalWindow != null)
                ? _detachedTerminalWindow.Frame
                : _toolWindow?.Frame) as IVsWindowFrame;
            if (myFrame == null)
            {
                return false;
            }

            var monitor = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (monitor == null)
            {
                return false;
            }

            if (Microsoft.VisualStudio.ErrorHandler.Failed(monitor.GetCurrentElementValue(
                (uint)Microsoft.VisualStudio.VSConstants.VSSELELEMID.SEID_WindowFrame, out object active)))
            {
                return false;
            }

            return ReferenceEquals(active, myFrame);
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
                uint message = unchecked((uint)wParam.ToInt64());

                // This runs on the dedicated hook thread, not the UI thread. All the actual
                // handling touches WPF/WinForms state, so marshal it to the UI thread without
                // blocking this callback. A cheap inline filter keeps us from flooding the
                // dispatcher with the high-frequency WM_MOUSEMOVE/plain-wheel events.
                if (ShouldDispatchMouseHookMessage(message))
                {
                    var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    try
                    {
                        // Fire-and-forget marshal to the UI thread; HandleMouseHookEvent runs there.
#pragma warning disable VSTHRD001, VSTHRD010, VSTHRD110
                        Dispatcher.BeginInvoke(new Action(() => HandleMouseHookEvent(message, info)));
#pragma warning restore VSTHRD001, VSTHRD010, VSTHRD110
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Mouse hook dispatch error: {ex.Message}");
                    }
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// Cheap, thread-safe pre-filter run on the hook thread to decide whether a mouse event
        /// is worth marshaling to the UI thread. Only the Ctrl+Scroll zoom gesture, left-button
        /// clicks, and in-progress Windows Terminal selection drags need handling.
        /// </summary>
        private bool ShouldDispatchMouseHookMessage(uint message)
        {
            switch (message)
            {
                case WM_MOUSEWHEEL:
                    return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                    return true;
                case WM_MOUSEMOVE:
                    return _wtTabBarHeight > 0
                           && _windowsTerminalSelectionPending
                           && !_windowsTerminalSelectionActive;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles a marshaled mouse-hook event on the UI thread. Mirrors the original inline
        /// hook logic; only the thread it runs on changed (see <see cref="LowLevelMouseHookCallback"/>).
        /// </summary>
        private void HandleMouseHookEvent(uint message, MSLLHOOKSTRUCT info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

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
                    ScheduleManualZoomRefresh();
                }
            }
            else if (message == WM_LBUTTONDOWN)
            {
                ActivateEmbeddedTerminalOnClick(info.pt);
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

        /// <summary>
        /// Resizes the embedded terminal window to match the panel size
        /// For Windows Terminal, hides the tab bar by positioning it off-screen
        /// </summary>
        private void ResizeEmbeddedTerminal()
        {
            var panel = ActiveTerminalPanel;
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle) && panel != null)
            {
                if (panel.Width <= 0 || panel.Height <= 0)
                {
                    return;
                }

                uint windowPosFlags = SWP_NOZORDER | SWP_NOACTIVATE;

                if (_wtTabBarHeight > 0)
                {
                    windowPosFlags |= SWP_FRAMECHANGED;

                    // Windows Terminal: hide tab bar by positioning it above the visible area
                    // Set height to panel height + tab bar (so tab bar goes off-screen above)
                    SetWindowPos(terminalHandle, IntPtr.Zero,
                                0, -_wtTabBarHeight, panel.Width, panel.Height + _wtTabBarHeight,
                                windowPosFlags);
                }
                else
                {
                    // Command Prompt: use panel dimensions directly
                    SetWindowPos(terminalHandle, IntPtr.Zero, 0, 0,
                                panel.Width, panel.Height,
                                windowPosFlags);
                }

                RefreshEmbeddedTerminalWindow();
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
            CancellationToken cancellationToken = default, Process launcherProcess = null)
        {
            var sw = Stopwatch.StartNew();
            var targetPids = new HashSet<uint> { (uint)conhostPid };
            var className = new System.Text.StringBuilder(256);

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The launcher died — its console window will never appear, so let the caller
                // react (retry the spawn) instead of polling out the full timeout (issue #73).
                if (launcherProcess != null && HasTerminalProcessExited(launcherProcess))
                    return IntPtr.Zero;

                // Refresh child PIDs each iteration using ToolHelp32 snapshot (sub-ms, no WMI).
                // GetWindowThreadProcessId returns the console client's PID (cmd.exe), not conhost's
                // PID, due to Windows backward compatibility — so we need the cmd.exe child PID.
                foreach (uint childPid in GetChildProcessIds((uint)conhostPid))
                    targetPids.Add(childPid);

                IntPtr found = IntPtr.Zero;
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (!targetPids.Contains(pid))
                        return true;

                    // A matching PID alone is not enough: other top-level windows can report
                    // these PIDs too (e.g. the conhost thread's invisible "Default IME" window).
                    // Embedding one of those leaves the panel blank while the real console
                    // window stays floating (issue #73) — accept only the console window class.
                    className.Length = 0;
                    GetClassName(hWnd, className, className.Capacity);
                    if (!string.Equals(className.ToString(), "ConsoleWindowClass", StringComparison.Ordinal))
                        return true;

                    found = hWnd;
                    ShowWindow(hWnd, SW_HIDE);
                    return false;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                    return found;

                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Makes sure the WinForms panel that hosts the embedded terminal exists and has a live
        /// Win32 handle, recreating it when it is missing or disposed. The panel can die when its
        /// WindowsFormsHost tears down — before this guard the launch silently bailed out, and
        /// because the dead panel persisted, every subsequent "Restart code agent" stayed blank
        /// until the project was reopened (issue #73). Must run on the UI thread. Returns false
        /// only when there is no TerminalHost left to attach a panel to.
        /// </summary>
        private bool EnsureTerminalPanelReady()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (terminalPanel != null && !terminalPanel.IsDisposed && terminalPanel.Handle != IntPtr.Zero)
                {
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the checks — fall through and recreate.
            }

            if (TerminalHost == null)
            {
                LogTerminalLaunch("terminal panel unavailable and TerminalHost is null — cannot recreate");
                return false;
            }

            LogTerminalLaunch($"terminal panel {(terminalPanel == null ? "missing" : "dead")} — recreating before embed");
            terminalPanel = new System.Windows.Forms.Panel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                BackColor = GetTerminalBackgroundColor()
            };
            TerminalHost.Child = terminalPanel;
            terminalPanel.Resize += (s, e) => ResizeEmbeddedTerminal();

            return terminalPanel.Handle != IntPtr.Zero;
        }

        /// <summary>
        /// True when the terminal launcher process has exited (or its state can no longer be read).
        /// </summary>
        private static bool HasTerminalProcessExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        /// <summary>
        /// Path of the terminal-launch diagnostic log. Debug.WriteLine is compiled out of Release
        /// builds, so launch failures on user machines (issue #73) were impossible to diagnose
        /// remotely — this small rolling log is what users attach to bug reports.
        /// </summary>
        private static string TerminalLaunchLogPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "ClaudeCodeExtension", "terminal-launch.log");

        private static readonly object _terminalLaunchLogLock = new object();

        /// <summary>
        /// Appends a timestamped line to the terminal-launch diagnostic log. Must never throw —
        /// diagnostics cannot be allowed to break the launch path. The file is reset once it
        /// grows past 512 KB so it can be left enabled permanently.
        /// </summary>
        private static void LogTerminalLaunch(string message)
        {
            try
            {
                lock (_terminalLaunchLogLock)
                {
                    string path = TerminalLaunchLogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        info.Delete();
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
                // Swallow everything — logging is best-effort only.
            }
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
                        await SendTextToTerminalAsync("wsl bash -lic \"npm install -g @openai/codex@latest\"");
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
                        await SendTextToTerminalAsync("wsl bash -lic \"cursor-agent update\"");
                        break;

                    case AiProvider.ClaudeCodeWSL:
                        // Claude Code WSL: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("wsl bash -lic \"claude update\"");
                        break;

                    case AiProvider.ClaudeCode:
                        // Claude Code Windows: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("claude update");
                        break;

                    case AiProvider.OpenCode:
                        // Open Code: send exit command
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000); // Reduced from 1500ms
                        await SendTextToTerminalAsync("npm i -g opencode-ai");
                        break;

                    case AiProvider.Windsurf:
                        // Windsurf: exit, wait, then update
                        await SendTextToTerminalAsync("exit");
                        await Task.Delay(1000);
                        await SendTextToTerminalAsync("wsl bash -lic \"devin update\"");
                        break;

                    case AiProvider.Pi:
                        // PI: exit by holding CTRL and tapping D twice. PI quits on the first
                        // CTRL+D, so the second leaks into cmd as a stray ^D — press Escape to
                        // discard that input line before typing the update command.
                        SendCtrlDD();
                        await Task.Delay(1000);
                        SendEscapeKey();
                        await Task.Delay(300);
                        await SendTextToTerminalAsync("npm install -g @earendil-works/pi-coding-agent@latest");
                        break;

                    case AiProvider.Antigravity:
                        // Antigravity: exit by holding CTRL and tapping D twice, wait, then update
                        SendCtrlDD();
                        await Task.Delay(3000);
                        await SendTextToTerminalAsync("agy update");
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
        /// Exits the Antigravity agent: holds CTRL down, taps D twice, then releases CTRL
        /// (Antigravity requires CTRL+D twice to quit instead of an "exit" command)
        /// </summary>
        private void SendCtrlDD()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                SetForegroundWindow(terminalHandle);
                SetFocus(terminalHandle);
                Thread.Sleep(50);

                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero); // CTRL down (hold)
                Thread.Sleep(50);
                keybd_event(VK_D, 0, 0, UIntPtr.Zero); // first D down
                Thread.Sleep(50);
                keybd_event(VK_D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // first D up
                Thread.Sleep(150);
                keybd_event(VK_D, 0, 0, UIntPtr.Zero); // second D down
                Thread.Sleep(50);
                keybd_event(VK_D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // second D up
                Thread.Sleep(50);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // CTRL up (release)
            }
        }

        /// <summary>
        /// Sends an Escape keypress to the terminal. In cmd.exe this discards the current
        /// input line, clearing any stray character (e.g. a leftover ^D after exiting PI)
        /// before the next command is typed.
        /// </summary>
        private void SendEscapeKey()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                SetForegroundWindow(terminalHandle);
                SetFocus(terminalHandle);
                Thread.Sleep(50);

                keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero); // ESC down
                Thread.Sleep(30);
                keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // ESC up
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

                case AiProvider.OpenCode:
                    providerAvailable = await IsOpenCodeAvailableAsync();
                    break;

                case AiProvider.Windsurf:
                    bool wslInstalledForWindsurf = await IsWslInstalledAsync();
                    if (wslInstalledForWindsurf)
                    {
                        providerAvailable = await IsWindsurfAvailableAsync();
                    }
                    break;

                case AiProvider.Pi:
                    providerAvailable = await IsPiAvailableAsync();
                    break;

                case AiProvider.Antigravity:
                    providerAvailable = await IsAntigravityAvailableAsync();
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
                var result = MessageBox.Show(
                    "Restart the code agent? The current session will be terminated.",
                    "Restart Code Agent",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (result != MessageBoxResult.Yes) return;

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
        /// Gets the appropriate Claude Code command to use for Windows or WSL.
        /// Prioritizes native Windows installation (%USERPROFILE%\.local\bin\claude.exe);
        /// otherwise falls back to plain "claude" so cmd.exe resolves it via PATHEXT
        /// (claude.exe from winget, claude.cmd from NPM, etc.).
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
                    // Fall back to whatever claude executable is on PATH — cmd.exe will
                    // resolve claude.exe (winget), claude.cmd (NPM), etc. via PATHEXT.
                    baseCommand = "claude";
                }
            }

            // A user-configured custom path overrides native/PATH resolution.
            baseCommand = ResolveProviderExecutable(
                isWsl ? AiProvider.ClaudeCodeWSL : AiProvider.ClaudeCode, baseCommand, isWsl);

            if (_settings?.ClaudeDangerouslySkipPermissions == true)
            {
                baseCommand = $"{baseCommand} --dangerously-skip-permissions";
            }

            // Consume one-shot resume request if present — the session-history
            // dialog sets this just before triggering a terminal restart.
            string resumeArg = System.Threading.Interlocked.Exchange(ref _pendingResumeSessionId, null);
            if (!string.IsNullOrEmpty(resumeArg))
            {
                if (resumeArg == "-c")
                {
                    baseCommand = $"{baseCommand} --continue";
                }
                else
                {
                    baseCommand = $"{baseCommand} --resume {resumeArg}";
                }
            }

            return baseCommand;
        }

        /// <summary>
        /// Gets the appropriate Codex command to use for Windows or WSL.
        /// Uses --ask-for-approval never when the compatibility toggle is enabled.
        /// </summary>
        /// <returns>The codex command to execute</returns>
        private string GetCodexCommand(bool isWsl = false)
        {
            string baseCommand = ResolveProviderExecutable(
                isWsl ? AiProvider.Codex : AiProvider.CodexNative, "codex", isWsl);

            if (_settings?.CodexFullAuto == true)
            {
                return $"{baseCommand} --ask-for-approval never";
            }

            return baseCommand;
        }

        /// <summary>
        /// Gets the appropriate Windsurf (devin) command.
        /// Uses --permission-mode dangerous when the setting is enabled.
        /// </summary>
        /// <returns>The devin command to execute</returns>
        private string GetWindsurfCommand()
        {
            string baseCommand = ResolveProviderExecutable(AiProvider.Windsurf, "devin", isWsl: true);

            if (_settings?.WindsurfDangerousMode == true)
            {
                return $"{baseCommand} --permission-mode dangerous";
            }

            return baseCommand;
        }

        /// <summary>
        /// Gets the appropriate Antigravity command.
        /// Uses --dangerously-skip-permissions when the setting is enabled.
        /// </summary>
        /// <returns>The agy command to execute</returns>
        private string GetAntigravityCommand()
        {
            string baseCommand = ResolveProviderExecutable(AiProvider.Antigravity, "agy");

            if (_settings?.AntigravityDangerouslySkipPermissions == true)
            {
                return $"{baseCommand} --dangerously-skip-permissions";
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
        /// Prioritizes installation at %LOCALAPPDATA%\cursor-agent\agent.cmd, falls back to agent in PATH.
        /// Appends --yolo when the CursorAgentAutoRun setting is enabled.
        /// </summary>
        /// <returns>The agent command to execute</returns>
        private string GetCursorAgentCommand()
        {
            // Check for installation at %LOCALAPPDATA%\cursor-agent\agent.cmd
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string nativeAgentPath = Path.Combine(localAppData, "cursor-agent", "agent.cmd");

            string baseCommand = File.Exists(nativeAgentPath) ? $"\"{nativeAgentPath}\"" : "agent";

            // A user-configured custom path overrides native/PATH resolution.
            baseCommand = ResolveProviderExecutable(AiProvider.CursorAgentNative, baseCommand);

            if (_settings?.CursorAgentAutoRun == true)
            {
                return $"{baseCommand} --yolo";
            }

            return baseCommand;
        }

        /// <summary>
        /// Gets the appropriate Cursor Agent command to use inside WSL.
        /// Appends --yolo when the CursorAgentAutoRun setting is enabled.
        /// </summary>
        /// <returns>The cursor-agent command to execute in WSL</returns>
        private string GetCursorAgentWslCommand()
        {
            string baseCommand = ResolveProviderExecutable(AiProvider.CursorAgent, "cursor-agent", isWsl: true);

            if (_settings?.CursorAgentAutoRun == true)
            {
                return $"{baseCommand} --yolo";
            }

            return baseCommand;
        }


        #endregion
    }
}
