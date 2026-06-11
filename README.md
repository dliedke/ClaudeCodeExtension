# Claude Code Extension for Visual Studio

Embedded terminal inside Visual Studio for **Claude Code, OpenAI Codex, Cursor Agent, Open Code, Windsurf, PI, and Google Antigravity** — with multi-line prompts, file attachments, and an integrated diff viewer.

<center>
<img src="https://i.ibb.co/mFcsh3nt/BFB9-B830-8122-4091-9-C8-B-869959-B1-B391.png" alt="Claude Code Extension Screenshot" width=350 height=450 />
</center>

Enjoying the extension? [Buy me a coffee](https://www.buymeacoffee.com/dliedke) — every cup helps keep it free. Bug reports, suggestions, and pull requests are welcome on [GitHub](https://github.com/dliedke/ClaudeCodeExtension).

[Mentioned in Awesome Codex CLI](https://github.com/RoggeOhta/awesome-codex-cli)

## Features

- **Embedded AI terminal** — Run any supported AI coding agent inside a Visual Studio tool window. Auto-detects the solution directory; restarts when you switch solutions. Optionally use Windows Terminal instead of Command Prompt for better emoji/Unicode rendering.
- **Multi-line prompts** — Press **Enter** to send, **Shift+Enter** or **Ctrl+Enter** for a new line. Toggle "Send with Enter" off in the ⚙ menu to make Enter insert a newline and reveal a Send button.
- **File and image attachments** — Paste images with **Ctrl+V**, drag & drop files onto the prompt area, or use the 📎 button. Any file type is accepted (no limit). Text content like Excel cells pastes as text, not as an image.
- **Editor selection → prompt** — Click 📋 or right-click selected code → *Send Selection to Claude Code* to insert a formatted snippet (file path + line numbers + syntax-highlighted code fence) into the prompt.
- **Integrated diff viewer** — For Git projects, the 📊 view shows uncommitted changes in a dedicated tab with search, auto-scroll, double-click-to-open, and double-click-line-to-navigate. Optionally auto-opens when you send a prompt.
- **Prompt history** — Last 50 prompts saved (with attached files). Browse with **Ctrl+Up / Ctrl+Down**; clear via right-click.
- **Claude Code session history** — 📜 toolbar button lists past sessions for the current workspace; resume any session or the most recent one with one click. Works for native and WSL Claude Code.
- **Claude usage in VS** — 📊 button (when Claude is active) opens the claude.ai usage page inside a dockable tab. Inline session/weekly progress bars below the prompt update automatically and adapt to the active theme.
- **Custom commands (⚡)** — Save slash commands or canned prompts and dispatch them to the active agent in one click. Configure via *⚙ → Configure Custom Commands...*.
- **"@" file picker** — Type **@** in the prompt box to search your solution's files and folders and insert one with the keyboard; keep typing to filter, arrow keys + Enter to insert, pick a folder to drill in.
- **On Agent Finish** — Optionally play a sound, show a notification (with duration, plus token count for Claude Code), and run an action (build/rebuild, run, tests, a script, or a follow-up command) when the agent goes idle. Global defaults plus per-solution overrides. Configure via *⚙ → Settings...*.
- **Model selection (Claude)** — 🤖 button to switch between Opus / Sonnet / Haiku and pick an effort level (Auto / Low / Medium / High / Max) for Opus thinking depth.
- **Detach / attach terminal** — Pop the terminal into a separate VS tab and bring it back at any time. State persists across sessions.
- **Theme aware** — Follows VS dark/light theme automatically, or force dark, light, or a custom background color via *⚙ → Settings → Theme*. Prompt and terminal zoom (Ctrl+Scroll) are persisted across sessions.
- **Persistent settings** — Layout, provider choice, model, flags, and zoom level all saved to `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`.

## Supported AI Providers

By default only **Claude Code** is shown in the agent picker — use *⚙ → Configure Visible Code Agents...* to opt in to the others. The active agent always remains visible.

| Provider | Platform | Command | Subscription / Notes |
|----------|----------|---------|----------------------|
| Claude Code | Windows / WSL | `claude` | Claude Pro or higher. [Setup docs](https://docs.claude.com/en/docs/claude-code/setup) |
| OpenAI Codex | Windows / WSL | `codex` | ChatGPT Plus or higher. Optional `--ask-for-approval never` toggle |
| Cursor Agent | Windows / WSL | `agent` / `cursor-agent` | Cursor account. Optional `--yolo` toggle |
| Open Code | Windows | `opencode` | Node.js 14+; provider configured via `Ctrl+P` → "connect providers" |
| Windsurf | WSL | `devin` | Windsurf paid plan. Optional `--permission-mode dangerous` toggle |
| PI | Windows | `pi` | Node.js + Git for Windows |
| Google Antigravity | Windows | `agy` | Google account. Optional `--dangerously-skip-permissions` toggle |

If a provider isn't installed, the extension shows the install command automatically when you select it. The **Update Agent** entry in the ⚙ menu runs the right update command for the active provider (e.g. `claude update`, `npm install -g @openai/codex@latest`, `cursor-agent update`).

## System Requirements

- Visual Studio 2022 or 2026 (x64 or ARM64)
- Windows 11
- Plus whatever the chosen AI provider needs (see table above)

## Installation

1. Download the latest VSIX from this page or search for "Claude Code Extension" inside Visual Studio's **Manage Extensions...** menu
2. Double-click the VSIX file to install or install inside Visual Studio, then restart Visual Studio
3. First time only: Open the tool window via **View → Other Windows → Claude Code Extension**

> If the terminal opens in a separate window instead of inside the extension panel, open Windows Settings → search "Terminal settings" → set **Terminal** to **Windows Console Host**.

**Optional — Windows Terminal**: For better emoji and Unicode rendering, install Windows Terminal from an elevated Command Prompt:
```
winget install --id Microsoft.WindowsTerminal -e
```
Then choose it via *⚙ → Set Terminal Type...*.

## Quick Start

1. Click ⚙ → pick your AI provider (use *Configure Visible Code Agents...* if it isn't listed)
2. If using Open Code, run `Ctrl+P` → "connect providers" once to authenticate
3. (Claude only) Pick a model via the 🤖 button
4. Type a prompt, press **Enter** to send. Attach files with Ctrl+V, drag-and-drop, or 📎
5. Watch the agent work in the embedded terminal. For Git projects, open 📊 to see live diffs

## Settings & Menus

**⚙ Settings menu** (gear button, top-right):
- Pick an AI provider, *Configure Visible Code Agents...*, *Configure Custom Commands...*, *Set Theme...*, *Set Terminal Type...*, *Set Working Directory...*
- Toggles: *Send with Enter*, *Auto-open Changes on Send*, *Prompt panel position (top/bottom/left/right)*, *Disable Auto Zoom on Startup*, *Send large prompts as file*
- Provider-specific flags: Claude *Skip Permissions*, Codex *Approval Never*, Cursor *Yolo Mode*, Windsurf *Dangerous Mode*, Antigravity *Skip Permissions*
- Update Agent, Detach/Attach Terminal, About

**🤖 Model menu** (Claude only): Fable / Opus / Sonnet / Haiku, effort level for Opus (Auto / Low / Medium / High / Max), Change Account, Install Caveman plugin.

**Custom commands (⚡)**: Once you've added a command via *Configure Custom Commands...*, the ⚡ toolbar button appears. Clicking an entry sends the saved text verbatim to the active agent — useful for slash commands or canned prompts.

### Recipe — Codex review of uncommitted code, dispatched from Claude Code

This binds a Claude Code skill that shells out to OpenAI Codex to audit pending changes for bugs, security issues, performance problems, and code quality.

1. **Install Codex CLI** (if needed):
   ```bash
   npm install -g @openai/codex
   codex login
   ```
2. **Create the skill from inside Claude Code** — paste this prompt into a Claude Code session:
   > Create a Claude Code user skill called `codex-review` at `~/.claude/skills/codex-review/SKILL.md`. The skill runs `codex review --uncommitted` against the current repo's uncommitted changes. Preconditions: verify git repo, verify uncommitted changes exist, skip non-meaningful diffs (config/lockfiles/whitespace), and verify `codex` is on PATH. Ask Codex for bugs, OWASP Top 10 issues, performance problems, and code quality findings — each with file:line, severity, why it matters, and a concrete fix. Codex is the reviewer; Claude relays the output verbatim. After creating the file, run `/reload-plugins`.
3. **Bind it as a custom command**: ⚙ → *Configure Custom Commands...* → Add... → Name: `Codex Review`, Command: `/codex-review`.
4. **Use it**: ⚡ → *Codex Review*. Claude runs the skill, Codex audits your diff, findings appear inline.

## Version History

### Version 10.98
- Opening or switching solutions no longer restarts the code agent several times in a row — the agent now starts once in the right folder, which also fixes most cases of the panel coming up blank right after loading a new solution.
- When the launch does fail to attach, the extension now waits for the old session to fully shut down and retries for longer before giving up.

### Version 10.97
- The terminal now retries the whole launch a few times when it comes up blank after "Restart code agent" or when switching solutions, recovering on its own from the brief startup failures that previously left the panel empty until you clicked restart again.

### Version 10.96
- More fixes for the panel staying blank after "Restart code agent": the panel now repairs itself when its hosting area was torn down, and attach failures show an error instead of silently leaving the panel empty until Visual Studio is reopened.

### Version 10.95
- More fixes for the terminal coming up blank after "Restart code agent": the restart now retries once when the terminal closes itself right after launch, and reports an error with a log file path instead of silently leaving the panel empty.

### Version 10.94
- Clicking the agent terminal now reliably focuses it with a single click, so you can immediately answer the agent's questions — previously it could take a second click before typing reached the terminal.

### Version 10.93
- The "On Agent Finish" run-script action now correctly runs `.cmd`/`.bat` and `.ps1` scripts and keeps their window open afterward, so you can read the output instead of the console flashing closed (or a PowerShell script just opening in an editor).

### Version 10.92
- The "On Agent Finish" notification now greys out with an explanation when Windows Terminal is selected, since it only works with the Command Prompt terminal — previously it could be enabled there but silently did nothing.

### Version 10.91
- Removed the "Don't bring Visual Studio to the foreground on terminal click" setting. Windows requires the Visual Studio window to be activated for typing to reach the embedded terminal, so the option could not work reliably and has been retired; clicking the terminal always brings Visual Studio forward again.

### Version 10.90
- Fixed the terminal coming up blank after "Restart code agent" (and other agent restarts) when an "On Agent Finish" notification was enabled — previously the panel could stay empty until Visual Studio was reopened.

### Version 10.89
- Added the new Fable model to the Claude model menu — select "Fable - Most powerful" to switch the running session to Claude's top-tier model.

### Version 10.88
- Fixed the "Don't bring Visual Studio to the foreground on terminal click" setting: clicking the terminal no longer pulls the whole Visual Studio window forward when the option is enabled, so overlapping window layouts stay intact.

### Version 10.87 - ArgoZhang contribution
- Configurable CLI executable path settings, now in a "CLI Paths" tab in the Settings window: point any provider at a specific executable, or leave it empty to use the default detection. A warning appears on save if a path doesn't exist.

### Version 10.86
- The Prompt / Paste Image box now has a drag grip on its bottom edge so you can resize the prompt area directly without hunting for the splitter below the buttons. The grip keeps a minimum prompt size so the input stays usable.

### Version 10.85
- New "Custom background color" theme option under Settings → Theme: pick any color with the color picker or type a hex value (e.g. #F4ECFF) to set the terminal panel and console background.

### Version 10.84
- The Settings window is now organized into tabs (Behavior, Layout, Terminal, Theme, Usage), making each group of options easier to find.
- New "Send prompt with" choice adds a Ctrl+Enter option: Enter inserts a newline and Ctrl+Enter sends, so a stray Enter tap no longer submits an incomplete prompt.
- Prompt font size and the inline usage bar options (show/hide and auto-refresh) can now be set directly in Settings, plus a "Reset to Defaults" button.

### Version 10.83
- Running multiple Visual Studio instances no longer causes the selected AI agent and model to get mixed up across windows. Each instance now keeps its own provider/model choice in memory and only writes it to the shared settings file on shutdown.

### Version 10.82
- New opt-in setting to prevent clicking the embedded terminal from bringing the entire Visual Studio window to the foreground. Useful when overlapping multiple VS instances and you want to interact with the terminal without rearranging your layout. Enable via Settings → "Don’t bring Visual Studio to the foreground on terminal click".

### Version 10.81
- The prompt panel can now be docked on the left or right (a side-by-side split) in addition to the top or bottom. Pick the position under Settings → Layout.

### Version 10.80
- Fixed the prompt becoming unresponsive to the keyboard (cursor not blinking, arrow keys not switching agents) while the embedded terminal still accepted typing — clicking in the extension now restores keyboard input without restarting Visual Studio.

### Version 10.79
- Fixed the prompt and its attached files being sent two or three times when the Send button (or Enter) was pressed again before a send finished.

### Version 10.78
- Fixed updating the PI agent failing because the extension tried to type "exit" — it now quits PI with CTRL+D twice before running the update.

### Version 10.77
- The "On Agent Finish" settings now open in their own window via a button in Settings, and you can keep different settings per solution — turn on "Use custom settings for this solution" to override the global defaults for just the project you're in.
- Fixed the embedded terminal breaking when you switch to a different solution while the agent-finish notification is enabled. Pending notifications are now cleared when a new solution loads.
- The agent-finish notification and action no longer trigger while the agent is waiting for your input (a yes/no confirmation or a selection prompt); they now wait for the real completion after you answer.
- Fixed the agent-finish watcher occasionally interfering with typing in the terminal — it no longer reads the console while you're actively typing there.
- Fixed the agent-finish notification taking much longer than the idle time you set — it no longer waits until you click away from the terminal, so it appears at the configured time.
- Fixed Visual Studio occasionally freezing while an agent-finish action ran (such as running the app).

### Version 10.76
- Added an optional notification when the agent finishes a task — play a sound and/or show a Visual Studio bar with how long it took (and, for Claude Code, how many tokens it used). It works by noticing when the terminal goes idle, so it covers any agent running in the Command Prompt terminal.
- The notification can also trigger an action when the agent is done: build or rebuild the solution, run it (with or without debugging), run your tests, run a script like deploy.cmd, or send a follow-up command back to the agent. Configure it under Settings, "On Agent Finish".
- Added an "@" file picker in the prompt box: type "@" to search your solution's files and folders and insert one without leaving the keyboard. Keep typing to filter, use the arrow keys and Enter (or click) to insert, and pick a folder to drill into it.

### Version 10.75
- Fixed the Claude Usage panel showing the claude.ai homepage and cookie banner instead of your usage, and not staying signed in across Visual Studio restarts.

### Version 10.74
- Added Antigravity to the marketplace tags so the extension is discoverable when searching for it.

### Version 10.72
- Fixed the Session History dialog showing "0 sessions found" when the project path contains non-English characters (e.g. Japanese) — the session list now loads correctly for these paths.

### Version 10.71
- Added a "Disable clipboard" option in the Settings dialog for users whose clipboard is held by another app (clipboard managers, Remote Desktop, security tools). When enabled, prompts are saved to a temporary file and a short reference is typed into the terminal with simulated keystrokes instead of being pasted.

### Version 10.70
- Fixed a system-wide keyboard and mouse freeze (and prompts occasionally landing in the wrong window) that could happen while sending a prompt when another app was contending for the clipboard. Input handling now runs independently of the editor, so it stays responsive during a send.

### Version 10.69
- Fixed the Update Agent button for Antigravity — it now exits the agent correctly before updating, so the installer runs instead of being typed into the running agent.

### Version 10.68
- Fixed Windsurf not launching when a new solution is opened while the terminal was already running — it would fall back to a plain command prompt until you manually restarted the agent. Windsurf now loads automatically like the other providers.

### Version 10.67
- Sending a prompt no longer aborts with a "Clipboard Verification Failed" pop-up when a clipboard manager or background app briefly holds the clipboard — the send now proceeds and a tolerant comparison ignores harmless line-ending differences. Strict abort behavior is still available via a new opt-in toggle in the Settings dialog.

### Version 10.66
- Clicking the embedded terminal now brings Visual Studio to the foreground even when another app is on top.

### Version 10.65
- Internal build fix for the editor context menu registration. No user-facing changes.

### Version 10.64
- XAML controls and their code-behind moved into a dedicated UI/ folder. No user-facing changes.

### Version 10.63
- Internal source tree reorganized into Controls/, ToolWindows/, Models/, and Package/ folders for easier navigation. No user-facing changes.

### Version 10.62
- Splitter between terminal and prompt can now be dragged fully to the top or bottom to hide either panel.

### Version 10.61
- Auto-reopened **Claude Usage** tab no longer steals focus on solution load.

### Version 10.60
- **Show Usage** menu item now displays a checkmark when the usage view is open.

### Version 10.59
- Consolidated layout, terminal type, theme, send behavior, auto-zoom, and auto-open changes into a single **Settings...** dialog in the ⚙ menu.
- Added an opt-out for the "Theme Changed" restart prompt for users who auto-switch themes when debugging.

### Version 10.58
- README cleanup: trimmed version history to user-visible features. Fixed outdated **Update Agent** button reference (now a menu item) and clarified installation source.

### Version 10.57
- README slim-down and shorter marketplace description.

### Version 10.56
- Inline usage bars now readable on light theme.

### Version 10.55
- Agent menu shows only Claude Code by default; new **Configure Visible Code Agents...** entry to opt in to the others.

### Version 10.54
- Antigravity: added **Skip Permissions** toggle.

### Version 10.53
- New AI provider: **Google Antigravity** (Gemini 3.5 Flash).

### Version 10.52
- New **Disable Auto Zoom on Startup** setting (useful on 4K / high-DPI displays).
- Faster terminal startup zoom.

### Version 10.51
- Usage page auto-confirms corporate proxy block screens.
- **Send large prompts as file** (opt-in) — avoids paste truncation on big prompts. PR #51, rbuss93.

### Version 10.50 - rbuss93 contribution
- Reliable prompt sends: chunked paste with clipboard verification prevents truncation and wrong-content sends.

### Version 10.49
- Claude Usage tab no longer steals focus during background refresh.

### Version 10.48 - CholmesFr contribution
- New AI provider: **PI Coding Agent**.

### Version 10.47
- No more duplicate "Theme Changed" dialogs; restart prompt skipped when new theme matches the agent's current color.

### Version 10.46
- New **Set Theme...** option to force Dark or Light theme regardless of VS theme.
- Fixed large prompt truncation.

### Version 10.44
- Inline usage bars no longer go stale when auto-refresh is off.

### Version 10.43
- Light theme support for Command Prompt.

### Version 10.42
- Toolbar declutter: 12 buttons reduced to 6 via grouped dropdowns.

### Version 10.41
- Mouse cursor stays visible while typing in the prompt area.

### Version 10.40
- Resilient clipboard handoff to terminal — retries longer and names the locking process on failure.

### Version 10.39 - Ocrosoft contribution
- UTF-8 codepage for the embedded Command Prompt — fixes garbled non-ASCII output.

### Version 10.38
- Usage page auto sign-out on **Change Account**.

### Version 10.37 - devStoner2024 contribution
- New **Switch Account** button in the Claude Usage tab for swapping between accounts and organizations.

### Version 10.36
- **Claude Code session history** — new 📜 button lists past sessions; resume any session or the most recent one with one click.
- Drag & drop file attachments onto the prompt area.

### Version 10.35
- Inline usage bars fixed after a claude.ai page layout change.

### Version 10.34
- Inline usage bar labelled **Weekly limit**.
- New **Extra usage** row when extra-usage billing is active.

### Version 10.33
- Auto-Refresh **Off** now stops all background bar refreshing.
- Usage window no longer steals focus on startup.

### Version 10.32
- **Send with Enter** toggle restored.

### Version 10.31
- Usage tab no longer blinks during background refresh.

### Version 10.30
- Usage bars persist after closing the usage tab.

### Version 10.29
- Inline usage bars update on load.
- Closing the usage tab with its X keeps inline bars updating.

### Version 10.28
- Shift+Enter and Ctrl+Enter reliably insert newlines.

### Version 10.27
- Fixed cursor disappearing and zoom landing on the wrong VS tab after startup.

### Version 10.26
- Fixed terminal zoom restore not applying on startup.

### Version 10.25
- Fixed terminal zoom restore landing on the Claude Usage tab.

### Version 10.24
- Claude Usage tab scrolls to top on refresh.

### Version 10.23
- Claude Usage tab: Ctrl+Scroll zoom with cursor fix.

### Version 10.22
- Fixed Claude Usage tab cursor disappearing and unwanted zoom on scroll.

### Version 10.21
- Fixed Claude Usage progress bars not showing fill.

### Version 10.20
- Claude Usage tab: shared login across VS instances.

### Version 10.19
- Fixed Claude Usage tab failing when multiple VS instances are open.

### Version 10.18
- Claude Usage tab UI polish.

### Version 10.17
- Enter always sends the prompt; Shift+Enter or Ctrl+Enter inserts a newline.
- Fixed Claude Usage progress bars on wide panels.

### Version 10.16
- **Claude Usage Limits in Visual Studio** — new 📊 button opens a dockable tab with claude.ai plan usage; inline session and weekly progress bars below the prompt.

### Version 10.15
- **Custom Commands** — configure reusable commands via the agent menu; the ⚡ toolbar button dispatches them to the active agent.

### Version 10.14
- Closing VS no longer closes unrelated Windows Terminal windows.

### Version 10.13
- Cut / Copy / Paste / Select All available in the prompt context menu.

### Version 10.12
- Qwen Code provider removed.
- More space for the prompt area.

### Version 10.11
- Cursor Agent: **Yolo Mode** toggle.
- Splitter boundary fix.

### Version 10.10
- **Install Caveman** plugin from the model menu.

### Version 10.8
- Automated marketplace publishing (no user-facing changes).

### Version 10.7
- Detects winget-installed Claude Code.
- Fixed `claude: command not found` in WSL.

### Version 10.6
- **Invert Layout** option in the settings menu.

### Version 10.5
- Fixed repeated WSL install popups.
- Fixed floating terminal window on slower machines.

### Version 10.4
- **Windsurf model selection** — Opus / Sonnet / Codex / Gemini Pro.
- Windsurf **Show Usage** menu item.

### Version 10.3
- **Windsurf (WSL)** provider added with full integration.

### Version 10.2
- Fixed CMake / Open Folder project directory detection.

### Version 10.1
- 📋 toolbar button inserts the editor selection into the prompt as a formatted snippet with file path and line numbers.

### Version 10.0
- Icon-based toolbar with compact emoji icons.
- Fixed detach icon on theme switch.

### Version 9.7
- Toolbar button color consistency across themes.

### Version 9.6
- File attachment chips moved to free up toolbar space.
- Removed the 5-file attachment limit — now unlimited.

### Version 9.5
- Fixed image / file not found by AI (consolidated temp folder).

### Version 9.4
- Fixed prompt paste failing when text was selected in the terminal.

### Version 9.3
- **Change Account** option in the Claude model menu.

### Version 9.2
- Terminal hidden from taskbar.
- Terminal layout refreshes on solution load.

### Version 9.1
- Added Windows Terminal install command to the "Not Found" dialog.

### Version 9.0
- F5 / Ctrl+F5 / Shift+F5 forwarded from the embedded terminal to Visual Studio debug commands.

### Version 8.9
- Extension icon shown on tool window tabs.

### Version 8.8
- Auto-focus detached terminal when the extension regains focus.

### Version 8.7
- Performance: non-blocking solution / project open and provider switching.
- Faster process termination on shutdown.

### Version 8.6
- Windows Terminal commands (model switch, effort, usage, language) fixed.
- Codex flag updated to `--ask-for-approval never`.
- Terminal lifecycle and layout stabilization.

### Version 8.5
- Fixed terminal zoom tracking.
- Fixed Windows Terminal paste and text selection.

### Version 8.4
- Detach Terminal: prompt area auto-expands on detach.
- Terminal and prompt zoom persistence across sessions.
- Detached state persistence fix.

### Version 8.3
- Detach Terminal: splitter stays visible; prompt font zoom (8–24pt).

### Version 8.2
- Multiple Detach Terminal fixes (fill tab, re-attach layout, double re-attach, prompt sending with diff open).

### Version 8.0
- **Detach Terminal** into a separate VS tool window tab; state persists.

### Version 7.8
- Fixed **Show Usage** for Windows Terminal.
- Adjusted Windows Terminal initial zoom.

### Version 7.7
- **Windows Terminal support** with seamless embedding, auto-detection, and install link.

### Version 7.6
- Fixed Show Usage menu navigation.

### Version 7.5 - adrian-schmidt contribution
- Set Working Directory dialog now follows VS theme.

### Version 7.4
- Prompt history now saves and restores file attachments.

### Version 7.3
- Special character rendering fixed (UTF-8 + Cascadia Mono).
- Diff viewer falls back to VS bundled git when `git` isn't on PATH.
- Clipboard contention retry logic.

### Version 7.2
- **Effort Level Selection** (Auto / Low / Medium / High / Max) in the model menu.
- **Show Usage** and **Set Language** menu items.

### Version 7.1
- **Codex: Full Auto** toggle.

### Version 7.0
- **Codex Windows native** support (previous Codex renamed "Codex (WSL)").

### Version 6.8
- Fixed "too many arguments" error when workspace path contains spaces.

### Version 6.7
- Updated documentation.

### Version 6.6 - fooberichu150 contribution
- Terminal embedding no longer requires Windows Console Host as the default terminal.
- Fixed terminal embedding on fresh VS launch / solution change.
- Workspace change now handles all providers correctly.

### Version 6.5
- **Claude Code: Skip Permissions** toggle.

### Version 6.4
- Double-click a diff code line to open the file at that line.

### Version 6.3
- Opus selection automatically opens the thinking mode selector.

### Version 6.2
- Fixed file attachment for non-standard file types.

### Version 6.1
- Major diff view performance fix for repositories with many changes.

### Version 6.0
- **Cursor Agent native Windows** support (previous renamed "Cursor Agent (WSL)").

### Version 5.9
- Improved WSL path conversion logic.

### Version 5.8
- Fixed WSL UNC path conversion bug.

### Version 5.7
- Diff view encoding fixes and auto-scroll improvements.

### Version 5.6
- Diff view performance improvements and search box.

### Version 5.5
- **Auto-open Changes on Send** option.
- Improved file change detection and auto-scroll behavior.

### Version 5.4
- Diff view available only for Git projects.

### Version 5.3
- Repository-wide diff tracking.
- **Auto-Scroll** for the diff view.
- Performance improvements for large repositories.

### Version 5.2
- Fix extension description.

### Version 5.1
- Diff tool performance optimizations.
- **Ctrl+Scroll** zoom on the diff view (50%–300%).
- More tracked file types (`.csproj`, `.sln`, etc.).

### Version 5.0
- **Integrated Diff Tool** — built-in diff view in a new tab.

### Version 4.2
- Clarified free-for-commercial-use license.
- Data privacy documentation.

### Version 4.1
- "Add Image" renamed to "Add File" with broad file type support.
- Multiple file attachment support.

### Version 4.0
- Fixed Excel cell paste (now pastes as text instead of an image).

### Version 3.8
- Fixed UI lag when typing in the prompt textbox.

### Version 3.7
- Performance improvements.
- Fix Sonnet model selection issues.

### Version 3.6
- **Open Code** support added.

### Version 3.5
- Fixes for supported providers.

### Version 3.4
- **ARM64 support** for Visual Studio.

### Version 3.3
- Clipboard preservation and restoration.

### Version 3.2
- **Claude Model Selection** dropdown (Opus / Sonnet / Haiku).

### Version 3.1
- Fixed instructions and about screens.

### Version 3.0
- **Qwen Code** support.

### Version 2.8
- Fix terminal hiding on tab switching.

### Version 2.7
- **Native Claude Code** support for Windows.

### Version 2.6
- Clickable image chips to open attached images.
- **VS 2026** support.
- Prompt history with Ctrl+Up / Ctrl+Down.

### Version 2.5
- Updated install instructions.

### Version 2.4
- Simplified window titles for Claude Code variants.

### Version 2.3
- Fixed WSL agent detection right after system boot.

### Version 2.2
- **Claude Code (WSL)** support.
- Unified exit logic across providers.

### Version 2.1
- Codex now runs in WSL.
- Improved Codex exit handling.

### Version 2.0
- **Update Agent** button added with per-provider update commands.

### Version 1.8
- Fixed terminal not opening when VS restarts with a solution loaded.

### Version 1.7
- Cleaner single-border UI.
- Terminal initializes on solution open.
- Improved solution switching.

### Version 1.6
- **Cursor Agent (WSL)** support with automatic detection and installation guide.

### Version 1.5
- Internal code reorganization (no functional changes).

### Version 1.4
- Fixed extension re-initialization when switching between windows.

### Version 1.3
- Automatic temp directory cleanup on startup.
- Simpler image naming.

### Version 1.2
- **OpenAI Codex** as a second AI assistant option.

### Version 1.1
- Theme support (follows VS light / dark).
- Helpful install instructions if Claude Code is not found.
- Fixed image pasting.

### Version 1.0
- Initial release: embedded AI assistant terminal in Visual Studio.

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

*Build with the help of Claude Opus 4.5, Claude Code, GPT-5, Codex, Qwen Code and Antigravity*
