# Claude Code Extension for Visual Studio

A Visual Studio extension that provides seamless integration with Claude Code, OpenAI Codex or Cursor Agent directly within the Visual Studio IDE.

<center>
<img src="https://i.ibb.co/mFcsh3nt/BFB9-B830-8122-4091-9-C8-B-869959-B1-B391.png" alt="Claude Code Extension Screenshot" width=350 height=450 />
</center>

## Features

### 🎯 **Integrated Terminal**
- Embedded terminal within Visual Studio supporting multiple AI providers
- Automatic workspace directory detection when loading solutions
- Seamless command execution without leaving the IDE

### 🤖 **Multiple AI Provider Support**
- **Claude Code**: Full support for Claude Code CLI integration (Windows native)
- **Claude Code (WSL)**: Support for Claude Code running inside WSL (Windows Subsystem for Linux)
- **OpenAI Codex**: Support for Codex AI assistant running inside WSL
- **Cursor Agent**: Support for Cursor Agent running inside WSL
- **Qwen Code**: Support for Qwen Code AI assistant (requires Node.js 20+)
- **Provider Switching**: Easy dropdown menu to switch between providers
- **Smart Detection**: Automatic detection and installation instructions for each AI tool
- **Claude Model Selection**: Quick model switching for Claude Code (Opus, Sonnet, Haiku) with dropdown menu

### ⌨️ **Smart Send Controls**
- **Send with Enter**: Toggle between Enter-to-send and manual send modes
- **Shift+Enter** or **Ctrl+Enter**: Create new lines when Send with Enter is enabled
- **Manual Send Button**: Appears when Send with Enter is disabled

### 🖼️ **Image Support**
- **Clipboard Paste**: Use Ctrl+V to paste images from clipboard in the prompt area
- **File Browser**: Click "Add Image" to select images from file system
- **Image Chips**: Visual representation of attached images with remove functionality
- **Clickable Chips**: Click on image chips to open and view images

### 📝 **Prompt History**
- **Smart History**: Automatically saves up to 50 most recent prompts
- **Quick Navigation**: Use Ctrl+Up/Ctrl+Down to browse through previous prompts
- **Clear Option**: Right-click in prompt area to clear history
- **Persistent Storage**: History saved between Visual Studio sessions

### 🔧 **Workspace Intelligence**
- **Solution Detection**: Automatically detects and switches to solution directory
- **Dynamic Updates**: Terminal restarts when switching between solutions
- **Fallback Handling**: Smart directory resolution when no solution is open

### 💾 **Persistent Settings**
- **JSON Configuration**: Settings stored in `%LocalAppData%\..\Local\ClaudeCodeExtension\claudecode-settings.json`
- **Send with Enter State**: Remembers your preferred input mode
- **Splitter Position**: Maintains your preferred layout between sessions
- **AI Provider Selection**: Remembers your preferred AI assistant
- **Claude Model Selection**: Remembers your last selected Claude model (Opus, Sonnet, or Haiku)

### 🎨 **Visual Studio Integration**
- **Dark/Light Theme**: Consistent with Visual Studio's dark/light theme
- **Resizable Layout**: Adjustable splitter between prompt and terminal areas
- **Native Controls**: Follows Visual Studio UI conventions
- **Dynamic Titles**: Window title changes based on selected AI provider

## System Requirements

- Visual Studio 2022 or 2026 (x64 and ARM64 supported)
- Windows operating system
- **For Claude Code (Windows)**: Claude Pro or better paid subscription + Claude Code CLI installed and accessible via `claude` in path.
  Refer to https://docs.claude.com/en/docs/claude-code/setup for Claude Code installation
- **For Claude Code (WSL)**: Claude Pro or better paid subscription + Windows Subsystem for Linux (WSL) + Claude Code CLI installed inside WSL
  Installation instructions are provided automatically if not installed
- **For OpenAI Codex**: Chat GPT Plus or better paid subscription + Windows Subsystem for Linux (WSL) + Codex AI assistant installed inside WSL
  Installation instructions are provided automatically if not installed
- **For Cursor Agent**: Windows Subsystem for Linux (WSL) + Cursor Agent installed inside WSL
  Installation instructions are provided automatically if not installed
- **For Qwen Code**: Node.js version 20 or higher + Qwen Code CLI installed and accessible via `qwen` in path.
  Installation instructions are provided automatically if not installed

## Installation

1. Download the latest VSIX package
2. Double-click the VSIX file to install
3. Restart Visual Studio
4. Open the extension via **View** → **Other Windows** → **Claude Code Extension**

**In case terminal is opening in a new window and not inside the extension:
Open windows settings, search for "Terminal settings", change Terminal option to "Windows Console Host".**

## Quick Start

- **First Time Setup**: Ensure your preferred AI provider (Claude Code, Claude Code WSL, OpenAI Codex, Cursor Agent, or Qwen Code) is installed and accessible
- **Open Tool Window**: View → Other Windows → Claude Code Extension
- **Choose AI Provider**: Click the ⚙ (gear) button to select between Claude Code, Claude Code (WSL), Codex, Cursor Agent, and Qwen Code
- **Select Claude Model**: Click the 🤖 (robot) button to choose between Opus, Sonnet, or Haiku (only visible when Claude Code is selected)
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

### Working with Prompt History

- **Browse Previous Prompts**: Press **Ctrl+Up** to navigate to older prompts in your history
- **Browse Forward**: Press **Ctrl+Down** to move to newer prompts or return to current text
- **View Attached Images**: Click on any image chip to open and view the image
- **Clear History**: Right-click in the prompt area and select "Clear Prompt History"
- **Automatic Saving**: Your last 50 prompts are automatically saved between sessions

### AI Provider Menu
- **Settings Menu**: Click the ⚙ (gear) button in the top-right corner to access provider settings
- **Claude Code**: Switch to Claude Code CLI integration (Windows native)
- **Claude Code (WSL)**: Switch to Claude Code running inside WSL
- **Codex**: Switch to Codex AI assistant (runs inside WSL)
- **Cursor Agent**: Switch to Cursor Agent (runs inside WSL)
- **Qwen Code**: Switch to Qwen Code AI assistant (requires Node.js 20+)
- **About**: View extension version and information

### Claude Model Selection Menu
- **Model Menu**: Click the 🤖 (robot) button to access Claude model selection (only visible when Claude Code or Claude Code WSL is selected)
- **Opus - Complex tasks**: Switch to Claude Opus for complex, multi-step tasks requiring deep reasoning
- **Sonnet - Everyday tasks**: Switch to Claude Sonnet for balanced performance on everyday coding tasks (default)
- **Haiku - Easy tasks**: Switch to Claude Haiku for quick, straightforward tasks with faster responses
- **Instant Switching**: Model changes are applied immediately by sending the `/model` command to the running terminal
- **Persistent Selection**: Your model choice is saved and restored between Visual Studio sessions

### Customization
- **Send with Enter**: Check/uncheck the checkbox to toggle sending behavior
- **Layout**: Drag the splitter to adjust the prompt/terminal ratio
- **AI Provider**: Use the context menu to switch between available providers
- **Settings persist automatically** between Visual Studio sessions

### Updating Your AI Agent

The extension includes a convenient Update Agent button (🔄️) that automatically updates your selected AI provider:

- **Claude Code (Windows)**: Exits the agent and runs `claude update`
- **Claude Code (WSL)**: Exits the agent and runs `claude update` inside WSL
- **Codex**: Exits the agent and runs `npm install -g @openai/codex@latest` inside WSL
- **Cursor Agent**: Exits the agent and runs `cursor-agent update` inside WSL
- **Qwen Code**: Exits the agent (using /quit command) and runs `npm install -g @qwen-code/qwen-code@latest` to update

Simply click the update button and the extension will handle the entire update process for you. Agents use appropriate exit methods (exit command for most, double CTRL+C for Codex, /quit command for Qwen Code) before updating.

## Version History

### Version 3.5

- Fixes for products supported

### Version 3.4

**New Features:**
- **ARM64 Support**: Added support for ARM64 architecture, enabling the extension to run on Visual Studio 2022/2026 ARM64 versions (e.g., Surface Pro X, Windows Dev Kit 2023, and other ARM-based Windows devices)

### Version 3.3

**Improvements:**
- **Clipboard Preservation**: The extension now preserves and restores your original clipboard content when sending commands to the AI agent
  - Supports all clipboard content types including Office data (Excel cells, Word content), images, files, HTML, and RTF
  - Excel cells are restored properly so you can paste them back as cells (not as images)
  - Your clipboard contents are automatically saved before sending a command and restored afterward
  - No more losing copied content when interacting with the AI terminal

### Version 3.2

**New Features:**
- **Claude Model Selection Dropdown**: Added a new 🤖 (robot) button next to the provider settings that allows quick switching between Claude models
  - **Opus**: For complex, multi-step tasks requiring deep reasoning
  - **Sonnet**: For everyday coding tasks with balanced performance (default)
  - **Haiku**: For quick, straightforward tasks with faster responses

### Version 3.1

- Fixed instructions and about screens

### Version 3.0

- Qwen Code support. And yes, it was developed with Qwen Code itself for testing purposes. Images not supported.

### Version 2.8

- Fix issues hiding terminal in tab switching

### Version 2.7

- Native Claude Code support for Windows

### Version 2.6

**New Features:**
- **Clickable Image Chips**: Click on attached image chips to open and view the images
- **VS 2026 Support**: Extended compatibility to support Visual Studio 2026

**Improvements:**
- **Prompt History**: Added prompt history feature to prevent lost prompts (navigate with Ctrl+Up/Ctrl+Down, clear with context menu)

### Version 2.5

**Updated Install Instructions:**
- Added troubleshooting tips for installation issues

### Version 2.4

**Simplified Window Titles:**
- Window title now shows "Claude Code" for all Claude Code variants (native Windows and WSL)
- Removed "(WSL)" suffix from window titles for cleaner UI experience
- All Claude Code installations now share the same display name in the tool window

### Version 2.3

**WSL Initialization Fix:**
- Fixed WSL-based agents (Claude Code WSL, Codex, Cursor Agent) not being detected right after system boot
- Added intelligent retry logic with progressive timeouts (5s, 8s, 12s) for WSL agent detection
- Implements up to 3 detection attempts with 2-second delays between retries to handle WSL initialization delays
- Improved reliability when opening Visual Studio immediately after boot
- Enhanced debug logging for better troubleshooting of WSL-related issues

### Version 2.2

**Claude Code WSL Support & Simplified Exit Logic:**
- Added new **Claude Code (WSL)** option for running Claude Code inside WSL
- Follows the same WSL integration pattern as Codex and Cursor Agent
- Simplified update agent logic: all agents now use `exit<enter>` command consistently
- Improved terminal restart logic with unified exit handling for all providers
- Enhanced enter key handling for WSL-based providers (Claude Code WSL, Codex, Cursor Agent)
- Automatic detection and installation instructions for Claude Code in WSL environments

### Version 2.1

**Codex WSL Integration & Exit Improvements:**
- Codex now runs inside WSL for better compatibility and performance
- Improved Codex exit handling: right-click terminal center before sending Ctrl+C
- Fixed AI provider switching to correctly exit the current provider (not the new one being selected)
- Smart provider tracking ensures proper exit commands for each AI assistant
- Consistent WSL-based architecture for both Codex and Cursor Agent

### Version 2.0

**Agent Update Button:**
- Added Update Agent button with refresh icon for easy agent updates
- Smart update command execution based on selected provider:
  - Claude Code: Runs `claude update` command
  - Codex: Runs `npm install -g @openai/codex@latest` inside WSL
  - Cursor Agent: Runs `cursor-agent update` inside WSL
- Convenient one-click updates without manually typing commands

### Version 1.8

**VS Restart Fix:**
- Fixed terminal not opening when Visual Studio restarts with a solution already loaded
- Extension now detects when a solution is already open on startup and initializes terminal immediately
- Improves reliability when working with solutions across VS sessions

### Version 1.7

What's New:
- Clean Single-Border Design: Redesigned the UI with elegant single borders around prompt and terminal areas - no more double borders!
- Better Contrast: Borders now use high-contrast colors (white in dark mode, black in light mode) for improved visibility
- Smarter Startup: Terminal now initializes only when you open a solution, not when the extension loads - faster and more efficient!
- Improved Solution Switching: When switching between solutions, the AI assistant properly reloads with the new workspace context
- Bug Fixes: Fixed various initialization and workspace detection issues for a smoother experience

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
  Workaround right now is to run VS.NET as Administrator.

## License & Usage

** IMPORTANT NOTICE**

This extension is proprietary software. **Unauthorized cloning, copying, modification, or distribution is strictly prohibited** without explicit written permission from the author.

### Restrictions
- No commercial use

### Permissions
For licensing inquiries or permission requests, please contact the author.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Build with the help of Claude Opus 4.1, Claude Code, GPT-5 and Qwen Code*