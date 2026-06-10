/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
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
        private const uint SWP_FRAMECHANGED = 0x0020;

        // ShowWindow commands
        private const int SW_SHOW = 5;
        private const int SW_HIDE = 0;

        // Window styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZE = 0x20000000;
        private const int WS_MAXIMIZE = 0x01000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_SYSMENU = 0x00080000;

        // Mouse event flags
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        // Window messages
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_MOUSEWHEEL = 0x020A;

        // GetAncestor flags
        private const uint GA_ROOT = 2;

        // RedrawWindow flags
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_FRAME = 0x0400;

        // Virtual key codes
        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_SPACE = 0x20;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_C = 0x43;
        private const int VK_D = 0x44;
        private const int VK_F5 = 0x74;

        // Input type constants
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

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
        [DllImport("user32.dll", SetLastError = true)]
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
        /// Retrieves the dots per inch (DPI) of a window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>
        /// Retrieves the name of the class to which the specified window belongs
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

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

        /// <summary>
        /// Invalidates the client area of a window
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        /// <summary>
        /// Updates the client area of a window by sending a paint message if needed
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateWindow(IntPtr hWnd);

        /// <summary>
        /// Invalidates or validates the specified portions of a window
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

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

        /// <summary>
        /// Retrieves the handle to the window that currently has the clipboard open (zero if none)
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();

        #endregion

        #region Win32 API Declarations - Input and Focus

        /// <summary>
        /// Sets keyboard focus to the specified window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool SetFocus(IntPtr hWnd);

        /// <summary>
        /// Retrieves the handle to the window that has the keyboard focus within the calling thread's
        /// input queue (which includes the embedded terminal, whose input queue is joined to ours via SetParent).
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>
        /// Brings the window to the foreground and activates it
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Retrieves a handle to the foreground window (the window with which the user is currently working)
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Retrieves the handle to the ancestor of the specified window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        /// <summary>
        /// Brings the specified window to the top of the Z order without activating it
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        /// <summary>
        /// Attaches or detaches the input processing mechanism of one thread to another.
        /// Used to bypass focus-stealing protection: when our thread shares input state with
        /// the current foreground window's thread, SetForegroundWindow is treated as
        /// originating from the foreground app and is no longer denied by Windows.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        /// <summary>
        /// Returns the calling thread's ID. Paired with AttachThreadInput.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

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
        /// Sends a message to a window and waits for it to be processed
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
        /// Sets the cursor shape; used to restore the cursor after Win32 "Hide pointer while typing"
        /// calls SetCursor(NULL) which WPF cannot counteract without a direct Win32 call.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        /// <summary>
        /// Loads a predefined system cursor (pass IntPtr.Zero for hInstance)
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        private const int IDC_IBEAM = 32513;

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

        #region Win32 Constants - Console

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint TMPF_TRUETYPE = 4;
        private const int FW_NORMAL = 400;

        #endregion

        #region Win32 Structures - Console Font

        /// <summary>
        /// Coordinate structure for console buffer
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        /// <summary>
        /// Extended console font information
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CONSOLE_FONT_INFOEX
        {
            public uint cbSize;
            public uint nFont;
            public COORD dwFontSize;
            public uint FontFamily;
            public uint FontWeight;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FaceName;
        }

        #endregion

        #region Win32 API Declarations - Console

        /// <summary>
        /// Attaches the calling process to the console of the specified process
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        /// <summary>
        /// Detaches the calling process from its console
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        /// <summary>
        /// Returns the window handle of the console attached to the calling process, or IntPtr.Zero when none
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        // ---- Console screen-buffer reading (used by the "On Agent Finish" idle detector) ----

        private const uint GENERIC_READ_CONSOLE = 0x80000000;
        private const uint GENERIC_WRITE_CONSOLE = 0x40000000;
        private const uint FILE_SHARE_READ_CONSOLE = 0x00000001;
        private const uint FILE_SHARE_WRITE_CONSOLE = 0x00000002;
        private const uint OPEN_EXISTING_CONSOLE = 3;

        /// <summary>
        /// Rectangle (character cells) used by CONSOLE_SCREEN_BUFFER_INFO for the visible window.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        /// <summary>
        /// Console screen buffer information (size, cursor position, visible window rectangle).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public ushort wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        /// <summary>
        /// Adds or removes a console control handler. Passing a null routine with add=true makes
        /// the calling process IGNORE Ctrl+C — used to shield VS while it is briefly attached to
        /// the agent's console (otherwise a user Ctrl+C in the agent could terminate VS).
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

        /// <summary>
        /// Synthesizes a tone on the default audio device. Unlike System.Media.SystemSounds (which
        /// plays the Windows sound-scheme event and is silent when that event is set to "None"),
        /// Beep is independent of the scheme, so the "On Agent Finish" chime is reliably audible.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Beep(uint dwFreq, uint dwDuration);

        /// <summary>
        /// Opens a handle to the attached console's active output buffer (via "CONOUT$").
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        /// <summary>
        /// Retrieves the size, cursor position, and visible window of a console screen buffer.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        /// <summary>
        /// Copies a number of characters from consecutive cells of a console screen buffer.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputCharacterW")]
        private static extern bool ReadConsoleOutputCharacter(
            IntPtr hConsoleOutput, [Out] char[] lpCharacter, uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

        /// <summary>
        /// Retrieves a handle to the specified standard device
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        /// <summary>
        /// Sets extended information about the current console font
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFOEX lpConsoleCurrentFontEx);

        /// <summary>
        /// Retrieves extended information about the current console font
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFOEX lpConsoleCurrentFontEx);

        #endregion

        #region Win32 API Declarations - Hooks

        /// <summary>
        /// Low-level keyboard hook identifier
        /// </summary>
        private const int WH_KEYBOARD_LL = 13;

        /// <summary>
        /// Low-level mouse hook identifier
        /// </summary>
        private const int WH_MOUSE_LL = 14;

        /// <summary>
        /// Point structure for low-level mouse hook
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        /// <summary>
        /// Low-level mouse hook data structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// Low-level keyboard hook data structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// GUI thread information structure for detecting which window has keyboard focus
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        /// <summary>
        /// Delegate for low-level keyboard hook callback
        /// </summary>
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Delegate for low-level mouse hook callback
        /// </summary>
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Installs an application-defined hook procedure into a hook chain (keyboard)
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        /// <summary>
        /// Installs an application-defined hook procedure into a hook chain (mouse)
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        /// <summary>
        /// Removes a hook procedure installed in a hook chain
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        /// <summary>
        /// Passes the hook information to the next hook procedure in the current hook chain
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Retrieves a module handle for the specified module
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>
        /// WM_QUIT message: posted to the dedicated hook thread to exit its message loop.
        /// </summary>
        private const uint WM_QUIT = 0x0012;

        /// <summary>
        /// Win32 MSG structure used by the dedicated hook thread's message loop.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        /// <summary>
        /// Retrieves a message from the calling thread's message queue (blocking).
        /// Used by the dedicated hook thread so its low-level hooks are serviced
        /// independently of the VS UI thread.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        /// <summary>
        /// Translates virtual-key messages into character messages.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        /// <summary>
        /// Dispatches a message to a window procedure.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        /// <summary>
        /// Posts a message to the message queue of the specified thread (used to signal
        /// the dedicated hook thread to exit via WM_QUIT).
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Determines whether a key is up or down at the time the function is called
        /// </summary>
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Retrieves information about the active window or a specified GUI thread
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        /// <summary>
        /// Determines whether a window is a child of a specified parent window
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        /// <summary>
        /// Retrieves a handle to the window at the specified screen point (topmost, hit-tested)
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        #endregion

        #region Win32 Structures - Process Snapshot

        /// <summary>
        /// Describes an entry from a list of processes in the system address space when taking a snapshot
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        #endregion

        #region Win32 API Declarations - Process Snapshot

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        /// <summary>
        /// Takes a snapshot of the specified processes, as well as the heaps, modules, and threads used by these processes
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        /// <summary>
        /// Retrieves information about the first process encountered in a system snapshot
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        /// <summary>
        /// Retrieves information about the next process recorded in a system snapshot
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        /// <summary>
        /// Closes an open object handle
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
    }
}
