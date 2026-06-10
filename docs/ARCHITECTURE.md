# Architecture — Key Non-Obvious Details

> On-demand reference. Read the relevant section **before editing the file it describes**
> (see the index in `CLAUDE.md` → Architecture). These are the non-obvious gotchas and
> design decisions that aren't apparent from the code alone.

## Terminal Embedding (Terminal.cs)

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
- **Terminal-click activation** (issue #69): a click on the embedded terminal runs `ActivateEmbeddedTerminalOnClick` (from the mouse hook), which raises the VS top-level window (`BringVisualStudioToForegroundIfNeeded`, AttachThreadInput dance to bypass focus-stealing protection) and selects the terminal pane (`IVsWindowFrame.Show()` when `IsTerminalToolWindowActive()` is false). **Do not re-attempt a "don't bring VS to the foreground on terminal click" opt-out** — it shipped in 10.82–10.90 and was removed in 10.91 as infeasible: keyboard input only goes to the foreground thread's focus window, so VS *must* be activated for typing to reach the terminal, and suppression attempts (`MA_NOACTIVATE` from the host panel, `WS_EX_NOACTIVATE` on the terminal — inert on `WS_CHILD` windows, gating `frame.Show()` via `ShowNoActivate()`) either didn't engage or would have broken typing. Windows itself also activates/raises the top-level owner when the cross-process child is clicked, outside the extension's control.
- **Single-click terminal focus** (issue #74): the click natively focuses the terminal child, but both activation steps above make VS move keyboard focus into the WPF tool-window content moments later (`frame.Show()` focuses the pane content; `SetForegroundWindow` restores VS's last focused control) — so the first click on the terminal got its focus silently stolen and a second click was needed before typing (e.g. answering an agent's choice prompt) reached the terminal. When either step actually ran, `ActivateEmbeddedTerminalOnClick` re-asserts terminal focus after the activation settles: await `frame.Show()` → 80 ms → `FocusTerminalPanel` → 80 ms → `SetFocus(terminalHandle)` (same double-assert the zoom replay needs because VS restores focus once more after pane activation). Still a no-op when VS was already foreground and the pane already active — ordinary clicks pay nothing.

**Command patterns**:
```
Windows: cmd.exe /k chcp 65001 >nul && cd /d "{dir}" && ping localhost -n 3 >nul && cls && {command}
WSL:     cmd.exe /k chcp 65001 >nul && cls && wsl bash -lic "cd {wslPath} && {command}"
```

**WSL path conversion** (`ConvertToWslPath()`): `\\wsl.localhost\distro\path` → `/path`, `C:\...` → `/mnt/c/...`

**WSL shell mode**: Uses `bash -lic` (login + interactive) to load `.profile`/`.bash_profile` PATH entries — applies to all WSL providers (Claude Code WSL, Codex WSL, Cursor Agent WSL, Windsurf)

## Provider Detection (ProviderManagement.cs)

- **Caching**: `_providerCache` (5-min TTL), separate `_wslCache` for WSL install status; `_cacheLock` for synchronized access; `IsCacheValid()` checks expiry
- **Claude Code detection**: Two-tier — first checks native path (`%USERPROFILE%\.local\bin\claude.exe`), then falls back to `where claude` (PATHEXT-aware, finds both `claude.exe` from winget and `claude.cmd` from NPM)
- **WSL detection**: `bash -lc` (login shell) for `which` — avoids `.bashrc` noise; retries 2x with 8s/20s timeouts for cold boot
- **PI detection**: `IsPiAvailableAsync()` runs `cmd /c where pi` (3s timeout), available when exit code 0 and stdout non-empty; native Windows NPM tool (`@earendil-works/pi-coding-agent`), TUI — paste uses Shift+Right-click and `WM_CHAR` Enter like Open Code
- **Antigravity detection**: `IsAntigravityAvailableAsync()` runs `cmd /c where agy` (3s timeout, PATH refreshed from registry), available when exit code 0 and stdout non-empty; Google's native Windows agent (install `irm https://antigravity.google/cli/install.ps1 | iex` to `%LocalAppData%\agy`, launched with `agy`). Runs in conhost, `WM_CHAR` Enter like Claude Code. **Paste workaround**: Antigravity disables conhost `ENABLE_QUICK_EDIT_MODE` at startup (re-disables faster than `SetConsoleMode` can restore), so a plain right-click opens the context menu instead of pasting. Dedicated paste branch right-clicks to open the menu, then Down ×3 → Enter via `keybd_event` to land on Paste (menu order Mark/Copy[disabled]/Paste/…; arrow nav stops on disabled items; locale-agnostic). Delays generous (500ms after right-click, 150ms between keys) — rushing drops keys. Excluded from the `isCommandPrompt` cancel-selection right-click
- **Early-exit logic**: Stops retrying only when stdout has content (ignores stderr-only warnings)
- **Notification flags**: Static booleans (one per provider) ensure install pop-ups show only once per VS session
- **Model menus**: `ModelContextMenu_Opened()` toggles Claude items vs Windsurf items based on active provider

## Caveman Plugin (ProviderManagement.cs)

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

## Visible Agents (ProviderManagement.cs)

- **Default**: `VisibleProviders = [ClaudeCode]` keeps the agent menu short out-of-the-box
- **Active-always-visible**: `ApplyProviderMenuVisibility()` shows any provider whose menu item is in `VisibleProviders` OR equals `SelectedProvider`. This guarantees a user who had a non-default agent picked before upgrading never loses access to it
- **Dialog**: "Configure Visible Code Agents..." in the provider context menu. Built programmatically; one checkbox per provider. The active provider's checkbox is force-checked and disabled so the user can't hide the currently-running agent. On OK, all checked checkboxes (including active) are saved into `VisibleProviders`
- **Menu wiring**: `ApplyProviderMenuVisibility()` is called from `ApplyLoadedSettings()` (startup) and `ProviderContextMenu_Opened` (every menu open) so visibility is always current
- **Provider→MenuItem lookup**: `_providerMenuItems` dictionary built lazily once via `GetProviderMenuItems()` so the field references are guaranteed initialized by the XAML parser before first access

## Custom Commands (CustomCommands.cs)

- **Configuration**: Stored as `List<CustomCommand>` (Name + Command) under `CustomCommands` in `claudecode-settings.json`
- **Configure dialog**: Opened via "Configure Custom Commands..." entry in the provider context menu (⚙ button). Built programmatically in WPF (no separate XAML); supports Add / Edit / Remove / Move Up / Move Down with double-click-to-edit
- **Toolbar button**: `CustomCommandsButton` (⚡ icon) — `Visibility="Collapsed"` by default, shown when `_settings.CustomCommands.Count > 0`. Populated by `RefreshCustomCommandsButton()`, called from `ApplyLoadedSettings()` and after the configure dialog closes
- **Dispatch**: Each menu item's `Tag` holds its `CustomCommand`; click handler sends `cmd.Command` verbatim via `SendTextToTerminalAsync()` — works against any active provider
- **Editor validation**: Empty command text rejected; if name is omitted, the command itself is shown as the dropdown label

## Custom CLI Paths (CliPaths.cs)

- **Purpose**: Per-provider override pointing each agent at a specific CLI executable instead of relying on PATH / the built-in native install location — for tools installed in non-standard locations
- **Configuration**: Stored as `Dictionary<AiProvider, string>` under `CustomExecutablePaths` in `claudecode-settings.json`; empty/whitespace entries are treated as unset
- **Settings tab**: Lives as the **CLI Paths** tab in the consolidated Settings dialog (`BuildCliPathsTabContent` builds the tab content; the host calls `ApplyCliPathChanges` on OK). Built programmatically in WPF (no XAML); a header note explains empty paths use default detection, then one aligned row per provider (from the static `CliPathProviders` table) with label + textbox; native (non-WSL) rows also get a "Browse..." `OpenFileDialog`. The Browse column width is reserved on every row so textboxes align even on WSL rows (no button). Native textboxes turn red when the path doesn't exist (`File.Exists`); WSL paths can't be probed from Windows so they're trusted as-is. On OK, `ConfirmCliPathsBeforeClose` blocks the close (keeps the dialog open) with a Yes/No warning when any native path doesn't exist — Yes saves anyway, No returns to fix it. Only actually-changed entries are written; clearing a field removes it
- **Resolution** (`ResolveProviderExecutable`): returns the configured path (native → double-quoted for cmd.exe; WSL → single-quoted only when it contains spaces, since it sits inside a double-quoted `bash -lic` string) or the default command when unset. Wired into `GetClaudeCommand`/`GetCodexCommand`/`GetWindsurfCommand`/`GetAntigravityCommand`/`GetCursorAgent*Command` and the OpenCode/Pi branches of `StartEmbeddedTerminalAsync` (both CMD and WT paths)
- **Detection** (`CustomExecutableConfigured`): each `Is*AvailableAsync()` short-circuits to `true` when a custom path is configured (native validated with `File.Exists`, WSL trusted), so a tool off-PATH still reports available
- **Apply**: `ApplyCliPathChanges` returns the list of providers whose path changed. Any change runs `ClearProviderCache()` (detection results differ), but the terminal restart (folded into the Settings dialog's single `RestartTerminalWithSelectedProviderAsync()`) fires **only when the active provider's** path changed — editing an inactive provider's path doesn't disturb the running agent

## Terminal I/O (TerminalIO.cs)

- **Paste mechanism**: Saves full clipboard state → sets text → right-clicks terminal center → sends Enter → restores clipboard
- **Clipboard retry**: Up to 10 retries with 100ms delay for `CLIPBRD_E_CANT_OPEN`
- **Enter key varies by provider**: `WM_CHAR` (Claude/OpenCode), `KEYDOWN/KEYUP` (WSL), double-Enter (Codex)
- **Chunked paste**: `SendTextToTerminalAsync()` splits text over `PasteChunkSize` (24 KB) into sequential pastes; each chunk waits a scaled post-paste budget (`500–800ms base + min(len/divisor, cap)`) so Enter (after the final chunk) never races a streaming paste. Provider paste + wait live in `TriggerPasteAndWaitAsync()`
- **Large prompts as file (opt-in)**: when `SendLargePromptsAsFile` and the prompt exceeds ~1 KB, `SendButton_Click` writes it to `%TEMP%\ClaudeCodeVS_Session\<guid>\prompt-<ts>.md` and sends `Read and follow: <path>` — bypasses the conhost INPUT_RECORD buffer (truncates ~1 KB regardless of chunking), keeps `Files attached:` intact. WSL gets a converted path. Issue #48
- **Clipboard verification**: `SetClipboardAndVerifyAsync()` sets then reads back the clipboard before each paste — guards against a clipboard manager (Win+V, Ditto, RDP) overwriting it mid-send. Tolerant compare (`ClipboardTextMatches`) normalizes line endings/trailing `\0\n`. Retries 3× with 150ms backoff; on persistent failure logs and pastes anyway (issue #59). Contention → enable `DisableClipboardSend` (issue #61)

## Claude Usage (Usage.cs / ClaudeUsageControl / ClaudeUsageToolWindow)

- **Tool window**: Embeds `claude.ai/settings/usage` in a WebView2 control (`ClaudeUsageToolWindow`, GUID `C3D4E5F6-...`); opened via toolbar or menu
- **X-button behavior**: Intercepted via `IVsWindowFrameNotify2.OnClose` — calls `frame.Hide()` and returns `E_ABORT` so the WebView2 scraper keeps running in the background
- **`ForceClose()`**: Toolbar toggle calls this to fully destroy the window (WebView2 disposed); distinct from X-button hide
- **Inline usage bars**: Mini progress bars in the prompt panel showing session/weekly usage; populated from `UsageSnapshot`; hidden when scraping fails or user not signed in
- **Snapshot caching**: Last successful scrape serialized as JSON to `LastUsageJson` + `LastUsageTimestamp`; restored on startup so bars render immediately with stale data while fresh fetch runs
- **Auto-refresh**: `UsageAutoRefreshSeconds` setting (0 = manual); page reload triggered by visible tool window's scraper — no background WebView2 to avoid focus contention
- **Session restore**: `UsageWindowOpened` persisted; `InitializeUsageMonitoring()` auto-reopens the window and reloads data 2s after solution load
- **User-data folder (persistence)**: A single fixed `%LocalAppData%\ClaudeCodeExtension\WebView2` folder holds the WebView2 profile so cookies/localStorage/IndexedDB survive a VS restart (devenv gets a new PID each launch — the old per-PID folder started every session logged-out, issue #62). `ClaudeUsageWebViewEnvironment.GetOrCreateAsync(primary, fallback)` falls back to a per-PID `WebView2_<pid>` folder only if `CreateAsync` throws because another VS process holds the lock. `CleanupStaleWebView2Folders()` reclaims dead `WebView2_*` folders (glob excludes the fixed `WebView2`). `shared_cookies.json` (`Load/SaveSharedCookiesAsync`) is a secondary fallback restore path
- **Corporate proxy interstitial handling (`TryHandleUrlBlock`)**: Detects when `core.Source` ends with `/urlblock.php` (iboss/Forcepoint/Zscaler style block pages) and injects a JS poller that retries up to 25× at 200ms looking for the Continue submit button — multiple selector fallbacks (`input[type=submit][name=ok]` → generic `input[type=submit]` → `button[type=submit]` → `button[name=ok]` → `form.submit()`). Works while the tool window is hidden because CoreWebView2 processes navigation and JS without a visible rendering surface. Throttled to one click per 3 s. Triggered from both `OnNavigationCompleted` and `OnSourceChanged`

## Settings (Settings.cs)

- **`_isInitializing` guard**: Prevents `SaveSettings()` during `LoadSettings()`
- **`[JsonExtensionData]`**: Preserves unknown JSON properties across DLL versions
- **Layout inversion**: `ApplyLayout()` swaps prompt and terminal grid rows. Outer `MainGrid` row `MinHeight`s are 0 in both orientations so the splitter can be dragged fully to the top or bottom to hide either panel. Inner `PromptSectionGrid` row 0 still uses MinHeight 80 to keep the prompt input usable when the prompt section itself has height. `ApplyLayout()` also hides/shows the terminal GroupBox header and reorders prompt section controls
- **Prompt resize grip**: `PromptResizeGrip` (a `Thumb` overlaid on the bottom edge of `PromptGroupBox`) lets the user drag the prompt area taller/shorter directly. `PromptResizeGrip_DragDelta` drives the same `SetSplitterPosition()` as the main splitter (controls/chips/usage rows are fixed-height, so the top row tracks box height 1:1); `DragCompleted` persists via `SaveSplitterPositionAfterLayout()`. `SetPromptResizeGripVisible()` shows it only in the default top/bottom layout (hidden when inverted or side-by-side, where the box edge isn't adjacent to the terminal boundary)

## Workspace (Workspace.cs)

Priority: DTE solution dir → active project dir → IVsSolution dir → current dir with `.sln`/`.csproj` → My Documents

## Detach (Detach.cs)

Re-parents terminal to/from `DetachedTerminalToolWindow` via `SetParent()`. Auto-reattaches when detached tab is closed.

## Theme (Theme.cs)

- **Two distinct colors**: `terminalPanel.BackColor` is the live WPF panel color; `_terminalAgentColor` is the color the embedded terminal/agent was launched with. Theme-change prompts compare these two — the panel can drift away from the agent color across multiple theme switches without restarting the agent
- **Agent color set only on launch**: `RecordTerminalAgentColor()` is called at the end of both successful embed paths in `StartEmbeddedTerminalAsync` (WT and CMD). It snapshots `terminalPanel.BackColor` and persists it as `LastAgentTerminalColorArgb`. Never updated by `UpdateTerminalTheme` (which only re-tints the panel)
- **Restart prompt skip conditions**: No prompt when (a) a forced theme is active (`SelectedThemePreference != Automatic`), (b) terminal isn't running, or (c) `terminalPanel.BackColor == _terminalAgentColor` — covers two-dark-themes-same-RGB and reverting to a previously-declined color
- **Re-entrancy guard**: `_isShowingThemeRestartPrompt` prevents a second "Theme Changed" `MessageBox` stacking on the first. WPF dispatcher keeps pumping during the modal, so a later debounce tick from another `VSColorTheme.ThemeChanged` event would otherwise open a second dialog
- **Debounce**: 500ms `DispatcherTimer` collapses rapid consecutive theme events into one prompt
- **Forced theme overrides**: `ApplyForcedThemeResources()` injects 9 VS brush keys into the control's `Resources` dictionary so child WPF elements pick up the forced palette via `DynamicResource`. Removed when reverting to Automatic
- **Custom background color** (`ThemePreference.Custom` + `CustomThemeColorArgb`, default #F4ECFF): `GetCustomThemeColor()` returns the color; `ApplyForcedThemeResources()` derives fg (black/white) + hover/border shades from its brightness; `SaveAndSetConsoleColorsRegistry()` writes the color (BGR) to `ColorTable00`, fg index 7 (`ColorTable07`=black/white). Set in Settings → Theme via hex box + `ColorDialog`
- **Opt-out**: `SkipThemeRestartPrompt` setting (exposed in the consolidated Settings dialog) short-circuits `OnThemeChangeDebounceTimerTick` entirely — for users who auto-swap themes mid-session (e.g. VS debugging theme on F5) and don't want to be asked

## Consolidated Settings Dialog (SettingsDialog.cs)

- **Single entry point**: `Settings...` menu item in the ⚙ context menu, consolidating all previously-scattered toggles
- **Tabbed layout**: `ShowConsolidatedSettingsDialogAsync()` builds a themed `TabControl` with six tabs via the local `AddTab(header)` helper: **Behavior** (send-key mode, large-prompt/clipboard sending, auto-open changes, don't-bring-to-front, prompt font size, On Agent Finish button), **Layout** (prompt-panel position, disable auto zoom), **Terminal** (terminal type), **Theme** (theme preference incl. custom-color hex box + `ColorDialog` picker, skip-restart-prompt), **Usage** (show inline bars, auto-refresh), **CLI Paths** (per-provider custom CLI executable paths — content built by `BuildCliPathsTabContent`, applied via `ApplyCliPathChanges`). Each tab is a `ScrollViewer` + `StackPanel`
- **Send-key mode**: a 3-way radio group ("Send prompt with") maps to two bools — `SendWithEnter` (Enter sends) / `SendWithCtrlEnter` (Ctrl+Enter sends, Enter = newline) / both false (button only). Keyboard handling lives in `PromptTextBox_PreviewKeyDown`. See issue #70
- **Dialog construction**: Built programmatically (no XAML); reuses `MakeSectionHeader`/`MakeCheckBox`/`MakeRadioButton`/`MakeThemedComboBox` helpers and `GetThemeBrushes`/`GetDialogButtonStyle` from CustomCommands.cs
- **Batched apply**: all settings persisted once on OK via a single `SaveSettings()`. Side effects fire in order: prompt button visibility → font size → `ApplyLayoutSettingsChange()` → theme repaint (`UpdateTerminalTheme`/`UpdateInlineUsageBarColors`) → usage refresh → at most one terminal restart (terminal-type change, or theme/custom-color change AND user confirms the restart popup — popup gated by `SkipThemeRestartPrompt`, terminal-not-running, agent-color-matches)
- **Reset to Defaults**: a left-aligned button in the bottom row restores every control on the dialog to its model default (after a confirm). Nothing persists until OK
- **Cross-tab sync**: the "Disable clipboard" checkbox (Behavior tab) is enabled only while Command Prompt is selected (Terminal tab) — `SyncDisableClipboardAvailability()` is wired to the terminal-type radios and unchecks/greys the box for Windows Terminal
- **Windows Terminal validation**: `IsWindowsTerminalAvailableAsync()` runs before persisting a WT selection; reverts to CMD with a `MessageBox` if `wt.exe` isn't on PATH
- **Themed templates**: a standalone dialog doesn't inherit VS's ComboBox/TabControl styling (default templates paint system blue), so they're replaced. `BuildThemedComboResources()`/`BuildThemedTabResources()` inject the VS palette into flat templates parsed via `XamlReader.Parse`; hover/shade derived from the theme bg (`ComputeAtHoverBrush`)

## Session History (SessionHistory.cs)

- **Scope**: Claude Code (native) and Claude Code (WSL) only — other providers don't use this transcript format
- **Toolbar button**: `SessionHistoryButton` — visible only when a Claude Code provider is active (`RefreshSessionHistoryButton()`)
- **Path encoding**: `EncodeClaudeProjectPath()` replicates Claude Code's filesystem encoding: every non-alphanumeric char → `-`. Example: `C:\Users\Daniel` → `C--Users-Daniel`
- **Directory resolution**: Native → `%USERPROFILE%\.claude\projects\<encoded-cwd>`; WSL → shells out `wslpath -w "$HOME/.claude/projects/<encoded>"` to get a `\\wsl.localhost\` UNC path readable by .NET
- **JSONL parsing**: `ParseSessionFile()` reads with `FileShare.ReadWrite` (so the active session file isn't locked); extracts first user-typed message as preview, skips `tool_result`/`image` parts, sums `input_tokens + output_tokens` from assistant lines
- **Dialog**: WPF modal built programmatically; loads sessions async after open (shows "Loading…" placeholder); supports Resume (restart terminal with `--resume <uuid>`), Resume Last Session (`--continue`), Delete, Refresh
- **Resume flow**: `ResumeSessionAsync()` sets `_pendingResumeSessionId` on the settings object → `GetClaudeCommand()` injects `--resume <id>` or `--continue` on the next terminal start; provider is forced to match the session's origin (native vs WSL)

## On Agent Finish (AgentCompletion.cs)

- **Scope**: any agent running in the **Command Prompt (conhost)** terminal — detection reads the console screen buffer, so it is provider-agnostic. Gated out for Windows Terminal (`SelectedTerminalType != CommandPrompt`), whose buffer lives in a separate process the console API can't read. Default disabled. Configured in its own window (`ShowAgentFinishSettingsDialogAsync`, AgentFinishDialog.cs) opened from a button in the consolidated Settings dialog. **UI gating**: the runtime no-op (arm returns early under WT) is backed by the consolidated Settings dialog disabling the "On Agent Finish…" button + showing a hint when the Windows Terminal radio is selected (`SyncAgentFinishAvailability()`, wired to the terminal-type radios exactly like `SyncDisableClipboardAvailability()`), so the feature can't be opened/enabled under WT and never appears silently broken
- **Global default + per-solution override**: the effective config is resolved by `GetEffectiveAgentFinish()` — returns `_settings.ProjectAgentFinish[<solution name>]` when the open solution (keyed by `.sln` name via `GetCurrentSolutionName()`) has an entry, else the global `_settings.AgentFinish`. The watcher captures the effective config into `_watchedAgentFinish` at arm time so a mid-turn solution switch can't swap it. The dialog edits in-memory working copies (`CloneAgentFinish`) and on OK writes the global back and upserts/removes the per-solution entry
- **Console-attach leak guard** (critical): the watcher `AttachConsole`s **VS itself** each tick. If VS is left attached, the **next `conhost.exe` spawn inherits VS's console instead of creating its own window and the terminal renders blank** (issue #73: "Restart code agent" stayed blank until VS was reopened; also visible after switching solutions). Three guards: `ResetAgentCompletionWatcher()` stops the watcher + dismisses the info bar on `OnBeforeCloseSolution`, the workspace-changed branch of `OnWorkspaceDirectoryChangedAsync`, **and at the top of every `StartEmbeddedTerminalAsync()`** (covers the restart button, provider/model switches, theme restarts, session resume); `EnsureNoConsoleAttached()` calls `FreeConsole` under a bounded `_consoleSnapshotLock` (`Monitor.TryEnter` w/ timeout), run after `StopExistingTerminalAsync()` and at the start of `ExecuteAgentFinishActionAsync` (no-op when VS has no console); and the conhost `Process.Start` itself runs inside a bounded `_consoleSnapshotLock` acquire + `FreeConsole` (background thread, 5 s timeout), so an in-flight capture can never overlap CreateProcess and a stuck attachment is cleared immediately before the spawn
- **Arming**: `ArmAgentCompletionWatcherAsync()` is fired (fire-and-forget) at the end of `SendButton_Click`. Stores the conhost PID (`cmdProcess.Id`), takes an initial screen snapshot and send time, then starts `_agentCompletionTimer` (1 s `DispatcherTimer`). For Claude Code it also records a token baseline (newest `*.jsonl` via `CountTranscriptTokens`) to enrich the notification — **not** used for detection. Re-arming resets state; `StopAgentCompletionTimer()` also runs from `CleanupResources()`
- **Console-client PID**: the terminal launches as `conhost.exe`, but `AttachConsole` needs a console *client*, so `ResolveConsoleClientPid()` derives the cmd.exe PID from `GetWindowThreadProcessId(terminalHandle)` (returns the client by Windows back-compat) and falls back to the conhost's first ToolHelp32 child. Resolved fresh each sample so a restart can't pin a dead PID
- **Completion detection** (`OnAgentCompletionTimerTick` → `TryCaptureConsoleText`): each tick resolves the client PID, then `SetConsoleCtrlHandler(NULL,TRUE)` (shield VS from a shared-console Ctrl+C) → `AttachConsole` → `CreateFile("CONOUT$")` → `ReadConsoleOutputCharacter` → `FreeConsole` → restore Ctrl+C (all under `_consoleSnapshotLock`). Visible text **plus cursor pos** is FNV-1a hashed (`ComputeStableHash`); a changed hash ⇒ agent working. Fires only when activity was seen this turn **and** the hash held unchanged ≥ `IdleSeconds` (default 3, clamped 2–120). Dead PID or 30-min cap disarms. **Waiting-for-input suppression**: a static y/n or selection prompt reads as idle, so the settled screen is classified by `LooksLikeAgentInputPrompt()` (`AgentPromptKeywords` + `❯`/numbered-menu over bottom ~18 lines); on a match the tick returns without firing/disarming, so it fires once the user answers. Conservative by design. **Typing-interference guard**: the capture briefly attaches VS (disturbs keystrokes), so each tick skips the read while the user is *actively typing* — gated on a recent keystroke (`_lastTerminalKeyUtc`, within `TerminalTypingGuardMs`=1500) **and** `IsTerminalFocused()`. Mere focus isn't enough (pasting leaves the terminal focused but un-typed — a focus-only gate stalled the turn ~15s)
- **Notify**: `OnAgentTurnCompletedAsync` plays a chime (opt-in) and shows a VS **main-window info bar** (`ShowAgentFinishNotificationAsync` via `__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost` + `SVsInfoBarUIFactory`) reading `Agent finished · <duration>` (+`<Δtokens>` only for Claude), shown even when the tool window is hidden. `AgentFinishInfoBarEvents` (`IVsInfoBarUIEvents`) handles close/unadvise and the action hyperlink
- **Actions** (`ExecuteAgentFinishActionAsync`): Build/Rebuild/Run/RunWithoutDebugging/RunTests → `dte.ExecuteCommand(...)`; RunScript → `RunFinishScriptAsync` `Process.Start` (`UseShellExecute`) in the workspace dir — a `.cmd`/`.bat` is wrapped as `cmd.exe /k "<path>"` and a `.ps1` as `powershell.exe -ExecutionPolicy Bypass -NoExit -File "<path>"` (the `.ps1` default shell verb is Edit, so shell-executing it directly would open an editor; `/k`/`-NoExit` keep the console open for output), anything else is shell-executed directly; SendToAgent → `SendTextToTerminalAsync`. `RequireFileChanges` gates on `git status --porcelain` in `_gitRepositoryRoot` (non-git ⇒ never blocks). `Confirm` (default true) surfaces the action as an info-bar button instead of auto-running
- **Console interop** (Interop.cs): reuses `AttachConsole`/`FreeConsole`/`CloseHandle`/`COORD`; adds `CreateFile`, `GetConsoleScreenBufferInfo`, `ReadConsoleOutputCharacterW`, `SetConsoleCtrlHandler`, `Beep`, and `SMALL_RECT`/`CONSOLE_SCREEN_BUFFER_INFO`

## "@" File/Folder Picker (AtMention.cs)

- **Trigger**: `PromptTextBox`'s `TextChanged` (wired in XAML) calls `UpdateAtMentionPopup()`, which detects an `@` token under the caret (`@` at text start or after whitespace, followed by non-whitespace). Always on; no setting
- **Index**: `EnumerateWorkspaceEntries()` walks `GetWorkspaceDirectoryAsync()` on a background thread, skipping build/VCS/package dirs (`AtIgnoredDirs`) and reparse points, returning workspace-relative `/`-separated paths (folders carry a trailing `/`), capped at 8000. Cached per-root with a 30 s TTL (`EnsureAtEntriesAsync`); a stale index refreshes in the background while current results still show. First trigger shows an "Indexing…" row, then `EnsureThenRefilterAsync` re-runs the filter
- **Popup**: a programmatic `Popup` + `ListBox` (`EnsureAtPopup`), positioned at the caret via `GetRectFromCharacterIndex`. Themed with `GetThemeBrushes` + a derived hover brush (`ComputeAtHoverBrush`) so selected/hover rows stay readable in a standalone popup (no system-blue). Closes on real focus loss but not when focus enters the popup (`IsInsideAtPopup`), so a mouse click can commit first
- **Keys**: `HandleAtMentionKey()` runs at the top of `PromptTextBox_PreviewKeyDown` (before history nav / send-on-Enter) and consumes Up/Down/Enter/Tab/Esc while the popup is open
- **Ranking** (`RankAtEntries`): a query may contain `/` for folder drill-down — the part after the last `/` matches the entry name and the prefix constrains the subtree; name prefix-matches rank above name/path substring matches
- **Insert** (`CommitAtSelection`): replaces the typed `@query` with `@<relative-path>`; a file appends a space and closes, a folder leaves the caret in place and re-opens the picker to drill in. Relative paths resolve for every provider (terminal cwd = workspace), so no WSL conversion. `_atSuppressTextChanged` guards the programmatic edit
