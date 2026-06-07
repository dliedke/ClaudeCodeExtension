# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

**Visual Studio Extension (VSIX)** for VS 2022/2026 — integrates AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Open Code, Windsurf, PI) via embedded terminal (Win32 `SetParent` interop).

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com) | **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 10.76 | **Target Framework**: .NET Framework 4.7.2

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
│   ├── ClaudeCodeControl.AgentCompletion.cs # "On Agent Finish": console-idle completion watcher, notify (info bar) + actions
│   ├── ClaudeCodeControl.AtMention.cs   # "@" file/folder picker in the prompt box (workspace index + popup)
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
- **WPF keyboard-focus reclaim** (issue #65): `SetParent`ing the terminal (separate-process window) into VS joins that process's input queue with the VS UI thread, so keyboard focus is shared state. If it gets stuck on the terminal, WPF's `Focus()` won't always move native focus back — the prompt stops accepting typing (caret stops blinking) and the provider menu can't be arrow-navigated until VS restarts. `ClaudeCodeControl_PreviewMouseLeftButtonDown` (tunneling, fires before any child handles the click) calls `ReclaimWpfKeyboardFocusIfStuck()`: when `GetFocus() != HwndSource.Handle` it `SetFocus`es the WPF host, so one click anywhere restores typing. No-op when WPF already owns focus

**Command patterns**:
```
Windows: cmd.exe /k chcp 65001 >nul && cd /d "{dir}" && ping localhost -n 3 >nul && cls && {command}
WSL:     cmd.exe /k chcp 65001 >nul && cls && wsl bash -lic "cd {wslPath} && {command}"
```

**WSL path conversion** (`ConvertToWslPath()`): `\\wsl.localhost\distro\path` → `/path`, `C:\...` → `/mnt/c/...`

**WSL shell mode**: Uses `bash -lic` (login + interactive) to load `.profile`/`.bash_profile` PATH entries — applies to all WSL providers (Claude Code WSL, Codex WSL, Cursor Agent WSL, Windsurf)

### Provider Detection (ProviderManagement.cs)

- **Caching**: `_providerCache` (5-min TTL), separate `_wslCache` for WSL install status; `_cacheLock` for synchronized access; `IsCacheValid()` checks expiry
- **Claude Code detection**: Two-tier — first checks native path (`%USERPROFILE%\.local\bin\claude.exe`), then falls back to `where claude` (PATHEXT-aware, finds both `claude.exe` from winget and `claude.cmd` from NPM)
- **WSL detection**: `bash -lc` (login shell) for `which` — avoids `.bashrc` noise; retries 2x with 8s/20s timeouts for cold boot
- **PI detection**: `IsPiAvailableAsync()` runs `cmd /c where pi` (3s timeout), available when exit code 0 and stdout non-empty; native Windows NPM tool (`@earendil-works/pi-coding-agent`), TUI — paste uses Shift+Right-click and `WM_CHAR` Enter like Open Code
- **Antigravity detection**: `IsAntigravityAvailableAsync()` runs `cmd /c where agy` (3s timeout, PATH refreshed from registry), available when exit code 0 and stdout non-empty; Google's native Windows agent (install `irm https://antigravity.google/cli/install.ps1 | iex` to `%LocalAppData%\agy`, launched with `agy`). Runs in conhost, `WM_CHAR` Enter like Claude Code. **Paste workaround**: Antigravity disables conhost `ENABLE_QUICK_EDIT_MODE` at startup (re-disables faster than `SetConsoleMode` can restore), so a plain right-click opens the context menu instead of pasting. Dedicated paste branch right-clicks to open the menu, then Down ×3 → Enter via `keybd_event` to land on Paste (menu order Mark/Copy[disabled]/Paste/…; arrow nav stops on disabled items; locale-agnostic). Delays generous (500ms after right-click, 150ms between keys) — rushing drops keys. Excluded from the `isCommandPrompt` cancel-selection right-click
- **Early-exit logic**: Stops retrying only when stdout has content (ignores stderr-only warnings)
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
- **Clipboard verification**: `SetClipboardAndVerifyAsync()` calls `Clipboard.SetText` then reads it back before each paste — guards against a clipboard manager (Win+V, Ditto, Office, RDP redirection) overwriting it mid-send. Tolerant comparison (`ClipboardTextMatches`): normalizes line endings, ignores trailing `\0`/`\n` from CF_UNICODETEXT round-trips. Retries `ClipboardVerifyRetries` (3) with 150ms backoff. On persistent failure it logs (`LogClipboardLockOwner`) and pastes anyway — silently bad pastes are rare and aborting blocks too many legitimate sends (issue #59). Users hitting contention should enable `DisableClipboardSend` (issue #61)

### Claude Usage (Usage.cs / ClaudeUsageControl / ClaudeUsageToolWindow)

- **Tool window**: Embeds `claude.ai/settings/usage` in a WebView2 control (`ClaudeUsageToolWindow`, GUID `C3D4E5F6-...`); opened via toolbar or menu
- **X-button behavior**: Intercepted via `IVsWindowFrameNotify2.OnClose` — calls `frame.Hide()` and returns `E_ABORT` so the WebView2 scraper keeps running in the background
- **`ForceClose()`**: Toolbar toggle calls this to fully destroy the window (WebView2 disposed); distinct from X-button hide
- **Inline usage bars**: Mini progress bars in the prompt panel showing session/weekly usage; populated from `UsageSnapshot`; hidden when scraping fails or user not signed in
- **Snapshot caching**: Last successful scrape serialized as JSON to `LastUsageJson` + `LastUsageTimestamp`; restored on startup so bars render immediately with stale data while fresh fetch runs
- **Auto-refresh**: `UsageAutoRefreshSeconds` setting (0 = manual); page reload triggered by visible tool window's scraper — no background WebView2 to avoid focus contention
- **Session restore**: `UsageWindowOpened` persisted; `InitializeUsageMonitoring()` auto-reopens the window and reloads data 2s after solution load
- **User-data folder (persistence)**: A single fixed `%LocalAppData%\ClaudeCodeExtension\WebView2` folder holds the WebView2 profile so cookies/localStorage/IndexedDB survive a VS restart (devenv gets a new PID each launch — the old per-PID folder started every session logged-out, issue #62). `ClaudeUsageWebViewEnvironment.GetOrCreateAsync(primary, fallback)` falls back to a per-PID `WebView2_<pid>` folder only if `CreateAsync` throws because another VS process holds the lock. `CleanupStaleWebView2Folders()` reclaims dead `WebView2_*` folders (glob excludes the fixed `WebView2`). `shared_cookies.json` (`Load/SaveSharedCookiesAsync`) is a secondary fallback restore path
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
- **Themed ComboBox** (On-Agent-Finish "Action" picker): a standalone dialog doesn't inherit VS's ComboBox styling, and the default ComboBox/ComboBoxItem templates paint their own system selection/hover on top of any `Background`, so a style trigger can't recolor them. `BuildThemedComboResources()` injects the VS palette colors into a flat `ComboBox`+`ComboBoxItem` template (`ComboBoxTemplateXaml`, parsed via `XamlReader.Parse`) — dark toggle, readable text, hover derived from the theme background (`ComputeAtHoverBrush`) instead of system blue
- **Layout swap helper**: `ApplyInvertLayoutChange()` in Settings.cs is the only entry point for inverting the layout — the old `InvertLayoutMenuItem_Click` is gone since the XAML field was removed

### Session History (SessionHistory.cs)

- **Scope**: Claude Code (native) and Claude Code (WSL) only — other providers don't use this transcript format
- **Toolbar button**: `SessionHistoryButton` — visible only when a Claude Code provider is active (`RefreshSessionHistoryButton()`)
- **Path encoding**: `EncodeClaudeProjectPath()` replicates Claude Code's filesystem encoding: every non-alphanumeric char → `-`. Example: `C:\Users\Daniel` → `C--Users-Daniel`
- **Directory resolution**: Native → `%USERPROFILE%\.claude\projects\<encoded-cwd>`; WSL → shells out `wslpath -w "$HOME/.claude/projects/<encoded>"` to get a `\\wsl.localhost\` UNC path readable by .NET
- **JSONL parsing**: `ParseSessionFile()` reads with `FileShare.ReadWrite` (so the active session file isn't locked); extracts first user-typed message as preview, skips `tool_result`/`image` parts, sums `input_tokens + output_tokens` from assistant lines
- **Dialog**: WPF modal built programmatically; loads sessions async after open (shows "Loading…" placeholder); supports Resume (restart terminal with `--resume <uuid>`), Resume Last Session (`--continue`), Delete, Refresh
- **Resume flow**: `ResumeSessionAsync()` sets `_pendingResumeSessionId` on the settings object → `GetClaudeCommand()` injects `--resume <id>` or `--continue` on the next terminal start; provider is forced to match the session's origin (native vs WSL)

### On Agent Finish (AgentCompletion.cs)

- **Scope**: any agent running in the **Command Prompt (conhost)** terminal — detection reads the console screen buffer, so it is provider-agnostic. Gated out for Windows Terminal (`SelectedTerminalType != CommandPrompt`), whose buffer lives in a separate process the console API can't read. Default disabled. Configured in its own window (`ShowAgentFinishSettingsDialogAsync`, AgentFinishDialog.cs) opened from a button in the consolidated Settings dialog
- **Global default + per-solution override**: the effective config is resolved by `GetEffectiveAgentFinish()` — returns `_settings.ProjectAgentFinish[<solution name>]` when the open solution (keyed by `.sln` name via `GetCurrentSolutionName()`) has an entry, else the global `_settings.AgentFinish`. The watcher captures the effective config into `_watchedAgentFinish` at arm time so a mid-turn solution switch can't swap it. The dialog edits in-memory working copies (`CloneAgentFinish`) and on OK writes the global back and upserts/removes the per-solution entry
- **Console-attach leak guard** (critical): the watcher `AttachConsole`s **VS itself** to the agent's console each tick. If VS is ever left attached, the **next `conhost.exe` spawn fails to create its window and the embedded terminal renders blank** — most visibly after switching solutions. Two guards: `ResetAgentCompletionForSolutionChange()` stops the watcher + dismisses the info bar on `OnBeforeCloseSolution` and at the start of the workspace-changed branch in `OnWorkspaceDirectoryChangedAsync`; and `EnsureNoConsoleAttached()` calls `FreeConsole` under a bounded `_consoleSnapshotLock` (`Monitor.TryEnter` with timeout, so a slow capture can't freeze the UI thread; skips if a capture holds the lock since it frees itself), run after `StopExistingTerminalAsync()` in `StartEmbeddedTerminalAsync` and at the start of `ExecuteAgentFinishActionAsync` before every launch/Run (no-op when VS has no console)
- **Arming**: `ArmAgentCompletionWatcherAsync()` is fired (fire-and-forget) at the end of `SendButton_Click`. Stores the conhost PID (`cmdProcess.Id`), takes an initial screen snapshot and send time, then starts `_agentCompletionTimer` (1 s `DispatcherTimer`). For Claude Code it also records a token baseline (newest `*.jsonl` via `CountTranscriptTokens`) to enrich the notification — **not** used for detection. Re-arming resets state; `StopAgentCompletionTimer()` also runs from `CleanupResources()`
- **Console-client PID**: the terminal launches as `conhost.exe`, but `AttachConsole` needs a console *client*, so `ResolveConsoleClientPid()` derives the cmd.exe PID from `GetWindowThreadProcessId(terminalHandle)` (returns the client by Windows back-compat) and falls back to the conhost's first ToolHelp32 child. Resolved fresh each sample so a restart can't pin a dead PID
- **Completion detection** (`OnAgentCompletionTimerTick` → `TryCaptureConsoleText`): each tick resolves the client PID, then `SetConsoleCtrlHandler(NULL,TRUE)` (shield VS from a user Ctrl+C on the shared console) → `AttachConsole(clientPid)` → `CreateFile("CONOUT$")` → read the visible window via `ReadConsoleOutputCharacter` → `FreeConsole` → restore Ctrl+C (all under `_consoleSnapshotLock`, tightly scoped, no defensive pre-`FreeConsole`). The visible text **plus cursor position** is FNV-1a hashed (`ComputeStableHash`); a changed hash ⇒ agent working (spinner/timer/cursor moves). Fires only when activity was seen this turn **and** the hash held unchanged ≥ `IdleSeconds` (default 3, clamped 2–120). A dead console PID or a 30-min cap disarms. `TryCaptureConsoleHash()` is a thin wrapper kept for the arm-time baseline. **Waiting-for-input suppression**: a static y/n or selection prompt also reads as idle, so the settled screen is classified by `LooksLikeAgentInputPrompt()` (keyword list `AgentPromptKeywords` + `❯`/numbered-menu detection over the bottom ~18 lines); on a match the tick returns **without firing or disarming**, so it fires on the real completion once the user answers. Conservative by design — rather miss an odd prompt than suppress a real completion. **Typing-interference guard**: the capture briefly `AttachConsole`s VS, which can disturb keystrokes, so each tick skips the read while the user is *actively typing* — gated on a recent keystroke (`_lastTerminalKeyUtc`, set in the keyboard hook when the terminal is focused; within `TerminalTypingGuardMs`=1500) **and** `IsTerminalFocused()`, then pushes `_lastConsoleChangeUtc` forward. Mere focus is not enough (pasting a prompt leaves the terminal focused but un-typed — a focus-only gate stalled the whole turn until the user clicked away, the ~15s lag)
- **Notify**: `OnAgentTurnCompletedAsync` plays a chime (opt-in) and shows a VS **main-window info bar** (`ShowAgentFinishNotificationAsync` via `__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost` + `SVsInfoBarUIFactory`) reading `Agent finished · <duration>` (+`<Δtokens>` only for Claude), shown even when the tool window is hidden. `AgentFinishInfoBarEvents` (`IVsInfoBarUIEvents`) handles close/unadvise and the action hyperlink
- **Actions** (`ExecuteAgentFinishActionAsync`): Build/Rebuild/Run/RunWithoutDebugging/RunTests → `dte.ExecuteCommand(...)`; RunScript → `Process.Start` (`UseShellExecute`) in the workspace dir; SendToAgent → `SendTextToTerminalAsync`. `RequireFileChanges` gates on `git status --porcelain` in `_gitRepositoryRoot` (non-git ⇒ never blocks). `Confirm` (default true) surfaces the action as an info-bar button instead of auto-running
- **Console interop** (Interop.cs): reuses `AttachConsole`/`FreeConsole`/`CloseHandle`/`COORD`; adds `CreateFile`, `GetConsoleScreenBufferInfo`, `ReadConsoleOutputCharacterW`, `SetConsoleCtrlHandler`, `Beep`, and `SMALL_RECT`/`CONSOLE_SCREEN_BUFFER_INFO`

### "@" File/Folder Picker (AtMention.cs)

- **Trigger**: `PromptTextBox`'s `TextChanged` (wired in XAML) calls `UpdateAtMentionPopup()`, which detects an `@` token under the caret (`@` at text start or after whitespace, followed by non-whitespace). Always on; no setting
- **Index**: `EnumerateWorkspaceEntries()` walks `GetWorkspaceDirectoryAsync()` on a background thread, skipping build/VCS/package dirs (`AtIgnoredDirs`) and reparse points, returning workspace-relative `/`-separated paths (folders carry a trailing `/`), capped at 8000. Cached per-root with a 30 s TTL (`EnsureAtEntriesAsync`); a stale index refreshes in the background while current results still show. First trigger shows an "Indexing…" row, then `EnsureThenRefilterAsync` re-runs the filter
- **Popup**: a programmatic `Popup` + `ListBox` (`EnsureAtPopup`), positioned at the caret via `GetRectFromCharacterIndex`. Themed with `GetThemeBrushes` + a derived hover brush (`ComputeAtHoverBrush`) so selected/hover rows stay readable in a standalone popup (no system-blue). Closes on real focus loss but not when focus enters the popup (`IsInsideAtPopup`), so a mouse click can commit first
- **Keys**: `HandleAtMentionKey()` runs at the top of `PromptTextBox_PreviewKeyDown` (before history nav / send-on-Enter) and consumes Up/Down/Enter/Tab/Esc while the popup is open
- **Ranking** (`RankAtEntries`): a query may contain `/` for folder drill-down — the part after the last `/` matches the entry name and the prefix constrains the subtree; name prefix-matches rank above name/path substring matches
- **Insert** (`CommitAtSelection`): replaces the typed `@query` with `@<relative-path>`; a file appends a space and closes, a folder leaves the caret in place and re-opens the picker to drill in. Relative paths resolve for every provider (terminal cwd = workspace), so no WSL conversion. `_atSuppressTextChanged` guards the programmatic edit

---

## Data Models (ClaudeCodeModels.cs)

```csharp
enum AiProvider { ClaudeCode, ClaudeCodeWSL, Codex, CodexNative, CursorAgent, CursorAgentNative, OpenCode, Windsurf, Pi, Antigravity }
enum ClaudeModel { Opus, Sonnet, Haiku }
enum WindsurfModel { ClaudeOpus, ClaudeSonnet, Codex, GeminiPro }
enum EffortLevel { Auto, Low, Medium, High, Max }
enum TerminalType { CommandPrompt, WindowsTerminal }
enum AgentFinishActionType { None, BuildSolution, RebuildSolution, Run, RunWithoutDebugging, RunTests, RunScript, SendToAgent }
class CustomCommand { Name, Command }
class AgentFinishConfig { Enabled, PlaySound, ShowToast, IdleSeconds, Action, ScriptOrCommand, RequireFileChanges, Confirm }
class PromptHistoryEntry { Text, FilePaths }
class SessionInfo { SessionId, FilePath, Preview, MessageCount, TokenCount, LastModified, Cwd, Provider }
class UsageSnapshot { SessionLabel, SessionReset, SessionPercent, WeeklyLabel, WeeklyReset, WeeklyPercent, HasExtraUsage, ExtraUsageSpent, ExtraUsageReset, ExtraUsagePercent }
```

Key settings: `SplitterPosition` (236px default), `SelectedProvider`, `VisibleProviders` (defaults to `[ClaudeCode]` — controls which agents appear in the provider menu; active provider is always shown regardless), `SelectedClaudeModel`, `SelectedWindsurfModel`, `PromptHistory` (max 50), `AutoOpenChangesOnPrompt`, `ClaudeDangerouslySkipPermissions`, `CodexFullAuto`, `CursorAgentAutoRun`, `WindsurfDangerousMode`, `SelectedEffortLevel`, `CustomWorkingDirectory`, `SelectedTerminalType`, `IsTerminalDetached`, `PromptFontSize` (8–24pt), `TerminalZoomDelta`, `InvertLayout`, `SelectedThemePreference` (Automatic/Dark/Light), `LastAgentTerminalColorArgb` (agent's launched color, used to skip redundant restart prompts), `SkipThemeRestartPrompt` (default false — suppresses the "Theme Changed, restart agent?" prompt entirely), `CustomCommands` (list of `{Name, Command}`), `UsageAutoRefreshSeconds` (0 = manual), `UsageWindowOpened` (auto-reopen on load), `ShowInlineUsageBars` (default true), `LastUsageJson` / `LastUsageTimestamp` (cached snapshot), `SendLargePromptsAsFile` (default false — when true, prompts >1 KB are sent as a file reference instead of inline paste), `AgentFinish` (`AgentFinishConfig` — global "On Agent Finish" default; default disabled), `ProjectAgentFinish` (`Dictionary<string,AgentFinishConfig>` — per-solution overrides keyed by `.sln` name, take precedence over `AgentFinish` when present)

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
| PI | `Pi` | Windows | `pi` | CTRL+D twice |
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
