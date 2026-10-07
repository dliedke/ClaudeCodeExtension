/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Recognizes the answer Claude Code gives to its /color command ("Session color set to: blue" /
 *          "Session color reset to default") so native mode can carry the color over to the chat header,
 *          and maps the CLI's color names to hex. Pure (no WPF, no VS SDK) so it is unit-testable.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClaudeCodeVS.Agents
{
    internal static class ClaudeSessionColor
    {
        private static readonly Regex SetNotice = new Regex(
            @"^Session color set to:\s*([A-Za-z]+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ResetNotice = new Regex(
            @"^Session color reset to default\.?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The CLI's eight named colors, picked to read on both the dark and light chat themes.
        private static readonly Dictionary<string, string> Hex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "red", "#E5534B" },
            { "blue", "#3B82F6" },
            { "green", "#3FB950" },
            { "yellow", "#E3B341" },
            { "purple", "#A371F7" },
            { "orange", "#F0883E" },
            { "pink", "#F778BA" },
            { "cyan", "#39C5CF" }
        };

        /// <summary>
        /// True when <paramref name="text"/> is, as a whole, the CLI's answer to /color. <paramref name="hex"/> is
        /// the color to apply, or empty for "back to default". Whole-message match only, so an assistant
        /// reply that merely quotes the sentence never changes the color.
        /// </summary>
        public static bool TryParseNotice(string text, out string hex)
        {
            hex = string.Empty;
            string trimmed = (text ?? string.Empty).Trim();

            if (ResetNotice.IsMatch(trimmed))
            {
                return true;
            }

            Match set = SetNotice.Match(trimmed);
            return set.Success && Hex.TryGetValue(set.Groups[1].Value, out hex);
        }
    }
}
