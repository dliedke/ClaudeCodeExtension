/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Display values and ring geometry for the native-mode composer's usage button and its popup (pure, unit-tested)
 *
 * *******************************************************************************************************************/

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Everything the composer's usage popup shows, already reduced to display values. The parent control
    /// fills it from the chat's own status line (context) and the cached Claude Usage snapshot (plan limits).
    /// </summary>
    public sealed class ChatUsageIndicatorData
    {
        /// <summary>false hides the button: only Claude providers have a context window and plan limits.</summary>
        public bool Visible { get; set; }

        /// <summary>false until a turn has reported the context window (none before the first turn, or after Clear Chat).</summary>
        public bool HasContext { get; set; }
        public long ContextTokens { get; set; }
        public int ContextWindow { get; set; }
        public int ContextPercent { get; set; }

        /// <summary>false until the Claude Usage tab has produced a snapshot.</summary>
        public bool HasPlanUsage { get; set; }
        public string SessionLabel { get; set; } = string.Empty;
        public int SessionPercent { get; set; }
        public string SessionReset { get; set; } = string.Empty;
        public bool ShowWeekly { get; set; }
        public int WeeklyPercent { get; set; }
        public string WeeklyReset { get; set; } = string.Empty;
        public bool ShowExtra { get; set; }
        public int ExtraPercent { get; set; }
        public string ExtraSpent { get; set; } = string.Empty;
        public string ExtraReset { get; set; } = string.Empty;

        /// <summary>"Updated 2:15 PM" or "Not updated since 2:15 PM"; empty when there is no snapshot.</summary>
        public string UpdatedText { get; set; } = string.Empty;
    }

    /// <summary>Pure formatting and geometry behind the usage button; no WPF elements are created here.</summary>
    public static class ChatUsageIndicator
    {
        /// <summary>"413.9k", "1M", "1.2M", "950" — the figures the status line gives, shortened the way the CLI shows them.</summary>
        public static string FormatTokens(long tokens)
        {
            if (tokens < 0) tokens = 0;

            // From 999,950 up the thousands would round to "1000k", so the million unit takes over there.
            if (tokens >= 999950)
            {
                return Math.Round(tokens / 1000000.0, 1).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            }

            if (tokens >= 1000)
            {
                return Math.Round(tokens / 1000.0, 1).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            }

            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Share of the window in use, 0-100. Input-side tokens only, as the CLI computes it. 0 when the window is unknown.</summary>
        public static int ContextPercent(long tokens, int window)
        {
            if (window <= 0 || tokens <= 0) return 0;

            double percent = Math.Min(100.0, tokens * 100.0 / window);
            return (int)Math.Round(percent, MidpointRounding.AwayFromZero);
        }

        /// <summary>"413.9k / 1M (41%)"; just the token count when the window size is unknown.</summary>
        public static string ContextLine(long tokens, int window)
        {
            if (window <= 0)
            {
                return FormatTokens(tokens);
            }

            return FormatTokens(tokens) + " / " + FormatTokens(window) + " (" + ContextPercent(tokens, window) + "%)";
        }

        /// <summary>Clamps a percentage to 0-100 (the usage page can report more for extra usage, which has its own bar).</summary>
        public static int ClampPercent(int value)
        {
            return value < 0 ? 0 : (value > 100 ? 100 : value);
        }

        /// <summary>"41% used · Resets in 2 h"; blank details are skipped, so a missing reset leaves just the percentage.</summary>
        public static string PlanRowText(int percent, params string[] details)
        {
            string text = ClampPercent(percent).ToString(CultureInfo.InvariantCulture) + "% used";
            if (details == null)
            {
                return text;
            }

            foreach (string detail in details)
            {
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    text += " · " + detail.Trim();
                }
            }

            return text;
        }

        /// <summary>
        /// The ring's arc for <paramref name="fraction"/> (0-1) of a circle centred on
        /// (<paramref name="center"/>, <paramref name="center"/>), starting at 12 o'clock and running clockwise.
        /// Null when there is nothing to draw. A full circle stops just short of closing: an arc whose start and
        /// end coincide is not drawn at all.
        /// </summary>
        public static PathGeometry RingArc(double fraction, double center, double radius)
        {
            if (double.IsNaN(fraction) || fraction <= 0) return null;

            double sweep = Math.Min(fraction, 0.9999);
            var figure = new PathFigure
            {
                StartPoint = new Point(center, center - radius),
                IsClosed = false
            };
            figure.Segments.Add(new ArcSegment(
                ArcPoint(sweep, center, radius),
                new Size(radius, radius),
                0,
                sweep > 0.5,
                SweepDirection.Clockwise,
                true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        /// <summary>The point <paramref name="fraction"/> of the way round a clockwise circle that starts at 12 o'clock.</summary>
        public static Point ArcPoint(double fraction, double center, double radius)
        {
            double angle = 2 * Math.PI * fraction;
            return new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        }
    }
}
