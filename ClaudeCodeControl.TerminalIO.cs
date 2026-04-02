/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Terminal input/output communication - sending text and keyboard events
 *
 * *******************************************************************************************************************/

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Terminal Communication

        /// <summary>
        /// Timeout for clipboard operations in milliseconds
        /// </summary>
        private const int ClipboardTimeoutMs = 2000;

        /// <summary>
        /// Maximum number of retry attempts for clipboard operations
        /// </summary>
        private const int ClipboardMaxRetries = 10;

        /// <summary>
        /// Delay between clipboard retry attempts in milliseconds
        /// </summary>
        private const int ClipboardRetryDelayMs = 100;

        /// <summary>
        /// Sends text to the embedded terminal by copying to clipboard and simulating paste
        /// Preserves the original clipboard content and restores it after sending
        /// This is the synchronous wrapper for backward compatibility
        /// </summary>
        /// <param name="text">The text to send to the terminal</param>
        private void SendTextToTerminal(string text)
        {
            // Fire and forget with error handling - the async version handles the actual work
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await SendTextToTerminalAsync(text);
            });
        }

        /// <summary>
        /// Sends text to the embedded terminal asynchronously
        /// Preserves the original clipboard content and restores it after sending
        /// </summary>
        /// <param name="text">The text to send to the terminal</param>
        private async Task SendTextToTerminalAsync(string text)
        {
            // Dictionary to store all original clipboard formats and their data
            System.Collections.Generic.Dictionary<string, object> originalClipboardData = null;

            try
            {
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    // Make sure we're on the UI thread for clipboard operations
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Save the current clipboard content before modifying it
                    originalClipboardData = await ClipboardRetryAsync(() => SaveClipboardContent());

                    // Clear clipboard immediately so the deselect right-click below won't paste old content
                    await ClipboardRetryAsync(() => Clipboard.Clear());
                    await Task.Delay(50);

                    // If terminal is detached, ensure the detached window tab is visible
                    // (auto-open changes may have activated the diff viewer tab instead)
                    if (_isTerminalDetached && _detachedTerminalWindow?.Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame detachedFrame)
                    {
                        detachedFrame.Show();
                        await Task.Delay(200);
                    }

                    // Set focus to terminal window
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);

                    await Task.Delay(500); // Reduced from 700ms

                    // For Command Prompt (conhost): right-click first to cancel any active text selection.
                    // If text is selected, right-click copies it to clipboard and deselects.
                    // If no text is selected, right-click pastes from clipboard (which is empty, so harmless).
                    bool isCommandPrompt = _wtTabBarHeight == 0
                                           && _currentRunningProvider != AiProvider.OpenCode;
                    if (isCommandPrompt)
                    {
                        await RightClickTerminalCenterAsync();
                        await Task.Delay(300);
                    }

                    // Now set the clipboard to the prompt text (after deselect right-click which may overwrite clipboard)
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await ClipboardRetryAsync(() => Clipboard.Clear());
                    await Task.Delay(50);
                    await ClipboardRetryAsync(() => Clipboard.SetText(text));

                    // Re-focus terminal after clipboard operations
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);
                    await Task.Delay(100);

                    // Paste text into the terminal
                    // Windows Terminal: use Ctrl+Shift+V (right-click opens context menu instead of pasting)
                    // OpenCode: use Shift+Right-click
                    // Others (Command Prompt): use right-click
                    if (_wtTabBarHeight > 0)
                    {
                        await PasteViaCtrlShiftVAsync();
                        await Task.Delay(500);
                    }
                    else if (_currentRunningProvider == AiProvider.OpenCode)
                    {
                        await ShiftRightClickTerminalCenterAsync();
                        await Task.Delay(800);
                    }
                    else
                    {
                        // Right-click to paste the clipboard content
                        await RightClickTerminalCenterAsync();
                        await Task.Delay(800);
                    }

                    // Send Enter key to execute the command
                    SendEnterKey();
                }
                else
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show("Terminal is not available. Please restart the terminal.",
                                  "Terminal Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Error sending text to terminal: {ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Restore the original clipboard content on UI thread
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await Task.Delay(100); // Small delay to ensure paste completed
                    await ClipboardRetryAsync(() => RestoreClipboardContent(originalClipboardData));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error restoring clipboard: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Executes a clipboard operation with automatic retry logic
        /// Handles CLIPBRD_E_CANT_OPEN (0x800401D0) errors that occur when another application holds the clipboard
        /// </summary>
        /// <param name="action">The clipboard action to execute</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        private async Task ClipboardRetryAsync(Action action, int maxRetries = ClipboardMaxRetries, int retryDelayMs = ClipboardRetryDelayMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                {
                    if (attempt == maxRetries)
                        throw;
                    await Task.Delay(retryDelayMs);
                }
            }
        }

        /// <summary>
        /// Executes a clipboard operation with automatic retry logic, returning a value
        /// Handles CLIPBRD_E_CANT_OPEN (0x800401D0) errors that occur when another application holds the clipboard
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="func">The clipboard function to execute</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        /// <returns>The result of the clipboard operation</returns>
        private async Task<T> ClipboardRetryAsync<T>(Func<T> func, int maxRetries = ClipboardMaxRetries, int retryDelayMs = ClipboardRetryDelayMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return func();
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                {
                    if (attempt == maxRetries)
                        throw;
                    await Task.Delay(retryDelayMs);
                }
            }
            return default; // Should never reach here
        }

        /// <summary>
        /// Executes a clipboard operation with synchronous retry logic
        /// Handles CLIPBRD_E_CANT_OPEN (0x800401D0) errors that occur when another application holds the clipboard
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="func">The clipboard function to execute</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        /// <returns>The result of the clipboard operation</returns>
        private T ClipboardRetrySync<T>(Func<T> func, int maxRetries = ClipboardMaxRetries, int retryDelayMs = ClipboardRetryDelayMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return func();
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                {
                    if (attempt == maxRetries)
                        throw;
                    Thread.Sleep(retryDelayMs);
                }
            }
            return default; // Should never reach here
        }

        /// <summary>
        /// Saves all clipboard content formats for later restoration
        /// Preserves all formats including Office-specific formats (Excel, Word, etc.)
        /// </summary>
        /// <returns>Dictionary of format names to data objects, or null if clipboard is empty</returns>
        private System.Collections.Generic.Dictionary<string, object> SaveClipboardContent()
        {
            try
            {
                IDataObject dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                    return null;

                string[] formats = dataObject.GetFormats();
                if (formats == null || formats.Length == 0)
                    return null;

                var savedData = new System.Collections.Generic.Dictionary<string, object>();

                foreach (string format in formats)
                {
                    try
                    {
                        // Skip formats that can cause issues
                        if (format == "EnhancedMetafile" ||
                            format == "MetaFilePict" ||
                            format == "DeviceIndependentBitmap" ||
                            format == "System.Drawing.Bitmap" ||
                            format.StartsWith("Object Descriptor") ||
                            format.StartsWith("Link Source") ||
                            format.StartsWith("Ole Private Data"))
                            continue;

                        object data = dataObject.GetData(format);
                        if (data != null)
                        {
                            // For MemoryStream, we need to copy it as the original may be disposed
                            if (data is System.IO.MemoryStream ms)
                            {
                                try
                                {
                                    if (ms.CanRead && ms.CanSeek)
                                    {
                                        var copy = new System.IO.MemoryStream();
                                        ms.Position = 0;
                                        ms.CopyTo(copy);
                                        copy.Position = 0;
                                        savedData[format] = copy;
                                    }
                                }
                                catch
                                {
                                    // Skip streams that can't be copied
                                }
                            }
                            // For other Stream types, skip them as they can cause issues
                            else if (data is System.IO.Stream)
                            {
                                continue;
                            }
                            // Save primitive types and strings directly
                            else if (data is string || data is string[] || data.GetType().IsPrimitive)
                            {
                                savedData[format] = data;
                            }
                            // For byte arrays, make a copy
                            else if (data is byte[] bytes)
                            {
                                var copy = new byte[bytes.Length];
                                Array.Copy(bytes, copy, bytes.Length);
                                savedData[format] = copy;
                            }
                        }
                    }
                    catch
                    {
                        // Skip formats that can't be read
                    }
                }

                return savedData.Count > 0 ? savedData : null;
            }
            catch
            {
                // Silently fail if we can't access clipboard
                return null;
            }
        }

        /// <summary>
        /// Restores previously saved clipboard content with all formats
        /// This preserves Office application data (Excel cells, Word content, etc.)
        /// </summary>
        /// <param name="savedData">Dictionary of format names to data objects</param>
        private void RestoreClipboardContent(System.Collections.Generic.Dictionary<string, object> savedData)
        {
            try
            {
                if (savedData == null || savedData.Count == 0)
                {
                    // Original clipboard was empty, clear it
                    Clipboard.Clear();
                    return;
                }

                // Create a new DataObject and add all saved formats
                DataObject newDataObject = new DataObject();

                foreach (var kvp in savedData)
                {
                    try
                    {
                        // Reset stream position if it's a stream
                        if (kvp.Value is System.IO.MemoryStream ms)
                        {
                            ms.Position = 0;
                        }
                        newDataObject.SetData(kvp.Key, kvp.Value);
                    }
                    catch
                    {
                        // Skip formats that can't be set
                    }
                }

                Clipboard.SetDataObject(newDataObject, true);
            }
            catch
            {
                // Silently fail if we can't restore clipboard
            }
        }

        /// <summary>
        /// Pastes text using Ctrl+Shift+V keyboard shortcut (for Windows Terminal).
        /// Windows Terminal right-click opens a context menu instead of pasting directly,
        /// so we use the keyboard shortcut which always pastes reliably.
        /// </summary>
        private async Task PasteViaCtrlShiftVAsync()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle)) return;

            SetForegroundWindow(terminalHandle);
            SetFocus(terminalHandle);
            await Task.Delay(100);

            // Ctrl+Shift+V
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            await Task.Delay(30);
            keybd_event(0x56, 0, 0, UIntPtr.Zero); // VK_V = 0x56
            await Task.Delay(30);
            keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>
        /// Simulates a right-click at the specified screen coordinates (async version)
        /// </summary>
        /// <param name="x">Screen X coordinate</param>
        /// <param name="y">Screen Y coordinate</param>
        private async Task SendRightClickAsync(int x, int y)
        {
            SetCursorPos(x, y);
            await Task.Delay(30); // Reduced from 50ms
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(30); // Reduced from 50ms
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        /// <summary>
        /// Simulates a right-click at the specified screen coordinates (sync version for backward compat)
        /// </summary>
        private void SendRightClick(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        /// <summary>
        /// Right-clicks on the center of the terminal window (async version)
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private async Task RightClickTerminalCenterAsync()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                // The window is positioned at Y = -_wtTabBarHeight, so add it back to get visible center
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                await SendRightClickAsync(centerX, centerY);
            }
        }

        /// <summary>
        /// Right-clicks on the center of the terminal window (sync version for backward compat)
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private void RightClickTerminalCenter()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                SendRightClick(centerX, centerY);
            }
        }

        /// <summary>
        /// Performs SHIFT+Right-click on the center of the terminal window (async version)
        /// Required for Open Code to paste text properly
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private async Task ShiftRightClickTerminalCenterAsync()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                // Move cursor to center
                SetCursorPos(centerX, centerY);
                await Task.Delay(30); // Reduced from 50ms

                // Hold SHIFT key down
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                await Task.Delay(30); // Reduced from 50ms

                // Perform right-click
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(30); // Reduced from 50ms
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(30); // Reduced from 50ms

                // Release SHIFT key
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        /// <summary>
        /// Performs SHIFT+Right-click on the center of the terminal window (sync version)
        /// Required for Open Code to paste text properly
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private void ShiftRightClickTerminalCenter()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                // Move cursor to center
                SetCursorPos(centerX, centerY);
                System.Threading.Thread.Sleep(30);

                // Hold SHIFT key down
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);

                // Perform right-click
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);

                // Release SHIFT key
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        /// <summary>
        /// Sends the Enter key to the terminal window
        /// Uses different methods depending on the provider (WSL-based vs Windows-based)
        /// </summary>
        private void SendEnterKey()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // Check CURRENTLY RUNNING provider (not the next one being set)
                bool isClaudeCodeWSL = _currentRunningProvider == AiProvider.ClaudeCodeWSL;

                // Check if we're using other WSL-based providers (Codex WSL, CursorAgent, Windsurf)
                bool isOtherWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                         _currentRunningProvider == AiProvider.CursorAgent ||
                                         _currentRunningProvider == AiProvider.Windsurf;

                bool isCodexNative = _currentRunningProvider == AiProvider.CodexNative;

                bool isQwenCode = _currentRunningProvider == AiProvider.QwenCode;
                bool isOpenCode = _currentRunningProvider == AiProvider.OpenCode;

                // Check if Windows Terminal is active (tab bar height > 0)
                bool isWindowsTerminal = _wtTabBarHeight > 0;

                if (isWindowsTerminal)
                {
                    // For Windows Terminal, use KEYDOWN/KEYUP approach (works better with embedded window)
                    SendEnterKeyDownUp();
                }
                else if (isClaudeCodeWSL)
                {
                    // For Claude Code (WSL), send Enter using WM_CHAR
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
                else if (isCodexNative)
                {
                    // For Codex (Windows native), use KEYDOWN/KEYUP approach (Codex requires double Enter)
                    SendEnterKeyDownUp();
                }
                else if (isOtherWSLProvider)
                {
                    // For other WSL-based providers (Codex, CursorAgent), use KEYDOWN/KEYUP approach
                    SendEnterKeyDownUp();
                }
                else if (isQwenCode)
                {
                    // For Qwen Code, use single WM_CHAR (similar to Claude Code)
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
                else if (isOpenCode)
                {
                    // For Open Code, use single WM_CHAR (similar to Claude Code)
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
                else
                {
                    // For Windows-based providers (Claude Code), use single WM_CHAR
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// Sends Enter key using KEYDOWN/KEYUP messages (required for Codex and Windows Terminal)
        /// For Windows Terminal, uses keybd_event for better compatibility with embedded windows
        /// Sends the key twice to ensure submission
        /// </summary>
        private void SendEnterKeyDownUp()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // For Windows Terminal, use keybd_event (works better with embedded windows)
                if (_wtTabBarHeight > 0)
                {
                    // First Enter attempt
                    keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(100);

                    // Second Enter attempt to ensure submission
                    keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                else
                {
                    // For other providers (Codex, etc), use PostMessage
                    // First Enter attempt
                    PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
                    System.Threading.Thread.Sleep(100);

                    // Second Enter attempt to ensure submission
                    PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
            }
        }

        #endregion
    }
}