# Claude Code Extension for Visual Studio

A Visual Studio extension that provides seamless integration with Claude Code or OpenAI Codex directly within the Visual Studio IDE.

<center>
<img src="https://i.ibb.co/mVCs0cNy/Claude-Code-Extension.png" alt="Claude Code Extension Screenshot" />
</center>

## Features

### 🎯 **Integrated Terminal**
- Embedded terminal within Visual Studio supporting multiple AI providers
- Automatic workspace directory detection when loading solutions
- Seamless command execution without leaving the IDE

### 🤖 **Multiple AI Provider Support**
- **Claude Code**: Full support for Claude Code CLI integration
- **OpenAI Codex**: Support for Codex CLI AI assistant
- **Provider Switching**: Right-click context menu to switch between providers
- **Smart Detection**: Automatic detection and instructions for installation of AI tools

### ⌨️ **Smart Send Controls**
- **Send with Enter**: Toggle between Enter-to-send and manual send modes
- **Shift+Enter** or **Ctrl+Enter**: Create new lines when Send with Enter is enabled
- **Manual Send Button**: Appears when Send with Enter is disabled

### 🖼️ **Image Support**
- **Clipboard Paste**: Use Ctrl+V to paste images from clipboard in the prompt area
- **File Browser**: Click "Add Image" to select images from file system
- **Image Chips**: Visual representation of attached images with remove functionality

### 🔧 **Workspace Intelligence**
- **Solution Detection**: Automatically detects and switches to solution directory
- **Dynamic Updates**: Terminal restarts when switching between solutions
- **Fallback Handling**: Smart directory resolution when no solution is open

### 💾 **Persistent Settings**
- **JSON Configuration**: Settings stored in `%LocalAppData%\..\Local\ClaudeCodeExtension\claudecode-settings.json`
- **Send with Enter State**: Remembers your preferred input mode
- **Splitter Position**: Maintains your preferred layout between sessions
- **AI Provider Selection**: Remembers your preferred AI assistant

### 🎨 **Visual Studio Integration**
- **Dark/Light Theme**: Consistent with Visual Studio's dark/light theme
- **Resizable Layout**: Adjustable splitter between prompt and terminal areas
- **Native Controls**: Follows Visual Studio UI conventions
- **Dynamic Titles**: Window title changes based on selected AI provider

## System Requirements

- Visual Studio 2022 17.14 or later
- Windows operating system
- **For Claude Code**: Claude Pro or better paid subscription + Claude Code CLI installed and accessible via `claude.cmd` in path.
  Refer to https://docs.claude.com/en/docs/claude-code/setup for Claude Code installation
- **For OpenAI Codex**: Chat GPT Plus or better paid subscription + Codex AI assistant installed and accessible via `codex.cmd` in path.
  Refer to https://developers.openai.com/codex/cli/ for Codex CLI installation

## Installation

1. Download the latest VSIX package
2. Double-click the VSIX file to install
3. Restart Visual Studio
4. Open the extension via **View** → **Other Windows** → **Claude Code Extension**

## Quick Start

- **First Time Setup**: Ensure your preferred AI provider (Claude Code or OpenAI Codex) is installed and accessible
- **Open Tool Window**: View → Other Windows → Claude Code Extension
- **Choose AI Provider**: Right-click in the terminal area to select between Claude Code and OpenAI Codex
- **Start Chatting**: Type your prompt and press Enter
- **Add Images**: Use Ctrl+V to paste or click "Add Image" button
- **Customize**: Toggle "Send with Enter" and adjust layout as needed

## Usage

1. **Open the Tool Window**: Navigate to View → Other Windows → Claude Code Extension
2. **Select AI Provider**: Right-click in the terminal area and choose your preferred AI assistant
3. **Enter Prompts**: Type your questions or requests in the prompt area
4. **Add Images**: Drag & drop, paste, or use the "Add Image" button
5. **Send Messages**: Press Enter (if enabled) or click the Send button
6. **View Responses**: See AI responses in the embedded terminal below and also interact with it directly

### AI Provider Menu
- **Right-click Context Menu**: Access the provider selection menu by right-clicking in the terminal area
- **Claude Code**: Switch to Claude Code CLI integration
- **Codex**: Switch to Codex AI assistant
- **About**: View extension version and information

### Customization
- **Send with Enter**: Check/uncheck the checkbox to toggle sending behavior
- **Layout**: Drag the splitter to adjust the prompt/terminal ratio
- **AI Provider**: Use the context menu to switch between available providers
- **Settings persist automatically** between Visual Studio sessions

## Version History

### Version 1.5

- **Code Organization Refactoring**: Split the monolithic ClaudeCodeControl.cs into 13 well-organized files for better maintainability
  - **ClaudeCodeControl.cs**: Core initialization and orchestration (128 lines)
  - **ClaudeCodeControl.Cleanup.cs**: Resource cleanup and temporary file management
  - **ClaudeCodeControl.ImageHandling.cs**: Image attachment and paste functionality
  - **ClaudeCodeControl.Interop.cs**: Win32 API declarations and structures
  - **ClaudeCodeControl.ProviderManagement.cs**: AI provider detection and switching
  - **ClaudeCodeControl.Settings.cs**: Settings persistence and management
  - **ClaudeCodeControl.Terminal.cs**: Terminal initialization and embedding
  - **ClaudeCodeControl.TerminalIO.cs**: Terminal communication and I/O
  - **ClaudeCodeControl.Theme.cs**: Visual Studio theme integration
  - **ClaudeCodeControl.UserInput.cs**: Keyboard and button input handling
  - **ClaudeCodeControl.Workspace.cs**: Solution and workspace directory management
  - **ClaudeCodeModels.cs**: Data models and enums
  - **SolutionEventsHandler.cs**: Solution events handler (separate class)
- **Enhanced Documentation**: Added comprehensive XML documentation comments to all methods, properties, and classes
- **Improved Code Structure**: Organized code into logical #region blocks for better navigation
- **No Functional Changes**: Refactoring maintains 100% backward compatibility with existing functionality

### Version 1.4

- **Single Initialization**: Fixed issue where extension would reinitialize every time it became visible after being hidden, now initializes only once
- **Improved Stability**: Enhanced initialization logic prevents multiple terminal instances and improves overall extension stability

### Version 1.3

- **Temporary Directory Cleanup**: Automatically clears %temp%\ClaudeCodeVS directories on initialization to prevent accumulation of old temporary files
- **Simplified Image Naming**: Pasted images now use a clean "image_[n].png" format (e.g., image_1.png, image_2.png) instead of long timestamp-based names
- **Image Counter Reset**: Image numbering restarts from 1 after each prompt is sent, keeping image names simple and organized
- **GUID-Based Prompt Directories**: Each prompt with images creates a unique directory %temp%\ClaudeCodeVS\[guid]\ preventing image overwrites and providing clean organization
- **UI Improvement**: Fixed dropdown button display issue by replacing problematic character with gear icon (⚙)

### Version 1.2

- **Multiple AI Provider Support**: Added support for Codex AI assistant alongside Claude Code
- **Provider Selection Menu**: Right-click context menu to switch between Claude Code and Codex
- **Dynamic Title Updates**: Window title changes based on selected AI provider (Claude Code Extension / Codex Extension)
- **Codex Detection**: Automatic detection of Codex installation at `%UserProfile%\AppData\Roaming\npm\codex.cmd`
- **Provider Persistence**: Selected AI provider is saved and restored between sessions
- **About Dialog**: Added About menu item showing extension version and information
- **Enhanced Terminal Logic**: Improved terminal initialization to work with multiple providers

### Version 1.1

- Dynamic dark/light theme according to Visual Studio theme (except for Claude Code terminal because in white does not look good at all)
- Fixed icon in View -> Other Windows -> Claude Code Extension menu
- If Claude Code claude.cmd is not found in path, show a message box with instructions and open URL for installation if user requests
- Fix issues pasting images in prompt area

### Version 1.0

- Initial release
- Embedded Claude Code terminal
- Send with Enter functionality with Shift+Enter and Ctrl+Enter for new lines
- Image drag & drop, paste, and file selection support
- Automatic workspace directory detection
- Solution event monitoring for dynamic directory switching
- Persistent JSON settings storage
- Resizable layout with splitter position memory
- Dark theme integration

- ## Kwown Issues

- In rare cases for some machines terminal might lauch outside the extension and
  fatal error "Stop code: KERNEL_SECURITY_CHECK_FAILURE (0x139)" can happen.
  Workaround right now is to run VS.NET 2022 as Administrator.

## License & Usage

** IMPORTANT NOTICE**

This extension is proprietary software. **Unauthorized cloning, copying, modification, or distribution is strictly prohibited** without explicit written permission from the author.

### Restrictions
- No cloning or forking of source code
- No modification or derivative works
- No redistribution or commercial use
- No reverse engineering

### Permissions
For licensing inquiries or permission requests, please contact the author.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Build with the help of Claude Opus 4.1, Claude Code and GPT-5*