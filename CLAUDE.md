# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

**Visual Studio Extension (VSIX)** for VS 2022/2026 — integrates AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Open Code, Windsurf, PI, and Google Antigravity) via embedded terminal (Win32 `SetParent` interop).

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com) | **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 24.0 | **Target Framework**: .NET Framework 4.7.2

---

## MANDATORY: Version & Documentation Updates

**Every development session that modifies code MUST update before finishing:**

**Versioning scheme (since 11.0)**: each release bumps the MAJOR version by one — 22.0 → 23.0 → 24.0 and so on. Always use `.0` as the minor (AssemblyVersion `24.0.0.0`, manifest `24.0`, README `### Version 24.0`). Do not resume 10.x-style minor bumps.

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
│   ├── ClaudeCodeControl.CliPaths.cs    # Per-provider custom CLI executable path: Settings "CLI Paths" tab content, resolution/validation helpers
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
├── docs/
│   └── ARCHITECTURE.md                  # Per-file non-obvious details (on-demand reference; indexed from CLAUDE.md)
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

Deep per-file gotchas and design decisions live in **`docs/ARCHITECTURE.md`** (kept out of this
always-loaded file to keep context lean). **Before editing any file below, read its section in that
doc** — it captures non-obvious behavior that isn't apparent from the code:

**Active provider UI rule (v24.0)**: provider checkmarks, tool-window captions, model/usage menu visibility, detached-tab caption, and visible-agent "active" labels must use `_currentRunningProvider` when a terminal is alive, falling back to `_settings.SelectedProvider` only before launch. Successful provider launches sync `_settings.SelectedProvider` in memory, but `SaveSettings()` still preserves provider/model/effort fields from disk during normal operation so multiple VS instances do not overwrite each other's selections.

| File | `docs/ARCHITECTURE.md` section |
|------|-------------------------------|
| `Controls/ClaudeCodeControl.Terminal.cs` | Terminal Embedding — SetParent embed, conhost/WT modes, F5/mouse hooks, focus reclaim (#65), click-to-foreground (#69), WSL command patterns |
| `Controls/ClaudeCodeControl.ProviderManagement.cs` | Provider Detection · Caveman Plugin · Visible Agents — caching, per-provider detect/paste quirks |
| `Controls/ClaudeCodeControl.CustomCommands.cs` | Custom Commands |
| `Controls/ClaudeCodeControl.CliPaths.cs` | Custom CLI Paths — CLI Paths settings tab, resolution/validation |
| `Controls/ClaudeCodeControl.TerminalIO.cs` | Terminal I/O — paste/clipboard, chunking, large-prompt-as-file |
| `Controls/ClaudeCodeControl.Usage.cs` | Claude Usage — WebView2 scraping, persistence, proxy interstitial |
| `Controls/ClaudeCodeControl.Settings.cs` | Settings — init guard, layout inversion, prompt resize grip |
| `Controls/ClaudeCodeControl.Workspace.cs` | Workspace — directory resolution priority |
| `Controls/ClaudeCodeControl.Detach.cs` | Detach — re-parenting / auto-reattach |
| `Controls/ClaudeCodeControl.Theme.cs` | Theme — agent vs panel color, restart prompt, custom color |
| `Controls/ClaudeCodeControl.SettingsDialog.cs` | Consolidated Settings Dialog — six tabs, batched apply, themed templates |
| `Controls/ClaudeCodeControl.SessionHistory.cs` | Session History — JSONL parsing, path encoding, resume flow |
| `Controls/ClaudeCodeControl.AgentCompletion.cs` | On Agent Finish — console-buffer idle detection, console-attach leak guard |
| `Controls/ClaudeCodeControl.AtMention.cs` | "@" File/Folder Picker — index, popup, ranking, insert |

When you add or materially change behavior in one of these files, update its section in
`docs/ARCHITECTURE.md` (not this table) — the same way you'd update the Architecture section before.

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

Key settings: `SplitterPosition` (236px default), `SendWithEnter` (default true), `SendWithCtrlEnter` (default false — when true and `SendWithEnter` false, Ctrl+Enter sends and Enter inserts a newline; issue #70), `SelectedProvider`, `VisibleProviders` (defaults to `[ClaudeCode]` — controls which agents appear in the provider menu; active provider is always shown regardless), `SelectedClaudeModel`, `SelectedWindsurfModel`, `PromptHistory` (max 50), `AutoOpenChangesOnPrompt`, `ClaudeDangerouslySkipPermissions`, `CodexFullAuto`, `CursorAgentAutoRun`, `WindsurfDangerousMode`, `SelectedEffortLevel`, `CustomWorkingDirectory`, `SelectedTerminalType`, `IsTerminalDetached`, `PromptFontSize` (8–24pt), `TerminalZoomDelta`, `InvertLayout`, `SelectedThemePreference` (Automatic/Dark/Light/Custom), `CustomThemeColorArgb` (bg color for Custom, default #F4ECFF), `LastAgentTerminalColorArgb` (agent's launched color, skips redundant restart prompts), `SkipThemeRestartPrompt` (default false), `CustomCommands` (list of `{Name, Command}`), `UsageAutoRefreshSeconds` (0 = manual), `UsageWindowOpened` (auto-reopen on load), `ShowInlineUsageBars` (default true), `LastUsageJson` / `LastUsageTimestamp` (cached snapshot), `SendLargePromptsAsFile` (default false — when true, prompts >1 KB are sent as a file reference instead of inline paste), `AgentFinish` (`AgentFinishConfig` — global "On Agent Finish" default; default disabled), `ProjectAgentFinish` (`Dictionary<string,AgentFinishConfig>` — per-solution overrides keyed by `.sln` name, take precedence over `AgentFinish` when present), `CustomExecutablePaths` (`Dictionary<AiProvider,string>` — per-provider custom CLI executable path override; empty/missing entries fall back to PATH/native detection)

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
