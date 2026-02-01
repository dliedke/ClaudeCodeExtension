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
        CursorAgent,
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
    /// Settings configuration for Claude Code extension
    /// </summary>
    public class ClaudeCodeSettings
    {
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
        /// List of previously sent prompts (most recent last)
        /// </summary>
        public System.Collections.Generic.List<string> PromptHistory { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>
        /// If true, automatically opens the Changes view, expands files, and enables auto-scroll when a prompt is sent
        /// Only applies when the project is in a git repository
        /// </summary>
        public bool AutoOpenChangesOnPrompt { get; set; } = false;
    }
}