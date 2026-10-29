# Agent Instructions for ClaudeCodeExtension

## Build & Test
- Build: `msbuild ClaudeCodeExtension.sln /p:Configuration=Release` or `msbuild ClaudeCodeExtension.sln /p:Configuration=Debug`
- Tests: `./test.cmd` builds `Tests/ClaudeCodeExtension.Tests.csproj` and runs it under `vstest.console.exe` (no VS instance needed) — version/package guards plus unit tests of pure helpers (parsers, formatters, path/session logic). `publish.cmd` runs it as a gate before releasing (`SKIP_TESTS=1` bypasses it).
- Debug: Press F5 in Visual Studio to launch experimental instance with `/rootsuffix Exp`, or run `./deploy-exp.cmd` (`-release`, `-run`) to build and deploy into the Exp hive. Anything needing a running VS (terminal embedding, provider round-trip, settings dialog) is only exercised this way.

## Rules
- Every code change is a release: bump the MAJOR version by one (minor stays `.0`) in `Properties/AssemblyInfo.cs` (both attributes) and `source.extension.vsixmanifest`, and add a `### Version X.0` entry at the top of README's Version History. `test.cmd` enforces it; details in `CLAUDE.md` and `docs/SKILLS.md`.
- Send agent-bound text through `SendTextToAgentAsync()`, never `SendTextToTerminalAsync()` — native mode has no console.
- Provider-dependent UI reads `_currentRunningProvider` when a terminal is alive, `_settings.SelectedProvider` only before launch.
- Focus the embedded terminal only through `FocusTerminalForInputAsync()` / `FocusTerminalForInput()` / `FocusTerminalWindow()`.
- Do not bump Newtonsoft.Json: it is pinned to 13.0.3, the version VS itself loads (issue #112).
- New or changed extension UI text (chat, panel, menus, dialogs, messages) is wrapped in `L("...")` / `N("...")` / `{local:Loc '...'}` and translated in every `lang/*.json` in the same change; `test.cmd` fails on a missing or orphaned translation (recipe: `docs/SKILLS.md` → *Localize chat text*).
- Change the resumed-transcript limits or any other value chosen for performance only together with a measurement recorded in `docs/PERFORMANCE.md` (method, machine, before/after).

## Code Style
- Language: C# targeting .NET Framework 4.7.2 for Visual Studio 2022/2026 (VS SDK 17.14)
- File Headers: Every `.cs` file must include copyright header with author (Daniel Liedke), copyright year (2026), and proprietary usage notice
- Namespace: Use `ClaudeCodeVS` for main controls, `ClaudeCodeExtension` for package class
- Partial Classes: Main control is split into specialized partial classes (e.g., `ClaudeCodeControl.Settings.cs`, `ClaudeCodeControl.Terminal.cs`)
- Imports: Use explicit `using` statements; avoid wildcard imports. Common: `System`, `System.Windows`, `Microsoft.VisualStudio.Shell`
- Types: Use C# built-in types (`string`, `bool`, `double`) over BCL types (`String`, `Boolean`, `Double`)
- Naming: PascalCase for public members, `_camelCase` with underscore for private fields, camelCase for local variables
- Error Handling: Use try-catch with `Debug.WriteLine` for logging; show `MessageBox` for user-facing errors
- Comments: XML doc comments (`///`) for public members describing purpose; inline comments for complex logic
- Settings: Persist to JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json` using Newtonsoft.Json
- Thread Safety: Use `ThreadHelper.ThrowIfNotOnUIThread()` and `JoinableTaskFactory.SwitchToMainThreadAsync()` for UI thread operations
- Resources: Dispose terminal processes and temp directories in `Dispose()` pattern; clean up `%TEMP%\ClaudeCodeVS\*` on startup
- Constants: Use `const` for hardcoded strings (e.g., `ConfigurationFileName`), `static readonly` for computed values

## Architecture
- Extension embeds a terminal (cmd.exe or wsl.exe) using Win32 interop to host AI CLI tools (Claude Code, Codex, Cursor Agent, Open Code, Devin, PI, Google Antigravity, Reasonix, Qwen Code, Grok) — see CLAUDE.md's Supported AI Providers table for the full list
- "Native mode" (the default since v179.0) replaces the embedded terminal with a chat tab driven by an `IAgentSession` adapter per provider (`Agents/`), instead of scraping console output; Reasonix and any provider without a headless protocol keep the terminal
- Settings, theme, workspace, terminal I/O, and provider management are separated into partial class files for maintainability
- Session history reads Claude Code JSONL transcripts directly, uses Codex App Server for native/WSL thread list, read, delete, and resume flows, and lists Devin sessions with `devin list` + ACP `session/load` (no delete)
