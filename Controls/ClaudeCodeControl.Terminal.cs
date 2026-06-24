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
        /// Cached VS top-level window that owns the active terminal host panel. Read by hook-thread
        /// focus checks, so those checks do not touch WPF/WinForms controls off the UI thread.
        /// </summary>
        private IntPtr _terminalHostRootWindow;

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
        /// PIDs of the previous terminal session's process tree that a stop issued kills for but
        /// that may still be tearing down. Spawning a fresh conhost while these are dying makes it
        /// exit instantly with code 0 (issue #73), so the relaunch loop re-checks this set before
        /// every respawn. Cleared at the start of each launch request; only touched on the UI thread.
        /// </summary>
        private readonly HashSet<int> _pendingTeardownProcessIds = new HashSet<int>();

        /// <summary>
        /// Monotonic request identifier used to debounce repaint passes after manual Ctrl+Scroll zoom.
        /// </summary>
        private int _manualZoomRefreshRequestId = 0;

        /// <summary>
        /// Enables automatic terminal zoom behavior after startup has settled.
        /// Manual Ctrl+Scroll zoom remains available regardless of this setting.
        /// </summary>
        private static readonly bool EnableStartupTerminalAutoZoom = true;

        /// <summary>
        /// Incremented for every left-button-down the mouse hook sees (anywhere on screen).
        /// The terminal-click focus guard (issue #74) captures the value for its click and
        /// aborts as soon as a newer click happens, so it can never fight the user over focus.
        /// Only touched on the UI thread.
        /// </summary>
        private int _terminalClickSequence;

        /// <summary>
        /// Monotonic counter for the WPF-prompt focus guard. Bumped on every left-click the WPF
        /// surface receives (so a newer click supersedes an older guard) and whenever a click is
        /// confirmed on the embedded terminal (so giving the terminal focus aborts a pending prompt
        /// guard). Lets a click into the prompt box re-assert WPF keyboard focus for a short window
        /// — symmetric with the terminal guard — without the two guards ever fighting. UI thread only.
        /// </summary>
        private int _promptFocusGuardSeq;

        /// <summary>Last time the user clicked or typed in the WPF prompt; keeps the prompt focus guard alive.</summary>
        private DateTime _lastPromptInteractionUtc = DateTime.MinValue;

        /// <summary>True while a single prompt focus-guard loop is running (prevents stacking loops).</summary>
        private bool _promptFocusGuardRunning;

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
                bool useReasonix = _settings?.SelectedProvider == AiProvider.Reasonix;
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
                else if (useReasonix)
                {
                    providerAvailable = await IsReasonixAvailableAsync();
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
                else if (useReasonix)
                {
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(AiProvider.Reasonix);
                    }
                    else
                    {
                        if (!_reasonixNotificationShown)
                        {
                            _reasonixNotificationShown = true;
                            ShowReasonixInstallationInstructions();
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
        private async Task StopExistingTerminalAsync(bool clearRunningProvider = true)
        {
            IntPtr existingTerminalHandle = terminalHandle;
            Process existingProcess = cmdProcess;
            int existingTerminalWindowProcessId = 0;
            bool isWindowsTerminal = false;

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
            if (clearRunningProvider)
            {
                _currentRunningProvider = null;
            }

            // Remember every PID this stop issued a kill for, so the relaunch loop can keep
            // checking whether the old session has really finished dying before each respawn.
            foreach (int killedPid in terminatedProcessIds)
            {
                _pendingTeardownProcessIds.Add(killedPid);
            }

            // Relaunching while the just-killed session is still tearing down is what makes the
            // fresh conhost exit instantly with code 0 or lose its window mid-embed (issue #73).
            // Originally observed with WSL, but the reporter's log shows the same fingerprint
            // with native Claude Code (slow agent/node tree teardown, e.g. under corporate EDR),
            // so wait (bounded) for the killed processes to actually disappear for ALL providers
            // before the caller spawns the next terminal.
            if (terminatedProcessIds.Count > 0)
            {
                var teardownTimer = Stopwatch.StartNew();
                while (teardownTimer.ElapsedMilliseconds < 3000 && AnyProcessStillRunning(terminatedProcessIds))
                {
                    await Task.Delay(100);
                }

                if (teardownTimer.ElapsedMilliseconds >= 100)
                {
                    LogTerminalLaunch($"waited {teardownTimer.ElapsedMilliseconds}ms for terminal teardown" +
                                      (AnyProcessStillRunning(terminatedProcessIds) ? " (old processes still running — proceeding anyway)" : ""));
                }
            }
        }

        /// <summary>
        /// True when any of the given process IDs still maps to a running process.
        /// </summary>
        private static bool AnyProcessStillRunning(HashSet<int> processIds)
        {
            return CountProcessesStillRunning(processIds) > 0;
        }

        /// <summary>
        /// Number of the given process IDs that still map to a running process.
        /// </summary>
        private static int CountProcessesStillRunning(HashSet<int> processIds)
        {
            int alive = 0;
            foreach (int pid in processIds)
            {
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        if (!process.HasExited)
                        {
                            alive++;
                        }
                    }
                }
                catch
                {
                    // Process is gone (or unreadable) — treat as exited.
                }
            }

            return alive;
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
                KillProcessAndChildren(processId, terminatedProcessIds);

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

                string workspaceDir = NormalizeWorkspaceDirectory(await GetWorkspaceDirectoryAsync());
                if (string.IsNullOrEmpty(workspaceDir))
                {
                    workspaceDir = NormalizeWorkspaceDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                }

                _lastTerminalLaunchWorkspaceDirectory = workspaceDir;
                _lastWorkspaceDirectory = workspaceDir;

                // Stop the agent-finish watcher and clear any stale notification before tearing
                // the old terminal down. Its 1-second console-attach tick can otherwise overlap
                // the teardown/relaunch below and leave VS attached to the dying console — the
                // new conhost then fails to create its window and the panel comes up blank
                // (issue #73: "Restart code agent" showing nothing until VS is reopened).
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ResetAgentCompletionWatcher();

                // Revive the low-level input hooks if their thread died — terminal click
                // activation/focus (issue #74) and F5 forwarding stop working without them,
                // and nothing else restarts the thread after a failure.
                StartHookThread();

                // Track teardown of the session being replaced for this launch request only.
                _pendingTeardownProcessIds.Clear();

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
                            cmdCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathCursor, cursorAgentWslCommand)}";
                            break;

                        case AiProvider.CodexNative:
                            string codexCommand = GetCodexCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {codexCommand}";
                            break;

                        case AiProvider.Codex:
                            string wslPathCodex = ConvertToWslPath(workspaceDir);
                            string codexWslCommand = GetCodexCommand(isWsl: true);
                            cmdCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathCodex, codexWslCommand)}";
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            string wslPathClaude = ConvertToWslPath(workspaceDir);
                            string claudeWslCommand = GetClaudeCommand(isWsl: true);
                            cmdCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathClaude, claudeWslCommand)}";
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
                            cmdCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathWindsurf, windsurfWslCommand)}";
                            break;

                        case AiProvider.Pi:
                            string piCommand = ResolveProviderExecutable(AiProvider.Pi, "pi");
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {piCommand}";
                            break;

                        case AiProvider.Antigravity:
                            string antigravityCommand = GetAntigravityCommand();
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {antigravityCommand}";
                            break;

                        case AiProvider.Reasonix:
                            string reasonixCommand = ResolveProviderExecutable(AiProvider.Reasonix, "reasonix");
                            cmdCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {reasonixCommand}";
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
                            if (provider.HasValue && _settings != null)
                            {
                                _settings.SelectedProvider = provider.Value;
                            }

                            string wtProviderTitle = GetExtensionTitle(provider);
                            UpdateToolWindowTitle(wtProviderTitle);
                            UpdateProviderSelection();

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
                            terminalCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathCursor, cursorAgentWslCommand)}";
                            break;

                        case AiProvider.CodexNative:
                            string codexCommand = GetCodexCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {codexCommand}";
                            break;

                        case AiProvider.Codex:
                            string wslPathCodex = ConvertToWslPath(workspaceDir);
                            string codexWslCommand = GetCodexCommand(isWsl: true);
                            terminalCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathCodex, codexWslCommand)}";
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            string wslPathClaude = ConvertToWslPath(workspaceDir);
                            string claudeWslCommand = GetClaudeCommand(isWsl: true);
                            terminalCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathClaude, claudeWslCommand)}";
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
                            terminalCommand = $"/k chcp 65001 >nul && cls && {BuildWslLaunchCommand(wslPathWindsurf, windsurfCmdCommand)}";
                            break;

                        case AiProvider.Pi:
                            string piTerminalCommand = ResolveProviderExecutable(AiProvider.Pi, "pi");
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {piTerminalCommand}";
                            break;

                        case AiProvider.Antigravity:
                            string antigravityTerminalCommand = GetAntigravityCommand();
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {antigravityTerminalCommand}";
                            break;

                        case AiProvider.Reasonix:
                            string reasonixTerminalCommand = ResolveProviderExecutable(AiProvider.Reasonix, "reasonix");
                            terminalCommand = $"/k chcp 65001 >nul && cd /d \"{workspaceDir}\" && ping localhost -n 3 >nul && cls && {reasonixTerminalCommand}";
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
                            // don't pile up live conhosts, then back off to let the teardown settle.
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            await StopExistingTerminalAsync(clearRunningProvider: false);
                            LogTerminalLaunch($"relaunching after embed failure ({lastFailureReason}); relaunch {relaunch + 1}/{relaunchBackoffMs.Length} after {relaunchBackoffMs[relaunch]}ms backoff");
                            await Task.Delay(relaunchBackoffMs[relaunch]);

                            // An instant-exit attempt leaves nothing of its own to wait on, but the
                            // ORIGINAL session's process tree may still be dying — and spawning into
                            // that teardown is exactly what kills the fresh conhost (issue #73).
                            // Hold this respawn (bounded) until the old tree is actually gone.
                            int teardownStillAlive = CountProcessesStillRunning(_pendingTeardownProcessIds);
                            if (teardownStillAlive > 0)
                            {
                                var respawnGateTimer = Stopwatch.StartNew();
                                while (respawnGateTimer.ElapsedMilliseconds < 5000 &&
                                       AnyProcessStillRunning(_pendingTeardownProcessIds))
                                {
                                    await Task.Delay(150);
                                }

                                LogTerminalLaunch($"waited extra {respawnGateTimer.ElapsedMilliseconds}ms for {teardownStillAlive} old terminal process(es) to exit before respawn" +
                                                  (AnyProcessStillRunning(_pendingTeardownProcessIds) ? " (still running — proceeding anyway)" : ""));
                            }
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
                                    // The agent-finish console capture replaces VS's standard
                                    // handles via AttachConsole, and FreeConsole leaves them
                                    // dangling once the old console dies. A conhost spawned with
                                    // those dead values inherited exits immediately with code 0
                                    // and the panel stays blank until VS is reopened (issue #73),
                                    // so put the originals back right before CreateProcess.
                                    bool stdHandlesWereDirty = RestoreOriginalStdHandles();

                                    cmdProcess = new Process { StartInfo = startInfo };
                                    cmdProcess.Start();
                                    LogTerminalLaunch($"conhost spawned: pid={cmdProcess.Id}, attempt={spawnAttempt}/2, vsHadConsoleAttached={vsHadConsole}, stdHandlesWereDirty={stdHandlesWereDirty}, spawnLockTaken={consoleLockTaken}");
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
                                // oldSessionProcsAlive confirms (or rules out) the teardown-contention
                                // cause from a user's log alone: >0 means the previous session's tree
                                // was still dying when this spawn was killed (issue #73).
                                LogTerminalLaunch($"conhost exited immediately after launch: exitCode={exitCode}, attempt={spawnAttempt}/2, oldSessionProcsAlive={CountProcessesStillRunning(_pendingTeardownProcessIds)}");
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
                            if (provider.HasValue && _settings != null)
                            {
                                _settings.SelectedProvider = provider.Value;
                            }

                            string providerTitle = GetExtensionTitle(provider);
                            UpdateToolWindowTitle(providerTitle);
                            UpdateProviderSelection();

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
        /// Saved original console ColorTable00..ColorTable15 from registry, for restoration
        /// after conhost starts. All 16 ANSI palette slots are tracked because the light-theme
        /// fix (issue #80) rewrites the accent slots, not just background/foreground.
        /// </summary>
        private readonly object[] _savedConsoleColorTable = new object[16];

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
                // If a previous launch saved the originals but never restored them
                // (e.g. a superseded/aborted launch during the issue #73 relaunch
                // churn), recover the true user values first. Otherwise the next
                // save would capture our own temporary themed color as the
                // "original" and bake it in permanently -- every later restore
                // would then write that color back, so a non-custom theme would
                // keep showing the old custom background forever.
                if (_consoleColorsSaved)
                    RestoreConsoleColorsRegistry();

                // Paint the console background with the current theme's background
                // color for ALL themes (custom/dark/light/automatic). ColorTable00
                // is written explicitly every time so a non-custom theme can never
                // inherit a stale ColorTable00 left behind by a previous custom run.
                // bg = ColorTable00, fg = ColorTable07 (black/white by brightness),
                // ScreenColors low nibble = fg index 7, high nibble = bg index 0.
                System.Drawing.Color bgColor = GetTerminalBackgroundColor();
                bool bgIsLight = Brightness(bgColor) > 150;
                // Console color tables store BGR (0x00BBGGRR).
                uint bgBgr = (uint)((bgColor.B << 16) | (bgColor.G << 8) | bgColor.R);
                uint fgBgr = bgIsLight ? 0x00000000U : 0x00FFFFFFU; // black on light, white on dark

                uint screenColors = 0x07U; // bg index 0 (ColorTable00), fg index 7 (ColorTable07)
                uint popupColors = screenColors;

                Debug.WriteLine($"SaveAndSetConsoleColorsRegistry - bg=#{bgColor.R:X2}{bgColor.G:X2}{bgColor.B:X2}, bgIsLight: {bgIsLight}");

                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Console", writable: true))
                {
                    if (key == null)
                    {
                        Debug.WriteLine("Console registry key not found");
                        return;
                    }

                    // Save original colors (all 16 palette slots, so the light-theme accent
                    // overrides below can be reverted cleanly).
                    _savedConsoleScreenColors = key.GetValue("ScreenColors");
                    _savedConsolePopupColors = key.GetValue("PopupColors");
                    for (int i = 0; i < 16; i++)
                        _savedConsoleColorTable[i] = key.GetValue($"ColorTable{i:D2}");
                    _consoleColorsSaved = true;
                    Debug.WriteLine($"Saved original ScreenColors: {_savedConsoleScreenColors}");

                    key.SetValue("ScreenColors", screenColors, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("PopupColors", popupColors, Microsoft.Win32.RegistryValueKind.DWord);
                    WriteThemedConsolePalette(key, bgBgr, fgBgr, bgIsLight);

                    Debug.WriteLine($"Set ScreenColors to: {screenColors:X}");
                }

                // Also set colors in the conhost-specific subkey to ensure they are applied
                using (var conhostKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ConsoleConhostSubkeyPath))
                {
                    if (conhostKey != null)
                    {
                        conhostKey.SetValue("ScreenColors", screenColors, Microsoft.Win32.RegistryValueKind.DWord);
                        conhostKey.SetValue("PopupColors", popupColors, Microsoft.Win32.RegistryValueKind.DWord);
                        WriteThemedConsolePalette(conhostKey, bgBgr, fgBgr, bgIsLight);
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
        /// Writes the themed background (ColorTable00) and foreground (ColorTable07) into a console
        /// registry key. On a LIGHT background the 14 accent slots (ColorTable01-06 and 08-15) ship
        /// tuned for a dark background and wash out (issue #80), so a light-appropriate full palette
        /// of darker/saturated accent colors is painted instead. On a dark background the default
        /// accent slots are left untouched.
        /// </summary>
        private void WriteThemedConsolePalette(Microsoft.Win32.RegistryKey key, uint bgBgr, uint fgBgr, bool bgIsLight)
        {
            key.SetValue("ColorTable00", bgBgr, Microsoft.Win32.RegistryValueKind.DWord); // themed background
            key.SetValue("ColorTable07", fgBgr, Microsoft.Win32.RegistryValueKind.DWord); // black/white text

            if (!bgIsLight)
                return;

            // Accent slots only (skip 00 = background and 07 = foreground, already set above).
            uint[] palette = GetLightConsolePalette();
            for (int i = 1; i < 16; i++)
            {
                if (i == 7) continue;
                key.SetValue($"ColorTable{i:D2}", palette[i], Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Returns a 16-entry ANSI palette (BGR, 0x00BBGGRR) with darker/saturated accent colors that
        /// stay legible on a light console background. The "bright" slots (09-14) mirror their normal
        /// counterparts so agent TUIs that emit bright accents (e.g. cyan inline code) remain readable.
        /// Slots 00 (background) and 07 (foreground) are placeholders; callers override them with the
        /// active theme colors.
        /// </summary>
        private static uint[] GetLightConsolePalette()
        {
            // Local helper: pack R,G,B into the console's BGR DWORD layout.
            uint Bgr(byte r, byte g, byte b) => (uint)((b << 16) | (g << 8) | r);

            return new uint[16]
            {
                Bgr(0xFF, 0xFF, 0xFF), // 00 black/background (overridden by caller)
                Bgr(0xC5, 0x0F, 0x1F), // 01 red
                Bgr(0x0E, 0x7A, 0x0E), // 02 green
                Bgr(0x94, 0x6A, 0x00), // 03 yellow (dark amber)
                Bgr(0x00, 0x37, 0xDA), // 04 blue
                Bgr(0x88, 0x17, 0x98), // 05 magenta
                Bgr(0x0E, 0x80, 0x80), // 06 cyan (teal)
                Bgr(0x00, 0x00, 0x00), // 07 white/foreground (overridden by caller)
                Bgr(0x76, 0x76, 0x76), // 08 bright black (gray)
                Bgr(0xC5, 0x0F, 0x1F), // 09 bright red
                Bgr(0x0E, 0x7A, 0x0E), // 10 bright green
                Bgr(0x94, 0x6A, 0x00), // 11 bright yellow
                Bgr(0x00, 0x37, 0xDA), // 12 bright blue
                Bgr(0x88, 0x17, 0x98), // 13 bright magenta
                Bgr(0x0E, 0x80, 0x80), // 14 bright cyan
                Bgr(0x26, 0x26, 0x26), // 15 bright white (dark gray on light bg)
            };
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

                        // Restore all 16 color table entries to their original state
                        for (int i = 0; i < 16; i++)
                        {
                            string name = $"ColorTable{i:D2}";
                            if (_savedConsoleColorTable[i] != null)
                                key.SetValue(name, _savedConsoleColorTable[i], Microsoft.Win32.RegistryValueKind.DWord);
                            else
                                key.DeleteValue(name, throwOnMissingValue: false);
                        }
                    }
                }

                // Also remove colors from conhost-specific subkey
                using (var conhostKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ConsoleConhostSubkeyPath, writable: true))
                {
                    if (conhostKey != null)
                    {
                        conhostKey.DeleteValue("ScreenColors", throwOnMissingValue: false);
                        conhostKey.DeleteValue("PopupColors", throwOnMissingValue: false);
                        for (int i = 0; i < 16; i++)
                            conhostKey.DeleteValue($"ColorTable{i:D2}", throwOnMissingValue: false);
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
        /// Focuses the WinForms terminal panel and the embedded terminal window so user keystrokes
        /// and keybd_event input land in the terminal rather than the last focused VS control.
        /// </summary>
        private void FocusTerminalPanel(System.Windows.Forms.Control panel)
        {
            if (panel == null || panel.IsDisposed || panel.Handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                panel.Select();
                panel.Focus();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FocusTerminalPanel panel focus error: {ex.Message}");
            }

            FocusTerminalWindow();
        }

        /// <summary>
        /// Gives native keyboard focus to the embedded terminal. SetParent already permanently
        /// joins the terminal thread's input queue with the VS UI thread (issue #65), so a plain
        /// SetFocus moves native focus without merging any extra queues.
        ///
        /// We deliberately do NOT AttachThreadInput the current foreground thread here. Merging an
        /// arbitrary (possibly busy) foreground thread's input queue with VS's makes those threads
        /// share one synchronous input queue for the duration; while the conhost thread is busy
        /// rendering the agent's output, keyboard and mouse events back up for everyone until they
        /// detach, which froze input in the terminal while the agent worked (v26/v27 regression).
        /// The terminal thread is attached only as a fallback when SetFocus didn't take, since it is
        /// already joined to us and so cannot cause the cross-thread stall the foreground attach did.
        /// </summary>
        private void FocusTerminalWindow()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return;
            }

            try
            {
                // SetParent already merged the terminal's input queue with ours, so this usually
                // takes on its own without any AttachThreadInput.
                SetFocus(terminalHandle);
                if (GetFocus() == terminalHandle)
                {
                    return;
                }

                // Fallback: briefly attach ONLY the terminal thread (already joined to us, so this
                // is cheap and cannot stall on an unrelated busy thread) and retry the focus.
                uint currentThreadId = GetCurrentThreadId();
                uint terminalThreadId = GetWindowThreadProcessId(terminalHandle, out _);
                if (terminalThreadId == 0 || terminalThreadId == currentThreadId)
                {
                    return;
                }

                bool attached = AttachThreadInput(currentThreadId, terminalThreadId, true);
                try
                {
                    SetFocus(terminalHandle);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, terminalThreadId, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FocusTerminalWindow error: {ex.Message}");
            }
        }

        /// <summary>
        /// Activates the terminal's VS tab and sets native focus to the embedded terminal.
        /// </summary>
        private async Task<bool> FocusTerminalForInputAsync(int settleDelayMs = 80)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return false;
            }

            await ActivateTerminalToolWindowAsync();
            await Task.Delay(60);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var panel = ActiveTerminalPanel;
            if (panel == null || panel.IsDisposed || panel.Handle == IntPtr.Zero)
            {
                return false;
            }

            FocusTerminalForInput();

            if (settleDelayMs > 0)
            {
                await Task.Delay(settleDelayMs);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                FocusTerminalWindow();
            }

            return true;
        }

        /// <summary>
        /// Sets native focus to the current terminal without changing VS tabs.
        /// </summary>
        private bool FocusTerminalForInput()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return false;
            }

            var panel = ActiveTerminalPanel;
            if (panel == null || panel.IsDisposed || panel.Handle == IntPtr.Zero)
            {
                FocusTerminalWindow();
                return true;
            }

            BringVisualStudioToForegroundIfNeeded();
            FocusTerminalPanel(panel);
            return true;
        }

        /// <summary>
        /// Gets the VS top-level window that owns the current terminal host panel.
        /// </summary>
        private IntPtr GetTerminalHostRootWindow()
        {
            var panel = ActiveTerminalPanel;
            if (panel != null && !panel.IsDisposed && panel.Handle != IntPtr.Zero)
            {
                IntPtr root = GetAncestor(panel.Handle, GA_ROOT);
                if (root != IntPtr.Zero && IsWindow(root))
                {
                    _terminalHostRootWindow = root;
                    return root;
                }
            }

            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                IntPtr root = GetAncestor(terminalHandle, GA_ROOT);
                if (root != IntPtr.Zero && IsWindow(root))
                {
                    _terminalHostRootWindow = root;
                    return root;
                }
            }

            _terminalHostRootWindow = IntPtr.Zero;
            return IntPtr.Zero;
        }

        /// <summary>
        /// Returns true when a hit-test HWND belongs to either the terminal window or its host panel.
        /// </summary>
        private bool IsTerminalOrHostPanelHit(IntPtr hwndAtPoint, System.Windows.Forms.Control panel)
        {
            if (hwndAtPoint == IntPtr.Zero)
            {
                return false;
            }

            if (terminalHandle != IntPtr.Zero &&
                IsWindow(terminalHandle) &&
                (hwndAtPoint == terminalHandle || IsChild(terminalHandle, hwndAtPoint)))
            {
                return true;
            }

            return panel != null &&
                   !panel.IsDisposed &&
                   panel.Handle != IntPtr.Zero &&
                   (hwndAtPoint == panel.Handle || IsChild(panel.Handle, hwndAtPoint));
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

            // Single-shot reclaim isn't enough on its own: after the click, a deferred focus steal
            // (the cross-process terminal grabbing the shared native focus, or — on Command Prompt —
            // the "On Agent Finish" console attach bouncing focus) can land a moment later and leave
            // typing into the prompt going nowhere, forcing repeated clicks. Keep the prompt focus
            // guard alive so WPF keeps native focus while the user works in the prompt.
            EnsureWpfPromptFocusGuardRunning();
        }

        /// <summary>
        /// Starts (or extends) the WPF-prompt focus guard, symmetric with
        /// <see cref="EnsureTerminalFocusAfterClickAsync"/>. While the user keeps interacting with the
        /// prompt (clicks or keystrokes refresh <see cref="_lastPromptInteractionUtc"/>), it re-asserts
        /// native keyboard focus on the WPF host whenever the embedded terminal has stolen it — so
        /// typing into the prompt lands instead of requiring repeated clicks, even during active
        /// generation. Only one loop runs at a time. It reclaims ONLY from the terminal (WPF popups /
        /// the ⚙ menu are left alone), and aborts on a terminal click or an app switch, so it never
        /// fights the user — only deferred focus steals.
        /// </summary>
        private void EnsureWpfPromptFocusGuardRunning()
        {
            _lastPromptInteractionUtc = DateTime.UtcNow;
            if (_promptFocusGuardRunning) return;
            _promptFocusGuardRunning = true;
            int seq = _promptFocusGuardSeq;

#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await RunWpfPromptFocusGuardAsync(seq); }
                finally { _promptFocusGuardRunning = false; }
            });
#pragma warning restore VSSDK007
        }

        private async Task RunWpfPromptFocusGuardAsync(int seq)
        {
            const int checkIntervalMs = 80;
            const double idleWindowMs = 700; // keep guarding until ~0.7s after the last prompt interaction

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var source = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (source == null || source.Handle == IntPtr.Zero) return;
            IntPtr wpfHandle = source.Handle;
            IntPtr wpfRoot = GetAncestor(wpfHandle, GA_ROOT);

            while ((DateTime.UtcNow - _lastPromptInteractionUtc).TotalMilliseconds < idleWindowMs)
            {
                await Task.Delay(checkIntervalMs);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // A terminal click supersedes this guard (the user wants the terminal now).
                if (seq != _promptFocusGuardSeq) return;
                if (!IsWindow(wpfHandle)) return;

                // The user moved to another application — leave focus alone. While the terminal holds
                // the shared native focus the foreground window is still the VS root, so that case is
                // not treated as "left VS" and we keep re-asserting.
                IntPtr foreground = GetForegroundWindow();
                if (wpfRoot != IntPtr.Zero && foreground != wpfRoot && foreground != terminalHandle)
                {
                    return;
                }

                // Only reclaim when the embedded terminal specifically holds the shared native focus.
                // Other native focus owners — a WPF ContextMenu/Popup (e.g. the ⚙ menu) or a child
                // editor — are legitimate and must be left alone, or menu/arrow navigation would break.
                IntPtr focusHwnd = GetFocus();
                bool terminalHasFocus = terminalHandle != IntPtr.Zero && IsWindow(terminalHandle)
                    && (focusHwnd == terminalHandle || IsChild(terminalHandle, focusHwnd));
                if (terminalHasFocus)
                {
                    SetFocus(wpfHandle);
                }
            }
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
                    FocusTerminalWindow();

                    SendBatchedCtrlChordToTerminal(delta > 0 ? (ushort)0xBB : (ushort)0xBD, steps);

                    // Trigger a WM_MOUSEMOVE so Windows re-shows the cursor after the
                    // synthesized keystrokes briefly suppressed it.
                    var curPos = System.Windows.Forms.Cursor.Position;
                    System.Windows.Forms.Cursor.Position = curPos;
                }
                else
                {
                    // Command Prompt: set the console font size directly (issue #76/#78). Posting
                    // WM_MOUSEWHEEL would be swallowed if the agent's TUI is already in mouse-input
                    // mode by the time the replay fires, so apply the saved delta the same
                    // input-mode-independent way the interactive zoom now does.
                    await Task.Run(() => TryAdjustConhostFontSize(delta));
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

            IntPtr foreground = GetForegroundWindow();
            IntPtr hostRoot = _terminalHostRootWindow;
            if (hostRoot != IntPtr.Zero && !IsWindow(hostRoot))
            {
                hostRoot = IntPtr.Zero;
            }

            if (hostRoot == IntPtr.Zero)
            {
                hostRoot = GetAncestor(terminalHandle, GA_ROOT);
            }

            if (foreground != terminalHandle &&
                (hostRoot == IntPtr.Zero || foreground != hostRoot))
            {
                return false;
            }

            if (IsTerminalFocusedOnThread(0))
            {
                return true;
            }

            uint terminalThreadId = GetWindowThreadProcessId(terminalHandle, out _);
            return terminalThreadId != 0 && IsTerminalFocusedOnThread(terminalThreadId);
        }

        /// <summary>
        /// Checks focus for a specific GUI thread. Passing 0 asks Windows for the foreground thread.
        /// </summary>
        private bool IsTerminalFocusedOnThread(uint threadId)
        {
            var guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = (uint)Marshal.SizeOf(guiInfo);

            if (!GetGUIThreadInfo(threadId, ref guiInfo))
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

                // IsTerminalFocused() is a cross-process call (GetGUIThreadInfo); running it on every
                // system-wide keystroke added latency to all typing. It is only needed for F5
                // forwarding and for the "On Agent Finish" typing guard — so compute it only when an
                // F5 is pressed or a completion watch is actually running, and skip it otherwise.
                bool needFocus = info.vkCode == VK_F5 || _completionWatchActive;
                bool terminalFocused = needFocus && IsTerminalFocused();

                // Note when the user is typing into the terminal so the "On Agent Finish" watcher
                // can pause its console read mid-keystroke (only meaningful while a watch is active).
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
        /// Win32 Z-order and that our tool window is the active pane inside VS, then keeps
        /// keyboard focus on the terminal until the activation dust settles.
        ///
        /// The click itself focuses the terminal natively, but the activation steps
        /// (SetForegroundWindow and IVsWindowFrame.Show) make VS move keyboard focus into the
        /// WPF tool-window content a moment later, silently stealing it from the terminal
        /// (issue #74). The original fix re-asserted focus exactly twice at fixed 80ms delays —
        /// on a heavily loaded machine (an agent crunching while VS repaints) VS's focus restore
        /// can land *after* both asserts, so every click appeared to do nothing and the terminal
        /// became impossible to select. The guard now verifies and re-asserts focus repeatedly
        /// for ~1.6s, aborting the moment the user clicks anywhere else or leaves VS, so a click
        /// on the terminal reliably ends with the terminal focused regardless of timing.
        /// </summary>
        private void ActivateEmbeddedTerminalOnClick(POINT screenPoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var panel = ActiveTerminalPanel;
            if (panel == null || panel.IsDisposed || !IsScreenPointInsideActiveTerminalPanel(screenPoint))
            {
                return;
            }

            // Verify the click really hit this visible terminal/host panel, not another VS instance
            // covering the same screen coordinates. Some conhost builds report the WinForms host
            // panel from WindowFromPoint even though the console is visibly under the pointer.
            IntPtr hwndAtPoint = WindowFromPoint(screenPoint);
            if (!IsTerminalOrHostPanelHit(hwndAtPoint, panel))
            {
                return;
            }

            // Giving the terminal focus must cancel any in-flight WPF-prompt focus guard, otherwise
            // the two would fight over the shared native focus.
            _promptFocusGuardSeq++;

            BringVisualStudioToForegroundIfNeeded();
            bool paneNeedsActivation = !IsTerminalToolWindowActive();
            int clickSequence = _terminalClickSequence;
            IntPtr vsRootWindow = GetTerminalHostRootWindow();

#pragma warning disable VSSDK007 // Fire-and-forget is intentional here
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (paneNeedsActivation)
                {
                    await ActivateTerminalToolWindowAsync();
                }

                await EnsureTerminalFocusAfterClickAsync(clickSequence, vsRootWindow);
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Focus guard for a click on the embedded terminal (issue #74): checks every 80ms for
        /// ~1.6s that native keyboard focus is on the terminal and re-asserts it when VS's
        /// activation shuffle has moved it elsewhere. Bails out immediately when the user clicks
        /// again anywhere (a newer click owns focus now), switches to another application, or the
        /// terminal goes away — so it cannot fight the user, only VS's deferred focus restores.
        /// Ordinary clicks cost one cheap GetFocus check per tick and no asserts.
        /// </summary>
        private async Task EnsureTerminalFocusAfterClickAsync(int clickSequence, IntPtr vsRootWindow)
        {
            const int checkIntervalMs = 80;
            // Short guard (~0.6s): just long enough to cover VS's one deferred focus restore after
            // the click's activation shuffle. The old ~5s window kept re-asserting terminal focus
            // long after the click, which fought the WPF-prompt guard and the user when they moved
            // on — a major part of the v26/v27 input regression. It still aborts instantly on any
            // click elsewhere / app switch, so a brief guard is enough.
            const int totalChecks = 8;

            for (int check = 0; check < totalChecks; check++)
            {
                await Task.Delay(checkIntervalMs);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // A newer click (terminal or not) supersedes this guard.
                if (clickSequence != _terminalClickSequence)
                {
                    return;
                }

                if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    return;
                }

                // The user moved to another application — leave focus alone. Some conhost builds can
                // temporarily report the terminal itself as foreground, so accept that as still ours.
                IntPtr foreground = GetForegroundWindow();
                if (vsRootWindow != IntPtr.Zero &&
                    foreground != vsRootWindow &&
                    foreground != terminalHandle)
                {
                    return;
                }

                var panel = ActiveTerminalPanel;
                if (panel == null || panel.IsDisposed)
                {
                    return;
                }

                if (!IsTerminalFocused())
                {
                    FocusTerminalPanel(panel);
                }
            }
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
            IntPtr root = GetTerminalHostRootWindow();
            if (root == IntPtr.Zero || !IsWindow(root) || foreground == root || foreground == terminalHandle)
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

                // Command Prompt (conhost) Ctrl+Scroll zoom (issue #76/#78): change the console font
                // size directly via SetCurrentConsoleFontEx instead of relying on the mouse wheel.
                // Neither the physical wheel nor a posted WM_MOUSEWHEEL reaches conhost's own zoom when
                // a TUI has put the console into mouse-input mode (QuickEdit off) — conhost forwards
                // the wheel to the app, which ignores it. Setting the font is independent of console
                // input mode, so zoom works whether the agent's TUI is up or not. The physical event
                // is consumed so the zoom is never applied twice when QuickEdit is on. Windows Terminal
                // is left on its native path (it isn't conhost and doesn't have this problem).
                if (message == WM_MOUSEWHEEL
                    && _wtTabBarHeight == 0
                    && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    var wheelInfo = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (TryForwardConhostCtrlZoom(wheelInfo))
                    {
                        // Consume so conhost doesn't also process the physical wheel (double zoom).
                        return (IntPtr)1;
                    }
                }

                // Right-click is left entirely to conhost/Windows: the extension no longer intercepts
                // the right mouse button. Pasting into the agent goes through the prompt box (which
                // delivers via conhost's own Edit→Paste regardless of QuickEdit/mouse-input mode), so
                // a terminal right-click paste fallback isn't needed — and the global interception it
                // required was the source of right-click context menus failing elsewhere in VS (#90).

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
        /// Zooms the embedded Command Prompt (conhost) by changing its console font size directly
        /// when the cursor is over it, and records the zoom delta for replay. Used so Ctrl+Scroll
        /// zoom keeps working even when a TUI has put the console into mouse-input mode (QuickEdit
        /// off), in which conhost ignores both the physical wheel and a posted WM_MOUSEWHEEL
        /// (issue #76/#78). Runs on the mouse-hook thread, so the hit test is a Win32 GetWindowRect
        /// check (no WPF/WinForms access); the font change (which briefly attaches to the agent's
        /// console) is offloaded to a background task and the settings/refresh work is marshaled to
        /// the UI thread. Returns true when the gesture targets the terminal and should be consumed
        /// by the caller — even before the async font change completes — so conhost never also
        /// processes the physical wheel (double zoom when QuickEdit is on).
        /// </summary>
        private bool TryForwardConhostCtrlZoom(MSLLHOOKSTRUCT info)
        {
            IntPtr handle = terminalHandle;
            if (handle == IntPtr.Zero || !IsWindow(handle)) return false;

            // Hook-thread-safe hit test: is the cursor over the embedded terminal window?
            if (!GetWindowRect(handle, out RECT rect)) return false;
            if (info.pt.x < rect.Left || info.pt.x >= rect.Right ||
                info.pt.y < rect.Top || info.pt.y >= rect.Bottom)
            {
                return false;
            }

            int wheelDelta = (short)((info.mouseData >> 16) & 0xFFFF);
            if (wheelDelta == 0) return false;

            int step = wheelDelta > 0 ? 1 : -1;

            // Apply the font change off the hook thread (AttachConsole is too heavy for a low-level
            // hook callback), then persist the delta (for restart replay) and run the deferred
            // repaint passes on the UI thread, mirroring the native Ctrl+Scroll handling.
            try
            {
#pragma warning disable VSTHRD110
                _ = Task.Run(async () =>
                {
                    bool applied = TryAdjustConhostFontSize(step);
                    if (!applied) return;

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (_settings == null) return;
                    _settings.TerminalZoomDelta += step;
                    _zoomSaveTimer?.Stop();
                    _zoomSaveTimer?.Start();
                    ScheduleManualZoomRefresh();
                });
#pragma warning restore VSTHRD110
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryForwardConhostCtrlZoom dispatch error: {ex.Message}");
            }

            return true;
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
                // Every click (anywhere) invalidates the focus guard of the previous
                // terminal click — increment before starting this click's guard.
                _terminalClickSequence++;
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
        /// True when there is still terminal state to manage, even if the embedded window was
        /// not found. Workspace events use this instead of cmdProcess == null so they do not
        /// launch a second terminal while a previous launch is still running or being retried.
        /// </summary>
        private bool HasTerminalLaunchState()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                return true;
            }

            if (cmdProcess == null)
            {
                return false;
            }

            return !HasTerminalProcessExited(cmdProcess);
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

                    case AiProvider.Reasonix:
                        // Reasonix: interrupt the TUI with CTRL+C, wait, then update via npm
                        SendCtrlC();
                        await Task.Delay(1000);
                        await SendTextToTerminalAsync("npm i -g reasonix");
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
                FocusTerminalForInput();
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
                FocusTerminalForInput();
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
                FocusTerminalForInput();
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
                FocusTerminalForInput();
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
                FocusTerminalForInput();
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

                case AiProvider.Reasonix:
                    providerAvailable = await IsReasonixAvailableAsync();
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

        private static string BuildWslLaunchCommand(string wslPath, string providerCommand)
        {
            return $"wsl bash -lic \"cd {QuoteForBash(wslPath)} && {providerCommand}\"";
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
                    baseCommand = $"{baseCommand} --resume {QuoteProviderArgument(resumeArg, isWsl)}";
                }
            }

            // Apply the TUI rendering preference via environment variables so the launched
            // Claude Code CLI starts directly in the chosen renderer. These must wrap the final
            // command (env assignment precedes the executable). cmd.exe uses "set X=Y && cmd";
            // WSL bash uses the "X=Y cmd" prefix-assignment form.
            //
            // Fullscreen rendering enables Claude Code's own mouse capture (terminal mouse
            // tracking). Inside the embedded terminal — where the extension already runs a global
            // mouse hook and continuously re-asserts focus — that mouse tracking emits a steady
            // burst of mouse-input escape sequences that Claude Code's fast-input heuristic
            // collapses into bracketed-paste blocks, flooding the prompt with
            // "[Pasted text #N +NNNN lines]" on launch and on every restart (issue #92). The
            // extension already provides mouse zoom, click-to-focus, and paste, so CLAUDE_CODE_DISABLE_MOUSE
            // turns off Claude's mouse capture while keeping the flicker-free rendering.
            var tuiEnvVars = new System.Collections.Generic.List<string>();
            if (_settings?.ClaudeTuiFullscreen == true)
            {
                tuiEnvVars.Add("CLAUDE_CODE_NO_FLICKER=1");
                tuiEnvVars.Add("CLAUDE_CODE_DISABLE_MOUSE=1");
            }
            else if (_settings?.ClaudeTuiFullscreen == false)
            {
                tuiEnvVars.Add("CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN=1");
            }

            if (tuiEnvVars.Count > 0)
            {
                baseCommand = isWsl
                    ? $"{string.Join(" ", tuiEnvVars)} {baseCommand}"
                    : $"{string.Join(" && ", tuiEnvVars.ConvertAll(v => "set " + v))} && {baseCommand}";
            }

            return baseCommand;
        }

        private static string QuoteProviderArgument(string value, bool isWsl)
        {
            return isWsl ? QuoteForBash(value) : QuoteForWindowsCommandArgument(value);
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
