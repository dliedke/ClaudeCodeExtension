# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

Visual Studio Extension (VSIX) for VS 2022/2026 — integrates AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Open Code, Devin, PI, Google Antigravity, Reasonix, Qwen Code, and Grok) via a native chat tab driven by each CLI's headless protocol ("native mode", default since v179.0) or an embedded terminal (Win32 `SetParent` interop).

- Author: Daniel Carvalho Liedke (dliedke@gmail.com) | License: MIT
- Repository: https://github.com/dliedke/ClaudeCodeExtension
- Current Version: 278.0 | Target Framework: .NET Framework 4.7.2

Step-by-step procedures for recurring tasks (release, Exp-hive debugging, publish, adding a
provider/setting/UI file) live in `docs/SKILLS.md`. Short build/style brief for non-Claude agents: `docs/AGENTS.md`.

---

## MANDATORY: Version & Documentation Updates

Every development session that modifies code MUST update before finishing:

Versioning scheme (since 11.0): each release bumps the MAJOR version by one — 22.0 → 23.0 → 24.0 and so on. Always use `.0` as the minor (AssemblyVersion `24.0.0.0`, manifest `24.0`, README `### Version 24.0`). Do not resume 10.x-style minor bumps.

1. `Properties/AssemblyInfo.cs`: Bump `AssemblyVersion` and `AssemblyFileVersion`
2. `source.extension.vsixmanifest`: Bump `Version` in `<Identity>` tag
3. `README.md`: Add `### Version X.Y` entry at top of `## Version History`
   - Style: Short, business-focused. One sentence per bullet (two max). Describe the user-visible feature or fix, not the implementation.
   - Avoid: code/file/class/method names, internal selectors, file paths, constants, line numbers, JS snippets, framework jargon (`CoreWebView2`, `INPUT_RECORD`, `NavigationCompleted`, etc.), step-by-step "how it works" explanations, and PR-description-style root-cause analysis.
   - Keep: what the user gets ("auto-confirms proxy block screens"), opt-in/opt-out status, and the menu/setting name they interact with.
   - Technical details belong in commit messages and `docs/ARCHITECTURE.md`, not in release notes.
   - Other README sections (Features, Supported AI Providers, System Requirements, Settings & Menus, etc.): Edits MUST be minimal. Update only the exact line/bullet affected by the change. Do not rewrite paragraphs, expand explanations, add subsections, reorder content, or restructure tables. If a new provider/setting needs a row, add one row. If a feature description needs a word change, change the word. README is reference doc — keep it slim to avoid file bloat.

---

## Build & Test

```bash
# Release
'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Release -v:minimal

# Debug
'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Debug -v:minimal
```

- F5 / Ctrl+F5 in Visual Studio → experimental instance with `/rootsuffix Exp`, for Debug and Release

### Debugging in the Exp hive (`deploy-exp.cmd`)

The VSSDK targets default `DeployExtension` to false, so the csproj sets `VSSDKTargetPlatformRegRootSuffix=Exp` and turns `DeployExtension` on for any configuration built inside Visual Studio. Without those two properties F5 opens an Exp instance with no extension in it. Command-line builds (`test.cmd`, `publish.cmd`) keep the default and deploy nothing.

```bash
./deploy-exp.cmd            # build Debug + deploy to the Exp hive
./deploy-exp.cmd -release   # build Release instead of Debug
./deploy-exp.cmd -run       # the above, then start devenv /rootsuffix Exp with the solution
./deploy-exp.cmd -run -open <path.sln>   # start Exp with another solution (e.g. a demo app, so F5 debugs it)
```

- Close the Exp instance before deploying — otherwise it keeps running the previous build.
- A first deploy into a hive that never had the extension fails with `VSSDK1031 ... could not be found`; the script recovers by running `devenv /rootsuffix Exp /updateconfiguration` and retrying.
- The same VSSDK1031 (in VS or from the script) appears when an idle 32-bit MSBuild node from an earlier VS build still holds the Exp hive's `privateregistry.bin` open — `/updateconfiguration` then silently merges nothing. The script ends those nodes before each attempt; inside VS, kill the `MSBuild.exe ... /nodemode:2` process and rebuild.
- Each deploy lands in a version-named folder, so a version bump leaves the previous one behind and the hive can keep loading the older assembly with no error anywhere. The `RemoveStaleExpDeployments` target in the csproj deletes the sibling version folders after every deploy to prevent that.

### Tests (`test.cmd`)

```bash
./test.cmd    # build Tests/ + run the unit suite (seconds, no VS)
```

- Runs `Tests/ClaudeCodeExtension.Tests.csproj` under `vstest.console.exe`: version/package guards (the Newtonsoft pin that would have caught #112, version consistency across the three sources) plus unit tests of the pure helpers.
- `publish.cmd` calls `test.cmd` before the Release rebuild. `SKIP_TESTS=1` bypasses the gate.
- The test project is in the solution without a `Release|Any CPU.Build.0` entry, so the Release rebuild in `publish.cmd` does not build it.
- Everything that needs a running Visual Studio (terminal embedding, provider round-trip, settings dialog) is tested manually with F5.

### Publishing

When the user asks to publish the app (or any equivalent phrasing like "publish the extension", "publish to marketplace", "ship it"), run `publish.cmd` from the repo root. Do not invoke MSBuild or marketplace APIs manually — `publish.cmd` is the authoritative deployment automation.

`publish.cmd` performs: Clean → Rebuild Release → publish VSIX via `VsixPublisher.exe` with `publishManifest.json`. Falls back from VS 2026 to VS 2022 tool paths automatically. Uses `VsixPub0038` log marker to detect success (works around VsixPublisher telemetry crash in VS 18).

`publishManifest.json`: Marketplace metadata — publisher `dliedke`, category `coding`, free, Q&A enabled, README.md as overview.

---

## Project Structure

```
ClaudeCodeExtension/
├── Controls/                            # Partial classes of ClaudeCodeControl
│   ├── ClaudeCodeControl.cs             # Core initialization & orchestration
│   ├── ClaudeCodeControl.Terminal.cs    # Terminal embedding, process init, F5 forwarding
│   ├── ClaudeCodeControl.ProviderManagement.cs  # AI provider detection & switching, toolbar layout, Caveman skill install
│   ├── ClaudeCodeControl.TerminalIO.cs  # Terminal I/O, command execution
│   ├── ClaudeCodeControl.Diff.cs        # Diff view integration, git polling
│   ├── ClaudeCodeControl.DisplayChange.cs # Repairs terminal geometry/console grid when the session DPI changes (RDP reconnect, monitor switch)
│   ├── ClaudeCodeControl.UserInput.cs   # Keyboard input, button handlers
│   ├── ClaudeCodeControl.Workspace.cs   # Solution/workspace directory detection
│   ├── ClaudeCodeControl.ImageHandling.cs # Image paste & file attachments
│   ├── ClaudeCodeControl.Settings.cs    # Settings persistence (JSON), layout inversion
│   ├── ClaudeCodeControl.SettingsDialog.cs # Consolidated Settings dialog: behavior, layout, terminal type, theme
│   ├── ClaudeCodeControl.SettingsBackup.cs # Settings → Backup tab: save/load the whole configuration to/from a JSON file
│   ├── ClaudeCodeControl.Cleanup.cs     # Resource cleanup, temp dir management
│   ├── ClaudeCodeControl.AgentCompletion.cs # "On Agent Finish": console-idle completion watcher, notify (info bar) + actions
│   ├── ClaudeCodeControl.AgentFinishDialog.cs # "On Agent Finish" Settings tab (first tab): per-solution config (no global default)
│   ├── ClaudeCodeControl.BuildErrors.cs # "Auto-send build errors": VS build-event hook, Error List collection, format + send to agent
│   ├── ClaudeCodeControl.RuntimeErrors.cs # "Auto-send runtime errors": VS debugger-event hook, unhandled-exception collection, format + send to agent
│   ├── ClaudeCodeControl.PendingReview.cs # "Track agent changes for review": per-turn snapshots, pending list in the Changes view, per-file/bulk Keep and Undo (#183)
│   ├── ClaudeCodeControl.GitSync.cs     # "Pull before sending": pre-prompt git pull; conflicts are prepended to the prompt for the agent to resolve
│   ├── ClaudeCodeControl.AtMention.cs   # "@" file/folder picker in the prompt box (workspace index + popup)
│   ├── ClaudeCodeControl.SlashCommands.cs # "/" command picker (#187): built-in/custom commands + skills with descriptions, reuses the "@" popup
│   ├── ClaudeCodeControl.TfvcCheckout.cs # "Check out TFVC files": Claude native mode pre-edit hook → VS QueryEdit checkout in TFVC-bound solutions
│   ├── ClaudeCodeControl.CustomCommands.cs # User-defined custom commands: configure dialog, toolbar dropdown, dispatch
│   ├── ClaudeCodeControl.DebugVisibility.cs # Keeps the extension (and any tab it created) visible while debugging (issues #130, #141)
│   ├── ClaudeCodeControl.CliPaths.cs    # Per-provider custom CLI executable path: Settings "CLI Paths" tab content, resolution/validation helpers
│   ├── ClaudeCodeControl.CommitMessage.cs # "Generate Commit Message": AI-drafted commit message from the git diff, filled into the Git Changes window via best-effort UI Automation
│   ├── ClaudeCodeControl.ModelCatalog.cs # Per-provider model list: CLI discovery + cache, selection storage, launch flags / live switch commands
│   ├── ClaudeCodeControl.ModelPickerDialog.cs # Searchable model picker window (Devin)
│   ├── ClaudeCodeControl.ModelRecommendation.cs # "Recommend AI Model" (Claude Code only): one-shot Opus/xhigh advisor call, confirm dialog, applies model + effort
│   ├── ClaudeCodeControl.SubagentCount.cs # "Show active subagent count": polls Claude Code session transcripts, live running-subagent badge in the panel toolbar or each native chat composer
│   ├── ClaudeCodeControl.OpenPets.cs    # "Install latest OpenPets" (Automation tab): GitHub release download with progress, SHA-256 check, starts the installer
│   ├── ClaudeCodeControl.GitHubIssues.cs # "GitHub Issues": issue list window, fix in a fix/issue-N branch + worktree + chat tab, commit/push, merge into base, reply + close
│   ├── ClaudeCodeControl.Interop.cs     # Win32 API declarations (P/Invoke)
│   ├── ClaudeCodeControl.Theme.cs       # Dark/light theme support
│   ├── ClaudeCodeControl.Detach.cs      # Terminal detach/attach to separate VS tab
│   ├── ClaudeCodeControl.Usage.cs       # Claude usage tool window wiring & inline bars
│   ├── ClaudeCodeControl.SessionHistory.cs # Claude/Codex/Devin history dialog: list/view/resume/delete
│   ├── ClaudeCodeControl.SessionHistoryProviders.cs # Per-CLI history adapters (Claude, Codex, Devin) behind ISessionHistoryProvider
│   ├── ClaudeCodeControl.NativeMode.cs  # "Native mode": chat instead of the embedded terminal, adapter selection, event bridge
│   ├── ClaudeCodeControl.NativeChat.cs  # Native mode chat tab: document-tab hosting, composer, agent/model/effort/permission selectors, live switching
│   ├── ClaudeCodeControl.ChatUsageIndicator.cs # Native mode composer usage button: context ring + plan-limit popup, background usage refresh
│   ├── ClaudeCodeControl.ChatTabStatus.cs  # Native chat tab status glyph (working/waiting/done/error) + automatic tab title (first prompt or low-cost AI)
│   └── ClaudeCodeControl.ChatTabRestore.cs # "Reopen chat tabs when the solution opens": per-workspace list of open chat tabs, resumed on the next opening (Claude tabs launch on first prompt)
├── Agents/                              # Headless agent protocols (no WPF, no VS SDK — unit-testable)
│   ├── IAgentSession.cs                 # Session contract shared by every adapter
│   ├── ModelCatalog.cs                  # Model option DTO + pure parsers for each CLI's model-listing output
│   ├── SlashCommandCatalog.cs           # "/" picker: command/skill discovery, front-matter parsing, ranking (pure)
│   ├── ModelRecommender.cs              # "Recommend AI Model": rubric, JSON schema, stdin request, result parser
│   ├── SessionTitleGenerator.cs         # Automatic chat tab title: low-cost model request + answer parser (pure)
│   ├── SubagentActivity.cs              # "Show active subagent count": transcript lines → running subagents (pure)
│   ├── OpenPetsRelease.cs               # "Install latest OpenPets": release JSON → Windows installer asset, SHA256SUMS lookup (pure)
│   ├── GitHubIssues.cs                  # "GitHub Issues": remote parsing, gh/REST issue JSON, worktree list, branch naming, prompts (pure)
│   ├── ClaudeStatusLine.cs              # Native mode: resolves + runs the user's Claude Code statusLine command, builds its stdin JSON
│   ├── AgentEvent.cs                    # Provider-agnostic event/usage/permission model
│   ├── JsonLineProcessHost.cs           # Shared process plumbing (stdio, line reader, tree teardown)
│   ├── ProcessTree.cs                   # Process-tree enumeration/termination
│   ├── ClaudeStreamJsonSession.cs / ClaudeStreamParser.cs / ClaudeCommandBuilder.cs / ClaudeEditHook.cs # Claude Code stream-json (+ pre-edit hook)
│   ├── AcpSession.cs / AcpCommandBuilder.cs # ACP (OpenCode, Devin, Devin native, Qwen Code, Grok, Reasonix)
│   ├── OneShotResumeSession.cs / CodexExecProtocol.cs / CursorAgentProtocol.cs # CLIs that exit each turn
│   ├── CodexAppServerClient.cs        # Codex thread list/read/delete client for session history
│   ├── DevinSessionHistoryClient.cs     # Devin history via `devin list` + ACP session/load
│   ├── DevinUsageClient.cs              # Devin account usage (ACUs, quotas) for native mode
│   ├── ClaudeSessionColor.cs            # Parses Claude Code's /color answer
│   ├── AgentCliNotFoundException.cs / AgentModelUnavailableException.cs # Native session start failures
│   ├── PiRpcSession.cs                  # PI's own RPC mode
│   └── PrintModeSession.cs              # Antigravity (--print, no event stream)
├── UI/                                  # XAML controls + paired code-behind
│   ├── ClaudeCodeControl.xaml
│   ├── ClaudeUsageControl.xaml(.cs)
│   ├── ChatTranscriptView.xaml(.cs)     # Native mode chat transcript
│   ├── ChatContainerView.xaml(.cs)      # Tab strip + content of the single chat window
│   ├── ThemedDialog.cs / ThemedDialogStyles.xaml # VS-themed title bar, scrollbars, rounded buttons/search box for the extension's dialogs
│   ├── ChatMessages.cs                  # Chat message view-models
│   ├── ChatInteractionViewModels.cs     # Question/permission/plan card view-models (transcript rows)
│   ├── ChatToolPresentation.cs          # Tool call → header line + rendered diff (pure, unit-tested)
│   ├── ChatStyles.xaml                  # Chat resource dictionary (composer menu styles, brushes)
│   ├── ChatUsageIndicator.cs            # Native composer usage button: token formatting, context/plan percentages, ring geometry (pure, unit-tested)
│   ├── GlyphIcon.cs                     # Ink-centering of icon-only buttons (attached property, pure math unit-tested)
│   ├── ComposerLabels.cs                # Short composer selector captions for narrow chat tabs (pure)
│   ├── ChatTabStatus.cs                 # Chat tab status glyph + title from the first prompt (pure, unit-tested)
│   ├── PromptAttachments.cs             # Splits the "Files attached:" block out of a sent prompt for the chat bubble (pure, unit-tested)
│   ├── ImagePreview.cs                  # Attachment thumbnails + hover preview, shared by composer chips/cards and the chat bubble strip
│   ├── Loc.cs / LocExtension.cs         # UI localization (whole extension): L("English")/N("English") + {local:Loc '...'}, lang/<culture>.json (pure, unit-tested)
│   ├── SessionListPresentation.cs       # Session History / welcome card recent list: cleaned first-prompt preview, date groups (pure, unit-tested)
│   ├── WelcomeCardLayout.cs             # Welcome card layout rule: tips stack once taller than 120 % of the left column width, account email line break (pure, unit-tested)
│   ├── HostedWindowBounds.cs            # Detects a native child window (Usage WebView2) drifted from its WPF slot (pure)
│   ├── MarkdownFlowRenderer.cs          # Markdown → FlowDocument + the MarkdownBlock control
│   └── DiffViewerControl.xaml(.cs)
├── ToolWindows/                         # VS tool window hosts
│   ├── ClaudeCodeToolWindow.cs
│   ├── DiffViewerToolWindow.cs
│   ├── DetachedTerminalToolWindow.cs
│   ├── ClaudeUsageToolWindow.cs
│   ├── NativeChatToolWindow.cs          # Document-area tab hosting one native-mode chat
│   ├── ChatContainerToolWindow.cs       # "Show all chats in one window": single remembered window, one inner tab per chat
│   └── IChatTabHost.cs                  # Where one chat is drawn (own VS tab or inner tab), shared by both hosts
├── Models/
│   ├── ClaudeCodeModels.cs              # Enums & settings class
│   └── NativeChatSessionState.cs        # Per-session native mode chat state (parallel sessions)
├── Package/                             # VS package & solution event wiring
│   ├── ClaudeCodeExtensionPackage.cs    # VS package registration
│   └── SolutionEventsHandler.cs         # Solution/project open events
├── Diff/                                # Diff engine
│   ├── DiffComputer.cs
│   ├── FileChangeTracker.cs
│   ├── PendingReviewTracker.cs          # Pending-review baselines per turn (pure, unit-tested behind IPendingReviewSource)
│   ├── PendingReviewMessages.cs         # Wording of the Keep/Undo/turn-off confirmations
│   ├── GitPendingReviewSource.cs        # Git implementation of IPendingReviewSource
│   └── ChangedFile.cs
├── Tests/                               # Unit suite (MSTest, no VS required — run via test.cmd)
│   ├── ClaudeCodeExtension.Tests.csproj # Not built by the Release rebuild in publish.cmd
│   ├── VersionConsistencyTests.cs / PackageVersionGuardTests.cs / TypeLoadProbeTests.cs # Version + package guards
│   └── AgentStreamParsingTests.cs, ChatToolPresentationTests.cs, SessionTranscriptFilterTests.cs, … # Pure-helper tests
├── docs/
│   ├── ARCHITECTURE.md                  # Per-file non-obvious details (on-demand reference; indexed from CLAUDE.md)
│   ├── SKILLS.md                        # Step-by-step task recipes (release, Exp debugging, publish, add provider/setting/UI file)
│   ├── AGENTS.md                        # Short instructions file for non-Claude agents
│   └── images/                          # README screenshots (linked by raw GitHub URL so the Marketplace page resolves them)
├── lang/                                # Chat UI translations (19 + English), embedded: <culture>.json = { "English text": "translation" }
├── tools/repo-watcher/                  # Scheduled PR/issue watcher (gh check, then unattended Claude Code); see docs/SKILLS.md
├── Properties/AssemblyInfo.cs           # AssemblyVersion/FileVersion (bumped every release) + InternalsVisibleTo for Tests
├── Root (project metadata only):
│   ├── ClaudeCodeExtensionPackage.vsct  # Command table
│   ├── source.extension.vsixmanifest
│   └── ClaudeCodeExtension.csproj / .sln
└── Scripts:
    ├── publish.cmd                      # Automated marketplace deployment script (runs test.cmd first; gitignored, local only)
    ├── test.cmd                         # Build + run the unit suite
    ├── deploy-exp.cmd                   # Build + deploy into the VS experimental (Exp) hive
    └── publishManifest.json             # VS Marketplace metadata
```

Folder reorg note: When adding a new XAML control, place both `.xaml` and `.xaml.cs` in `UI/` and add a `<Page Include="UI\Foo.xaml">` entry plus a `<Compile Include="UI\Foo.xaml.cs">` with `<DependentUpon>Foo.xaml</DependentUpon>` to the csproj. Partial-class extensions of `ClaudeCodeControl` live in `Controls/`.

---

## Code Style & Conventions

- Language: C# / .NET Framework 4.7.2
- File Headers: Every `.cs` file must include copyright header (Daniel Liedke, 2026)
- Namespaces: `ClaudeCodeVS` (controls/models), `ClaudeCodeExtension` (package)
- Naming: PascalCase public, `_camelCase` private fields, camelCase locals
- Error Handling: try-catch + `Debug.WriteLine`; `MessageBox` for user-facing errors
- Thread Safety: `ThreadHelper.ThrowIfNotOnUIThread()` / `SwitchToMainThreadAsync()`
- Settings: JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`

```csharp
/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: <description>
 * ***********************/
```

---

## Architecture — Key Non-Obvious Details

Deep per-file gotchas and design decisions live in `docs/ARCHITECTURE.md` (kept out of this
always-loaded file to keep context lean). Before editing any file below, read its section in that
doc — it captures non-obvious behavior that isn't apparent from the code:

Four cross-cutting rules (full text in `docs/ARCHITECTURE.md` → *Cross-Cutting Rules*) — read before touching provider selection, UI captions, terminal focus, or anything that sends text to the agent:
- Active provider UI rule (v24.0): provider-dependent UI (checkmarks, captions, menu visibility, active labels) must use `_currentRunningProvider` when a terminal is alive, falling back to `_settings.SelectedProvider` only before launch.
- Terminal focus rule (v26.0): never focus the embedded terminal with direct `SetForegroundWindow`/`SetFocus`; use `FocusTerminalForInputAsync()`/`FocusTerminalForInput()`/`FocusTerminalWindow()`. Hook focus checks stay Win32-only.
- Agent send rule (v82.0): send agent-bound text through `SendTextToAgentAsync()`, not `SendTextToTerminalAsync()` — native mode has no console. Console-only traffic (slash commands, CLI updates, plugin installs) is the exception; new console-scraping logic must bail out when `IsNativeModeActive`.
- Chat localization rule (v269.0, whole extension since v270.0): new or changed UI text — chat, panel, menus, dialogs, message boxes — is wrapped in `L("...")`/`N("...")`/`{local:Loc '...'}` and translated in every `lang/*.json` in the same change (recipe: `docs/SKILLS.md` → *Localize chat text*); `test.cmd` fails on a missing or orphaned translation. Text set from code must also be redone by `RefreshChatLanguage()` (or a tool window's own `Loc.LanguageChanged` handler) so a language switch reaches everything already open.

| File | `docs/ARCHITECTURE.md` section |
|------|-------------------------------|
| `Controls/ClaudeCodeControl.Terminal.cs` | Terminal Embedding — SetParent embed, conhost/WT modes, F5/mouse hooks, focus reclaim (#65), click-to-foreground (#69), WSL command patterns |
| `Controls/ClaudeCodeControl.ProviderManagement.cs` | Provider Detection · Caveman Skill · Visible Agents · Configurable Toolbar Buttons — caching, per-provider detect/paste quirks, toolbar vs ☰ Tools placement |
| `Controls/ClaudeCodeControl.CustomCommands.cs` | Custom Commands |
| `Controls/ClaudeCodeControl.DebugVisibility.cs` | Debug Visibility — always-on visibility restore for the panel and its tabs while debugging |
| `Controls/ClaudeCodeControl.DisplayChange.cs` | Display / DPI Change Repair — reported-vs-painted cell size, width guard, console grid fit, pass schedule |
| `Controls/ClaudeCodeControl.CliPaths.cs` | Custom CLI Paths — CLI Paths settings tab, resolution/validation |
| `Controls/ClaudeCodeControl.CommitMessage.cs` | Generate Commit Message — assistant-turn capture tap, Git Changes window UI Automation, clipboard fallback |
| `Controls/ClaudeCodeControl.ModelCatalog.cs`, `Agents/ModelCatalog.cs` | Model Catalog & Selection — per-CLI listing commands, cache/TTL, how each agent's model is applied |
| `Controls/ClaudeCodeControl.ModelRecommendation.cs`, `Agents/ModelRecommender.cs` | Recommend AI Model — separate one-shot advisor CLI, lean flags, rubric command-line safety, prompt source/apply target |
| `Controls/ClaudeCodeControl.SubagentCount.cs`, `Agents/SubagentActivity.cs` | Subagent Count — transcript start/finish rules, which transcripts are polled, stale/epoch filters |
| `Controls/ClaudeCodeControl.OpenPets.cs`, `Agents/OpenPetsRelease.cs` | Install Latest OpenPets — release asset choice, checksum verification, download progress, installer start |
| `Controls/ClaudeCodeControl.GitHubIssues.cs`, `Agents/GitHubIssues.cs` | GitHub Issues — gh vs REST, worktree layout, `WorkspaceOverride` sessions, detached merge, conflict hand-off, cleanup |
| `Controls/ClaudeCodeControl.TerminalIO.cs` | Terminal I/O — paste/clipboard, chunking, large-prompt-as-file |
| `Controls/ClaudeCodeControl.Usage.cs`, `Controls/ClaudeCodeControl.ChatUsageIndicator.cs`, `UI/ChatUsageIndicator.cs`, `Agents/DevinUsageClient.cs` | Claude Usage · Devin Usage — WebView2 scraping, persistence, proxy interstitial, native composer usage button, Devin ACU usage |
| `Controls/ClaudeCodeControl.Settings.cs` | Settings — init guard, layout inversion, prompt resize grip |
| `Controls/ClaudeCodeControl.Workspace.cs` | Workspace — directory resolution priority |
| `Controls/ClaudeCodeControl.Detach.cs` | Detach — re-parenting / auto-reattach |
| `Controls/ClaudeCodeControl.Theme.cs` | Theme — agent vs panel color, restart prompt, custom color |
| `Controls/ClaudeCodeControl.SettingsDialog.cs`, `Controls/ClaudeCodeControl.SettingsBackup.cs` | Consolidated Settings Dialog — tabs, batched apply, themed templates, Backup tab save/load |
| `Controls/ClaudeCodeControl.SessionHistory.cs`, `Controls/ClaudeCodeControl.SessionHistoryProviders.cs`, `Agents/DevinSessionHistoryClient.cs` | Session History — JSONL parsing, path encoding, resume flow |
| `Controls/ClaudeCodeControl.AgentCompletion.cs` | On Agent Finish — console-buffer idle detection, console-attach leak guard |
| `Controls/ClaudeCodeControl.AgentFinishDialog.cs` | On Agent Finish — first Settings tab, per-solution config only, follow-up presets |
| `Controls/ClaudeCodeControl.BuildErrors.cs` | Auto-Send Build Errors — build-event hook, Error List collection, dedupe/loop guard |
| `Controls/ClaudeCodeControl.RuntimeErrors.cs` | Auto-Send Runtime Errors — debugger break-mode hook, unhandled-exception collection, dedupe guard |
| `Controls/ClaudeCodeControl.GitSync.cs` | Pull Before Sending — pre-prompt `git pull`, skip conditions, conflict-as-prompt handoff, `--autostash` caveats |
| `Controls/ClaudeCodeControl.AtMention.cs`, `Controls/ClaudeCodeControl.SlashCommands.cs` | "@" File/Folder Picker · "/" Command Picker — index, popup, ranking, insert |
| `Controls/ClaudeCodeControl.PendingReview.cs`, `Diff/PendingReviewTracker.cs` | Pending Review — turn keys, reference snapshot, accumulation, overlapping turns, Keep/Undo, Changes view scope |
| `Controls/ClaudeCodeControl.TfvcCheckout.cs`, `Agents/ClaudeEditHook.cs` | TFVC Checkout — PreToolUse hook over the control channel, QueryEdit checkout, deny-with-reason |
| `Controls/ClaudeCodeControl.NativeMode.cs`, `Agents/*`, `UI/ChatTranscriptView.xaml` | Native Mode — Agent Sessions: `IAgentSession` contract, the six adapters, event map, streaming-duplication traps, `SendTextToAgentAsync` bifurcation |
| `UI/Loc.cs`, `UI/LocExtension.cs`, `lang/*.json` | Chat UI Localization — English-as-key, lang file loading/override, XAML timing, what is never translated |
| `Controls/ClaudeCodeControl.NativeChat.cs`, `Controls/ClaudeCodeControl.ChatTabStatus.cs`, `Controls/ClaudeCodeControl.ChatTabRestore.cs`, `ToolWindows/NativeChatToolWindow.cs`, `ToolWindows/ChatContainerToolWindow.cs` | Native Mode — Chat tab and composer (incl. tab status glyph + auto title, reopening tabs with the solution): MDI document-tab hosting, transcript re-parenting, composer reuse of the panel send path, live model/effort/permission switching via resume |

When you add or materially change behavior in one of these files, update its section in
`docs/ARCHITECTURE.md` (not this table) — the same way you'd update the Architecture section before.

---

## Data Models (ClaudeCodeModels.cs)

Enums (`AiProvider`, `ClaudeModel`, `EffortLevel`, `CodexReasoningLevel`, `ThemePreference`, `TerminalType`, `LayoutOrientation`, `AgentFinishActionType`, `ToolbarButton`), settings/DTO classes, and the full annotated `Settings` field reference live in `docs/ARCHITECTURE.md` → *Data Models & Settings*. Update that section when adding or changing a model, enum value, or setting.

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
| Qwen Code | `QwenCode` | Windows | `qwen` | `/quit` |
| Grok | `Grok` | Windows | `grok` | `/quit` |

Plugin: Caveman (JuliusBrussee/caveman) — Claude Code skill, installed for Windows or WSL from *⚙ → Settings... → Automation* (Install Caveman)

---

## Reference (in docs/ARCHITECTURE.md)

To keep this always-loaded file lean, the following reference material lives in `docs/ARCHITECTURE.md`:

- Key GUIDs — package, VSIX identity, command set, tool window IDs
- Key Dependencies — SDK / build-tools / Newtonsoft.Json / DiffPlex versions
- Adding a New AI Provider (Checklist) — the per-file steps to wire up a new provider
