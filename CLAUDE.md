# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

This is a **Visual Studio Extension (VSIX)** for Visual Studio 2022/2026 that provides seamless integration with multiple AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Qwen Code, Open Code, Windsurf) directly within the IDE. It embeds a terminal via Win32 interop and provides features like multi-line prompts, file attachments, prompt history, integrated diff viewer, and theme support.

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com)
- **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 10.4
- **Target Framework**: .NET Framework 4.7.2

---

## MANDATORY: Version & Documentation Updates

**IMPORTANT: Every development session that modifies code MUST include the following updates before finishing:**

1. **Bump Assembly Version** in `Properties/AssemblyInfo.cs`:
   - Update both `AssemblyVersion` and `AssemblyFileVersion` (e.g., `7.0.0.0` -> `7.1.0.0`)
2. **Bump Manifest Version** in `source.extension.vsixmanifest`:
   - Update the `Version` attribute in the `<Identity>` tag (e.g., `7.0` -> `7.1`)
3. **Update README.md**:
   - Add a new version entry at the top of the `## Version History` section describing what changed
   - Follow the existing format: `### Version X.Y` with bullet points describing changes

---

## Build & Test

- **MSBuild Path**: `'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe'`
- **Build (Release)**: `'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Release -v:minimal`
- **Build (Debug)**: `'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Debug -v:minimal`
- **Debug**: Press F5 in Visual Studio to launch experimental instance with `/rootsuffix Exp`
- **No automated tests**: Testing is done via F5 debugging in VS 2022/2026 experimental instance
- **Output**: VSIX package for distribution

## Version Files

| File | What to Update |
|------|---------------|
| `Properties/AssemblyInfo.cs` | `AssemblyVersion("X.Y.0.0")` and `AssemblyFileVersion("X.Y.0.0")` on last two lines |
| `source.extension.vsixmanifest` | `Version="X.Y"` in the `<Identity>` tag (line 4) |
| `README.md` | Add new `### Version X.Y` section after the `## Version History` heading |

---

## Project Structure

```
ClaudeCodeExtension/
├── ClaudeCodeExtension.sln              # Solution file
├── ClaudeCodeExtension.csproj           # Project file (.NET Framework 4.7.2)
├── source.extension.vsixmanifest        # VSIX manifest (version, metadata)
├── Properties/
│   └── AssemblyInfo.cs                  # Assembly version info
├── CLAUDE.md                            # This file
├── AGENTS.md                            # Agent build instructions
├── README.md                            # Full documentation & version history
├── LICENSE.txt                          # MIT License
├── app.ico / app.png                    # Extension icons
│
├── Core Control (partial classes of ClaudeCodeControl):
│   ├── ClaudeCodeControl.cs             # Core initialization & orchestration
│   ├── ClaudeCodeControl.Terminal.cs    # Terminal embedding (cmd.exe/wsl.exe), process init, F5 forwarding
│   ├── ClaudeCodeControl.ProviderManagement.cs  # AI provider detection & switching
│   ├── ClaudeCodeControl.TerminalIO.cs  # Terminal I/O, command execution
│   ├── ClaudeCodeControl.Diff.cs        # Diff view integration, git polling
│   ├── ClaudeCodeControl.UserInput.cs   # Keyboard input, button handlers
│   ├── ClaudeCodeControl.Workspace.cs   # Solution/workspace directory detection
│   ├── ClaudeCodeControl.ImageHandling.cs # Image paste & file attachments
│   ├── ClaudeCodeControl.Settings.cs    # Settings persistence (JSON)
│   ├── ClaudeCodeControl.Cleanup.cs     # Resource cleanup, temp dir management
│   ├── ClaudeCodeControl.Interop.cs     # Win32 API declarations (P/Invoke)
│   ├── ClaudeCodeControl.Theme.cs       # Dark/light theme support
│   └── ClaudeCodeControl.Detach.cs      # Terminal detach/attach to separate VS tab
│
├── UI:
│   ├── ClaudeCodeControl.xaml           # Main extension UI layout
│   ├── DiffViewerControl.xaml           # Diff viewer UI
│   ├── DiffViewerControl.xaml.cs        # Diff viewer logic (tree, search, zoom)
│   ├── ClaudeCodeToolWindow.cs          # Main tool window wrapper
│   ├── DiffViewerToolWindow.cs          # Diff viewer tool window wrapper
│   └── DetachedTerminalToolWindow.cs    # Detached terminal tool window wrapper
│
├── Diff Engine:
│   ├── Diff/DiffComputer.cs             # Diff computation using DiffPlex library
│   ├── Diff/FileChangeTracker.cs        # Git baseline tracking, change detection
│   └── Diff/ChangedFile.cs              # Data models (ChangeType, DiffLine, ChangedFile)
│
├── Models & Package:
│   ├── ClaudeCodeModels.cs              # Enums (AiProvider, ClaudeModel, WindsurfModel) & settings class
│   ├── ClaudeCodeExtensionPackage.cs    # VS package registration & menu commands
│   └── SolutionEventsHandler.cs         # Solution/project open events
```

---

## Code Style & Conventions

- **Language**: C# targeting .NET Framework 4.7.2
- **File Headers**: Every `.cs` file must include copyright header with author (Daniel Liedke), copyright year (2026), and proprietary usage notice
- **Namespaces**: `ClaudeCodeVS` for main controls and models, `ClaudeCodeExtension` for package class
- **Partial Classes**: Main control is split into 13 specialized partial class files (all `partial class ClaudeCodeControl`)
- **Naming**: PascalCase for public members, `_camelCase` with underscore for private fields, camelCase for locals
- **Error Handling**: try-catch with `Debug.WriteLine` for logging; `MessageBox` for user-facing errors
- **Thread Safety**: Use `ThreadHelper.ThrowIfNotOnUIThread()` and `JoinableTaskFactory.SwitchToMainThreadAsync()`
- **Settings**: Persist to JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json` using Newtonsoft.Json
- **Constants**: Use `const` for hardcoded strings, `static readonly` for computed values
- **Types**: Use C# built-in types (`string`, `bool`) over BCL types (`String`, `Boolean`)

### File Header Template

```csharp
/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: <description of file purpose>
 *
 * *******************************************************************************************************************/
```

---

## Architecture Deep Dive

### Core Control (ClaudeCodeControl - Partial Class)

The main UI control is split across 13 partial class files. All share the same `ClaudeCodeControl : UserControl, IDisposable` class in the `ClaudeCodeVS` namespace.

#### ClaudeCodeControl.cs — Core Initialization

- **Constructor**: Initializes XAML, temp directories, solution events, theme events, and lifecycle event handlers
- **Key fields**: `_toolWindow` (parent tool window ref), `_hasInitialized` (prevents re-init on tab switches)
- **`ClaudeCodeControl_Loaded()`**: Loads settings, applies them to UI, initializes terminal (only once)
- **`OnWorkspaceDirectoryChangedAsync()`**: Called when solution/project changes; restarts terminal with new working directory; schedules deferred terminal layout refresh on solution load events
- **Initialization guard**: The `_hasInitialized` flag prevents multiple initializations when switching tabs in VS

#### ClaudeCodeControl.Terminal.cs — Terminal Embedding

Manages the embedded cmd.exe/wsl.exe terminal via Win32 interop.

- **Key fields**: `terminalPanel` (WinForms Panel host), `cmdProcess` (Process), `terminalHandle` (IntPtr), `_currentRunningProvider`, `_wtExePath` (resolved full path to wt.exe), `_wtTabBarHeight` (WT tab bar pixel height, 0 for CMD), `_terminalLifecycleSemaphore` (serializes start/stop transitions), `_terminalStartupSessionId` (monotonic ID to discard stale startup work), `_keyboardHookHandle` / `_mouseHookHandle` (low-level hook handles), `_windowsTerminalSelectionPending` / `_windowsTerminalSelectionActive` (WT selection tracking state)
- **`StartEmbeddedTerminalAsync()`**: Core startup method
  - Acquires `_terminalLifecycleSemaphore` to prevent overlapping start/stop transitions
  - Calls `StopExistingTerminalAsync()` to cleanly shut down any running terminal (WM_CLOSE + recursive process tree kill)
  - Builds provider-specific command strings
  - Supports two terminal modes: **Command Prompt** (conhost.exe) and **Windows Terminal** (wt.exe), selected via `_settings.SelectedTerminalType`
  - For Windows Terminal: resolves `_wtExePath` via `IsWindowsTerminalAvailableAsync()`, launches `wt.exe --window new`, finds the new `CASCADIA_HOSTING_WINDOW_CLASS` window, embeds it with `WS_CHILD` style, calculates tab bar height for positioning
  - Uses `GetFreshPathFromRegistry()` to refresh PATH for detecting newly installed tools
  - Sets `VIRTUAL_TERMINAL_LEVEL=1` env var to enable ANSI/VT escape sequence rendering in conhost
  - Sets console font to "Cascadia Mono" via registry (`HKCU\Console\FaceName`) before starting conhost, restores original after
  - Hides window immediately via `SW_HIDE` to prevent blinking
  - Embeds into panel using `SetParent()`, applies window styles via `ApplyEmbeddedTerminalWindowStyle()`
  - Calls `SchedulePostStartupTerminalAdjustments()` for deferred zoom replay and layout stabilization
  - Updates `_currentRunningProvider` tracking
- **`StopExistingTerminalAsync()`**: Sends `WM_CLOSE` to the terminal window, then recursively terminates the process tree using `TryTerminateProcessTree()` with safeguards against terminating the VS process
- **`ApplyEmbeddedTerminalWindowStyle()`**: Centralized window style application; uses `WS_CHILD` for Windows Terminal (better embedding), classic style for conhost (preserves input/focus); also sets `WS_EX_TOOLWINDOW` and clears `WS_EX_APPWINDOW` on extended style to hide the terminal from the Windows taskbar
- **`SchedulePostStartupTerminalAdjustments()`**: Fire-and-forget deferred startup — runs `StabilizeEmbeddedTerminalLayoutAsync()` for delayed resize passes, then replays saved zoom delta and WT zoom-out
- **`SchedulePostSolutionLoadTerminalRefresh()`**: Deferred resize/repaint passes (200ms, 500ms, 1000ms) after solution load/close events to fix visual corruption caused by VS re-layout during solution transitions
- **`ScheduleManualZoomRefresh()`**: Debounced repaint passes after manual Ctrl+Scroll zoom to eliminate stale black regions
- **`RefreshEmbeddedTerminalWindow()`**: Forces repaint of both the WinForms panel and the embedded terminal via `InvalidateRect`/`RedrawWindow`/`UpdateWindow`
- **`ApplyWindowsTerminalZoomOutAsync()`**: Sends Ctrl+Minus 3 times via `keybd_event` after WT embed for better visibility
- **F5 Key Forwarding**: Low-level keyboard hook (`WH_KEYBOARD_LL`) intercepts F5/Ctrl+F5/Shift+F5 when the terminal has focus and forwards them as VS debug commands (`Debug.Start`, `Debug.StartWithoutDebugging`, `Debug.StopDebugging`) via DTE
  - `InstallLowLevelKeyboardHook()`: Installs the hook using `SetWindowsHookEx`
  - `LowLevelKeyboardHookCallback()`: Checks `IsTerminalFocused()` via `GetGUIThreadInfo`, consumes the keystroke by returning `1`
  - `IsTerminalFocused()`: Uses `GUITHREADINFO` + `IsChild()` to detect if the focused window belongs to the terminal
- **Low-level mouse hook** (`WH_MOUSE_LL`): Tracks Ctrl+Scroll over the terminal panel to persist zoom delta and enables SHIFT+drag selection for Windows Terminal
  - `LowLevelMouseHookCallback()`: Handles `WM_MOUSEWHEEL` (zoom tracking), `WM_LBUTTONDOWN`/`WM_MOUSEMOVE`/`WM_LBUTTONUP` (selection tracking)
  - `BeginWindowsTerminalSelectionTracking()` / `UpdateWindowsTerminalSelectionTracking()` / `ResetWindowsTerminalSelectionTracking()`: Converts plain left-drag into SHIFT+drag so WT enters selection mode even when the TUI has mouse reporting enabled
- **ToolHelp32 process discovery**: `GetChildProcessIds()` uses `CreateToolhelp32Snapshot` (kernel-level, sub-ms) to find child PIDs for conhost window handle lookup, avoiding WMI dependency

**Terminal command patterns**:
```
Windows native: cmd.exe /k chcp 65001 >nul && cd /d "{dir}" && ping localhost -n 3 >nul && cls && {command}
WSL providers:  cmd.exe /k chcp 65001 >nul && cls && wsl bash -ic "cd {wslPath} && {command}"
```

**Path conversion** (`ConvertToWslPath()`):
- `\\wsl.localhost\<distro>\path` → `/path`
- `\\wsl$\<distro>\path` → `/path`
- `C:\Users\...` → `/mnt/c/Users/...`

**Provider executable detection**:
- `GetClaudeCommand()`: Prioritizes native .exe over NPM installation; appends `--dangerously-skip-permissions` if `_settings.ClaudeDangerouslySkipPermissions == true`
- `GetCodexCommand()`: Returns `codex`; appends `--ask-for-approval never` if `_settings.CodexFullAuto == true`
- `GetCursorAgentCommand()`: Checks `%LOCALAPPDATA%\cursor-agent\` first, then PATH
- `GetWindsurfCommand()`: Returns `devin`; appends `--permission-mode dangerous` if `_settings.WindsurfDangerousMode == true`
- **Flag persistence**: `GetClaudeCommand()`, `GetCodexCommand()`, and `GetWindsurfCommand()` are called in every restart path (workspace change, manual restart, provider switch, VS startup), so the flags are always applied when settings are saved

#### ClaudeCodeControl.ProviderManagement.cs — Provider Detection

Detects and validates availability of all 9 AI providers.

- **Caching**: `_providerCache` Dictionary with 5-minute TTL (`ProviderCacheExpiry = 300000ms`)
- **Detection methods**: All use `cmd.exe /c where {command}` (Windows) or `wsl bash -ic "which {command}"` (WSL)
- **Windows Terminal detection**: `IsWindowsTerminalAvailableAsync()` uses `where wt.exe` with fresh PATH from registry; stores resolved full path in `_wtExePath` for reliable launch
- **WSL retry logic**: `IsClaudeCodeWSLAvailableAsync()` retries 2 times with 3s/5s timeouts for cold WSL boot
- **Notification flags**: Static booleans (`_claudeNotificationShown`, etc.) ensure install instructions show only once per VS session
- **`ClearProviderCache()`**: Should be called when user actions might change availability (e.g., after update)
- **`UpdateProviderSelection()`**: Shows `ModelDropdownButton` for both Claude and Windsurf providers; hides it for all other providers
- **`ModelContextMenu_Opened()`**: Dynamically shows/hides Claude-specific items (Opus/Sonnet/Haiku, effort levels, Change Account, Set Language) vs Windsurf-specific items (model list, Show Usage URL) depending on the active provider
- **`ShowUsageMenuItem_Click()`**: Sends `/usage` command directly to Claude Code terminal
- **`WindsurfShowUsageMenuItem_Click()`**: Opens `https://windsurf.com/subscription/usage` in the default browser via `Process.Start()`
- **Windsurf model click handlers** (`WindsurfClaudeOpusMenuItem_Click`, `WindsurfClaudeSonnetMenuItem_Click`, `WindsurfCodexMenuItem_Click`, `WindsurfGeminiProMenuItem_Click`): Save `SelectedWindsurfModel` setting and send the corresponding `/model <name>` command to the terminal (`/model opus`, `/model sonnet`, `/model codex`, `/model gemini pro`)
- **`UpdateModelSelection()`**: Updates checkmarks for both Claude models and Windsurf models
- **`SetLanguageMenuItem_Click()`**: Sends `/config` then navigates TUI via `PostMessage` (conhost) or `keybd_event` (Windows Terminal) — types "language", Down, Space
- **`SetTerminalTypeMenuItem_Click()`**: WPF dialog for selecting Command Prompt vs Windows Terminal; checks WT availability; restarts terminal on change
- **`ShowWorkingDirectoryInputDialog()`**: WPF dialog built programmatically; reads VS theme colors via `VsBrushes` (background, foreground, textbox, buttons) and applies them so the dialog matches the current dark/light VS theme; falls back to `SystemColors` if theme read fails; validates path in real-time (red text when directory doesn't exist)

#### ClaudeCodeControl.TerminalIO.cs — Terminal I/O

Sends text and keystrokes to the embedded terminal.

- **`SendTextToTerminalAsync()`**: Main I/O method
  1. Saves entire clipboard state (all formats including Office data, MemoryStreams)
  2. Sets clipboard to target text
  3. Right-clicks terminal center to paste (Shift+right-click for OpenCode)
  4. Sends Enter key via `WM_CHAR` or `KEYDOWN`/`KEYUP`
  5. Restores original clipboard content
  - All clipboard operations use retry helpers to handle `CLIPBRD_E_CANT_OPEN` contention
- **Clipboard retry helpers**: `ClipboardRetryAsync()`, `ClipboardRetryAsync<T>()`, `ClipboardRetrySync<T>()` — retry up to `ClipboardMaxRetries` (10) times with `ClipboardRetryDelayMs` (100ms) delay, catching only `COMException` 0x800401D0
- **`SendEnterKey()`**: Provider-specific behavior:
  - Claude/QwenCode/OpenCode: Single `WM_CHAR` with `VK_RETURN`
  - WSL providers: `KEYDOWN`/`KEYUP` approach
  - Codex (WSL): Enter sent twice via `KEYDOWN`/`KEYUP` (required by Codex CLI)
  - Codex (Windows native): `KEYDOWN`/`KEYUP` approach, Enter sent twice
- **`SendCtrlC()`**: Uses multiple approaches — `keybd_event`, `SendInput`, and `PostMessage`
- **`ClipboardTimeoutMs`** = 2000ms, **`ClipboardMaxRetries`** = 10, **`ClipboardRetryDelayMs`** = 100ms

#### ClaudeCodeControl.Diff.cs — Diff Integration

Manages git-based change tracking and the diff viewer window.

- **Key fields**: `_fileChangeTracker`, `_diffViewerWindow`, `_isDiffTrackingActive`, `_gitRepositoryRoot`
- **Git status polling**: `_gitStatusPollTimer` fires every 3 seconds
- **`TryApplyGitBaseline()`**: Core git integration
  - Runs `git status --porcelain=v1 -z` (null-separated for reliable parsing)
  - Reads original content via `git show HEAD:{path}`
  - Uses parallel fetch for performance
  - Handles Created/Modified/Deleted/Renamed files
- **`RefreshDiffViewAsync()`**: Heavy computation on background thread, UI updates on main thread
- **`IsGitRepositoryClean()`**: Cached check with 5-second throttle
- **`FindGitRepositoryRoot()`**: Walks up directory tree looking for `.git`
- **Timeouts**: `GitStatusTimeoutMs` = 8000ms, `MaxGitFileBytes` = 4MB

#### ClaudeCodeControl.UserInput.cs — Input Handling

Keyboard and button event handlers.

- **Prompt history**: `_historyIndex` (-1 = not navigating), `MaxHistorySize` = 50
- **`SendButton_Click()`**: Main submission flow
  1. Builds prompt with file attachments
  2. Creates temp directory with GUID for file persistence
  3. Converts paths to WSL format if needed
  4. Ensures diff tracking started
  5. Auto-opens changes view if enabled
  6. Clears prompt and resets history navigation
- **Key bindings**:
  - Enter: Send (if `SendWithEnter` enabled)
  - Shift+Enter / Ctrl+Enter: Insert newline
  - Ctrl+Up/Down: Navigate prompt history
  - Ctrl+V: Image/file paste handling

#### ClaudeCodeControl.Settings.cs — Settings Persistence

JSON-based settings at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`.

- **`_isInitializing`**: Prevents `SaveSettings()` during `LoadSettings()` to avoid overwriting with defaults
- **`LoadSettings()`** → **`ApplyLoadedSettings()`**: Deserialize and apply to UI
- **`SaveSettings()`**: Serialize with formatting; always guarded by `!_isInitializing`
- **`[JsonExtensionData]`**: `ClaudeCodeSettings.AdditionalData` preserves unknown JSON properties, preventing older DLL versions from discarding settings added by newer versions
- **Splitter position**: Stored in pixels, converted to/from `GridLength`

#### ClaudeCodeControl.ImageHandling.cs — File Attachments

Handles clipboard paste and file picker for attachments.

- **No file limit**: `attachedImagePaths` list (no cap), sequential naming for pasted images
- **`TryPasteImage()`**: Checks text first (prevents Excel cells becoming images), supports Image/Bitmap/PNG formats
- **File picker filter**: All files, Images, Documents (PDF/Word/txt), Spreadsheets (Excel/CSV), Data (JSON/XML/YAML), Code
- **UI**: Creates "chip" elements with filename and X remove button; chips are clickable to open the file
- **Temp directories**: Session-specific `%TEMP%\ClaudeCodeVS_Session\{GUID}\`
- **Memory cleanup**: Uses `DeleteObject()` P/Invoke for HBITMAP resources

#### ClaudeCodeControl.Workspace.cs — Workspace Detection

Multi-step directory resolution for terminal working directory.

- **`GetWorkspaceDirectoryAsync()`** priority:
  1. DTE solution directory
  2. Active project directory
  3. IVsSolution directory
  4. Current directory containing `.sln`/`.csproj`
  5. Fallback to My Documents
- **`OnWorkspaceDirectoryChangedAsync()`**: Restarts terminal only if directory actually changed; resets diff baseline; schedules deferred terminal layout refresh (`SchedulePostSolutionLoadTerminalRefresh`) on solution load events to fix visual corruption
- **Solution events**: Registered via `SolutionEventsHandler` (IVsSolutionEvents)

#### ClaudeCodeControl.Cleanup.cs — Resource Management

Cleanup and disposal of all resources.

- **`CleanupResources()`**: Stops diff tracking, unsubscribes theme events, sends `WM_CLOSE` to terminal, terminates process trees via `TryTerminateProcessTree()`, deletes temp directories
- **`TryTerminateProcessTree()`**: Kills a process tree once, tracking already-terminated PIDs to avoid double-kill; guards against terminating the VS process
- **`KillProcessAndChildren()`**: Recursive WMI-based process tree kill using `Win32_Process` query
- **Temp directories cleaned on init**: `CleanupClaudeCodeVSTempDirectories()` runs in background `Task.Run()`, preserves current session directory, removes old `ClaudeCodeVS*` directories from `%TEMP%`

#### ClaudeCodeControl.Interop.cs — Win32 API

Complete P/Invoke declarations for terminal embedding.

**Key constants**:
```
SWP_NOZORDER=0x0004, SWP_NOACTIVATE=0x0010, SWP_FRAMECHANGED=0x0020
SW_SHOW=5, SW_HIDE=0
GWL_STYLE=-16, GWL_EXSTYLE=-20
WS_CHILD=0x40000000, WS_POPUP=0x80000000, WS_CAPTION=0x00C00000, WS_THICKFRAME=0x00040000, WS_SYSMENU=0x00080000
WS_EX_APPWINDOW=0x00040000, WS_EX_TOOLWINDOW=0x00000080
WM_CLOSE=0x0010, WM_KEYDOWN=0x0100, WM_KEYUP=0x0101, WM_CHAR=0x0102
WM_MOUSEMOVE=0x0200, WM_LBUTTONDOWN=0x0201, WM_LBUTTONUP=0x0202, WM_MOUSEWHEEL=0x020A
RDW_INVALIDATE=0x0001, RDW_ERASE=0x0004, RDW_ALLCHILDREN=0x0080, RDW_UPDATENOW=0x0100, RDW_FRAME=0x0400
VK_TAB=0x09, VK_RETURN=0x0D, VK_SHIFT=0x10, VK_CONTROL=0x11, VK_SPACE=0x20, VK_UP=0x26, VK_RIGHT=0x27, VK_DOWN=0x28, VK_C=0x43, VK_F5=0x74
INPUT_KEYBOARD=1, KEYEVENTF_EXTENDEDKEY=0x0001, KEYEVENTF_KEYUP=0x0002
WH_KEYBOARD_LL=13, WH_MOUSE_LL=14, TH32CS_SNAPPROCESS=0x00000002
```

**P/Invoke functions by category**:
- Window management: `SetParent`, `SetWindowPos`, `ShowWindow`, `SetWindowLong`, `GetWindowLong`, `GetWindowRect`, `IsWindow`, `IsWindowVisible`, `GetDpiForWindow`, `GetClassName`
- Window enumeration: `EnumWindows`, `GetWindowThreadProcessId`
- Input: `SetFocus`, `SetForegroundWindow`, `SendInput`, `PostMessage`, `SendMessage`, `keybd_event`
- Mouse: `SetCursorPos`, `mouse_event`
- GDI: `DeleteObject` (for HBITMAP cleanup)
- Painting: `InvalidateRect`, `UpdateWindow`, `RedrawWindow` (for terminal repaint after layout/zoom changes)
- Console: `AttachConsole`, `FreeConsole`, `GetStdHandle`, `SetCurrentConsoleFontEx`, `GetCurrentConsoleFontEx` (available for console font operations)
- Hooks: `SetWindowsHookEx` (keyboard + mouse overloads), `UnhookWindowsHookEx`, `CallNextHookEx`, `GetModuleHandle`, `GetAsyncKeyState`, `GetGUIThreadInfo`, `IsChild`
- Process snapshot: `CreateToolhelp32Snapshot`, `Process32First`, `Process32Next`, `CloseHandle`

**Structures**: `RECT`, `INPUT`/`INPUTUNION`/`KEYBDINPUT` (for `SendInput` API), `COORD`/`CONSOLE_FONT_INFOEX` (for console font), `POINT`, `MSLLHOOKSTRUCT` (mouse hook), `KBDLLHOOKSTRUCT` (keyboard hook), `GUITHREADINFO` (focus detection), `PROCESSENTRY32` (process snapshot)

#### ClaudeCodeControl.Detach.cs — Terminal Detach/Attach

Allows detaching the terminal to a separate VS tool window tab and re-attaching it back.

- **Key fields**: `_detachedTerminalWindow` (DetachedTerminalToolWindow ref), `_detachedTerminalPanel` (WinForms Panel in detached window), `_isTerminalDetached` (current state), `_detachedClosedSubscribed` / `_detachedVisibilitySubscribed` (guard flags for event subscriptions)
- **`ActiveTerminalPanel`**: Property that returns the correct panel (detached or main) based on `_isTerminalDetached`
- **`DetachTerminalAsync()`**: Creates/finds `DetachedTerminalToolWindow`, re-parents terminal handle via `SetParent()`, hides main terminal area, expands prompt area by 80px, saves state to settings
- **`AttachTerminalAsync()`**: Re-parents terminal back to main panel, restores splitter position, closes detached window frame, unwires events
- **`ToggleDetachAsync()`**: Toggles between detached/attached states
- **`OnToolWindowFrameShow()`**: When main tool window is activated while terminal is detached, shows the detached window and resizes terminal
- **`OnDetachedWindowClosed()`**: Handles user closing the detached tab — re-attaches terminal automatically
- **`UpdateDetachButtonIcon()`**: Updates the detach button with arrow-in-box (attach) or arrow-out-of-box (detach) icon via WPF Canvas/Polyline

#### ClaudeCodeControl.Theme.cs — Theme Support

Integrates with Visual Studio dark/light theme.

- **Event-driven**: Subscribes to `VSColorTheme.ThemeChanged` (not polling)
- **`GetTerminalBackgroundColor()`**: Reads `VsBrushes.WindowKey`
- **`_lastTerminalColor`**: Caches last color to detect actual changes
- **Fire-and-forget pattern**: Uses `_ = JoinableTaskFactory.RunAsync()` with `#pragma warning disable VSSDK007, VSTHRD110`

### Data Models (ClaudeCodeModels.cs)

```csharp
enum AiProvider { ClaudeCode, ClaudeCodeWSL, Codex, CodexNative, CursorAgent, CursorAgentNative, QwenCode, OpenCode, Windsurf }
enum ClaudeModel { Opus, Sonnet, Haiku }
enum WindsurfModel { ClaudeOpus, ClaudeSonnet, Codex, GeminiPro }  // /model opus | /model sonnet | /model codex | /model gemini pro
enum EffortLevel { Auto, Low, Medium, High, Max }
enum TerminalType { CommandPrompt, WindowsTerminal }

class ClaudeCodeSettings {
    [JsonExtensionData] IDictionary<string, JToken> AdditionalData;  // preserves unknown props across DLL versions
    bool SendWithEnter = true;
    double SplitterPosition = 236.0;       // pixels
    AiProvider SelectedProvider = ClaudeCode;
    ClaudeModel SelectedClaudeModel = Sonnet;
    WindsurfModel SelectedWindsurfModel = ClaudeSonnet; // persisted Windsurf model selection
    List<PromptHistoryEntry> PromptHistory; // max 50 items
    bool AutoOpenChangesOnPrompt = false;
    bool ClaudeDangerouslySkipPermissions = false;  // --dangerously-skip-permissions flag
    bool CodexFullAuto = false;            // --ask-for-approval never flag for Codex (legacy name)
    bool WindsurfDangerousMode = false;    // --permission-mode dangerous flag for Windsurf
    EffortLevel SelectedEffortLevel = Auto; // Claude Code effort level
    string CustomWorkingDirectory = "";    // absolute or relative to solution dir
    TerminalType SelectedTerminalType = CommandPrompt; // terminal emulator selection
    bool IsTerminalDetached = false;       // terminal detached to separate tool window tab
    double PromptFontSize = 0.0;           // prompt text box font size (8–24pt, 0 = VS default)
    int TerminalZoomDelta = 0;             // net zoom delta for terminal (replayed on restart)
}
```

### Diff Engine

- **DiffComputer.cs**: Uses DiffPlex `InlineDiffBuilder`; 3 context lines around changes; two-pass algorithm (identify changes → build output with `...` separators)
- **FileChangeTracker.cs**: Thread-safe via `ConcurrentDictionary`; tracks 48 file extensions; ignores `bin`, `obj`, `node_modules`, `.git`, `.vs`, `packages`, `dist`, `build`, `__pycache__`; max file size 4MB; sorts by last-modified time
- **ChangedFile.cs**: Implements `INotifyPropertyChanged`; enums `ChangeType` (Created/Modified/Deleted/Renamed) and `DiffLineType` (Context/Added/Removed)

### DetachedTerminalToolWindow.cs

Tool window for hosting the detached terminal in a separate VS tab. Implements `IVsWindowFrameNotify` and `IVsWindowFrameNotify2` for frame event tracking.

- **GUID**: `B2C3D4E5-F6A7-8901-BCDE-FA2345678901`
- **`TerminalPanel`**: WinForms Panel (Dock=Fill, black background) that hosts the re-parented terminal handle
- **`VisibilityChanged`** event: Fires on `__FRAMESHOW` transitions (shown/hidden/tab activated/deactivated)
- **`Closed`** event: Fires from `IVsWindowFrameNotify2.OnClose()` to trigger re-attach
- **`UpdateCaption()`**: Updates the tool window title with the current provider name
- **`UpdateTheme()`**: Syncs panel background color with VS theme
- **Frame notifications**: Subscribes via `IVsWindowFrame2.Advise()` in `OnToolWindowCreated()`

### DiffViewerControl.xaml.cs

- **Virtualization**: Uses `VirtualizingPanel` with `Recycling` mode and fixed 20px line heights for performance
- **Lazy loading**: Diff panels populated only when file items are expanded
- **Search**: Ctrl+F opens search; Enter/Shift+Enter navigate matches; Escape closes
- **Auto-scroll**: Enabled when changes detected; disables after 3s inactivity (`AutoScrollDisableDelayMs = 3000`); respects user manual scroll override
- **Zoom**: Ctrl+Scroll, range 0.5–3.0, step 0.1

### Package Registration (ClaudeCodeExtensionPackage.cs)

```csharp
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(ClaudeCodeToolWindow))]
[ProvideToolWindow(typeof(DiffViewerToolWindow), Transient = true)]
[ProvideToolWindow(typeof(DetachedTerminalToolWindow), Transient = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
```

### UI Layout (ClaudeCodeControl.xaml)

Three-row grid: prompt area (`*`), splitter (4px), terminal area (`2*`). Key elements:
- `PromptTextBox`: Multi-line, Cascadia Mono font
- `AttachedImagesPanel`: WrapPanel for file chips
- `ViewChangesButton` (65px): Opens diff viewer (git repos only)
- `ModelDropdownButton` (30x30): Model context menu (visible for Claude and Windsurf providers; items toggled by `ModelContextMenu_Opened`)
- `MenuDropdownButton` (30x30): Provider/settings context menu
- `UpdateAgentButton` (30px): One-click update
- `DetachTerminalButton`: Toggle detach/attach terminal to separate tab
- `TerminalHost`: WindowsFormsHost embedding the terminal panel

---

## Key GUIDs & IDs

| Identifier | GUID | Purpose |
|-----------|------|---------|
| Package GUID | `3fa29425-3add-418f-82f6-0c9b7419b2ca` | VS package registration |
| VSIX Identity | `87de5d13-743e-46b3-b05e-24e1cbeca0c3` | Extension marketplace ID |
| Command Set | `11111111-2222-3333-4444-555555555555` | Menu command group |
| Project GUID | `75253A84-A760-4061-9885-42A4DAF4B995` | .csproj project ID |
| Detached Terminal Window | `B2C3D4E5-F6A7-8901-BCDE-FA2345678901` | DetachedTerminalToolWindow |
| Tool Window Command ID | `0x0100` | ClaudeCodeToolWindow |

---

## Important Magic Values & Timeouts

| Value | Location | Purpose |
|-------|----------|---------|
| 50 prompts max | UserInput.cs (`MaxHistorySize`) | Prompt history limit |
| 4 MB | Diff.cs (`MaxGitFileBytes`) / FileChangeTracker | Max file size for diff |
| 8000 ms | Diff.cs (`GitStatusTimeoutMs`) | Git command timeout |
| 5 min (300000 ms) | ProviderManagement.cs (`ProviderCacheExpiry`) | Provider availability cache TTL |
| 3 seconds | Diff.cs | Git status poll interval |
| 5 seconds | Diff.cs | Git clean check throttle |
| 3000 ms | DiffViewerControl.xaml.cs (`AutoScrollDisableDelayMs`) | Auto-scroll inactivity timeout |
| 2000 ms | TerminalIO.cs (`ClipboardTimeoutMs`) | Clipboard operation timeout |
| 10 retries | TerminalIO.cs (`ClipboardMaxRetries`) | Max clipboard retry attempts |
| 100 ms | TerminalIO.cs (`ClipboardRetryDelayMs`) | Delay between clipboard retries |
| 2000 ms | Terminal.cs | Panel initialization wait |
| 5000 ms | Terminal.cs | Window handle find timeout |
| 500 ms | SolutionEventsHandler.cs | Delay after solution open |
| 300 ms | SolutionEventsHandler.cs | Delay after project open |
| 236.0 px | ClaudeCodeModels.cs | Default splitter position |
| 0.5–3.0 | DiffViewerControl.xaml.cs | Zoom range (step 0.1) |
| 3 context lines | DiffComputer.cs (`ContextLines`) | Lines shown around diff changes |
| 3 times | Terminal.cs (`ApplyWindowsTerminalZoomOutAsync`) | Ctrl+Minus zoom out steps for WT |
| 15000 ms | Terminal.cs (`FindNewWtWindowAsync`) | Timeout finding new WT window |
| 200/500/1000 ms | Terminal.cs (`SchedulePostSolutionLoadTerminalRefresh`) | Deferred resize passes after solution load |

---

## Common Threading Patterns

### Switch to UI thread
```csharp
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
ThreadHelper.ThrowIfNotOnUIThread();
```

### Fire-and-forget async event handler
```csharp
#pragma warning disable VSSDK007, VSTHRD110
_ = JoinableTaskFactory.RunAsync(async () =>
{
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    // ... UI work
});
#pragma warning restore VSSDK007, VSTHRD110
```

### Background work with UI update
```csharp
await Task.Run(() => { /* heavy work */ });
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
// update UI
```

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.VisualStudio.SDK | 17.0.32112.339 | VS extensibility APIs |
| Microsoft.VSSDK.BuildTools | 17.14.2101 | VSIX build tools |
| Newtonsoft.Json | 13.0.3 | JSON serialization for settings |
| DiffPlex | 1.7.2 | Diff computation engine |

---

## Supported AI Providers

| Provider | Enum Value | Platform | Executable | Exit Command |
|----------|-----------|----------|-----------|-------------|
| Claude Code | `ClaudeCode` | Windows native | `claude` in PATH | `exit` |
| Claude Code (WSL) | `ClaudeCodeWSL` | WSL | `claude` inside WSL | `exit` |
| Codex | `CodexNative` | Windows native | `codex` in PATH (via npm) | Double CTRL+C |
| Codex (WSL) | `Codex` | WSL | `codex` inside WSL | Double CTRL+C |
| Cursor Agent | `CursorAgentNative` | Windows native | `agent.exe` at `%USERPROFILE%\.local\bin\` or `agent.cmd` in PATH | `exit` |
| Cursor Agent (WSL) | `CursorAgent` | WSL | `cursor-agent` inside WSL | `exit` |
| Qwen Code | `QwenCode` | Windows native | `qwen` in PATH (Node.js 20+) | `/quit` |
| Open Code | `OpenCode` | Windows native | `opencode` in PATH (Node.js 14+) | `exit` |
| Windsurf (WSL) | `Windsurf` | WSL | `devin` inside WSL (`~/.local/bin/devin`) | `exit` |

---

## Key Features

- Embedded terminal within Visual Studio (via Win32 `SetParent` interop)
- Multi-line prompts (Shift+Enter / Ctrl+Enter for newlines)
- File attachments (unlimited: images, PDFs, documents, code, etc.)
- Image paste from clipboard (Ctrl+V) with Excel-cell-as-text handling
- Prompt history (last 50 prompts, Ctrl+Up/Down navigation)
- Integrated diff viewer with git-based change tracking (3-second polling)
- Claude model selection (Opus, Sonnet, Haiku)
- Effort level selection (Auto, Low, Medium, High, Max) via `/effort` command
- Show Usage shortcut in model menu (via `/usage` command)
- Windsurf model selection (Claude Opus, Claude Sonnet, Codex, Gemini Pro) via `/model` command
- Windsurf Show Usage shortcut in model menu (opens https://windsurf.com/subscription/usage in browser)
- Set Language shortcut in model menu (via `/config` TUI navigation)
- Dark/light theme integration (event-driven, not polling)
- Auto-open changes on send
- Optional `--dangerously-skip-permissions` mode for Claude Code
- Optional `--ask-for-approval never` mode for Codex
- Optional `--permission-mode dangerous` mode for Windsurf
- One-click agent updates
- Detach/attach terminal into a separate VS tool window tab (state persisted across sessions; auto-focus on extension activation)
- F5/Ctrl+F5/Shift+F5 forwarding from terminal to VS debug commands via low-level keyboard hook
- Prompt font zoom (Ctrl+Scroll, range 8–24pt, persisted)
- Terminal zoom (Ctrl+Scroll, zoom delta persisted and replayed on restart via low-level mouse hook)
- Windows Terminal text selection assistance (SHIFT+drag injection for TUI mouse reporting bypass)
- Terminal lifecycle serialization via semaphore to prevent overlapping start/stop transitions
- UTF-8 encoding (`chcp 65001`) and Virtual Terminal Processing (`VIRTUAL_TERMINAL_LEVEL=1`) for proper Unicode and ANSI rendering
- Clipboard preservation during terminal I/O with automatic retry on contention
- Provider availability caching (5-minute TTL)
- WSL path conversion for cross-platform support
- Virtualized diff rendering for large repositories
- Configurable terminal emulator: Command Prompt (default) or Windows Terminal (better emoji/unicode rendering)
- Windows Terminal integration with auto-zoom, tab bar hiding, and DPI-aware positioning
- Embedded terminal hidden from Windows taskbar (via `WS_EX_TOOLWINDOW` extended style)
- Deferred terminal layout refresh on solution load/close to prevent visual corruption

---

## Adding a New AI Provider (Checklist)

When adding a new AI provider, update these locations:

1. **`ClaudeCodeModels.cs`**: Add value to `AiProvider` enum; add settings property if provider has flags (e.g., `WindsurfDangerousMode`)
2. **`ClaudeCodeControl.ProviderManagement.cs`**: Add `Is{Provider}AvailableAsync()` detection method; add to cache logic; add install instructions; add notification flag; add menu click handler; add to `UpdateProviderSelection()` (checkmark + header name); add to `ProviderContextMenu_Opened()` for conditional menu visibility
3. **`ClaudeCodeControl.Terminal.cs`**: Add command building in `StartEmbeddedTerminalAsync()` (both Windows Terminal and Command Prompt switch blocks); add to `providerTitle` switch in conhost embed section; add to `InitializeTerminalAsync()` (availability check + startup block); add to `RestartTerminalWithSelectedProviderAsync()`; add to `UpdateAgentButton_Click()` for update command; add `Get{Provider}Command()` if provider has flags
4. **`ClaudeCodeControl.TerminalIO.cs`**: Add Enter key behavior in `SendEnterKey()`; add to `isOtherWSLProvider` if WSL-based
5. **`ClaudeCodeControl.UserInput.cs`**: Add to `isWSLProvider` check for WSL path conversion
6. **`ClaudeCodeControl.Detach.cs`**: Add to `GetCurrentProviderName()` switch
7. **`ClaudeCodeControl.xaml`**: Add context menu item for the provider; add settings menu item if provider has flags
8. **`README.md`**: Document the new provider in Features, System Requirements, AI Provider Menu, and Updating sections
