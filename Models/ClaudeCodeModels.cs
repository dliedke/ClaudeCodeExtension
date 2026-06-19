/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Data models and enums for Claude Code extension
 *
 * *******************************************************************************************************************/

namespace ClaudeCodeVS
{
    /// <summary>
    /// AI Provider types supported by the extension.
    /// Explicit ordinals preserve previously-serialized SelectedProvider values
    /// in user settings across removals (ordinal 6 was QwenCode, now retired).
    /// </summary>
    public enum AiProvider
    {
        ClaudeCode = 0,
        ClaudeCodeWSL = 1,
        Codex = 2,
        CodexNative = 3,
        CursorAgent = 4,
        CursorAgentNative = 5,
        // 6 = QwenCode (removed in v10.12)
        OpenCode = 7,
        Windsurf = 8,
        Pi = 9,
        Antigravity = 10
    }

    /// <summary>
    /// Claude model types for Claude Code and Claude Code WSL
    /// </summary>
    public enum ClaudeModel
    {
        Opus,
        Sonnet,
        Haiku
    }

    /// <summary>
    /// Model types for the Windsurf provider
    /// </summary>
    public enum WindsurfModel
    {
        ClaudeOpus,
        ClaudeSonnet,
        Codex,
        GeminiPro
    }

    /// <summary>
    /// Effort levels for Claude Code reasoning
    /// </summary>
    public enum EffortLevel
    {
        Auto,
        Low,
        Medium,
        High,
        Max
    }


    /// <summary>
    /// Theme preference for the extension's terminal panel.
    /// Controls whether the terminal colors follow Visual Studio's theme
    /// or are forced to dark/light regardless of the IDE.
    /// </summary>
    public enum ThemePreference
    {
        /// <summary>
        /// Automatically follow the Visual Studio IDE theme (default behavior)
        /// </summary>
        Automatic,

        /// <summary>
        /// Always use dark theme regardless of VS IDE theme
        /// </summary>
        Dark,

        /// <summary>
        /// Always use light theme regardless of VS IDE theme
        /// </summary>
        Light,

        /// <summary>
        /// Use a specific user-chosen background color regardless of VS IDE theme.
        /// The color is stored in <see cref="ClaudeCodeSettings.CustomThemeColorArgb"/>.
        /// </summary>
        Custom
    }
    /// <summary>
    /// Terminal emulator type for the embedded terminal
    /// </summary>
    public enum TerminalType
    {
        /// <summary>
        /// Windows built-in Command Prompt (conhost.exe)
        /// </summary>
        CommandPrompt,

        /// <summary>
        /// Windows Terminal (modern terminal with better emoji/unicode support)
        /// </summary>
        WindowsTerminal
    }

    /// <summary>
    /// Orientation of the split between the prompt panel and the embedded terminal.
    /// Horizontal = stacked (top/bottom, the classic layout); Vertical = side-by-side
    /// (left/right). Combined with <see cref="ClaudeCodeSettings.InvertLayout"/> this
    /// yields the four "prompt panel position" choices: Top, Bottom, Left, Right.
    /// </summary>
    public enum LayoutOrientation
    {
        /// <summary>Prompt and terminal stacked vertically (top/bottom split).</summary>
        Horizontal,

        /// <summary>Prompt and terminal placed side by side (left/right split).</summary>
        Vertical
    }

    /// <summary>
    /// Action automatically performed when the AI agent finishes a turn
    /// (used by the "On Agent Finish" feature). Claude Code only.
    /// </summary>
    public enum AgentFinishActionType
    {
        None,
        BuildSolution,
        RebuildSolution,
        Run,
        RunWithoutDebugging,
        RunTests,
        RunScript,
        SendToAgent
    }

    /// <summary>
    /// User-defined shortcut for a frequently sent prompt or slash command.
    /// Surfaced in a dropdown next to the toolbar so the user can dispatch
    /// canned prompts (e.g. "/codex-review", "explain this file") to the
    /// active code agent without retyping them.
    /// </summary>
    public class CustomCommand
    {
        /// <summary>
        /// Display label shown in the toolbar dropdown menu.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Literal text sent to the terminal when the menu item is clicked.
        /// May be a slash command, a free-form prompt, or any string the
        /// active agent understands.
        /// </summary>
        public string Command { get; set; } = string.Empty;
    }

    /// <summary>
    /// Configuration for the "On Agent Finish" feature: optional sound + visible
    /// notification and an action (build/run/tests/script/chained command) triggered
    /// when a Claude Code turn completes. Detection rides the JSONL transcript, so the
    /// feature is Claude Code only. See ClaudeCodeControl.AgentCompletion.cs.
    /// </summary>
    public class AgentFinishConfig
    {
        /// <summary>Master switch. When false the completion watcher never arms.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Play a system sound when the agent finishes.</summary>
        public bool PlaySound { get; set; } = true;

        /// <summary>Show a Visual Studio info bar when the agent finishes.</summary>
        public bool ShowToast { get; set; } = true;

        /// <summary>
        /// Seconds the terminal must stay idle (no on-screen change) before a turn is
        /// considered complete. Guards against firing during brief pauses mid-turn.
        /// Clamped to 2–120 in the UI.
        /// </summary>
        public int IdleSeconds { get; set; } = 3;

        /// <summary>Action to run when the agent finishes.</summary>
        public AgentFinishActionType Action { get; set; } = AgentFinishActionType.None;

        /// <summary>
        /// Script path (for <see cref="AgentFinishActionType.RunScript"/>) or literal
        /// command text (for <see cref="AgentFinishActionType.SendToAgent"/>). Ignored
        /// by the built-in Visual Studio actions.
        /// </summary>
        public string ScriptOrCommand { get; set; } = string.Empty;

        /// <summary>
        /// When true, script windows opened by <see cref="AgentFinishActionType.RunScript"/>
        /// close automatically after the script finishes. When false, they stay open so
        /// the output can be read.
        /// </summary>
        public bool AutoCloseScript { get; set; } = false;

        /// <summary>
        /// When true, <see cref="AgentFinishActionType.Run"/> and
        /// <see cref="AgentFinishActionType.RunWithoutDebugging"/> clean the solution before launching.
        /// </summary>
        public bool CleanBeforeRun { get; set; } = true;

        /// <summary>
        /// When true, <see cref="AgentFinishActionType.Run"/> and
        /// <see cref="AgentFinishActionType.RunWithoutDebugging"/> rebuild the solution before launching.
        /// </summary>
        public bool RebuildBeforeRun { get; set; } = true;

        /// <summary>Only run the action when the agent actually changed files (git working tree dirty).</summary>
        public bool RequireFileChanges { get; set; } = false;

        /// <summary>
        /// When true, the action is offered as a button on the notification and runs
        /// only if the user clicks it. When false it runs automatically.
        /// </summary>
        public bool Confirm { get; set; } = true;
    }

    /// <summary>
    /// Summary metadata for a single Claude Code session loaded from a JSONL transcript
    /// under <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-uuid&gt;.jsonl</c>.
    /// Built in-memory by the session-history dialog, never persisted.
    /// </summary>
    public class SessionInfo
    {
        /// <summary>Session UUID (also the JSONL filename without extension).</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Absolute path to the JSONL transcript on disk.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>First user-typed message in the session, trimmed for the list preview.</summary>
        public string Preview { get; set; } = string.Empty;

        /// <summary>Count of user + assistant turns (skipping system/snapshot/attachment lines).</summary>
        public int MessageCount { get; set; }

        /// <summary>Sum of input + output tokens across all assistant messages.</summary>
        public int TokenCount { get; set; }

        /// <summary>File mtime — used to sort the list newest-first.</summary>
        public System.DateTime LastModified { get; set; }

        /// <summary>Working directory recorded in the transcript (the original cwd of the session).</summary>
        public string Cwd { get; set; } = string.Empty;

        /// <summary>Provider this session belongs to (Windows-native or WSL Claude Code).</summary>
        public AiProvider Provider { get; set; }
    }

    /// <summary>
    /// Represents a single prompt history entry with optional file attachments
    /// </summary>
    public class PromptHistoryEntry
    {
        /// <summary>
        /// The prompt text
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// File paths that were attached when the prompt was sent
        /// </summary>
        public System.Collections.Generic.List<string> FilePaths { get; set; } = new System.Collections.Generic.List<string>();
    }

    /// <summary>
    /// Settings configuration for Claude Code extension
    /// </summary>
    public class ClaudeCodeSettings
    {
        /// <summary>
        /// Captures any unknown JSON properties so that older DLL versions
        /// do not silently discard settings added by newer versions.
        /// </summary>
        [Newtonsoft.Json.JsonExtensionData]
        public System.Collections.Generic.IDictionary<string, Newtonsoft.Json.Linq.JToken> AdditionalData { get; set; }

        /// <summary>
        /// If true, Enter key sends the prompt (Shift+Enter / Ctrl+Enter for newline).
        /// If false, Enter inserts a newline and the Send button is shown to submit.
        /// </summary>
        public bool SendWithEnter { get; set; } = true;

        /// <summary>
        /// If true (and <see cref="SendWithEnter"/> is false), Ctrl+Enter sends the prompt
        /// while plain Enter inserts a newline. Lets users avoid accidentally sending an
        /// incomplete prompt with a stray Enter tap while still having a keyboard send shortcut.
        /// Ignored when <see cref="SendWithEnter"/> is true. See issue #70.
        /// </summary>
        public bool SendWithCtrlEnter { get; set; } = false;

        /// <summary>
        /// If true, prompts above ~1 KB are written to a temp file and only a file reference
        /// (`Prompt content saved to: &lt;path&gt;`) is pasted into the terminal. This avoids the
        /// conhost INPUT_RECORD buffer overflow that truncates the front of large pastes and
        /// preserves the `Files attached:` list which would otherwise fall off the front.
        /// If false, the prompt is pasted inline regardless of size (legacy behavior).
        /// See issue #48.
        /// </summary>
        public bool SendLargePromptsAsFile { get; set; } = false;

        /// <summary>
        /// If true, the clipboard is never used to send a prompt. The assembled prompt is always
        /// written to a temp file and only a short file reference is injected into the terminal via
        /// OS-level Unicode keystrokes (SendInput). For users whose clipboard is held by another app
        /// (clipboard managers, RDP redirection, security tooling) so paste-based sending fails.
        /// If false, the normal clipboard paste path is used. See issue #61.
        /// </summary>
        public bool DisableClipboardSend { get; set; } = false;

        /// <summary>
        /// When true, "Send Selection" only inserts the file reference (path + line numbers)
        /// without the code block. Default false.
        /// </summary>
        public bool SendSelectionReferenceOnly { get; set; } = false;

        /// <summary>
        /// Saved position of the grid splitter (in pixels)
        /// </summary>
        public double SplitterPosition { get; set; } = 236.0; // Default pixel height for first row

        /// <summary>
        /// Currently selected AI provider
        /// </summary>
        public AiProvider SelectedProvider { get; set; } = AiProvider.ClaudeCode;

        /// <summary>
        /// Which AI providers should be listed in the agent selection menu.
        /// Defaults to Claude Code only so the menu stays short out-of-the-box.
        /// The currently selected provider is always shown in the menu regardless
        /// of this list, so users who had a different provider configured before
        /// upgrading don't lose access to it.
        /// </summary>
        public System.Collections.Generic.List<AiProvider> VisibleProviders { get; set; }
            = new System.Collections.Generic.List<AiProvider> { AiProvider.ClaudeCode };

        /// <summary>
        /// Currently selected Claude model (for Claude Code and Claude Code WSL providers)
        /// </summary>
        public ClaudeModel SelectedClaudeModel { get; set; } = ClaudeModel.Sonnet;

        /// <summary>
        /// Currently selected Windsurf model
        /// </summary>
        public WindsurfModel SelectedWindsurfModel { get; set; } = WindsurfModel.ClaudeSonnet;

        /// <summary>
        /// List of previously sent prompts with optional file attachments (most recent last)
        /// </summary>
        public System.Collections.Generic.List<PromptHistoryEntry> PromptHistory { get; set; } = new System.Collections.Generic.List<PromptHistoryEntry>();

        /// <summary>
        /// If true, automatically opens the Changes view, expands files, and enables auto-scroll when a prompt is sent
        /// Only applies when the project is in a git repository
        /// </summary>
        public bool AutoOpenChangesOnPrompt { get; set; } = false;

        /// <summary>
        /// If true, starts Claude Code with the --dangerously-skip-permissions parameter
        /// Applies to Claude Code (Windows) and Claude Code (WSL)
        /// </summary>
        public bool ClaudeDangerouslySkipPermissions { get; set; } = false;

        /// <summary>
        /// Legacy compatibility toggle for Codex startup automation.
        /// If true, starts Codex with --ask-for-approval never.
        /// Applies to Codex (Windows native) and Codex (WSL).
        /// </summary>
        public bool CodexFullAuto { get; set; } = false;

        /// <summary>
        /// If true, starts Windsurf with --permission-mode dangerous.
        /// Applies to Windsurf (WSL).
        /// </summary>
        public bool WindsurfDangerousMode { get; set; } = false;

        /// <summary>
        /// If true, starts Antigravity with the --dangerously-skip-permissions parameter
        /// Applies to Antigravity (Windows native).
        /// </summary>
        public bool AntigravityDangerouslySkipPermissions { get; set; } = false;

        /// <summary>
        /// If true, starts Cursor Agent with --yolo to skip all approvals.
        /// Applies to Cursor Agent (Windows native) and Cursor Agent (WSL).
        /// </summary>
        public bool CursorAgentAutoRun { get; set; } = false;

        /// <summary>
        /// Currently selected effort level for Claude Code
        /// </summary>
        public EffortLevel SelectedEffortLevel { get; set; } = EffortLevel.Auto;

        /// <summary>
        /// Custom working directory for the terminal.
        /// Can be an absolute path or a path relative to the solution directory.
        /// When empty or null, the default solution/project directory is used.
        /// </summary>
        public string CustomWorkingDirectory { get; set; } = "";

        /// <summary>
        /// Per-provider custom CLI executable paths, keyed by <see cref="AiProvider"/>.
        /// When an entry is present and non-empty, it overrides the default executable
        /// detection/launch for that provider (instead of relying on PATH or the built-in
        /// native install location). Native providers expect a full Windows path
        /// (e.g. C:\Tools\claude.exe); WSL providers expect a Linux path or command
        /// (e.g. /home/user/.local/bin/claude). Empty/missing entries fall back to the
        /// default behavior.
        /// </summary>
        public System.Collections.Generic.Dictionary<AiProvider, string> CustomExecutablePaths { get; set; }
            = new System.Collections.Generic.Dictionary<AiProvider, string>();

        /// <summary>
        /// Terminal emulator to use (Command Prompt or Windows Terminal)
        /// Defaults to Command Prompt for compatibility
        /// </summary>
        public TerminalType SelectedTerminalType { get; set; } = TerminalType.CommandPrompt;

        /// <summary>
        /// Whether the terminal is currently detached into a separate tool window tab
        /// </summary>
        public bool IsTerminalDetached { get; set; } = false;

        /// <summary>
        /// Font size for the prompt text box (in WPF device-independent units, range 8–24).
        /// 0 means "use VS default" (not yet changed by user).
        /// </summary>
        public double PromptFontSize { get; set; } = 0.0;

        /// <summary>
        /// Net zoom delta applied by the user to the embedded terminal via Ctrl+Scroll.
        /// Positive = zoomed in, negative = zoomed out. Replayed on each terminal restart.
        /// </summary>
        public int TerminalZoomDelta { get; set; } = 0;

        /// <summary>
        /// When true, the extension skips the automatic terminal zoom-out and saved
        /// zoom-delta replay that runs after each terminal start. Manual Ctrl+Scroll
        /// zoom remains available. Useful on high-DPI displays where the default
        /// auto-zoom produces fonts that are too small, or to avoid the brief input
        /// freeze caused by the synthesized keystrokes during startup.
        /// </summary>
        public bool DisableStartupAutoZoom { get; set; } = false;

        /// <summary>
        /// If true, the layout is inverted, swapping the prompt and terminal slots
        /// within the active orientation. For a Horizontal split this puts the
        /// terminal on top and the prompt on the bottom; for a Vertical split it
        /// puts the terminal on the left and the prompt on the right.
        /// Default is false (prompt first: top, or left).
        /// </summary>
        public bool InvertLayout { get; set; } = false;

        /// <summary>
        /// Whether the prompt panel and terminal are stacked (Horizontal, top/bottom —
        /// the classic layout) or placed side by side (Vertical, left/right).
        /// Combined with <see cref="InvertLayout"/> this determines whether the prompt
        /// panel sits on the Top, Bottom, Left, or Right.
        /// </summary>
        public LayoutOrientation SelectedLayoutOrientation { get; set; } = LayoutOrientation.Horizontal;

        /// <summary>
        /// Theme preference for the terminal panel.
        /// Automatic = follow VS IDE theme (default), Dark = always dark, Light = always light.
        /// </summary>
        public ThemePreference SelectedThemePreference { get; set; } = ThemePreference.Automatic;

        /// <summary>
        /// ARGB value of the background color used when
        /// <see cref="SelectedThemePreference"/> is <see cref="ThemePreference.Custom"/>.
        /// Defaults to #F4ECFF (a light lavender). Text color is derived
        /// automatically from this color's brightness.
        /// </summary>
        public int CustomThemeColorArgb { get; set; } = unchecked((int)0xFFF4ECFF);

        /// <summary>
        /// ARGB value of the terminal panel color the AI agent was last
        /// launched with. Persisted so the "Theme changed -- restart agent?"
        /// prompt can be skipped when the new color matches what the agent
        /// already has. 0 = not yet set.
        /// </summary>
        public int LastAgentTerminalColorArgb { get; set; } = 0;

        /// <summary>
        /// When true, the "Theme changed. Restart the AI code agent?" prompt
        /// is suppressed entirely. Useful for users who automatically swap
        /// themes mid-session (e.g. VS debugging theme on F5) and do not want
        /// to be asked every time.
        /// </summary>
        public bool SkipThemeRestartPrompt { get; set; } = false;

        /// <summary>
        /// User-defined custom commands surfaced in the toolbar custom-commands
        /// dropdown. Empty list hides the dropdown button entirely.
        /// </summary>
        public System.Collections.Generic.List<CustomCommand> CustomCommands { get; set; } = new System.Collections.Generic.List<CustomCommand>();

        /// <summary>
        /// Global default configuration for the "On Agent Finish" notification +
        /// action feature. Used by any solution that has no per-project override.
        /// See ClaudeCodeControl.AgentCompletion.cs.
        /// </summary>
        public AgentFinishConfig AgentFinish { get; set; } = new AgentFinishConfig();

        /// <summary>
        /// Per-solution "On Agent Finish" overrides, keyed by solution name
        /// (the .sln file name without extension). When the current solution name
        /// has an entry here it takes precedence over <see cref="AgentFinish"/>;
        /// otherwise the global default is used.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, AgentFinishConfig> ProjectAgentFinish { get; set; }
            = new System.Collections.Generic.Dictionary<string, AgentFinishConfig>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Auto-refresh interval (seconds) for the Claude usage tool window's
        /// embedded WebView2. 0 = manual refresh only.
        /// </summary>
        public int UsageAutoRefreshSeconds { get; set; } = 0;

        /// <summary>
        /// Persisted across sessions: true when the user had the Claude usage
        /// tool window open at last shutdown. Used to auto-reopen it on the
        /// next solution load.
        /// </summary>
        public bool UsageWindowOpened { get; set; } = false;

        /// <summary>
        /// If true, the inline mini usage bars are shown in the prompt panel
        /// when usage data has been successfully scraped. Hidden silently
        /// when scraping fails or the user is not signed in.
        /// </summary>
        public bool ShowInlineUsageBars { get; set; } = true;

        /// <summary>
        /// Last successfully scraped inline usage payload (JSON serialized
        /// <see cref="UsageSnapshot"/>). Restored on startup so the bars
        /// render immediately with stale data while a fresh fetch runs.
        /// </summary>
        public string LastUsageJson { get; set; } = "";

        /// <summary>
        /// Timestamp (UTC ISO 8601) of the last successful usage scrape.
        /// </summary>
        public string LastUsageTimestamp { get; set; } = "";
    }

    /// <summary>
    /// Inline usage data scraped from claude.ai/settings/usage.
    /// Labels and reset texts are kept verbatim from the page so the original
    /// localization (Portuguese, English, etc.) is preserved in the UI.
    /// </summary>
    public class UsageSnapshot
    {
        /// <summary>"Sessão atual" / "Current session" — verbatim label from claude.ai.</summary>
        public string SessionLabel { get; set; } = "";

        /// <summary>"Reinicia em 2 h 36 min" / "Resets in ..." — verbatim text from claude.ai.</summary>
        public string SessionReset { get; set; } = "";

        /// <summary>Session usage percentage (0-100), parsed from aria-valuenow.</summary>
        public int SessionPercent { get; set; }

        /// <summary>"Todos os modelos" / "All models" — verbatim label from claude.ai.</summary>
        public string WeeklyLabel { get; set; } = "";

        /// <summary>"Reinicia ter., 20:00" / "Resets ..." — verbatim text from claude.ai.</summary>
        public string WeeklyReset { get; set; } = "";

        /// <summary>Weekly usage percentage (0-100), parsed from aria-valuenow.</summary>
        public int WeeklyPercent { get; set; }

        /// <summary>true when extra-usage billing is enabled and the section was found on the page.</summary>
        public bool HasExtraUsage { get; set; }

        /// <summary>"R$110.71 spent" — verbatim label from the extra-usage row.</summary>
        public string ExtraUsageSpent { get; set; } = "";

        /// <summary>"Resets May 1" — verbatim reset text from the extra-usage row.</summary>
        public string ExtraUsageReset { get; set; } = "";

        /// <summary>Extra usage percentage parsed from the "X% used" text. May exceed 100.</summary>
        public int ExtraUsagePercent { get; set; }
    }
}
