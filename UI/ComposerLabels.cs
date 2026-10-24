/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Short captions for the native-mode composer selectors when the chat tab is too narrow for the full ones
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Shrinks the agent / model / effort / permission captions so the composer's action row keeps
    /// fitting on one line in a narrow chat tab. Pure string work, kept out of the code-behind so the
    /// shortening rules can be unit-tested (<c>Tests/ComposerLabelTests.cs</c>).
    /// </summary>
    public static class ComposerLabels
    {
        /// <summary>Caption width, in characters, for the middle ("compact") density tier.</summary>
        public const int CompactMaxChars = 14;

        /// <summary>
        /// Caption width for the tightest tier. Ten rather than a rounder eight so the WSL variants
        /// ("Claude WSL") still name the platform instead of ending in an ellipsis — telling the WSL
        /// agent from the Windows one is the whole point of that suffix.
        /// </summary>
        public const int TightMaxChars = 10;

        /// <summary>
        /// Hand-picked replacements. Anything not listed here falls through to plain truncation, which
        /// is fine for model ids (the tooltip carries the full name) but would read badly for the
        /// captions users see most often — "Skip permissions" truncated is "Skip permis…", not "Skip".
        /// </summary>
        private static readonly Dictionary<string, string> ShortNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Providers
            { "Claude Code", "Claude" },
            { "Cursor Agent", "Cursor" },
            { "Open Code", "OpenCode" },
            { "Antigravity", "Antigrav" },

            // Permissions
            // (N marks the short forms for translation; TryGetShortName translates them when shown.)
            { "Skip permissions", Loc.N("Skip") },
            { "Ask permission", Loc.N("Ask") },
            { "Plan mode", Loc.N("Plan") },
            { "Auto mode", Loc.N("Auto") },
            { "Accept edits", Loc.N("Accept") },
            { "Manual mode", Loc.N("Manual") },

            // Effort / reasoning
            { "Extra High", "XHigh" },
            { "Ultracode", "Ultra" },
            { "Medium", "Med" },

            // Models
            { "Opus Plan", "OpusPlan" }
        };

        /// <summary>
        /// Returns <paramref name="label"/> shortened to at most <paramref name="maxChars"/> characters.
        /// The known-name table is applied first (so "Claude Code" becomes "Claude" even when it would
        /// have fit), then the WSL/native provider suffixes are folded, and only what is still too long
        /// is truncated with an ellipsis.
        /// </summary>
        public static string Shorten(string label, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return label;
            }

            string text = label.Trim();
            string suffix = string.Empty;

            // "(native)" only exists to separate an agent from its own WSL twin, so it carries no
            // information once the WSL one is spelled out — dropping it is free. "(WSL)" does carry
            // information, so it is kept, just without the parentheses.
            string nativeSuffix = Loc.L(" (native)");
            if (text.EndsWith(" (WSL)", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - " (WSL)".Length);
                suffix = " WSL";
            }
            else if (text.EndsWith(" (native)", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - " (native)".Length);
            }
            else if (text.EndsWith(nativeSuffix, StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - nativeSuffix.Length);
            }

            string mapped;
            if (TryGetShortName(text, out mapped))
            {
                text = mapped;
            }

            text += suffix;

            if (maxChars > 1 && text.Length > maxChars)
            {
                text = text.Substring(0, maxChars - 1).TrimEnd() + "…";
            }

            return text;
        }

        /// <summary>
        /// The short form of a caption, matched against the English name or its translation (the
        /// composer shows translated captions), and returned translated as well.
        /// </summary>
        private static bool TryGetShortName(string text, out string shortName)
        {
            if (ShortNames.TryGetValue(text, out shortName))
            {
                shortName = Loc.L(shortName);
                return true;
            }

            foreach (KeyValuePair<string, string> entry in ShortNames)
            {
                if (string.Equals(Loc.L(entry.Key), text, StringComparison.Ordinal))
                {
                    shortName = Loc.L(entry.Value);
                    return true;
                }
            }

            shortName = null;
            return false;
        }
    }
}
