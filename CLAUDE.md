# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

**Visual Studio Extension (VSIX)** for VS 2022/2026 — integrates AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Open Code, Windsurf, PI) via embedded terminal (Win32 `SetParent` interop).

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com) | **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 10.67 | **Target Framework**: .NET Framework 4.7.2

---

## MANDATORY: Version & Documentation Updates

**Every development session that modifies code MUST update before finishing:**

1. **`Properties/AssemblyInfo.cs`**: Bump `AssemblyVersion` and `AssemblyFileVersion`
2. **`source.extension.vsixmanifest`**: Bump `Version` in `<Identity>` tag
3. **`README.md`**: Add `### Version X.Y` entry at top of `## Version History`
   - **Style**: Short, business-focused. One sentence per bullet (two max). Describe the user-visible feature or fix, not the implementation.
   - **Avoid**: code/file/class/method names, internal selectors, file paths, constants, line numbers, JS snippets, framework jargon (`CoreWebView2`, `INPUT_RECORD`, `NavigationCompleted`, etc.), step-by-step "how it works" explanations, and PR-description-style root-cause analysis.
   - **Keep**: what the user gets ("auto-confirms proxy block screens"), opt-in/opt-out status, and the menu/setting name they interact with.
   - Technical details belong in commit messages and `CLAUDE.md` Architecture section, not in release notes.
   - **Other README sections (Features, System Requirements, Provider Menu, Updating, etc.)**: Edits MUST be minimal. Update only the exact line/bullet affected by the change. Do not rewrite paragraphs, expand explanations, add subsections, reorder content, or restructure tables. If a new provider/setting needs a row, add one row. If a feature description needs a word change, change the word. README is reference doc — keep it slim to avoid file bloat.

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
├── Controls/                            # Partial classes of ClaudeCodeControl
│   ├── ClaudeCodeControl.cs             # Core initialization & orchestration
│   ├── ClaudeCodeControl.Terminal.cs    # Terminal embedding, process init, F5 forwarding
│   ├── ClaudeCodeControl.ProviderManagement.cs  # AI provider detection & switching, Caveman plugin install
│   ├── ClaudeCodeControl.TerminalIO.cs  # Terminal I/O, command execution
│   ├── ClaudeCodeControl.Diff.cs        # Diff view integration, git polling
│   ├── ClaudeCodeControl.UserInput.cs   # Keyboard input, button handlers
│   ├── ClaudeCodeControl.Workspace.cs   # Solution/workspace directory detection
│   ├── ClaudeCodeControl.ImageHandling.cs # Image paste & file attachments
│   ├── ClaudeCodeControl.Settings.cs    # Settings persistence (JSON), layout inversion
│   ├── ClaudeCodeControl.SettingsDialog.cs # Consolidated Settings dialog: behavior, layout, terminal type, theme
│   ├── ClaudeCodeControl.Cleanup.cs     # Resource cleanup, temp dir management
│   ├── ClaudeCodeControl.CustomCommands.cs # User-defined custom commands: configure dialog, toolbar dropdown, dispatch
│   ├── ClaudeCodeControl.Interop.cs     # Win32 API declarations (P/Invoke)
│   ├── ClaudeCodeControl.Theme.cs       # Dark/light theme support
│   ├── ClaudeCodeControl.Detach.cs      # Terminal detach/attach to separate VS tab
│   ├── ClaudeCodeControl.Usage.cs       # Claude usage tool window wiring & inline bars
│   └── ClaudeCodeControl.SessionHistory.cs # Session history dialog: list/resume/delete JSONL transcripts
├── UI/                                  # XAML controls + paired code-behind
│   ├── ClaudeCodeControl.xaml
│   ├── ClaudeUsageControl.xaml(.cs)
│   └── DiffViewerControl.xaml(.cs)
├── ToolWindows/                         # VS tool window hosts
│   ├── ClaudeCodeToolWindow.cs
│   ├── DiffViewerToolWindow.cs
│   ├── DetachedTerminalToolWindow.cs
│   └── ClaudeUsageToolWindow.cs
├── Models/
│   └── ClaudeCodeModels.cs              # Enums & settings class
├── Package/                             # VS package & solution event wiring
│   ├── ClaudeCodeExtensionPackage.cs    # VS package registration
│   └── SolutionEventsHandler.cs         # Solution/project open events
├── Diff/                                # Diff engine
│   ├── DiffComputer.cs
│   ├── FileChangeTracker.cs
│   └── ChangedFile.cs
├── Root (project metadata only):
│   ├── ClaudeCodeExtensionPackage.vsct  # Command table
│   ├── source.extension.vsixmanifest
│   └── ClaudeCodeExtension.csproj / .sln
└── Publishing:
    ├── publish.cmd                      # Automated marketplace deployment script
    └── publishManifest.json             # VS Marketplace metadata
```

**Folder reorg note**: When adding a new XAML control, place both `.xaml` and `.xaml.cs` in `UI/` and add a `<Page Include="UI\Foo.xaml">` entry plus a `<Compile Include="UI\Foo.xaml.cs">` with `<DependentUpon>Foo.xaml</DependentUpon>` to the csproj. Partial-class extensions of `ClaudeCodeControl` live in `Controls/`.

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
 * Autor:  Daniel Carvalho Liedke / Claude Code
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
- **PI detection**: `IsPiAvailableAsync()` runs `cmd /c where pi` (3s timeout), available when exit code 0 and stdout non-empty; native Windows NPM tool (`@earendil-works/pi-coding-agent`), TUI-based — paste uses Shift+Right-click and `WM_CHAR` Enter like Open Code
- **Antigravity detection**: `IsAntigravityAvailableAsync()` runs `cmd /c where agy` (3s timeout, PATH refreshed from registry), available when exit code 0 and stdout non-empty; Google's native Windows agent (installed via `irm https://antigravity.google/cli/install.ps1 | iex` to `%LocalAppData%\agy`, launched with `agy`). Runs in regular conhost and uses `WM_CHAR` Enter like Claude Code. **Paste workaround**: Antigravity disables conhost `ENABLE_QUICK_EDIT_MODE` at startup (so its TUI can capture mouse input), so a plain right-click opens the conhost context menu (Mark/Copy/Paste/…) instead of pasting. `SetConsoleMode` re-attachment via `AttachConsole(conhostPid)` was tried first but Antigravity races us and re-disables it before the click lands. Working approach: dedicated paste branch right-clicks to open the menu, then sends Down → Down → Down → Enter via `keybd_event` (`SendKeyDownUp` helper). The menu order is Mark, Copy (disabled), Paste, Select All, Scroll, Find; Windows menus still stop on disabled items during arrow navigation, so three Downs are needed to land on Paste. Menu order is locale-agnostic; only the labels change. Delays are intentionally generous (500ms after right-click, 150ms between keys) — rushing drops keystrokes. Antigravity is excluded from the `isCommandPrompt` cancel-selection right-click for the same reason
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

### Visible Agents (ProviderManagement.cs)

- **Default**: `VisibleProviders = [ClaudeCode]` keeps the agent menu short out-of-the-box
- **Active-always-visible**: `ApplyProviderMenuVisibility()` shows any provider whose menu item is in `VisibleProviders` OR equals `SelectedProvider`. This guarantees a user who had a non-default agent picked before upgrading never loses access to it
- **Dialog**: "Configure Visible Code Agents..." in the provider context menu. Built programmatically; one checkbox per provider. The active provider's checkbox is force-checked and disabled so the user can't hide the currently-running agent. On OK, all checked checkboxes (including active) are saved into `VisibleProviders`
- **Menu wiring**: `ApplyProviderMenuVisibility()` is called from `ApplyLoadedSettings()` (startup) and `ProviderContextMenu_Opened` (every menu open) so visibility is always current
- **Provider→MenuItem lookup**: `_providerMenuItems` dictionary built lazily once via `GetProviderMenuItems()` so the field references are guaranteed initialized by the XAML parser before first access

### Custom Commands (CustomCommands.cs)

- **Configuration**: Stored as `List<CustomCommand>` (Name + Command) under `CustomCommands` in `claudecode-settings.json`
- **Configure dialog**: Opened via "Configure Custom Commands..." entry in the provider context menu (⚙ button). Built programmatically in WPF (no separate XAML); supports Add / Edit / Remove / Move Up / Move Down with double-click-to-edit
- **Toolbar button**: `CustomCommandsButton` (⚡ icon) — `Visibility="Collapsed"` by default, shown when `_settings.CustomCommands.Count > 0`. Populated by `RefreshCustomCommandsButton()`, called from `ApplyLoadedSettings()` and after the configure dialog closes
- **Dispatch**: Each menu item's `Tag` holds its `CustomCommand`; click handler sends `cmd.Command` verbatim via `SendTextToTerminalAsync()` — works against any active provider
- **Editor validation**: Empty command text rejected; if name is omitted, the command itself is shown as the dropdown label

### Terminal I/O (TerminalIO.cs)

- **Paste mechanism**: Saves full clipboard state → sets text → right-clicks terminal center → sends Enter → restores clipboard
- **Clipboard retry**: Up to 10 retries with 100ms delay for `CLIPBRD_E_CANT_OPEN`
- **Enter key varies by provider**: `WM_CHAR` (Claude/OpenCode), `KEYDOWN/KEYUP` (WSL), double-Enter (Codex)
- **Chunked paste**: `SendTextToTerminalAsync()` splits text longer than `PasteChunkSize` (24 KB) into sequential pastes — each chunk fits the per-chunk post-paste budget (`500–800ms base + min(len/PasteMsPerCharDivisor, MaxExtraPasteDelayMs)`, default 1ms per 5 chars, capped at 5s), so Enter (sent only after the final chunk) can never race ahead of a streaming paste. Small prompts (≤24 KB) run the loop once, equivalent to the old single-shot path. Provider-specific paste + scaled wait extracted into `TriggerPasteAndWaitAsync()` for reuse across chunks
- **Large prompts as file (opt-in)**: When `_settings.SendLargePromptsAsFile` is true and the assembled prompt exceeds ~1 KB, `SendButton_Click` writes the prompt to `%TEMP%\ClaudeCodeVS_Session\<guid>\prompt-<timestamp>.md` and sends only `Read and follow: <path>` instead of the inline text. Bypasses the conhost INPUT_RECORD buffer entirely (which truncates pastes around ~1 KB regardless of chunking), keeping the `Files attached:` list intact. WSL providers get a WSL-converted path. See issue #48
- **Clipboard verification**: `SetClipboardAndVerifyAsync()` calls `Clipboard.SetText` then reads the clipboard back before each paste trigger — guards against a clipboard manager (Win+V history, Ditto, Office clipboard, RDP redirection) overwriting it mid-send. Comparison is tolerant (via `ClipboardTextMatches`): normalizes line endings, ignores trailing `\0`/`\n` from CF_UNICODETEXT round-trips. Retries up to `ClipboardVerifyRetries` (3) with a 150 ms backoff. On persistent failure, behavior is to log to Debug (via `LogClipboardLockOwner`) and proceed with the paste — silently bad pastes are rare in practice and aborting blocks too many legitimate sends (issue #59). Users who keep hitting clipboard contention should enable `DisableClipboardSend` to bypass the clipboard entirely (issue #61)

### Claude Usage (Usage.cs / ClaudeUsageControl / ClaudeUsageToolWindow)

- **Tool window**: Embeds `claude.ai/settings/usage` in a WebView2 control (`ClaudeUsageToolWindow`, GUID `C3D4E5F6-...`); opened via toolbar or menu
- **X-button behavior**: Intercepted via `IVsWindowFrameNotify2.OnClose` — calls `frame.Hide()` and returns `E_ABORT` so the WebView2 scraper keeps running in the background
- **`ForceClose()`**: Toolbar toggle calls this to fully destroy the window (WebView2 disposed); distinct from X-button hide
- **Inline usage bars**: Mini progress bars in the prompt panel showing session/weekly usage; populated from `UsageSnapshot`; hidden when scraping fails or user not signed in
- **Snapshot caching**: Last successful scrape serialized as JSON to `LastUsageJson` + `LastUsageTimestamp`; restored on startup so bars render immediately with stale data while fresh fetch runs
- **Auto-refresh**: `UsageAutoRefreshSeconds` setting (0 = manual); page reload triggered by visible tool window's scraper — no background WebView2 to avoid focus contention
- **Session restore**: `UsageWindowOpened` persisted; `InitializeUsageMonitoring()` auto-reopens the window and reloads data 2s after solution load
- **User-data folder (persistence)**: A single fixed `%LocalAppData%\ClaudeCodeExtension\WebView2` folder holds the WebView2 profile so cookies/localStorage/IndexedDB survive a VS restart (devenv gets a new PID each launch — the old per-PID folder started every session logged-out on the cookie banner, issue #62). `ClaudeUsageWebViewEnvironment.GetOrCreateAsync(primary, fallback)` falls back to a per-PID `WebView2_<pid>` folder only if `CreateAsync` throws because another VS process holds the shared folder's lock. `CleanupStaleWebView2Folders()` reclaims legacy/fallback `WebView2_*` folders whose process is dead; its glob excludes the fixed `WebView2` folder. `shared_cookies.json` (`Load/SaveSharedCookiesAsync`) remains as a secondary cross-instance/fallback restore path
- **Corporate proxy interstitial handling (`TryHandleUrlBlock`)**: Detects when `core.Source` ends with `/urlblock.php` (iboss/Forcepoint/Zscaler style block pages) and injects a JS poller that retries up to 25× at 200ms looking for the Continue submit button — multiple selector fallbacks (`input[type=submit][name=ok]` → generic `input[type=submit]` → `button[type=submit]` → `button[name=ok]` → `form.submit()`). Works while the tool window is hidden because CoreWebView2 processes navigation and JS without a visible rendering surface. Throttled to one click per 3 s. Triggered from both `OnNavigationCompleted` and `OnSourceChanged`

### Settings (Settings.cs)

- **`_isInitializing` guard**: Prevents `SaveSettings()` during `LoadSettings()`
- **`[JsonExtensionData]`**: Preserves unknown JSON properties across DLL versions
- **Layout inversion**: `ApplyLayout()` swaps prompt and terminal grid rows. Outer `MainGrid` row `MinHeight`s are 0 in both orientations so the splitter can be dragged fully to the top or bottom to hide either panel. Inner `PromptSectionGrid` row 0 still uses MinHeight 80 to keep the prompt input usable when the prompt section itself has height. `ApplyLayout()` also hides/shows the terminal GroupBox header and reorders prompt section controls

### Workspace (Workspace.cs)

Priority: DTE solution dir → active project dir → IVsSolution dir → current dir with `.sln`/`.csproj` → My Documents

### Detach (Detach.cs)

Re-parents terminal to/from `DetachedTerminalToolWindow` via `SetParent()`. Auto-reattaches when detached tab is closed.

### Theme (Theme.cs)

- **Two distinct colors**: `terminalPanel.BackColor` is the live WPF panel color; `_terminalAgentColor` is the color the embedded terminal/agent was launched with. Theme-change prompts compare these two — the panel can drift away from the agent color across multiple theme switches without restarting the agent
- **Agent color set only on launch**: `RecordTerminalAgentColor()` is called at the end of both successful embed paths in `StartEmbeddedTerminalAsync` (WT and CMD). It snapshots `terminalPanel.BackColor` and persists it as `LastAgentTerminalColorArgb`. Never updated by `UpdateTerminalTheme` (which only re-tints the panel)
- **Restart prompt skip conditions**: No prompt when (a) a forced theme is active (`SelectedThemePreference != Automatic`), (b) terminal isn't running, or (c) `terminalPanel.BackColor == _terminalAgentColor` — covers two-dark-themes-same-RGB and reverting to a previously-declined color
- **Re-entrancy guard**: `_isShowingThemeRestartPrompt` prevents a second "Theme Changed" `MessageBox` stacking on the first. WPF dispatcher keeps pumping during the modal, so a later debounce tick from another `VSColorTheme.ThemeChanged` event would otherwise open a second dialog
- **Debounce**: 500ms `DispatcherTimer` collapses rapid consecutive theme events into one prompt
- **Forced theme overrides**: `ApplyForcedThemeResources()` injects 9 VS brush keys into the control's `Resources` dictionary so child WPF elements pick up the forced palette via `DynamicResource`. Removed when reverting to Automatic
- **Opt-out**: `SkipThemeRestartPrompt` setting (exposed in the consolidated Settings dialog) short-circuits `OnThemeChangeDebounceTimerTick` entirely — for users who auto-swap themes mid-session (e.g. VS debugging theme on F5) and don't want to be asked

### Consolidated Settings Dialog (SettingsDialog.cs)

- **Single entry point**: `Settings...` menu item in the ⚙ context menu, replacing 7 previously-scattered toggles: Invert Layout, Disable Auto Zoom, Terminal Type, Theme, Send with Enter, Large prompts as file, Auto Open Changes on Send
- **Dialog construction**: Built programmatically (no XAML) via `ShowConsolidatedSettingsDialogAsync()`; reuses `MakeSectionHeader`/`MakeCheckBox`/`MakeRadioButton` helpers and `GetThemeBrushes`/`GetDialogButtonStyle` from CustomCommands.cs
- **Batched apply**: All settings persisted once on OK via a single `SaveSettings()` at the end. Side effects fire in order: prompt button visibility → `ApplyInvertLayoutChange()` → theme repaint (`UpdateTerminalTheme`/`UpdateInlineUsageBarColors`) → at most one terminal restart (only when terminal type changed, or theme changed AND user confirms restart popup — popup itself is gated by `SkipThemeRestartPrompt`, terminal-not-running, and agent-color-already-matches)
- **Windows Terminal validation**: `IsWindowsTerminalAvailableAsync()` runs before persisting a WT selection; reverts to CMD with a `MessageBox` if `wt.exe` isn't on PATH
- **Layout swap helper**: `ApplyInvertLayoutChange()` in Settings.cs is the only entry point for inverting the layout — the old `InvertLayoutMenuItem_Click` is gone since the XAML field was removed

### Session History (SessionHistory.cs)

- **Scope**: Claude Code (native) and Claude Code (WSL) only — other providers don't use this transcript format
- **Toolbar button**: `SessionHistoryButton` — visible only when a Claude Code provider is active (`RefreshSessionHistoryButton()`)
- **Path encoding**: `EncodeClaudeProjectPath()` replicates Claude Code's filesystem encoding: every non-alphanumeric char → `-`. Example: `C:\Users\Daniel` → `C--Users-Daniel`
- **Directory resolution**: Native → `%USERPROFILE%\.claude\projects\<encoded-cwd>`; WSL → shells out `wslpath -w "$HOME/.claude/projects/<encoded>"` to get a `\\wsl.localhost\` UNC path readable by .NET
- **JSONL parsing**: `ParseSessionFile()` reads with `FileShare.ReadWrite` (so the active session file isn't locked); extracts first user-typed message as preview, skips `tool_result`/`image` parts, sums `input_tokens + output_tokens` from assistant lines
- **Dialog**: WPF modal built programmatically; loads sessions async after open (shows "Loading…" placeholder); supports Resume (restart terminal with `--resume <uuid>`), Resume Last Session (`--continue`), Delete, Refresh
- **Resume flow**: `ResumeSessionAsync()` sets `_pendingResumeSessionId` on the settings object → `GetClaudeCommand()` injects `--resume <id>` or `--continue` on the next terminal start; provider is forced to match the session's origin (native vs WSL)

---

## Data Models (ClaudeCodeModels.cs)

```csharp
enum AiProvider { ClaudeCode, ClaudeCodeWSL, Codex, CodexNative, CursorAgent, CursorAgentNative, OpenCode, Windsurf, Pi, Antigravity }
enum ClaudeModel { Opus, Sonnet, Haiku }
enum WindsurfModel { ClaudeOpus, ClaudeSonnet, Codex, GeminiPro }
enum EffortLevel { Auto, Low, Medium, High, Max }
enum TerminalType { CommandPrompt, WindowsTerminal }
class CustomCommand { Name, Command }
class PromptHistoryEntry { Text, FilePaths }
class SessionInfo { SessionId, FilePath, Preview, MessageCount, TokenCount, LastModified, Cwd, Provider }
class UsageSnapshot { SessionLabel, SessionReset, SessionPercent, WeeklyLabel, WeeklyReset, WeeklyPercent, HasExtraUsage, ExtraUsageSpent, ExtraUsageReset, ExtraUsagePercent }
```

Key settings: `SplitterPosition` (236px default), `SelectedProvider`, `VisibleProviders` (defaults to `[ClaudeCode]` — controls which agents appear in the provider menu; active provider is always shown regardless), `SelectedClaudeModel`, `SelectedWindsurfModel`, `PromptHistory` (max 50), `AutoOpenChangesOnPrompt`, `ClaudeDangerouslySkipPermissions`, `CodexFullAuto`, `CursorAgentAutoRun`, `WindsurfDangerousMode`, `SelectedEffortLevel`, `CustomWorkingDirectory`, `SelectedTerminalType`, `IsTerminalDetached`, `PromptFontSize` (8–24pt), `TerminalZoomDelta`, `InvertLayout`, `SelectedThemePreference` (Automatic/Dark/Light), `LastAgentTerminalColorArgb` (agent's launched color, used to skip redundant restart prompts), `SkipThemeRestartPrompt` (default false — suppresses the "Theme Changed, restart agent?" prompt entirely), `CustomCommands` (list of `{Name, Command}`), `UsageAutoRefreshSeconds` (0 = manual), `UsageWindowOpened` (auto-reopen on load), `ShowInlineUsageBars` (default true), `LastUsageJson` / `LastUsageTimestamp` (cached snapshot), `SendLargePromptsAsFile` (default false — when true, prompts >1 KB are sent as a file reference instead of inline paste)

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
| Open Code | `OpenCode` | Windows | `opencode` | `exit` |
| Windsurf (WSL) | `Windsurf` | WSL | `devin` | `exit` |
| PI | `Pi` | Windows | `pi` | `exit` |
| Antigravity | `Antigravity` | Windows | `agy` | Double CTRL+D |

**Plugin**: Caveman (JuliusBrussee/caveman) — installable into Claude Code sessions via model menu

---

## Key GUIDs

| Identifier | GUID |
|-----------|------|
| Package | `3fa29425-3add-418f-82f6-0c9b7419b2ca` |
| VSIX Identity | `87de5d13-743e-46b3-b05e-24e1cbeca0c3` |
| Command Set | `11111111-2222-3333-4444-555555555555` |
| Detached Terminal Window | `B2C3D4E5-F6A7-8901-BCDE-FA2345678901` |
| Claude Usage Tool Window | `C3D4E5F6-A7B8-9012-CDEF-123456789AB1` |
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
8. **`SessionHistory.cs`**: Update `IsClaudeCodeSessionHistoryProvider()` if the new provider supports JSONL session transcripts; call `RefreshSessionHistoryButton()` from `UpdateProviderSelection()`
9. **`README.md`**: Document in Features, System Requirements, AI Provider Menu, Updating sections
