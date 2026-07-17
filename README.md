# Claude Code Extension for Visual Studio

Embedded terminal inside Visual Studio for **Claude Code, OpenAI Codex, Cursor Agent, Open Code, Devin, PI, Google Antigravity, and Reasonix** — with multi-line prompts, file attachments, and an integrated diff viewer.

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
- **Auto-send build errors** — Optionally send build errors (with warnings for context) to the active agent automatically whenever a Visual Studio build finishes with errors, so it can fix them. Opt-in via *⚙ → Settings... → Behavior*.
- **Model selection** — 🤖 button to switch models: for Claude, Best / Opus / Sonnet / Haiku / Opus Plan plus an effort level (Auto / Low / Medium / High / Max) for Opus thinking depth; for Devin, a configurable list of models you can edit via *Configure Models...*; for Codex, Cursor Agent, PI, Antigravity, Reasonix, and Open Code, it opens the agent's own model picker in the terminal.
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
| Devin | Windows / WSL | `devin` | Devin account. Optional `--permission-mode dangerous` toggle. Native install from Windows Terminal: `irm https://static.devin.ai/cli/setup.ps1 \| iex` |
| PI | Windows | `pi` | Node.js + Git for Windows |
| Google Antigravity | Windows | `agy` | Google account. Optional `--dangerously-skip-permissions` toggle |
| Reasonix | Windows | `reasonix` | DeepSeek API key (`DEEPSEEK_API_KEY`). Install with `npm i -g reasonix` |

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
- Pick an AI provider, *Configure Visible Code Agents...*
- Provider-specific flags: Claude *Skip Permissions*, Codex *Approval Never*, Cursor *Yolo Mode*, Devin *Dangerous Mode*, Antigravity *Skip Permissions*
- *Configure Custom Commands...*, *Settings...*, About
- *Settings...* opens the consolidated dialog with tabs for Behavior (send key, large prompts, auto-open Changes, auto-send build errors, font size), Layout (prompt panel position), Terminal type, Theme, Usage, Toolbar, and CLI Paths

**☰ Tools dropdown**: Holds *Update Code Agent*, *Restart Code Agent*, *Detach/Attach Terminal*, *View Changes*, *Session History*, *Show Usage*, and *Set Working Directory...*. Promote any of these to one-click toolbar buttons — and reorder them by dragging — via *⚙ → Settings... → Toolbar*; promoted features leave the dropdown, which hides once they all become buttons.

**🤖 Model menu**: For Claude — Opus / Sonnet / Haiku, effort level for Opus (Auto / Low / Medium / High / Max), Change Account, Install Caveman plugin. For Devin — pick from a configurable model list, edited via *Configure Models...*.

**On Agent Finish**: Configure via *⚙ → Settings... → On Agent Finish...*. For scripts, enable *Close script window when it finishes* to auto-close the script console. For *Run (F5)* and *Run without debugging (Ctrl+F5)*, use *Clean solution before running* and *Rebuild solution before running* to control whether the solution is prepared before launch.

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

## Known Issues

- I know it is a pain, but sometimes in plan mode when there is an AI question after a lot of text, the keyboard does not work to select answer.
After some time it will work again. Very hard to fix issue even with advanced models like Fable 5. Probably related to Claude Code itself.
https://github.com/anthropics/claude-code/issues/63504
https://github.com/anthropics/claude-code/issues/41501

## Version History

### Version 67.0
- Fix issues.

### Version 66.0
- **More reliable sending for Devin**: fixed an intermittent issue where the prompt would appear typed into Devin's session but Enter wouldn't submit it. The terminal now waits a bit longer for Devin's interface to catch up before pressing Enter.

### Version 65.0
- **Auto-send build errors to the agent**: an opt-in setting that, whenever a Visual Studio build finishes with errors, automatically sends the errors (plus warnings for context) to the active agent so it can fix them. Enable it via *⚙ → Settings... → Behavior*.

### Version 64.0
- The Settings console font list now shows only monospaced fonts, preventing the jumbled terminal text that appears when a proportional font is picked (issue #105). A "Show all fonts" option is available for anyone who needs a font the filter leaves out, and the picker warns whenever the selected font is not monospaced.

### Version 63.0
- The extension now detects when Windows "legacy console" mode is enabled, which prevented the terminal from ever attaching to the panel, and offers to fix it with one click (no administrator rights needed) followed by a Visual Studio restart (issue #104).

### Version 62.0
- The tool window title no longer shows a model name for Claude Code and Devin — it now shows just the agent name, since the model can be changed from inside the terminal at any time.
- The model menu no longer marks any model as selected for Claude Code and Devin; picking one still switches the model in the running agent.

### Version 61.0
- The model (🤖) button now also appears for Codex, Cursor Agent, PI, Antigravity, Reasonix, and Open Code — clicking it opens the agent's own model picker so you can switch models without leaving the terminal.

### Version 60.0
- Fixed the model menu showing Claude-only options **Best** and **Opus Plan** while Devin was the active agent — these picks now appear for Claude Code only.

### Version 59.0
- Improved the long-standing "keyboard stops working when the agent asks a question" problem after very long answers (e.g. plan mode) — the extension no longer bombards the busy agent with focus changes and console reads that amplified the lock-ups (issue #89).
- When the agent's terminal is genuinely frozen catching up on a huge response, a notification now explains that typing and clicks will recover by themselves in a moment, instead of leaving the keyboard silently dead. Note: the remaining pause after very long answers comes from the Claude Code CLI itself and clears on its own.

### Version 58.0
- Fixed the **@ file/folder picker** cutting off long file paths with no way to see the rest — the popup now scrolls horizontally and shows the full path in a tooltip on hover (issue #103).

### Version 57.0
- Fixed the hidden prompt input box leaving a blank gap where the box used to be after switching to another window (like Solution Explorer) and back (issue #101). Hiding the box also no longer overwrites the saved prompt/terminal split size, so it comes back at its previous height when re-enabled.

### Version 56.0
- **Terminal-only mode**: hide the prompt input box to give the terminal more vertical space, while keeping the Send/Attach, Restart, Model, and ⚙ buttons reachable to turn it back on. Toggle from *⚙ → Hide Prompt Input Box* or *⚙ → Settings → Layout* (issue #101).

### Version 55.0
- Fixed **On Agent Finish** firing prematurely while Claude Code was still waiting on its own background agents to finish — the completion notification now holds until the whole turn, background agents included, is actually done.

### Version 54.0
- Fixed **Claude Usage** showing outdated numbers and no longer trimming to just the usage bars after a recent Claude.ai layout change — the session and weekly percentages now read correctly again, and the view works regardless of your account's display language.

### Version 53.0
- **Working directory** can now be set per solution — switching between solutions no longer carries over a custom working directory from a different one (issue #100).
- Fixed selecting and copying text from the agent's replies under **Windows Terminal** — the selection assist now engages instantly instead of racing a busy Visual Studio, and right-click now copies the selection and pastes when nothing is selected, matching Command Prompt (issue #99).
- Removed the **TUI Fullscreen** menu option — Claude Code now always launches in its classic (non-fullscreen) terminal renderer.

### Version 52.0
- **On Agent Finish** can now play a distinct sound when the agent stops and waits for your answer (a yes/no or selection prompt) instead of finishing — so you can tell "the agent needs me" apart from "the agent is done" without looking. Opt-in from the **On Agent Finish** settings.

### Version 51.0
- Fixed **Ctrl+Scroll** (zoom) not working in other apps while the extension's terminal panel occupied the same screen area — the extension no longer hijacks the gesture unless Visual Studio is actually the foreground window.

### Version 50.0
- Fixed the **On Agent Finish** dialog showing an unwanted scrollbar after the new follow-up field was added.
- Fixed **On Agent Finish** sometimes watching the wrong terminal's console under **Windows Terminal** when more than one agent terminal was running at once, so completion detection now reliably tracks the terminal you're actually using.
- The **On Agent Finish** follow-up field now has a **Preset** picker with ready-to-use texts, including one that asks the agent to commit and push while keeping only the human author and no AI references in the commit.
- Notification buttons for "send to agent" actions and follow-ups now show much more of the command text instead of cutting it off after a couple of words.

### Version 49.0
- New installs now come with a **Commit & Push** custom command pre-added to the toolbar dropdown, ready to send to the agent.
- **On Agent Finish** actions can now have a follow-up: after the action succeeds (e.g. Build solution), optionally also send a message to the agent (e.g. "Commit and push the changes"). When "Ask before running the action" is on, the follow-up gets its own confirmation notification instead of running silently.

### Version 48.0
- **Session History** now has a **View** button (also on the right-click menu) that opens the selected conversation as readable text in your default editor, so you can skim a past session before resuming it.

### Version 47.0
- Updating the **PI** or **Antigravity** CLI from the model menu now exits the agent with its `/quit` command before running the update, for a cleaner and more reliable handoff.
- **Session History** now finds your transcripts even when `CLAUDE_CONFIG_DIR` is pointed directly at the `projects` folder instead of the `.claude` root.

### Version 46.0
- The Claude Code model menu now offers two more picks alongside Opus/Sonnet/Haiku: **Best** (Fable 5 where available, otherwise the latest Opus) and **Opus Plan** (Opus while planning, Sonnet during execution).
- Fixed the keyboard occasionally not reaching the terminal when the agent asks a question — clicking into the terminal now reliably keeps focus there while you type your answer, even if you pause before replying.
- **On Agent Finish** no longer stays silent when the agent ends its turn with a question in its reply (e.g. "Do you want me to also update the tests?") — the finish notification now fires for those turns instead of mistaking the finished reply for a prompt still waiting on you.

### Version 45.0
- **Session History** now honors a relocated Claude Code data folder — if you set the `CLAUDE_CONFIG_DIR` environment variable (e.g. to another drive), it lists and resumes sessions from there instead of the default `~/.claude` location.

### Version 44.0
- Added a **Console font** picker (*⚙ → Settings... → Terminal*) — search your installed fonts with a live preview and choose one that renders your language's characters (Chinese, Japanese, Korean, etc.) instead of the default Cascadia Mono. Applies to both Command Prompt and Windows Terminal.

### Version 43.0
- The **Max** and **Ultracode** effort levels now apply to the current session only and are no longer remembered between Visual Studio restarts — the next launch starts from your last durable level (Low, Medium, High, or Extra High), matching how Claude Code treats these levels.

### Version 42.0
- The **Effort** setting for **Claude Code** (native and WSL) is now a slider in the model (🤖) menu — drag between Low, Medium, High, Extra High, Max, and Ultracode instead of picking from a list, with the current level shown above it. Ultracode needs dynamic workflows enabled (see `/config`).

### Version 41.0
- **On Agent Finish** now works reliably with **Windows Terminal**, including full-screen agents like **Devin** — it reads the terminal's console buffer directly instead of relying on the previous accessibility-based reading, which often never detected completion. Enable it as usual via *⚙ → Settings... → On Agent Finish...*.

### Version 40.0
- Fixed the **Devin** model menu showing the model list repeated many times — the saved model list grew on every Visual Studio start. Existing duplicated lists are cleaned up automatically.
- Fixed **View Changes**, **Session History**, and **Show Usage** disappearing entirely (from both the ☰ Tools menu and the toolbar) when they didn't apply to the current agent or folder — they are now always available, and clicking one when it isn't applicable explains why (View Changes needs a Git repository; Session History is Claude Code only; Show Usage is Claude Code and Devin only) (issue #97).

### Version 39.0
- Fixed **Update Code Agent** for **Devin (native)** — it now actually updates the CLI to the latest version. The previous `devin update` command only printed instructions, and the installer was blocked while Devin was still running ("Access is denied"); the update now stops any running Devin first so it completes.
- **On Agent Finish** is now available with **Windows Terminal** (experimental) — previously it worked only with Command Prompt. Enable it as usual via *⚙ → Settings... → On Agent Finish...*; a note in Settings flags the Windows Terminal support as experimental.

### Version 38.0
- Added **Devin** as a native Windows AI agent — the Windows-native Devin CLI alongside the existing Devin (WSL). Install it from Windows Terminal with `irm https://static.devin.ai/cli/setup.ps1 | iex`, then enable it from the agent menu via "Configure Visible Code Agents...". Once installed it runs in both Windows Terminal and the regular Command Prompt, it honors the Dangerous Mode toggle, and Show Usage opens the Devin usage page.
- **Configurable Devin models** — the model (🤖) menu now lets you choose from a user-editable list of Devin models, and a new "Configure Models..." entry lets you add, edit, remove, and reorder them. Seeded with SWE-1.6, Claude Opus 4.6 Thinking, Claude Opus 4.8 High, GPT-5.5 High Thinking, and Gemini 3.1 Pro High Thinking. Both Devin (native) and Devin (WSL) share the list.
- Renamed the former "Windsurf" provider to **Devin** throughout the extension to match the CLI's branding; the WSL provider is now listed as "Devin (WSL)". Existing selections continue to work.

### Version 37.0
- Fixed mouse-wheel scrolling in Claude Code's "TUI Fullscreen" mode scrolling the agent even when another window (such as Notepad) was layered over the terminal area — the wheel now only scrolls the agent while the terminal is focused.

### Version 36.0
- Restored mouse-wheel scrolling in Claude Code's "TUI Fullscreen" mode — scrolling the wheel over the terminal now moves smoothly through the conversation one line at a time, instead of doing nothing. For native Claude Code, Page Up/Page Down are set to scroll one line and Shift+Page Up/Page Down keep the half-page jump (issue #96).

### Version 35.0
- Added the ability to rename past sessions in the Session History window — right-click a session (or select it and press F2), or use the new "Rename" button, to give it a custom title. Renamed sessions stand out with their own highlighted layout, and custom titles persist across Visual Studio restarts; clear the title to restore the auto-generated preview (issue #95).
- Added a filter box and a "Renamed only" toggle to the Session History window so you can quickly find a session by title, preview text, or date. The "Renamed only" choice is remembered across restarts, and the window layout was tidied up for less clutter.

### Version 34.0
- Fixed "On Agent Finish" sometimes notifying that the agent had finished while it was still working, after clicking inside the terminal — selecting or clicking text in the Command Prompt freezes the screen, which was misread as the agent going idle (issue #94).
- Raised the default idle time before a turn is detected as finished from 3 to 5 seconds.

### Version 33.0
- Added Reasonix (DeepSeek-native coding agent) as a supported AI agent. Install it with `npm i -g reasonix`, then enable it from the agent menu via "Configure Visible Code Agents..." (issue #93).

### Version 32.0
- Fixed the agent terminal being flooded with "[Pasted text +N lines]" blocks on startup and on every setting change when "TUI Fullscreen" was enabled — fullscreen rendering now keeps its flicker-free drawing without taking over the mouse, which was the source of the flood (issue #92).

### Version 31.0
- Added a "TUI Fullscreen" option to the Claude Code model menu to switch the agent between fullscreen (flicker-free) and classic terminal rendering on demand.

### Version 30.0
- Fixed the agent terminal gradually stopping accepting keyboard input while "On Agent Finish" was enabled with the Command Prompt terminal — the background check that watches for the agent finishing no longer bounces keyboard focus out of the terminal or prompt, so typing keeps landing throughout a turn.

### Version 29.0
- Fixed right-clicking elsewhere in Visual Studio sometimes failing to show the context menu and instead pasting the clipboard into the agent terminal (issue #90). The extension no longer intercepts the right mouse button at all — paste into the agent through the prompt box (Ctrl+V, then Send), which works the same in every terminal mode.
- Fixed Visual Studio hanging when an agent finished while the Settings dialog was open with "On Agent Finish" enabled — the finish notification and action now wait until the dialog is closed.

### Version 28.0
- Fixed keyboard and mouse input dropping out in the agent terminal while the agent was working — typing, arrow keys, and clicks stopped landing for a while and then recovered on their own. Terminal focus no longer merges an unrelated window's input queue, so input stays responsive even while the agent is busy.
- Clicking into the terminal or the prompt box no longer pulls focus back and forth for several seconds afterward, so focus settles immediately on whichever one you clicked.

### Version 27.0
- NOTE: this is a big release, for both trying to fix the terrible input issue in console and new features.
- Please be patience and report any issues you find! Also if you don't like the extension, please just do not use and do not give bad review.
- Features/Fixes:
- You can now promote frequently used features — Update Code Agent, Detach/Attach Terminal, Restart Code Agent, View Changes, Session History, Show Usage, and Set Working Directory — to one-click toolbar buttons from the new "Toolbar" tab in Settings, where you can also reorder them; features you don't promote stay in a compact Tools (☰) dropdown that hides when empty.
- Fixed "On Agent Finish" sometimes never firing when the agent's final answer ended with a numbered list — such answers are no longer mistaken for a waiting prompt.
- Clicking into the prompt box now reliably keeps keyboard focus, so typing lands without having to click several times; clicking the terminal also holds focus longer while the agent is working.

### Version 26.0
- Fixed the embedded terminal sometimes becoming visible but not accepting typing, arrow keys, or pasted prompts after clicking it — terminal focus is now restored reliably for Command Prompt and Windows Terminal (issue #86).

### Version 25.0
- When "On Agent Finish" skips its action because no files changed, it now stays silent — no notification or sound — instead of announcing the skipped turn.

### Version 24.0
- Fixed the agent picker, tool-window title, model menu, and usage controls showing a stale provider after another agent was already running.
- Fixed WSL provider launches for workspaces with special characters in the path and made custom WSL executable paths safer.
- Fixed Code Changes diffs for filenames with spaces and Git renames/copies, and prevented same-named attachments from overwriting each other.
- Protected shared Claude usage cookies on disk and prevented the Claude usage panel from accepting messages from non-Claude pages.

### Version 23.0
- Fixed Visual Studio hanging intermittently and right-click context menus failing to appear throughout the IDE while the extension was enabled — the embedded terminal's right-click paste no longer blocks or swallows right-clicks outside the terminal.
- Fixed right-clicking in one Visual Studio instance pasting into another instance's terminal when two instances run side by side and overlap on screen (such as during F5 debugging) — each instance now only responds to right-clicks on its own terminal when that terminal is actually the window on top.
- Added an opt-in **Send selection as reference only** setting — when enabled, *Send Selection* inserts just the file path and line numbers instead of the selected code, letting the AI agent read the file directly (Settings → Behavior). Thanks to [@iwiwb](https://github.com/iwiwb) for the contribution (issue #84).

### Version 22.0
- Fixed prompts being flooded with thousands of duplicated "[Pasted text]" blocks or repeated characters on every send — sending now pastes the real text through the terminal's own paste instead of typing it character by character, which the agent could turn into a runaway loop (issue #83).
- Fixed the PI agent: sending a prompt no longer fills the terminal with garbage and crashes it (Command Prompt), and no longer freezes Visual Studio for about a minute before the text appears (Windows Terminal) (issue #82).
- Fixed a prompt sometimes landing in the wrong window's terminal when two Visual Studio instances are open at once — the prompt now always goes to the terminal of the instance you sent it from.

### Version 21.0
- Non-English text (such as Chinese, Japanese, or Korean) typed in the prompt box now reaches the terminal correctly instead of arriving as garbled characters (issue #79).
- Agent output in the terminal is now readable under a light Visual Studio theme — accent colors like cyan and blue are painted in darker, legible tones instead of washing out against the light background (issue #80).

### Version 20.0
- Terminal zoom (Ctrl+Scroll) and right-click paste now keep working after the agent's interface is fully up, not just during startup — previously both stopped responding once the agent took over the terminal (issue #78).

### Version 19.0
- Terminal zoom (Ctrl+Scroll) and paste now keep working when signed in with a custom API key — previously some sessions left the mouse zoom and right-click paste unresponsive, and the extension now falls back automatically so both behave the same as a normal sign-in (issue #76).

### Version 18.0
- Replying to the agent's questions with the arrow keys is now reliable when "On Agent Finish" is enabled — while the agent waits for your answer the completion watcher leaves the focused terminal alone instead of fighting you for keyboard focus, so you no longer have to click the terminal repeatedly before a keystroke registers.

### Version 17.0
- Removed the Fable option from the Claude model menu — choose Opus, Sonnet, or Haiku.

### Version 16.0
- Changes to "On Agent Finish" settings now take effect for a turn that is already running — the new settings are applied when the agent finishes, instead of only on your next prompt.

### Version 15.0
- Arrow keys now work reliably while navigating the agent's question and selection menus in plan mode when "On Agent Finish" is enabled — the completion watcher recognizes the menu sooner and stays backed off as you move between options, instead of eating keystrokes.

### Version 14.0
- Fixed "Restart code agent" leaving the panel blank after an "On Agent Finish" notification had fired — previously the panel could stay broken until Visual Studio was reopened (issue #73).
- Arrow keys and typed answers now work reliably when replying to the agent's questions in the console while "On Agent Finish" is enabled — the completion watcher now backs off while the agent waits for your reply.

### Version 13.0
- "On Agent Finish" scripts can now close their console window automatically when they finish.
- "On Agent Finish" Run and Run without debugging actions can now clean and rebuild the solution before launching, and those preferences are saved.

### Version 12.0
- Loading or switching solutions now avoids repeated terminal attach attempts and no longer keeps retrying the same failed launch, reducing blank terminal panels after a new solution opens.

### Version 11.0
- Fixed "Restart code agent" leaving the panel blank on machines where the previous agent session shuts down slowly (issue #73) — the restart now waits for the old session to fully terminate for every provider before launching the new one, instead of only for WSL.
- Clicking the agent terminal now reliably focuses it even when the machine is busy (issue #74) — previously the focus could be silently taken back by Visual Studio right after the click, making the terminal impossible to select while the agent was working hard.

### Version 10.99
- Fixed the agent terminal staying stuck on a previously chosen custom background color after switching the theme back to Automatic, Dark, or Light — the terminal now always matches the selected theme.

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
- Fixed Devin not launching when a new solution is opened while the terminal was already running — it would fall back to a plain command prompt until you manually restarted the agent. Devin now loads automatically like the other providers.

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
- **Devin model selection** — Opus / Sonnet / Codex / Gemini Pro.
- Devin **Show Usage** menu item.

### Version 10.3
- **Devin (WSL)** provider added with full integration.

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
  - [Devin/Cognition](https://cognition.com/legal/privacy-policy)
  - [PI](https://pi.dev/)
  - [Google Antigravity](https://policies.google.com/privacy)
  - [Reasonix](https://reasonix.io/)
- **No Third-Party Access**: Data is only accessible to the configured model provider

### Contact
For licensing inquiries or permission requests, please contact the author at dliedke@gmail.com.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Build 100% Vibe Coding with Claude Opus/Sonnet, Claude Code, GPT, Codex, Qwen Code and Antigravity*
