/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Win32 API interop declarations and structures
 *
 * *******************************************************************************************************************/

using System;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Win32 Constants

        // SetWindowPos flags
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // ShowWindow commands
        private const int SW_SHOW = 5;
        private const int SW_HIDE = 0;

        // Window styles
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZE = 0x20000000;
        private const int WS_MAXIMIZE = 0x01000000;
        private const int WS_SYSMENU = 0x00080000;

        // Mouse event flags
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        // Window messages
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;

        // Virtual key codes
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;

        // Input type constants
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        #endregion

        #region Win32 Structures

        /// <summary>
        /// Rectangle structure for window coordinates
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// Input structure for SendInput
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        /// <summary>
        /// Union for different input types
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        /// <summary>
        /// Keyboard input structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        #endregion

        #region Win32 API Declarations - Window Management

        /// <summary>
        /// Sets the parent window for a child window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        /// <summary>
        /// Changes the size and position of a window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>
        /// Shows or hides a window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// Determines whether the specified window handle identifies an existing window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>
        /// Determines the visibility state of the specified window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// Changes an attribute of the specified window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>
        /// Retrieves information about the specified window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        /// <summary>
        /// Retrieves the dimensions of the bounding rectangle of a window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        #endregion

        #region Win32 API Declarations - Window Enumeration

        /// <summary>
        /// Delegate for window enumeration callback
        /// </summary>
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// Enumerates all top-level windows
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>
        /// Retrieves the process identifier of the thread that created the window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        #endregion

        #region Win32 API Declarations - Input and Focus

        /// <summary>
        /// Sets keyboard focus to the specified window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool SetFocus(IntPtr hWnd);

        /// <summary>
        /// Brings the window to the foreground and activates it
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Synthesizes keystrokes, mouse motions, and button clicks
        /// </summary>
        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// Posts a message to the message queue of a window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Synthesizes a keystroke
        /// </summary>
        [DllImport("user32.dll")]
        private static extern void keybd_event(int bVk, int bScan, uint dwFlags, UIntPtr dwExtraInfo);

        #endregion

        #region Win32 API Declarations - Mouse and Cursor

        /// <summary>
        /// Moves the cursor to the specified screen coordinates
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        /// <summary>
        /// Synthesizes mouse motion and button clicks
        /// </summary>
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        #endregion

        #region Win32 API Declarations - GDI

        /// <summary>
        /// Deletes a logical pen, brush, font, bitmap, region, or palette
        /// </summary>
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        #endregion
    }
}