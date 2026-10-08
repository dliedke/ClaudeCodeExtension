/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: Detects a native child window (the Claude Usage WebView2) that no longer sits where WPF
 *          laid it out. A WebView2 is a real HWND, so WPF clipping and z-order do not apply to it: when
 *          Visual Studio resizes the docked tool window and the HWND keeps its old size, the browser
 *          paints over whatever is docked next to it. This is the pure comparison; the Win32 reads and
 *          the repair live in ClaudeUsageControl.
 * ***********************/

using System;

namespace ClaudeCodeVS.UI
{
    /// <summary>A rectangle in whole device pixels, as edges (the same shape as a Win32 RECT).</summary>
    public struct PixelRect
    {
        public PixelRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    public static class HostedWindowBounds
    {
        /// <summary>
        /// Slack, in pixels, before a difference counts. WPF rounds fractional layout positions and
        /// per-monitor DPI scaling can land a window one pixel off its element without anything being
        /// wrong; a stale window is off by whole rows or columns.
        /// </summary>
        public const int DefaultTolerance = 2;

        /// <summary>
        /// True when the hosted window's rectangle differs from where WPF placed its element by more
        /// than <paramref name="tolerance"/> on any edge. An expected rectangle with no area (the
        /// element has not been laid out yet, or is collapsed) never counts, so a window that is
        /// legitimately about to be positioned is not "repaired".
        /// </summary>
        public static bool IsMisaligned(PixelRect actual, PixelRect expected, int tolerance = DefaultTolerance)
        {
            if (expected.Width <= 0 || expected.Height <= 0) return false;

            return Math.Abs(actual.Left - expected.Left) > tolerance
                || Math.Abs(actual.Top - expected.Top) > tolerance
                || Math.Abs(actual.Right - expected.Right) > tolerance
                || Math.Abs(actual.Bottom - expected.Bottom) > tolerance;
        }
    }
}
