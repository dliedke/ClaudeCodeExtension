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
using System.Windows;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Terminal Communication

        /// <summary>
        /// Sends text to the embedded terminal by copying to clipboard and simulating paste
        /// </summary>
        /// <param name="text">The text to send to the terminal</param>
        private void SendTextToTerminal(string text)
        {
            try
            {
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    // Copy text to clipboard
                    Clipboard.SetText(text);

                    // Set focus to terminal window
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);

                    System.Threading.Thread.Sleep(200);

                    // Right-click to paste in CMD window
                    RightClickTerminalCenter();

                    System.Threading.Thread.Sleep(1000);

                    // Send Enter key to execute the command
                    SendEnterKey();
                }
                else
                {
                    MessageBox.Show("Terminal is not available. Please restart the terminal.",
                                  "Terminal Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending text to terminal: {ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Simulates a right-click at the specified screen coordinates
        /// </summary>
        /// <param name="x">Screen X coordinate</param>
        /// <param name="y">Screen Y coordinate</param>
        private void SendRightClick(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        /// <summary>
        /// Right-clicks on the center of the terminal window
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
        /// Sends the Enter key to the terminal window
        /// Uses different methods depending on the provider (Codex vs Claude Code)
        /// </summary>
        private void SendEnterKey()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // Check if we're using Codex
                bool isCodex = _settings?.SelectedProvider == AiProvider.Codex;

                if (isCodex)
                {
                    // For Codex, use KEYDOWN/KEYUP approach
                    SendEnterKeyDownUp();
                }
                else
                {
                    // For Claude Code, use single WM_CHAR
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