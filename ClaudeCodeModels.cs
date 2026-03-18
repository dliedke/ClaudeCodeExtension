/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
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
    /// AI Provider types supported by the extension
    /// </summary>
    public enum AiProvider
    {
        ClaudeCode,
        ClaudeCodeWSL,
        Codex,
        CodexNative,
        CursorAgent,
        CursorAgentNative,
        QwenCode,
        OpenCode
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
        /// If true, Enter key sends the prompt (Shift+Enter for newline)
        /// If false, Enter key creates newline (button click sends prompt)
        /// </summary>
        public bool SendWithEnter { get; set; } = true;

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
        /// If true, starts Codex with the --full-auto parameter
        /// Applies to Codex (Windows native) and Codex (WSL)
        /// </summary>
        public bool CodexFullAuto { get; set; } = false;

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
    }
}
