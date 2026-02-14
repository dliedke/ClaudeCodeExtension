# CLAUDE.md - Claude Code Extension for Visual Studio

## Project Overview

This is a **Visual Studio Extension (VSIX)** for Visual Studio 2022/2026 that provides seamless integration with multiple AI code assistants (Claude Code, OpenAI Codex, Cursor Agent, Qwen Code, Open Code) directly within the IDE. It embeds a terminal via Win32 interop and provides features like multi-line prompts, file attachments, prompt history, integrated diff viewer, and theme support.

- **Author**: Daniel Carvalho Liedke (dliedke@gmail.com)
- **License**: MIT
- **Repository**: https://github.com/dliedke/ClaudeCodeExtension
- **Current Version**: 6.3

---

## MANDATORY: Version & Documentation Updates

**IMPORTANT: Every development session that modifies code MUST include the following updates before finishing:**

1. **Bump Assembly Version** in `Properties/AssemblyInfo.cs`:
   - Update both `AssemblyVersion` and `AssemblyFileVersion` (e.g., `6.1.0.0` -> `6.2.0.0`)
2. **Bump Manifest Version** in `source.extension.vsixmanifest`:
   - Update the `Version` attribute in the `<Identity>` tag (e.g., `6.1` -> `6.2`)
3. **Update README.md**:
   - Add a new version entry at the top of the `## Version History` section describing what changed
   - Follow the existing format: `### Version X.Y` with bullet points describing changes

---

## Build & Test

- **Build (Release)**: `msbuild ClaudeCodeExtension.sln /p:Configuration=Release`
- **Build (Debug)**: `msbuild ClaudeCodeExtension.sln /p:Configuration=Debug`
- **Debug**: Press F5 in Visual Studio to launch experimental instance with `/rootsuffix Exp`
- **No automated tests**: Testing is done via F5 debugging in VS 2022/2026 experimental instance
- **Output**: VSIX package for distribution

## Version Files

| File | What to Update |
|------|---------------|
| `Properties/AssemblyInfo.cs` | `AssemblyVersion("X.Y.0.0")` and `AssemblyFileVersion("X.Y.0.0")` on last two lines |
| `source.extension.vsixmanifest` | `Version="X.Y"` in the `<Identity>` tag (line 4) |
| `README.md` | Add new `### Version X.Y` section after the `## Version History` heading |

## Project Structure

```
ClaudeCodeExtension/
├── ClaudeCodeExtension.sln              # Solution file
├── ClaudeCodeExtension.csproj           # Project file (.NET Framework 4.7.2)
├── source.extension.vsixmanifest        # VSIX manifest (version, metadata)
├── Properties/
│   └── AssemblyInfo.cs                  # Assembly version info
├── CLAUDE.md                            # This file
├── AGENTS.md                            # Agent build instructions
├── README.md                            # Full documentation & version history
├── LICENSE.txt                          # MIT License
├── app.ico / app.png                    # Extension icons
│
├── Core Control (partial classes of ClaudeCodeControl):
│   ├── ClaudeCodeControl.cs             # Core initialization & orchestration
│   ├── ClaudeCodeControl.Terminal.cs    # Terminal embedding (cmd.exe/wsl.exe), process init
│   ├── ClaudeCodeControl.ProviderManagement.cs  # AI provider detection & switching
│   ├── ClaudeCodeControl.TerminalIO.cs  # Terminal I/O, command execution
│   ├── ClaudeCodeControl.Diff.cs        # Diff view integration, git polling
│   ├── ClaudeCodeControl.UserInput.cs   # Keyboard input, button handlers
│   ├── ClaudeCodeControl.Workspace.cs   # Solution/workspace directory detection
│   ├── ClaudeCodeControl.ImageHandling.cs # Image paste & file attachments
│   ├── ClaudeCodeControl.Settings.cs    # Settings persistence (JSON)
│   ├── ClaudeCodeControl.Cleanup.cs     # Resource cleanup, temp dir management
│   ├── ClaudeCodeControl.Interop.cs     # Win32 API declarations (P/Invoke)
│   └── ClaudeCodeControl.Theme.cs       # Dark/light theme support
│
├── UI:
│   ├── ClaudeCodeControl.xaml           # Main extension UI layout
│   ├── DiffViewerControl.xaml           # Diff viewer UI
│   ├── DiffViewerControl.xaml.cs        # Diff viewer logic (tree, search, zoom)
│   ├── ClaudeCodeToolWindow.cs          # Main tool window wrapper
│   └── DiffViewerToolWindow.cs          # Diff viewer tool window wrapper
│
├── Diff Engine:
│   ├── Diff/DiffComputer.cs             # Diff computation using DiffPlex library
│   ├── Diff/FileChangeTracker.cs        # Git baseline tracking, change detection
│   └── Diff/ChangedFile.cs              # Data models (ChangeType, DiffLine, ChangedFile)
│
├── Models & Package:
│   ├── ClaudeCodeModels.cs              # Enums (AiProvider, ClaudeModel) & settings class
│   ├── ClaudeCodeExtensionPackage.cs    # VS package registration & menu commands
│   └── SolutionEventsHandler.cs         # Solution/project open events
```

## Code Style & Conventions

- **Language**: C# targeting .NET Framework 4.7.2
- **File Headers**: Every `.cs` file must include copyright header with author (Daniel Liedke), copyright year (2026), and proprietary usage notice
- **Namespaces**: `ClaudeCodeVS` for main controls, `ClaudeCodeExtension` for package class
- **Partial Classes**: Main control is split into specialized partial classes (e.g., `ClaudeCodeControl.Settings.cs`)
- **Naming**: PascalCase for public members, `_camelCase` with underscore for private fields, camelCase for locals
- **Error Handling**: try-catch with `Debug.WriteLine` for logging; `MessageBox` for user-facing errors
- **Thread Safety**: Use `ThreadHelper.ThrowIfNotOnUIThread()` and `JoinableTaskFactory.SwitchToMainThreadAsync()`
- **Settings**: Persist to JSON at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json` using Newtonsoft.Json
- **Constants**: Use `const` for hardcoded strings, `static readonly` for computed values
- **Types**: Use C# built-in types (`string`, `bool`) over BCL types (`String`, `Boolean`)

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.VisualStudio.SDK | 17.0.32112.339 | VS integration |
| Microsoft.VSSDK.BuildTools | 17.14.2101 | Build tools |
| Newtonsoft.Json | 13.0.3 | JSON serialization |
| DiffPlex | 1.7.2 | Diff computation |

## Architecture Notes

- Extension embeds terminal (cmd.exe or wsl.exe) using Win32 interop to host AI CLI tools
- 7 AI providers supported: Claude Code (Win), Claude Code (WSL), Codex (WSL), Cursor Agent (Win), Cursor Agent (WSL), Qwen Code, Open Code
- Diff viewer uses git baseline comparison with DiffPlex for line-by-line diffs
- Background threading for diff computation (v6.1+) to avoid UI freezes
- Settings, theme, workspace, terminal I/O, and provider management are separated into partial class files
- Package GUID: `3fa29425-3add-418f-82f6-0c9b7419b2ca`
- VSIX Identity GUID: `87de5d13-743e-46b3-b05e-24e1cbeca0c3`
- Targets VS 2022/2026 on both x64 and ARM64

## Supported AI Providers

| Provider | Platform | Executable |
|----------|----------|-----------|
| Claude Code | Windows native | `claude` in PATH |
| Claude Code (WSL) | WSL | `claude` inside WSL |
| OpenAI Codex | WSL | `codex` inside WSL |
| Cursor Agent | Windows native | `agent.exe` at `%USERPROFILE%\.local\bin\` or `agent.cmd` in PATH |
| Cursor Agent (WSL) | WSL | `cursor-agent` inside WSL |
| Qwen Code | Windows native | `qwen` in PATH (Node.js 20+) |
| Open Code | Windows native | `opencode` in PATH (Node.js 14+) |

## Key Features

- Embedded terminal within Visual Studio
- Multi-line prompts (Shift+Enter / Ctrl+Enter for newlines)
- File attachments (up to 5 files: images, PDFs, documents, code, etc.)
- Image paste from clipboard (Ctrl+V)
- Prompt history (last 50 prompts, Ctrl+Up/Down navigation)
- Integrated diff viewer with git-based change tracking
- Claude model selection (Opus, Sonnet, Haiku)
- Dark/light theme integration
- Auto-open changes on send
- One-click agent updates
