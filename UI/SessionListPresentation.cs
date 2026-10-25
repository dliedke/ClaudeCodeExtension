/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: How a past conversation reads in Session History and the welcome card's recent list: cleaned first-prompt preview, date group (pure)
 *
 * *******************************************************************************************************************/

using System;
using System.Text;

namespace ClaudeCodeVS.UI
{
    /// <summary>Date bands Session History groups its rows under when sorted by date.</summary>
    public enum SessionDateGroup
    {
        Today,
        Yesterday,
        Last7Days,
        Last30Days,
        Older
    }

    /// <summary>
    /// Pure helpers behind the session lists. Kept out of the dialog code so the rules can be unit
    /// tested (<c>Tests/SessionListPresentationTests.cs</c>).
    /// </summary>
    public static class SessionListPresentation
    {
        /// <summary>Longest preview shown for an untitled session.</summary>
        public const int PreviewMaxChars = 120;

        /// <summary>
        /// Opening words of the block "Pull before sending" puts in front of a prompt when the pull left
        /// conflicts (<c>BuildConflictPromptBlock</c>). The block ends with a <c>---</c> line when the
        /// user's own request follows it.
        /// </summary>
        internal const string AutoPullPreamble = "A `git pull` ran automatically before this message";

        /// <summary>
        /// Opening of the note "Track agent changes for review" puts in front of a prompt after an Undo
        /// (<c>PendingReviewMessages.BuildUndoneFilesNote</c>). It ends with <c>]</c> and a blank line.
        /// </summary>
        internal const string IdeNotePrefix = "[Note from the IDE:";

        /// <summary>
        /// What the user actually typed in a prompt the extension sent: the pull-conflict block, the
        /// review Undo note and the "Files attached:" list are dropped, whitespace is collapsed to single
        /// spaces and the result is cut at <see cref="PreviewMaxChars"/>. Empty when nothing typed is
        /// left (a prompt that was only attachments or only the conflict hand-off), so the caller can
        /// fall through to the next message.
        /// </summary>
        public static string CleanPromptPreview(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return string.Empty;
            }

            string text = prompt.Replace("\r\n", "\n").TrimStart();

            if (text.StartsWith(AutoPullPreamble, StringComparison.Ordinal))
            {
                text = AfterSeparatorLine(text);
            }

            if (text.StartsWith(IdeNotePrefix, StringComparison.Ordinal))
            {
                int end = text.IndexOf("]\n", StringComparison.Ordinal);
                text = end >= 0 ? text.Substring(end + 2).TrimStart() : string.Empty;
            }

            text = PromptAttachments.Split(text).Body;

            return Truncate(CollapseWhitespace(text), PreviewMaxChars);
        }

        /// <summary>
        /// Band of <paramref name="lastModified"/> seen from <paramref name="now"/>, by calendar day:
        /// today, yesterday, then the rest of the last 7 and 30 days. A time in the future (clock skew)
        /// counts as today.
        /// </summary>
        public static SessionDateGroup GetDateGroup(DateTime lastModified, DateTime now)
        {
            int days = (int)(now.Date - lastModified.Date).TotalDays;

            if (days <= 0) return SessionDateGroup.Today;
            if (days == 1) return SessionDateGroup.Yesterday;
            if (days < 7) return SessionDateGroup.Last7Days;
            if (days < 30) return SessionDateGroup.Last30Days;
            return SessionDateGroup.Older;
        }

        /// <summary>Everything after the first line that is exactly <c>---</c>, or empty when there is none.</summary>
        private static string AfterSeparatorLine(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    return string.Join("\n", lines, i + 1, lines.Length - i - 1).TrimStart();
                }
            }

            return string.Empty;
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool pendingSpace = false;

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static string Truncate(string text, int maxChars)
        {
            return text.Length > maxChars ? text.Substring(0, maxChars).TrimEnd() + "…" : text;
        }
    }
}
