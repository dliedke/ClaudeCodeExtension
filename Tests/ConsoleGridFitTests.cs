/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the half of the display-change repair that works on the console grid rather than
 *          on window pixels: recovering the cell size the host actually paints with, and fitting the
 *          console viewport back onto the window it lives in.
 *
 *          Both come from measured Remote Desktop reconnects (terminal-launch.log). The viewport:
 *          the panel passed through 829x70 px while Visual Studio re-laid it out, conhost followed it
 *          down to a 6-row viewport and stayed there when the panel came back at 829x623 px - a fifth
 *          of the panel painted, no prompt box anywhere, and the display breaking up on every scroll.
 *          The cell size: the host reports it in the units of the DPI the console was created at, so
 *          6x13 px is painted 12x26 on a console started at 200%. Taking the reported number at face
 *          value skipped the rescale the next reconnect needed and left the font twice too large.
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ConsoleGridFitTests
    {
        #region Reported cell size vs painted cell size

        /// <summary>
        /// The measured discrepancy, and the reason the first cut of this guard skipped a rescale the
        /// reconnect needed: a console created on a 192 DPI session reports a 6x13 px cell and paints
        /// 12x26. Confirmed twice in the same log - a 1658 px wide window holding 135 columns
        /// (135 x 12 px), and the 1654x1014 px window conhost gave itself for a 135x39 grid.
        /// dwMaximumWindowSize is what gives it away: 39 rows of a 1080 px screen is 27 px of pitch
        /// against a reported 13, so the host is painting at 200% and the cell is 12x26.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_RecoversTheSizeAHostVirtualizedAt200PercentPaints()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(
                reportedCellWidthPx: 6, reportedCellHeightPx: 13,
                maxViewRows: 39, bufferRows: 9001, screenHeightPx: 1080,
                clientWidthPx: 0, viewCols: 0,
                out int paintedWidthPx, out int paintedHeightPx, out bool measured);

            Assert.AreEqual(26, paintedHeightPx, "Twice the reported 13 - not the 27 the raw division suggests.");
            Assert.AreEqual(12, paintedWidthPx);
            Assert.IsTrue(measured, "The host maximum was usable, so this is a measurement.");
        }

        /// <summary>
        /// Why the raw division is not used as the size: dwMaximumWindowSize has window chrome deducted,
        /// so it reads a few percent high, and at 4K that difference is a visible strip of panel. The
        /// measured case (log 21:01:00): 86 rows of a 2160 px screen is 25.1 px of pitch against a
        /// reported 12, and taking 25 as the cell gave 49 rows where conhost itself had laid out 51 -
        /// two rows of the 1245 px panel left blank. Rounding the ratio to a quarter step - Windows
        /// scales in no other steps - lands on exactly 24 and agrees with the host.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_RoundsTheScaleRatherThanTheSize()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(6, 12, 86, 9001, 2160, 0, 0,
                                                         out int paintedWidthPx, out int paintedHeightPx, out bool _);

            Assert.AreEqual(24, paintedHeightPx, "1245 px over 24 is the 51 rows conhost had; over 25 it is 49.");
            Assert.AreEqual(12, paintedWidthPx);
        }

        /// <summary>
        /// 150% is a scale Windows offers and a whole-number ratio would miss, so the rounding is to
        /// the quarter and not to the integer.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_HandlesTheQuarterScales()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(8, 16, 44, 9001, 1080, 0, 0,
                                                         out int paintedWidthPx, out int paintedHeightPx, out bool _);

            Assert.AreEqual(24, paintedHeightPx, "1080/44 = 24.5 px of pitch against a reported 16 is 150%.");
            Assert.AreEqual(12, paintedWidthPx);
        }

        /// <summary>
        /// After the rescale has halved the reported cell the host reports more rows as its maximum,
        /// and the estimate follows - which is what keeps the fit from asking for twice the rows the
        /// panel can show.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_FollowsTheHostAfterARescale()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(3, 7, 77, 9001, 1080, 0, 0,
                                                         out int paintedWidthPx, out int paintedHeightPx, out bool _);

            Assert.AreEqual(14, paintedHeightPx, "A 3x7 px cell on a 192 DPI console is painted 6x14.");
            Assert.AreEqual(6, paintedWidthPx);
        }

        /// <summary>
        /// A console with no scaling in play has to come out unchanged, or the estimate would be a
        /// second source of wrong cell sizes on every ordinary machine.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_LeavesAnUnscaledHostAlone()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(6, 13, 83, 9001, 1080, 0, 0,
                                                         out int paintedWidthPx, out int paintedHeightPx, out bool _);

            Assert.AreEqual(13, paintedHeightPx);
            Assert.AreEqual(6, paintedWidthPx);
        }

        /// <summary>
        /// The alternate screen buffer - where every full-screen agent UI runs - is the case the host
        /// maximum cannot answer: the buffer IS the viewport, so the host caps its maximum by the
        /// buffer and the division yields the grid instead of the scale. The window is the second
        /// source, and it survives the collapse this file repairs: rows collapse, columns do not.
        /// The measured reconnect again - 1658 px of client holding 135 columns is 12 px of painted
        /// pitch against a reported 6, so the host paints at 200% and the cell is 12x26.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_MeasuresOffTheWindowWhenTheMaximumCannotAnswer()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(
                reportedCellWidthPx: 6, reportedCellHeightPx: 13,
                maxViewRows: 40, bufferRows: 40, screenHeightPx: 1080,
                clientWidthPx: 1658, viewCols: 135,
                out int paintedWidthPx, out int paintedHeightPx, out bool measured);

            Assert.AreEqual(26, paintedHeightPx, "135 columns across 1658 px is a 12 px painted cell, twice the reported 6.");
            Assert.AreEqual(12, paintedWidthPx);
            Assert.IsTrue(measured, "The window is a measurement, not an assumption.");
        }

        /// <summary>
        /// The collapsed viewport itself: 6 rows over a 623 px panel, in the alternate buffer. The
        /// rows say nothing about the cell any more - which is why the estimate is taken off the
        /// columns - and the answer has to be the same 12x26 as before the collapse, or the fit that
        /// follows asks for twice the columns the panel can show.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_SurvivesACollapsedViewport()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(6, 13, 6, 6, 1080, 1658, 135,
                                                         out int paintedWidthPx, out int paintedHeightPx, out bool measured);

            Assert.AreEqual(12, paintedWidthPx);
            Assert.AreEqual(26, paintedHeightPx);
            Assert.IsTrue(measured);
        }

        /// <summary>
        /// With neither source available the reported size is all there is - but it is passed through
        /// as a guess, and <c>measured</c> false is what stops the fit from computing a grid from it.
        /// Before that flag existed this case silently produced a target at twice the columns the
        /// panel could show, grew the buffer to match, and the window re-apply that followed made
        /// conhost drop every column past the panel.
        /// </summary>
        [TestMethod]
        public void EstimatePaintedConsoleCell_FallsBackToTheReportedSize()
        {
            ClaudeCodeControl.EstimatePaintedConsoleCell(6, 13, 0, 9001, 1080, 0, 0,
                                                         out int w1, out int h1, out bool m1);
            Assert.AreEqual(6, w1, "no maximum");
            Assert.AreEqual(13, h1, "no maximum");
            Assert.IsFalse(m1, "no maximum and no window - nothing was measured");

            ClaudeCodeControl.EstimatePaintedConsoleCell(6, 13, 40, 40, 1080, 0, 0,
                                                         out int w2, out int h2, out bool m2);
            Assert.AreEqual(6, w2, "maximum capped by the buffer, not the screen");
            Assert.AreEqual(13, h2, "maximum capped by the buffer, not the screen");
            Assert.IsFalse(m2, "a maximum capped by the buffer says nothing about the cell");

            ClaudeCodeControl.EstimatePaintedConsoleCell(0, 0, 39, 9001, 1080, 1658, 135,
                                                         out int w3, out int h3, out bool m3);
            Assert.AreEqual(0, w3, "font unreadable");
            Assert.AreEqual(0, h3, "font unreadable");
            Assert.IsFalse(m3, "there is nothing to scale without a reported size");
        }

        #endregion

        #region Fitting the viewport back onto the window

        /// <summary>
        /// The measured state after the reconnect: a 6-row viewport in a window with room for 47, so
        /// conhost painted 78 px of a 623 px panel and left the rest black. The columns were already
        /// right (135 x 6 px = the 812 px client area), and they must stay untouched.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_GrowsAViewportTheHostShrankAndDidNotGrowBack()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 79, viewCols: 135, viewRows: 6,
                cursorRow: 81,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 83,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.IsTrue(needed, "A 6-row viewport in a 47-row window is the failure this exists for.");
            Assert.AreEqual(47, fit.Rows, "623 px over a 13 px cell is 47 rows.");
            Assert.AreEqual(135, fit.Cols, "The columns already fit the panel and must not move.");
            Assert.AreEqual(79, fit.Top, "The viewport already contained the cursor, so it stays where it is.");
            Assert.IsFalse(fit.BufferMustGrow, "Nothing is wider than the buffer here.");
        }

        /// <summary>
        /// A narrower window must never narrow the grid: conhost does not reflow, so every character
        /// past the new width is discarded across the whole scrollback and no scrollbar reaches it
        /// afterwards. An overhanging viewport is recoverable, a truncated buffer is not.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_NeverShrinksTheColumns()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 200, bufferRows: 9001,
                viewTop: 0, viewCols: 200, viewRows: 50,
                cursorRow: 20,
                paintedCellWidthPx: 8, paintedCellHeightPx: 16,
                clientWidthPx: 800, clientHeightPx: 800,
                maxViewCols: 240, maxViewRows: 67,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.IsFalse(needed, "Only the width is off, and narrowing it is exactly what must not happen.");
            Assert.AreEqual(200, fit.Cols, "100 columns would fit the window - and would cost 100 columns of scrollback.");
        }

        /// <summary>
        /// A reconnect at a higher resolution leaves the window wider than the buffer. The viewport
        /// cannot reach past the buffer, so the buffer has to be widened first - which is safe, unlike
        /// narrowing it.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_FlagsTheBufferWhenTheWindowIsWiderThanIt()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 80, bufferRows: 9001,
                viewTop: 0, viewCols: 80, viewRows: 30,
                cursorRow: 20,
                paintedCellWidthPx: 8, paintedCellHeightPx: 16,
                clientWidthPx: 1600, clientHeightPx: 800,
                maxViewCols: 240, maxViewRows: 67,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.IsTrue(needed);
            Assert.AreEqual(200, fit.Cols, "1600 px over an 8 px cell is 200 columns.");
            Assert.IsTrue(fit.BufferMustGrow, "The buffer is 80 wide; the viewport cannot be 200 until it is not.");
            Assert.AreEqual(50, fit.Rows);
        }

        /// <summary>
        /// A row of play is what integer division against the cell size produces, and chasing it would
        /// cost a console attach per pass for a row nobody can see.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_LeavesACellOfSlackAlone()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 0, viewCols: 135, viewRows: 46,
                cursorRow: 20,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 83,
                out ClaudeCodeControl.ConsoleGridFit _);

            Assert.IsFalse(needed, "47 wanted against 46 shown is not worth an attach.");
        }

        /// <summary>
        /// The other half of the measured failure: the viewport covered 6 rows starting at the top
        /// while the agent drew at the cursor on row 100, so what the panel showed was a frozen
        /// screen. Once the size alone has earned the rewrite, the cursor decides where the repaired
        /// viewport is anchored - that is where a full-screen agent UI draws.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_PullsTheViewportBackOntoTheCursor()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 0, viewCols: 135, viewRows: 6,
                cursorRow: 100,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 83,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.IsTrue(needed, "6 rows in a window with room for 47 is the collapse the fit exists for.");
            Assert.AreEqual(47, fit.Rows);
            Assert.AreEqual(54, fit.Top, "The cursor ends up on the last row of the viewport: 100 - 47 + 1.");
        }

        /// <summary>
        /// A cursor outside the viewport is not damage on its own - it is also exactly what a user who
        /// has scrolled back to read earlier output looks like. Treating it as a trigger made every
        /// SessionSwitch (locking and unlocking the workstation, no display change involved at all)
        /// snap the terminal back to the end and throw that scroll position away, because the
        /// trailing passes run the fit regardless of whether any DPI moved.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_LeavesAScrolledBackViewportAlone()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 500, viewCols: 135, viewRows: 47,
                cursorRow: 8000,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 83,
                out ClaudeCodeControl.ConsoleGridFit _);

            Assert.IsFalse(needed, "The grid fills the window; only the user's scroll position is unusual.");
        }

        /// <summary>
        /// The host maximum may cap the target, but never below what is on screen right now. Straight
        /// after a DPI change conhost computes `dwMaximumWindowSize` against the new screen while
        /// still painting with the old cell, so it comes back at roughly half the current column
        /// count - and clamping to that narrowed a 134-column viewport to 69. The buffer keeps the
        /// text either way, but half of it ends up off screen, which is the opposite of the job.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_NeverLetsTheHostMaximumNarrowTheViewport()
        {
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 0, viewCols: 134, viewRows: 6,
                cursorRow: 3,
                paintedCellWidthPx: 12, paintedCellHeightPx: 26,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 69, maxViewRows: 39,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.IsTrue(needed, "6 rows where 23 fit is still a collapse worth repairing.");
            Assert.AreEqual(134, fit.Cols,
                "The stale host maximum of 69 must not take 65 columns off what the user can already see.");
            Assert.IsFalse(fit.BufferMustGrow, "134 columns fit in the 135-column buffer that is already there.");
        }

        /// <summary>
        /// The viewport is clamped to what the host says it can show at the current font
        /// (dwMaximumWindowSize). Asking for more is a SetConsoleWindowInfo that fails outright, which
        /// would leave the grid exactly as broken as it was.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_ClampsToWhatTheHostCanShow()
        {
            ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 0, viewCols: 135, viewRows: 6,
                cursorRow: 3,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 20,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.AreEqual(20, fit.Rows, "47 rows fit the window, but the host reports 20 as its maximum.");
        }

        /// <summary>
        /// The viewport has to stay inside the buffer at both ends, or SetConsoleWindowInfo refuses
        /// the rectangle. Anchoring on a cursor near the top of the buffer is where the lower bound
        /// bites; a short buffer is where the upper one does.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_KeepsTheViewportInsideTheBuffer()
        {
            ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 60, viewCols: 135, viewRows: 6,
                cursorRow: 3,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 83,
                out ClaudeCodeControl.ConsoleGridFit atTop);

            Assert.AreEqual(0, atTop.Top, "Row 3 minus 47 rows is negative; the viewport starts at the buffer top.");

            ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 50,
                viewTop: 44, viewCols: 135, viewRows: 6,
                cursorRow: 49,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 83,
                out ClaudeCodeControl.ConsoleGridFit atBottom);

            Assert.AreEqual(47, atBottom.Rows);
            Assert.AreEqual(3, atBottom.Top, "47 rows in a 50-row buffer can only start at row 3.");
        }

        /// <summary>
        /// A measurement the host would not give up - no font, a window with no client area yet - must
        /// produce no fit at all. Guessing here writes a viewport over a grid nobody measured.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_DoesNothingWithoutAMeasurement()
        {
            Assert.IsFalse(ClaudeCodeControl.TryComputeConsoleGridFit(
                135, 9001, 0, 135, 6, 81, 0, 0, 812, 623, 135, 83, out ClaudeCodeControl.ConsoleGridFit _),
                "cell size unknown");

            Assert.IsFalse(ClaudeCodeControl.TryComputeConsoleGridFit(
                135, 9001, 0, 135, 6, 81, 6, 13, 0, 0, 135, 83, out ClaudeCodeControl.ConsoleGridFit _),
                "window not laid out yet");

            Assert.IsFalse(ClaudeCodeControl.TryComputeConsoleGridFit(
                0, 0, 0, 0, 0, -1, 6, 13, 812, 623, 135, 83, out ClaudeCodeControl.ConsoleGridFit _),
                "console not readable");
        }

        /// <summary>
        /// A maximum of 0 means "no cap", which is how the first attempt asks for the grid the window
        /// actually affords. It matters because the host's own maximum lies right after a DPI change:
        /// on the second measured reconnect conhost reported 39 rows while the panel had room for 47,
        /// and the first cut clamped to it, left 116 px of the panel unpainted, and called it success.
        /// </summary>
        [TestMethod]
        public void TryComputeConsoleGridFit_TreatsAZeroMaximumAsNoCap()
        {
            ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 55, viewCols: 135, viewRows: 10,
                cursorRow: 61,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 0, maxViewRows: 0,
                out ClaudeCodeControl.ConsoleGridFit uncapped);

            Assert.AreEqual(47, uncapped.Rows, "Without a cap the window decides: 623 px over a 13 px cell.");

            ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 9001,
                viewTop: 55, viewCols: 135, viewRows: 10,
                cursorRow: 61,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 135, maxViewRows: 39,
                out ClaudeCodeControl.ConsoleGridFit capped);

            Assert.AreEqual(39, capped.Rows, "With the host maximum in play it caps - which is what the fallback is for.");
        }

        #endregion

        #region Source-level guards

        /// <summary>
        /// The fit is the half of the repair that works on the console rather than on the window, and
        /// it has to run where it can matter: on the pass that moved the cells - the host recomputes
        /// its grid there - and on the last pass, whose result is the state the user is left with.
        /// <para>
        /// Source-level, in the style of <see cref="ConsoleCellDpiScalingTests"/>: the repair reads a
        /// live panel handle and attaches to a foreign console, neither of which exists in a test host.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TheRepairFitsTheConsoleGridAfterApplyingTheGeometry()
        {
            string repair = ExtractMethodBody(DisplayChangeSource,
                "private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)");

            int resize = repair.IndexOf("ResizeEmbeddedTerminal(forceSizeNotification: adoptedNewDpi);", StringComparison.Ordinal);
            int fit = repair.IndexOf("FitConsoleGridAfterRepairAsync(", StringComparison.Ordinal);

            Assert.IsTrue(resize >= 0, "The geometry still has to be applied.");
            Assert.IsTrue(fit > resize, "The grid can only be fitted to the window after the window has its size.");
            StringAssert.Contains(repair, "if (_displayRepairFitPending || latePass)",
                "Fitting on every pass costs an attach cycle per pass; fitting on neither leaves the 6-row viewport.");
            StringAssert.Contains(repair, "_displayRepairFitPending = true;",
                "The pass that moves the cells owes a fit; if the panel is still moving, a later pass has to take it.");
        }

        /// <summary>
        /// A grid that does not fill the window on an early pass is routine - the panel is still
        /// settling and the next pass gets another go. Only the last pass leaves the user with it, so
        /// only the last pass may put an info bar up. The fit reports its verdict rather than raising
        /// the bar itself, because it is no longer the only way the repair can run out of options: a
        /// cell the zoom clamp refuses to rescale leaves the same unusable terminal and never reaches
        /// the fit at all.
        /// </summary>
        [TestMethod]
        public void OnlyTheLastPassTellsTheUserTheGridDidNotSurvive()
        {
            string fit = ExtractMethodBody(DisplayChangeSource,
                "private async Task<bool> FitConsoleGridAfterRepairAsync(int requestId, int passIndex, bool isConhost, bool lastPass)");

            StringAssert.Contains(fit, "return lastPass && outcome != null && outcome.StillOff;",
                "Notifying on an early pass reports a terminal the next pass is about to repair.");
            Assert.IsFalse(fit.Contains("NotifyTerminalGridDidNotSurviveDisplayChange();"),
                "The fit reports; the pass decides - otherwise a failed rescale has no way to reach the user.");

            string repair = ExtractMethodBody(DisplayChangeSource,
                "private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)");

            StringAssert.Contains(repair, "if (lastPass && gridCouldNotBeRepaired && IsDisplayChangeRepairStillWanted(requestId))",
                "Only the last pass judges, and only while it is still the current cycle.");
            StringAssert.Contains(repair, "NotifyTerminalGridDidNotSurviveDisplayChange();",
                "A grid the repair cannot fix has to be the user's decision to restart, not a silent broken panel.");
        }

        /// <summary>
        /// The buffer is the scrollback. Growing it is safe in either dimension; shrinking it drops
        /// every character past the new width across all of it, or every row past the new height,
        /// which is the damage the whole repair exists to prevent - so both sides of the
        /// SetConsoleScreenBufferSize go through Math.Max against what is already there. The rows
        /// have to be able to grow at all, or a viewport that collapsed in the alternate screen
        /// buffer - where the buffer IS the viewport - can never be brought back.
        /// </summary>
        [TestMethod]
        public void TheFitOnlyEverGrowsTheBuffer()
        {
            string fit = ExtractMethodBody(AgentCompletionSource,
                "private ConsoleGridFitOutcome TryFitConsoleGridToWindow(int clientWidthPx, int clientHeightPx,");

            StringAssert.Contains(fit, "if (fit.BufferMustGrow)",
                "The buffer is only touched when the viewport cannot fit inside the one that is there.");
            StringAssert.Contains(fit, "X = (short)Math.Max(fit.Cols, before.BufferCols)",
                "A target narrower than the buffer must never narrow it - that discards text across the whole scrollback.");
            StringAssert.Contains(fit, "Y = (short)Math.Max(fit.Rows, before.BufferRows)",
                "Rows grow with the viewport where they must, and never shrink: the 9000-row scrollback is the buffer.");
        }

        /// <summary>
        /// The clamp that made the collapse unrecoverable in the alternate screen buffer: there the
        /// buffer is the viewport, so pinning the target rows to the buffer pinned them to the six
        /// rows the collapse left. The read-back then compared six against six, found nothing wrong,
        /// and the last pass never told the user. Rows now take the window and the buffer follows.
        /// </summary>
        [TestMethod]
        public void TheFitRestoresACollapsedViewportInTheAlternateBuffer()
        {
            // The measured collapse, in the alternate buffer: 6 rows where the panel affords 47.
            bool needed = ClaudeCodeControl.TryComputeConsoleGridFit(
                bufferCols: 135, bufferRows: 6,
                viewTop: 0, viewCols: 135, viewRows: 6,
                cursorRow: 2,
                paintedCellWidthPx: 6, paintedCellHeightPx: 13,
                clientWidthPx: 812, clientHeightPx: 623,
                maxViewCols: 0, maxViewRows: 0,
                out ClaudeCodeControl.ConsoleGridFit fit);

            Assert.IsTrue(needed, "6 rows over a 623 px panel is the collapse this exists for.");
            Assert.AreEqual(47, fit.Rows, "623 px over a 13 px cell, not the 6 rows the buffer happens to hold.");
            Assert.IsTrue(fit.BufferMustGrow, "The buffer has to grow with the viewport, or the write cannot take.");
            Assert.AreEqual(0, fit.Top, "A viewport as tall as the buffer starts at its first row.");
        }

        /// <summary>
        /// The window the fit is measured against is the panel's, so the fit has to ask for that grid
        /// first. `dwMaximumWindowSize` is conhost's own idea of what fits the screen at the cell size
        /// it is rendering with, and right after a DPI change it is still the old one - trusting it
        /// first is what left the second measured reconnect 8 rows short with a clean log line.
        /// </summary>
        [TestMethod]
        public void TheFitAsksForTheWindowsGridBeforeItSettlesForTheHostMaximum()
        {
            string fit = ExtractMethodBody(AgentCompletionSource,
                "private ConsoleGridFitOutcome TryFitConsoleGridToWindow(int clientWidthPx, int clientHeightPx,");

            int honest = fit.IndexOf("respectHostMaximum: false", StringComparison.Ordinal);
            int capped = fit.IndexOf("respectHostMaximum: true", StringComparison.Ordinal);

            Assert.IsTrue(honest >= 0, "The first attempt has to be the grid the window affords.");
            Assert.IsTrue(capped > honest, "The host maximum is the fallback after a refusal, not the target.");
            StringAssert.Contains(fit, "StillOff = TryComputeConsoleGridFit(after, clientWidthPx, clientHeightPx, respectHostMaximum: false",
                "A grid left short by the host cap is still a grid that does not fill the panel, and the user has to hear about it.");
        }

        /// <summary>
        /// A viewport the host refuses after the buffer has already been grown still leaves a console
        /// that was changed: conhost sizes its own window to the new buffer, and only the caller's
        /// window re-apply puts that back. Reporting Changed=false there left the terminal at the
        /// size conhost had chosen, with no later pass able to recover it - on the next pass the
        /// buffer is already wide, so nothing asks to grow it and nothing reports a change.
        /// </summary>
        [TestMethod]
        public void ABufferThatGrewCountsAsChanged_EvenWhenTheViewportWasRefused()
        {
            string fit = ExtractMethodBody(AgentCompletionSource,
                "private ConsoleGridFitOutcome TryFitConsoleGridToWindow(int clientWidthPx, int clientHeightPx,");

            StringAssert.Contains(fit, "bufferChanged = true;",
                "The buffer write is the point of no return; every outcome after it has to carry that.");
            StringAssert.Contains(fit, "Changed = bufferChanged,",
                "A refused viewport still leaves a console conhost has resized its window to.");
        }

        /// <summary>
        /// A grid that could not be read, or whose painted cell could not be measured, is not a grid
        /// that is fine: it is one nothing can be said about. Both used to come back with Changed and
        /// StillOff clear, which is the signature of "nothing to repair" - so the quiet-cycle summary
        /// swallowed the one line a bug report needs and the last pass never told the user, while the
        /// terminal went on painting six rows of its panel.
        /// </summary>
        [TestMethod]
        public void AGridThatCouldNotBeMeasuredIsNotReportedAsHealthy()
        {
            string fit = ExtractMethodBody(AgentCompletionSource,
                "private ConsoleGridFitOutcome TryFitConsoleGridToWindow(int clientWidthPx, int clientHeightPx,");

            StringAssert.Contains(fit, "if (!before.PaintedCellMeasured)",
                "An assumed cell size must not be turned into a grid - that is what grows the buffer to twice the panel.");

            string repair = ExtractMethodBody(DisplayChangeSource,
                "private async Task<bool> FitConsoleGridAfterRepairAsync(int requestId, int passIndex, bool isConhost, bool lastPass)");

            StringAssert.Contains(repair, "routine: outcome != null && outcome.Measured && !outcome.Changed && !outcome.StillOff",
                "Only a measured, unchanged, well-fitting grid is routine; anything else has to reach the log.");
        }

        /// <summary>
        /// Rewriting the grid makes conhost resize its own window to match it - measured at
        /// 1654x1014 px over an 829x623 px panel, i.e. the terminal overhanging twofold. Only the
        /// repair can put that back, and only right there: the last pass has nobody after it.
        /// </summary>
        [TestMethod]
        public void EveryFitThatChangedTheGridReAppliesTheWindowGeometry()
        {
            string fit = ExtractMethodBody(DisplayChangeSource,
                "private async Task<bool> FitConsoleGridAfterRepairAsync(int requestId, int passIndex, bool isConhost, bool lastPass)");

            StringAssert.Contains(fit, "if (outcome != null && outcome.Changed)",
                "Only a fit that rewrote the grid can have made the host resize its window.");
            StringAssert.Contains(fit, "ResizeEmbeddedTerminal();",
                "Without this the terminal keeps the size conhost gave itself, over a panel half that size.");
        }

        /// <summary>
        /// The cell estimate divides a screen height by the host's own `dwMaximumWindowSize`, and the
        /// host measures that against the monitor IT sits on. `GetSystemMetrics(SM_CYSCREEN)` is
        /// always the PRIMARY monitor, so on a second monitor of a different height the division
        /// carries the ratio of the two monitors as well: worked through on paper for a 4K primary
        /// with Visual Studio on a 1080p secondary, a 13 px cell came out as 26 and the fit then asked
        /// for 23 rows where the panel had room for 47 - and the other way round, a 1080p primary with
        /// VS on 4K asked for 95 rows where 47 fitted, which also grows the buffer permanently because
        /// columns never shrink.
        /// </summary>
        [TestMethod]
        public void TheCellEstimateMeasuresAgainstTheTerminalsOwnMonitor()
        {
            string read = ExtractMethodBody(AgentCompletionSource,
                "private static ConsoleGridSnapshot ReadConsoleGrid(IntPtr handle, int screenHeightPx, int clientWidthPx)");

            StringAssert.Contains(read, "screenHeightPx,",
                "The screen height has to be passed in by a caller that knows which monitor the terminal is on.");
            Assert.IsFalse(read.Contains("GetSystemMetrics"),
                "Measuring against the primary monitor is what makes the estimate wrong on a second monitor.");

            string height = ExtractMethodBody(TerminalSource, "private int GetTerminalScreenHeightPx()");

            StringAssert.Contains(height, "MonitorFromWindow(terminalHandle, MONITOR_DEFAULTTONEAREST)",
                "The monitor has to be the one the embedded terminal window sits on.");
            StringAssert.Contains(height, "info.rcMonitor.Bottom - info.rcMonitor.Top",
                "rcMonitor, not rcWork: the host's maximum is derived from the full monitor, taskbar included.");
        }

        /// <summary>
        /// Visual Studio re-lays the panel out across a display change - the measured reconnect took it
        /// through 829x70 px - and the fit is not allowed to believe a size like that. Fitting there
        /// writes the viewport down to five rows through `SetConsoleWindowInfo`, which is the exact
        /// damage the repair exists to undo, and the next fit would only come seconds later.
        /// </summary>
        [TestMethod]
        public void TheFitWaitsForThePanelToStopMoving()
        {
            string fit = ExtractMethodBody(DisplayChangeSource,
                "private async Task<bool> FitConsoleGridAfterRepairAsync(int requestId, int passIndex, bool isConhost, bool lastPass)");

            StringAssert.Contains(fit, "bool sizeIsStable = clientWidthPx == _lastRepairClientWidthPx &&",
                "A size has to be measured twice before a grid is written to match it.");
            StringAssert.Contains(fit, "if (!sizeIsStable && !lastPass)",
                "The last pass has to judge whatever is in front of it - deferring there would judge nothing.");
            StringAssert.Contains(fit, "_displayRepairFitPending = false;",
                "A fit that ran clears the debt; one that deferred must leave it set for the next pass.");
        }

        /// <summary>
        /// Each pass briefly does AttachConsole/FreeConsole on Visual Studio's own process, which
        /// bounces the native keyboard focus off the embedded terminal - typed characters then land
        /// nowhere. The completion watcher snapshots and restores focus around exactly this call for
        /// that reason; the repair needs it more, since its passes reach 12.75 s past the event and
        /// SessionUnlock fires just as someone comes back to the keyboard.
        /// </summary>
        [TestMethod]
        public void EveryConsoleStepOfTheRepairPreservesTheKeyboardFocus()
        {
            string source = DisplayChangeSource;
            string helper = ExtractMethodBody(source, "private async Task<T> RunConsoleWorkAsync<T>(Func<T> work)");

            int inFile = CountOccurrences(source, "await Task.Run(");
            int inHelper = CountOccurrences(helper, "await Task.Run(");

            Assert.AreEqual(1, inHelper, "The helper is where the work leaves the UI thread.");
            Assert.AreEqual(inHelper, inFile,
                "Every console step has to go through RunConsoleWorkAsync, which restores the focus afterwards; " +
                "a bare Task.Run here leaves the user's keystrokes going nowhere.");

            StringAssert.Contains(helper, "IntPtr focusBefore = GetFocus();");
            StringAssert.Contains(helper, "IsOwnedInputFocusWindow(focusBefore)",
                "Only focus that belongs to us may be restored - never focus the user moved elsewhere.");
            StringAssert.Contains(helper, "SetFocus(focusBefore);");
        }

        /// <summary>
        /// Adding an info bar to the main window from underneath the nested message loop a modal
        /// dialog pumps deadlocked Visual Studio - the completion watcher skips its whole tick for
        /// that reason. A display change can land while a dialog is open just as easily.
        /// </summary>
        [TestMethod]
        public void TheGridNoticeStandsDownWhileAModalDialogIsOpen()
        {
            string notify = ExtractMethodBody(DisplayChangeSource,
                "private void NotifyTerminalGridDidNotSurviveDisplayChange()");

            int modal = notify.IndexOf("ComponentDispatcher.IsThreadModal", StringComparison.Ordinal);
            int throttle = notify.IndexOf("_lastTerminalGridNoticeUtc = DateTime.UtcNow;", StringComparison.Ordinal);

            Assert.IsTrue(modal >= 0, "The info bar must not be raised from underneath a modal dialog.");
            Assert.IsTrue(throttle > modal,
                "Taking the throttle stamp before standing down would spend the notice on a dialog nobody saw.");
        }

        #endregion

        private static string TerminalSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Terminal.cs");

        private static string AgentCompletionSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.AgentCompletion.cs");

        private static string DisplayChangeSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs");

        /// <summary>
        /// Number of non-overlapping occurrences of <paramref name="needle"/> in <paramref name="text"/>.
        /// </summary>
        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Returns the text of a method's body, located by its signature and closed by brace matching.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Method not found; update this guard with the rename: {signature}");

            int open = source.IndexOf('{', start + signature.Length);
            Assert.IsTrue(open >= 0, $"No body found for: {signature}");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces after: {signature}");
            return null;
        }
    }
}
