/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Resource cleanup and temporary file management
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl 
    { 
        #region Temporary Directory Fields

        /// <summary>
        /// Session-specific temporary directory for storing pasted images
        /// </summary>
        private string tempImageDirectory;

        #endregion

        #region Temporary Directory Initialization

        /// <summary>
        /// Initializes the temporary directory for storing pasted images
        /// Cleans up any existing ClaudeCodeVS temp directories first
        /// </summary>
        private void InitializeTempDirectory()
        {
            try
            {
                string sessionRootPath = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session");
                tempImageDirectory = Path.Combine(sessionRootPath, Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempImageDirectory);

                // Cleanup old temp folders in the background so control construction does not stall the UI thread.
                _ = System.Threading.Tasks.Task.Run(() => CleanupClaudeCodeVSTempDirectories(tempImageDirectory));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating temp directory: {ex.Message}");
                // Fallback to a simpler path
                tempImageDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempImageDirectory);
            }
        }

        /// <summary>
        /// Cleans up all ClaudeCodeVS temporary directories from previous sessions
        /// </summary>
        /// <param name="currentSessionDirectory">Current live session directory to preserve</param>
        private void CleanupClaudeCodeVSTempDirectories(string currentSessionDirectory = null)
        {
            try
            {
                string tempPath = Path.GetTempPath();
                string currentSessionFullPath = string.IsNullOrEmpty(currentSessionDirectory)
                    ? null
                    : Path.GetFullPath(currentSessionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Clean up any legacy ClaudeCodeVS directories (pre-7.6, now consolidated into ClaudeCodeVS_Session)
                string claudeCodeVSPath = Path.Combine(tempPath, "ClaudeCodeVS");
                if (Directory.Exists(claudeCodeVSPath))
                {
                    Directory.Delete(claudeCodeVSPath, true);
                }

                // Clean up session directories
                string sessionPath = Path.Combine(tempPath, "ClaudeCodeVS_Session");
                if (Directory.Exists(sessionPath))
                {
                    foreach (string sessionDirectory in Directory.GetDirectories(sessionPath))
                    {
                        string fullSessionPath = Path.GetFullPath(sessionDirectory)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                        if (string.Equals(fullSessionPath, currentSessionFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        Directory.Delete(sessionDirectory, true);
                    }

                    foreach (string sessionFile in Directory.GetFiles(sessionPath))
                    {
                        File.Delete(sessionFile);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up ClaudeCodeVS temp directories: {ex.Message}");
                // Continue even if cleanup fails
            }
        }

        #endregion

        #region Unload and Cleanup

        /// <summary>
        /// Handles control unload event - keeps terminal alive for tab switches
        /// </summary>
        private void ClaudeCodeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Don't save during unload - settings should only be saved when user makes changes
            // NOTE: Don't cleanup terminal here - Unloaded fires during tab switches
            // Terminal cleanup only happens in Dispose() when VS is actually closing
        }

        /// <summary>
        /// Cleans up all resources including processes and temporary files.
        /// UI-bound work (event unsubscription, window closing) runs on the UI thread.
        /// Heavy work (process tree termination, temp directory deletion) is offloaded
        /// to a background thread to avoid blocking VS during shutdown.
        /// </summary>
        private void CleanupResources()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // Persist the latest UI state before tearing down the control.
                SaveSettings();

                // Cleanup diff tracking
                CleanupDiffTracking();

                // Unsubscribe from theme change events
                CleanupThemeEvents();

                // Unsubscribe from tool window frame notifications
                if (_toolWindow != null)
                {
                    _toolWindow.FrameShow -= OnToolWindowFrameShow;
                }

                // Uninstall the low-level mouse hook used for zoom tracking
                UninstallMouseHook();

                // Uninstall the low-level keyboard hook used for F5/Ctrl+F5 interception
                UninstallKeyboardHook();

                // Cleanup detached terminal window
                if (_isTerminalDetached && _detachedTerminalWindow != null)
                {
                    try
                    {
                        // Re-parent terminal back to main panel before killing
                        if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle) && terminalPanel != null)
                        {
                            SetParent(terminalHandle, terminalPanel.Handle);
                        }

                        // Unwire events
                        if (_detachedClosedSubscribed)
                        {
                            _detachedTerminalWindow.Closed -= OnDetachedWindowClosed;
                            _detachedClosedSubscribed = false;
                        }
                        if (_detachedVisibilitySubscribed)
                        {
                            _detachedTerminalWindow.VisibilityChanged -= OnDetachedVisibilityChanged;
                            _detachedVisibilitySubscribed = false;
                        }
                        if (_detachedTerminalPanel != null)
                        {
                            _detachedTerminalPanel.Resize -= DetachedPanel_Resize;
                        }

                        // Close the detached window frame
                        if (_detachedTerminalWindow.Frame is IVsWindowFrame windowFrame)
                        {
                            windowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                        }

                        _detachedTerminalPanel = null;
                        _detachedTerminalWindow = null;
                        _isTerminalDetached = false;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error cleaning up detached terminal: {ex.Message}");
                    }
                }

                // Capture process info while still on UI thread (Win32 calls require it)
                int terminalWindowProcessId = 0;
                bool isWindowsTerminal = false;
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    GetWindowThreadProcessId(terminalHandle, out uint terminalWindowPid);
                    terminalWindowProcessId = (int)terminalWindowPid;
                    isWindowsTerminal = IsWindowsTerminalProcess(terminalWindowProcessId);
                    PostMessage(terminalHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                int cmdProcessId = 0;
                Process cmdProcessRef = cmdProcess;
                if (cmdProcessRef != null)
                {
                    try
                    {
                        cmdProcessId = cmdProcessRef.Id;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited
                    }
                }
                cmdProcess = null;

                string tempDir = tempImageDirectory;

                // Clear attached images list
                attachedImagePaths?.Clear();

                // Offload heavy process termination and temp directory cleanup to background thread
                int currentVsProcessId = Process.GetCurrentProcess().Id;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var terminatedProcessIds = new System.Collections.Generic.HashSet<int>();

                        if (cmdProcessId > 0)
                        {
                            try
                            {
                                TryTerminateProcessTree(cmdProcessId, terminatedProcessIds);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error terminating terminal launcher process tree: {ex.Message}");
                            }
                        }

                        if (cmdProcessRef != null)
                        {
                            try
                            {
                                cmdProcessRef.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error disposing terminal launcher process: {ex.Message}");
                            }
                        }

                        // Skip killing the WindowsTerminal.exe process tree — it is a shared
                        // host for ALL WT windows. WM_CLOSE (sent above) closes only our window.
                        if (terminalWindowProcessId > 0 &&
                            terminalWindowProcessId != currentVsProcessId &&
                            !isWindowsTerminal)
                        {
                            TryTerminateProcessTree(terminalWindowProcessId, terminatedProcessIds);
                        }

                        // Clean up temporary directory
                        if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                        {
                            try
                            {
                                Directory.Delete(tempDir, true);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error cleaning temp directory: {ex.Message}");
                                try
                                {
                                    foreach (string file in Directory.GetFiles(tempDir))
                                    {
                                        File.Delete(file);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error during background cleanup: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Kills a process and all its child processes using ToolHelp32 snapshots.
        /// ToolHelp32 is a kernel-level snapshot API (sub-millisecond) and avoids the
        /// significant overhead of WMI queries (which can take 1-5 seconds each).
        /// </summary>
        /// <param name="processId">The process ID to kill</param>
        private void KillProcessAndChildren(int processId)
        {
            try
            {
                // Use ToolHelp32 snapshot to find child processes (sub-ms, no WMI dependency)
                var childPids = GetChildProcessIds((uint)processId);
                foreach (uint childPid in childPids)
                {
                    try
                    {
                        // Recursively kill children of this child
                        KillProcessAndChildren((int)childPid);

                        // Kill the child process
                        using (var childProcess = Process.GetProcessById((int)childPid))
                        {
                            if (!childProcess.HasExited)
                            {
                                childProcess.Kill();
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process already exited
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error killing child process {childPid}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in KillProcessAndChildren: {ex.Message}");
            }
        }

        /// <summary>
        /// Implements IDisposable - disposes of all managed resources
        /// </summary>
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CleanupResources();
        }

        #endregion
    }
}
