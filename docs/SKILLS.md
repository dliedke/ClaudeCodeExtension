# SKILLS.md — Task Recipes

Ordered procedures for the recurring tasks in this repo. Each entry is *how to do it*, the
gotcha that bites, and how to verify you got it right.

Where things live:

| Doc | Answers |
|-----|---------|
| `CLAUDE.md` | Always-loaded rules, project map, conventions |
| `ARCHITECTURE.md` | Per-file non-obvious behavior — *why the code is like that* |
| `SKILLS.md` (this file) | Repeatable procedures — *how to perform a task* |
| `AGENTS.md` | Condensed build/style brief for non-Claude agents |

---

## Index

| I need to… | Skill |
|------------|-------|
| Finish a code-changing session | [Ship a release](#ship-a-release) |
| Build and run the fast checks | [Build & test](#build--test) |
| See a change running in real VS | [Debug in the Exp hive](#debug-in-the-exp-hive) |
| Push to the marketplace | [Publish](#publish) |
| Wire up another AI CLI | [Add a new AI provider](#add-a-new-ai-provider) |
| Add a persisted option | [Add a new setting](#add-a-new-setting) |
| Add a XAML control or partial class | [Add a new UI file](#add-a-new-ui-file) |
| Send text to the agent / touch provider UI / focus the terminal | [Pre-flight gates](#pre-flight-gates) |
| Know which doc to update | [Keep docs in sync](#keep-docs-in-sync) |

---

## Ship a release

**Mandatory for every session that modifies code.** Bump the MAJOR by one, minor always `.0`
(176.0 → 177.0). Do not resume 10.x-style minor bumps.

1. `Properties/AssemblyInfo.cs` — `AssemblyVersion` **and** `AssemblyFileVersion` to `177.0.0.0`
   (both, four parts).
2. `source.extension.vsixmanifest` — `Version="177.0"` in the `<Identity>` tag (two parts).
3. `README.md` — add `### Version 177.0` at the top of `## Version History`.

**Verify:** `./test.cmd`. `VersionConsistencyTests` makes this rule executable — it asserts
`AssemblyVersion == AssemblyFileVersion`, that the short form matches the manifest and the
newest README heading, and that the minor is `.0`. A missed file fails the suite, and
`publish.cmd` runs it as a gate.

**Release-note style** (README only — the one section where prose discipline matters):

- Short and business-focused. One sentence per bullet, two max.
- Describe the user-visible feature or fix, not the implementation.
- Avoid: code/file/class/method names, internal selectors, paths, constants, line numbers,
  JS snippets, framework jargon (`CoreWebView2`, `INPUT_RECORD`, `NavigationCompleted`),
  step-by-step "how it works", PR-style root-cause analysis.
- Keep: what the user gets ("auto-confirms proxy block screens"), opt-in/opt-out status, and
  the menu or setting name they interact with.
- Technical detail belongs in the commit message and `ARCHITECTURE.md`.

**Other README sections** (Features, System Requirements, Provider Menu, Updating…): edit the
exact line affected and nothing else. New provider → one row. Reworded feature → one word. Do
not rewrite paragraphs, add subsections, reorder, or restructure tables. README is reference
doc; keep it slim.

---

## Build & test

```bash
# Release
'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Release -v:minimal

# Debug
'/c/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/MSBuild.exe' ClaudeCodeExtension.sln -p:Configuration=Debug -v:minimal

./test.cmd    # build Tests/ + run the unit suite (seconds, no VS)
```

- From WSL bash, shell out via `powershell.exe -NoProfile -Command "& '<exe>' ..."` rather than
  `cmd.exe`.
- Scripts probe VS 2026 (`...\18\Enterprise`) first, then fall back to VS 2022.
- `test.cmd` covers version/package guards and pure helpers (parsers, formatters, path/session
  logic). `SKIP_TESTS=1` bypasses the `publish.cmd` gate.
- The test project has no `Release|Any CPU.Build.0` entry, so the Release rebuild in
  `publish.cmd` does not build it.
- Anything needing a live VS — terminal embedding, provider round-trip, settings dialog — has
  no automated coverage. Exercise it by hand in the Exp hive.

---

## Debug in the Exp hive

```bash
./deploy-exp.cmd            # build Debug + deploy to Exp
./deploy-exp.cmd -release   # Release instead
./deploy-exp.cmd -run       # deploy, then launch devenv /rootsuffix Exp with the solution
```

F5 / Ctrl+F5 inside Visual Studio does the same for Debug and Release: the csproj sets
`VSSDKTargetPlatformRegRootSuffix=Exp` and turns on `DeployExtension` for builds inside VS,
because the VSSDK targets default `DeployExtension` to false and F5 would otherwise open a
clean Exp instance with no extension in it. Command-line builds (`test.cmd`, `publish.cmd`)
keep the default and deploy nothing.

Gotchas:

- **Close the Exp instance before deploying** — otherwise it keeps running the previous build.
- First deploy into a hive that never had the extension fails with `VSSDK1031 ... could not be
  found`. The script recovers by running `devenv /rootsuffix Exp /updateconfiguration` and
  retrying.
- Each deploy lands in a version-named folder, so a version bump leaves the old one behind and
  the hive can silently keep loading the older assembly with no error anywhere. The
  `RemoveStaleExpDeployments` csproj target deletes sibling version folders after every deploy.

---

## Publish

Any phrasing — "publish the app", "publish to marketplace", "ship it" — means one thing:

```bash
./publish.cmd    # from the repo root
```

Do **not** invoke MSBuild or marketplace APIs by hand; `publish.cmd` is the authoritative
automation. It runs `test.cmd` → Clean → Rebuild Release → publish the VSIX through
`VsixPublisher.exe` with `publishManifest.json`, falling back from VS 2026 to VS 2022 tool
paths. Success is detected via the `VsixPub0038` log marker, which works around the
VsixPublisher telemetry crash in VS 18.

`publishManifest.json` holds the marketplace metadata: publisher `dliedke`, category `coding`,
free, Q&A enabled, README.md as the overview.

Publishing is outward-facing and hard to reverse — confirm with the user before running it
unless they just asked for it.

---

## Add a new AI provider

1. `ClaudeCodeModels.cs` — add to the `AiProvider` enum; add a settings property if needed.
   **Never renumber existing ordinals** (6 is retired `QwenCode`); persisted user settings
   depend on them being stable.
2. `ProviderManagement.cs` — detection method, cache logic, install instructions, notification
   flag, menu handlers, `UpdateProviderSelection()`, `ProviderContextMenu_Opened()`.
3. `Terminal.cs` — command building in `StartEmbeddedTerminalAsync()` (**both** the CMD and WT
   paths), `providerTitle` switch, `InitializeTerminalAsync()`,
   `RestartTerminalWithSelectedProviderAsync()`, `UpdateAgentButton_Click()`,
   `Get{Provider}Command()`.
4. `TerminalIO.cs` — Enter-key behavior in `SendEnterKey()`; add to `isOtherWSLProvider` if WSL.
5. `UserInput.cs` — add to the `isWSLProvider` check for WSL path conversion.
6. `Detach.cs` — add to the `GetCurrentProviderName()` switch.
7. `ClaudeCodeControl.xaml` — context menu item, plus a settings item if the provider has flags.
8. `SessionHistory.cs` — update `IsClaudeCodeSessionHistoryProvider()` if it supports JSONL
   transcripts; call `RefreshSessionHistoryButton()` from `UpdateProviderSelection()`.
9. `ModelCatalog.cs` — add a `ModelCatalogSources` entry if the CLI can list its models (plus a
   parser in `Agents/ModelCatalog.cs` if no existing shape fits), and teach
   `GetModelLaunchFlag` / `GetLiveModelSwitchCommand` how the pick is applied. Skip this and
   the agent gets no model menu at all.
10. `README.md` — one row/line each in Features, System Requirements, AI Provider Menu, Updating.
11. `CLAUDE.md` — one row in the Supported AI Providers table.

For native mode, also add an `IAgentSession` adapter under `Agents/` — see
`ARCHITECTURE.md` → *Native Mode — Agent Sessions* for the contract and the
streaming-duplication traps.

---

## Add a new setting

1. `Models/ClaudeCodeModels.cs` — add the property to the settings class with its default.
   Defaults must match what a fresh install should do; the JSON is merged over them.
2. Surface it — `ClaudeCodeControl.SettingsDialog.cs` (pick the right tab) or the ⚙ menu.
3. Apply it on load in `ClaudeCodeControl.Settings.cs` (`LoadSettings()` re-runs, so make the
   apply idempotent).
4. `ARCHITECTURE.md` → *Data Models & Settings* — add it to the Key settings list with its
   default and a one-line description.

Persistence is JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json` via
Newtonsoft.Json. Note that `SaveSettings()` deliberately preserves provider/model/effort fields
from disk during normal operation so parallel VS instances don't clobber each other — if your
setting is per-instance selection state, follow that pattern.

---

## Add a new UI file

**XAML control** — both files go in `UI/`, and the csproj needs two entries:

```xml
<Page Include="UI\Foo.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
...
<Compile Include="UI\Foo.xaml.cs">
  <DependentUpon>Foo.xaml</DependentUpon>
</Compile>
```

**Partial class of `ClaudeCodeControl`** — goes in `Controls/`, named
`ClaudeCodeControl.<Area>.cs`, plus a plain `<Compile Include="...">` entry.

Every `.cs` file needs the copyright header:

```csharp
/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: <description>
 * ***********************/
```

Namespaces: `ClaudeCodeVS` for controls/models, `ClaudeCodeExtension` for the package.

---

## Pre-flight gates

Check these **before writing the code**, not in review. Full text in `ARCHITECTURE.md` →
*Cross-Cutting Rules*.

**Sending text to the agent** (v82.0) — use `SendTextToAgentAsync()`, never
`SendTextToTerminalAsync()`. Native mode has no console, so a direct terminal call silently
sends nothing. Exceptions that stay console-only: slash commands, CLI self-updates, plugin
installs. Any new console-scraping logic must bail out when `IsNativeModeActive`.

**Provider-dependent UI** (v24.0) — checkmarks, tool-window captions, model/usage menu
visibility, detached-tab caption, visible-agent "active" labels: read `_currentRunningProvider`
when a terminal is alive, falling back to `_settings.SelectedProvider` only before launch.

**Focusing the terminal** (v26.0) — never `SetForegroundWindow(terminalHandle)` or bare
`SetFocus(terminalHandle)`. Use `FocusTerminalForInputAsync()`, `FocusTerminalForInput()`, or
`FocusTerminalWindow()`. Low-level hook focus checks stay Win32-only on cached root-window
state — do not touch WPF/WinForms controls from the hook thread.

**Bumping Newtonsoft.Json** — don't. It's pinned to 13.0.3, the version VS itself loads
(issue #112). `PackageVersionGuardTests` enforces it.

---

## Keep docs in sync

| You changed | Update |
|-------------|--------|
| Any code | Version in the 3 sources + README release note ([Ship a release](#ship-a-release)) |
| Non-obvious behavior in a tracked file | That file's section in `ARCHITECTURE.md` |
| A model, enum value, or setting | `ARCHITECTURE.md` → *Data Models & Settings* |
| A new provider | Supported AI Providers table in `CLAUDE.md` + README sections |
| A new file or folder | Project Structure tree in `CLAUDE.md` |
| A repeatable procedure | This file |

`CLAUDE.md` is always loaded — keep additions to it minimal and push detail into
`ARCHITECTURE.md` or here. Deep per-file explanation never goes in `CLAUDE.md`; its table maps
files to `ARCHITECTURE.md` sections, so update the section, not the table.
