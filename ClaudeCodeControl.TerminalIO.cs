/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
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
                    originalClipboardData = SaveClipboardContent();

                    // Clear clipboard before copying new text to prevent stale content
                    Clipboard.Clear();
                    await Task.Delay(50);

                    // Copy text to clipboard
                    Clipboard.SetText(text);

                    // Set focus to terminal window
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);

                    await Task.Delay(500); // Reduced from 700ms

                    // Right-click to paste in CMD window
                    // For OpenCode, use SHIFT+Right-click instead
                    if (_currentRunningProvider == AiProvider.OpenCode)
                    {
                        await ShiftRightClickTerminalCenterAsync();
                    }
                    else
                    {
                        await RightClickTerminalCenterAsync();
                    }

                    await Task.Delay(800); // Reduced from 1000ms

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
                    RestoreClipboardContent(originalClipboardData);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error restoring clipboard: {ex.Message}");
                }
            }
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
        /// </summary>
        private async Task RightClickTerminalCenterAsync()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;
                await SendRightClickAsync(centerX, centerY);
            }
        }

        /// <summary>
        /// Right-clicks on the center of the terminal window (sync version for backward compat)
        /// </summary>
        private void RightClickTerminalCenter()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;
                SendRightClick(centerX, centerY);
            }
        }

        /// <summary>
        /// Performs SHIFT+Right-click on the center of the terminal window (async version)
        /// Required for Open Code to paste text properly
        /// </summary>
        private async Task ShiftRightClickTerminalCenterAsync()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

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
        /// </summary>
        private void ShiftRightClickTerminalCenter()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

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

                // Check if we're using other WSL-based providers (Codex, CursorAgent)
                bool isOtherWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                         _currentRunningProvider == AiProvider.CursorAgent;

                bool isQwenCode = _currentRunningProvider == AiProvider.QwenCode;
                bool isOpenCode = _currentRunningProvider == AiProvider.OpenCode;

                if (isClaudeCodeWSL)
                {
                    // For Claude Code (WSL), send Enter using WM_CHAR
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
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
        /// Sends Enter key using KEYDOWN/KEYUP messages (required for Codex)
        /// Sends the key twice to ensure submission
        /// </summary>
        private void SendEnterKeyDownUp()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
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

        #endregion
    }
}