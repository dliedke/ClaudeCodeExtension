# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

**Visual Studio Extension (VSIX)** for VS 2022/2026 — integrates AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Open Code, Devin, PI, Google Antigravity, and Reasonix) via embedded terminal (Win32 `SetParent` interop).

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com) | **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 67.0 | **Target Framework**: .NET Framework 4.7.2

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
   - Technical details belong in commit messages and `docs/ARCHITECTURE.md`, not in release notes.
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

### Debugging in the Exp hive (`deploy-exp.cmd`)

The VSSDK targets default `DeployExtension` to **false**, so the csproj sets `VSSDKTargetPlatformRegRootSuffix=Exp` and turns `DeployExtension` on for Debug builds inside Visual Studio. Without those two properties F5 opens an Exp instance with no extension in it. Command-line builds (`test.cmd`, `publish.cmd`) keep the default and deploy nothing.

```bash
./deploy-exp.cmd        # build Debug + deploy to the Exp hive
./deploy-exp.cmd -run   # the above, then start devenv /rootsuffix Exp with the solution
```

- Close the Exp instance before deploying — otherwise it keeps running the previous build.
- A first deploy into a hive that never had the extension fails with `VSSDK1031 ... could not be found`; the script recovers by running `devenv /rootsuffix Exp /updateconfiguration` and retrying.

### Tests (`test.cmd`)

```bash
./test.cmd    # build Tests/ + run the unit suite (seconds, no VS)
```

- Runs `Tests/ClaudeCodeExtension.Tests.csproj` under `vstest.console.exe`: version/package guards (the Newtonsoft pin that would have caught #112, version consistency across the three sources) plus unit tests of the pure helpers.
- **`publish.cmd` calls `test.cmd`** before the Release rebuild. `SKIP_TESTS=1` bypasses the gate.
- The test project is in the solution **without** a `Release|Any CPU.Build.0` entry, so the Release rebuild in `publish.cmd` does not build it.
- Everything that needs a running Visual Studio (terminal embedding, provider round-trip, settings dialog) is tested manually with F5.

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
│   ├── ClaudeCodeControl.AgentFinishDialog.cs # "On Agent Finish" settings window: global default + per-solution override
│   ├── ClaudeCodeControl.BuildErrors.cs # "Auto-send build errors": VS build-event hook, Error List collection, format + send to agent
│   ├── ClaudeCodeControl.RuntimeErrors.cs # "Auto-send runtime errors": VS debugger-event hook, unhandled-exception collection, format + send to agent
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

**Two cross-cutting rules** (full text in `docs/ARCHITECTURE.md` → *Cross-Cutting Rules*) — read before touching provider selection, UI captions, or terminal focus:
- **Active provider UI rule (v24.0)**: provider-dependent UI (checkmarks, captions, menu visibility, active labels) must use `_currentRunningProvider` when a terminal is alive, falling back to `_settings.SelectedProvider` only before launch.
- **Terminal focus rule (v26.0)**: never focus the embedded terminal with direct `SetForegroundWindow`/`SetFocus`; use `FocusTerminalForInputAsync()`/`FocusTerminalForInput()`/`FocusTerminalWindow()`. Hook focus checks stay Win32-only.

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
| `Controls/ClaudeCodeControl.AgentFinishDialog.cs` | On Agent Finish — settings window, global default + per-solution override, follow-up presets |
| `Controls/ClaudeCodeControl.BuildErrors.cs` | Auto-Send Build Errors — build-event hook, Error List collection, dedupe/loop guard |
| `Controls/ClaudeCodeControl.RuntimeErrors.cs` | Auto-Send Runtime Errors — debugger break-mode hook, unhandled-exception collection, dedupe guard |
| `Controls/ClaudeCodeControl.AtMention.cs` | "@" File/Folder Picker — index, popup, ranking, insert |

When you add or materially change behavior in one of these files, update its section in
`docs/ARCHITECTURE.md` (not this table) — the same way you'd update the Architecture section before.

---

## Data Models (ClaudeCodeModels.cs)

Enums (`AiProvider`, `ClaudeModel`, `EffortLevel`, `TerminalType`, `AgentFinishActionType`), settings/DTO classes, and the full annotated `Settings` field reference live in **`docs/ARCHITECTURE.md`** → *Data Models & Settings*. Update that section when adding or changing a model, enum value, or setting.

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
| Devin (WSL) | `Devin` | WSL | `devin` | `exit` |
| Devin (native) | `DevinNative` | Windows | `devin` | `exit` |
| PI | `Pi` | Windows | `pi` | CTRL+D twice |
| Antigravity | `Antigravity` | Windows | `agy` | Double CTRL+D |
| Reasonix | `Reasonix` | Windows | `reasonix` | CTRL+C |

**Plugin**: Caveman (JuliusBrussee/caveman) — installable into Claude Code sessions via model menu

---

## Reference (in docs/ARCHITECTURE.md)

To keep this always-loaded file lean, the following reference material lives in **`docs/ARCHITECTURE.md`**:

- **Key GUIDs** — package, VSIX identity, command set, tool window IDs
- **Key Dependencies** — SDK / build-tools / Newtonsoft.Json / DiffPlex versions
- **Adding a New AI Provider (Checklist)** — the per-file steps to wire up a new provider
