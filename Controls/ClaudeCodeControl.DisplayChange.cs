/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Keeps the embedded terminal readable, and its scrollback intact, when the session DPI
 *          changes underneath it.
 *
 *          The terminal is a window owned by another process (Windows Terminal or conhost), embedded
 *          with SetParent. That makes it a child window, and a child window never receives
 *          WM_DPICHANGED - so when the session DPI changes (a Remote Desktop reconnect at a
 *          different resolution, a monitor switch, a scaling change), the host goes on rendering
 *          with the character cell size it started with, in physical pixels. Measured on a
 *          3840x2160 session at 200% reconnecting as 1920x1080 at 100%: the cell stayed 12x26 px
 *          while the panel halved from 1614 to 812 px, so the text looked twice as large and the
 *          column count fell from ~134 to ~69.
 *
 *          The column collapse is the expensive half. Conhost does not reflow: when its buffer gets
 *          narrower, every character past the new width is discarded, and no horizontal scrollbar
 *          can reach it afterwards. The scrollback is truncated silently, at a different column for
 *          each stretch of output that lived through a different width - which is where "the text
 *          is cut off at different positions depending on how far I scroll" comes from. Zooming out
 *          later widens the grid again, but cannot bring back characters that are gone.
 *
 *          Hence the order of the repair: rescale the console font by the DPI ratio first, which
 *          holds the column count where it was, and only then re-apply the geometry. Where that
 *          rescale is not available - the console attach fails, or the host is Windows Terminal,
 *          which ignores the console font APIs - the window is left overhanging the panel instead
 *          of being narrowed, trading a clipped right edge for an intact buffer. Windows Terminal
 *          reflows, so it is exempt from that guard.
 *
 *          One trap runs through all of this: the cell size the host REPORTS is not the one it
 *          PAINTS. GetCurrentConsoleFontEx answers in the units of the DPI the console was created at,
 *          so a console started on a 192 dpi session reports a 6x13 px cell while painting 12x26 -
 *          measured on one reconnect against a 1658 px window holding 135 columns, and again against
 *          the 1654x1014 px window conhost gave itself for a 135x39 grid. Reading the reported size as
 *          the painted one costs twice: the repair then skips the rescale the reconnect needs, leaving
 *          the font twice too large (the report this file exists for), and the grid fit asks for twice
 *          the rows the panel can show. Every pixel calculation therefore goes through
 *          EstimatePaintedConsoleCell, which recovers the painted size from the host own
 *          dwMaximumWindowSize measured against the screen. The DPI rescale itself keeps working in
 *          reported units - that is what its ratio is expressed in - and a halved 3x7 px cell is an
 *          ordinary 6x14 on screen, not the unreadable thing it reads as.
 *
 *          The guard that enforces that order does not live here but in ResizeEmbeddedTerminal,
 *          because the panel is re-laid out the instant the resolution changes and its Resize
 *          handler narrows the window - and truncates the buffer - long before the first pass below
 *          gets to run. Guarding only in this file would guard nothing.
 *
 *          The other half is telling the agent about it. A console host reports a size change to the
 *          program inside it only when its row or column count changes, so a pass that lands on the
 *          pixel size the window already had leaves a full-screen agent UI drawing against the old
 *          row count: it repaints at the wrong offset and scrolls to a cursor that ends up outside
 *          the visible area. The pass that adopts the new DPI therefore has ResizeEmbeddedTerminal
 *          overshoot the height by a cell row and settle back.
 *
 *          A window resize is only ever a request, though, and the same reconnect proved it: while
 *          Visual Studio re-laid the panel out it passed through 829x70 px, conhost shrank its
 *          viewport to 6 rows to match, and when the panel came back at 829x623 px the viewport
 *          stayed at 6 - five percent of the panel painted, the agent's prompt box drawn into rows
 *          the viewport no longer covered, and every scroll dragging that six-row band over a nine
 *          thousand row buffer. That is what the reports of "the display breaks up when I scroll"
 *          after a reconnect are. The console API is the lever the window does not give:
 *          TryFitConsoleGridToWindow grows the viewport - and, where it must, the buffer - back onto
 *          the window and anchors it on the cursor, where a full-screen agent UI draws. Columns only
 *          ever grow there; narrowing them is the truncation this file exists to avoid. Where even
 *          that does not take, the last pass says so in an info bar offering a restart, instead of
 *          leaving a terminal that looks broken and cannot be repaired from the inside.
 *
 *          Every pass writes to %LocalAppData%\ClaudeCodeExtension\terminal-launch.log via
 *          LogTerminalLaunch - the DPIs, the panel and window measurements, whether the rescale took,
 *          and once per display change the console grid itself (buffer, viewport, cursor, cell) read
 *          through TryReadConsoleGrid, plus what the grid fit did with it. Debug.WriteLine is compiled out of Release builds, and
 *          this failure only happens on a real reconnect on a real machine, so the log is the only
 *          way to tell a buffer that conhost has already truncated apart from a grid the program
 *          inside it has not been told about.
 *
 *          A cycle that finds nothing to repair does not write that trail, though. SessionUnlock is
 *          one of the triggers, so locking and unlocking the workstation runs a full six-pass cycle
 *          with no display change behind it, and at some fifteen lines a time those were pushing the
 *          launch history the log exists for (issue #73) out of the 512 KB it keeps. The routine
 *          lines are therefore held rather than written (LogDisplayRepair), and a cycle that ends
 *          without having found anything replaces them with one summary line
 *          (FinishDisplayRepairLogCycle). The moment a line is NOT routine - a rescale, a grid the
 *          fit had to move or could not get right, a console that could not be read - the held lines
 *          go out in order ahead of it and the rest of the cycle logs as it always did. So the cycles
 *          a bug report is about are still there in full; only the ones that had nothing to say are
 *          quiet. What reaches the log is all this decides: no pass does anything differently.
 *
 *          Two system events cover the ways the DPI changes - DisplaySettingsChanged for a
 *          resolution or monitor change, SessionSwitch for the Remote Desktop reconnect that
 *          changes it while the session is disconnected - and both land on the same repair. It runs
 *          six times, the last one 12.75 s after the event, because an RDP reconnect settles slowly -
 *          conhost was measured still reporting the old DPI metrics three seconds after one, and the
 *          last pass is the one that judges whether the terminal came out usable.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Fields

        /// <summary>
        /// True once the display/session event handlers are subscribed, so the subscription is idempotent.
        /// </summary>
        private bool _displayChangeHandlersInstalled;

        /// <summary>
        /// Generation counter for the delayed repair passes; a newer display change supersedes the
        /// passes still pending from the previous one.
        /// </summary>
        private int _displayChangeRepairRequestId;

        /// <summary>
        /// Delays (ms) BETWEEN the repair passes after a display change - they accumulate, so the
        /// passes land at 150 / 550 / 1450 / 3250 / 6750 / 12750 ms, not at the bare figures below.
        /// An RDP reconnect does not settle at once - the panel and the reported DPI can still be the
        /// old ones when the first event arrives - so the repair is attempted a few times instead of
        /// once. Passes after a successful rescale are cheap no-ops, because the cell size then
        /// already matches the DPI. The last two sit far out (measured: conhost was still reporting
        /// the metrics of the old DPI three seconds into a reconnect, and a grid fitted against those
        /// comes out wrong), and the last one decides whether the user gets told the terminal needs a
        /// restart.
        /// </summary>
        private static readonly int[] DisplayChangeRepairDelaysMs = { 150, 400, 900, 1800, 3500, 6000 };

        /// <summary>
        /// Repair cycles the resize handler may start for one DPI. A rescale can fail for a reason
        /// that passes - the console is briefly unreachable while the agent restarts - and the rule
        /// this replaces ("once per DPI, ever", with the flag set at the top of the first pass,
        /// before anything had been attempted) turned such a failure into a terminal held at the old
        /// width for the rest of its life, with no retry and nothing in the log to say why.
        /// </summary>
        private const int MaxDisplayRepairCyclesPerDpi = 3;

        /// <summary>
        /// Quiet period between two repair cycles started from the resize handler for the same DPI.
        /// It has to outlast a full cycle (12.75 s) so a retry judges the state that cycle left
        /// behind rather than racing it.
        /// </summary>
        private const int DisplayRepairRetryCooldownMs = 15000;

        /// <summary>
        /// Client size the previous pass measured, so a pass can tell whether the panel has stopped
        /// moving. Visual Studio re-lays the panel out across a display change - the measured
        /// reconnect took it through 829x70 px - and a grid fitted to a size like that writes the
        /// viewport down to a handful of rows, which is the damage this file exists to undo.
        /// </summary>
        private int _lastRepairClientWidthPx;
        private int _lastRepairClientHeightPx;

        /// <summary>
        /// True while a grid fit is owed but has not been done yet, because every size measured so far
        /// was still moving. Without it a fit deferred on the pass that adopted the DPI would not be
        /// retried until the trailing passes, seconds later.
        /// </summary>
        private bool _displayRepairFitPending;

        /// <summary>
        /// When the "the grid did not survive the display change" notice was last shown. Undocking a
        /// laptop raises several display events in a row and each of them runs a repair, so the notice
        /// is throttled the same way the terminal-busy one is.
        /// </summary>
        private DateTime _lastTerminalGridNoticeUtc = DateTime.MinValue;

        /// <summary>
        /// Routine log lines of the current repair cycle, held back rather than written. SessionUnlock
        /// is one of the triggers, so every lock and unlock of the workstation runs a full cycle even
        /// though nothing about the display has changed - and each one wrote the better part of twenty
        /// lines into a log that resets at 512 KB, pushing out the launch history it exists for
        /// (issue #73). The lines are kept instead of dropped because the moment a cycle turns out to
        /// have found something, they are the context that makes the rest of it readable.
        /// </summary>
        private readonly List<string> _displayRepairHeldLogLines = new List<string>();

        /// <summary>
        /// True once the current cycle has written a line that is not routine, which releases the held
        /// lines and puts every later line of the cycle straight through. A cycle that repairs
        /// something logs exactly as much as it did before this dampening existed.
        /// </summary>
        private bool _displayRepairLogVerbose;

        #endregion

        #region Subscription

        /// <summary>
        /// Subscribes to the display and session events that indicate the session DPI may have changed.
        /// Idempotent - the Loaded handler of the control runs again on tab switches.
        /// </summary>
        private void InitializeDisplayChangeHandling()
        {
            if (_displayChangeHandlersInstalled)
            {
                return;
            }

            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _displayChangeHandlersInstalled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeDisplayChangeHandling error: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribes from the display and session events. SystemEvents holds its handlers in a
        /// static list, so skipping this would keep the whole control alive after the tool window closes.
        /// </summary>
        private void CleanupDisplayChangeHandling()
        {
            if (!_displayChangeHandlersInstalled)
            {
                return;
            }

            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CleanupDisplayChangeHandling error: {ex.Message}");
            }
            finally
            {
                _displayChangeHandlersInstalled = false;

                // Unsubscribing stops new cycles; this stops the one that is already running. Its
                // passes reach 12.75 s past the event and IsDisplayChangeRepairStillWanted gates only
                // on the window handle, which Cleanup does not clear - it posts WM_CLOSE, which is
                // asynchronous. Without this a display change or SessionUnlock seconds before Visual
                // Studio closes leaves passes attaching consoles to devenv.exe and moving windows
                // during shutdown - the issue #73 hazard the rest of this file guards against.
                Interlocked.Increment(ref _displayChangeRepairRequestId);
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            LogTerminalLaunch("display change: DisplaySettingsChanged - repair scheduled");
            ScheduleDisplayChangeRepair();
        }

        /// <summary>
        /// A Remote Desktop reconnect changes the session resolution while the session is disconnected,
        /// so the display-settings event can be missed entirely; the reconnect itself is the signal.
        /// ConsoleConnect covers going back to the physical console, SessionUnlock the case where the
        /// resolution changed while the session sat locked.
        /// </summary>
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.RemoteConnect ||
                e.Reason == SessionSwitchReason.ConsoleConnect ||
                e.Reason == SessionSwitchReason.SessionUnlock)
            {
                LogTerminalLaunch($"display change: SessionSwitch/{e.Reason} - repair scheduled");
                ScheduleDisplayChangeRepair();
            }
        }

        #endregion

        #region Repair

        /// <summary>
        /// Whether the panel's Resize handler should start a repair cycle for this DPI.
        /// <para>
        /// Always, for a DPI no cycle has run for. Beyond that at most
        /// <see cref="MaxDisplayRepairCyclesPerDpi"/> times and never within
        /// <see cref="DisplayRepairRetryCooldownMs"/> of the last attempt, which is what keeps a
        /// rescale that cannot succeed from looping while still giving one that failed for a passing
        /// reason another go. The Resize handler fires for every pixel of a splitter drag, so a gate
        /// that merely rate-limits would not be enough on its own.
        /// </para>
        /// </summary>
        private bool ShouldScheduleDisplayRepairFromResize(uint panelDpi)
        {
            if (_lastDisplayRepairDpi != panelDpi)
            {
                return true;
            }

            if (_displayRepairCyclesForDpi >= MaxDisplayRepairCyclesPerDpi)
            {
                return false;
            }

            return unchecked(Environment.TickCount - _lastDisplayRepairScheduleTick) >= DisplayRepairRetryCooldownMs;
        }

        /// <summary>
        /// Records that a repair cycle is being started for <paramref name="panelDpi"/>, closing the
        /// gate in <see cref="ShouldScheduleDisplayRepairFromResize"/> immediately. The cycle counter
        /// is advanced by the first pass instead, so only cycles that actually ran are counted.
        /// </summary>
        private void NoteDisplayRepairScheduled(uint panelDpi)
        {
            if (_lastDisplayRepairDpi != panelDpi)
            {
                _lastDisplayRepairDpi = panelDpi;
                _displayRepairCyclesForDpi = 0;
            }

            _lastDisplayRepairScheduleTick = Environment.TickCount;
        }

        /// <summary>
        /// Runs the repair a few times after a display change. Fire and forget on purpose: these
        /// events arrive on a system thread and must not block it.
        /// </summary>
        private void ScheduleDisplayChangeRepair()
        {
            int requestId = Interlocked.Increment(ref _displayChangeRepairRequestId);

#pragma warning disable VSSDK007 // fire-and-forget is intentional; the event arrives off the UI thread
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    for (int pass = 0; pass < DisplayChangeRepairDelaysMs.Length; pass++)
                    {
                        await Task.Delay(DisplayChangeRepairDelaysMs[pass]);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                        if (!IsDisplayChangeRepairStillWanted(requestId))
                        {
                            LogDisplayRepair($"dpi repair pass {pass}: superseded or terminal gone - stopping",
                                             routine: true);
                            return;
                        }

                        await RepairTerminalGeometryAfterDisplayChangeAsync(requestId, pass);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error repairing terminal geometry after display change: {ex.Message}");
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Runs one console-touching step off the UI thread, returns to the UI thread, and puts the
        /// native keyboard focus back where it was.
        /// <para>
        /// Every step below briefly does <c>AttachConsole</c>/<c>FreeConsole</c> on Visual Studio's own
        /// process, and that bounces the native focus off the embedded terminal - typed characters
        /// then land nowhere until the user clicks back in. The completion watcher wraps its console
        /// read in exactly this snapshot/restore for that reason (see the comment above
        /// <c>IsOwnedInputFocusWindow</c>'s caller); the repair needs it more, not less: its passes
        /// reach up to 12.75 s past the event, and <c>SessionUnlock</c> fires precisely when someone
        /// comes back to the keyboard and starts typing. The snapshot is taken on the UI thread, which
        /// <c>SetParent</c> joined to the terminal's input queue, so <c>GetFocus</c> sees the
        /// terminal's focus.
        /// </para>
        /// </summary>
        private async Task<T> RunConsoleWorkAsync<T>(Func<T> work)
        {
            IntPtr focusBefore = GetFocus();
            bool restoreFocus = IsOwnedInputFocusWindow(focusBefore);

            T result = await Task.Run(work);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (restoreFocus && IsWindow(focusBefore) && GetFocus() != focusBefore)
            {
                SetFocus(focusBefore);
            }

            return result;
        }

        /// <summary>
        /// True while this repair pass is still the current one and the terminal is alive. Re-checked
        /// after every thread switch, because a pass yields twice and the terminal can be stopped or
        /// the pass superseded in between.
        /// </summary>
        private bool IsDisplayChangeRepairStillWanted(int requestId)
        {
            return requestId == _displayChangeRepairRequestId &&
                   terminalHandle != IntPtr.Zero &&
                   IsWindow(terminalHandle);
        }

        /// <summary>
        /// One repair pass: bring the character cell size back in line with the session DPI, then
        /// re-apply the geometry. The order matters - see the file header. Narrowing a conhost window
        /// before its cells have been rescaled is what truncates the scrollback, so when the rescale
        /// does not happen the width is held instead.
        /// <para>
        /// Entered on the UI thread and returns on it, but deliberately without the usual
        /// <c>ThrowIfNotOnUIThread</c> guard (same reasoning as <c>PersistConhostZoomFontSize</c>):
        /// the guard marks the method UI-thread-only for the analyzer, which propagates the
        /// requirement up through <see cref="ScheduleDisplayChangeRepair"/> to the SystemEvents
        /// handlers that legitimately arrive off the UI thread.
        /// </para>
        /// </summary>
        private async Task RepairTerminalGeometryAfterDisplayChangeAsync(int requestId, int passIndex)
        {
            uint panelDpi = GetTerminalPanelDpi();
            bool isConhost = _wtTabBarHeight == 0;
            bool lastPass = passIndex == DisplayChangeRepairDelaysMs.Length - 1;

            // Everything below is measured against the panel's DPI, and 0 means it could not be read
            // (see GetTerminalPanelDpi). Skipping costs one pass of six; carrying on with a guessed
            // 96 would rescale the font against a number nobody reported and then record it as the
            // DPI the cells belong to.
            if (panelDpi == 0)
            {
                LogDisplayRepair($"dpi repair pass {passIndex}: panel DPI unavailable - pass skipped", routine: true);

                if (lastPass && IsDisplayChangeRepairStillWanted(requestId))
                {
                    FinishDisplayRepairLogCycle();
                }

                return;
            }

            // The host settles its own metrics late after a reconnect, so the grid is re-checked on
            // the trailing passes and not only on the one that moved the cells.
            bool latePass = passIndex >= DisplayChangeRepairDelaysMs.Length - 2;

            // Marks this DPI as attempted, so ResizeEmbeddedTerminal does not keep scheduling cycles
            // for it while this one runs - and, after it, not before the retry cooldown. The counter
            // advances on the first pass rather than where a cycle is scheduled, so only cycles that
            // actually started count against the bound.
            NoteDisplayRepairScheduled(panelDpi);
            if (passIndex == 0)
            {
                _displayRepairCyclesForDpi++;
            }

            // A cell size captured at this DPI needs nothing; neither does one never captured at
            // all, which means no geometry has been applied yet and there is nothing to preserve.
            // Read before the first log line because it is also what decides whether this pass has
            // anything to say: a cell that already matches the DPI is the state every lock and
            // unlock of the workstation finds.
            bool cellSizeMatchesDpi = _terminalCellDpi == 0 || _terminalCellDpi == panelDpi;

            if (passIndex == 0)
            {
                // Fresh cycle, so nothing is owed from the one this replaces - including its held log
                // lines. This is the only place they are reset (see FinishDisplayRepairLogCycle).
                _displayRepairHeldLogLines.Clear();
                _displayRepairLogVerbose = false;
            }

            LogDisplayRepair($"dpi repair pass {passIndex}: panelDpi={panelDpi} cellDpi={_terminalCellDpi} " +
                             $"host={(isConhost ? "conhost" : "wt")} panel={DescribeTerminalPanelSize()} " +
                             $"window={DescribeTerminalWindowRect()}",
                             routine: cellSizeMatchesDpi);

            // The console grid before anything is touched. Taken on the first pass only - each probe
            // costs an attach cycle - and it is the baseline the last pass is compared against: a
            // buffer that is narrower afterwards means text was discarded for good.
            if (passIndex == 0)
            {
                // Fresh cycle: nothing measured yet, and no fit owed from the cycle this one replaced.
                _lastRepairClientWidthPx = 0;
                _lastRepairClientHeightPx = 0;
                _displayRepairFitPending = false;

                int screenHeightPx = GetTerminalScreenHeightPx();
                TryGetEmbeddedTerminalClientSize(out int baselineClientWidthPx, out int _);
                ConsoleGridSnapshot gridBefore =
                    await RunConsoleWorkAsync(() => TryReadConsoleGrid(screenHeightPx, baselineClientWidthPx));

                if (!IsDisplayChangeRepairStillWanted(requestId))
                {
                    return;
                }

                LogDisplayRepair($"dpi repair pass {passIndex}: console before  {gridBefore.Describe()}",
                                 routine: true);
            }

            // True once this pass has moved the cell grid to the new DPI. Only then is the forced
            // size notification worth its cost: that is the moment the host has a new row and column
            // count that the program inside it has not been told about yet.
            bool adoptedNewDpi = false;

            // Set by the last pass when it leaves the terminal in a state nothing else can fix - a
            // cell the zoom clamp would not rescale, or a grid that would not come back onto the
            // window. Either way the user is left with a terminal they cannot work in and is offered
            // the restart that does fix it.
            bool gridCouldNotBeRepaired = false;

            if (!cellSizeMatchesDpi)
            {
                if (isConhost)
                {
                    uint fromDpi = _terminalCellDpi;

                    // The font APIs briefly attach VS to the agent console - never on the UI thread.
                    ConsoleCellRescale rescale = await RunConsoleWorkAsync(() =>
                    {
                        TryAdjustConhostFontSize(0, out ConsoleCellRescale result, fromDpi, panelDpi);
                        return result;
                    });

                    if (!IsDisplayChangeRepairStillWanted(requestId))
                    {
                        return;
                    }

                    // Three outcomes, and only the first may let the width guard fall: the cell
                    // reached the height the new DPI calls for; the clamp held it back, wholly or in
                    // part, so it still belongs to the old DPI; or the console could not be reached
                    // at all. The middle one used to be logged and treated as the first.
                    string rescaleDetail;
                    if (rescale.NewCellHeightPx <= 0)
                    {
                        rescaleDetail = "failed - width will be held";
                    }
                    else if (!rescale.ReachedTargetHeight)
                    {
                        rescaleDetail = $"clamped at {rescale.NewCellHeightPx}px " +
                                        $"(zoom bound reached, cell still belongs to {fromDpi} dpi) - width will be held";
                    }
                    else
                    {
                        rescaleDetail = $"ok, cell height now {rescale.NewCellHeightPx}px";
                    }

                    LogDisplayRepair($"dpi repair pass {passIndex}: rescale {fromDpi}->{panelDpi} dpi {rescaleDetail}",
                                     routine: false);

                    if (rescale.NewCellHeightPx > 0 && rescale.ReachedTargetHeight)
                    {
                        // The cell size belongs to this DPI now, which also makes the remaining
                        // passes no-ops. Deliberately not persisted as the console font size: this
                        // is a runtime correction for the session DPI, not a size the user picked -
                        // and _conhostDpiCellOffsetPx is what keeps the Ctrl+Scroll zoom from
                        // persisting it by accident on the next notch.
                        _conhostDpiCellOffsetPx += rescale.DeltaPx;
                        _terminalCellDpi = panelDpi;
                        cellSizeMatchesDpi = true;
                        adoptedNewDpi = true;
                    }
                    else if (lastPass)
                    {
                        // Nothing else will try. A terminal whose cells never caught up with the
                        // session DPI keeps its width held and stays wider than its panel, and no
                        // pass after this one can change that - so say so, the same way a grid that
                        // could not be re-fitted does.
                        gridCouldNotBeRepaired = true;
                    }
                }
                else
                {
                    // Windows Terminal: no lever from here - it ignores the console font APIs and has
                    // no font switch on its command line - but it reflows on a width change, so a
                    // narrower panel costs layout, not characters. Adopt the DPI so the later passes
                    // stop retrying, and let the geometry through unguarded.
                    _terminalCellDpi = panelDpi;
                    cellSizeMatchesDpi = true;
                    adoptedNewDpi = true;
                }
            }

            // Windows Terminal only: the offset that hides the tab bar is DPI-scaled and was
            // calculated once, at launch. Conhost has no such offset (_wtTabBarHeight stays 0).
            if (_wtTabBarHeight > 0)
            {
                // The panel's DPI, not the terminal window's: the embedded host is a child window and
                // keeps reporting the DPI it started at (see GetWtTabBarHeight).
                int refreshedTabBarHeight = GetWtTabBarHeight(panelDpi);
                if (refreshedTabBarHeight > 0)
                {
                    _wtTabBarHeight = refreshedTabBarHeight;
                }
            }

            // The fallback that keeps a conhost window whose cells still belong to the old DPI from
            // being narrowed lives in ResizeEmbeddedTerminal itself: the panel Resize handler gets
            // there first on a resolution change, long before this pass runs, so guarding only here
            // would guard nothing.
            ResizeEmbeddedTerminal(forceSizeNotification: adoptedNewDpi);

            LogDisplayRepair($"dpi repair pass {passIndex}: applied, adoptedNewDpi={adoptedNewDpi} " +
                             $"cellDpi={_terminalCellDpi} panel={DescribeTerminalPanelSize()} " +
                             $"window={DescribeTerminalWindowRect()}",
                             routine: !adoptedNewDpi);

            if (adoptedNewDpi)
            {
                _displayRepairFitPending = true;
            }

            // The window is the right size now; the grid inside it need not be. Worth the attach once
            // the cells have moved - that is when the host recomputes the grid - and on the trailing
            // passes, where the geometry has settled and this is the state the user is left with. A
            // fit the pass below defers because the panel is still being re-laid out keeps the flag
            // set, so the very next pass takes it instead of leaving it to the trailing ones seconds
            // later.
            if (_displayRepairFitPending || latePass)
            {
                if (await FitConsoleGridAfterRepairAsync(requestId, passIndex, isConhost, lastPass))
                {
                    gridCouldNotBeRepaired = true;
                }
            }

            // Everything that can be tried has been. A cell the clamp would not rescale and a grid
            // that would not come back onto the window both leave a terminal the user cannot work
            // in, and only a restart clears either.
            if (lastPass && gridCouldNotBeRepaired && IsDisplayChangeRepairStillWanted(requestId))
            {
                NotifyTerminalGridDidNotSurviveDisplayChange();
            }

            // End of the cycle: either it found something and has already said so in full, or it
            // leaves the one summary line. A cycle superseded in the middle of the fit above is not
            // ended here - the cycle that replaced it owns the log from now on.
            if (lastPass && IsDisplayChangeRepairStillWanted(requestId))
            {
                FinishDisplayRepairLogCycle();
            }
        }

        /// <summary>
        /// Reads the console grid back after a repair pass and, on conhost, pulls it onto the window
        /// when it does not fill it. Resizing the host window is a hint the host may act on late or
        /// not at all: measured on an RDP reconnect, conhost followed the panel down to 6 rows while
        /// Visual Studio re-laid it out and stayed there once the panel was 623 px tall again - the
        /// panel painting 6 rows of its 47, the agent drawing its prompt box into rows the viewport no
        /// longer covered, and every scroll moving that 6-row band over a 9000-row buffer.
        /// <para>
        /// Windows Terminal owns its grid (and reflows), so there it stays a reading. Returns true
        /// when the last pass leaves a grid that does not fill its window: the state the user is
        /// stuck with, and the only thing that reliably clears it is a restart. The caller owns the
        /// info bar, because a cell the zoom clamp refused to rescale leaves the same terminal and
        /// never reaches this method.
        /// </para>
        /// </summary>
        private async Task<bool> FitConsoleGridAfterRepairAsync(int requestId, int passIndex, bool isConhost, bool lastPass)
        {
            if (!isConhost)
            {
                if (lastPass)
                {
                    int wtScreenHeightPx = GetTerminalScreenHeightPx();
                    TryGetEmbeddedTerminalClientSize(out int wtClientWidthPx, out int _);
                    ConsoleGridSnapshot wtGrid =
                        await RunConsoleWorkAsync(() => TryReadConsoleGrid(wtScreenHeightPx, wtClientWidthPx));

                    LogDisplayRepair($"dpi repair pass {passIndex}: console after   {wtGrid.Describe()}",
                                     routine: true);
                }

                _displayRepairFitPending = false;
                return false;
            }

            if (!TryGetEmbeddedTerminalClientSize(out int clientWidthPx, out int clientHeightPx))
            {
                return false;
            }

            // Fitting a grid to a panel Visual Studio is still re-laying out writes the damage this
            // file undoes: the measured reconnect took the panel through 829x70 px, and a fit landing
            // there puts the viewport at five rows. So a size has to be seen twice before it is
            // believed - except on the last pass, which has to judge whatever is in front of it.
            bool sizeIsStable = clientWidthPx == _lastRepairClientWidthPx &&
                                clientHeightPx == _lastRepairClientHeightPx;
            _lastRepairClientWidthPx = clientWidthPx;
            _lastRepairClientHeightPx = clientHeightPx;

            if (!sizeIsStable && !lastPass)
            {
                LogDisplayRepair($"dpi repair pass {passIndex}: panel still moving " +
                                 $"({clientWidthPx}x{clientHeightPx} client) - fit deferred",
                                 routine: true);
                return false;
            }

            int screenHeightPx = GetTerminalScreenHeightPx();

            ConsoleGridFitOutcome outcome =
                await RunConsoleWorkAsync(() => TryFitConsoleGridToWindow(clientWidthPx, clientHeightPx, screenHeightPx));

            if (!IsDisplayChangeRepairStillWanted(requestId))
            {
                return false;
            }

            _displayRepairFitPending = false;

            // A grid that came back already filling the window is the routine outcome - that is what a
            // cycle nothing was wrong with measures. A grid the fit had to move, one it could not get
            // right, and a console it could not read or measure are each worth the trail behind them.
            // Measured is part of that test because a console whose grid could not be read at all used
            // to come back with both flags clear, which read as "nothing to repair": the one line a
            // bug report needs was then held back and replaced by the summary saying so.
            LogDisplayRepair($"dpi repair pass {passIndex}: {outcome?.Detail ?? "grid unavailable"}",
                             routine: outcome != null && outcome.Measured && !outcome.Changed && !outcome.StillOff);

            // conhost sizes its own window to whatever grid it ends up with - measured at 1654x1014 px
            // over an 829x623 px panel, the terminal overhanging the panel twofold. Every fit that
            // changed something therefore has to put the geometry back; no later pass can be relied
            // on for that, least of all after the last one.
            if (outcome != null && outcome.Changed)
            {
                ResizeEmbeddedTerminal();
                LogDisplayRepair($"dpi repair pass {passIndex}: window re-applied after the fit, " +
                                 $"window={DescribeTerminalWindowRect()}");
            }

            // Only the last pass judges, and only on a grid that was actually measured: an earlier
            // mismatch is routine - the panel is still settling and the next pass gets another go -
            // and a console that could not be attached at all says nothing about the grid inside it.
            return lastPass && outcome != null && outcome.StillOff;
        }

        /// <summary>
        /// Last-resort notice: the grid could not be brought back onto the window, which leaves a
        /// terminal that paints part of its panel and draws the agent's UI where it cannot be seen.
        /// Nothing in the repair can fix that state, so the user is told and offered the restart that
        /// does. Throttled like the terminal-busy notice - undocking a laptop raises several display
        /// events in a row, and each of them runs a repair.
        /// </summary>
        private void NotifyTerminalGridDidNotSurviveDisplayChange()
        {
            // Never add an info bar to the main window while one of our modal dialogs owns it. The
            // completion watcher skips its own tick for the same reason, and its comment records what
            // happens otherwise: doing this from underneath the nested message loop a modal pumps
            // deadlocked Visual Studio. A display change can easily land while a dialog is open, and
            // the throttle stamp is deliberately not taken here, so a later pass may still say this.
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            {
                LogDisplayRepair("display change: grid notice suppressed - a modal dialog is open");
                return;
            }

            if ((DateTime.UtcNow - _lastTerminalGridNoticeUtc).TotalSeconds < 60)
            {
                return;
            }
            _lastTerminalGridNoticeUtc = DateTime.UtcNow;

#pragma warning disable VSSDK007 // fire-and-forget is intentional; the info bar must not block the pass
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ShowAgentFinishNotificationAsync(
                    "The agent terminal did not come back at the right size after the display changed, so part of the panel stays blank and the agent draws where you cannot see it. Restarting the terminal repairs it.",
                    "Restart terminal",
                    async delegate { await RestartTerminalWithSelectedProviderAsync(); },
                    InfoBarSlot.TerminalGeometry);
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Log sink for the repair passes, on the UI thread only (the held lines are not synchronized,
        /// and the two event handlers that log off it write straight to <c>LogTerminalLaunch</c>).
        /// <para>
        /// A line marked <paramref name="routine"/> is one a cycle that found nothing to do writes
        /// anyway - the per-pass measurements, the deferred fit, a grid that came back already correct.
        /// Those are held until the cycle either turns out to have found something, which releases them
        /// in order, or ends without having found anything, which replaces them with the single summary
        /// line in <see cref="FinishDisplayRepairLogCycle"/>. Nothing about the repair itself depends on
        /// this: it decides what reaches the log, never what the pass does.
        /// </para>
        /// </summary>
        private void LogDisplayRepair(string message, bool routine = false)
        {
            if (routine && !_displayRepairLogVerbose)
            {
                _displayRepairHeldLogLines.Add(message);
                return;
            }

            if (!_displayRepairLogVerbose)
            {
                _displayRepairLogVerbose = true;

                foreach (string held in _displayRepairHeldLogLines)
                {
                    LogTerminalLaunch(held);
                }
                _displayRepairHeldLogLines.Clear();
            }

            LogTerminalLaunch(message);
        }

        /// <summary>
        /// Closes the log cycle on the last pass. A cycle that stayed routine throughout - the common
        /// case, every lock and unlock of the workstation - leaves one line saying so and what it
        /// measured, instead of the fifteen-odd it would otherwise write. A cycle cut short by a newer
        /// display change never gets here; its held lines are dropped by the next cycle's first pass,
        /// which is also the only place they are reset, so a superseded cycle cannot clear the lines of
        /// the one that replaced it.
        /// </summary>
        private void FinishDisplayRepairLogCycle()
        {
            int held = _displayRepairHeldLogLines.Count;
            bool wasVerbose = _displayRepairLogVerbose;

            _displayRepairHeldLogLines.Clear();
            _displayRepairLogVerbose = false;

            if (wasVerbose)
            {
                return;
            }

            LogTerminalLaunch($"display change: nothing to repair - cellDpi={_terminalCellDpi} " +
                              $"panel={DescribeTerminalPanelSize()} window={DescribeTerminalWindowRect()} " +
                              $"({held} routine line(s) not written)");
        }

        /// <summary>
        /// Panel size as "WxH", or "?" when the panel is gone. Diagnostic formatting only.
        /// </summary>
        private string DescribeTerminalPanelSize()
        {
            var panel = ActiveTerminalPanel;
            return (panel == null || panel.IsDisposed) ? "?" : $"{panel.Width}x{panel.Height}";
        }

        /// <summary>
        /// Embedded terminal window rect as "WxH@(left,top)", or "?" when it cannot be read. The top
        /// is included because Windows Terminal is deliberately parked above the panel to hide its
        /// tab bar, and a stale offset there clips the wrong number of pixels.
        /// </summary>
        private string DescribeTerminalWindowRect()
        {
            if (terminalHandle != IntPtr.Zero && GetWindowRect(terminalHandle, out RECT rect))
            {
                return $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top}@({rect.Left},{rect.Top})";
            }

            return "?";
        }

        #endregion
    }
}
