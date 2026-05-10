# Claude Code Extension for Visual Studio

A Visual Studio extension that provides seamless integration with Claude Code, OpenAI Codex, Cursor Agent, Open Code, Windsurf or PI directly within the Visual Studio IDE.

<center>
<img src="https://i.ibb.co/mFcsh3nt/BFB9-B830-8122-4091-9-C8-B-869959-B1-B391.png" alt="Claude Code Extension Screenshot" width=350 height=450 />
</center>

In case you enjoy this work and want to support it, you can buy me a coffee here: [https://buymeacoffee.com/dliedke](https://www.buymeacoffee.com/dliedke) - every cup helps fuel development and keep the extension free for everyone!

Any feedback, suggestions, or contributions are also very welcome - feel free to post review here, open issues or submit pull requests in the GitHub repository.

[Mentioned in Awesome Codex CLI](https://github.com/RoggeOhta/awesome-codex-cli)

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
- **Open Code**: Support for Open Code AI assistant (requires Node.js 14+)
- **Windsurf (WSL)**: Support for Windsurf (devin) running inside WSL
- **PI**: Support for PI coding agent (requires Node.js + Git for Windows)
- **Provider Switching**: Easy dropdown menu to switch between providers
- **Smart Detection**: Automatic detection and installation instructions for each AI tool
- **Claude Model Selection**: Quick model switching for Claude Code (Opus, Sonnet, Haiku) with dropdown menu. For Opus also possible to select low, medium, high thinking modes

### ⌨️ **Smart Send Controls**
- **Enter to send**: Press Enter to send the prompt to the active code agent
- **Shift+Enter** or **Ctrl+Enter**: Insert a new line in the prompt
- **Toggle "Send with Enter"**: Available in the Code Agent settings menu (⚙). When disabled, Enter inserts a newline and a Send (▶) button appears in the prompt toolbar to submit the prompt

### 📋 **Editor Selection to Prompt**
- **Toolbar Button**: Click the 📋 button to grab the currently selected code from the active editor and insert it into the prompt
- **Editor Context Menu**: Right-click on selected code in the editor and choose "Send Selection to Claude Code"
- **Formatted Snippet**: Code is inserted with file path, line numbers, and syntax-highlighted code fence (e.g., ```csharp)
- **Non-Destructive**: Code is inserted into the prompt without sending — type your question or instruction first, then send
- **Relative Paths**: File paths are automatically made relative to the current workspace/solution directory

### 🖼️ **File Attachment Support**
- **Clipboard Paste**: Use Ctrl+V to paste images from clipboard in the prompt area (text content like Excel cells will paste as text)
- **File Browser**: Click "Add File" to select files from file system (no limit)
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
- **Splitter Position**: Maintains your preferred layout between sessions
- **Invert Layout**: Remembers your preferred panel arrangement (prompt on top or bottom)
- **AI Provider Selection**: Remembers your preferred AI assistant
- **Claude Model Selection**: Remembers your last selected Claude model (Opus, Sonnet, or Haiku)
- **Claude Skip Permissions State**: Remembers whether Claude Code starts with `--dangerously-skip-permissions`
- **Codex Full Auto State**: Remembers whether Codex starts with `--full-auto`
- **Windsurf Dangerous Mode State**: Remembers whether Windsurf starts with `--permission-mode dangerous`

### ⚡ **Custom Commands**
- **User-Defined Shortcuts**: Add slash commands or canned prompts (e.g. `/codex-review`, "explain this file") and dispatch them to the active code agent with one click
- **Configure Dialog**: "Configure Custom Commands..." entry in the Code Agent Selection (⚙) menu opens an Add/Edit/Remove/Reorder UI
- **Toolbar Dropdown**: Once at least one custom command exists, a ⚡ button appears in the toolbar; clicking it shows a menu of saved commands
- **Direct Send**: Selecting a command sends the configured text verbatim to the running agent — no editing in the prompt box first
- **Persistent**: Saved alongside the rest of the extension settings in `claudecode-settings.json`

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
- **For Open Code**: Node.js version 14 or higher + Open Code CLI installed and accessible via `opencode` in path.
  Installation instructions are provided automatically if not installed
- **For Windsurf (WSL)**: Windows Subsystem for Linux (WSL) + Windsurf Paid Plan (devin) CLI installed inside WSL
  Install via `curl -fsSL https://cli.devin.ai/install.sh | bash`
  Installation instructions are provided automatically if not installed
  Optional: Use `--permission-mode dangerous` flag via the extension settings menu
- **For PI**: Node.js installed + Git for Windows (Git Bash) + PI CLI installed and accessible via `pi` in path.
  Install via `npm install -g @earendil-works/pi-coding-agent`
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

- **First-Time Setup**: Verify that your preferred AI provider (Claude Code, Claude Code WSL, OpenAI Codex, Cursor Agent, Cursor Agent WSL, Open Code, Windsurf, or PI) is installed and accessible
- **Open the Tool Window**: View → Other Windows → Claude Code Extension
- **Select an AI Provider**: Click the ⚙ (gear) button and choose among Claude Code, Claude Code (WSL), Codex, Cursor Agent, Cursor Agent (WSL), Open Code, Windsurf (WSL), or PI
- **Connect to Provider**: If you use Open Code, press **Ctrl+P**, search for "connect providers", and complete the authentication flow
- **Select a Claude Model**: Click the 🤖 (robot) button to choose Opus, Sonnet, or Haiku (available only when Claude Code is selected)
- **Start a Session**: Enter your prompt and press Enter
- **Attach Files**: Use Ctrl+V to paste or click the "Add File" button
- **Customize**: Adjust the layout as needed and pick your preferred AI provider from the ⚙ menu

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
- **Windsurf (WSL)**: Switch to Windsurf (devin) running inside WSL
- **PI**: Switch to PI coding agent (Windows)
- **Auto-open Changes on Send**: (Git projects only) Automatically opens the Changes view, expands all files, and enables auto-scroll when you send a prompt - perfect for watching the AI work in real-time
- **Claude Code: Skip Permissions**: (Claude providers only) Starts Claude Code with `--dangerously-skip-permissions`, saves the preference, and reloads Claude Code immediately when changed
- **Codex: Full Auto**: (Codex providers only) Starts Codex with `--full-auto`, saves the preference, and reloads Codex immediately when changed
- **Windsurf: Dangerous Mode**: (Windsurf provider only) Starts Windsurf with `--permission-mode dangerous`, saves the preference, and reloads Windsurf immediately when changed
- **Invert Layout**: Swaps the prompt and terminal positions, placing the terminal on top and the prompt area on the bottom. Buttons stay in the middle between panels.
- **About**: View extension version and information

### Claude Model Selection Menu
- **Model Menu**: Click the 🤖 (robot) button to access Claude model selection (only visible when Claude Code or Claude Code WSL is selected)
- **Opus - Complex tasks**: Switch to Claude Opus for complex, multi-step tasks requiring deep reasoning
- **Sonnet - Everyday tasks**: Switch to Claude Sonnet for balanced performance on everyday coding tasks (default)
- **Haiku - Easy tasks**: Switch to Claude Haiku for quick, straightforward tasks with faster responses
- **Instant Switching**: Model changes are applied immediately by sending the `/model` command to the running terminal
- **Persistent Selection**: Your model choice is saved and restored between Visual Studio sessions

### Claude Code Session History

The new 📜 toolbar button opens Claude Code session history for the current workspace. It is visible only when the selected provider is **Claude Code** or **Claude Code (WSL)**.

- **Current Workspace Sessions**: Lists previous Claude Code sessions from `~/.claude/projects/<encoded-cwd>/*.jsonl`
- **Session Details**: Shows timestamp, message count, token usage, and the first user prompt to help identify the right session
- **Resume**: Select a session and click **Resume** to relaunch Claude with `claude --resume <id>`
- **Resume Last Session**: Click **Resume Last Session** to run `claude --continue` for the most recent session in the current workspace
- **Delete**: Removes the selected transcript file from disk
- **WSL Support**: Works with Claude Code (WSL) by resolving WSL session paths back to Windows paths

### Customization
- **Layout**: Drag the splitter to adjust the prompt/terminal ratio. Use "Invert Layout" in the ⚙ menu to swap prompt and terminal positions
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
- **Windsurf (WSL)**: Exits the agent and runs `devin update` inside WSL
- **PI**: Exits the agent and runs `npm install -g @earendil-works/pi-coding-agent@latest`

Click the update button and the extension will handle the update process. Agents use the appropriate exit methods before updating (exit command for most, double CTRL+C for Codex).

### Custom Commands

User-defined shortcuts that dispatch a slash command or prompt directly to the active code agent.

**Configure**:
1. Click the ⚙ button → **Configure Custom Commands...**
2. Click **Add...**, enter a friendly **Name** (shown in the dropdown) and the **Command** text (sent verbatim to the agent)
3. Use **Edit / Remove / Move Up / Move Down** to manage existing entries, then **Close**
4. Once at least one command is saved, the ⚡ button appears in the toolbar — click it to pick a command and send it instantly

**Recipe — Have Codex review uncommitted code from inside Claude Code via a custom command**

This walkthrough creates a Claude Code skill (`/codex-review`) that shells out to the OpenAI Codex CLI to audit pending changes for bugs, security issues, performance problems, and code quality. You then bind that slash command to a custom command in this extension so it is one click away.

**Step 1 — Install the OpenAI Codex CLI** (if you haven't already):
```bash
npm install -g @openai/codex
codex login
```

**Step 2 — Have Claude Code create the skill for you**. Open a Claude Code session and paste this prompt:

> Create a Claude Code user skill called `codex-review` at `~/.claude/skills/codex-review/SKILL.md`. The skill runs `codex review --uncommitted` (OpenAI Codex CLI) against the current repo's uncommitted changes and surfaces the findings. Preconditions: verify git repo, verify there are uncommitted changes, skip when the diff is only non-meaningful (harness/IDE config, lockfile regens, build artifacts, whitespace), and verify `codex` is on PATH. The Codex prompt should ask for bugs, security issues (OWASP Top 10), performance problems, and code quality findings, each with file:line, severity, why it matters, and a concrete fix. Codex is the reviewer; Claude relays the output verbatim and does not auto-apply fixes. After creating the file, run `/reload-plugins` so the skill is registered.

Claude will write the `SKILL.md` file with the proper frontmatter and instructions, then reload plugins so `/codex-review` becomes available immediately.

**Step 3 — Bind it as a custom command in this extension**:
1. Make sure the active provider is **Claude Code** (or **Claude Code (WSL)**)
2. ⚙ → **Configure Custom Commands...** → **Add...**
3. **Name**: `Codex Review`
4. **Command**: `/codex-review`
5. **OK** → **Close**

**Step 4 — Use it**. Click the new ⚡ button in the toolbar → **Codex Review**. Claude Code receives the `/codex-review` slash command, runs the skill, calls Codex against your uncommitted diff, and reports back the findings — without you ever leaving Visual Studio.

## Version History

### Version 10.48 - CholmesFr contribution
- **PI Coding Agent support**: Full integration with [PI Coding Agent](https://github.com/earendil-works/pi-coding-agent) — a new AI provider that runs natively on Windows (requires Node.js + Git for Windows). Install via `npm install -g @earendil-works/pi-coding-agent`.

### Version 10.47
- **Fix: Theme change prompts**: No more duplicate "Theme Changed" dialogs stacking on top of each other. The restart prompt is also skipped when the new theme color matches what the agent already has — both for VS theme switches and for the "Set Theme..." menu — so switching between themes with the same color no longer asks to restart. The color the agent was started with is now remembered across VS sessions.

### Version 10.46
- **Theme preference setting**: New "Set Theme..." option in the Settings menu lets you force Dark or Light theme regardless of the Visual Studio IDE theme. Default is Automatic (follows VS). When a forced theme is active, VS theme changes no longer prompt to restart the terminal. (Fixes [#47](https://github.com/dliedke/ClaudeCodeExtension/issues/47))
- **Fix: Large prompt truncation**: Post-paste delay before sending Enter now scales with text length (+1ms per 2 characters, capped at 5s extra) so large prompts are fully received by the terminal before submission. (Fixes [#48](https://github.com/dliedke/ClaudeCodeExtension/issues/48))

### Version 10.44
- **Fix: Inline usage bars going stale**: Background refresh timer now always runs (60s default) when inline bars are enabled, regardless of the auto-refresh combo setting. The combo's "Off" value now only suppresses the page-visible reload — it no longer prevents the hidden show-hide cycle that keeps the bars current.

### Version 10.43
- **Light theme support for Command Prompt**: Terminal colors now match VS light/dark theme. Prompts to restart terminal when theme changes.
  - Note: Command Prompt mode only. Windows Terminal uses its own color scheme.

### Version 10.42
- **Toolbar declutter**: Reduced from 12 buttons to 6. Attach + Insert Selection merged into a 📎 dropdown; View Changes + Session History + Show Usage merged into a 📊 Views dropdown; Update Agent and Detach Terminal moved into the ⚙️ Settings menu. Removed Show Usage from the 🤖 Model menu.

### Version 10.41
- **Mouse cursor stays visible while typing in prompt area**: Added `Cursor="IBeam"` to `PromptTextBox` so WPF maintains the text cursor and restores it on mouse move, overriding the Windows "Hide pointer while typing" system behavior.

### Version 10.40
- **Resilient clipboard handoff to terminal**: Sending a prompt no longer fails outright when another process briefly holds the Win32 clipboard (e.g. a clipboard manager such as Win+V history or Ditto, RDP clipboard redirection, or conhost in mark/select mode). Retry ceiling raised from 1s (10×100ms) to 6s (30×200ms). The `SaveClipboardContent` and pre-paste `Clipboard.Clear` calls are now non-fatal — if they exhaust retries, the send still proceeds (worst case: user's prior clipboard isn't preserved). Only `Clipboard.SetText` remains fatal, since the pasted text would otherwise be wrong; on that failure the dialog now names the locking process (e.g. `Ditto.exe (PID 1234)`) and lists common culprits, instead of the bare `CLIPBRD_E_CANT_OPEN` HRESULT. Owner detection uses `GetOpenClipboardWindow` + `GetWindowThreadProcessId`.

### Version 10.39 - Ocrosoft contribution
- **UTF-8 codepage for conhost via per-exe registry subkey**: The embedded Command Prompt now boots with codepage 65001 (UTF-8) by writing `CodePage=65001` to `HKCU\Console\%SystemRoot%_System32_conhost.exe` before launching conhost. The parent `HKCU\Console\CodePage` value is ignored in practice, so the per-executable subkey is required. Original `CodePage` value (and the subkey itself, if it didn't exist) is restored after conhost starts, alongside the existing `FaceName` / `FontFamily` restore. Fixes garbled non-ASCII output in the embedded terminal.

### Version 10.38
- **Usage page auto sign-out on Change Account**: When "Change Account" is selected from the model menu and the Claude Usage feature is active (usage bars or usage window enabled), the embedded WebView2 usage page automatically clears its claude.ai session cookies and reloads — so the new account is reflected without requiring a manual sign-out in the usage window.

### Version 10.37 - devStoner2024 contribution
- **Switch Account button for Claude Usage**: New **👤 Switch Account** button in the Claude Usage tool window toolbar. Clicking it temporarily disables the page-trim CSS and clicks the claude.ai user avatar to surface the native account/organization switcher (e.g. swap between a Team account and a Personal Max account in the embedded view). Click **↻ Refresh** to return to the focused usage view.

### Version 10.36
- **Claude Code session history**: New 📜 toolbar button (visible only for Claude Code / Claude Code WSL) opens a dialog listing past sessions for the current workspace, parsed from `~/.claude/projects/<encoded-cwd>/*.jsonl`. Each entry shows timestamp, message count, token usage, and the first user prompt. Resume relaunches Claude with `--resume <id>`; **Resume Last Session** maps to `claude --continue`. Delete removes the transcript on disk. Works for both native Windows and WSL Claude Code (WSL paths resolved via `wslpath -w`).
- **Drag & drop file attachments**: Files dragged onto the **Prompt / Paste Image** area are now attached as if added via the 📎 button. Folders are skipped, duplicates are filtered out.

### Version 10.35
- **Inline usage bars now refresh correctly**: The Claude usage page layout split bars across multiple `<section>` elements and switched labels from `<p>` to `<span>`/`<div>` tags. The scraper has been rewritten to walk up to the row container, read `.text-primary` (label) and `.text-secondary`/`.text-footnote` (reset) by class, identify session/weekly bars by label text, and parse the displayed `X% used` text for extra usage values exceeding 100%.

### Version 10.34
- **Weekly limit label**: The "All models" inline usage bar is now labelled **Weekly limit** for clarity.
- **Extra usage bar**: Inline usage panel now shows an **Extra usage** row (blue bar) when extra-usage billing is active, displaying the amount spent, reset date, and percentage used (including values over 100%).

### Version 10.33
- **Auto-Refresh Off now works correctly**: Setting Auto-Refresh to Off in the Claude Usage window now stops all background bar refreshing immediately. Previously the background timer kept running at a 5-minute interval regardless of the Off setting. Fixes [#41](https://github.com/dliedke/ClaudeCodeExtension/issues/41).
- **Usage window no longer steals focus**: Background initialization of the Claude Usage WebView2 (on VS startup) now uses `ShowNoActivate` so VS does not steal focus from the user's active editor or another application.

### Version 10.32
- **"Send with Enter" option restored**: Re-added as a toggle in the Code Agent settings menu (next to "Auto-open Changes on Send"). When disabled, Enter inserts a newline and a Send (▶) button appears in the prompt toolbar to submit. Default remains enabled. Fixes [#39](https://github.com/dliedke/ClaudeCodeExtension/issues/39).

### Version 10.31
- **Usage tab no longer blinks during background refresh**: Periodic inline-bar scrapes now reload the hidden WebView2 directly without activating the Claude Usage tab. The tab only becomes visible when the user explicitly opens it.

### Version 10.30
- **Usage bars persist after closing tab**: When the Claude Usage tab is closed via X while inline bars are enabled, the state is saved. On next VS session, the usage window is created hidden in the background so the WebView2 scraper runs and bars stay up to date without the tab being visible.

### Version 10.29
- **Usage bars update on load when enabled**: Inline usage bars now show cached data immediately on extension load without requiring the Claude Usage window to be open. When the usage window was previously open, it auto-reopens and triggers a reload so both the inline bars and the usage tab display fresh data.
- **Usage tab X-close keeps bars updating**: Closing the Claude Usage tab with its X button now hides the frame instead of destroying it, so the embedded scraper keeps running and the inline bars continue to receive live data. The toolbar button still performs a real close (destroys the window and hides the bars).
- **Toolbar button toggles bars with the tab**: Clicking the usage toolbar button to turn off the feature hides the inline bars too; clicking it again re-enables both the bars and the tab.

### Version 10.28
- **Shift+Enter and Ctrl+Enter insert newlines**: Both key combos now reliably insert a newline in the prompt textbox; previously Ctrl+Enter was silently ignored by WPF.

### Version 10.27
- **Fix cursor disappearing after terminal zoom restore**: Mouse cursor reappears automatically after startup zoom replay instead of requiring the user to move the mouse.
- **Fix zoom applied to wrong VS tab**: Terminal zoom on startup now activates the correct VS tool window tab (or detached terminal tab) before sending keystrokes, ensuring zoom always lands on the terminal.

### Version 10.26
- **Fix terminal zoom restore not applying on startup**: Zoom replay now correctly focuses the terminal panel before sending keystrokes, so the zoom lands on the terminal even when Claude Usage or another VS tab is active.

### Version 10.25
- **Fix terminal zoom restore landing on Claude Usage tab**: Ctrl+Scroll zoom replay on startup now posts messages directly to the terminal window handle instead of using simulated keystrokes, so it always targets the terminal regardless of which VS tab has focus.

### Version 10.24
- **Claude Usage tab scrolls to top on refresh**: Page returns to the top after every manual or auto-refresh.

### Version 10.23
- **Claude Usage tab: Ctrl+Scroll zoom with cursor fix**: Ctrl+Scroll zooms the usage page in/out; mouse cursor no longer disappears after zooming.

### Version 10.22
- **Fix Claude Usage tab cursor disappearing and unwanted zoom on scroll**: Scrolling the usage page no longer accidentally zooms the content or hides the mouse cursor.

### Version 10.21
- **Fix Claude Usage progress bars not showing fill**: Usage percentage bars now correctly display their colored fill (e.g. 18% used).

### Version 10.20
- **Claude Usage tab: shared login across VS instances**: Opening a second Visual Studio window automatically picks up the login session from the first — no need to sign in again.

### Version 10.19
- **Fix Claude Usage tab failing when multiple VS instances are open**: Each VS instance now uses its own isolated browser profile so they no longer conflict with each other.

### Version 10.18
- **Claude Usage tab UI polish**: Removed unwanted horizontal scrollbar; toolbar buttons now have visible borders and more spacing.

### Version 10.17
- **Enter always sends the prompt**: The "Send with Enter" toggle and standalone Send button have been removed. Enter always sends; Shift+Enter or Ctrl+Enter inserts a newline.
- **Fix Claude Usage progress bars on wide panels**: Usage bars now expand to fill the full panel width instead of rendering at a fixed narrow size.

### Version 10.16
- **Claude Usage Limits in Visual Studio** ([#38](https://github.com/dliedke/ClaudeCodeExtension/issues/38)): New **📊 Claude Usage** toolbar button (visible when Claude is active) opens a dockable tool window showing your claude.ai plan usage directly inside VS. Includes Refresh, Auto-refresh (Off / 30s / 1m / 2m / 5m), Open in Browser, and Sign out. Compact inline progress bars below the prompt mirror session and weekly usage and update automatically.

### Version 10.15
- **Custom Commands**: Configure reusable commands via the agent menu > "Configure Custom Commands...". Each command has a name and text; a ⚡ toolbar button appears when commands are defined and sends the selected command directly to the active agent. Useful for frequently used slash commands or canned prompts.

### Version 10.14
- **Fix: closing VS no longer closes unrelated Windows Terminal windows** ([#37](https://github.com/dliedke/ClaudeCodeExtension/issues/37)): Shutting down VS or switching providers now only closes the embedded terminal session, leaving any other Windows Terminal windows the user has open untouched.

### Version 10.13
- **Cut/Copy/Paste/Select All in prompt context menu** ([#34](https://github.com/dliedke/ClaudeCodeExtension/issues/34)): Right-clicking the prompt now shows standard text editing actions alongside the history option.

### Version 10.12
- **Qwen Code provider removed**: Qwen Code has been dropped from the provider list. Existing settings that referenced it automatically fall back to Claude Code.
- **More space for the prompt area**: The minimum terminal height has been reduced so the splitter can travel further down.

### Version 10.11
- **Cursor Agent: Yolo Mode**: New toggle in the model menu starts Cursor Agent with `--yolo` to skip all approval prompts, similar to Claude Code’s "Dangerous Skip Permissions" option.
- **Splitter boundary fix**: Dragging the splitter to the bottom of the panel no longer pushes the terminal out of view.

### Version 10.10
- **Install Caveman plugin from menu**: New "Install Caveman" entry in the model menu for Claude Code automatically installs the [Caveman](https://github.com/JuliusBrussee/caveman) ultra-compressed communication plugin into the active session.

### Version 10.8
- **Automated marketplace publishing**: Internal release to validate the new `publish.cmd` deployment script. No user-facing changes.

### Version 10.7
- **Detect winget-installed Claude Code**: Claude Code installed via winget (or any other installer) is now correctly recognized. Fixes [#30](https://github.com/dliedke/ClaudeCodeExtension/issues/30).
- **Fix `claude: command not found` in WSL**: WSL providers now load the full login shell PATH so tools installed via `.profile` or `.bash_profile` are found correctly.

### Version 10.6
- **Invert Layout option**: Swap the prompt and terminal positions via the settings (gear) menu > "Invert Layout".

### Version 10.5
- **Fix repeated WSL install popups**: WSL provider detection now correctly distinguishes "not installed" from shell startup noise, eliminating false "please install" dialogs.
- **Fix floating terminal window**: Terminal embedding is more reliable on slower machines and no longer sometimes appears as a separate floating window.

### Version 10.4
- **Windsurf model selection**: Choose between Claude Opus, Claude Sonnet, Codex, and Gemini Pro for the Windsurf provider.
- **Windsurf Show Usage**: New menu item opens the Windsurf usage page in the browser.

### Version 10.3
- **Windsurf (WSL) provider**: Full support for the Windsurf (devin) agent running inside WSL, including auto-detection, install instructions, one-click update, and dangerous mode toggle.

### Version 10.2
- **Fix CMake / Open Folder project directory**: The terminal now launches in the correct directory for CMake and folder-based projects that don’t use a `.sln` file.

### Version 10.1
- **Send editor selection to prompt**: The 📋 toolbar button inserts the currently selected code from the editor into the prompt as a formatted snippet with file path and line numbers. Also available via right-click > "Send Selection to Claude Code".

### Version 10.0
- **Icon-based toolbar**: Toolbar actions replaced with compact emoji icons (▶ 📎 🔀 ⟳) and tooltips for a cleaner layout.
- **Fix detach icon on theme switch**: The detach/attach button icon now correctly updates when switching between VS dark and light themes.

### Version 9.7
- **Fix button color consistency**: All toolbar buttons now use the same theme-aware text color. Previously, icon buttons (model selector, settings gear, detach) used hardcoded gray text instead of matching the VS theme color like the other buttons.

### Version 9.6
- **UI improvement**: Moved file attachment chips to the "Send with Enter" row, freeing up space in the button toolbar and reducing clutter.
- **Removed file attachment limit**: Previously capped at 5 files; now unlimited files can be attached to a prompt.

### Version 9.5
- **Fix image/file not found by AI**: Consolidated temp file storage into a single `ClaudeCodeVS_Session` folder. Previously, pasted images and per-prompt file copies used separate root folders (`ClaudeCodeVS_Session` and `ClaudeCodeVS`), which caused the AI to sometimes fail to locate attached files.

### Version 9.4
- **Fix text selection blocking prompt**: When a user has selected text in the Command Prompt terminal, the prompt paste would fail. Now sends an extra right-click before pasting to clear any active text selection.

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
- **Increased File Limit**: Added support for multiple file attachments (previously 3 images)
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
  - [Open Code](https://opencode.ai/legal/privacy-policy)
- **No Third-Party Access**: Data is only accessible to the configured model provider

### Contact
For licensing inquiries or permission requests, please contact the author at dliedke@gmail.com.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Build with the help of Claude Opus 4.5, Claude Code, GPT-5 and Qwen Code*
