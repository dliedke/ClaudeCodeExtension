/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the console cell rescaling that keeps the embedded terminal's column count stable
 *          across a session DPI change. The column count is the whole point: conhost does not
 *          reflow, so every character past a narrower buffer width is discarded for good.
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ConsoleCellDpiScalingTests
    {
        /// <summary>
        /// The measured report: a 3840x2160 session at 200% reconnected over RDP as 1920x1080 at
        /// 100%. The cell stayed 26px tall while the panel halved, which cut the column count from
        /// ~134 to ~69 and truncated the scrollback at column 69. Halving the cell with the DPI is
        /// what keeps the columns - and therefore the text - where they were.
        /// </summary>
        [TestMethod]
        public void ScaleConsoleCellHeightForDpi_HalvesTheCellWhenTheDpiHalves()
        {
            Assert.AreEqual(13, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(26, 192, 96));
        }

        /// <summary>
        /// The same reconnect in the other direction - back to the high-DPI display - has to grow
        /// the cell again, or the text stays half-size on a screen with twice the pixels.
        /// </summary>
        [TestMethod]
        public void ScaleConsoleCellHeightForDpi_GrowsTheCellWhenTheDpiGrows()
        {
            Assert.AreEqual(26, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(13, 96, 192));
            Assert.AreEqual(20, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(16, 96, 120), "125%");
        }

        /// <summary>
        /// 0 means "nothing to do" and keeps the caller from spending a console attach on a no-op.
        /// An unknown DPI (GetDpiForWindow fails, or the cell size was never captured) must not be
        /// guessed at, because guessing wrong is what truncates the buffer.
        /// </summary>
        [TestMethod]
        public void ScaleConsoleCellHeightForDpi_ReturnsZeroWhenThereIsNothingToDo()
        {
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(26, 96, 96), "same DPI");
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(26, 0, 96), "source DPI unknown");
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(26, 96, 0), "target DPI unknown");
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(0, 192, 96), "cell height unknown");
        }

        /// <summary>
        /// The result is clamped to the range the Ctrl+Scroll zoom uses (6..60 px), so an absurd DPI
        /// ratio - a misreported session, a remote display with an extreme scale - cannot leave the
        /// terminal with an unreadable or giant cell.
        /// </summary>
        [TestMethod]
        public void ScaleConsoleCellHeightForDpi_ClampsToTheZoomRange()
        {
            Assert.AreEqual(60, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(26, 96, 960), "upper bound");
            Assert.AreEqual(6, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(26, 960, 96), "lower bound");
        }

        /// <summary>
        /// A cell already sitting at the clamp bound cannot move any further in that direction, so
        /// there is no new height to report. This is "nothing to do", not "it went wrong" - see
        /// <see cref="TheClampReportsTheHeightThatIsThere_NotAFailure"/> for why the difference
        /// decides whether the terminal recovers.
        /// </summary>
        [TestMethod]
        public void ScaleConsoleCellHeightForDpi_ReturnsZeroWhenTheClampLeavesTheHeightUnchanged()
        {
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(6, 192, 96), "already at the floor");
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(60, 96, 192), "already at the ceiling");
        }

        /// <summary>
        /// A zero cell height reads as "the console could not be reached", and on that reading the
        /// display repair keeps the width guard on with no retry - so a clamped-out rescale has to
        /// hand back the height that IS there. But the height alone cannot be the verdict either: a
        /// user zoomed to the 6 px floor gets no rescale on a DPI decrease and one near it gets a
        /// partial one, and reading either as success let the repair adopt the new DPI, drop the
        /// width guard, and have the next resize narrow conhost against cells that still belonged to
        /// the old DPI - discarding every column past the panel. Hence the third value: the height
        /// the ratio asked for, compared against what the clamp allowed.
        /// <para>
        /// Source-level: the surrounding method attaches to a foreign console.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TheClampReportsTheHeightThatIsThere_ButNotAsSuccess()
        {
            string adjust = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.AgentCompletion.cs"),
                "private int TryAdjustConhostFontSize(int stepUnits, out ConsoleCellRescale rescale,");

            int scale = adjust.IndexOf("newHeight = ScaleConsoleCellHeightForDpi(", StringComparison.Ordinal);
            int report = adjust.IndexOf("NewCellHeightPx = oldHeight,", StringComparison.Ordinal);

            Assert.IsTrue(scale >= 0, "The DPI mode still has to go through the pure scaling helper.");
            Assert.IsTrue(report > scale,
                "A clamped-out rescale has to hand back the current height; 0 there strands the width guard on.");
            StringAssert.Contains(adjust, "ReachedTargetHeight = idealHeight == oldHeight,",
                "Nothing owed and the clamp refusing look identical in the height alone - only the ideal tells them apart.");

            string repair = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs"),
                "private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)");

            StringAssert.Contains(repair, "if (rescale.NewCellHeightPx > 0 && rescale.ReachedTargetHeight)",
                "The DPI may only be adopted by a rescale that actually reached it, or the width guard falls too early.");
        }

        /// <summary>
        /// The clamp's two outcomes, in the arithmetic itself: a ratio that lands on the height that
        /// is already there owed nothing, while one the floor refuses leaves a cell that still
        /// belongs to the old DPI. <c>ScaleConsoleCellHeightForDpi</c> returns 0 for both, which is
        /// why the ideal height exists alongside it.
        /// </summary>
        [TestMethod]
        public void IdealConsoleCellHeightForDpi_TellsNothingOwedApartFromClamped()
        {
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(6, 192, 96),
                "A 6 px cell is already at the floor, so the halving cannot be applied.");
            Assert.AreEqual(3, ClaudeCodeControl.IdealConsoleCellHeightForDpi(6, 192, 96),
                "The ratio asked for 3 px; the clamp is what refused it, and the cell is still the old DPI's.");

            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(13, 96, 96),
                "Same DPI, nothing to do.");
            Assert.AreEqual(13, ClaudeCodeControl.IdealConsoleCellHeightForDpi(13, 96, 96),
                "Nothing was owed here - the ideal is the height that is already there.");
        }

        /// <summary>
        /// The measured trip: 200% -> 100% -> 250% -> 200% with an 11 px cell. Scaling the current
        /// height each time rounded an already rounded value - 11 -> 6 (5.5 up) -> 15 -> 12 - and the
        /// terminal came back 9% larger, with 68 columns where it had 75 in the same panel. Scaled
        /// from the base it started at, the same trip lands on 6 -> 14 -> 11.
        /// </summary>
        [TestMethod]
        public void ARoundTripThroughSeveralDpisComesBackToTheSameCell()
        {
            const int baseHeight = 11;
            const uint baseDpi = 192;

            int at96 = ClaudeCodeControl.ScaleConsoleCellHeightForDpi(baseHeight, 192, 96,
                ClaudeCodeControl.IdealConsoleCellHeightForDpi(baseHeight, baseDpi, 96));
            int at240 = ClaudeCodeControl.ScaleConsoleCellHeightForDpi(at96, 96, 240,
                ClaudeCodeControl.IdealConsoleCellHeightForDpi(baseHeight, baseDpi, 240));
            int back = ClaudeCodeControl.ScaleConsoleCellHeightForDpi(at240, 240, 192,
                ClaudeCodeControl.IdealConsoleCellHeightForDpi(baseHeight, baseDpi, 192));

            Assert.AreEqual(6, at96, "5.5 rounds up, as before.");
            Assert.AreEqual(14, at240, "13.75 from the base - not the 15 that 6 x 2.5 gave.");
            Assert.AreEqual(11, back, "Back at the DPI it started at, the cell is the one it started with.");

            // What the old arithmetic did with the same trip, for the record.
            int oldAt240 = ClaudeCodeControl.ScaleConsoleCellHeightForDpi(at96, 96, 240);
            Assert.AreEqual(15, oldAt240);
            Assert.AreEqual(12, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(oldAt240, 240, 192),
                "The drift the base size exists to prevent.");
        }

        /// <summary>
        /// A target computed from the base can sit on the far side of the current height: the grid fit
        /// may have stepped the font down a pixel after the last rescale, and a small DPI decrease then
        /// asks for a cell a pixel taller than that. That is not the clamp turning a shrink into a
        /// growth - read as one, the repair would call the rescale clamped and hold the width.
        /// </summary>
        [TestMethod]
        public void ATargetFromTheBaseMayMoveAgainstTheDpiWhenTheClampDidNotMoveIt()
        {
            // Base 15 px at 240 dpi, stepped down to 13 by the fit; 240 -> 216 dpi asks for 13.5 -> 14.
            int ideal = ClaudeCodeControl.IdealConsoleCellHeightForDpi(15, 240, 216);
            Assert.AreEqual(14, ideal);
            Assert.AreEqual(14, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(13, 240, 216, ideal));

            // The clamp itself still may not do it: a cell below the floor stays put on a decrease.
            Assert.AreEqual(0, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(4, 192, 96, 2));
        }

        /// <summary>
        /// The base is captured by the first rescale and dropped by a zoom, which is the user picking
        /// a size - and a new terminal starts from the saved size with no base at all. Source-level:
        /// the capture sits inside the console attach.
        /// </summary>
        [TestMethod]
        public void TheRescaleWorksFromTheBaseAndTheZoomResetsIt()
        {
            string adjust = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.AgentCompletion.cs"),
                "private int TryAdjustConhostFontSize(int stepUnits, out ConsoleCellRescale rescale,");

            StringAssert.Contains(adjust, "IdealConsoleCellHeightForDpi(_conhostBaseCellHeightPx, _conhostBaseCellDpi, scaleToDpi)",
                "The target has to come from the base, or the rounding of every step compounds.");
            StringAssert.Contains(adjust, "_conhostBaseCellHeightPx = oldHeight;",
                "The first rescale captures the size the user had.");

            int zoomReset = adjust.IndexOf("if (!dpiMode)", StringComparison.Ordinal);
            Assert.IsTrue(zoomReset >= 0 && adjust.IndexOf("_conhostBaseCellHeightPx = 0;", zoomReset, StringComparison.Ordinal) > zoomReset,
                "A zoom is a size the user chose; the next rescale has to start from it.");

            StringAssert.Contains(TerminalSource, "_conhostBaseCellHeightPx = 0;",
                "A new terminal starts from the saved size, not from the previous one's base.");
        }

        /// <summary>
        /// Rounds away from zero rather than truncating: a cell one pixel short of the ratio is a
        /// column more than the panel can show, which is exactly the overflow this is meant to avoid.
        /// </summary>
        [TestMethod]
        public void ScaleConsoleCellHeightForDpi_RoundsAwayFromZero()
        {
            Assert.AreEqual(13, ClaudeCodeControl.ScaleConsoleCellHeightForDpi(10, 96, 120), "12.5 rounds to 13");
        }

        /// <summary>
        /// The first cut of this fix guarded the width only inside the repair pass, and the repair
        /// pass is not what gets there first: a resolution change re-lays out the panel immediately,
        /// its Resize handler calls ResizeEmbeddedTerminal, and conhost has already thrown the
        /// scrollback away by the time the 150 ms pass runs. The guard therefore has to sit in
        /// ResizeEmbeddedTerminal itself, which every one of those callers goes through.
        /// <para>
        /// Source-level, in the style of <see cref="DebugVisibilityTests"/>: the method reads a
        /// live panel handle and moves a foreign HWND, neither of which exists outside a running IDE.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TheWidthGuardRunsOnEveryResize_NotOnlyInTheRepairPass()
        {
            string resize = ExtractMethodBody(TerminalSource,
                "private void ResizeEmbeddedTerminal(bool forceSizeNotification = false)");

            StringAssert.Contains(resize, "else if (_terminalCellDpi != panelDpi)",
                "Every resize has to compare the cell DPI against the live panel DPI, not just the repair pass.");
            StringAssert.Contains(resize, "_heldTerminalWidthPx = GetEmbeddedTerminalWidth();",
                "While the cells belong to the old DPI the window must keep its width - narrowing conhost truncates the buffer for good.");
            StringAssert.Contains(resize, "minWidth = _heldTerminalWidthPx;",
                "The held width is captured once. Re-reading the live rect made it a ratchet: a window conhost had " +
                "sized for itself became the new floor and the panel could never pull it back.");
            StringAssert.Contains(resize, "if (_wtTabBarHeight == 0)",
                "Windows Terminal reflows and must stay exempt from the width guard, or it keeps overhanging the panel.");
            StringAssert.Contains(resize, "ScheduleDisplayChangeRepair();",
                "A panel DPI nothing has repaired yet must start a repair from here: a monitor drag raises no display or session event.");
            StringAssert.Contains(resize, "if (ShouldScheduleDisplayRepairFromResize(panelDpi))",
                "The gate has to bound the retries without making the first failure final - see the helper.");
            StringAssert.Contains(resize, "NoteDisplayRepairScheduled(panelDpi);",
                "The gate has to close here, not 150 ms later in the pass: every resize in between wrote a log line " +
                "on the UI thread and superseded the pass that was about to repair.");
        }

        /// <summary>
        /// A rescale can fail for a reason that passes - the console is unreachable for a moment while
        /// the agent restarts - and the rule this replaced ("once per DPI, ever", with the flag set at
        /// the top of the first pass, before anything had been attempted) turned that into a terminal
        /// held at the old width for the rest of its life, with no retry and nothing in the log. The
        /// retries are bounded instead, by count and by a cooldown that outlasts a full cycle.
        /// </summary>
        [TestMethod]
        public void AFailedRescaleIsRetried_ButNotForever()
        {
            string gate = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs"),
                "private bool ShouldScheduleDisplayRepairFromResize(uint panelDpi)");

            StringAssert.Contains(gate, "if (_lastDisplayRepairDpi != panelDpi)",
                "A DPI nothing has run for always gets a cycle.");
            StringAssert.Contains(gate, "if (_displayRepairCyclesForDpi >= MaxDisplayRepairCyclesPerDpi)",
                "Without a bound a rescale that cannot succeed reschedules itself for as long as the terminal lives.");
            StringAssert.Contains(gate, "DisplayRepairRetryCooldownMs",
                "The Resize handler fires for every pixel of a splitter drag, so the bound needs a cooldown beside it.");

            string source = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs");
            int cooldown = ReadIntConstant(source, "DisplayRepairRetryCooldownMs");
            int cycleMs = 150 + 400 + 900 + 1800 + 3500 + 6000;

            Assert.IsTrue(cooldown > cycleMs,
                $"The cooldown ({cooldown} ms) has to outlast a full repair cycle ({cycleMs} ms), or a retry races the cycle it is judging.");
        }

        /// <summary>
        /// A console host reports a size change to the program inside it only when its row or column
        /// count changes, so the one-pixel nudge of the first cut notified nobody: the agent kept
        /// drawing against the old grid and scrolled to a cursor outside the visible area. The nudge
        /// has to be at least one cell, has to be vertical - a column less makes conhost drop a
        /// column of text permanently - and has to go upward, where the surplus is clipped by the
        /// panel rather than pushing lines into the scrollback.
        /// </summary>
        [TestMethod]
        public void TheForcedSizeNotificationOvershootsTheHeightOnly()
        {
            string source = TerminalSource;
            string resize = ExtractMethodBody(source,
                "private void ResizeEmbeddedTerminal(bool forceSizeNotification = false)");

            StringAssert.Contains(resize, "targetWidth, targetHeight + ConsoleSizeNudgeHeightPx,",
                "The nudge must add to the height and leave the width alone, or it costs a column of scrollback.");
            Assert.IsFalse(resize.Contains("targetWidth - 1"),
                "Nudging the width down is what truncates the buffer; only the height may move.");
            Assert.IsFalse(resize.Contains("targetHeight - 1"),
                "A one-pixel nudge stays inside the same cell row and notifies nobody.");

            StringAssert.Contains(source, "private const int ConsoleSizeNudgeHeightPx = ConhostZoomMaxPx * MaxPaintedCellScale;",
                "ConhostZoomMaxPx bounds the REPORTED cell; the nudge has to clear a PAINTED one, which is up to " +
                "MaxPaintedCellScale times larger on a console created at a higher DPI. A nudge shorter than a " +
                "painted row lands inside the grid the host already has and notifies nobody - the very failure " +
                "the forced notification exists for.");
        }

        /// <summary>
        /// Only the pass that actually moves the cells to the new DPI needs the notification; the
        /// trailing no-op passes would just make the window jump for nothing.
        /// </summary>
        [TestMethod]
        public void OnlyThePassThatAdoptsTheNewDpiForcesTheNotification()
        {
            string repair = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs"),
                "private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)");

            StringAssert.Contains(repair, "ResizeEmbeddedTerminal(forceSizeNotification: adoptedNewDpi);",
                "Forcing on every pass makes the terminal jump once per pass for no gain.");
            StringAssert.Contains(repair, "NoteDisplayRepairScheduled(panelDpi);",
                "Each pass must mark its DPI as attempted, or ResizeEmbeddedTerminal keeps scheduling new cycles for it.");
            StringAssert.Contains(repair, "_displayRepairCyclesForDpi++;",
                "Only a cycle that actually started counts against the retry bound.");
        }

        /// <summary>
        /// 96 is a perfectly ordinary DPI, so it cannot double as "the panel could not be asked". It
        /// did: a panel between handles, one disposed mid-detach, or a `GetDpiForWindow` returning 0
        /// all read back as a genuine 96 — and on a 192 DPI machine one such reading made the cell
        /// size look stale, halved the console font, and recorded 96 as the DPI the cells belonged
        /// to. With the rescale then failing, the width guard latched with nothing left to retry.
        /// </summary>
        [TestMethod]
        public void AnUnreadablePanelDpiChangesNothing()
        {
            string dpi = ExtractMethodBody(TerminalSource, "private uint GetTerminalPanelDpi()");

            StringAssert.Contains(dpi, "return 0;",
                "The failure value has to be distinguishable from a DPI that was actually read.");
            Assert.IsFalse(dpi.Contains("return 96;"),
                "Falling back to 96 hands the callers a guess they cannot tell from a measurement.");

            string resize = ExtractMethodBody(TerminalSource,
                "private void ResizeEmbeddedTerminal(bool forceSizeNotification = false)");

            StringAssert.Contains(resize, "if (panelDpi == 0)",
                "An unreadable DPI must not start a rescale, and must not clear a width that is being held.");

            string repair = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs"),
                "private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)");

            StringAssert.Contains(repair, "if (panelDpi == 0)",
                "A pass measured against a guessed DPI writes that guess into _terminalCellDpi as the truth.");
        }

        /// <summary>
        /// The DPI rescale is a correction for the session's DPI, not a font size the user chose, and
        /// the repair says so where it makes it. The Ctrl+Scroll zoom persists an absolute cell
        /// height, though, so without the running offset the first wheel notch after a reconnect
        /// saved the corrected cell as the user's choice: one click after a 200%-to-100% reconnect
        /// halved the console font for every later session, including back on the high-DPI display.
        /// </summary>
        [TestMethod]
        public void TheZoomDoesNotPersistWhatTheDisplayRepairCorrected()
        {
            string drain = ExtractMethodBody(TerminalSource, "private void StartConhostZoomDrainWorker()");

            StringAssert.Contains(drain, "PersistConhostZoomFontSize(settledCellHeightPx - _conhostDpiCellOffsetPx);",
                "What is saved has to be the size the user zoomed to measured against the size they had picked.");

            string repair = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DisplayChange.cs"),
                "private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)");

            StringAssert.Contains(repair, "_conhostDpiCellOffsetPx += rescale.DeltaPx;",
                "Every rescale has to add to the offset, or the next zoom persists the difference it made.");

            string reset = TerminalSource;
            StringAssert.Contains(reset, "_conhostDpiCellOffsetPx = 0;",
                "A new terminal starts from the saved size, so the previous one's corrections are not owed to anybody.");
        }

        /// <summary>
        /// Reads a <c>private const int NAME = &lt;digits&gt;;</c> out of a source file. The constants
        /// this file reasons about are private, and the arithmetic they have to satisfy is worth
        /// asserting even where the value itself cannot be referenced from here.
        /// </summary>
        private static int ReadIntConstant(string source, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                source, @"const\s+int\s+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*=\s*(?<value>\d+)\s*;");

            Assert.IsTrue(match.Success, $"No 'const int {name} = <number>;' found in the source.");
            return int.Parse(match.Groups["value"].Value);
        }

        private static string TerminalSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Terminal.cs");

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
