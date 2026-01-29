# Agent Instructions for ClaudeCodeExtension

## Build & Test
- **Build**: `msbuild ClaudeCodeExtension.sln /p:Configuration=Release` or `msbuild ClaudeCodeExtension.sln /p:Configuration=Debug`
- **No automated tests**: This is a Visual Studio extension; testing is done via F5 debugging in VS 2022
- **Debug**: Press F5 in Visual Studio to launch experimental instance with `/rootsuffix Exp`

## Code Style
- **Language**: C# targeting .NET Framework 4.7.2 for Visual Studio 2022 SDK
- **File Headers**: Every `.cs` file must include copyright header with author (Daniel Liedke), copyright year (2026), and proprietary usage notice
- **Namespace**: Use `ClaudeCodeVS` for main controls, `ClaudeCodeExtension` for package class
- **Partial Classes**: Main control is split into specialized partial classes (e.g., `ClaudeCodeControl.Settings.cs`, `ClaudeCodeControl.Terminal.cs`)
- **Imports**: Use explicit `using` statements; avoid wildcard imports. Common: `System`, `System.Windows`, `Microsoft.VisualStudio.Shell`
- **Types**: Use C# built-in types (`string`, `bool`, `double`) over BCL types (`String`, `Boolean`, `Double`)
- **Naming**: PascalCase for public members, `_camelCase` with underscore for private fields, camelCase for local variables
- **Error Handling**: Use try-catch with `Debug.WriteLine` for logging; show `MessageBox` for user-facing errors
- **Comments**: XML doc comments (`///`) for public members describing purpose; inline comments for complex logic
- **Settings**: Persist to JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json` using Newtonsoft.Json
- **Thread Safety**: Use `ThreadHelper.ThrowIfNotOnUIThread()` and `JoinableTaskFactory.SwitchToMainThreadAsync()` for UI thread operations
- **Resources**: Dispose terminal processes and temp directories in `Dispose()` pattern; clean up `%TEMP%\ClaudeCodeVS\*` on startup
- **Constants**: Use `const` for hardcoded strings (e.g., `ConfigurationFileName`), `static readonly` for computed values

## Architecture
- Extension embeds terminal (cmd.exe or wsl.exe) using Win32 interop to host AI CLI tools (Claude Code, Codex, Cursor Agent, Qwen Code)
- Settings, theme, workspace, terminal I/O, and provider management are separated into partial class files for maintainability
