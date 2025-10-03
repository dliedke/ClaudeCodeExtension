# Claude Code Extension for Visual Studio

A Visual Studio extension that provides seamless integration with Claude Code, OpenAI Codex or Cursor Agent directly within the Visual Studio IDE.

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
- **Cursor Agent**: Support for Cursor Agent running inside WSL (Windows Subsystem for Linux)
- **Provider Switching**: Easy dropdown menu to switch between providers
- **Smart Detection**: Automatic detection and installation instructions for each AI tool

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
- **For Cursor Agent**: Windows Subsystem for Linux (WSL) + Cursor Agent installed inside WSL
  Installation instructions are provided automatically when selecting Cursor Agent without WSL/cursor-agent installed

## Installation

1. Download the latest VSIX package
2. Double-click the VSIX file to install
3. Restart Visual Studio
4. Open the extension via **View** → **Other Windows** → **Claude Code Extension**

## Quick Start

- **First Time Setup**: Ensure your preferred AI provider (Claude Code, OpenAI Codex, or Cursor Agent) is installed and accessible
- **Open Tool Window**: View → Other Windows → Claude Code Extension
- **Choose AI Provider**: Click the ⚙ (gear) button to select between Claude Code, Codex, and Cursor Agent
- **Start Chatting**: Type your prompt and press Enter
- **Add Images**: Use Ctrl+V to paste or click "Add Image" button
- **Customize**: Toggle "Send with Enter" and adjust layout as needed

## Usage

1. **Open the Tool Window**: Navigate to View → Other Windows → Claude Code Extension
2. **Select AI Provider**: Click the ⚙ (gear) button and choose your preferred AI assistant
3. **Enter Prompts**: Type your questions or requests in the prompt area
4. **Add Images**: Drag & drop, paste, or use the "Add Image" button
5. **Send Messages**: Press Enter (if enabled) or click the Send button
6. **View Responses**: See AI responses in the embedded terminal below and also interact with it directly

### AI Provider Menu
- **Settings Menu**: Click the ⚙ (gear) button in the top-right corner to access provider settings
- **Claude Code**: Switch to Claude Code CLI integration
- **Codex**: Switch to Codex AI assistant
- **Cursor Agent**: Switch to Cursor Agent (runs inside WSL)
- **About**: View extension version and information

### Customization
- **Send with Enter**: Check/uncheck the checkbox to toggle sending behavior
- **Layout**: Drag the splitter to adjust the prompt/terminal ratio
- **AI Provider**: Use the context menu to switch between available providers
- **Settings persist automatically** between Visual Studio sessions

## Version History

### Version 1.7 ✨

**What's New:**
- **🎨 Clean Single-Border Design**: Redesigned the UI with elegant single borders around prompt and terminal areas - no more double borders!
- **🌓 Better Contrast**: Borders now use high-contrast colors (white in dark mode, black in light mode) for improved visibility
- **⚡ Smarter Startup**: Terminal now initializes only when you open a solution, not when the extension loads - faster and more efficient!
- **🔄 Improved Solution Switching**: When switching between solutions, the AI assistant properly reloads with the new workspace context
- **🐛 Bug Fixes**: Fixed various initialization and workspace detection issues for a smoother experience

### Version 1.6

**Cursor Agent Support:**
- Added full support for Cursor Agent running inside Windows Subsystem for Linux (WSL)
- Automatic WSL detection and path conversion for seamless integration
- Comprehensive installation guide displayed when WSL or Cursor Agent is not detected

**Improvements:**
- Better AI provider persistence across solution changes
- Enhanced provider detection and switching logic

### Version 1.5

**Behind the Scenes:**
- Major code reorganization for better maintainability (split into 13 specialized files)
- Added comprehensive documentation throughout the codebase
- No changes to functionality - everything works exactly the same!

### Version 1.4

**Stability Improvements:**
- Fixed extension re-initialization issue when switching between windows
- Prevents multiple terminal instances from being created
- More reliable overall behavior

### Version 1.3

**File Management & UI:**
- Automatic cleanup of temporary image directories on startup
- Simpler image naming: `image_1.png`, `image_2.png` instead of long timestamps
- Each prompt with images gets its own unique folder to prevent conflicts
- Fixed gear icon (⚙) display in settings button

### Version 1.2

**Multi-Provider Support:**
- Added OpenAI Codex as a second AI assistant option
- Easy switching between Claude Code and Codex via settings menu
- Window title shows which AI provider you're currently using
- Your provider choice is saved between sessions

### Version 1.1

**Visual & Usability:**
- Theme support: Extension now follows Visual Studio's light/dark theme
- Better icon display in the View menu
- Helpful installation instructions if Claude Code is not found
- Fixed image pasting issues

### Version 1.0

**Initial Release:**
- Embedded AI assistant terminal right in Visual Studio
- Send prompts with Enter (or use Shift+Enter for new lines)
- Full image support: paste, drag & drop, or browse for files
- Automatic workspace detection when opening solutions
- All your preferences saved automatically

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