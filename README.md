# Claude Code Extension for Visual Studio

Please also check [Claude Code Studio from wluisdev](https://marketplace.visualstudio.com/items?itemName=wluisdev.ClaudeCodeStudio)

Native chat or terminal inside Visual Studio for the best AI coding agents on the market: **Claude Code, OpenAI Codex, Cursor Agent, Open Code, Devin, PI, Google Antigravity, Reasonix, Qwen Code, and Grok** — with custom actions, automations when AI finish work, github issues fix automations, integrated diff viewer and much more.

<center>
<img src="https://raw.githubusercontent.com/dliedke/ClaudeCodeExtension/master/docs/images/extension-screenshot.png" alt="Claude Code Extension Screenshot" width=900 />
</center>

Enjoying the extension? [Buy me a coffee](https://www.buymeacoffee.com/dliedke) — every cup helps keep it free. Companies can use the extension for free too. Bug reports, suggestions, and pull requests are welcome on [GitHub](https://github.com/dliedke/ClaudeCodeExtension).

[Mentioned in Awesome Codex CLI](https://github.com/RoggeOhta/awesome-codex-cli)

## Features

- **Native mode (default for new installs)** — Show the conversation as a chat in its own document tab instead of an embedded terminal: answers arrive as formatted text, file edits open into a colored diff, and each turn ends with how long it took, what it cost, and (for Claude Code) your status line if you have one. The tab has its own message box with image paste and file attachments, plus agent, model, effort and permission selectors you can change mid-conversation. Turn off via *⚙ → Settings... → Terminal* to use the embedded terminal instead; Reasonix and any agent that can't run this way keep using the terminal.

- **Embedded AI terminal** — Run any supported AI coding agent inside a Visual Studio tool window. Auto-detects the solution directory; restarts when you switch solutions. Optionally use Windows Terminal instead of Command Prompt for better emoji/Unicode rendering.

- **Multi-line prompts** — Press **Enter** to send, **Shift+Enter** or **Ctrl+Enter** for a new line. To send with Ctrl+Enter instead, or to use a Send button, change the send key in *⚙ → Settings... → Prompt*.

- **File and image attachments** — Paste images with **Ctrl+V**, drag & drop files onto the prompt area, or use the 📎 menu (attach a file, insert the editor selection or the active file's path). Any file type is accepted (no limit). Text content like Excel cells pastes as text, not as an image.

- **Editor selection → prompt** — Click 📋 or right-click selected code → *Send Selection to Claude Code* to insert a formatted snippet (file path + line numbers + syntax-highlighted code fence) into the prompt. Turn on *Send selection as reference only* in *⚙ → Settings... → Prompt* to insert just the file path and line numbers.

- **Integrated diff viewer** — For Git projects, the 📊 view shows uncommitted changes in a dedicated tab with search, double-click-to-open, and double-click-line-to-navigate. Optionally auto-opens when you send a prompt, and can track agent changes for per-file Keep/Undo review.

- **Prompt history** — Last 50 prompts saved (with attached files). Browse with **Ctrl+Up / Ctrl+Down**; clear via right-click.

- **Claude Code, Codex and Devin session history** — 📜 toolbar button lists past sessions for the current workspace; view, rename, delete, or resume any session, including the most recent one. Works on Windows and WSL.

- **Claude usage in VS** — 📊 button (when Claude is active) opens the claude.ai usage page inside a dockable tab. Inline session/weekly progress bars below the prompt update automatically and adapt to the active theme.

- **Custom commands (⚡)** — Save slash commands or canned prompts and dispatch them to the active agent in one click. Configure via *⚙ → Configure Custom Commands...*.

- **"@" file picker** — Type **@** in the prompt box (or the native mode chat composer) to search your solution's files and folders and insert one with the keyboard; keep typing to filter, arrow keys + Enter to insert, pick a folder to drill in. File types and skipped folders are configurable in Settings → Prompt.

- **"/" command picker** — Claude Code only: type **/** at the start of a prompt (in the prompt box or the native chat composer) to list the built-in commands, your custom commands and skills, with their descriptions.

- **On Agent Finish** — Optionally play a sound, show a notification (with duration, plus token count for Claude Code), and run an action (build/rebuild, run, tests, a script, or a follow-up command) when the agent goes idle. Configured per solution. Configure via *⚙ → Settings...*.

- **Pull before sending** — Runs `git pull` in the solution’s repository before your first prompt, so the agent never edits a file that is already out of date on the remote. Conflicts from that pull are handed to the agent to resolve. On by default; turn it off via *⚙ → Settings... → Automation*.

- **TFVC checkout** — In TFVC-bound solutions, Claude Code in native mode has read-only files checked out through Visual Studio before it edits them. On by default; turn it off via *⚙ → Settings... → Automation*.

- **Auto-send build and runtime errors** — Optionally send build errors (with warnings for context) to the active agent automatically whenever a Visual Studio build finishes with errors, so it can fix them. While debugging, unhandled exceptions can be sent the same way, with their stack trace. Opt-in via *⚙ → Settings... → Automation*.

- **Generate Commit Message** — Toolbar/menu action that asks the active agent to write a commit message from the current changes and fills it into the Git Changes window. Requires native mode; falls back to the clipboard if the commit message box can't be found. A sibling action, **Generate Commit Message, Commit and Push**, does the same then immediately stages, commits and pushes all changes.

- **Recommend AI Model** — Claude Code only: asks Opus (Extra High) which model and effort suit the prompt you typed, then lets you adjust and apply them.

- **GitHub Issues** — Lists the repository's GitHub issues and fixes each one in its own branch, worktree and chat tab, then commits, merges and replies on the issue. Requires native mode; uses the GitHub CLI (`gh`) for private repositories, replies and closing.

- **Model selection** — 🤖 button to switch models: for Claude, Fable / Opus / Sonnet / Haiku / Opus Plan plus an effort level; for Codex, its reported models plus a reasoning level (Model default / Low / Medium / High / Extra High / Max / Ultra); for Devin, a searchable picker with favorites and per-model prices; for every other agent, the models it reports itself, with *Refresh Models* to re-read them and *Choose in the Agent...* to fall back to its own picker.

- **Detach / attach terminal** — Pop the terminal into a separate VS tab and bring it back at any time. State persists across sessions.

- **Theme aware** — Follows VS dark/light theme automatically, or force dark, light, or a custom background color via *⚙ → Settings → Theme*. Prompt zoom is persisted across sessions, and native chat zoom (Ctrl+Scroll, Ctrl+0 resets it) is too; set the terminal's console font and size via *⚙ → Settings → Terminal* (Ctrl+Scroll zoom applies for the current session).

- **20 languages support**: English, Simplified Chinese, Japanese, Portuguese (Brazil), Spanish, Korean, German, French, Russian, Traditional Chinese, Indonesian, Turkish, Vietnamese, Polish, Italian, Ukrainian, Arabic, Hindi, Thai and Dutch. Pick one under *⚙ → Settings → Layout → Chat language*, or leave it on Automatic to follow the Visual Studio language.

- **Persistent settings** — Layout, provider choice, model, flags, and font sizes all saved to `%LocalAppData%\ClaudeCodeExtension\claudecode-settings.json`.


## Native Mode

The default chat experience for new installs — turn it off via *⚙ → Settings... → Terminal → Use native mode* to go back to the embedded terminal. Lost the chat tab? *💬 Show Chat* in the panel or *View → Other Windows → Claude Code Chat* brings it back.

<!-- Images are served from this repository's raw URLs, which the VS Marketplace can also reach — the
     overview is rendered from this file with no repo context, so relative paths would not resolve there.
     The links go live once docs/images/ is pushed to master. -->

**Questions answered in the chat** — the agent's multiple-choice questions become clickable cards, each with a free-text answer of your own.

<img src="https://raw.githubusercontent.com/dliedke/ClaudeCodeExtension/master/docs/images/native-mode-question.png" alt="Native mode: answering an agent question in the chat" width="900" />

**Plan review** — in plan mode the agent proposes a plan first; approve it (optionally on a different model) or send it back for changes without leaving the transcript.

<img src="https://raw.githubusercontent.com/dliedke/ClaudeCodeExtension/master/docs/images/native-mode-plan.png" alt="Native mode: reviewing and approving a plan" width="900" />

**Real diffs for file edits** — every edit opens into a colored diff with a line count, so you can see what changed without switching tabs.

<img src="https://raw.githubusercontent.com/dliedke/ClaudeCodeExtension/master/docs/images/native-mode-diff.png" alt="Native mode: a file edit shown as a colored diff" width="900" />

**Parallel chats and session buttons** — ✚ opens another chat tab with its own agent, model and permission selectors; ✎ renames the session, 🎨 changes its color and ↻ clears the conversation.

**Permission prompts and tool cards** — the caret beside Allow offers *Allow for the rest of this session*, and a tool card's ↗ icon opens the file it touched in the editor.

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
| Qwen Code | Windows | `qwen` | Node.js 22+; Coding Plan or API key via `/auth`. Optional `--yolo` toggle |
| Grok | Windows | `grok` | grok.com account (`grok login`). Optional `--always-approve` toggle |

If a provider isn't installed, the extension shows the install command automatically when you select it. The **Update Code Agent** entry in the ☰ Tools menu runs the right update command for the active provider (e.g. `claude update`, `npm install -g @openai/codex@latest`, `cursor-agent update`).

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
Then choose it via *⚙ → Settings... → Terminal*.

## Quick Start

1. Click ⚙ → pick your AI provider (use *Configure Visible Code Agents...* if it isn't listed)
2. If using Open Code, run `Ctrl+P` → "connect providers" once to authenticate
3. Pick a model via the 🤖 button
4. Type a prompt, press **Enter** to send. Attach files with Ctrl+V, drag-and-drop, or 📎
5. Watch the agent work in the embedded terminal. For Git projects, open 📊 to see live diffs

## Settings & Menus

**⚙ Settings menu** (gear button, top-right):
- Pick an AI provider, *Configure Visible Code Agents...*
- Provider-specific flags: Claude *Skip Permissions* and *Start in Plan Mode*, Codex *Approval Never*, Cursor *Yolo Mode*, Devin *Dangerous Mode*, Antigravity *Skip Permissions*, Qwen Code *Yolo Mode*, Grok *Always Approve*
- *Hide Prompt Input Box*, *Configure Custom Commands...*, *Settings...*, About
- *Settings...* opens the consolidated dialog with tabs for On Agent Finish, Prompt (send key, large prompts, clipboard, selection as reference, last prompt, font size, @ file picker), Automation (auto-open Changes, track agent changes for review, pull before sending, TFVC checkout, auto-send build and runtime errors, Install Caveman, Install latest OpenPets), Layout (prompt panel position, prompt box, usage bars, subagent count), Terminal (native mode, terminal type, encoding, fonts), Theme, Toolbar, CLI Paths (including extra launch arguments), and Backup (save/load all settings to a file)

**☰ Tools dropdown**: Holds *Update Code Agent*, *Restart Code Agent*, *Detach/Attach Terminal*, *View Code Changes*, *Session History*, *Show Usage*, *Set Working Directory...*, *Send Build Errors to Agent*, *Generate Commit Message*, *Generate Commit Message, Commit and Push*, *Recommend AI Model*, and *GitHub Issues*. Promote any of these to one-click toolbar buttons — and reorder them by dragging — via *⚙ → Settings... → Toolbar*; promoted features leave the dropdown, which hides once they all become buttons.

**🤖 Model menu**: For Claude — Fable / Opus / Sonnet / Haiku / Opus Plan, effort level (Low / Medium / High / Extra High / Max / Ultracode), Change Account, Set Language. For every other agent — its own models (grouped into submenus when the list is long), *Refresh Models*, and *Choose in the Agent...*.

**On Agent Finish**: Configure via *⚙ → Settings... → On Agent Finish* tab. For scripts, enable *Close script window when it finishes* to auto-close the script console. For *Run (F5)* and *Run without debugging (Ctrl+F5)*, use *Clean solution before running* and *Rebuild solution before running* to control whether the solution is prepared before launch.

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

## Contributing

Pull requests for new features are welcome. Bug fixes too — if you find an issue, feel free to fix it and submit a PR.

- Use a high-quality AI model for coding, such as Claude Opus with High effort.
- Test your changes carefully in Visual Studio (F5 experimental instance).
- Run all unit tests with `test.cmd` and make sure they pass before submitting.
- Bump the version by one major number (e.g. 236.0 → 237.0) in `Properties/AssemblyInfo.cs` and `source.extension.vsixmanifest`, and add a short `### Version X.0` entry at the top of Version History in this README. See `CLAUDE.md` for details.
- The extension's original developer reviews the PR and publishes the new version to the Marketplace.

## Version History

### Version 272.0
- Native chat text is left-aligned again instead of justified.
- Code blocks in the native chat can be selected and copied in part again, and starting a selection in one no longer jumps the conversation.
- Numbered lists in the native chat show their full numbers from 10 up instead of cutting off the first digit.
- A numbered list that starts at a number other than 1 keeps its numbering.
- Expanded tool calls show their command and output unwrapped again, with sideways scrolling for long lines.
- Zooming the native chat in with Ctrl+Scroll no longer pushes your messages and tool calls past the right edge.
- The coloured bar of a tool call no longer pokes out of its rounded frame, and the expand arrow sits centred whether the row is open or closed. Thanks to [@Gh61](https://github.com/Gh61) (#225).

### Version 271.0
- Fixed native chat text (replies, your messages, tool call input and output) showing in a serif font instead of the chat font chosen in Settings. Thanks to [@Gh61](https://github.com/Gh61) (#224).

### Version 270.0
- The language setting now translates the whole extension, not just the chat: panel, menus, toolbar, Settings and other dialogs, messages, and the Code Changes and Claude Usage windows (*Settings → Layout → Language*).
- The language switches in real time: picking one in Settings changes the whole extension immediately, the Settings window included, and everything already open follows; Cancel puts the previous language back.
- All 20 languages now cover the full extension.

### Version 269.0
- The chat tab is now available in 20 languages: English, Simplified Chinese, Japanese, Portuguese (Brazil), Spanish, Korean, German, French, Russian, Traditional Chinese, Indonesian, Turkish, Vietnamese, Polish, Italian, Ukrainian, Arabic, Hindi, Thai and Dutch. Pick one under *Settings → Layout → Chat language*, or leave it on Automatic to follow the Visual Studio language.
- Changing the chat language now takes effect at once in every open chat, without restarting Visual Studio; messages already in the conversation keep the language they were written in.

### Version 268.0
- The native chat tab can now be shown in your language: Italian is included, and by default the chat follows the Visual Studio language (*Settings → Layout → Chat language*). Other languages can be added by dropping a translation file in the extension's `lang` folder under Local AppData. Thanks to [@ClaudioCas](https://github.com/ClaudioCas) (#223).
- Chat tabs now come back when you reopen a solution, each continuing its conversation with the earlier messages shown; a reopened tab starts its agent only when you send it a message. On by default (*Settings → Terminal → Reopen chat tabs when the solution opens*); Claude Code, Codex and Devin.
- A resumed conversation shows its saved title and color right away instead of after the first message.
- Images you attach now appear as thumbnails in your chat message instead of a list of file paths; click one to open it.
- Attachment cards in the prompt show only the picture, and the large preview on hover now opens quickly and reliably.
- New *Compact chat toolbar* option puts the chat toolbar inside the message box, with the less used actions grouped in the ⋯ menu (*Settings → Layout*, off by default).
- The agent selector in the chat toolbar now hides itself when only one agent is enabled.
- New option for a plain working status that always reads "Thinking…" instead of the rotating playful words (*Settings → Layout*); once a chat has started, the message box hint shrinks to "Reply…".
- In the Modern icon style, running tool steps show the same rotating ring as the status line.
- The *Claude Code* toolbar now shows up on its own after installation; hide it from View → Toolbars if you don't want it.
- Fixed an empty box shown after the weekly limit in the usage bars and in the native chat's usage popup.
- The usage popup no longer stays on "Refreshing…" for a long time while the usage page is reloaded in the background.

### Version 267.0
- Session History now lists Codex chats started in the native chat, so they can be viewed and resumed again. Thanks to [@nanitaro](https://github.com/nanitaro) for reporting (#222).
- The plan approval card in the native chat now has a text box for telling the agent what to change; "Send feedback" returns it to planning with your notes. Thanks to [@lpowca](https://github.com/lpowca) for reporting (#221).

### Version 266.0
- Fixed chat replies showing raw markdown (asterisks, dashes, backticks) instead of formatted text when they quoted a file reference or a link in code formatting. Thanks to [@ClaudioCas](https://github.com/ClaudioCas) (#220).

### Version 265.0
- Long resumed conversations in the native chat now open quickly at the newest message, and rows render as they scroll into view. Thanks to [@MartinWooChina](https://github.com/MartinWooChina) (#219).
- The chat now keeps following long streamed replies to the last line, and fast mouse-wheel scrolling no longer jumps or loops; sending a prompt snaps to the new message.

### Version 264.0
- The context-usage ring now fills on Anthropic-compatible gateways that omit per-iteration usage (for example DeepSeek), instead of staying empty at "No turn yet". Thanks to [@MartinWooChina](https://github.com/MartinWooChina) (#218).

### Version 263.0
- Native chat now shows the correct context usage and sets a matching auto-compact point for DeepSeek and other custom 1M-context models, instead of the CLI's 200k fallback. Thanks to [@MartinWooChina](https://github.com/MartinWooChina) (#216).

### Version 262.0
- Fixed a crash that could close Visual Studio while hovering over or resizing the native chat. Thanks to [@MartinWooChina](https://github.com/MartinWooChina) (#217).

### Version 261.0
- Opening or switching a folder or solution no longer freezes Visual Studio while the chat agent restarts; the restart now waits until the IDE has finished loading. Thanks to [@MartinWooChina](https://github.com/MartinWooChina) (#215).
- Changing the model, effort, or permission mode no longer blocks the IDE while the previous agent shuts down.
- New *Modern* chat icon style: the chat toolbar, Stop and jump-to-latest buttons use Visual Studio's own icons, the live status shows a rotating ring, and chat tabs show colored status (✅ done, ❌ error, ❓ waiting). Opt-in from *Settings → Layout → Chat icons*. Thanks to [@ClaudioCas](https://github.com/ClaudioCas) (#214).
- In the Modern style you can pick the working icon on the tab: blue arrows, an animated clock or an animated moon.
- New option to hide token counts in the chat, both while the agent works and after each turn (*Settings → Layout → Chat tabs*).
- The chat's turn timer now counts whole seconds instead of tenths.
- In the Modern style, attached images show as cards with the whole picture and the file name, so you can tell what you attached at a glance.
- New *Claude Code* toolbar (View → Toolbars) and View menu entries open the chat or the panel in one click.

### Version 260.0
- When Claude Code resumes on its own after its background tasks finish, the native chat now shows the turn as running again (clock, tokens, Stop and tab status) instead of leaving it at "Done". The end of such a turn only plays the finish sound and notification, without running the configured action.

### Version 259.0
- Native chat tabs now show what each session is doing in front of their name (⏳ working, ❓ waiting for your answer, ✳ done, ❌ error), so you no longer need to open every tab to check on it.
- Chat tabs you have not renamed are named after their topic, taken from your first message or, for Claude Code, written by a low-cost AI model. Both options are on by default in *Settings → Layout → Chat tabs*.

### Version 258.0
- In native mode the Claude Code panel now hides itself once the chat opens in its own tab, instead of staying open as an empty window. It comes back when the chat is docked back or native mode is turned off; turn it off in *Settings → Layout* ("Hide this panel while the native chat is in its own tab").
- When the agent asks a question, you can pick an answer and also type extra details in the free-text box. Both are sent together, and the box shows which answers the details apply to.

### Version 257.0
- "Approve and skip permissions" on a plan now skips permission prompts for that plan only: once the agent finishes it, the chat returns to the mode it was in before, such as Plan mode (issue #210).

### Version 256.0
- Refreshed the README: features are easier to scan and the contributing guide is clearer.

### Version 255.0
- The Claude Code Chat tab now comes back after restarting Visual Studio even when you had closed the Claude Code panel and kept only the chat open (issue #209).

### Version 254.0
- GitHub Issues now works with every native agent, not only Claude Code: after Fix with Agent the window shows the agent working and unlocks Commit & Push as soon as it finishes, instead of staying stuck on "Starting the agent…".
- Codex chat tabs opened with ✚ or from GitHub Issues now receive their prompts instead of silently ignoring them.

### Version 253.0
- Session History's "Resume Last Session" button now reopens the most recent Claude Code or Devin conversation in the native chat instead of closing the window and starting a fresh chat (issue #208).

### Version 252.0
- Resuming a long native chat no longer stalls Visual Studio: historical thinking is not replayed (live thinking still streams as before), and only the newest 40 tool calls are replayed as full cards, with older ones kept as one-line summaries.

### Version 251.0
- Qwen Code questions in the native chat now show the actual question and its choices, with a box for your own answer, instead of an empty window with only Submit and Cancel (issue #203).

### Version 250.0
- The Claude Code panel and its chat tab now load automatically when Visual Studio starts, even when the panel is behind another tab, so you no longer have to click the panel first (issue #204).

### Version 249.0
- Fixed picking another agent in the native chat sometimes doing nothing until a different project was opened.

### Version 248.0
- New "Show active subagent count" option (Settings → Layout, off by default): the panel toolbar, or each chat tab in native mode, shows a live count of the subagents Claude Code is running, and hovering it lists what each one is working on. Claude Code only.

### Version 247.0
- "Generate Commit Message, Commit and Push" no longer brings Visual Studio to the front or opens Git Changes, so it runs quietly in the background while you work elsewhere.

### Version 246.0
- New "Keep last prompt visible" option (Settings → Prompt, off by default): the prompt box keeps showing your last prompt, dimmed, until you start typing, so you can see what the agent is working on. It is never resent by accident; Ctrl+Up recalls it for editing (issue #201).

### Version 245.0
- Native mode: new chat tabs opened with "+" now use the configured chat font, font size and composer height instead of the defaults.
- Thanks to [@karpach-relativity](https://github.com/karpach-relativity) for contributing this fix (#202).

### Version 244.0
- Native mode: in a narrow chat tab the welcome card moves its tips below the welcome details instead of stretching tall, and the signed-in email gets its own line.
- Thanks to [@gh61](https://github.com/Gh61) for contributing this improvement (#200).

### Version 243.0
- Native mode: scrolling inside an expanded tool card, such as a long shell command, no longer jumps the chat back to the newest reply while the agent keeps writing.
- Thanks to [@gh61](https://github.com/Gh61) for contributing this fix (#199).

### Version 242.0
- Native mode: tool cards show their own short description, resumed Claude Code chats show earlier tool calls as cards, and replies stream smoothly with live formatting. Thanks to [@gh61](https://github.com/Gh61) (#194–#198).

### Version 241.0
- Native mode: switching Claude Code's permission mode mid-response no longer stops the agent, and leaving Plan mode waits for plan approval (#193).

### Version 240.0
- *Fix with Agent* in GitHub Issues asks which model and effort to use, with 💡 Recommend for Claude Code.

### Version 239.0
- Images in the GitHub Issues window now load without any risk of briefly freezing Visual Studio.
- Hovering an image in the GitHub Issues window shows a larger preview; clicking it still opens the image in the browser.

### Version 238.0
- New **GitHub Issues** (🐞) window: fix each issue in its own branch, worktree and chat tab, then commit, push, merge and reply/close from the same place, with live fix status and formatted issue text.

### Version 237.0
- **Native mode code blocks** have a copy button in the top-right corner that copies just the code.

### Version 236.0
- Native mode usage button: plan limit labels no longer get covered by long status text, and it shows "Refreshing…" and then the update time.

### Version 235.0
- **Native mode usage button** next to the composer's selectors shows the chat's context window use and your plan limits; *See detailed breakdown* opens the Claude Usage tab.

### Version 234.0
- **Pull from git before the first prompt** now also pulls when you open another solution in the same Visual Studio session; before, the first prompt there could go out without a pull.

### Version 233.0
- **Install latest OpenPets** in *⚙ → Settings... → Automation* downloads the newest OpenPets release for Windows, shows the download progress, and starts its installer.

### Version 232.0
- **Devin model picker** shows a cost bar and the input, cached input and output price per million tokens for each model; picking a price moves the bar, and the model you are using is marked on it.

### Version 230.0
- **Claude Usage tab** no longer paints over the pane docked next to it (for example the Terminal below it) after the layout changes; it now snaps back to its own area automatically.

### Version 229.0
- **Status line in native chat**: if you set up a Claude Code status line, its output now appears under the summary after each Claude turn in native mode, for Claude Code on Windows and in WSL; nothing extra is shown when none is configured (issue #188).

### Version 228.0
- New **"/" command picker** in the Claude chat prompt box, plus /compact reporting freed context, /color tinting the chat header and /clear clearing the chat.

### Version 227.0
- **Send button** now appears in the corner of the prompt box (terminal and native chat) when "Button only" send mode is selected, and changing the send key in Settings takes effect in open chats right away.

### Version 226.0
- **Install Caveman** now has separate *Windows* and *WSL* buttons, so you can install the skill for either one regardless of which agent is running; the Settings note explains that each keeps its own skills.

### Version 225.0
- **Install Caveman** moved to *⚙ → Settings... → Automation* and now opens a command prompt that installs the Caveman skill for you, then reloads the agent (terminal or native chat) when you close it.

### Version 224.0
- **Show Usage** for Devin now works in native mode: it posts your ACU usage (used, limit and percent) in the chat, and typing `/usage` there does the same.
- In terminal mode, **Show Usage** for Devin now runs `/usage` in the terminal instead of opening the web page.

### Version 223.0
- On Agent Finish moved to its own first tab in Settings and starts off for never-configured solutions; the Usage tab's options moved to Layout.

### Version 222.0
- On Agent Finish is now per solution only (no global defaults), and the long Behavior tab in Settings is split into Prompt and Automation tabs.

### Version 221.0
- Usage bars no longer show your previous account after switching Claude Code accounts; the Claude Usage page signs out and asks you to sign in again.

### Version 220.0
- Generate Commit Message (including Commit and Push) no longer credits the AI: the chat shows the cleaned message, and more forms of AI attribution are removed from the commit.

### Version 219.0
- Fixed "Update Agent" for Devin (native) failing with a "file in use" error; the update now retries automatically.

### Version 218.0
- New opt-in *Track agent changes for review (Keep/Undo)*: the Changes view lists the files the agent changed, with per-file and bulk Keep/Undo and Compare. Thanks to [@osjimenez](https://github.com/osjimenez) (#183).

### Version 217.0
- Added **Grok** (Grok Build) as a supported agent in terminal and native mode, with a *Grok: Always Approve* toggle.

### Version 216.0
- Added **Qwen Code** as a supported agent in terminal and native mode, with a *Qwen Code: Yolo Mode* toggle.

### Version 215.0
- Claude Code model menus and *Recommend AI Model* no longer offer Fable when the Claude usage page shows a zero usage-credits balance, since Fable requires credits; if the balance cannot be read, Fable stays available.

### Version 214.0
- Inline usage bars keep updating on spend-limit-only accounts and say when they were last updated if a refresh fails (#182).

### Version 213.0
- New **Recommend AI Model** (💡) for Claude Code: Opus suggests the model and effort that fit your prompt, and *Apply* switches to it.

### Version 212.0
- Plan mode now sticks after approving a plan, and terminal mode gains *Claude Code: Start in Plan Mode* (#181).

### Version 211.0
- Custom commands can be cloned, and the new Settings **Backup** tab saves and restores the whole extension configuration.

### Version 210.0
- "Generate Commit Message, Commit and Push" no longer shows a confirmation dialog after a successful push; errors are still reported.

### Version 209.0
- Multi-line prompts sent to Claude Code no longer get split into separate messages or held back waiting for Enter, so long prompts submit reliably as a single message.
- Thanks to [@rbuss93](https://github.com/rbuss93) for the contribution (#180).

### Version 208.0
- Fixed prompts sometimes staying in the terminal input line without being submitted, especially right after a long agent response, so you no longer have to press Enter yourself.
- Thanks to [@rbuss93](https://github.com/rbuss93) for the contribution (#178).

### Version 207.0
- The Native mode chat's 📎 button now opens a menu with "Attach a file...", "Insert editor selection" and "Insert active file path", so these are reachable when the chat is in its own tab (issue #174).

### Version 206.0
- Fixed the working directory not updating when switching directly from one open folder to another (File > Open > Folder), so the panel and agent now follow the newly opened folder instead of staying on the previous one.

### Version 205.0
- New *Generate Commit Message, Commit and Push* action; both commit actions can be toolbar buttons or On Agent Finish presets, and generated messages no longer credit the AI.

### Version 204.0
- Claude Code in native mode checks files out of TFVC through Visual Studio before editing them; a failed checkout blocks the edit.

### Version 203.0
- Fixed "Send Selection to Claude Code" so it reaches the conversation when Native mode's chat has its own tab, instead of being lost behind the "Show Chat" button.

### Version 202.0
- The "@" file picker reaches every file in large projects, honors .gitignore, skips Unity noise and supports file-type and folder filters (#174).

### Version 201.0
- The terminal keeps its font size and full size across repeated display changes, and Ctrl+0 resets the native chat zoom. Thanks to [@metman-oss](https://github.com/metman-oss) (#173) and [@karpach-relativity](https://github.com/karpach-relativity) (#172).

### Version 200.0
- Native mode: parallel chat tabs get the full toolbar, and a closed main chat tab stays closed until you click *💬 Show Chat*.

### Version 199.0
- Native mode: the agent (Claude Code, Codex, Cursor Agent, etc.) selector now works in every parallel chat tab, not just the first one — each tab switches and keeps its own agent independently (issue #171).

### Version 198.0
- Native Claude Code sessions now show up in Claude Code's own resume list, and session names sync both ways (#170).

### Version 197.0
- Open Code support is back: pick it from the agent menu to run it in the terminal or in native mode, with model selection, custom CLI path and extra launch arguments like the other agents.

### Version 196.0
- Native mode: a lost chat tab can always be brought back with *💬 Show Chat* or *View → Other Windows → Claude Code Chat*; closing the tab now just closes it (#168).

### Version 195.0
- Fixed native mode for Devin falling back to the embedded terminal on launch whenever a Sonnet/Opus effort-level model (e.g. "Claude Sonnet 5 High") was selected — the pick now applies correctly instead of being reported as unavailable.

### Version 194.0
- In native mode, a message typed to Devin while it is working now reaches it straight away and redirects what it is doing, instead of waiting for the current answer to finish.

### Version 193.0
- "Pull from git before the first prompt" now also fires when the first message of a session is sent from a custom command, not just when typed into the prompt box.

### Version 192.0
- The agent-finished and terminal-repair notices no longer replace each other, and a brief missing display scale no longer resizes the terminal.

### Version 191.0
- The terminal keeps its text size, output and full panel size after Remote Desktop reconnects or monitor/scaling changes. Thanks to [@metman-oss](https://github.com/metman-oss) (#166).

### Version 190.0
- Codex and Cursor Agent now switch model mid-conversation without restarting the chat, matching how effort already worked — Claude Code already had this.

### Version 189.0
- Native mode's permission selector adds *Manual mode*, and *Ask permission* is renamed to *Accept edits*.

### Version 188.0
- Native mode's permission selector adds *Auto*, letting Claude Code decide when to prompt. Thanks to [@karpach-relativity](https://github.com/karpach-relativity) (#165).

### Version 187.0
- Native mode plan cards get an *Approve and skip permissions* button, plus a **▾** menu to approve on a different model (Opus, Sonnet, Haiku) with or without skipping permissions — the agent keeps working without being interrupted.

### Version 186.0
- Open Code support removed. If it was your selected agent, the extension falls back to Claude Code.

### Version 185.0
- New *Pull before sending* option (on by default) runs git pull before your first prompt and hands any conflicts to the agent to resolve.

### Version 184.0
- New *⚙ → Settings... → Theme → Native Color Schema* option to choose the default color for native mode sessions; it is remembered across Visual Studio restarts, and a color picked for a single session still takes priority.

### Version 183.0
- Native mode: the Edit/Write/MultiEdit permission prompt now shows the colored diff up front, the same way it renders after you click Allow, instead of raw JSON (issue #160).

### Version 182.0
- Settings tooltips now wrap, and a new Toolbar *Alignment* option can push buttons back to the right. Thanks to [@rbuss93](https://github.com/rbuss93) (#156, #158).

### Version 181.0
- Native mode: fixed replies sometimes missing, clearer messages when Claude Code is missing or signed out, and clickable links; the Command Prompt terminal always has scrollback.

### Version 180.0
Security fix for Antigravity in native mode: text in a prompt, such as build errors or exception messages from the opened code, can no longer be read as extra command-line options that turn off permission prompts.

### Version 179.0
Native mode is now the default chat experience for new installs, replacing the embedded terminal as the first thing you see; switch back to the terminal any time via *⚙ → Settings... → Terminal → Use native mode*. Existing installs keep whatever they already had configured.

### Version 178.0
- Switching Claude's model or effort in native mode now applies instantly to the running conversation, keeping prompt-cache savings.

### Version 177.0
- Native mode: closing the chat tab docks it into the panel and reopening the panel restores the tab; the panel toolbar is now one scrollable strip.

### Version 176.0
- New chats from a parallel tab match that tab's agent and model, the picked Claude model survives an unclean shutdown, and the chat toolbar gets 📎.

### Version 175.0
Toolbar icons are now centered inside their buttons instead of sitting low against the bottom edge, where they looked clipped. Applies to the panel toolbar and to native mode's chat toolbar.

### Version 174.0
- Native mode's chat toolbar adapts to narrow windows with shorter labels, a ⋯ menu and ◀/▶ scrolling.

### Version 173.0
- Native mode's composer toolbar no longer leaves stray gaps between its buttons and selectors.

### Version 172.0
- Fixed clearing Set Working Directory not returning to the project folder, and Ctrl+Up/Down in native mode leaves the cursor at the end (#152).

### Version 171.0
- Fixed an empty ☰ Tools menu and Set Working Directory changes in native mode; Detach is no longer offered in the Toolbar tab in native mode.

### Version 170.0
- Native mode's chat tab gets its own ⚙, ☰ and ⚡ buttons and a scrollable action row, with many panel toolbar and layout fixes (#151). Native mode also falls back to the terminal when the chosen model is unavailable.

### Version 169.0
Fixed native mode losing access to the model/effort/permission selectors when "Hide prompt input box" was turned on and the chat wasn't detached to its own tab.

### Version 168.0
- The Devin model picker now supports favorites, which replace the automatic recently-used list.

### Version 167.0
- Devin's model menu now opens a searchable picker with each model's context window, cost tier and prices.

### Version 166.0
- Native mode tool cards show a ↗ icon to jump to the file they touched, for every provider.

### Version 165.0
- Parallel native chat tabs get their own model, effort and permission selectors; fixed Clear Chat and lost tab titles/colors in parallel tabs.

### Version 163.0
- Native mode: Skip → Ask permission switch fixed, *Allow for the rest of this session* added, and a note when Claude Code has updated itself.

### Version 162.0
- The terminal fixes itself when a command switches its text encoding (can be turned off in Settings → Terminal).

### Version 161.0
- The model menu lists Devin's real models from your account, and every agent's model list refreshes at each Visual Studio start.

### Version 160.0
- Prompts from a parallel chat tab appear in that tab, and a new per-provider *Extra launch arguments* box sits under Settings → CLI Paths.

### Version 159.0
- Inline usage bars refresh right after Visual Studio starts without opening the Claude Usage tab.

### Version 158.0
- The Claude Usage panel returns to the usage bars automatically after switching accounts.

### Version 157.0
- Fixed Switch Account and log out in the Claude Usage panel, and Refresh always returns to the usage page.

### Version 156.0
Fixed multiple-choice questions never appearing when "Skip permissions" was on — Claude reported the question tool as unavailable for the session and answered in plain text instead. Questions and plan approvals now show up as clickable cards in every permission mode.

### Version 155.0
- Usage bars auto-refresh every minute and update even when the Claude Usage tab sits behind another tab (#111).

### Version 154.0
- Usage bars refresh reliably with live data (#111), and the terminal no longer goes blank after starting debugging (#142).

### Version 153.0
Claude Usage now refreshes automatically while its Visual Studio tab is unfocused, so the usage bars stay current without pressing Refresh.

### Version 152.0
Fixed the Claude Usage panel wrongly showing "WebView2 runtime is required" when another Visual Studio instance already had it open — it now falls back automatically so both instances can display the usage page at the same time.

### Version 151.0
- A collapsed or background panel no longer slides open on every debug step, and a closed panel stays closed (#141).

### Version 150.0 - NeilN1 contribution
- Fixed Windows Terminal launches failing on non-ASCII paths (#138). Thanks to NeilN1.

### Version 149.0
- Fixed agent switching in native mode leaving the previous agent running, and failed Codex/Cursor turns now show the agent's own error.

### Version 148.0
The Claude model menu now offers **Fable** in place of **Best**, selecting Claude Fable directly for the most demanding tasks. Available in both the terminal and the native chat.

### Version 147.0
- Fixed Windows Terminal failing to start with non-ASCII user names (#138), and prompt history fills the chat tab you are typing in.

### Version 146.0
- Fixed Windows Terminal failing to launch when the path contains an apostrophe (#138).

### Version 145.0
The effort level you select now survives a restart of the code agent — previously the new session quietly started at the CLI's own level while the slider still showed the previous selection.

### Version 144.0
Native mode tables now render full grid lines around every cell (header and data rows), instead of just an underline below the header.

### Version 143.0
Fixed closing one Visual Studio instance sometimes killing the Claude terminal in other open instances when using Windows Terminal (issue #135).

### Version 142.0
- The Claude Usage window no longer pops up during background refreshes (#133). Thanks to [@metman-oss](https://github.com/metman-oss).

### Version 141.0
- Devin queues follow-up messages while replying, and the Claude Usage window no longer goes blank when auto-hidden (#131).

### Version 140.0
"On Agent Finish"'s follow-up field now offers a "Generate Commit Message" preset alongside the "no AI credit" one — picking it runs the same Generate Commit Message action instead of sending literal text to the agent.

### Version 139.0
Session History now supports Devin (native or WSL), including viewing, resuming any session or the latest one, and restoring earlier messages in native mode.

### Version 138.0
Restored the highlighted feature and menu names in the README so the Marketplace page reads as before.

### Version 137.0
Listed Generate Commit Message among the Features, and tidied up the repository's internal documentation files.

### Version 136.0
Fixed Generate Commit Message playing the "On Agent Finish" sound or running its action after the AI wrote the commit message.

### Version 135.0
Fixed native mode chat occasionally rendering streamed replies with the first few characters of a line missing.

### Version 134.0
- New **Generate Commit Message** button: the agent writes a commit message from your changes and fills it into Git Changes.

### Version 133.0
Fixed restored Codex sessions opening without their earlier messages in native mode, including older interrupted conversations.

### Version 132.0
Session History now supports Codex on Windows and WSL, including viewing, renaming, deleting, resuming any thread or the latest one, and restoring earlier messages in native mode.

### Version 131.0
The Claude Code panel and any tabs it created (detached terminal, native mode chat tabs) now stay visible while you debug your project, instead of getting hidden by Visual Studio's own layout behavior (issue #130).

### Version 130.0
- Send Selection works in the XAML editor, 📎 adds *Insert Active File Path* (#127), and automated sends go to the last focused chat tab.

### Version 128.0
Exported session transcripts now always start with the session's name, so the file can be pasted straight into a GitHub issue as its details.

### Version 127.0
Each parallel chat session now shows its own name above the conversation and in its document tab title, so open chats are told apart from the tab strip alone.
Attachments and pasted images now stay in the chat tab they were added to, instead of always going to the first session.

### Version 126.0
- Native mode supports parallel chat sessions: ✚ opens an independent session in its own tab, and ↻ starts fresh.

### Version 125.0
- Native mode: Esc stops the turn, and sessions can be renamed and given their own accent color.

### Version 124.0
Reasonix now always runs in the embedded terminal, even with native mode turned on.
The Reasonix model menu now lists the models actually configured on your machine, read from Reasonix itself, instead of a fixed list that could not be used.

### Version 121.0
Native mode now records why an agent failed to start when the chat falls back to the terminal, including the message the CLI itself printed.

### Version 120.0
Fixed Reasonix native mode still opening the terminal on later launches, once a leftover Reasonix process from an earlier attempt had already been holding onto the workspace.

### Version 119.0
Added diagnostic logging for native mode start failures, to help pin down why a provider sometimes still opens the terminal with native mode on.

### Version 118.0
Fixed native mode (chat tab) not being used when switching AI provider from the ⚙ menu — it now behaves the same as switching on startup or restart, instead of silently dropping to the terminal.

### Version 117.0
Fixed the Settings window sometimes opening with the Terminal tab scrolled to the middle instead of the top.

### Version 116.0
Fixed Reasonix (native) getting stuck on first launch behind its one-time "Allow anonymous CLI usage statistics?" console prompt — it is now answered automatically so the agent starts normally.

### Version 115.0
- *Update Agent* works in native mode and resumes the conversation afterwards; fixed "Access is denied" and a Defender flag on Devin's updater.

### Version 114.0
- Codex gets a welcome card with model and account, a reasoning-level selector, queued follow-ups and clearer token counts in native mode.

### Version 113.0
- Fixed Change Account for WSL in native mode, clearer agent-switch failures, and CLIs installed while Visual Studio runs are now detected (#124).

### Version 112.0
- The terminal scrolls back during generation with less garbled text (#118, #119), the custom command editor is resizable (#123), and native mode keeps the chat on project reload.

### Version 111.0
Every agent now has a model picker, in the chat tab and in the terminal's model (🤖) menu — the list comes from the agent itself, with Refresh Models to update it. Reasonix, which publishes no list, gets a Configure Models... editor like Devin's.

### Version 110.0
Fixed the mouse pointer disappearing after sending a prompt to the terminal, and it no longer jumps away from where you left it.

### Version 109.0
Chat tab links are now brighter in dark theme, so file and web references stay easy to read against a dark background.

### Version 108.0
File and web links in the chat tab now work even when the agent wraps the citation in a code snippet, instead of only when it's plain text.

### Version 107.0
- Links and file references in the chat tab are clickable, and text selection uses a softer accent color.

### Version 106.0
- Session History ignores injected caveat/skill text and slash-command-only sessions. Thanks to [@wluisdev](https://github.com/wluisdev) (#120).

### Version 105.0
Updated extension description

### Version 104.0
Updated extension description

### Version 103.0
Native mode now in beta phase

### Version 102.0
- The Devin model picked in native mode now really applies, switches without a restart, and reports unavailable models.

### Version 101.0
- Fixed the native mode composer placeholder so it reads **Ctrl+Up/Down for prompt history** instead of a rendered-as-1 arrow glyph.

### Version 100.0
- Updated the remaining native mode hints and documentation to match the **Ctrl+Up / Ctrl+Down** prompt history shortcut, including the welcome card tips and the placeholder text.

### Version 98.0
- Refreshed the Devin model list with a *Configure Devin Models...* entry, and native chat gets Ctrl+Up/Down prompt history.

### Version 97.0
- Attached images show thumbnails with a larger hover preview in both the chat composer and the terminal panel.

### Version 95.0
- Fixed native mode's Antigravity chat not seeing the open project.

### Version 94.0
- Fixed a runaway Windows Terminal font size after load, and native chat animates running tool calls.

### Version 93.0
- Native mode still in alpha phase. It will be improved and stabilized in next releases. Please do not use for real work yet.
- The **@** file/folder picker now also works in native mode's chat composer, not just the terminal-mode prompt box.

### Version 92.0
- Native mode still in alpha phase. It will be improved and stabilized in next releases. Please do not use for real work yet.
- Native mode now shows which Claude account is signed in, both on the welcome screen and after using **Change Account**.

### Version 91.0
- Native mode still in alpha phase. It will be improved and stabilized in next releases. Please do not use for real work yet.
- Added **Change Account** to the ⚙ menu in native mode: opens claude.ai in your browser to switch accounts, then resumes the conversation.

### Version 90.0
- Native mode still in alpha phase. It will be improved and stabilized in next releases. Please do not use for real work yet.
- Maintenance release over 89.0, with no functional changes.

### Version 89.0
- Native mode: non-English characters reach the agent, /plan, /model, /effort and /btw work in the chat, resumed sessions show history, and build errors can be sent.

### Version 88.0
- Native mode: model/effort/permission changes no longer end the conversation, and new chats open with a welcome card.

### Version 87.0
- Native mode chat gets formatted answers, descriptive tool cards with diffs, image paste, font settings, prompt history and a live progress line.

### Version 86.0
- Native mode still in alpha phase. It will be improved and stabilized in next releases. Please do not use for real work yet.

### Version 85.0
- Native mode chat answers questions with clickable cards, supports plan mode, and gains an effort slider, a new chat button and zoom.

### Version 84.0
- Native mode opens the conversation in its own document tab, with its own message box and agent/model/effort/permission selectors.

### Version 83.0
- Fixed native mode showing an empty panel when the terminal had been detached.

### Version 82.0
- New optional **native mode**: the conversation shows as a chat instead of a terminal, and On Agent Finish fires when the agent reports it is done.

### Version 80.0
- Fixed On Agent Finish sometimes not firing for typed prompts, turns over 30 minutes, or answers containing "to navigate".

### Version 79.0
- Fixed the terminal opening with larger text after an upgrade by carrying your previous zoom over to the font size setting (#115).

### Version 78.0
- Ctrl+Scroll zoom in the Command Prompt terminal is now remembered as the console font size.

### Version 77.0
- Fixed the new Session History sort selector's drop-down being unreadable in dark mode (light text on a white popup); it now follows the current theme.

### Version 76.0
- New opt-in *Auto-send runtime errors to the agent*, and Session History gains a sort selector (#114).

### Version 75.0
- Fixed the extension failing with "Error sending prompt: Method not found: ...JToken.ToString..." when submitting a prompt or opening the Settings dialog on version 74 (issue #112).

### Version 74.0
- New **Console font size** setting, plus fixes for runaway Ctrl+Scroll zoom and missed first keystrokes.

### Version 73.0
- Fixed the Claude Usage panel cutting off content (like usage credits, balance, and buy-credits) instead of letting you scroll down to it.

### Version 72.0
- Fixed the agent's input box flooding with thousands of repeated pasted-text blocks on Windows 10 right after sending a prompt (issue #83).

### Version 71.0
- Fixed messages sent to the agent getting prefixed with a growing block of blank lines that accumulated in the agent's input box over a session (issue #108).

### Version 70.0
- Fixed the v69.0 launch-script fix itself breaking terminal startup on some machines ("'chcp' is not recognized") — the temp launch script is now written without a UTF-8 byte-order mark, which `cmd.exe` couldn't parse (issue #107).

### Version 69.0
- Fixed the terminal failing to launch ("The system cannot find the file specified") when the solution/workspace folder path contains " - ", such as `MS-059 - ZZZZ-EC16` (issue #106).

### Version 68.0
- New **Send Build Errors to Agent** button, with fixes for stuck pastes, Windows Terminal and On Agent Finish after build-error sends.

### Version 67.0
- Fix issues.

### Version 66.0
- **More reliable sending for Devin**: fixed an intermittent issue where the prompt would appear typed into Devin's session but Enter wouldn't submit it. The terminal now waits a bit longer for Devin's interface to catch up before pressing Enter.

### Version 65.0
- **Auto-send build errors to the agent**: an opt-in setting that, whenever a Visual Studio build finishes with errors, automatically sends the errors (plus warnings for context) to the active agent so it can fix them. Enable it via *⚙ → Settings... → Behavior*.

### Version 64.0
- The console font list shows only monospaced fonts, with a *Show all fonts* option (#105).

### Version 63.0
- The extension now detects when Windows "legacy console" mode is enabled, which prevented the terminal from ever attaching to the panel, and offers to fix it with one click (no administrator rights needed) followed by a Visual Studio restart (issue #104).

### Version 62.0
- The tool window title and model menu no longer show a model for Claude Code and Devin, since it can change inside the terminal.

### Version 61.0
- The model (🤖) button now also appears for Codex, Cursor Agent, PI, Antigravity, Reasonix, and Open Code — clicking it opens the agent's own model picker so you can switch models without leaving the terminal.

### Version 60.0
- Fixed the model menu showing Claude-only options **Best** and **Opus Plan** while Devin was the active agent — these picks now appear for Claude Code only.

### Version 59.0
- Reduced keyboard lock-ups after very long answers, with a notice when the terminal is catching up (#89).

### Version 58.0
- Fixed the **@ file/folder picker** cutting off long file paths with no way to see the rest — the popup now scrolls horizontally and shows the full path in a tooltip on hover (issue #103).

### Version 57.0
- Fixed a blank gap left by the hidden prompt box after switching windows (#101).

### Version 56.0
- **Terminal-only mode**: hide the prompt input box to give the terminal more vertical space, while keeping the Send/Attach, Restart, Model, and ⚙ buttons reachable to turn it back on. Toggle from *⚙ → Hide Prompt Input Box* or *⚙ → Settings → Layout* (issue #101).

### Version 55.0
- Fixed **On Agent Finish** firing prematurely while Claude Code was still waiting on its own background agents to finish — the completion notification now holds until the whole turn, background agents included, is actually done.

### Version 54.0
- Fixed **Claude Usage** showing outdated numbers and no longer trimming to just the usage bars after a recent Claude.ai layout change — the session and weekly percentages now read correctly again, and the view works regardless of your account's display language.

### Version 53.0
- The working directory can be set per solution (#100), Windows Terminal copy/paste was fixed (#99), and TUI Fullscreen was removed.

### Version 52.0
- On Agent Finish can play a distinct sound when the agent is waiting for your answer.

### Version 51.0
- Fixed **Ctrl+Scroll** (zoom) not working in other apps while the extension's terminal panel occupied the same screen area — the extension no longer hijacks the gesture unless Visual Studio is actually the foreground window.

### Version 50.0
- On Agent Finish gets follow-up presets, tracks the right Windows Terminal, and shows more of the command text in notifications.

### Version 49.0
- New installs include a **Commit & Push** custom command, and On Agent Finish actions can send a follow-up message.

### Version 48.0
- **Session History** now has a **View** button (also on the right-click menu) that opens the selected conversation as readable text in your default editor, so you can skim a past session before resuming it.

### Version 47.0
- PI and Antigravity exit cleanly before updating, and Session History supports `CLAUDE_CONFIG_DIR` pointing at the projects folder.

### Version 46.0
- The Claude model menu adds **Best** and **Opus Plan**, with fixes for terminal focus on questions and On Agent Finish on question replies.

### Version 45.0
- **Session History** now honors a relocated Claude Code data folder — if you set the `CLAUDE_CONFIG_DIR` environment variable (e.g. to another drive), it lists and resumes sessions from there instead of the default `~/.claude` location.

### Version 44.0
- New **Console font** picker with live preview for languages such as Chinese, Japanese and Korean.

### Version 43.0
- The **Max** and **Ultracode** effort levels now apply to the current session only and are no longer remembered between Visual Studio restarts — the next launch starts from your last durable level (Low, Medium, High, or Extra High), matching how Claude Code treats these levels.

### Version 42.0
- Claude Code's effort level is now a slider in the 🤖 menu, from Low to Ultracode.

### Version 41.0
- On Agent Finish works reliably with Windows Terminal, including full-screen agents like Devin.

### Version 40.0
- Fixed a duplicated Devin model list, and View Changes, Session History and Show Usage are always available (#97).

### Version 39.0
- Update Code Agent now really updates Devin (native), and On Agent Finish is available with Windows Terminal (experimental).

### Version 38.0
- Added **Devin (native)** for Windows with configurable Devin models; the former Windsurf provider is renamed to Devin.

### Version 37.0
- Fixed mouse-wheel scrolling in Claude Code's "TUI Fullscreen" mode scrolling the agent even when another window (such as Notepad) was layered over the terminal area — the wheel now only scrolls the agent while the terminal is focused.

### Version 36.0
- Restored smooth mouse-wheel scrolling in Claude Code's TUI Fullscreen mode (#96).

### Version 35.0
- Sessions can be renamed in Session History, which also gains a filter box and a *Renamed only* toggle (#95).

### Version 34.0
- Fixed On Agent Finish firing early after clicking the terminal (#94), and the default idle time is now 5 seconds.

### Version 33.0
- Added Reasonix (DeepSeek-native coding agent) as a supported AI agent. Install it with `npm i -g reasonix`, then enable it from the agent menu via "Configure Visible Code Agents..." (issue #93).

### Version 32.0
- Fixed the terminal being flooded with "[Pasted text]" blocks in TUI Fullscreen mode (#92).

### Version 31.0
- Added a "TUI Fullscreen" option to the Claude Code model menu to switch the agent between fullscreen (flicker-free) and classic terminal rendering on demand.

### Version 30.0
- Fixed the terminal gradually ignoring keyboard input while On Agent Finish was enabled.

### Version 29.0
- Fixed right-clicks elsewhere in Visual Studio pasting into the terminal (#90), and a hang when the agent finished with Settings open.

### Version 28.0
- Fixed keyboard and mouse input dropping out while the agent works, and focus no longer bounces after clicking.

### Version 27.0
- Frequently used features can be promoted to toolbar buttons from a new Toolbar tab, with On Agent Finish and prompt box focus fixes.

### Version 26.0
- Fixed the embedded terminal sometimes becoming visible but not accepting typing, arrow keys, or pasted prompts after clicking it — terminal focus is now restored reliably for Command Prompt and Windows Terminal (issue #86).

### Version 25.0
- When "On Agent Finish" skips its action because no files changed, it now stays silent — no notification or sound — instead of announcing the skipped turn.

### Version 24.0
- Fixed stale provider labels, WSL launches with special characters, diffs for renamed files, and hardened Claude usage cookies.

### Version 23.0
- Fixed Visual Studio hangs and missing context menus from right-click paste, and added *Send selection as reference only*. Thanks to [@iwiwb](https://github.com/iwiwb) (#84).

### Version 22.0
- Fixed prompts flooding with duplicated pastes (#83), PI crashing on send (#82), and prompts landing in another Visual Studio instance.

### Version 21.0
- Non-English prompt text reaches the terminal correctly (#79), and agent output is readable under a light theme (#80).

### Version 20.0
- Terminal zoom (Ctrl+Scroll) and right-click paste now keep working after the agent's interface is fully up, not just during startup — previously both stopped responding once the agent took over the terminal (issue #78).

### Version 19.0
- Terminal zoom (Ctrl+Scroll) and paste now keep working when signed in with a custom API key — previously some sessions left the mouse zoom and right-click paste unresponsive, and the extension now falls back automatically so both behave the same as a normal sign-in (issue #76).

### Version 18.0
- Arrow-key replies to agent questions are reliable while On Agent Finish is enabled.

### Version 17.0
- Removed the Fable option from the Claude model menu — choose Opus, Sonnet, or Haiku.

### Version 16.0
- Changes to "On Agent Finish" settings now take effect for a turn that is already running — the new settings are applied when the agent finishes, instead of only on your next prompt.

### Version 15.0
- Arrow keys now work reliably while navigating the agent's question and selection menus in plan mode when "On Agent Finish" is enabled — the completion watcher recognizes the menu sooner and stays backed off as you move between options, instead of eating keystrokes.

### Version 14.0
- Fixed Restart leaving a blank panel after an On Agent Finish notification (#73), and question replies work reliably.

### Version 13.0
- "On Agent Finish" scripts can now close their console window automatically when they finish.
- "On Agent Finish" Run and Run without debugging actions can now clean and rebuild the solution before launching, and those preferences are saved.

### Version 12.0
- Loading or switching solutions now avoids repeated terminal attach attempts and no longer keeps retrying the same failed launch, reducing blank terminal panels after a new solution opens.

### Version 11.0
- Fixed Restart leaving a blank panel on slow shutdowns (#73), and clicking the terminal focuses it reliably (#74).

### Version 10.99
- Fixed the agent terminal staying stuck on a previously chosen custom background color after switching the theme back to Automatic, Dark, or Light — the terminal now always matches the selected theme.

### Version 10.98
- Opening or switching solutions starts the agent once in the right folder, fixing most blank panels after loading.

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
- Removed the *Don't bring Visual Studio to the foreground* setting, which could not work reliably.

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
- Settings is organized into tabs, Ctrl+Enter can be the send key, and prompt font and usage bar options moved into Settings.

### Version 10.83
- Running multiple Visual Studio instances no longer causes the selected AI agent and model to get mixed up across windows. Each instance now keeps its own provider/model choice in memory and only writes it to the shared settings file on shutdown.

### Version 10.82
- New opt-in setting to keep Visual Studio from coming to the foreground when clicking the terminal.

### Version 10.81
- The prompt panel can now be docked on the left or right (a side-by-side split) in addition to the top or bottom. Pick the position under Settings → Layout.

### Version 10.80
- Fixed the prompt becoming unresponsive to the keyboard (cursor not blinking, arrow keys not switching agents) while the embedded terminal still accepted typing — clicking in the extension now restores keyboard input without restarting Visual Studio.

### Version 10.79
- Fixed the prompt and its attached files being sent two or three times when the Send button (or Enter) was pressed again before a send finished.

### Version 10.78
- Fixed updating the PI agent failing because the extension tried to type "exit" — it now quits PI with CTRL+D twice before running the update.

### Version 10.77
- On Agent Finish opens in its own window with per-solution overrides, and no longer fires while the agent waits for input.

### Version 10.76
- New **On Agent Finish** notification and actions, and a new **"@" file picker** in the prompt box.

### Version 10.75
- Fixed the Claude Usage panel showing the claude.ai homepage and cookie banner instead of your usage, and not staying signed in across Visual Studio restarts.

### Version 10.74
- Added Antigravity to the marketplace tags so the extension is discoverable when searching for it.

### Version 10.72
- Fixed the Session History dialog showing "0 sessions found" when the project path contains non-English characters (e.g. Japanese) — the session list now loads correctly for these paths.

### Version 10.71
- New *Disable clipboard* option for machines where another app holds the clipboard.

### Version 10.70
- Fixed a system-wide input freeze while sending a prompt when another app was using the clipboard.

### Version 10.69
- Fixed the Update Agent button for Antigravity — it now exits the agent correctly before updating, so the installer runs instead of being typed into the running agent.

### Version 10.68
- Fixed Devin not launching when a new solution is opened while the terminal was already running — it would fall back to a plain command prompt until you manually restarted the agent. Devin now loads automatically like the other providers.

### Version 10.67
- Sending no longer aborts when a clipboard manager briefly holds the clipboard, with strict checking available as an opt-in.

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
  - [Qwen Code](https://github.com/QwenLM/qwen-code)
  - [Grok Build](https://grok.com/build)
- **No Third-Party Access**: Data is only accessible to the configured model provider

### Contact
For licensing inquiries or permission requests, please contact the author at dliedke@gmail.com.

---

*Claude Code Extension for Visual Studio - Enhancing your AI-assisted development workflow*

*Built with Claude Code, GPT, Codex, Qwen Code and Antigravity*
