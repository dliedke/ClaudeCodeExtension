# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

**Visual Studio Extension (VSIX)** for VS 2022/2026 — integrates AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Qwen Code, Open Code, Windsurf) via embedded terminal (Win32 `SetParent` interop).

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com) | **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 10.10 | **Target Framework**: .NET Framework 4.7.2

---

## MANDATORY: Version & Documentation Updates

**Every development session that modifies code MUST update before finishing:**

1. **`Properties/AssemblyInfo.cs`**: Bump `AssemblyVersion` and `AssemblyFileVersion`
2. **`source.extension.vsixmanifest`**: Bump `Version` in `<Identity>` tag
3. **`README.md`**: Add `### Version X.Y` entry at top of `## Version History`

---

## Build & Test

```bash
# Release
'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Release -v:minimal

# Debug
'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Debug -v:minimal
```

- **Debug**: F5 in Visual Studio → experimental instance with `/rootsuffix Exp`
- **No automated tests** — manual testing via F5 in VS 2022/2026

### Publishing

When the user asks to **publish the app** (or any equivalent phrasing like "publish the extension", "publish to marketplace", "ship it"), run `publish.cmd` from the repo root. Do not invoke MSBuild or marketplace APIs manually — `publish.cmd` is the authoritative deployment automation.

**`publish.cmd`** performs: Clean → Rebuild Release → publish VSIX via `VsixPublisher.exe` with `publishManifest.json`. Falls back from VS 2026 to VS 2022 tool paths automatically. Uses `VsixPub0038` log marker to detect success (works around VsixPublisher telemetry crash in VS 18).

**`publishManifest.json`**: Marketplace metadata — publisher `dliedke`, category `coding`, free, Q&A enabled, README.md as overview.

---

## Project Structure

```
ClaudeCodeExtension/
├── Core Control (partial classes of ClaudeCodeControl):
│   ├── ClaudeCodeControl.cs             # Core initialization & orchestration
│   ├── ClaudeCodeControl.Terminal.cs    # Terminal embedding, process init, F5 forwarding
│   ├── ClaudeCodeControl.ProviderManagement.cs  # AI provider detection & switching, Caveman plugin install
│   ├── ClaudeCodeControl.TerminalIO.cs  # Terminal I/O, command execution
│   ├── ClaudeCodeControl.Diff.cs        # Diff view integration, git polling
│   ├── ClaudeCodeControl.UserInput.cs   # Keyboard input, button handlers
│   ├── ClaudeCodeControl.Workspace.cs   # Solution/workspace directory detection
│   ├── ClaudeCodeControl.ImageHandling.cs # Image paste & file attachments
│   ├── ClaudeCodeControl.Settings.cs    # Settings persistence (JSON), layout inversion
│   ├── ClaudeCodeControl.Cleanup.cs     # Resource cleanup, temp dir management
│   ├── ClaudeCodeControl.Interop.cs     # Win32 API declarations (P/Invoke)
│   ├── ClaudeCodeControl.Theme.cs       # Dark/light theme support
│   └── ClaudeCodeControl.Detach.cs      # Terminal detach/attach to separate VS tab
├── UI:
│   ├── ClaudeCodeControl.xaml / DiffViewerControl.xaml(.cs)
│   ├── ClaudeCodeToolWindow.cs / DiffViewerToolWindow.cs / DetachedTerminalToolWindow.cs
├── Diff Engine:
│   ├── Diff/DiffComputer.cs / FileChangeTracker.cs / ChangedFile.cs
├── Models & Package:
│   ├── ClaudeCodeModels.cs              # Enums & settings class
│   ├── ClaudeCodeExtensionPackage.cs    # VS package registration
│   └── SolutionEventsHandler.cs         # Solution/project open events
├── Publishing:
│   ├── publish.cmd                      # Automated marketplace deployment script
│   └── publishManifest.json             # VS Marketplace metadata
```

---

## Code Style & Conventions

- **Language**: C# / .NET Framework 4.7.2
- **File Headers**: Every `.cs` file must include copyright header (Daniel Liedke, 2026)
- **Namespaces**: `ClaudeCodeVS` (controls/models), `ClaudeCodeExtension` (package)
- **Naming**: PascalCase public, `_camelCase` private fields, camelCase locals
- **Error Handling**: try-catch + `Debug.WriteLine`; `MessageBox` for user-facing errors
- **Thread Safety**: `ThreadHelper.ThrowIfNotOnUIThread()` / `SwitchToMainThreadAsync()`
- **Settings**: JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`

```csharp
/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: <description>
 * *******************************************************************************************************************/
```

---

## Architecture — Key Non-Obvious Details

### Terminal Embedding (Terminal.cs)

- **Two terminal modes**: Command Prompt (conhost) and Windows Terminal (wt.exe), via `_settings.SelectedTerminalType`
- **Lifecycle serialization**: `_terminalLifecycleSemaphore` prevents overlapping start/stop transitions
- **Session ID tracking**: `_terminalStartupSessionId` discards stale startup work when a new terminal start is triggered before the old one finishes
- **`SetParent()` retry**: Up to 3 attempts with 200ms delay and Win32 error logging (`Marshal.GetLastWin32Error()`), re-applies window styles between retries
- **Conhost handle discovery**: `FindMainWindowHandleByConhostAsync()` retries with 5s then 10s timeouts; uses ToolHelp32 (`CreateToolhelp32Snapshot`) for child PID lookup
- **WT embedding**: Finds `CASCADIA_HOSTING_WINDOW_CLASS`, embeds with `WS_CHILD`, calculates tab bar height offset
- **Terminal hidden from taskbar**: `WS_EX_TOOLWINDOW` + clear `WS_EX_APPWINDOW`
- **F5 forwarding**: Low-level keyboard hook (`WH_KEYBOARD_LL`) intercepts F5/Ctrl+F5/Shift+F5 → VS debug commands via DTE
- **Mouse hook** (`WH_MOUSE_LL`): Tracks Ctrl+Scroll zoom delta (persisted); converts plain left-drag to SHIFT+drag for WT text selection
- **Post-startup**: `SchedulePostStartupTerminalAdjustments()` runs deferred resize + zoom replay; `SchedulePostSolutionLoadTerminalRefresh()` does 200/500/1000ms repaint passes after solution load
- **Fresh PATH from registry**: `GetFreshPathFromRegistry()` reads PATH from `HKLM` and `HKCU` registry keys to detect newly installed tools (e.g. Windows Terminal) without requiring VS restart

**Command patterns**:
```
Windows: cmd.exe /k chcp 65001 >nul && cd /d "{dir}" && ping localhost -n 3 >nul && cls && {command}
WSL:     cmd.exe /k chcp 65001 >nul && cls && wsl bash -lic "cd {wslPath} && {command}"
```

**WSL path conversion** (`ConvertToWslPath()`): `\\wsl.localhost\distro\path` → `/path`, `C:\...` → `/mnt/c/...`

**WSL shell mode**: Uses `bash -lic` (login + interactive) to load `.profile`/`.bash_profile` PATH entries — applies to all WSL providers (Claude Code WSL, Codex WSL, Cursor Agent WSL, Windsurf)

### Provider Detection (ProviderManagement.cs)

- **Caching**: `_providerCache` with 5-min TTL, separate `_wslCache` for WSL installation status
- **Thread-safe cache**: `_cacheLock` object for synchronized access; `IsCacheValid()` checks timestamp expiry
- **Claude Code detection**: Two-tier — first checks native path (`%USERPROFILE%\.local\bin\claude.exe`), then falls back to `where claude` (PATHEXT-aware, finds both `claude.exe` from winget and `claude.cmd` from NPM)
- **WSL detection**: `bash -lc` (login shell) for `which` commands — avoids `.bashrc` noise; retries 2x with 8s/20s timeouts for cold boot
- **Early-exit logic**: Only stops retrying when stdout has content (ignores stderr-only shell warnings)
- **Notification flags**: Static booleans (one per provider) ensure install pop-ups show only once per VS session
- **Model menus**: `ModelContextMenu_Opened()` toggles Claude items vs Windsurf items based on active provider

### Caveman Plugin (ProviderManagement.cs)

- **Not a standalone provider** — a Claude Code plugin (JuliusBrussee/caveman) for ultra-compressed communication
- **Menu item**: "Install Caveman" in the model context menu, visible only when Claude Code or Claude Code (WSL) is running
- **Installation flow**: Sends sequential `/plugin` slash commands into the active Claude Code session with timed delays:
  1. `/plugin marketplace add JuliusBrussee/caveman` (7s wait)
  2. `/plugin install caveman@caveman --scope user` (4s wait)
  3. Enter key to confirm trust prompts (1.5s wait)
  4. `/reload-plugins` (3s wait)
  5. `/caveman` (2s wait)
  6. `yes` to confirm activation
- **Confirmation dialog**: Shows all commands that will be sent before execution

### Terminal I/O (TerminalIO.cs)

- **Paste mechanism**: Saves full clipboard state → sets text → right-clicks terminal center → sends Enter → restores clipboard
- **Clipboard retry**: Up to 10 retries with 100ms delay for `CLIPBRD_E_CANT_OPEN`
- **Enter key varies by provider**: `WM_CHAR` (Claude/Qwen/OpenCode), `KEYDOWN/KEYUP` (WSL), double-Enter (Codex)

### Settings (Settings.cs)

- **`_isInitializing` guard**: Prevents `SaveSettings()` during `LoadSettings()`
- **`[JsonExtensionData]`**: Preserves unknown JSON properties across DLL versions
- **Layout inversion**: `ApplyLayout()` swaps prompt and terminal grid rows, adjusts MinHeights (Terminal 150px, Prompt 80px), hides/shows terminal GroupBox header, reorders prompt section controls

### Workspace (Workspace.cs)

Priority: DTE solution dir → active project dir → IVsSolution dir → current dir with `.sln`/`.csproj` → My Documents

### Detach (Detach.cs)

Re-parents terminal to/from `DetachedTerminalToolWindow` via `SetParent()`. Auto-reattaches when detached tab is closed.

---

## Data Models (ClaudeCodeModels.cs)

```csharp
enum AiProvider { ClaudeCode, ClaudeCodeWSL, Codex, CodexNative, CursorAgent, CursorAgentNative, QwenCode, OpenCode, Windsurf }
enum ClaudeModel { Opus, Sonnet, Haiku }
enum WindsurfModel { ClaudeOpus, ClaudeSonnet, Codex, GeminiPro }
enum EffortLevel { Auto, Low, Medium, High, Max }
enum TerminalType { CommandPrompt, WindowsTerminal }
```

Key settings: `SendWithEnter`, `SplitterPosition` (236px default), `SelectedProvider`, `SelectedClaudeModel`, `SelectedWindsurfModel`, `PromptHistory` (max 50), `AutoOpenChangesOnPrompt`, `ClaudeDangerouslySkipPermissions`, `CodexFullAuto`, `WindsurfDangerousMode`, `SelectedEffortLevel`, `CustomWorkingDirectory`, `SelectedTerminalType`, `IsTerminalDetached`, `PromptFontSize` (8–24pt), `TerminalZoomDelta`, `InvertLayout`

---

## Supported AI Providers

| Provider | Enum | Platform | Executable | Exit Command |
|----------|------|----------|-----------|-------------|
| Claude Code | `ClaudeCode` | Windows | `claude` | `exit` |
| Claude Code (WSL) | `ClaudeCodeWSL` | WSL | `claude` | `exit` |
| Codex | `CodexNative` | Windows | `codex` | Double CTRL+C |
| Codex (WSL) | `Codex` | WSL | `codex` | Double CTRL+C |
| Cursor Agent | `CursorAgentNative` | Windows | `agent.exe` / `agent.cmd` | `exit` |
| Cursor Agent (WSL) | `CursorAgent` | WSL | `cursor-agent` | `exit` |
| Qwen Code | `QwenCode` | Windows | `qwen` | `/quit` |
| Open Code | `OpenCode` | Windows | `opencode` | `exit` |
| Windsurf (WSL) | `Windsurf` | WSL | `devin` | `exit` |

**Plugin**: Caveman (JuliusBrussee/caveman) — installable into Claude Code sessions via model menu

---

## Key GUIDs

| Identifier | GUID |
|-----------|------|
| Package | `3fa29425-3add-418f-82f6-0c9b7419b2ca` |
| VSIX Identity | `87de5d13-743e-46b3-b05e-24e1cbeca0c3` |
| Command Set | `11111111-2222-3333-4444-555555555555` |
| Detached Terminal Window | `B2C3D4E5-F6A7-8901-BCDE-FA2345678901` |
| Tool Window Command ID | `0x0100` |

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.VisualStudio.SDK | 17.0.32112.339 | VS extensibility APIs |
| Microsoft.VSSDK.BuildTools | 17.14.2101 | VSIX build tools |
| Newtonsoft.Json | 13.0.3 | Settings serialization |
| DiffPlex | 1.7.2 | Diff computation |

---

## Adding a New AI Provider (Checklist)

1. **`ClaudeCodeModels.cs`**: Add to `AiProvider` enum; add settings property if needed
2. **`ProviderManagement.cs`**: Add detection method, cache logic, install instructions, notification flag, menu handlers, `UpdateProviderSelection()`, `ProviderContextMenu_Opened()`
3. **`Terminal.cs`**: Add command building in `StartEmbeddedTerminalAsync()` (both CMD and WT paths), `providerTitle` switch, `InitializeTerminalAsync()`, `RestartTerminalWithSelectedProviderAsync()`, `UpdateAgentButton_Click()`, `Get{Provider}Command()`
4. **`TerminalIO.cs`**: Add Enter key behavior in `SendEnterKey()`; add to `isOtherWSLProvider` if WSL
5. **`UserInput.cs`**: Add to `isWSLProvider` check for WSL path conversion
6. **`Detach.cs`**: Add to `GetCurrentProviderName()` switch
7. **`ClaudeCodeControl.xaml`**: Add context menu item; add settings item if provider has flags
8. **`README.md`**: Document in Features, System Requirements, AI Provider Menu, Updating sections
