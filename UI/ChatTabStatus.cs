/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native chat tab caption: the status glyph in front of it (working, waiting for the user, done,
 *          error) and the automatic title taken from the first prompt. Pure, unit-tested.
 *
 * *******************************************************************************************************************/

using System.Text.RegularExpressions;

namespace ClaudeCodeVS.UI
{
    /// <summary>What a chat session is doing, as shown in front of its tab caption.</summary>
    public enum ChatTabStatus
    {
        /// <summary>No turn has run yet: no glyph.</summary>
        None,

        /// <summary>A turn is running (thinking, writing, using tools).</summary>
        Working,

        /// <summary>The turn is blocked on a question, a permission or a plan review.</summary>
        WaitingForUser,

        /// <summary>The last turn ended; the agent is ready for the next prompt.</summary>
        Done,

        /// <summary>The send failed or the agent reported an error.</summary>
        Error
    }

    public static class ChatTabCaption
    {
        /// <summary>Longest automatic title, in characters, before it is cut at a word boundary.</summary>
        public const int MaxAutoTitleChars = 40;

        /// <summary>
        /// The glyph shown in front of the caption, empty for <see cref="ChatTabStatus.None"/>. "✳" for a
        /// finished turn is the same mark Claude Code puts in a terminal's title when it is idle.
        /// </summary>
        public static string Glyph(ChatTabStatus status)
        {
            switch (status)
            {
                case ChatTabStatus.Working: return "⏳";
                case ChatTabStatus.WaitingForUser: return "❓";
                case ChatTabStatus.Done: return "✳";
                case ChatTabStatus.Error: return "❌";
                default: return string.Empty;
            }
        }

        /// <summary>The caption with its status glyph in front, or unchanged when the glyph is off or empty.</summary>
        public static string WithStatus(string caption, ChatTabStatus status, bool showStatus)
        {
            string glyph = showStatus ? Glyph(status) : string.Empty;
            return glyph.Length == 0 ? caption : glyph + " " + caption;
        }

        /// <summary>
        /// A tab title from the user's prompt: its first non-empty line with whitespace collapsed and
        /// markdown/quote marks trimmed, cut at a word boundary to <paramref name="maxChars"/> with "…".
        /// Empty when the prompt has no text.
        /// </summary>
        public static string TitleFromPrompt(string prompt, int maxChars = MaxAutoTitleChars)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return string.Empty;
            }

            string firstLine = string.Empty;
            foreach (string line in prompt.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    firstLine = line;
                    break;
                }
            }

            string text = Regex.Replace(firstLine, @"\s+", " ").Trim().Trim('#', '*', '>', '"', '\'', '`', ' ');
            return Shorten(text, maxChars);
        }

        /// <summary>Cuts <paramref name="text"/> at the last space before <paramref name="maxChars"/>, adding "…".</summary>
        public static string Shorten(string text, int maxChars = MaxAutoTitleChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            string cut = text.Substring(0, maxChars);
            int space = cut.LastIndexOf(' ');
            if (space >= maxChars / 2)
            {
                cut = cut.Substring(0, space);
            }

            return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
        }
    }
}
