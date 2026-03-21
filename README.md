# Claude Code Extension for Visual Studio

A Visual Studio extension that provides seamless integration with Claude Code, OpenAI Codex, Cursor Agent, Qwen Code or Open Code directly within the Visual Studio IDE.

<center>
<img src="https://i.ibb.co/mFcsh3nt/BFB9-B830-8122-4091-9-C8-B-869959-B1-B391.png" alt="Claude Code Extension Screenshot" width=350 height=450 />
</center>

In case you enjoy this work and want to support it, you can buy me a coffee here: [https://buymeacoffee.com/dliedke](https://www.buymeacoffee.com/dliedke) - every cup helps fuel development and keep the extension free for everyone!

Any feedback, suggestions, or contributions are also very welcome - feel free to post review here, open issues or submit pull requests in the GitHub repository.

## Features

### 🎯 **Integrated Terminal**
- Embedded terminal within Visual Studio supporting multiple AI providers
- Automatic workspace directory detection when loading solutions
- Seamless command execution without leaving the IDE YES

### 🤖 **Multiple AI Provider Support**
- **Claude Code**: Full support for Claude Code CLI integration (Windows native)
- **Claude Code (WSL)**: Support for Claude Code running inside WSL (Windows Subsystem for Linux)
- **OpenAI Codex**: Support for Codex AI assistant (Windows native)
- **OpenAI Codex (WSL)**: Support for Codex AI assistant running inside WSL
- **Cursor Agent**: Support for Cursor Agent CLI integration (Windows native)
- **Cursor Agent (WSL)**: Support for Cursor Agent running inside WSL
- **Qwen Code**: Support for Qwen Code AI assistant (requires Node.js 20+)
- **Open Code**: Support for Open Code AI assistant (requires Node.js 14+)
- **Provider Switching**: Easy dropdown menu to switch between providers
- **Smart Detection**: Automatic detection and installation instructions for each AI tool
- **Claude Model Selection**: Quick model switching for Claude Code (Opus, Sonnet, Haiku) with dropdown menu. For Opus also possible to select low, medium, high thinking modes

### ⌨️ **Smart Send Controls**
- **Send with Enter**: Toggle between Enter-to-send and manual send modes
- **Shift+Enter** or **Ctrl+Enter**: Create new lines when Send with Enter is enabled
- **Manual Send Button**: Appears when Send with Enter is disabled

### 🖼️ **File Attachment Support**
- **Clipboard Paste**: Use Ctrl+V to paste images from clipboard in the prompt area (text content like Excel cells will paste as text)
- **File Browser**: Click "Add File" to select up to 5 files from file system
- **Supported File Types**: Images, PDFs, documents (Word, text), spreadsheets (Excel, CSV), data files (JSON, XML, YAML), code files, and more
- **File Chips**: Visual representation of attached files with remove functionality
- **Clickable Chips**: Click on file chips to open and view files
- **Smart Paste**: Excel cells and other text content paste as text, not images

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
- **Claude Skip Permissions State**: Remembers whether Claude Code starts with `--dangerously-skip-permissions`
- **Codex Full Auto State**: Remembers whether Codex starts with `--full-auto`

### 🔍 **Zoom Support**
- **Prompt Zoom**: Ctrl+Scroll on the prompt text box to increase/decrease font size (range 8–24pt), persisted across sessions
- **Terminal Zoom**: Ctrl+Scroll on the terminal area to zoom in/out, zoom level persisted and replayed on restart (works for both Command Prompt and Windows Terminal)

### 🪟 **Detach / Attach Terminal**
- **Detach**: Click the detach button to pop the terminal out into a separate Visual Studio tool window tab
- **Attach**: Close the detached tab or click the attach button to bring the terminal back to the main panel
- **Expanded Prompt**: When detached, the prompt area automatically grows for more comfortable editing
- **Persistent State**: Detached/attached state is saved and restored across Visual Studio sessions
- **Seamless Operation**: Terminal restart, provider switching, and theme changes work while detached

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
- **For OpenAI Codex (Windows)**: Chat GPT Plus or better paid subscription + Codex CLI installed and accessible via `codex` in path.
  Installation instructions are provided automatically if not installed
  Optional: Use `--full-auto` flag for automated approval mode via the extension settings menu
- **For OpenAI Codex (WSL)**: Chat GPT Plus or better paid subscription + Windows Subsystem for Linux (WSL) + Codex AI assistant installed inside WSL
  Installation instructions are provided automatically if not installed
  Optional: Use `--full-auto` flag for automated approval mode via the extension settings menu
- **For Cursor Agent (Windows)**: Cursor Agent CLI installed and accessible via `agent` in path.
  Installation instructions are provided automatically if not installed
- **For Cursor Agent (WSL)**: Windows Subsystem for Linux (WSL) + Cursor Agent installed inside WSL
  Installation instructions are provided automatically if not installed
- **For Qwen Code**: Node.js version 20 or higher + Qwen Code CLI installed and accessible via `qwen` in path.
  Installation instructions are provided automatically if not installed
- **For Open Code**: Node.js version 14 or higher + Open Code CLI installed and accessible via `opencode` in path.
  Installation instructions are provided automatically if not installed

## Installing Windows Terminal (Optional)

Windows Terminal provides better emoji, Unicode, and ANSI rendering compared to Command Prompt.

To install, open **Command Prompt as Administrator** and run:

```
winget install --id Microsoft.WindowsTerminal -e
```

After installing, restart Visual Studio. Then select Windows Terminal via the ⚙ menu → **Set Terminal Type...**.

## Installation

1. Download the latest VSIX package
2. Double-click the VSIX file to install
3. Restart Visual Studio
4. Open the extension via **View** → **Other Windows** → **Claude Code Extension**

**If the terminal opens in a separate window instead of inside the extension:
Open Windows Settings, search for "Terminal settings", and set the Terminal option to "Windows Console Host".**

## Quick Start

- **First-Time Setup**: Verify that your preferred AI provider (Claude Code, Claude Code WSL, OpenAI Codex, Cursor Agent, Cursor Agent WSL, or Qwen Code) is installed and accessible
- **Open the Tool Window**: View → Other Windows → Claude Code Extension
- **Select an AI Provider**: Click the ⚙ (gear) button and choose among Claude Code, Claude Code (WSL), Codex, Cursor Agent, Cursor Agent (WSL), Qwen Code, or Open Code
- **Connect to Provider**: If you use Open Code, press **Ctrl+P**, search for "connect providers", and complete the authentication flow
- **Select a Claude Model**: Click the 🤖 (robot) button to choose Opus, Sonnet, or Haiku (available only when Claude Code is selected)
- **Start a Session**: Enter your prompt and press Enter
- **Attach Files**: Use Ctrl+V to paste or click the "Add File" button
- **Customize**: Toggle "Send with Enter" and adjust the layout as needed

## Usage

1. **Open the Tool Window**: Navigate to View → Other Windows → Claude Code Extension
2. **Select an AI Provider**: Click the ⚙ (gear) button and choose your preferred assistant
3. **Enter Prompts**: Type your questions or requests in the prompt area
4. **Attach Files**: Paste images/text with Ctrl+V or use the "Add File" button to attach up to five files
5. **Send Messages**: Press Enter (if enabled) or click the Send button
6. **Review Responses**: Read responses in the embedded terminal and interact with it directly as needed
7. **Review Code Changes**: (Only projects in Git) Use the integrated diff tool to compare code changes in a new tab while the AI is working.Option to search, auto-scroll, double click in filename to open, double click in code line to navigate to file

### Working with Prompt History

- **Browse Previous Prompts**: Press **Ctrl+Up** to navigate to older prompts in your history
- **Browse Forward**: Press **Ctrl+Down** to move to newer prompts or return to current text
- **View Attached Files**: Click on any file chip to open and view the file
- **Clear History**: Right-click in the prompt area and select "Clear Prompt History"
- **Automatic Saving**: Your last 50 prompts are automatically saved between sessions

### AI Provider Menu
- **Settings Menu**: Click the ⚙ (gear) button in the top-right corner to access provider settings
- **Claude Code**: Switch to Claude Code CLI integration (Windows native)
- **Claude Code (WSL)**: Switch to Claude Code running inside WSL
- **Codex**: Switch to Codex AI assistant (runs inside WSL)
- **Cursor Agent**: Switch to Cursor Agent CLI integration (Windows native)
- **Cursor Agent (WSL)**: Switch to Cursor Agent (runs inside WSL)
- **Open Code**: Switch to Open Code AI assistant (Windows)
- **Qwen Code**: Switch to Qwen Code AI assistant (requires Node.js 20+)
- **Auto-open Changes on Send**: (Git projects only) Automatically opens the Changes view, expands all files, and enables auto-scroll when you send a prompt - perfect for watching the AI work in real-time
- **Claude Code: Skip Permissions**: (Claude providers only) Starts Claude Code with `--dangerously-skip-permissions`, saves the preference, and reloads Claude Code immediately when changed
- **Codex: Full Auto**: (Codex providers only) Starts Codex with `--full-auto`, saves the preference, and reloads Codex immediately when changed
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
- **Settings Persist Automatically**: Preferences are saved between Visual Studio sessions

### Updating Your AI Agent

The extension includes an Update Agent button (🔄️) that updates your selected AI provider:

- **Claude Code (Windows)**: Exits the agent and runs `claude update`
- **Claude Code (WSL)**: Exits the agent and runs `claude update` inside WSL
- **Codex**: Exits the agent and runs `npm install -g @openai/codex@latest` inside WSL
- **Cursor Agent (Windows)**: Exits the agent and runs `agent update`
- **Cursor Agent (WSL)**: Exits the agent and runs `cursor-agent update` inside WSL
- **Open Code**: Exits the agent and runs `npm i -g opencode-ai`
- **Qwen Code**: Exits the agent (using /quit command) and runs `npm install -g @qwen-code/qwen-code@latest` to update

Click the update button and the extension will handle the update process. Agents use the appropriate exit methods before updating (exit command for most, double CTRL+C for Codex, /quit command for Qwen Code).

## Version History

### Version 9.3
- **Change Account**: Added "Change Account" option in the Claude model menu. Sends `/logout`, prompts the user to switch accounts in the browser, then resumes Claude Code with `claude --resume` (respects `--dangerously-skip-permissions` setting).

### Version 9.2
- **Terminal hidden from taskbar**: The embedded terminal window (Command Prompt or Windows Terminal) no longer appears as a separate entry in the Windows taskbar, keeping the taskbar clean.
- **Terminal layout refresh on solution load**: When opening or closing a solution, the embedded terminal now receives deferred resize/repaint passes to fix visual corruption caused by VS re-layout during solution transitions. Works with both Command Prompt and Windows Terminal.

### Version 9.1
- **Windows Terminal installation instructions**: Added `winget install --id Microsoft.WindowsTerminal -e` command to the "Windows Terminal Not Found" dialog and to the README installation section.

### Version 9.0
- **F5/Ctrl+F5/Shift+F5 forwarding from terminal to VS**: When the embedded terminal has keyboard focus, F5 (Start Debugging), Ctrl+F5 (Start Without Debugging), and Shift+F5 (Stop Debugging) are now intercepted and forwarded to Visual Studio as debug commands instead of being consumed by the terminal. Works with both attached and detached terminal windows.

### Version 8.9
- **Extension icon on tool window tabs**: The extension icon (app.ico) is now displayed on all tool window tabs (main window and detached terminal) for easier identification

### Version 8.8
- **Auto-focus detached terminal on extension focus**: When the terminal is detached to a separate tab and the main extension panel regains focus, the detached terminal tab is automatically activated so the user can see the AI output alongside the prompt area

### Version 8.7
- **Performance: Non-blocking solution/project open**: Changed `SolutionEventsHandler` from synchronous `JoinableTaskFactory.Run` to fire-and-forget `RunAsync`, eliminating VS hangs when opening solutions/projects (provider detection + terminal startup no longer block the UI thread)
- **Performance: Fast process tree termination**: Replaced WMI-based `KillProcessAndChildren` (1-5 seconds per query) with ToolHelp32 kernel snapshots (sub-millisecond), dramatically speeding up terminal shutdown and VS exit
- **Performance: Background process cleanup on shutdown**: `CleanupResources` now offloads process tree termination and temp directory deletion to a background thread, keeping the UI responsive during VS shutdown
- **Performance: Non-blocking provider menu switching**: All 8 provider menu click handlers converted from blocking `JoinableTaskFactory.Run` to `async void`, preventing UI freezes when switching AI providers
- **Performance: Non-blocking settings menu handlers**: Terminal type, working directory, skip permissions, and Codex full-auto toggle handlers no longer block the UI thread during terminal restart
- **Performance: Non-blocking context menu open**: Provider context menu now uses cached workspace directory instead of synchronous `GetWorkspaceDirectoryAsync` call, eliminating brief hangs on menu open
- **Removed System.Management dependency**: No longer needed after WMI replacement with ToolHelp32

### Version 8.6
- **Fix Windows Terminal commands**: Menu commands (model switch, effort level, usage, set language) now work correctly with Windows Terminal — converted synchronous blocking handlers to async void so focus transfers properly before keyboard simulation
- **Fix Windows Terminal language setting**: Keyboard input for the `/config` TUI (typing "language", arrow keys, space) now uses `keybd_event` instead of `PostMessage` when running in Windows Terminal
- **Terminal lifecycle serialization**: Added semaphore to prevent overlapping terminal start/stop transitions when rapidly switching providers or restarting
- **Improved terminal cleanup**: Uses `WM_CLOSE` + recursive process tree termination instead of simple `Kill`, with safeguards against terminating the VS process itself
- **Non-blocking startup**: Control construction no longer blocks the UI thread for temp directory cleanup or solution events registration
- **Terminal layout stabilization**: Delayed resize/repaint passes after startup and manual Ctrl+Scroll zoom to eliminate stale black regions
- **Codex flag update**: Updated Codex automation flag from `--full-auto` to `--ask-for-approval never` to match current Codex CLI syntax

### Version 8.5
- **Fix terminal zoom tracking**: Replaced WPF PreviewMouseWheel (which never fired for embedded Win32 windows) with a low-level mouse hook that reliably detects Ctrl+Scroll over the terminal, so TerminalZoomDelta is now correctly tracked and replayed on restart
- **Fix Windows Terminal paste**: Changed paste mechanism from right-click (which opens a context menu in WT, causing random text selection) to Ctrl+Shift+V keyboard shortcut, which always pastes reliably
- **Fix Windows Terminal text selection**: Right-click is no longer used for paste in WT, freeing it for the user's normal copy/paste/selection operations

### Version 8.4
- **Detach Terminal - Auto-expand prompt**: On detach, the prompt area automatically grows 80px for more comfortable editing; restores to original size on re-attach
- **Terminal zoom persistence**: Ctrl+Scroll zoom on the terminal is tracked as a delta and replayed via WM_MOUSEWHEEL on every restart, preserving the preferred zoom level across sessions (works for both Command Prompt and Windows Terminal)
- **Prompt font size persistence**: Ctrl+Scroll on the prompt text box adjusts font size and persists across sessions; uses VS default when not explicitly set
- **Detached state persistence fix**: Auto-restore of detached terminal now works when opening any solution, not just when VS starts with a solution already open

### Version 8.3
- **Detach Terminal - Splitter resize**: When terminal is detached, the GridSplitter stays visible so the prompt area can be resized freely
- **Prompt font zoom**: Ctrl+Scroll on the prompt text box increases or decreases the font size (range 8–24pt, persisted across sessions)
- **Detach Terminal fix**: Fixed SplitterPosition being corrupted when SaveSettings was called while the grid was in collapsed state during detach

### Version 8.2
- **Detach Terminal fix**: Terminal now properly fills the entire detached tab (show window first, then re-parent with delayed resize retries)
- **Detach Terminal fix**: Re-attach layout fully restored (layout restored before re-parent so panel has dimensions; delayed resize retries)
- **Detach Terminal fix**: Fixed double re-attach when closing detached tab (Closed event fired twice; added guard + single event source)
- **Detach Terminal fix**: Fixed prompt not being sent when diff changes view is open and terminal is detached
- **Detach Terminal fix**: Prompt area now properly expands to fill all available space when terminal is detached

### Version 8.0
- **Detach Terminal**: New ability to detach the embedded terminal into a separate VS tool window tab via a detach button below the Update button
- When detached, the prompt area expands to fill the available space
- Closing the detached tab automatically re-attaches the terminal to the main panel
- Detached/attached state persists across Visual Studio sessions
- Terminal restart and provider switching work seamlessly while detached
- Theme changes are reflected in the detached terminal window

### Version 7.8
- **Fixed Show Usage for Windows Terminal**: Simplified Show Usage to use `/usage` command directly instead of navigating `/config` menu with keyboard simulation
- **Adjusted Windows Terminal zoom level**: Reduced initial zoom out from 4 to 3 steps for better readability

### Version 7.7
- **Added Windows Terminal support**: Added configurable terminal type selection (Command Prompt vs Windows Terminal) via new "Set Terminal Type..." menu option
- **Windows Terminal integration**: Seamlessly embeds Windows Terminal window into the extension panel using `SetParent` Win32 interop, with DPI-aware tab bar positioning
- **Better emoji and Unicode rendering**: Windows Terminal offers superior rendering of emojis, box-drawing characters, and Unicode symbols compared to Command Prompt
- **Automatic Windows Terminal detection**: Extension detects Windows Terminal availability and provides installation links if not found (Microsoft Store or GitHub)

### Version 7.6
- **Fixed Show Usage menu shortcut**: Updated navigation to use Up arrow then tab for selecting the usage option in `/config`, matching the current Claude Code CLI menu layout

### Version 7.5 - by adrian-schmidt contribution
- **Set Working Directory dialog now follows VS theme**: The dialog background, text, input fields, and buttons now adapt to Visual Studio's dark or light theme instead of using system defaults

### Version 7.4
- **Prompt history now saves file attachments**: When a prompt is sent with attached files, the file paths are stored in the history entry. Navigating history with Ctrl+Up/Down automatically restores files that still exist on disk

### Version 7.3
- **Fixed special characters not rendering properly (Issue #17)**: Added `chcp 65001` (UTF-8), `VIRTUAL_TERMINAL_LEVEL=1` environment variable, and automatic console font change to "Cascadia Mono" (via `SetCurrentConsoleFontEx` Win32 API) for full Unicode glyph support including block elements and box-drawing characters used by AI providers
- **Fixed extension not working properly with some prompts (Issue #20)**: The Virtual Terminal Processing, UTF-8 encoding, and font fixes resolve display corruption and unexpected output that occurred when AI providers used ANSI styling codes and Unicode characters
- **Fixed diff viewer not detecting git changes**: Fixed bug where the diff viewer showed "No changes detected" when `git` command failed (not in PATH, timeout, etc.) — previously treated identically to "no changes". Now git failures preserve existing tracker state instead of clearing it
- **Added VS bundled git fallback**: The diff viewer now automatically finds Visual Studio's bundled git when `git` is not in the system PATH, using VS installation paths and `vswhere.exe`
- **Fixed clipboard contention error**: Added automatic retry logic (up to 10 attempts with 100ms delay) for all clipboard operations to handle `CLIPBRD_E_CANT_OPEN` errors that occur when another application holds the clipboard open

### Version 7.2
- **Added Effort Level Selection**: Added effort level options (Auto, Low, Medium, High, Max) to the Model Selection menu, sends `/effort <level>` to Claude Code, setting is persisted across sessions
- **Added Show Usage**: Sends `/config` and navigates to usage display via automated keystrokes
- **Added Set Language**: Sends `/config` and navigates to language selection via automated keystrokes

### Version 7.1
- **Added Codex Full Auto Toggle**: Added `Codex: Full Auto` option to the Code Agent Selection menu (⚙)
  - Starts Codex (Windows and WSL) with `--full-auto` when enabled
  - Setting is persisted in local JSON settings and restored in the next session
  - Changing this option now reloads Codex automatically so the new startup flag applies immediately

### Version 7.0
- **Added Codex Windows native support**: Added Codex as a Windows native provider (running directly on Windows via npm), renamed previous WSL-only Codex to "Codex (WSL)" in the menu

### Version 6.8
- **Fixed 'too many arguments' error when workspace path contains spaces (Issue #11)**: WSL-based providers (Codex, Claude Code WSL, Cursor Agent WSL) now properly quote the workspace path in `cd` commands, preventing bash errors when the solution directory contains spaces

### Version 6.7 - updated documentation

### Version 6.6 - by fooberichu150 contribution

- **Fixed Terminal Embedding Without Requiring Windows Console Host**: The extension now launches `conhost.exe` explicitly (with `-- cmd.exe ...`), bypassing the Windows Terminal delegation mechanism
- Users no longer need to set "Windows Console Host" as their default terminal — Windows Terminal remains the default for all other applications including debug sessions
- Added `FindMainWindowHandleByConhostAsync` which searches both the conhost PID and its cmd.exe child PID via WMI to reliably find the embedded window handle regardless of Windows backward-compatibility behavior
- **Fixed Terminal Embedding on Fresh VS Launch / Solution Change**: Replaced WMI-based child process lookup with ToolHelp32 kernel snapshot API (`CreateToolhelp32Snapshot`) for finding the cmd.exe child PID under conhost.exe
- The WMI one-shot query at T+200ms was too early on busy systems, causing `FindMainWindowHandleByConhostAsync` to return `IntPtr.Zero` and the terminal to open as a floating external window instead of being embedded
- ToolHelp32 is sub-millisecond and retried on every poll iteration, reliably finding the child PID regardless of system load
- **Fixed Workspace Change for All Providers**: `OnWorkspaceDirectoryChangedAsync` now correctly handles `CursorAgentNative`, `QwenCode`, and `OpenCode` provider availability checks and install instructions on solution open/change

### Version 6.5
- **Claude Permissions Toggle Added**: Added `Claude Code: Skip Permissions` option to the Code Agent Selection menu (⚙)
  - Starts Claude Code (Windows and WSL) with `--dangerously-skip-permissions` when enabled
  - Setting is persisted in local JSON settings and restored in the next session
  - Changing this option now reloads Claude Code automatically so the new startup flag applies immediately

### Version 6.4
- **Double-Click Diff Line to Navigate**: Double-clicking a diff code line in the Changes view now opens the file in the Visual Studio editor and navigates to that specific line number

### Version 6.3
- **Improved Opus Model Selection**: Selecting Opus now automatically opens the thinking mode selector
  - Sends `/model opus` followed by `/model` to present low, medium, and high thinking effort options
  
### Version 6.2
- **Fixed File Attachment for Any File Type**: Resolved issue where non-standard file types (e.g., .FIT, .gpx, and other binary/custom formats) were not being included in the AI prompt when attached via "Add File"
  
### Version 6.1
- **Major Diff View Performance Fix**: Resolved severe Visual Studio slowdowns when the diff view is open with many code changes
  - Moved diff computation to a background thread, eliminating UI freezes during diff processing
  - Made the git status poll timer non-blocking so the UI thread stays responsive during polling cycles
  - Fixed broken change detection that was causing unnecessary full UI rebuilds every 3 seconds
  - Search navigation (Next/Previous match) now updates only the affected lines instead of rebuilding the entire UI
  - Diff panels for collapsed files are now lazily populated, reducing initial render time and memory usage

### Version 6.0
- **Cursor Agent Native Windows Support**: Added native Windows support for Cursor Agent CLI
  - New "Cursor Agent" option in the provider menu for native Windows installation
  - Existing WSL-based Cursor Agent renamed to "Cursor Agent (WSL)" for clarity
  - Automatic detection of `agent.exe` at `%USERPROFILE%\.local\bin\agent.exe` or `agent.cmd` in PATH
  - Installation instructions provided via PowerShell: `irm 'https://cursor.com/install?win32=true' | iex`
  - Update support via `agent update` command

### Version 5.9
- **Improved WSL Path Conversion Logic**: Enhanced handling of WSL UNC paths for Claude Code (WSL) and other WSL-based AI providers

### Version 5.8
- **Fixed WSL Path Conversion Bug**: Corrected handling of WSL UNC paths when running Claude Code (WSL) or other WSL-based AI providers
  - Now properly converts `\\wsl.localhost\<distro>\path` to `/path` instead of incorrectly converting to `/mnt/wsl.localhost/...`
  - Also supports legacy `\\wsl$\<distro>\path` format
  - Maintains correct behavior for Windows drive paths (e.g., `C:\` still converts to `/mnt/c/`)
  - Fixes issue where solutions opened from WSL paths would fail with "No such file or directory" errors

### Version 5.7
- **Fix encoding issues with diff view**: Ensured proper handling of different file encodings to prevent garbled text in diffs
  - Now correctly displays UTF-8, UTF-16, and other common encodings
  - Added fallback logic for unsupported encodings
- **Fixed Diff View Auto-Scroll Bug**: Resolved issue where auto-scroll could get stuck enabled after rapid file changes
  - Improved state management to ensure auto-scroll toggles correctly
  - Added additional logging for troubleshooting
- **Auto stop Auto-Scroll on Manual Scroll**: Auto-scroll now immediately disables when the user manually scrolls the diff view
  - Prevents conflicts between automatic and manual scrolling
  - Enhances user control over the diff view experience

### Version 5.6
- **Improved Diff View Performance**: Enhanced performance for large repositories with many changed files
  - Reduced CPU and memory usage during git polling
  - Optimized diff rendering for faster updates
- **Search Functionality in Diff View**: Added search box to find specific changes quickly
  - Type keywords to quickly locate specific code in the diff list
  - Supports partial matches and case-insensitive search
  - Enter to search and Shift+Enter for last search

### Version 5.5
- **Auto-open Changes on Send**: New option in the Code Agent Selection menu (⚙) to automatically open the Changes view when you send a prompt
  - Automatically opens the Changes tab, expands all files, and enables auto-scroll
  - Perfect for watching the AI work on your code in real-time
  - Only appears when working with Git repositories
  - Setting is saved and persists between Visual Studio sessions
  - Disabled by default - enable it in the ⚙ menu
- **Improved File Change Detection**: Fixed issue where newly created/modified files weren't appearing in the Changes view until the window was reopened
  - Git baseline now refreshes on each poll cycle to detect new files immediately
  - Files that couldn't be read from git HEAD are now still tracked (shown with all lines as additions)
  - Increased git command timeout for better reliability
- **Files Sorted by Modification Time**: Changed files in the diff view are now sorted by last modified time
  - Most recently updated files appear at the bottom of the list
  - Makes it easier to see which files the AI is currently working on
- **Improved Auto-Scroll Toggle Button Visibility**: The auto-scroll button now displays with a distinct blue background and white text when enabled, making it much clearer when auto-scroll is active
- **Auto-Scroll User Preference Tracking**: Auto-scroll now respects user preference
  - When you manually disable auto-scroll, it stays disabled and won't automatically re-enable
  - When you manually enable auto-scroll, automatic re-enabling is allowed again
  - User preference resets after baseline reset (when starting fresh with new changes)
- **Fixed Auto-Scroll Button State Sync**: Resolved issue where button events could fire unexpectedly during programmatic state updates

### Version 5.4
- **Diff View only for projects in Git**: Due to impossible complexities for filewatcher implementation, projects not
in Git repositories will no longer show change button the diff tab. Not the most advanced coding AI or even I could fix the issues.

### Version 5.3
- **Repository-Wide Diff Tracking**: Fixed diff tool to detect all changed files across the entire git repository, not just files within the opened solution directory
  - Files in other projects/directories within the same git repository are now properly detected
  - Modified files outside the solution directory now correctly show as "Modified" instead of "Created"
  - Uses git baseline (`git show HEAD:path`) to get original file content for accurate diff comparison
- **Auto-Scroll for Diff View**: New auto-scroll feature that follows changes as the AI agent codes
  - Automatically enables when new changes are detected
  - Scrolls to show the latest modified files
  - Automatically disables after 3 seconds of inactivity
  - Toggle button (↓↓) to manually enable/disable auto-scroll
- **Improved performance for large repositories using git** (tested with .net runtime repo with 57k+ files)

### Version 5.2
- Fix extension description

### Version 5.1
- **Performance Optimizations for Diff Tool**: Improved performance for large projects
  - Static window title ("Code Changes") instead of dynamic updates with line counts
  - Git status polling now only runs when the diff tab is active
  - File watcher pauses when diff tab is hidden, resumes with forced refresh when activated
  - Reduces CPU and I/O overhead when working with large repositories
- **Zoom Support**: Use Ctrl+Scroll to zoom in/out on the diff view (50% to 300%)
- **Additional Tracked File Types**: Added support for `.vsixmanifest`, `.csproj`, `.vbproj`, `.fsproj`, `.sln`, `.props`, `.targets`, `.resx`, and `.settings` files

### Version 5.0
- **Integrated Diff Tool**: Major release adding a built-in diff tool for comparing code changes in a new tab.
- This is a large new feature and will be stabilized in future releases. If you find issues, please open an issue in the Git repository.
- Supports both Git projects and standalone projects.

### Version 4.2
- **Updated License & Usage Section**: Clarified that the extension is free for all users including commercial/internal use
- **Data Privacy Documentation**: Added links to data retention policies for all supported AI providers (Claude Code, OpenAI Codex, Cursor, Qwen Code, Open Code)
- **Contact Information**: Added author contact email for licensing inquiries

### Version 4.1
- **Enhanced File Support**: "Add Image" button renamed to "Add File" with support for multiple file types
- **Increased File Limit**: Now supports up to 5 file attachments (previously 3 images)
- **Common File Formats**: Added support for documents (PDF, Word, text), spreadsheets (Excel, CSV), data files (JSON, XML, YAML), code files, and more
- **Flexible File Selection**: File browser now accepts all file types (*.*) plus convenient filters for common formats

### Version 4.0
- Fixed Excel cell paste issue - Excel data now pastes as text instead of as an image
- Clipboard text content is now prioritized over image formats to ensure proper paste behavior

### Version 3.8
- Fixed UI lag when typing in prompt textbox by replacing polling-based theme detection with event-driven approach

### Version 3.7
- Performance improvements
- Fix issues setting Sonnet model in some scenarios

### Version 3.6

**New Features:**
- **Open Code Support**: Added support for open code integration

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

This extension is provided free of charge under the MIT License.

### Usage Rights
- **Free for All**: The extension is free to use for personal, educational, and commercial purposes
- **Output Ownership**: All prompts, source code, and generated outputs belong to the user
- **Internal Use**: Commercial organizations may use this extension internally without restriction

### Restrictions
- **No Reselling**: The extension itself may not be sold commercially
- **No Unauthorized Clones**: Creating derivative extensions requires author permission

### Data Handling & Privacy
- **Local Storage**: Up to 50 prompts are cached locally at `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`
- **Cloud Processing**: All prompts are sent to the configured AI provider
- **Data Retention**: Follows each provider's data usage policy:
  - [Anthropic/Claude Code](https://code.claude.com/docs/en/data-usage)
  - [OpenAI/Codex](https://platform.openai.com/docs/guides/your-data)
  - [Cursor](https://cursor.com/privacy)
  - [Qwen Code](https://qwen.ai/privacypolicy)
  - [Open Code](https://opencode.ai/legal/privacy-policy)
- **No Third-Party Access**: Data is only accessible to the configured model provider

### Contact
For licensing inquiries or permission requests, please contact the author at dliedke@gmail.com.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Build with the help of Claude Opus 4.5, Claude Code, GPT-5 and Qwen Code*
