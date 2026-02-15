# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

This is a **Visual Studio Extension (VSIX)** for Visual Studio 2022/2026 that provides seamless integration with multiple AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Qwen Code, Open Code) directly within the IDE. It embeds a terminal via Win32 interop and provides features like multi-line prompts, file attachments, prompt history, integrated diff viewer, and theme support.

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com)
- **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 6.3
- **Target Framework**: .NET Framework 4.7.2

---

## MANDATORY: Version & Documentation Updates

**IMPORTANT: Every development session that modifies code MUST include the following updates before finishing:**

1. **Bump Assembly Version** in `Properties/AssemblyInfo.cs`:
   - Update both `AssemblyVersion` and `AssemblyFileVersion` (e.g., `6.3.0.0` -> `6.4.0.0`)
2. **Bump Manifest Version** in `source.extension.vsixmanifest`:
   - Update the `Version` attribute in the `<Identity>` tag (e.g., `6.3` -> `6.4`)
3. **Update README.md**:
   - Add a new version entry at the top of the `## Version History` section describing what changed
   - Follow the existing format: `### Version X.Y` with bullet points describing changes

---

## Build & Test

- **Build (Release)**: `msbuild ClaudeCodeExtension.sln /p:Configuration=Release`
- **Build (Debug)**: `msbuild ClaudeCodeExtension.sln /p:Configuration=Debug`
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
│   ├── ClaudeCodeControl.Terminal.cs    # Terminal embedding (cmd.exe/wsl.exe), process init
│   ├── ClaudeCodeControl.ProviderManagement.cs  # AI provider detection & switching
│   ├── ClaudeCodeControl.TerminalIO.cs  # Terminal I/O, command execution
│   ├── ClaudeCodeControl.Diff.cs        # Diff view integration, git polling
│   ├── ClaudeCodeControl.UserInput.cs   # Keyboard input, button handlers
│   ├── ClaudeCodeControl.Workspace.cs   # Solution/workspace directory detection
│   ├── ClaudeCodeControl.ImageHandling.cs # Image paste & file attachments
│   ├── ClaudeCodeControl.Settings.cs    # Settings persistence (JSON)
│   ├── ClaudeCodeControl.Cleanup.cs     # Resource cleanup, temp dir management
│   ├── ClaudeCodeControl.Interop.cs     # Win32 API declarations (P/Invoke)
│   └── ClaudeCodeControl.Theme.cs       # Dark/light theme support
│
├── UI:
│   ├── ClaudeCodeControl.xaml           # Main extension UI layout
│   ├── DiffViewerControl.xaml           # Diff viewer UI
│   ├── DiffViewerControl.xaml.cs        # Diff viewer logic (tree, search, zoom)
│   ├── ClaudeCodeToolWindow.cs          # Main tool window wrapper
│   └── DiffViewerToolWindow.cs          # Diff viewer tool window wrapper
│
├── Diff Engine:
│   ├── Diff/DiffComputer.cs             # Diff computation using DiffPlex library
│   ├── Diff/FileChangeTracker.cs        # Git baseline tracking, change detection
│   └── Diff/ChangedFile.cs              # Data models (ChangeType, DiffLine, ChangedFile)
│
├── Models & Package:
│   ├── ClaudeCodeModels.cs              # Enums (AiProvider, ClaudeModel) & settings class
│   ├── ClaudeCodeExtensionPackage.cs    # VS package registration & menu commands
│   └── SolutionEventsHandler.cs         # Solution/project open events
```

---

## Code Style & Conventions

- **Language**: C# targeting .NET Framework 4.7.2
- **File Headers**: Every `.cs` file must include copyright header with author (Daniel Liedke), copyright year (2026), and proprietary usage notice
- **Namespaces**: `ClaudeCodeVS` for main controls and models, `ClaudeCodeExtension` for package class
- **Partial Classes**: Main control is split into 12 specialized partial class files (all `partial class ClaudeCodeControl`)
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

The main UI control is split across 12 partial class files. All share the same `ClaudeCodeControl : UserControl, IDisposable` class in the `ClaudeCodeVS` namespace.

#### ClaudeCodeControl.cs — Core Initialization

- **Constructor**: Initializes XAML, temp directories, solution events, theme events, and lifecycle event handlers
- **Key fields**: `_toolWindow` (parent tool window ref), `_hasInitialized` (prevents re-init on tab switches)
- **`ClaudeCodeControl_Loaded()`**: Loads settings, applies them to UI, initializes terminal (only once)
- **`OnWorkspaceDirectoryChangedAsync()`**: Called when solution/project changes; restarts terminal with new working directory
- **Initialization guard**: The `_hasInitialized` flag prevents multiple initializations when switching tabs in VS

#### ClaudeCodeControl.Terminal.cs — Terminal Embedding

Manages the embedded cmd.exe/wsl.exe terminal via Win32 interop.

- **Key fields**: `terminalPanel` (WinForms Panel host), `cmdProcess` (Process), `terminalHandle` (IntPtr), `_currentRunningProvider`
- **`StartEmbeddedTerminalAsync()`**: Core startup method
  - Kills existing process gracefully (exit command or CTRL+C depending on provider)
  - Builds provider-specific command strings
  - Uses `GetFreshPathFromRegistry()` to refresh PATH for detecting newly installed tools
  - Hides window immediately via `SW_HIDE` to prevent blinking
  - Embeds into panel using `SetParent()`, strips window decorations (`WS_CAPTION`, `WS_THICKFRAME`, etc.)
  - Updates `_currentRunningProvider` tracking

**Terminal command patterns**:
```
Windows native: cmd.exe /k cd /d "{dir}" && ping localhost -n 3 >nul && cls && {command}
WSL providers:  cmd.exe /k cls && wsl bash -ic "cd {wslPath} && {command}"
```

**Path conversion** (`ConvertToWslPath()`):
- `\\wsl.localhost\<distro>\path` → `/path`
- `\\wsl$\<distro>\path` → `/path`
- `C:\Users\...` → `/mnt/c/Users/...`

**Provider executable detection**:
- `GetClaudeCommand()`: Prioritizes native .exe over NPM installation
- `GetCursorAgentCommand()`: Checks `%LOCALAPPDATA%\cursor-agent\` first, then PATH

#### ClaudeCodeControl.ProviderManagement.cs — Provider Detection

Detects and validates availability of all 7 AI providers.

- **Caching**: `_providerCache` Dictionary with 5-minute TTL (`ProviderCacheExpiry = 300000ms`)
- **Detection methods**: All use `cmd.exe /c where {command}` (Windows) or `wsl bash -ic "which {command}"` (WSL)
- **WSL retry logic**: `IsClaudeCodeWSLAvailableAsync()` retries 2 times with 3s/5s timeouts for cold WSL boot
- **Notification flags**: Static booleans (`_claudeNotificationShown`, etc.) ensure install instructions show only once per VS session
- **`ClearProviderCache()`**: Should be called when user actions might change availability (e.g., after update)

#### ClaudeCodeControl.TerminalIO.cs — Terminal I/O

Sends text and keystrokes to the embedded terminal.

- **`SendTextToTerminalAsync()`**: Main I/O method
  1. Saves entire clipboard state (all formats including Office data, MemoryStreams)
  2. Sets clipboard to target text
  3. Right-clicks terminal center to paste (Shift+right-click for OpenCode)
  4. Sends Enter key via `WM_CHAR` or `KEYDOWN`/`KEYUP`
  5. Restores original clipboard content
- **`SendEnterKey()`**: Provider-specific behavior:
  - Claude/QwenCode/OpenCode: Single `WM_CHAR` with `VK_RETURN`
  - WSL providers: `KEYDOWN`/`KEYUP` approach
  - Codex: Enter sent twice (required by Codex CLI)
- **`SendCtrlC()`**: Uses multiple approaches — `keybd_event`, `SendInput`, and `PostMessage`
- **`ClipboardTimeoutMs`** = 2000ms

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
  1. Builds prompt with file attachments (up to 5 files)
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
- **Splitter position**: Stored in pixels, converted to/from `GridLength`

#### ClaudeCodeControl.ImageHandling.cs — File Attachments

Handles clipboard paste and file picker for attachments.

- **Limits**: Max 5 files (`attachedImagePaths` list), sequential naming for pasted images
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
- **`OnWorkspaceDirectoryChangedAsync()`**: Restarts terminal only if directory actually changed; resets diff baseline
- **Solution events**: Registered via `SolutionEventsHandler` (IVsSolutionEvents)

#### ClaudeCodeControl.Cleanup.cs — Resource Management

Cleanup and disposal of all resources.

- **`CleanupResources()`**: Stops diff tracking, unsubscribes theme events, kills child processes recursively via WMI, kills cmd process, deletes temp directories
- **`KillProcessAndChildren()`**: Recursive WMI-based process tree kill using `Win32_Process` query
- **Temp directories cleaned on init**: `CleanupClaudeCodeVSTempDirectories()` removes all old `ClaudeCodeVS*` directories from `%TEMP%`

#### ClaudeCodeControl.Interop.cs — Win32 API

Complete P/Invoke declarations for terminal embedding.

**Key constants**:
```
SWP_NOZORDER=0x0004, SWP_NOACTIVATE=0x0010
SW_SHOW=5, SW_HIDE=0
GWL_STYLE=-16
WS_CAPTION=0x00C00000, WS_THICKFRAME=0x00040000, WS_SYSMENU=0x00080000
WM_KEYDOWN=0x0100, WM_KEYUP=0x0101, WM_CHAR=0x0102
VK_RETURN=0x0D, VK_SHIFT=0x10, VK_CONTROL=0x11, VK_C=0x43
INPUT_KEYBOARD=1, KEYEVENTF_KEYUP=0x0002
```

**P/Invoke functions by category**:
- Window management: `SetParent`, `SetWindowPos`, `ShowWindow`, `SetWindowLong`, `GetWindowLong`, `GetWindowRect`, `IsWindow`, `IsWindowVisible`
- Window enumeration: `EnumWindows`, `GetWindowThreadProcessId`
- Input: `SetFocus`, `SetForegroundWindow`, `SendInput`, `PostMessage`, `keybd_event`
- Mouse: `SetCursorPos`, `mouse_event`
- GDI: `DeleteObject` (for HBITMAP cleanup)

**Structures**: `RECT`, `INPUT`/`INPUTUNION`/`KEYBDINPUT` (for `SendInput` API)

#### ClaudeCodeControl.Theme.cs — Theme Support

Integrates with Visual Studio dark/light theme.

- **Event-driven**: Subscribes to `VSColorTheme.ThemeChanged` (not polling)
- **`GetTerminalBackgroundColor()`**: Reads `VsBrushes.WindowKey`
- **`_lastTerminalColor`**: Caches last color to detect actual changes
- **Fire-and-forget pattern**: Uses `_ = JoinableTaskFactory.RunAsync()` with `#pragma warning disable VSSDK007, VSTHRD110`

### Data Models (ClaudeCodeModels.cs)

```csharp
enum AiProvider { ClaudeCode, ClaudeCodeWSL, Codex, CursorAgent, CursorAgentNative, QwenCode, OpenCode }
enum ClaudeModel { Opus, Sonnet, Haiku }

class ClaudeCodeSettings {
    bool SendWithEnter = true;
    double SplitterPosition = 236.0;       // pixels
    AiProvider SelectedProvider = ClaudeCode;
    ClaudeModel SelectedClaudeModel = Sonnet;
    List<string> PromptHistory;            // max 50 items
    bool AutoOpenChangesOnPrompt = false;
}
```

### Diff Engine

- **DiffComputer.cs**: Uses DiffPlex `InlineDiffBuilder`; 3 context lines around changes; two-pass algorithm (identify changes → build output with `...` separators)
- **FileChangeTracker.cs**: Thread-safe via `ConcurrentDictionary`; tracks 48 file extensions; ignores `bin`, `obj`, `node_modules`, `.git`, `.vs`, `packages`, `dist`, `build`, `__pycache__`; max file size 4MB; sorts by last-modified time
- **ChangedFile.cs**: Implements `INotifyPropertyChanged`; enums `ChangeType` (Created/Modified/Deleted/Renamed) and `DiffLineType` (Context/Added/Removed)

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
[ProvideMenuResource("Menus.ctmenu", 1)]
```

### UI Layout (ClaudeCodeControl.xaml)

Three-row grid: prompt area (`*`), splitter (4px), terminal area (`2*`). Key elements:
- `PromptTextBox`: Multi-line, Cascadia Mono font
- `AttachedImagesPanel`: WrapPanel for file chips
- `ViewChangesButton` (65px): Opens diff viewer (git repos only)
- `ModelDropdownButton` (30x30): Model context menu
- `MenuDropdownButton` (30x30): Provider/settings context menu
- `UpdateAgentButton` (30px): One-click update
- `TerminalHost`: WindowsFormsHost embedding the terminal panel

---

## Key GUIDs & IDs

| Identifier | GUID | Purpose |
|-----------|------|---------|
| Package GUID | `3fa29425-3add-418f-82f6-0c9b7419b2ca` | VS package registration |
| VSIX Identity | `87de5d13-743e-46b3-b05e-24e1cbeca0c3` | Extension marketplace ID |
| Command Set | `11111111-2222-3333-4444-555555555555` | Menu command group |
| Project GUID | `75253A84-A760-4061-9885-42A4DAF4B995` | .csproj project ID |
| Tool Window Command ID | `0x0100` | ClaudeCodeToolWindow |

---

## Important Magic Values & Timeouts

| Value | Location | Purpose |
|-------|----------|---------|
| 5 files max | ImageHandling.cs | File attachment limit |
| 50 prompts max | UserInput.cs (`MaxHistorySize`) | Prompt history limit |
| 4 MB | Diff.cs (`MaxGitFileBytes`) / FileChangeTracker | Max file size for diff |
| 8000 ms | Diff.cs (`GitStatusTimeoutMs`) | Git command timeout |
| 5 min (300000 ms) | ProviderManagement.cs (`ProviderCacheExpiry`) | Provider availability cache TTL |
| 3 seconds | Diff.cs | Git status poll interval |
| 5 seconds | Diff.cs | Git clean check throttle |
| 3000 ms | DiffViewerControl.xaml.cs (`AutoScrollDisableDelayMs`) | Auto-scroll inactivity timeout |
| 2000 ms | TerminalIO.cs (`ClipboardTimeoutMs`) | Clipboard operation timeout |
| 2000 ms | Terminal.cs | Panel initialization wait |
| 5000 ms | Terminal.cs | Window handle find timeout |
| 500 ms | SolutionEventsHandler.cs | Delay after solution open |
| 300 ms | SolutionEventsHandler.cs | Delay after project open |
| 236.0 px | ClaudeCodeModels.cs | Default splitter position |
| 0.5–3.0 | DiffViewerControl.xaml.cs | Zoom range (step 0.1) |
| 3 context lines | DiffComputer.cs (`ContextLines`) | Lines shown around diff changes |

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
| OpenAI Codex | `Codex` | WSL | `codex` inside WSL | Double CTRL+C |
| Cursor Agent | `CursorAgent` | WSL | `cursor-agent` inside WSL | `exit` |
| Cursor Agent (Win) | `CursorAgentNative` | Windows native | `agent.exe` at `%USERPROFILE%\.local\bin\` or `agent.cmd` in PATH | `exit` |
| Qwen Code | `QwenCode` | Windows native | `qwen` in PATH (Node.js 20+) | `/quit` |
| Open Code | `OpenCode` | Windows native | `opencode` in PATH (Node.js 14+) | `exit` |

---

## Key Features

- Embedded terminal within Visual Studio (via Win32 `SetParent` interop)
- Multi-line prompts (Shift+Enter / Ctrl+Enter for newlines)
- File attachments (up to 5 files: images, PDFs, documents, code, etc.)
- Image paste from clipboard (Ctrl+V) with Excel-cell-as-text handling
- Prompt history (last 50 prompts, Ctrl+Up/Down navigation)
- Integrated diff viewer with git-based change tracking (3-second polling)
- Claude model selection (Opus, Sonnet, Haiku) with thinking mode for Opus
- Dark/light theme integration (event-driven, not polling)
- Auto-open changes on send
- One-click agent updates
- Clipboard preservation during terminal I/O
- Provider availability caching (5-minute TTL)
- WSL path conversion for cross-platform support
- Virtualized diff rendering for large repositories

---

## Adding a New AI Provider (Checklist)

When adding a new AI provider, update these locations:

1. **`ClaudeCodeModels.cs`**: Add value to `AiProvider` enum
2. **`ClaudeCodeControl.ProviderManagement.cs`**: Add `Is{Provider}AvailableAsync()` detection method; add to cache logic; add install instructions
3. **`ClaudeCodeControl.Terminal.cs`**: Add command building in `StartEmbeddedTerminalAsync()`; add to `GetProviderDisplayName()`
4. **`ClaudeCodeControl.TerminalIO.cs`**: Add Enter key behavior in `SendEnterKey()`; add exit method if non-standard
5. **`ClaudeCodeControl.UserInput.cs`**: Add update command in update agent handler; add to provider menu items
6. **`ClaudeCodeControl.xaml`**: Add context menu item for the provider
7. **`README.md`**: Document the new provider in Features and System Requirements sections
