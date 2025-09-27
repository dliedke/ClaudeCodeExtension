# Claude Code Extension for Visual Studio

A Visual Studio extension that provides seamless integration with Claude Code directly within the Visual Studio IDE.

## Features

### 🎯 **Integrated Terminal**
- Embedded Claude Code terminal within Visual Studio
- Automatic workspace directory detection when loading solutions
- Seamless command execution without leaving the IDE

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
- **JSON Configuration**: Settings stored in `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`
- **Send with Enter State**: Remembers your preferred input mode
- **Splitter Position**: Maintains your preferred layout between sessions

### 🎨 **Visual Studio Integration**
- **Dark Theme**: Consistent with Visual Studio's dark theme
- **Resizable Layout**: Adjustable splitter between prompt and terminal areas
- **Native Controls**: Follows Visual Studio UI conventions

## System Requirements

- Visual Studio 2022 or later
- Claude Pro or higher subscription
- Claude Code CLI installed and accessible via `claude.cmd`. Refer to https://docs.claude.com/en/docs/claude-code/setup for installation
- Windows operating system

## Installation

1. Download the latest VSIX package
2. Double-click the VSIX file to install
3. Restart Visual Studio
4. Open the extension via **View** → **Other Windows** → **Claude Code Extension**

## Usage

1. **Open the Tool Window**: Navigate to View → Other Windows → Claude Code Extension
2. **Enter Prompts**: Type your questions or requests in the prompt area
3. **Add Images**: Drag & drop, paste, or use the "Add Image" button
4. **Send Messages**: Press Enter (if enabled) or click the Send button
5. **View Responses**: See Claude's responses in the embedded terminal below and also interact with it directly


### Customization
- **Send with Enter**: Check/uncheck the checkbox to toggle sending behavior
- **Layout**: Drag the splitter to adjust the prompt/terminal ratio
- **Settings persist automatically** between Visual Studio sessions

## Version History

### Version 1.0.0
- 🎉 Initial release
- ✅ Embedded Claude Code terminal
- ✅ Send with Enter functionality with Shift+Enter and Ctrl+Enter for new lines
- ✅ Image drag & drop, paste, and file selection support
- ✅ Automatic workspace directory detection
- ✅ Solution event monitoring for dynamic directory switching
- ✅ Persistent JSON settings storage
- ✅ Resizable layout with splitter position memory
- ✅ Dark theme integration
- ✅ Visual Studio threading compliance (no warnings)

## License & Usage

**⚠️ IMPORTANT NOTICE**

This extension is proprietary software. **Unauthorized cloning, copying, modification, or distribution is strictly prohibited** without explicit written permission from the author.

### Restrictions
- ❌ No cloning or forking of source code
- ❌ No modification or derivative works
- ❌ No redistribution or commercial use
- ❌ No reverse engineering

### Permissions
For licensing inquiries or permission requests, please contact the author.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Build with the help of Claude Opus 4.1, Claude Code and GPT-5*