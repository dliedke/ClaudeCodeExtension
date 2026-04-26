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
        Windsurf = 8
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
        /// Saved position of the grid splitter (in pixels)
        /// </summary>
        public double SplitterPosition { get; set; } = 236.0; // Default pixel height for first row

        /// <summary>
        /// Currently selected AI provider
        /// </summary>
        public AiProvider SelectedProvider { get; set; } = AiProvider.ClaudeCode;

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
        /// If true, the layout is inverted: terminal on top, prompt on bottom.
        /// Default is false (prompt on top, terminal on bottom).
        /// </summary>
        public bool InvertLayout { get; set; } = false;

        /// <summary>
        /// User-defined custom commands surfaced in the toolbar custom-commands
        /// dropdown. Empty list hides the dropdown button entirely.
        /// </summary>
        public System.Collections.Generic.List<CustomCommand> CustomCommands { get; set; } = new System.Collections.Generic.List<CustomCommand>();

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
    }
}
