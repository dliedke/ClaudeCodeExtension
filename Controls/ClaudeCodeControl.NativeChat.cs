/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native mode chat tab — hosts the transcript in a document tab with its own composer and selectors
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using static ClaudeCodeVS.UI.Loc;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Chat Tab Fields

        private NativeChatToolWindow _nativeChatWindow;

        /// <summary>True while the transcript is parented into the document tab instead of the panel.</summary>
        private bool _chatIsInTab;

        /// <summary>
        /// True once the chat has actually left the panel for its own tab — the point at which every
        /// panel control it mirrors (RefreshToolbarLayout's promoted buttons, ⚙/☰/⚡/🤖, and
        /// AttachDropdownButton/SendPromptButton next to the now-hidden prompt box) becomes redundant.
        /// Deliberately not just <see cref="IsNativeModeActive"/>: while the chat is still docked
        /// (ComposerMode.ActionsOnly) the panel is the only surface open, so its own toolbar must stay.
        /// </summary>
        internal bool IsChatDetachedToOwnTab => IsNativeModeActive && _chatIsInTab;

        /// <summary>Composer events are wired once for the lifetime of the control.</summary>
        private bool _composerWired;

        /// <summary>Guards against a second switch being started while a relaunch is in flight.</summary>
        private bool _nativeSwitchInProgress;

        /// <summary>
        /// The default session's counterpart to <see cref="NativeChatSessionState.MigrationSourceSessionId"/>
        /// — see that field's doc comment. Set wherever <c>_agentSession</c> is (re)assigned, consumed
        /// by the <c>SessionStarted</c> case in <c>ApplyAgentEvent</c>.
        /// </summary>
        private string _nativeSessionMigrationSourceId;

        /// <summary>Stores per-session send request handlers for proper cleanup.</summary>
        private Dictionary<string, EventHandler> _sessionSendHandlers = new Dictionary<string, EventHandler>();

        /// <summary>Stores per-session window closed handlers for proper cleanup.</summary>
        private Dictionary<string, EventHandler> _sessionClosedHandlers = new Dictionary<string, EventHandler>();

        /// <summary>Stores per-session window activated handlers for proper cleanup.</summary>
        private Dictionary<string, EventHandler> _sessionActivatedHandlers = new Dictionary<string, EventHandler>();

        /// <summary>Stores per-session transcript focus handlers for proper cleanup.</summary>
        private Dictionary<string, DependencyPropertyChangedEventHandler> _sessionFocusHandlers =
            new Dictionary<string, DependencyPropertyChangedEventHandler>();

        /// <summary>
        /// The chat tab the user last worked in, in a multi-tab (parallel sessions) setup.
        /// Null/empty means the default session — the one driven by <c>_agentSession</c> and shown
        /// either in the panel or in <see cref="_nativeChatWindow"/>. Read by
        /// <c>ResolveFocusedNativeSessionId()</c> (NativeMode.cs) so automated sends (build/runtime
        /// errors, custom commands, On Agent Finish follow-ups) go to whichever tab is actually focused
        /// instead of always the first session.
        /// </summary>
        private string _lastFocusedNativeSessionId;

        #endregion

        #region Chat Tab Hosting

        /// <summary>
        /// Pure decision behind how much of the default session's composer shows, split out so it is
        /// testable without a WPF tree. Native mode off is the only case with nothing to show; docked in
        /// the panel keeps just the action row (the panel's own prompt box is still the text input);
        /// in its own tab the composer is the only input surface, so it needs everything.
        /// </summary>
        internal static ChatTranscriptView.ComposerMode ResolveComposerMode(bool nativeActive, bool chatInTab)
        {
            if (!nativeActive)
            {
                return ChatTranscriptView.ComposerMode.Hidden;
            }

            return chatInTab ? ChatTranscriptView.ComposerMode.Full : ChatTranscriptView.ComposerMode.ActionsOnly;
        }

        /// <summary>
        /// Moves the transcript into its own document tab — the default home for native mode, so the
        /// conversation gets the full editor width and can be driven without the panel.
        /// <para>
        /// The transcript instance is never rebuilt: it is re-parented, so the messages already in it
        /// (and the session streaming into it) survive the move in both directions.
        /// </para>
        /// </summary>
        private async Task ShowNativeChatTabAsync(bool focusComposer)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (ChatTranscript == null)
            {
                return;
            }

            try
            {
                var package = await GetPackageAsync();
                if (package == null)
                {
                    Debug.WriteLine("ShowNativeChatTabAsync: could not get the package; the chat stays in the panel.");
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // A pane whose frame VS has already torn down (tab closed, layout reset, panel closed
                // together with the tab) is still handed back by FindToolWindow. Reusing it gives a
                // null frame, `frame?.Show()` no-ops, and the panel is left collapsed around a tab that
                // never appears — the "blank panel, only usage bars" state. Drop the stale pane so the
                // block below builds a fresh one.
                if (_nativeChatWindow != null && !(_nativeChatWindow.Frame is IVsWindowFrame))
                {
                    _nativeChatWindow.Closed -= OnNativeChatWindowClosed;
                    _nativeChatWindow.Activated -= OnDefaultNativeChatWindowActivated;
                    _nativeChatWindow = null;
                }

                if (_nativeChatWindow == null)
                {
                    _nativeChatWindow = package.FindToolWindow(typeof(NativeChatToolWindow), 0, true) as NativeChatToolWindow;
                    if (_nativeChatWindow != null)
                    {
                        _nativeChatWindow.Closed += OnNativeChatWindowClosed;
                        _nativeChatWindow.Activated += OnDefaultNativeChatWindowActivated;
                    }
                }

                IVsWindowFrame frame = _nativeChatWindow?.Frame as IVsWindowFrame;
                if (_nativeChatWindow == null || frame == null)
                {
                    // VS would not give us a document tab. Never leave the panel collapsed with the
                    // conversation nowhere: keep it in the panel so it stays usable, and let a later
                    // panel activation or ⧉ click try the tab again.
                    Debug.WriteLine("ShowNativeChatTabAsync: no usable chat tab frame; keeping the chat in the panel.");
                    if (_nativeChatWindow != null)
                    {
                        _nativeChatWindow.Closed -= OnNativeChatWindowClosed;
                        _nativeChatWindow.Activated -= OnDefaultNativeChatWindowActivated;
                        _nativeChatWindow = null;
                    }

                    EnsureNativeChatVisibleInPanel();
                    UpdateDetachButtonIcon(false);
                    return;
                }

                WireChatComposer();

                // Re-parent whenever the transcript is not already sitting in this pane — covers both
                // the first move out of the panel and a panel-reopen that pulls the chat back out of
                // the panel slot into a fresh tab.
                if (!_nativeChatWindow.HasChatContent)
                {
                    // A WPF element lives in exactly one visual tree, so it has to leave its old parent
                    // before the pane can take it.
                    DetachTranscriptFromCurrentParent();
                    _nativeChatWindow.SetChatContent(ChatTranscript);
                }

                _chatIsInTab = true;

                ChatTranscript.Visibility = Visibility.Visible;
                ChatTranscript.SetComposerMode(ResolveComposerMode(IsNativeModeActive, _chatIsInTab));
                UpdateChatComposerState();
                UpdateChatTabCaption();
                SetPanelTerminalAreaVisible(false);
                ApplyPromptPanelHiddenState();

                // Issue #151 follow-up: RefreshToolbarLayout is what actually collapses the panel's
                // own promoted buttons/⚙/☰/⚡/🤖 once _chatIsInTab is true — without this call here the
                // panel keeps showing its full toolbar (duplicated with the composer's mirrors) until
                // something unrelated happens to trigger a refresh.
                RefreshToolbarLayout();

                int showHr = frame.Show();
                if (ErrorHandler.Failed(showHr))
                {
                    // The frame existed but would not surface. Same recovery as a null frame: fall
                    // back to the panel rather than leaving it collapsed around an invisible tab.
                    Debug.WriteLine($"ShowNativeChatTabAsync: frame.Show() failed (hr={showHr}); keeping the chat in the panel.");
                    EnsureNativeChatVisibleInPanel();
                    UpdateDetachButtonIcon(false);
                    return;
                }

                UpdateDetachButtonIcon(true);

                if (focusComposer)
                {
                    ChatTranscript.FocusComposer();
                }

                ChatTranscript.ScrollToEndIfFollowing();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening the chat tab: {ex.Message}");
            }
        }

        /// <summary>
        /// Brings the transcript back into the panel slot. Called when the user closes the tab and when
        /// native mode ends — never loses the conversation, only where it is drawn.
        /// </summary>
        private void ReturnNativeChatToPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_chatIsInTab || ChatTranscript == null)
            {
                return;
            }

            try
            {
                _nativeChatWindow?.SetChatContent(null);

                if (TerminalSlotGrid != null && !TerminalSlotGrid.Children.Contains(ChatTranscript))
                {
                    TerminalSlotGrid.Children.Add(ChatTranscript);
                }

                _chatIsInTab = false;

                // Inside the panel the prompt box sits directly above the transcript, so the composer
                // only keeps its action row (selectors + mirrored config buttons) rather than a second
                // input box saying the same thing — see ResolveComposerMode.
                ChatTranscript.SetComposerMode(ResolveComposerMode(IsNativeModeActive, _chatIsInTab));
                ChatTranscript.Visibility = IsNativeModeActive ? Visibility.Visible : Visibility.Collapsed;

                SetPanelTerminalAreaVisible(true);
                ApplyPromptPanelHiddenState();

                // Mirror image of the ShowNativeChatTabAsync call: now that the chat is back in the
                // panel, its own toolbar controls need to reappear.
                RefreshToolbarLayout();

                // The chat lives in the panel again, so a panel hidden on its behalf has to come back.
                RestoreAutoHiddenPanel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error returning the chat to the panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Recovery path for a transcript that ended up parented nowhere — its document tab was torn
        /// down by VS without a <see cref="NativeChatToolWindow.Closed"/> notification we could act on
        /// (closing the panel and the tab together is the repro). Unlike
        /// <see cref="ReturnNativeChatToPanel"/> this does not require <see cref="_chatIsInTab"/> to be
        /// set, and it re-parents the transcript wherever it currently is. The result is always a
        /// usable panel; the caller decides whether to try the tab again afterwards.
        /// </summary>
        private void EnsureNativeChatVisibleInPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null)
            {
                return;
            }

            try
            {
                DetachTranscriptFromCurrentParent();

                if (TerminalSlotGrid != null && !TerminalSlotGrid.Children.Contains(ChatTranscript))
                {
                    TerminalSlotGrid.Children.Add(ChatTranscript);
                }

                _chatIsInTab = false;

                ChatTranscript.SetComposerMode(ResolveComposerMode(IsNativeModeActive, _chatIsInTab));
                ChatTranscript.Visibility = IsNativeModeActive ? Visibility.Visible : Visibility.Collapsed;

                SetPanelTerminalAreaVisible(true);
                ApplyPromptPanelHiddenState();
                RefreshToolbarLayout();
                RestoreAutoHiddenPanel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error restoring the chat into the panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Unhooks the transcript from whatever visual tree it is in (the panel slot, the chat tab's
        /// host grid, or a stray parent) so it can be added to a new one without WPF's "element already
        /// has a parent" exception. Callers re-add it wherever it belongs afterwards.
        /// </summary>
        private void DetachTranscriptFromCurrentParent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null)
            {
                return;
            }

            if (_nativeChatWindow != null && _nativeChatWindow.HasChatContent)
            {
                _nativeChatWindow.SetChatContent(null);
            }

            if (TerminalSlotGrid != null && TerminalSlotGrid.Children.Contains(ChatTranscript))
            {
                TerminalSlotGrid.Children.Remove(ChatTranscript);
            }

            if (ChatTranscript.Parent is System.Windows.Controls.Panel host)
            {
                host.Children.Remove(ChatTranscript);
            }
        }

        /// <summary>
        /// Shows or hides the panel's terminal group box. While the chat is in its own tab that slot has
        /// nothing to draw, so it is collapsed and its minimum size released — the same treatment a
        /// detached terminal gets, and the prompt box expands into the freed space.
        /// </summary>
        private void SetPanelTerminalAreaVisible(bool visible)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (TerminalGroupBox == null || MainGrid == null)
            {
                return;
            }

            // A detached terminal already collapsed this area for its own reasons; putting it back here
            // would show an empty box.
            if (visible && _isTerminalDetached)
            {
                return;
            }

            TerminalGroupBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            int terminalSlot = (_settings?.InvertLayout == true) ? 0 : 2;

            if (visible)
            {
                RestoreTerminalSlotMinimumSize();
            }
            else if (LayoutGridIsVertical)
            {
                MainGrid.ColumnDefinitions[terminalSlot].MinWidth = 0;
            }
            else
            {
                MainGrid.RowDefinitions[terminalSlot].MinHeight = 0;
            }

            MainGrid.UpdateLayout();
        }

        private void OnNativeChatWindowClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // _chatIsInTab still set means the user (or VS) closed the tab itself. A programmatic close —
            // ⧉ dock, native mode ending — goes through CloseNativeChatTab, which has already moved the
            // transcript back into the panel and cleared the flag before the frame closes.
            //
            // The tab just closes: the conversation is not docked into the panel (issue #168 follow-up).
            // The transcript is only taken out of the dying pane — the session keeps running and
            // streaming into it — and _chatIsInTab stays set, so the panel keeps its "chat in its own
            // tab" layout with 💬 Show Chat as the way to reopen it (IsChatTabClosed).
            bool closedByUser = _chatIsInTab;
            if (_chatIsInTab && _nativeChatWindow != null && _nativeChatWindow.HasChatContent)
            {
                _nativeChatWindow.SetChatContent(null);
            }

            // The pane is transient: once closed it is gone, so the next open has to build a new one.
            if (_nativeChatWindow != null)
            {
                _nativeChatWindow.Closed -= OnNativeChatWindowClosed;
                _nativeChatWindow.Activated -= OnDefaultNativeChatWindowActivated;
                _nativeChatWindow = null;
            }

            // The main chat's tab closed by hand is not reopened with the solution either.
            if (closedByUser)
            {
                RecordOpenChatTabs();
            }
        }

        /// <summary>
        /// The chat's home is its own tab but the user closed that tab. The conversation is intact,
        /// just not shown anywhere; 💬 Show Chat (or anything that needs the user) reopens it.
        /// </summary>
        private bool IsChatTabClosed => IsChatDetachedToOwnTab && _nativeChatWindow == null;

        /// <summary>
        /// Reopens a closed chat tab so the panel's own prompt box has somewhere for the answer to
        /// land. Called only for that explicit case (the user typed into the panel while the tab was
        /// closed) — a question/permission card blocking the agent no longer forces the tab back open
        /// on its own; it renders into the (still running, just not shown) transcript and waits for
        /// 💬 Show Chat, so a tab the user deliberately closed does not keep reappearing on its own.
        /// </summary>
        private void ReopenClosedChatTab()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsChatTabClosed)
            {
                return;
            }

#pragma warning disable VSSDK007, VSTHRD110
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => ShowNativeChatTabAsync(focusComposer: false));
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// The default session's tab was brought to the front. Tracked as "null" (the sentinel for the
        /// default session) rather than an id, since the default session is never registered in
        /// <c>_nativeSessions</c> — see <c>ResolveFocusedNativeSessionId()</c> in NativeMode.cs.
        /// </summary>
        private void OnDefaultNativeChatWindowActivated(object sender, EventArgs e)
        {
            _lastFocusedNativeSessionId = null;
        }

        /// <summary>
        /// Records which chat the user is working in, for <c>ResolveFocusedNativeSessionId()</c>. Pass
        /// null for the default session (the one on <c>_agentSession</c>).
        /// <para>
        /// Driven by keyboard focus entering a transcript rather than by the pane's own
        /// <see cref="NativeChatToolWindow.Activated"/> notification alone: that notification comes from
        /// <c>FRAMESHOW_TabActivated</c>, which VS only raises when switching between tabs **in the same
        /// tab group**. With one chat docked to the side and another in the document area — a normal way
        /// to run two sessions, and how this was first reported broken — both panes are visible at once,
        /// so clicking between them moves focus without firing any frame notification and the tracked id
        /// never left the default session. Keyboard focus is the signal that actually distinguishes them
        /// in every layout.
        /// </para>
        /// </summary>
        private void MarkNativeSessionFocused(string sessionId)
        {
            _lastFocusedNativeSessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId;
        }

        /// <summary>
        /// Subscribes one transcript so focus landing anywhere inside it marks its session as the one
        /// automated sends target. <paramref name="sessionId"/> is null for the default session.
        /// </summary>
        private void WireTranscriptFocusTracking(ChatTranscriptView transcript, string sessionId)
        {
            if (transcript == null)
            {
                return;
            }

            DependencyPropertyChangedEventHandler handler = (s, e) =>
            {
                // Only the transition into the control; losing focus must not clear the tracked id, or
                // clicking into the code editor would send the next build error back to the default tab.
                if (e.NewValue is bool focused && focused)
                {
                    MarkNativeSessionFocused(sessionId);
                }
            };

            // Keyed by id so a re-shown session replaces its own handler instead of stacking a second
            // one; the default session uses a fixed key since its id is the null sentinel.
            string key = sessionId ?? DefaultSessionFocusKey;
            if (_sessionFocusHandlers.TryGetValue(key, out var previous))
            {
                transcript.IsKeyboardFocusWithinChanged -= previous;
            }

            _sessionFocusHandlers[key] = handler;
            transcript.IsKeyboardFocusWithinChanged += handler;
        }

        /// <summary>Dictionary key standing in for the default session's null id.</summary>
        private const string DefaultSessionFocusKey = "\0default";

        /// <summary>
        /// Closes the chat tab, if it is open. Used when native mode ends: leaving an empty tab behind
        /// would look like the conversation was lost.
        /// </summary>
        private void CloseNativeChatTab()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Take the reference before closing: CloseFrame fires the Closed handler, which clears the
            // field on its way out.
            NativeChatToolWindow window = _nativeChatWindow;

            ReturnNativeChatToPanel();

            if (window == null)
            {
                return;
            }

            try
            {
                if (window.Frame is IVsWindowFrame frame)
                {
                    frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error closing the chat tab: {ex.Message}");
            }

            window.Closed -= OnNativeChatWindowClosed;
            _nativeChatWindow = null;
        }

        /// <summary>
        /// Shows a specific session's transcript in its own tool window tab (multi-session support).
        /// </summary>
        private async Task ShowSessionInTabAsync(NativeChatSessionState session, bool focusComposer)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (session?.ChatTranscript == null)
                return;

            try
            {
                var package = await GetPackageAsync();
                if (package == null)
                {
                    Debug.WriteLine("ShowSessionInTabAsync: could not get the package.");
                    return;
                }

                // Create/get tool window for this session (using session's window ID)
                var window = package.FindToolWindow(typeof(NativeChatToolWindow), session.WindowId, true) as NativeChatToolWindow;
                if (window == null)
                {
                    Debug.WriteLine($"ShowSessionInTabAsync: could not create window for session {session.SessionId}.");
                    return;
                }

                // Set the transcript content
                window.SetChatContent(session.ChatTranscript);
                session.Window = window;

                // Wire close event (with proper handler storage for cleanup)
                string sessionId = session.SessionId;
                EventHandler closedHandler = (s, e) => OnSessionWindowClosed(sessionId);
                _sessionClosedHandlers[sessionId] = closedHandler;
                window.Closed += closedHandler;

                // Wire activation so automated sends (build/runtime errors, custom commands, On Agent
                // Finish follow-ups) can target whichever tab the user actually has in front.
                EventHandler activatedHandler = (s, e) => MarkNativeSessionFocused(sessionId);
                _sessionActivatedHandlers[sessionId] = activatedHandler;
                window.Activated += activatedHandler;

                // The frame notification above only covers same-tab-group switches; focus entering the
                // transcript is what catches a side-docked chat being clicked. See MarkNativeSessionFocused.
                WireTranscriptFocusTracking(session.ChatTranscript, sessionId);

                // Wire composer events for this session
                WireSessionComposerEvents(session.ChatTranscript, sessionId);

                // Parallel session tabs have no docked-in-panel counterpart, so they are always Full.
                session.ChatTranscript.SetComposerMode(ChatTranscriptView.ComposerMode.Full);
                UpdateChatComposerState();
                UpdateSessionTabCaption(session);

                // Mirrors the panel's promoted toolbar buttons into this brand-new tab immediately,
                // instead of leaving it on just ⚡/☰/⚙ until something else happens to call
                // RefreshToolbarLayout (e.g. the ☰ Tools menu being opened).
                RefreshToolbarLayout();

                // Show the window
                if (window.Frame is IVsWindowFrame frame)
                {
                    frame.Show();
                }

                if (focusComposer)
                {
                    session.ChatTranscript.Focus();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing session in tab: {ex.Message}");
            }
        }

        /// <summary>
        /// Wires the composer of one session's transcript. The panel's own transcript goes through
        /// <see cref="WireChatComposer"/>; this is the same list, now including the model/effort/
        /// permission selectors (v163.0) — <see cref="OnComposerSelectorClicked"/> and
        /// <see cref="OnComposerEffortChanged"/> resolve the owning session from the sender and relaunch
        /// through <see cref="RelaunchSessionAsync"/> instead of the panel's global one. Provider
        /// switching is the one selector still panel-only (see the comment in
        /// <see cref="OnComposerSelectorClicked"/>).
        /// </summary>
        private void WireSessionComposerEvents(ChatTranscriptView transcript, string sessionId)
        {
            // Routed to this session's agent, so it is a closure and has to be stored to be removed.
            // The handler switches to the main thread itself before touching any UI, so the analyzer's
            // main-thread requirement is already satisfied inside it.
#pragma warning disable VSTHRD010
            EventHandler sendHandler = (s, e) => OnSessionComposerSendRequested(sessionId);
#pragma warning restore VSTHRD010
            _sessionSendHandlers[sessionId] = sendHandler;
            transcript.SendRequested += sendHandler;

            // Stop/Interaction resolve the session from the sender, so the shared handlers are correct.
            transcript.StopRequested -= OnChatStopRequested;
            transcript.StopRequested += OnChatStopRequested;

            transcript.InteractionResolved -= OnChatInteractionResolved;
            transcript.InteractionResolved += OnChatInteractionResolved;

            // Selectors resolve the owning session from the sender too — see the class summary above.
            transcript.SelectorClicked -= OnComposerSelectorClicked;
            transcript.SelectorClicked += OnComposerSelectorClicked;

            transcript.EffortChanged -= OnComposerEffortChanged;
            transcript.EffortChanged += OnComposerEffortChanged;

            transcript.UsageRequested -= OnComposerUsageRequested;
            transcript.UsageRequested += OnComposerUsageRequested;
            transcript.UsageDetailsRequested -= OnComposerUsageDetailsRequested;
            transcript.UsageDetailsRequested += OnComposerUsageDetailsRequested;

            // Toolbar and composer affordances. These were missing entirely, which is why none of the
            // buttons under the prompt box did anything in a new tab.
            transcript.FilesDropped += OnComposerFilesDropped;
            transcript.AttachRequested += OnComposerAttachRequested;
            transcript.InsertSelectionRequested += OnComposerInsertSelectionRequested;
            transcript.InsertActiveFilePathRequested += OnComposerInsertActiveFilePathRequested;
            transcript.ClearChatRequested += OnComposerClearChatRequested;
            transcript.NewChatRequested += OnComposerNewChatRequested;
            transcript.RenameSessionRequested += OnComposerRenameSessionRequested;
            transcript.WelcomeSessionRequested += OnWelcomeSessionRequested;
            transcript.WelcomeAllSessionsRequested += OnWelcomeAllSessionsRequested;
            transcript.ColorPickerRequested += OnComposerColorPickerRequested;
            transcript.ZoomChanged += OnComposerZoomChanged;
            transcript.ComposerHeightChanged += OnComposerHeightChanged;
            transcript.PasteRequested += OnComposerPasteRequested;
            transcript.HistoryPreviousRequested += OnComposerHistoryPreviousRequested;
            transcript.HistoryNextRequested += OnComposerHistoryNextRequested;
            transcript.LinkClicked += OnChatLinkClicked;
            transcript.ToolFileOpenRequested += OnToolFileOpenRequested;

            // Same shared handlers as the default session's composer: the menus they open (Settings,
            // Tools, Custom Commands) and the toolbar actions they invoke already resolve the focused
            // session on their own (ResolveFocusedNativeSessionId), so there is nothing session-specific
            // to close over here.
            transcript.ConfigMenuClicked -= OnComposerConfigMenuClicked;
            transcript.ConfigMenuClicked += OnComposerConfigMenuClicked;

            transcript.PromotedButtonClicked -= OnComposerPromotedButtonClicked;
            transcript.PromotedButtonClicked += OnComposerPromotedButtonClicked;
        }

        /// <summary>Handles send request from a specific session's composer.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnSessionComposerSendRequested(string sessionId)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var session = GetSession(sessionId);
                if (session?.ChatTranscript == null)
                    return;

                string text = session.ChatTranscript.ComposerText.Trim();
                bool hasFiles = session.AttachedFiles.Count > 0;
                if (string.IsNullOrEmpty(text) && !hasFiles)
                    return;

                // Typed /clear = the tab's Clear Chat button (the sender resolves to this session).
                if (!hasFiles && IsBareClearCommand(text))
                {
                    session.ChatTranscript.ComposerText = string.Empty;
                    OnComposerClearChatRequested(session.ChatTranscript, EventArgs.Empty);
                    return;
                }

                // Same "Files attached:" header the panel sends, built from this tab's own list so an
                // attachment staged in another chat is never dragged along.
                var fullPrompt = new StringBuilder();
                if (hasFiles)
                {
                    fullPrompt.Append(BuildAttachmentPromptBlock(
                        session.AttachedFiles.ToList(), IsWslProvider(session.SelectedProvider)));
                }
                if (!string.IsNullOrEmpty(text))
                {
                    fullPrompt.AppendLine(text);
                }

                session.ChatTranscript.ComposerText = string.Empty;
                session.AttachedFiles.Clear();
                UpdateSessionAttachmentChips(session);

                await SendPromptToNativeAgentAsync(fullPrompt.ToString(), sessionId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat send failed for session {sessionId}: {ex.Message}");
            }
        }

        /// <summary>Handles a session window being closed.</summary>
        private void OnSessionWindowClosed(string sessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var session = GetSession(sessionId);
                if (session != null)
                {
                    // Remove the send handler
                    if (_sessionSendHandlers.TryGetValue(sessionId, out var sendHandler))
                    {
                        session.ChatTranscript.SendRequested -= sendHandler;
                        _sessionSendHandlers.Remove(sessionId);
                    }

                    // Remove the closed handler
                    _sessionClosedHandlers.Remove(sessionId);

                    // Remove the activated/focus handlers; if this closed tab was the focused one,
                    // automated sends fall back to the default session rather than a now-gone id.
                    _sessionActivatedHandlers.Remove(sessionId);

                    if (_sessionFocusHandlers.TryGetValue(sessionId, out var focusHandler))
                    {
                        session.ChatTranscript.IsKeyboardFocusWithinChanged -= focusHandler;
                        _sessionFocusHandlers.Remove(sessionId);
                    }

                    if (_lastFocusedNativeSessionId == sessionId)
                    {
                        _lastFocusedNativeSessionId = null;
                    }

                    RemoveSession(sessionId);

                    // A tab the user closes is not reopened with the solution (a no-op while the
                    // solution or Visual Studio is closing — see RecordChatTabsBeforeClose).
                    RecordOpenChatTabs();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling session window close: {ex.Message}");
            }
        }

        /// <summary>
        /// The ⧉ dock/undock control for native mode: docks the chat back into the panel when it is
        /// in its own tab (or its tab was closed), or pops it out to its own tab when it is docked.
        /// Closing the tab directly does not dock it — see <see cref="OnNativeChatWindowClosed"/>.
        /// </summary>
        private async Task ToggleChatTabAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_chatIsInTab)
            {
                // Dock the chat back into the panel. CloseNativeChatTab tears the tab down, which
                // fires OnNativeChatWindowClosed -> ReturnNativeChatToPanel.
                CloseNativeChatTab();
                UpdateDetachButtonIcon(false);
                return;
            }

            await ShowNativeChatTabAsync(focusComposer: true);
            HidePanelIfChatInOwnTab();
        }

        /// <summary>
        /// "Show Chat" (issue #168) — the panel's 💬 Show Chat button and View &gt; Other Windows &gt;
        /// Claude Code Chat. Whatever state the chat is in, this ends with the conversation on screen:
        /// a tab VS hid (debug/design layout switch) is shown again, a tab that would not surface is
        /// rebuilt once, and if VS still refuses the chat falls back into the panel, which is shown.
        /// Native mode off means the panel's terminal is the chat, so the panel is what gets shown.
        /// </summary>
        internal async Task ShowNativeChatAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                if (!IsNativeModeActive || ChatTranscript == null)
                {
                    ShowPanelFrame();
                    return;
                }

                // Docked into the panel on purpose (⧉): the panel is where the chat lives.
                if (!_chatIsInTab && TerminalSlotGrid != null && TerminalSlotGrid.Children.Contains(ChatTranscript))
                {
                    ShowPanelFrame();
                    PromptTextBox?.Focus();
                    return;
                }

                // The default session's own tab was closed and another chat tab is still open: bring
                // that one forward instead of resurrecting the closed tab — closing a tab should not
                // keep bringing it back just because it was "the main one" (v200.0).
                if (IsChatTabClosed)
                {
                    NativeChatSessionState promoted = GetPromotableSession();
                    if (promoted != null)
                    {
                        BringSessionTabToFront(promoted);
                        return;
                    }
                }

                await ShowNativeChatTabAsync(focusComposer: true);

                if (!IsNativeChatTabOnScreen())
                {
                    // The frame said yes but is still not visible: drop it and build a fresh tab.
                    CloseNativeChatTab();
                    await ShowNativeChatTabAsync(focusComposer: true);
                }

                if (!IsNativeChatTabOnScreen())
                {
                    EnsureNativeChatVisibleInPanel();
                    UpdateDetachButtonIcon(false);
                    ShowPanelFrame();
                    return;
                }

                HidePanelIfChatInOwnTab();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing the chat: {ex.Message}");
            }
        }

        /// <summary>True when the default session's chat is in its own tab and VS reports that tab visible.</summary>
        private bool IsNativeChatTabOnScreen()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return _chatIsInTab
                   && _nativeChatWindow != null
                   && _nativeChatWindow.HasChatContent
                   && _nativeChatWindow.Frame is IVsWindowFrame frame
                   && frame.IsVisible() == VSConstants.S_OK;
        }

        /// <summary>The default session's chat tab is open (startup restore check, issue #209).</summary>
        internal bool IsChatTabOpen
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return IsNativeChatTabOnScreen();
            }
        }

        /// <summary>The default session's chat tab frame, or null when it is not open.</summary>
        internal IVsWindowFrame ChatTabFrame
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _nativeChatWindow?.Frame as IVsWindowFrame;
            }
        }

        /// <summary>
        /// Called by the package as Visual Studio shuts down, while every frame is still up: remembers
        /// whether the chat tab was open with the panel closed. That layout is not restored by VS —
        /// the closed panel is dropped and the chat tab is transient — so the package rebuilds it at
        /// the next start (issue #209).
        /// </summary>
        internal void RecordChatLayoutForRestart()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null)
            {
                return;
            }

            // The chat tabs to reopen next time, while every tab is still up.
            RecordChatTabsBeforeClose();

            bool panelOpen = _toolWindow?.Frame is IVsWindowFrame panelFrame && panelFrame.IsVisible() == VSConstants.S_OK;
            bool restore = ShouldRestoreChatWithoutPanel(IsNativeChatTabOnScreen(), panelOpen);
            if (_settings.RestoreChatWithoutPanel == restore)
            {
                return;
            }

            _settings.RestoreChatWithoutPanel = restore;
            SaveSettings();
        }

        /// <summary>
        /// Only the chat-without-panel layout needs the package's help: an open panel is restored by VS
        /// and opens the chat itself, and a closed chat should stay closed.
        /// </summary>
        internal static bool ShouldRestoreChatWithoutPanel(bool chatTabOpen, bool panelOpen) => chatTabOpen && !panelOpen;

        /// <summary>Shows (and activates) the extension's own panel.</summary>
        private void ShowPanelFrame()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_toolWindow?.Frame is IVsWindowFrame frame)
            {
                frame.Show();
            }
        }

        /// <summary>
        /// Hides the panel once the chat is on screen in its own tab (<see cref="ClaudeCodeSettings.AutoHidePanelInNativeMode"/>):
        /// from then on the panel only holds the usage bars and a Show Chat button. Hide, never close —
        /// the control, the agent session and the chat tab keep running, exactly as when the user
        /// closes the panel with its X.
        /// </summary>
        private void HidePanelIfChatInOwnTab()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isShuttingDown || _settings?.AutoHidePanelInNativeMode != true || !IsNativeModeActive || !IsNativeChatTabOnScreen())
            {
                return;
            }

            try
            {
                if (_toolWindow?.Frame is IVsWindowFrame frame
                    && frame.IsVisible() == VSConstants.S_OK
                    && ErrorHandler.Succeeded(frame.Hide()))
                {
                    _panelAutoHidden = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error hiding the panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the panel again if <see cref="HidePanelIfChatInOwnTab"/> hid it: the chat is coming
        /// back into the panel, or native mode ended and the panel hosts the terminal again.
        /// </summary>
        private void RestoreAutoHiddenPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_panelAutoHidden || _isShuttingDown)
            {
                return;
            }

            _panelAutoHidden = false;

            // The hide armed the "panel reopened" reconcile, which would pull the chat straight back
            // out into a tab — the opposite of why the panel is being shown.
            _panelHiddenSinceShow = false;
            ShowPanelFrame();
        }

        private void ShowChatTabButton_Click(object sender, RoutedEventArgs e)
        {
#pragma warning disable VSSDK007, VSTHRD110
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(ShowNativeChatAsync);
#pragma warning restore VSSDK007, VSTHRD110
        }

        private void UpdateChatTabCaption()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_nativeChatWindow == null)
            {
                return;
            }

            string title = GetCurrentNativeSessionTitle();
            string captionTitle = string.IsNullOrWhiteSpace(title) ? GetAutoChatTitle(null) : title;
            _nativeChatWindow.UpdateCaption(DecorateChatTabCaption(null, BuildChatTabCaption(GetActiveOrSelectedProvider(), captionTitle)));

            // Also kept as a header above the transcript: the VS tab strip truncates, and with several
            // chats open the header is what tells them apart once a tab is selected.
            ChatTranscript?.SetSessionTitle(title);
            ChatTranscript?.SetSessionTitleColor(GetCurrentNativeSessionColor());
        }

        /// <summary>
        /// Tab caption for a chat: the session's title (custom or automatic) when it has one, so several
        /// open chats are distinguishable from the tab strip alone — the agent's name in front of it
        /// left no room for the title once VS truncated the tab (v259.0). Untitled: the agent's name.
        /// </summary>
        private string BuildChatTabCaption(AiProvider? provider, string sessionTitle)
        {
            if (!string.IsNullOrWhiteSpace(sessionTitle))
            {
                return sessionTitle.Trim();
            }

            string name = GetProviderDisplayName(provider);
            return string.IsNullOrEmpty(name) ? L("Chat") : L("{0} Chat", name);
        }

        /// <summary>Refreshes the caption and header of one session's tab, leaving the others alone.</summary>
        private void UpdateSessionTabCaption(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session?.ChatTranscript == null)
            {
                return;
            }

            string agentSessionId = session.AgentSession?.SessionId;
            string title = GetNativeSessionTitle(agentSessionId);
            if (string.IsNullOrWhiteSpace(title) && session.IssueNumber > 0)
            {
                // A GitHub issue fix tab is named after its issue until the user renames it.
                title = L("Issue #{0}", session.IssueNumber) + (string.IsNullOrWhiteSpace(session.IssueTitle) ? string.Empty : ": " + session.IssueTitle.Trim());
            }

            string captionTitle = string.IsNullOrWhiteSpace(title) ? GetAutoChatTitle(session) : title;
            session.Window?.UpdateCaption(DecorateChatTabCaption(session, BuildChatTabCaption(session.SelectedProvider, captionTitle)));
            session.ChatTranscript.SetSessionTitle(title);
            session.ChatTranscript.SetSessionTitleColor(GetNativeSessionColor(agentSessionId));
        }

        /// <summary>The user-assigned title for an agent session id, or empty when it has none.</summary>
        private string GetNativeSessionTitle(string agentSessionId)
        {
            if (string.IsNullOrEmpty(agentSessionId) || _settings?.SessionCustomTitles == null)
            {
                return string.Empty;
            }

            _settings.SessionCustomTitles.TryGetValue(agentSessionId, out string title);
            return title ?? string.Empty;
        }

        /// <summary>
        /// The color for an agent session id: the one the user picked for that session, else the
        /// Native Color Schema default from Settings, else empty (built-in accent).
        /// </summary>
        private string GetNativeSessionColor(string agentSessionId)
        {
            string color = null;
            if (!string.IsNullOrEmpty(agentSessionId))
            {
                _settings?.SessionTitleColors?.TryGetValue(agentSessionId, out color);
            }

            if (!string.IsNullOrEmpty(color))
            {
                return color;
            }

            return _settings?.DefaultNativeSessionColor ?? string.Empty;
        }

        /// <summary>
        /// Carries Claude's <c>/color</c> over to the chat header: when the CLI answers "Session color set to: X"
        /// (or "reset to default") the session's stored color follows, exactly as if it had been picked with
        /// the palette button. The CLI's own prompt-bar color has no UI in native mode, so this is the only
        /// place the command can show.
        /// </summary>
        private void ApplyClaudeColorNotice(string agentSessionId, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrEmpty(agentSessionId) || _settings == null
                || !IsClaudeProvider(GetActiveOrSelectedProvider())
                || !ClaudeSessionColor.TryParseNotice(text, out string hex))
            {
                return;
            }

            if (_settings.SessionTitleColors == null)
            {
                _settings.SessionTitleColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (string.IsNullOrEmpty(hex))
            {
                _settings.SessionTitleColors.Remove(agentSessionId);
            }
            else
            {
                _settings.SessionTitleColors[agentSessionId] = hex;
            }

            SaveSettings();
            RefreshNativeSessionColors();
        }

        /// <summary>Re-applies session colors to the panel transcript and every open chat tab.</summary>
        private void RefreshNativeSessionColors()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Direct call: UpdateChatTabCaption bails out while the chat is docked in the panel.
            ChatTranscript?.SetSessionTitleColor(GetCurrentNativeSessionColor());

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = new List<NativeChatSessionState>(_nativeSessions.Values);
            }

            foreach (NativeChatSessionState session in sessions)
            {
                UpdateSessionTabCaption(session);
            }
        }

        /// <summary>
        /// Re-keys a custom title/color from an old session id to the one the CLI just confirmed
        /// (v163.1, issue reported after v163.0). <c>ClaudeStreamJsonSession.SessionId</c> documents
        /// why this is needed: "a --resume relaunch can come back with a new one" — every relaunch that
        /// preserves the conversation (model, effort, permission, plan-mode switch; an interrupt; even
        /// a CLI update) can silently swap the id a rename or color pick was stored under, orphaning
        /// it. Both dictionaries are also read by the Session History window keyed by the CLI's own
        /// on-disk id (see the doc comment on <see cref="OnComposerRenameSessionRequested"/>), so this
        /// re-keys the existing entry instead of switching to an extension-owned synthetic id, which
        /// would sever that integration.
        /// </summary>
        private void MigrateSessionTitleAndColor(string oldSessionId, string newSessionId)
        {
            if (_settings == null || string.IsNullOrEmpty(oldSessionId) || string.IsNullOrEmpty(newSessionId) ||
                string.Equals(oldSessionId, newSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool changed = false;

            if (_settings.SessionCustomTitles != null &&
                _settings.SessionCustomTitles.TryGetValue(oldSessionId, out string title))
            {
                _settings.SessionCustomTitles.Remove(oldSessionId);
                _settings.SessionCustomTitles[newSessionId] = title;
                changed = true;
            }

            if (_settings.SessionTitleColors != null &&
                _settings.SessionTitleColors.TryGetValue(oldSessionId, out string color))
            {
                _settings.SessionTitleColors.Remove(oldSessionId);
                _settings.SessionTitleColors[newSessionId] = color;
                changed = true;
            }

            if (changed)
            {
                SaveSettings();
            }
        }

        /// <summary>
        /// The session whose transcript raised a composer event, or null for the panel's own transcript.
        /// Lets the shared handlers act on the tab the user actually clicked in.
        /// </summary>
        private NativeChatSessionState ResolveSessionFromSender(object sender)
        {
            var transcript = sender as ChatTranscriptView;
            if (transcript == null)
            {
                return null;
            }

            lock (_sessionLock)
            {
                foreach (KeyValuePair<string, NativeChatSessionState> pair in _nativeSessions)
                {
                    if (ReferenceEquals(pair.Value.ChatTranscript, transcript))
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The user-assigned title (✎ button, or the Session History rename) for the session currently
        /// running in the tab, or empty when it has none or the session id isn't known yet.
        /// </summary>
        private string GetCurrentNativeSessionTitle()
        {
            return GetNativeSessionTitle(_agentSession?.SessionId);
        }

        /// <summary>
        /// The user-assigned title color (color swatch) for the session currently running in the
        /// tab, or empty when it has none or the session id isn't known yet — falls back to the
        /// default accent color.
        /// </summary>
        private string GetCurrentNativeSessionColor()
        {
            return GetNativeSessionColor(_agentSession?.SessionId);
        }

        #endregion

        #region Composer Wiring

        private void WireChatComposer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_composerWired || ChatTranscript == null)
            {
                return;
            }

            ChatTranscript.SendRequested += OnComposerSendRequested;
            ChatTranscript.FilesDropped += OnComposerFilesDropped;
            ChatTranscript.AttachRequested += OnComposerAttachRequested;
            ChatTranscript.InsertSelectionRequested += OnComposerInsertSelectionRequested;
            ChatTranscript.InsertActiveFilePathRequested += OnComposerInsertActiveFilePathRequested;
            ChatTranscript.SelectorClicked += OnComposerSelectorClicked;
            ChatTranscript.EffortChanged += OnComposerEffortChanged;
            ChatTranscript.UsageRequested += OnComposerUsageRequested;
            ChatTranscript.UsageDetailsRequested += OnComposerUsageDetailsRequested;
            ChatTranscript.ClearChatRequested += OnComposerClearChatRequested;
            ChatTranscript.NewChatRequested += OnComposerNewChatRequested;
            ChatTranscript.RenameSessionRequested += OnComposerRenameSessionRequested;
            ChatTranscript.WelcomeSessionRequested += OnWelcomeSessionRequested;
            ChatTranscript.WelcomeAllSessionsRequested += OnWelcomeAllSessionsRequested;
            ChatTranscript.ColorPickerRequested += OnComposerColorPickerRequested;
            ChatTranscript.ZoomChanged += OnComposerZoomChanged;
            ChatTranscript.ComposerHeightChanged += OnComposerHeightChanged;
            ChatTranscript.PasteRequested += OnComposerPasteRequested;
            ChatTranscript.HistoryPreviousRequested += OnComposerHistoryPreviousRequested;
            ChatTranscript.HistoryNextRequested += OnComposerHistoryNextRequested;
            ChatTranscript.ComposerPreviewKeyDown += ComposerInput_AtMentionPreviewKeyDown;
            ChatTranscript.ComposerInputBox.TextChanged += ComposerInput_AtMentionTextChanged;
            ChatTranscript.LinkClicked += OnChatLinkClicked;
            ChatTranscript.ToolFileOpenRequested += OnToolFileOpenRequested;
            ChatTranscript.ConfigMenuClicked += OnComposerConfigMenuClicked;
            ChatTranscript.PromotedButtonClicked += OnComposerPromotedButtonClicked;

            // Null = the default session. The transcript object survives being re-parented between the
            // panel and its tab, so this one subscription covers both homes for the rest of its life.
            WireTranscriptFocusTracking(ChatTranscript, null);

            _composerWired = true;
        }

        /// <summary>
        /// Applies the persisted look of the chat: font, Ctrl+Scroll zoom and composer height. Called
        /// wherever the transcript becomes visible — in the tab and in the panel — so the conversation
        /// always comes back the size the user left it, and again when the settings dialog changes it.
        /// </summary>
        private void ApplyChatAppearance()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || _settings == null)
            {
                return;
            }

            ChatTranscript.SetChatFont(_settings.NativeChatFontFaceName, _settings.NativeChatFontSizePt);
            ChatTranscript.Zoom = _settings.NativeChatZoom;
            ChatTranscript.ComposerHeight = _settings.NativeChatComposerHeight;
            ApplyChatIconStyle();
        }

        /// <summary>
        /// Ctrl+V in the composer: an image on the clipboard becomes an attachment, exactly as it does
        /// in the panel's prompt box. Anything else falls through to the text box's own paste.
        /// </summary>
        private void OnComposerPasteRequested(object sender, ChatPasteEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            if (owner != null)
            {
                // The image goes into the tab the user pasted in, not into the panel's list.
                if (TrySaveClipboardImage(out string pastedPath))
                {
                    AddSessionAttachments(owner, new[] { pastedPath });
                    e.Handled = true;
                }
                return;
            }

            if (TryPasteImage())
            {
                e.Handled = true;
            }
        }

        private void OnComposerHistoryPreviousRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Every chat tab shares these handlers, so the recalled prompt has to be aimed at the
            // transcript that raised the event — otherwise it lands in the panel's prompt box.
            SetHistoryNavigationTarget(ResolveSessionFromSender(sender));
            NavigateHistoryUp();
        }

        private void OnComposerHistoryNextRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SetHistoryNavigationTarget(ResolveSessionFromSender(sender));
            NavigateHistoryDown();
        }

        /// <summary>Ctrl+Scroll zoom and the composer height are per-user layout, so both are persisted.</summary>
        private void OnComposerZoomChanged(object sender, double zoom)
        {
            if (_settings == null) return;

            _settings.NativeChatZoom = zoom;
            SaveSettings();
        }

        private void OnComposerHeightChanged(object sender, double height)
        {
            if (_settings == null) return;

            _settings.NativeChatComposerHeight = height;
            SaveSettings();
        }

        /// <summary>
        /// Clears the current conversation and starts fresh. Resolves the owning session from the
        /// sender (v163.0) — this is wired into every parallel tab's composer too, and without that
        /// resolution "Clear Chat" clicked in tab 2 used to wipe out the panel's own conversation
        /// instead of tab 2's.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerClearChatRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                NativeChatSessionState owner = ResolveSessionFromSender(sender);
                ChatTranscriptView transcript = owner?.ChatTranscript ?? ChatTranscript;

                if ((owner?.AgentSession ?? _agentSession) == null)
                {
                    return;
                }

                if (MessageBox.Show(
                        L("Clear this conversation and start fresh?"),
                        L("Clear Chat"),
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }

                transcript.Clear();

                // A cleared chat has no context window in use until its next turn reports one.
                if (owner != null)
                {
                    owner.LastStatusLine = null;
                }
                else
                {
                    _mainStatusLine = null;
                }
                RefreshChatUsageIndicators();

                // No resume id is staged, so the relaunch below starts a brand new session.
                if (owner != null)
                {
                    await RelaunchSessionAsync(owner, L("🤖 New chat"), forceNewSession: true);
                }
                else
                {
                    await RelaunchNativeSessionAsync(L("🤖 New chat"), forceNewSession: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat clear failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a new parallel chat session with the same provider (and model/effort/permission/plan
        /// mode) as whichever chat raised the "+": the tab itself when clicked from a parallel session
        /// (<see cref="ResolveSessionFromSender"/>), or this session's own live state for the panel's
        /// default session. Reading <c>_settings.SelectedProvider</c> unconditionally used to violate
        /// the Active Provider UI rule — that field only tracks the panel's own selection and goes
        /// stale the moment a parallel tab picks a different provider, so a "+" clicked from any tab
        /// silently opened a session for whatever the settings field happened to hold instead of the
        /// tab that was actually clicked.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerNewChatRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                NativeChatSessionState owner = ResolveSessionFromSender(sender);
                IAgentSession referenceSession = owner?.AgentSession ?? _agentSession;
                if (referenceSession == null)
                {
                    MessageBox.Show(L("No active session. Start native mode first."), L("New Session"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Get workspace
                string workspace = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrWhiteSpace(workspace) || !System.IO.Directory.Exists(workspace))
                {
                    MessageBox.Show(L("Cannot create new session: no workspace available."), L("New Session"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Owner's own provider for a parallel tab; otherwise the provider actually running in
                // the panel, not the possibly-stale _settings value (see the Active Provider UI rule).
                AiProvider provider = owner?.SelectedProvider ?? _currentRunningProvider ?? _settings.SelectedProvider;

                // Create new session, seeded from the owner tab's own model/effort/permission/plan-mode
                // snapshot so a "+" clicked inside e.g. a Devin tab running a non-default model opens
                // another Devin tab on that same model, not the global settings' defaults.
                var newSession = await StartParallelSessionAsync(provider, workspace, owner);
                if (newSession == null)
                {
                    MessageBox.Show(L("Failed to create new session."), L("New Session"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat new-session failed: {ex.Message}");
                MessageBox.Show(L("Failed to create new session: {0}", ex.Message), L("New Session"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Creates a parallel chat session, shows it in its own document tab and starts its agent in
        /// <paramref name="workspace"/>. <paramref name="configure"/> runs before the agent is created and
        /// the tab is shown, so fields it sets (e.g. a GitHub issue tab's worktree, issue number, model
        /// and effort) already shape the launch flags and the tab's caption. Returns null when the agent
        /// session could not be created; a launch failure throws.
        /// </summary>
        internal async Task<NativeChatSessionState> StartParallelSessionAsync(
            AiProvider provider, string workspace, NativeChatSessionState seed, Action<NativeChatSessionState> configure = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var newSession = CreateAndRegisterSession(provider, workspace, seed, configure);
            if (newSession?.AgentSession == null)
            {
                return null;
            }

            // Composer/transcript events are wired by ShowSessionInTabAsync below.

            // Clear and start
            newSession.ChatTranscript.Clear();
            newSession.ChatTranscript.SetStatus(L("Starting new session..."));

            // Show new session in its own tab. Marked focused right away rather than waiting for
            // the tab's own activation event, so a build/runtime error landing before the user
            // clicks anything still targets the tab they just opened, not the old one.
            await ShowSessionInTabAsync(newSession, focusComposer: false);
            _lastFocusedNativeSessionId = newSession.SessionId;

            // Configure composer controls for this session. Model/effort/permission are fully live
            // here (v163.0) — they relaunch this tab's own session via RelaunchSessionAsync, not
            // the global one.
            UpdateChatComposerState(newSession);
            // Apply the same font, zoom and composer height as the main session for consistency
            if (_settings != null)
            {
                newSession.ChatTranscript.SetChatFont(_settings.NativeChatFontFaceName, _settings.NativeChatFontSizePt);
                newSession.ChatTranscript.ComposerHeight = _settings.NativeChatComposerHeight;
            }
            newSession.ChatTranscript.Zoom = _settings?.NativeChatZoom ?? 1.0;

            // Start the agent
            newSession.SessionCts = new CancellationTokenSource();
            await newSession.AgentSession.StartAsync(workspace, newSession.SessionCts.Token);

            newSession.ChatTranscript.SetStatus(L("Ready."));
            // Show welcome card in the new session, not the global one
            ShowChatWelcome(newSession.ChatTranscript, workspace);
            return newSession;
        }

        /// <summary>
        /// Renames the session currently open in the tab (✎ button). Stored the same way as a rename
        /// from the Session History dialog (issue #95) — keyed by session UUID in
        /// <c>_settings.SessionCustomTitles</c> — so a title set here also appears in Session History,
        /// and a title set there is picked up here the next time the tab caption refreshes.
        /// </summary>
        private void OnComposerRenameSessionRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A rename in a session tab must retitle that tab's agent, not the panel's.
            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            IAgentSession agent = owner != null ? owner.AgentSession : _agentSession;

            if (agent == null)
            {
                return;
            }

            string sessionId = agent.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                MessageBox.Show(
                    L("This session doesn't have an id to rename yet. Send at least one message first — " +
                      "some agents (e.g. Codex, Antigravity) don't expose a renamable session id at all."),
                    L("Rename Session"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string currentTitle = null;
            _settings?.SessionCustomTitles?.TryGetValue(sessionId, out currentTitle);

            var info = new SessionInfo { SessionId = sessionId, CustomTitle = currentTitle ?? string.Empty };
            string newTitle = ShowRenameSessionDialog(info, Application.Current?.MainWindow);
            if (newTitle == null) return; // cancelled

            newTitle = newTitle.Trim();
            if (_settings.SessionCustomTitles == null)
            {
                _settings.SessionCustomTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (string.IsNullOrEmpty(newTitle))
            {
                _settings.SessionCustomTitles.Remove(sessionId);
            }
            else
            {
                _settings.SessionCustomTitles[sessionId] = newTitle;
            }

            SaveSettings();

            // Issue #170: also hand the name to Claude Code so its own /resume picker shows it.
            AiProvider renamedProvider = owner?.SelectedProvider ?? _currentRunningProvider ?? _settings.SelectedProvider;
            StartSessionHistoryTask(() => SyncClaudeSessionTitleAsync(renamedProvider, sessionId, newTitle),
                "claudecode/nativechat/renamesync");

            if (owner != null)
            {
                UpdateSessionTabCaption(owner);
            }
            else
            {
                UpdateChatTabCaption();
            }
        }

        /// <summary>
        /// Picks a custom title color for the session currently open in the tab (the swatch next to
        /// the session name header). Stored the same way as the title itself — keyed by session UUID
        /// in <c>_settings.SessionTitleColors</c> — a single native color-chooser dialog, no hex box:
        /// the header already shows a swatch as a live preview of the last-picked color.
        /// </summary>
        private void OnComposerColorPickerRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            IAgentSession agent = owner != null ? owner.AgentSession : _agentSession;

            if (agent == null)
            {
                return;
            }

            string sessionId = agent.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                MessageBox.Show(
                    L("This session doesn't have an id to color yet. Send at least one message first — " +
                      "some agents (e.g. Codex, Antigravity) don't expose a renamable session id at all."),
                    L("Session Color"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string currentHex = GetNativeSessionColor(sessionId);
            System.Drawing.Color initial;
            try
            {
                initial = System.Drawing.ColorTranslator.FromHtml(
                    string.IsNullOrEmpty(currentHex) ? "#1C8AE0" : currentHex); // #1C8AE0 matches ChatAccentBrush
            }
            catch (Exception)
            {
                initial = System.Drawing.ColorTranslator.FromHtml("#1C8AE0");
            }

            using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = initial })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return; // cancelled
                }

                if (_settings.SessionTitleColors == null)
                {
                    _settings.SessionTitleColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                _settings.SessionTitleColors[sessionId] =
                    $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            }

            SaveSettings();

            if (owner != null)
            {
                UpdateSessionTabCaption(owner);
            }
            else
            {
                UpdateChatTabCaption();
            }
        }

        /// <summary>
        /// Refreshes everything the composer shows about the running agent: the four selector captions,
        /// which of them apply, and the send-key preference. Runs whenever native mode is active, not
        /// only while the chat is in its own tab — <see cref="ComposerMode.ActionsOnly"/> keeps those
        /// selectors visible while docked in the panel too, and they need live labels there as well.
        /// </summary>
        /// <summary>
        /// Pushes the current send-key preference (Enter / Ctrl+Enter / button only) to the panel's
        /// chat transcript and every parallel session tab. The composers only copy it while their
        /// full state refreshes, so without this a change made in Settings stayed ignored until
        /// something else happened to refresh them (issue #186).
        /// </summary>
        private void ApplyChatSendKeySettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var transcripts = new List<ChatTranscriptView> { ChatTranscript };
            lock (_sessionLock)
            {
                foreach (NativeChatSessionState session in _nativeSessions.Values)
                {
                    transcripts.Add(session.ChatTranscript);
                }
            }

            foreach (ChatTranscriptView transcript in transcripts)
            {
                if (transcript == null)
                    continue;

                transcript.SendWithEnter = _settings?.SendWithEnter != false;
                transcript.SendWithCtrlEnter = _settings?.SendWithCtrlEnter == true;
            }
        }

        private void UpdateChatComposerState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || !IsNativeModeActive)
            {
                return;
            }

            UpdateChatComposerState(ChatTranscript);
        }

        /// <summary>Updates composer state for a specific transcript (supports multi-session).</summary>
        private void UpdateChatComposerState(ChatTranscriptView transcript)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (transcript == null)
                return;

            AiProvider? provider = GetActiveOrSelectedProvider();
            bool isClaude = IsClaudeProvider(provider);
            bool isCodex = IsCodexProvider(provider);
            bool isDevin = provider == AiProvider.Devin || provider == AiProvider.DevinNative;
            string reasoningLabel = isCodex
                ? GetChatCodexReasoningLabel()
                : GetChatEffortLabel();

            transcript.SendWithEnter = _settings?.SendWithEnter != false;
            transcript.SendWithCtrlEnter = _settings?.SendWithCtrlEnter == true;

            UpdateChatUsageIndicator(transcript, null, provider);

            transcript.SetSelectorLabels(
                GetChatProviderDisplayName(provider),
                GetChatModelLabel(provider),
                reasoningLabel,
                GetChatPermissionLabel(provider));

            transcript.SetSelectorAvailability(
                provider: ShouldShowChatProviderSelector(_settings?.VisibleProviders, provider),
                model: isClaude || ProviderHasModelCatalog(provider),
                effort: isClaude || isCodex,
                permission: GetChatPermissionLabel(provider) != null);

            if (isCodex)
            {
                transcript.SetEffortStopLabels(GetChatCodexReasoningStopLabels());
                transcript.SetEffortSlider(
                    CodexReasoningToSliderIndex(
                        _settings != null
                            ? _settings.SelectedCodexReasoningLevel
                            : CodexReasoningLevel.Default),
                    reasoningLabel,
                    L("Reasoning"));
            }
            else
            {
                transcript.SetEffortStopLabels(GetChatEffortStopLabels());
                transcript.SetEffortSlider(
                    EffortToSliderIndex(
                        _settings != null ? _settings.SelectedEffortLevel : EffortLevel.High),
                    reasoningLabel,
                    L("Effort"));
            }

            ApplyChatAppearance();
            UpdateComposerAttachmentChips();
        }

        /// <summary>
        /// Updates composer state for one parallel session's own tab (v163.0), reading that session's
        /// own model/effort/permission snapshot instead of the global settings the panel and default
        /// tab use — the same four selectors, sourced differently. Attachment chips and the shared
        /// appearance settings (font, zoom, composer height) are handled separately for a parallel
        /// tab (<see cref="AddSessionAttachments"/>, the explicit font/zoom/composer-height assignments where the tab
        /// is created) since <see cref="ApplyChatAppearance"/> and <see cref="UpdateComposerAttachmentChips"/>
        /// both only ever target the panel's own <c>ChatTranscript</c>.
        /// </summary>
        private void UpdateChatComposerState(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ChatTranscriptView transcript = session?.ChatTranscript;
            if (transcript == null)
                return;

            AiProvider provider = session.SelectedProvider;
            bool isClaude = IsClaudeProvider(provider);
            bool isCodex = IsCodexProvider(provider);
            string reasoningLabel = isCodex
                ? GetCodexReasoningLabel(session.SelectedCodexReasoningLevel)
                : GetChatEffortLabel(session.SelectedEffortLevel);

            transcript.SendWithEnter = _settings?.SendWithEnter != false;
            transcript.SendWithCtrlEnter = _settings?.SendWithCtrlEnter == true;

            UpdateChatUsageIndicator(transcript, session, provider);

            string permissionLabel = GetChatPermissionLabel(session);

            transcript.SetSelectorLabels(
                GetChatProviderDisplayName(provider),
                GetChatModelLabel(session),
                reasoningLabel,
                permissionLabel);

            transcript.SetSelectorAvailability(
                provider: ShouldShowChatProviderSelector(_settings?.VisibleProviders, provider),
                model: isClaude || ProviderHasModelCatalog(provider),
                effort: isClaude || isCodex,
                permission: permissionLabel != null);

            if (isCodex)
            {
                transcript.SetEffortStopLabels(GetChatCodexReasoningStopLabels());
                transcript.SetEffortSlider(
                    CodexReasoningToSliderIndex(session.SelectedCodexReasoningLevel),
                    reasoningLabel,
                    L("Reasoning"));
            }
            else
            {
                transcript.SetEffortStopLabels(GetChatEffortStopLabels());
                transcript.SetEffortSlider(
                    EffortToSliderIndex(session.SelectedEffortLevel),
                    reasoningLabel,
                    L("Effort"));
            }
        }

        #endregion

        #region Markdown Link Navigation

        /// <summary>Splits a trailing <c>:line</c> or <c>:line-line2</c> off a file reference, e.g. one an agent cites for its own diff.</summary>
        private static readonly Regex ChatFileLineRefPattern = new Regex(@"^(?<path>.+):(?<line1>\d+)(?:-(?<line2>\d+))?$", RegexOptions.Compiled);

        /// <summary>
        /// A link clicked in the rendered transcript: a web address opens in the default browser,
        /// anything else is treated as a file reference (optionally with a trailing <c>:line</c>) and
        /// opened in the editor at that line.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatLinkClicked(object sender, string url)
#pragma warning restore VSTHRD100
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                if (Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return;
                }

                await OpenChatFileLinkAsync(url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat link click failed for '{url}': {ex.Message}");
            }
        }

        /// <summary>
        /// The ↗ icon on a tool row. Unlike <see cref="OnChatLinkClicked"/>, a miss is reported in the
        /// row's own transcript — the path came straight off the tool call rather than being guessed out
        /// of prose, so silently doing nothing would just look broken instead of looking like a
        /// reasonable "not a real citation" skip.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnToolFileOpenRequested(object sender, string filePath)
#pragma warning restore VSTHRD100
        {
            try
            {
                bool opened = await OpenChatFileLinkAsync(filePath);
                if (!opened && sender is ChatTranscriptView transcript)
                {
                    var notice = new ChatMessageViewModel(ChatMessageKind.Notice)
                    {
                        Text = L("Could not find \"{0}\" to open it.", filePath)
                    };
                    notice.Complete();
                    transcript.Messages.Add(notice);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tool file open failed for '{filePath}': {ex.Message}");
            }
        }

        /// <summary>Returns whether the reference was actually opened, so a deliberate click (the tool
        /// row's ↗ icon) can tell the user why nothing happened instead of failing silently — an
        /// auto-linked citation in free-form text stays silent on a miss since most of those are guesses.</summary>
        private async Task<bool> OpenChatFileLinkAsync(string reference)
        {
            string path = reference.Trim();
            int line = 0;

            Match match = ChatFileLineRefPattern.Match(path);
            if (match.Success)
            {
                path = match.Groups["path"].Value;
                int.TryParse(match.Groups["line1"].Value, out line);
            }

            string resolved = await ResolveChatFileLinkAsync(path);
            if (resolved == null)
            {
                return false;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte == null)
            {
                return false;
            }

            dte.ItemOperations.OpenFile(resolved);

            if (line > 0 && dte.ActiveDocument != null)
            {
                var selection = dte.ActiveDocument.Selection as EnvDTE.TextSelection;
                selection?.GotoLine(line, false);
            }

            return true;
        }

        /// <summary>
        /// Resolves a path an agent cited to an actual file: as typed, relative to the workspace root,
        /// or — since agents often abbreviate to a bare filename or a repo-relative form that does not
        /// match where the extension resolved the workspace — by falling back to the same index the "@"
        /// mention picker builds and matching on the trailing path segments.
        /// </summary>
        private async Task<string> ResolveChatFileLinkAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            // A WSL-hosted provider (Codex, Claude Code WSL, Cursor Agent, Devin) reports its own view
            // of the file system — /mnt/c/... — not the Windows path Visual Studio needs. Convert first;
            // this is a no-op for every other path shape, including one that is already Windows-style.
            string candidate = ConvertFromWslMountPath(path).Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }

            string workspace = await GetWorkspaceDirectoryAsync();
            if (string.IsNullOrEmpty(workspace))
            {
                return null;
            }

            string direct = Path.Combine(workspace, candidate);
            if (File.Exists(direct))
            {
                return direct;
            }

            await EnsureAtEntriesAsync();
            if (_atEntries == null)
            {
                return null;
            }

            string normalizedTarget = path.Replace('\\', '/').TrimStart('/');
            string entry = _atEntries.FirstOrDefault(e =>
                !e.EndsWith("/", StringComparison.Ordinal) &&
                (string.Equals(e, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                 e.EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileName(e), normalizedTarget, StringComparison.OrdinalIgnoreCase)));

            return entry != null ? Path.Combine(workspace, entry.Replace('/', Path.DirectorySeparatorChar)) : null;
        }

        #endregion

        #region Welcome Card

        /// <summary>
        /// CLI versions already measured, keyed by provider. Static so the probe runs once per Visual
        /// Studio session rather than once per new chat; an empty value records "asked and got nothing",
        /// which stops a missing or slow CLI from being probed again on every conversation.
        /// </summary>
        private static readonly ConcurrentDictionary<AiProvider, string> _cliVersions =
            new ConcurrentDictionary<AiProvider, string>();

        /// <summary>
        /// Signed-in Claude account label for the WSL provider (only <see cref="AiProvider.ClaudeCodeWSL"/>
        /// is ever keyed here). Unlike <see cref="_cliVersions"/> this is not "ask once per VS session" —
        /// it is cleared by <see cref="InvalidateSignedInClaudeAccountLabelAsync"/> whenever the account
        /// changes, so the welcome card does not have to shell out to <c>wslpath</c> on every render but
        /// still picks up a switch.
        /// </summary>
        private static readonly ConcurrentDictionary<AiProvider, string> _wslAccountLabels =
            new ConcurrentDictionary<AiProvider, string>();

        /// <summary>
        /// Signed-in Codex account labels returned by the CLI's app-server <c>account/read</c>
        /// endpoint. Native and WSL Codex have separate credential stores, so each provider is cached
        /// independently. The short lifetime avoids showing an account changed outside Visual Studio
        /// for the rest of the IDE session.
        /// </summary>
        private static readonly ConcurrentDictionary<AiProvider, string> _codexAccountLabels =
            new ConcurrentDictionary<AiProvider, string>();

        private static readonly ConcurrentDictionary<AiProvider, DateTime> _codexAccountLabelFetchedUtc =
            new ConcurrentDictionary<AiProvider, DateTime>();

        /// <summary>Prevents card re-renders from starting duplicate app-server account probes.</summary>
        private static readonly ConcurrentDictionary<AiProvider, byte> _codexAccountLabelLookups =
            new ConcurrentDictionary<AiProvider, byte>();

        private static readonly TimeSpan CodexAccountLabelTimeToLive = TimeSpan.FromMinutes(1);

        /// <summary>
        /// How long a version probe is given before it is killed. It is decoration on a card that is
        /// already on screen, so it may never hold anything up.
        /// </summary>
        private const int CliVersionTimeoutMs = 5000;

        /// <summary>Maximum total time for the Codex app-server handshake and account read.</summary>
        private const int CodexAccountLookupTimeoutMs = 5000;

        /// <summary>
        /// Shows the card that opens a fresh conversation: which agent is running, with which model,
        /// effort and permissions, against which folder, and what the chat can do. It stands in for the
        /// banner the CLI prints on startup, which native mode never sees — the agent is running
        /// headless and its greeting is not part of the event stream.
        /// </summary>
        /// <param name="workspace">
        /// Folder the agent was started in. Callers that have just resolved it pass it; the last known
        /// one is used otherwise.
        /// </param>
        private void ShowChatWelcome(string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null)
            {
                return;
            }

            ShowChatWelcome(ChatTranscript, workspace);
        }

        /// <summary>Shows welcome card in a specific transcript (supports multi-session).</summary>
        private void ShowChatWelcome(ChatTranscriptView transcript, string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (transcript == null)
                return;

            AiProvider? provider = GetActiveOrSelectedProvider();
            string directory = string.IsNullOrWhiteSpace(workspace) ? _lastWorkspaceDirectory : workspace;

            transcript.ShowWelcome(
                BuildWelcomeTitle(provider),
                BuildWelcomeFacts(provider, directory),
                BuildWelcomeTips(provider));

            if (provider.HasValue)
            {
                BeginCliVersionLookup(provider.Value, directory);
                BeginAccountLabelLookup(provider.Value, directory);
            }

            BeginWelcomeRecentSessionsLookup(transcript, directory);
        }

        /// <summary>How many past conversations the welcome card lists.</summary>
        private const int WelcomeRecentSessionCount = 5;

        /// <summary>Transcripts whose recent list is being read, so the card's re-renders do not pile up reads.</summary>
        private readonly HashSet<ChatTranscriptView> _welcomeRecentLookups = new HashSet<ChatTranscriptView>();

        /// <summary>
        /// Fills the welcome card's recent list with the workspace's latest Claude Code conversations,
        /// read in the background. Conversations open in a chat tab are left out. Any other agent, or no
        /// past conversation, leaves the card on its tips.
        /// </summary>
        private void BeginWelcomeRecentSessionsLookup(ChatTranscriptView transcript, string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            NativeChatSessionState owner = ResolveSessionFromSender(transcript);
            AiProvider? provider = owner != null ? owner.SelectedProvider : GetActiveOrSelectedProvider();

            if (!IsClaudeCodeSessionHistoryProvider(provider) || string.IsNullOrWhiteSpace(workspace))
            {
                transcript.SetWelcomeRecentSessions(null);
                return;
            }

            if (!_welcomeRecentLookups.Add(transcript))
            {
                return;
            }

            var openSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(_agentSession?.SessionId))
            {
                openSessionIds.Add(_agentSession.SessionId);
            }

            lock (_sessionLock)
            {
                foreach (NativeChatSessionState session in _nativeSessions.Values)
                {
                    if (!string.IsNullOrEmpty(session.AgentSession?.SessionId))
                    {
                        openSessionIds.Add(session.AgentSession.SessionId);
                    }
                }
            }

            StartSessionHistoryTask(async () =>
            {
                try
                {
                    List<SessionInfo> recent = await LoadRecentClaudeSessionsAsync(
                        provider.Value, workspace, WelcomeRecentSessionCount, openSessionIds);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (transcript.IsWelcomeVisible)
                    {
                        transcript.SetWelcomeRecentSessions(recent.Select(BuildWelcomeRecentSession).ToList());
                    }
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _welcomeRecentLookups.Remove(transcript);
                }
            }, "claudecode/nativechat/welcomerecent");
        }

        /// <summary>One row of the welcome card's recent list: the name (else first prompt), then when and on which branch.</summary>
        private static WelcomeRecentSession BuildWelcomeRecentSession(SessionInfo info)
        {
            DateTime when = info.LastModified;
            string detail;
            switch (SessionListPresentation.GetDateGroup(when, DateTime.Now))
            {
                case SessionDateGroup.Today:
                    detail = L("Today") + " " + when.ToString("t", Loc.Culture);
                    break;
                case SessionDateGroup.Yesterday:
                    detail = L("Yesterday") + " " + when.ToString("t", Loc.Culture);
                    break;
                default:
                    detail = when.ToString("d", Loc.Culture);
                    break;
            }

            if (!string.IsNullOrEmpty(info.GitBranch))
            {
                detail += "  ·  " + info.GitBranch;
            }

            return new WelcomeRecentSession
            {
                Title = info.DisplayTitle,
                Detail = detail,
                ToolTip = string.IsNullOrWhiteSpace(info.CustomTitle) ? null : info.Preview,
                Session = info
            };
        }

        /// <summary>A conversation clicked in the welcome card: resumed in the tab whose card it was.</summary>
        private void OnWelcomeSessionRequested(object sender, WelcomeRecentSession row)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (row?.Session is SessionInfo info)
            {
                NativeChatSessionState owner = ResolveSessionFromSender(sender);
                StartSessionHistoryTask(() => ResumeSessionAsync(info, owner), "claudecode/nativechat/welcomeresume");
            }
        }

        private void OnWelcomeAllSessionsRequested(object sender, EventArgs e)
        {
            StartSessionHistoryTask(ShowSessionHistoryDialogAsync, "claudecode/nativechat/welcomehistory");
        }

        /// <summary>
        /// Most user/assistant messages a replayed transcript may add. A long conversation would
        /// otherwise take seconds to render and bury the composer under thousands of bubbles; the newest
        /// ones are the ones worth showing, so the cap keeps the tail and says how much it dropped. The
        /// value is a judgement call from v89.0, not a measurement. Tool cards do not count against it.
        /// </summary>
        private const int ResumedTranscriptMaxMessages = 200;

        /// <summary>
        /// Most tool calls of a replayed Claude transcript shown as full tool cards; older ones inside
        /// the shown messages collapse into one-line summaries. Kept apart from (and above)
        /// <see cref="ResumedTranscriptMaxMessages"/> because a single turn often runs several tools.
        /// Lowered from 500 after measuring a real resume: 116 full cards still cost enough layout
        /// work to stall Visual Studio for seconds, and a one-line summary keeps the call traceable.
        /// </summary>
        internal const int ResumedTranscriptMaxToolCards = 40;

        /// <summary>
        /// Lines of one summary row of older tool calls. The row is a single text block, and a long run
        /// of calls between two messages would otherwise make it as heavy as the cards it replaces.
        /// </summary>
        internal const int ResumedToolSummaryMaxLines = 30;

        /// <summary>
        /// Replays a resumed conversation into the chat. The CLI is the reason this exists: measured,
        /// <c>--resume</c> in print mode restores the agent's memory in full but emits nothing of the
        /// history — only <c>system/init</c> and then the new turn. So the agent remembered everything
        /// while the chat opened blank, which is what "session history doesn't load" looked like.
        /// The transcript on disk is the same JSONL the Session History dialog already reads.
        /// </summary>
        /// <param name="sessionId">
        /// The id the user asked to resume, or null/"-c" for anything else. Codex resolves "Resume Last"
        /// to an explicit id before this point; Claude's "-c" sentinel has no native-mode equivalent.
        /// </param>
        /// <param name="target">The parallel tab to replay into, or null for the default session's chat.</param>
        /// <returns>False when there is nothing to replay, and the caller shows the welcome card.</returns>
        private async Task<bool> TryReplayResumedTranscriptAsync(AiProvider provider, string workspace, string sessionId,
            NativeChatSessionState target = null)
        {
            ChatTranscriptView chat = target != null ? target.ChatTranscript : ChatTranscript;
            if (string.IsNullOrEmpty(sessionId) || sessionId == "-c" || chat == null)
            {
                return false;
            }

            try
            {
                if (IsCodexSessionHistoryProvider(provider))
                {
                    CodexThreadTranscript transcript = await ReadCodexThreadAsync(provider, workspace, sessionId);
                    List<CodexTranscriptMessage> messages = transcript?.Messages?
                        .Where(message => message != null && !message.IsTool && !string.IsNullOrWhiteSpace(message.Text))
                        .ToList() ?? new List<CodexTranscriptMessage>();
                    LogTerminalLaunch($"Native mode: Codex replay thread={sessionId}, messages={messages.Count}");
                    if (messages.Count == 0) return false;

                    int start = Math.Max(0, messages.Count - ResumedTranscriptMaxMessages);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AddNativeMessage(chat, ChatMessageKind.Notice, start > 0
                        ? L("🕘 Resumed this Codex conversation — showing the last {0} of {1} messages.", messages.Count - start, messages.Count)
                        : L("🕘 Resumed this Codex conversation — {0} earlier messages restored.", messages.Count));

                    for (int index = start; index < messages.Count; index++)
                    {
                        CodexTranscriptMessage message = messages[index];
                        AddNativeMessage(chat, message.IsUser ? ChatMessageKind.User : ChatMessageKind.Assistant,
                            message.Text.TrimEnd());
                    }

                    chat.ScrollToLatest();
                    return true;
                }

                string directory = await ResolveSessionDirectoryAsync(provider, workspace);
                if (string.IsNullOrEmpty(directory))
                {
                    return false;
                }

                string path = Path.Combine(directory, sessionId + ".jsonl");
                if (!File.Exists(path))
                {
                    Debug.WriteLine($"Native mode: no transcript to replay at {path}");
                    return false;
                }

                // Parsed off the UI thread — a long session is megabytes of JSONL — and returned as
                // plain data, because a WPF view model may only be built on the thread that owns it.
                ResumedTranscript replay = await Task.Run(() => ReadResumedTranscript(path));

                if (replay == null || replay.Rows.Count == 0)
                {
                    return false;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                AddNativeMessage(chat, ChatMessageKind.Notice, replay.TotalMessages > replay.ShownMessages
                    ? L("🕘 Resumed this conversation — showing the last {0} of {1} messages.", replay.ShownMessages, replay.TotalMessages)
                    : L("🕘 Resumed this conversation — {0} earlier messages restored.", replay.ShownMessages));

                // A replayed transcript skips ShowChatWelcome entirely (see its caller), which is the
                // only other place this label is shown — so a resumed conversation never said who is
                // signed in, even right after a "Change Account" switch that resumed instead of starting
                // fresh.
                if (IsClaudeProvider(provider))
                {
                    bool isWsl = provider == AiProvider.ClaudeCodeWSL;
                    string account = await GetSignedInClaudeAccountLabelAsync(isWsl);

                    // Only a real account is worth caching — an empty result here is as likely to be
                    // "not signed in yet" (e.g. a login that hadn't finished writing to disk) as
                    // "genuinely signed out", and unlike a CLI version this can change moment to moment.
                    // Caching the empty answer would have permanently silenced every later welcome card
                    // until the next Change Account click, since BeginAccountLabelLookup's ContainsKey
                    // guard treats any cached entry — including an empty one — as "already answered".
                    if (isWsl && !string.IsNullOrEmpty(account))
                    {
                        _wslAccountLabels[provider] = account;
                    }

                    if (!string.IsNullOrEmpty(account))
                    {
                        AddNativeMessage(chat, ChatMessageKind.Notice, L("Signed in as {0}", account));
                    }
                }

                foreach (ResumedTranscriptRow row in replay.Rows)
                {
                    if (row.Kind == ChatMessageKind.ToolCall)
                    {
                        chat.Messages.Add(BuildReplayedToolCallMessage(row));
                    }
                    else
                    {
                        AddNativeMessage(chat, row.Kind, row.Text);
                    }
                }

                chat.ScrollToLatest();
                LogTerminalLaunch($"Native mode: replayed session={sessionId}, " +
                    $"messages={replay.ShownMessages}/{replay.TotalMessages}, rows={replay.Rows.Count}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: replaying the resumed transcript failed: {ex.Message}");
                LogTerminalLaunch($"Native mode: transcript replay failed for provider={provider}, " +
                    $"session={sessionId}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// A replayed tool row as the live stream would have drawn it — the same card, finished, with
        /// the result the transcript recorded. The diff is left for the first open
        /// (<see cref="ChatMessageViewModel.SetDeferredDiff"/>), and a failed call stays collapsed
        /// rather than opening itself as a live one does: a replay is history, not something to act on.
        /// </summary>
        private static ChatMessageViewModel BuildReplayedToolCallMessage(ResumedTranscriptRow row)
        {
            AgentEvent call = AgentEvent.ToolCallStarted(row.ToolCallId, row.ToolName, row.ToolInputJson);
            ChatMessageViewModel message = BuildToolCallMessage(call, deferDiff: true);

            message.ToolResult = row.ToolResult;
            message.IsError = row.IsError;
            message.IsRunning = false;

            return message;
        }

        /// <summary>
        /// Reads a session's JSONL for the chat replay. See
        /// <see cref="ReadResumedTranscript(TextReader, int, int, int)"/> for what it returns.
        /// </summary>
        private static ResumedTranscript ReadResumedTranscript(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return ReadResumedTranscript(reader,
                    ResumedTranscriptMaxMessages, ResumedTranscriptMaxToolCards, ResumedToolSummaryMaxLines);
            }
        }

        /// <summary>
        /// Turns a Claude transcript into replay rows, oldest first: user and assistant messages, the
        /// assistant's tool calls (each paired with its <c>tool_result</c> by <c>tool_use_id</c>), and
        /// summary rows for older tool calls.
        /// <list type="bullet">
        /// <item>Only messages count against <paramref name="maxMessages"/>; the tail is kept, along with
        /// every tool call that follows the oldest kept message.</item>
        /// <item>The newest <paramref name="maxToolCards"/> tool calls are full cards. Older ones become
        /// one line each (<c>▤ Read · …\src\Foo.cs</c>), and each run of them between two messages is
        /// merged into one <see cref="ChatMessageKind.Notice"/> row of at most
        /// <paramref name="maxSummaryLines"/> lines — so the rows stay bounded by the message cap.</item>
        /// <item>The <c>tool_result</c> blocks the CLI re-injects as "user" lines fill the cards, never
        /// a user bubble — the same rule the Session History dialog and the exported text follow.</item>
        /// </list>
        /// Trimmed while reading, so a very long transcript is never held in full. Pure, for the tests.
        /// </summary>
        internal static ResumedTranscript ReadResumedTranscript(TextReader reader, int maxMessages, int maxToolCards,
            int maxSummaryLines)
        {
            var builder = new ResumedTranscriptBuilder(maxMessages, maxToolCards);

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject obj;
                try { obj = JObject.Parse(line); }
                catch { continue; }

                string type = (string)obj["type"];
                if (type != "user" && type != "assistant") continue;

                JToken message = obj["message"];
                if (message == null) continue;

                JToken content = message["content"];

                if (type == "user")
                {
                    // Results first: a line that carries both closes the previous calls before the
                    // next message starts.
                    var parts = content as JArray;
                    if (parts != null)
                    {
                        foreach (JToken part in parts)
                        {
                            if ((string)part["type"] == "tool_result")
                            {
                                builder.AddToolResult((string)part["tool_use_id"], part["content"],
                                    (bool?)part["is_error"] ?? false);
                            }
                        }
                    }

                    builder.AddMessage(ChatMessageKind.User, ExtractUserText(content));
                    continue;
                }

                if (content == null || content.Type != JTokenType.Array)
                {
                    builder.AddMessage(ChatMessageKind.Assistant, ExtractAssistantText(content));
                    continue;
                }

                // Text before and after a tool call stays on its own side of the card.
                var text = new StringBuilder();
                foreach (JToken part in content.Children())
                {
                    if ((string)part["type"] == "tool_use")
                    {
                        builder.AddMessage(ChatMessageKind.Assistant, text.ToString());
                        text.Clear();

                        // Serialized the way the live stream parser does (never JToken.ToString(Formatting), #112).
                        JToken input = part["input"];
                        builder.AddToolCall((string)part["id"], (string)part["name"],
                            input != null ? Newtonsoft.Json.JsonConvert.SerializeObject(input) : string.Empty);
                    }
                    else if ((string)part["type"] == "text")
                    {
                        // Historical thinking is deliberately not replayed: in real sessions it is the
                        // bulk of the transcript (measured: 267k of 315k replay characters), and
                        // rendering it as markdown on the UI thread is what made a resumed chat stall
                        // Visual Studio for ~30 seconds. Live thinking still streams normally.
                        AppendAssistantTextPart(text, part);
                    }
                }

                builder.AddMessage(ChatMessageKind.Assistant, text.ToString());
            }

            return builder.Build(maxSummaryLines);
        }

        /// <summary>One line of a summary row: the tool's glyph, name and target, as its card header reads.</summary>
        internal static string BuildToolSummaryLine(string toolName, string inputJson)
        {
            ChatToolPresentation presentation = ChatToolPresenter.Describe(toolName, inputJson, includeDiff: false);

            return string.IsNullOrEmpty(presentation.Subtitle)
                ? presentation.Icon + " " + presentation.Title
                : presentation.Icon + " " + presentation.Title + " · " + presentation.Subtitle;
        }

        /// <summary>The rows <see cref="ReadResumedTranscript(TextReader, int, int, int)"/> produced.</summary>
        internal sealed class ResumedTranscript
        {
            public List<ResumedTranscriptRow> Rows { get; } = new List<ResumedTranscriptRow>();

            /// <summary>User and assistant messages in the whole transcript.</summary>
            public int TotalMessages { get; set; }

            /// <summary>User and assistant messages among <see cref="Rows"/>.</summary>
            public int ShownMessages { get; set; }

            /// <summary>Tool calls among the shown rows that were reduced to a summary line.</summary>
            public int SummarizedToolCalls { get; set; }
        }

        /// <summary>
        /// One replay row. <see cref="ChatMessageKind.User"/>/<see cref="ChatMessageKind.Assistant"/>
        /// carry <see cref="Text"/>; <see cref="ChatMessageKind.ToolCall"/> carries the tool fields;
        /// <see cref="ChatMessageKind.Notice"/> is a summary of older tool calls in <see cref="Text"/>.
        /// </summary>
        internal sealed class ResumedTranscriptRow
        {
            public ChatMessageKind Kind { get; set; }
            public string Text { get; set; } = string.Empty;
            public string ToolCallId { get; set; } = string.Empty;
            public string ToolName { get; set; } = string.Empty;
            public string ToolInputJson { get; set; } = string.Empty;
            public string ToolResult { get; set; } = string.Empty;
            public bool IsError { get; set; }

            /// <summary>Set once the call fell out of the newest tool cards; only this line is kept.</summary>
            public string SummaryLine { get; set; }
        }

        /// <summary>
        /// The sliding windows behind <see cref="ReadResumedTranscript(TextReader, int, int, int)"/>: the
        /// last N messages (with the tool calls that follow them) and, within those, the last M full
        /// tool cards.
        /// </summary>
        private sealed class ResumedTranscriptBuilder
        {
            private readonly int _maxMessages;
            private readonly int _maxToolCards;
            private readonly LinkedList<ResumedTranscriptRow> _window = new LinkedList<ResumedTranscriptRow>();

            /// <summary>Full cards, oldest first — the next one to reduce to a summary line is the head.</summary>
            private readonly Queue<ResumedTranscriptRow> _fullCards = new Queue<ResumedTranscriptRow>();

            /// <summary>Full cards still waiting for their <c>tool_result</c>.</summary>
            private readonly Dictionary<string, ResumedTranscriptRow> _awaitingResult =
                new Dictionary<string, ResumedTranscriptRow>(StringComparer.Ordinal);

            private int _totalMessages;
            private int _shownMessages;

            public ResumedTranscriptBuilder(int maxMessages, int maxToolCards)
            {
                _maxMessages = Math.Max(1, maxMessages);
                _maxToolCards = Math.Max(0, maxToolCards);
            }

            public void AddMessage(ChatMessageKind kind, string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                _totalMessages++;
                _shownMessages++;
                _window.AddLast(new ResumedTranscriptRow { Kind = kind, Text = text.TrimEnd() });

                if (_shownMessages <= _maxMessages)
                {
                    return;
                }

                // Drop the oldest message, then the tool calls that belonged to it, so the replay never
                // opens on cards whose message is gone.
                RemoveFirst();
                while (_window.First != null && _window.First.Value.Kind == ChatMessageKind.ToolCall)
                {
                    RemoveFirst();
                }
            }

            public void AddToolCall(string id, string name, string inputJson)
            {
                var row = new ResumedTranscriptRow
                {
                    Kind = ChatMessageKind.ToolCall,
                    ToolCallId = id ?? string.Empty,
                    ToolName = name ?? string.Empty,
                    ToolInputJson = inputJson ?? string.Empty
                };

                _window.AddLast(row);
                _fullCards.Enqueue(row);
                if (!string.IsNullOrEmpty(row.ToolCallId))
                {
                    _awaitingResult[row.ToolCallId] = row;
                }

                if (_fullCards.Count > _maxToolCards)
                {
                    Summarize(_fullCards.Dequeue());
                }
            }

            public void AddToolResult(string id, JToken content, bool isError)
            {
                ResumedTranscriptRow row;
                if (string.IsNullOrEmpty(id) || !_awaitingResult.TryGetValue(id, out row))
                {
                    return;
                }

                _awaitingResult.Remove(id);
                row.ToolResult = TruncateToolResult(ClaudeStreamParser.FlattenResultContent(content)) ?? string.Empty;
                row.IsError = isError;
            }

            public ResumedTranscript Build(int maxSummaryLines)
            {
                var result = new ResumedTranscript
                {
                    TotalMessages = _totalMessages,
                    ShownMessages = _shownMessages
                };

                var run = new List<string>();
                foreach (ResumedTranscriptRow row in _window)
                {
                    if (row.SummaryLine != null)
                    {
                        run.Add(row.SummaryLine);
                        result.SummarizedToolCalls++;
                        continue;
                    }

                    FlushSummary(result, run, maxSummaryLines);
                    result.Rows.Add(row);
                }

                FlushSummary(result, run, maxSummaryLines);
                return result;
            }

            private static void FlushSummary(ResumedTranscript result, List<string> run, int maxSummaryLines)
            {
                if (run.Count == 0)
                {
                    return;
                }

                int shown = Math.Min(run.Count, Math.Max(1, maxSummaryLines));
                var text = new StringBuilder();
                text.Append(run.Count == 1 ? L("1 earlier tool call:") : L("{0} earlier tool calls:", run.Count));
                for (int index = 0; index < shown; index++)
                {
                    text.Append('\n').Append(run[index]);
                }
                if (run.Count > shown)
                {
                    text.Append('\n').Append(L("… and {0} more", run.Count - shown));
                }

                result.Rows.Add(new ResumedTranscriptRow { Kind = ChatMessageKind.Notice, Text = text.ToString() });
                run.Clear();
            }

            private void RemoveFirst()
            {
                ResumedTranscriptRow row = _window.First.Value;
                _window.RemoveFirst();

                if (row.Kind != ChatMessageKind.ToolCall)
                {
                    _shownMessages--;
                    return;
                }

                // The oldest card in the window is also the oldest full card, unless it was already
                // reduced to a summary line.
                if (row.SummaryLine == null && _fullCards.Count > 0 && ReferenceEquals(_fullCards.Peek(), row))
                {
                    _fullCards.Dequeue();
                }

                Forget(row);
            }

            /// <summary>Keeps only the one-line summary; the input and result are not shown any more.</summary>
            private void Summarize(ResumedTranscriptRow row)
            {
                row.SummaryLine = BuildToolSummaryLine(row.ToolName, row.ToolInputJson);
                row.ToolInputJson = string.Empty;
                row.ToolResult = string.Empty;
                Forget(row);
            }

            private void Forget(ResumedTranscriptRow row)
            {
                ResumedTranscriptRow waiting;
                if (!string.IsNullOrEmpty(row.ToolCallId) &&
                    _awaitingResult.TryGetValue(row.ToolCallId, out waiting) && ReferenceEquals(waiting, row))
                {
                    _awaitingResult.Remove(row.ToolCallId);
                }
            }
        }

        /// <summary>Caption on the card's frame: the agent, and its CLI version once that is known.</summary>
        private string BuildWelcomeTitle(AiProvider? provider)
        {
            string name = GetChatProviderDisplayName(provider);

            string version;
            if (provider.HasValue &&
                _cliVersions.TryGetValue(provider.Value, out version) &&
                !string.IsNullOrEmpty(version))
            {
                return name + " v" + version;
            }

            return name;
        }

        /// <summary>
        /// One-line "the CLI updated since last time" notice for the transcript, mirroring the changelog
        /// line the terminal greeting prints after a self-update. Returns null — nothing is posted —
        /// unless <paramref name="currentVersion"/> is a real version, a different one was recorded
        /// before, and the new one is strictly newer: a downgrade, or the very first run with nothing
        /// recorded, stays silent. Components are compared as integers so "2.1.9" precedes "2.1.10".
        /// </summary>
        internal static string BuildCliUpdateNotice(string providerName, string previousVersion, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(previousVersion))
            {
                return null;
            }

            currentVersion = currentVersion.Trim();
            previousVersion = previousVersion.Trim();

            if (string.Equals(previousVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!IsNewerCliVersion(previousVersion, currentVersion))
            {
                return null;
            }

            string name = string.IsNullOrWhiteSpace(providerName) ? L("The agent CLI") : providerName.Trim();

            return L("🆕 {0} updated to v{1} since you were last here (was v{2}) — " +
                     "what's new: https://code.claude.com/docs/en/changelog", name, currentVersion, previousVersion);
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is a strictly higher version than
        /// <paramref name="baseline"/>, compared component-by-component as integers. A pre-release or
        /// build suffix ("-beta", "+1") is ignored — only the dotted numbers decide. Anything
        /// unparseable returns false, so a malformed probe result never produces an "updated" claim.
        /// </summary>
        private static bool IsNewerCliVersion(string baseline, string candidate)
        {
            int[] a = ParseVersionComponents(baseline);
            int[] b = ParseVersionComponents(candidate);

            if (a == null || b == null)
            {
                return false;
            }

            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int left = i < a.Length ? a[i] : 0;
                int right = i < b.Length ? b[i] : 0;
                if (right != left)
                {
                    return right > left;
                }
            }

            return false;
        }

        private static int[] ParseVersionComponents(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            // Drop a trailing "-beta.1" / "+build" so only the dotted numbers are parsed.
            int cut = version.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0)
            {
                version = version.Substring(0, cut);
            }

            string[] parts = version.Split('.');
            var numbers = new List<int>(parts.Length);
            foreach (string part in parts)
            {
                if (!int.TryParse(part.Trim(), out int value) || value < 0)
                {
                    return null;
                }
                numbers.Add(value);
            }

            return numbers.Count == 0 ? null : numbers.ToArray();
        }

        /// <summary>
        /// Posts <see cref="BuildCliUpdateNotice"/>'s line to the transcript once per real version bump,
        /// then records the new version so it is not repeated. Claude providers only (the changelog URL
        /// is Claude's). Must be called on the UI thread.
        /// </summary>
        private void MaybeAnnounceCliUpdate(AiProvider provider, string currentVersion)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null || string.IsNullOrWhiteSpace(currentVersion) || !IsClaudeProvider(provider))
            {
                return;
            }

            currentVersion = currentVersion.Trim();

            if (_settings.LastSeenCliVersions == null)
            {
                _settings.LastSeenCliVersions = new Dictionary<string, string>();
            }

            string key = provider.ToString();
            _settings.LastSeenCliVersions.TryGetValue(key, out string previous);

            string notice = BuildCliUpdateNotice(GetChatProviderDisplayName(provider), previous, currentVersion);

            if (!string.Equals(previous, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastSeenCliVersions[key] = currentVersion;
                SaveSettings();
            }

            if (!string.IsNullOrEmpty(notice))
            {
                AddNativeMessage(ChatMessageKind.Notice, notice);
            }
        }

        /// <summary>
        /// Reads the signed-in Claude account from the CLI's own state file (<c>~/.claude.json</c>,
        /// key <c>oauthAccount</c>) — the same place the CLI itself keeps it. Native mode has no other
        /// source for this: the headless <c>system/init</c> event carries session/model info only, no
        /// account or email field. Best-effort — the file's shape is undocumented and CLI-owned, so any
        /// read/parse failure (including a concurrent CLI write) just means no label is shown.
        /// </summary>
        private static string GetSignedInClaudeAccountLabel()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            return ReadClaudeAccountLabelFromFile(path);
        }

        /// <summary>
        /// WSL-aware counterpart of <see cref="GetSignedInClaudeAccountLabel"/>: the CLI running under
        /// <c>ClaudeCodeWSL</c> authenticates against the distro's own <c>~/.claude.json</c>, a separate
        /// file from the Windows-side one the sync overload reads, so reusing that path for a WSL
        /// session always reported whichever account Windows last used instead of the one the running
        /// conversation is actually signed in as. Resolves the WSL-side path to its Windows UNC form via
        /// <see cref="ResolveWslPathAsync"/> first, then reads it exactly like the Windows copy.
        /// </summary>
        private static async Task<string> GetSignedInClaudeAccountLabelAsync(bool isWsl)
        {
            if (!isWsl)
            {
                return GetSignedInClaudeAccountLabel();
            }

            string wslPath = await ResolveWslPathAsync("$HOME/.claude.json");
            return string.IsNullOrEmpty(wslPath) ? null : ReadClaudeAccountLabelFromFile(wslPath);
        }

        private static string ReadClaudeAccountLabelFromFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }

                JToken account = JObject.Parse(json)["oauthAccount"];
                if (account == null)
                {
                    return null;
                }

                string email = (string)account["emailAddress"];
                string displayName = (string)account["displayName"];

                if (string.IsNullOrWhiteSpace(email))
                {
                    return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
                }

                return string.IsNullOrWhiteSpace(displayName) ? email : $"{displayName} ({email})";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadClaudeAccountLabelFromFile failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Asks the CLI itself who is signed in via <c>claude auth status --json</c> (issue: native-mode
        /// account switching used to only open claude.ai in a browser, which cannot change what the
        /// locally-run CLI is authenticated as — the CLI's own credentials are a separate store from a
        /// browser session against the web app). Used right after a Change-Account relaunch, when the
        /// process just restarted and a subprocess round-trip's latency is negligible; the cheap
        /// file-based <see cref="GetSignedInClaudeAccountLabel"/> stays the source for the passive
        /// welcome-screen fact, where a blocking CLI spawn on every session start would be a bad trade.
        /// </summary>
        private async Task<string> GetClaudeAuthStatusEmailAsync(bool isWsl, CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = isWsl
                    ? new ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        Arguments = "bash -lic \"claude auth status --json\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                    : new ProcessStartInfo
                    {
                        FileName = ResolveNativeClaudeExecutable(),
                        Arguments = "auth status --json",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                using (var process = Process.Start(startInfo))
                {
                    bool completed = await WaitForProcessExitAsync(process, 5000, cancellationToken);
                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    JObject status = JObject.Parse(output);
                    if ((bool?)status["loggedIn"] != true)
                    {
                        return null;
                    }

                    string email = (string)status["email"];
                    return string.IsNullOrWhiteSpace(email) ? null : email;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetClaudeAuthStatusEmailAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears the CLI's own record of who is signed in (<c>~/.claude.json</c>'s <c>oauthAccount</c>
        /// key), so a native-mode "Change Account" cannot relaunch and immediately re-report the old
        /// account just because the file on disk hasn't changed yet. Native mode has no console to run
        /// the terminal-mode <c>/logout</c> against (<see cref="ChangeAccountNativeMenuItem_Click"/>), so
        /// this is the closest equivalent reachable headlessly. Best-effort and local-only: it does not
        /// revoke the token server-side, only removes the label source, exactly like a read failure in
        /// <see cref="GetSignedInClaudeAccountLabel"/> already means "no label" rather than "logged out".
        /// </summary>
        private static void InvalidateSignedInClaudeAccountLabel()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            RemoveOauthAccountKey(path);
        }

        /// <summary>
        /// WSL-aware counterpart of <see cref="InvalidateSignedInClaudeAccountLabel"/> — see <see
        /// cref="GetSignedInClaudeAccountLabelAsync"/> for why the WSL-side credential file is a
        /// different one. Also drops the cached welcome-card label (<see cref="_wslAccountLabels"/>) so
        /// the next card re-reads instead of instantly re-showing the account just logged out of.
        /// </summary>
        private static async Task InvalidateSignedInClaudeAccountLabelAsync(bool isWsl)
        {
            _wslAccountLabels.TryRemove(AiProvider.ClaudeCodeWSL, out _);

            if (!isWsl)
            {
                InvalidateSignedInClaudeAccountLabel();
                return;
            }

            string wslPath = await ResolveWslPathAsync("$HOME/.claude.json");
            if (!string.IsNullOrEmpty(wslPath))
            {
                RemoveOauthAccountKey(wslPath);
            }
        }

        private static void RemoveOauthAccountKey(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }

                JObject root = JObject.Parse(json);
                if (root.Remove("oauthAccount"))
                {
                    File.WriteAllText(path, root.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RemoveOauthAccountKey failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves a WSL-side path expression (evaluated by bash, so <c>$HOME</c> etc. work) to its
        /// Windows UNC form via <c>wslpath -w</c>, the same approach
        /// <see cref="ResolveWslSessionDirectoryAsync"/> uses for the session transcripts directory —
        /// here for the CLI's own credential file so it can be read/edited through regular .NET file IO.
        /// </summary>
        private static async Task<string> ResolveWslPathAsync(string linuxPathExpression)
        {
            try
            {
                string args = $"bash -lic \"wslpath -w \\\"{linuxPathExpression}\\\"\"";
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();
                    Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                    bool exited = await Task.Run(() => p.WaitForExit(5000));
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }

                    string stdout = await stdoutTask;
                    await stderrTask;
                    stdout = stdout?.Trim();
                    return string.IsNullOrEmpty(stdout) ? null : stdout;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResolveWslPathAsync error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The lines under the mascot. Only what the extension actually controls is claimed: model and
        /// reasoning are omitted for agents that pick those inside their own UI, exactly as the
        /// composer hides those selectors for them. Claude effort and Codex reasoning are both owned
        /// by the extension and are therefore shown.
        /// </summary>
        private List<string> BuildWelcomeFacts(AiProvider? provider, string workspace)
        {
            var facts = new List<string>();

            bool isClaude = IsClaudeProvider(provider);
            bool isCodex = IsCodexProvider(provider);
            bool isDevin = provider == AiProvider.Devin || provider == AiProvider.DevinNative;

            if (isClaude)
            {
                facts.Add(L("{0} with {1} effort", GetChatModelLabel(provider), GetChatEffortLabel().ToLowerInvariant()));

                bool isWsl = provider == AiProvider.ClaudeCodeWSL;
                string account = isWsl
                    ? (_wslAccountLabels.TryGetValue(AiProvider.ClaudeCodeWSL, out string cachedAccount) ? cachedAccount : null)
                    : GetSignedInClaudeAccountLabel();

                if (!string.IsNullOrEmpty(account))
                {
                    facts.Add(WelcomeCardLayout.BreakBeforeEmail(L("Signed in as {0}", account)));
                }
            }
            else if (isCodex)
            {
                string model = GetChatModelLabel(provider);
                string modelLabel = string.Equals(model, L("Model"), StringComparison.Ordinal)
                    ? L("Agent default model")
                    : model;
                facts.Add(L("{0} with {1} reasoning", modelLabel,
                    GetChatCodexReasoningLabel().ToLowerInvariant()));

                if (provider.HasValue &&
                    _codexAccountLabels.TryGetValue(provider.Value, out string account) &&
                    !string.IsNullOrWhiteSpace(account))
                {
                    facts.Add(WelcomeCardLayout.BreakBeforeEmail(L("Signed in as {0}", account)));
                }
            }
            else if (isDevin)
            {
                facts.Add(GetChatModelLabel(provider));
            }

            string permission = GetChatPermissionLabel(provider);
            if (!string.IsNullOrEmpty(permission))
            {
                facts.Add(permission);
            }

            if (!string.IsNullOrWhiteSpace(workspace))
            {
                facts.Add(workspace);
            }

            return facts;
        }

        /// <summary>
        /// The right-hand column. Deliberately only things the chat tab can actually do — a tip that
        /// does not work is worse than no tip, so the composer's "@" picker is not mentioned (there
        /// isn't one) and the slash-command tip appears only where those commands are handled.
        /// </summary>
        private static List<string> BuildWelcomeTips(AiProvider? provider)
        {
            var tips = new List<string>
            {
                L("Press Ctrl+Up/Down in the prompt box for prompt history."),
                L("Ctrl+V pastes an image from the clipboard, or drag & drop files onto the chat."),
                L("Ctrl+Scroll zooms the conversation (Ctrl+0 resets it), and the top edge of the prompt box can be dragged."),
                L("The buttons below switch agent, model, effort and permissions mid-conversation.")
            };

            if (IsClaudeProvider(provider))
            {
                tips.Add(L("Type /plan, /model or /effort to switch without leaving the prompt box."));
                tips.Add(L("This chat runs locally in Visual Studio and isn't published to claude.ai. " +
                           "Start \"claude\" in a terminal for a session you can follow from the Claude mobile and desktop apps."));
            }

            tips.Add(L("✚ starts a new chat."));

            return tips;
        }

        /// <summary>
        /// Asks the CLI for its version in the background and re-renders the card when it answers. Not
        /// awaited by anything: the card is complete without it, and a CLI that is slow to start must
        /// not delay the conversation.
        /// </summary>
        private void BeginCliVersionLookup(AiProvider provider, string workspace)
        {
            if (_cliVersions.ContainsKey(provider))
            {
                // Already measured — BuildWelcomeTitle has it, or it is known to be unavailable.
                return;
            }

            string fileName;
            string arguments;
            string pathOverride;

            if (!TryBuildVersionCommand(provider, out fileName, out arguments, out pathOverride))
            {
                _cliVersions[provider] = string.Empty;
                return;
            }

#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                string version = await Task.Run(() => ReadCliVersion(fileName, arguments, pathOverride))
                    .ConfigureAwait(false);

                _cliVersions[provider] = version ?? string.Empty;

                if (string.IsNullOrEmpty(version))
                {
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Nothing below is worth doing once the user has moved to a different agent: the
                // update notice would land in the wrong transcript and the redraw would be moot.
                if (GetActiveOrSelectedProvider() != provider)
                {
                    return;
                }

                // Mirrors the terminal greeting's changelog line after a self-update between sessions.
                MaybeAnnounceCliUpdate(provider, version);

                // Only worth redrawing while the card this probe belongs to is still the one on screen:
                // the user may already have sent a prompt.
                if (ChatTranscript != null && ChatTranscript.IsWelcomeVisible)
                {
                    ShowChatWelcome(workspace);
                }
            }).FileAndForget("claudecode/nativemode/cliversion");
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Looks up the signed-in WSL account in the background and re-renders the card when it answers,
        /// same pattern as <see cref="BeginCliVersionLookup"/> — but unlike that cache, an empty result
        /// here is deliberately never stored: a CLI version that comes back empty stays empty for the
        /// rest of the VS session, while "not signed in" can flip to "signed in" moments later (mid
        /// login, or a slow write to the CLI's own state file), so caching a miss would have silenced
        /// every later welcome card until the next explicit Change Account click. Only needed for
        /// <see cref="AiProvider.ClaudeCodeWSL"/> — the Windows provider's account is read synchronously
        /// in <see cref="BuildWelcomeFacts"/> since that is a fast local file read, but the WSL copy needs
        /// a <c>wslpath</c> round-trip first, which must not block the card already on screen.
        /// </summary>
        private void BeginAccountLabelLookup(AiProvider provider, string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (provider == AiProvider.ClaudeCodeWSL)
            {
                BeginClaudeWslAccountLabelLookup(provider, workspace);
                return;
            }

            if (IsCodexProvider(provider))
            {
                BeginCodexAccountLabelLookup(provider, workspace);
            }
        }

        private void BeginClaudeWslAccountLabelLookup(AiProvider provider, string workspace)
        {
            if (_wslAccountLabels.ContainsKey(provider))
            {
                return;
            }

#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                string account = await GetSignedInClaudeAccountLabelAsync(true).ConfigureAwait(false);

                if (string.IsNullOrEmpty(account))
                {
                    return;
                }

                _wslAccountLabels[provider] = account;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Only worth redrawing while the card this probe belongs to is still the one on screen:
                // the user may already have sent a prompt, or switched to another agent.
                if (ChatTranscript != null &&
                    ChatTranscript.IsWelcomeVisible &&
                    GetActiveOrSelectedProvider() == provider)
                {
                    ShowChatWelcome(workspace);
                }
            }).FileAndForget("claudecode/nativemode/accountlabel");
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Reads the active Codex identity without opening its token file. The documented app-server
        /// account endpoint works whether credentials live in <c>auth.json</c> or the OS keyring and
        /// returns the ChatGPT email when one is available. It runs after the card is already visible
        /// and re-renders only when the label changes.
        /// </summary>
        private void BeginCodexAccountLabelLookup(AiProvider provider, string workspace)
        {
            if (_codexAccountLabelFetchedUtc.TryGetValue(provider, out DateTime fetchedUtc) &&
                DateTime.UtcNow - fetchedUtc < CodexAccountLabelTimeToLive)
            {
                return;
            }

            if (!_codexAccountLabelLookups.TryAdd(provider, 0))
            {
                return;
            }

#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    string account = await GetSignedInCodexAccountLabelAsync(provider).ConfigureAwait(false);

                    string previous = null;
                    _codexAccountLabels.TryGetValue(provider, out previous);

                    if (string.IsNullOrWhiteSpace(account))
                    {
                        _codexAccountLabels.TryRemove(provider, out _);
                    }
                    else
                    {
                        _codexAccountLabels[provider] = account;
                    }

                    _codexAccountLabelFetchedUtc[provider] = DateTime.UtcNow;

                    if (string.Equals(previous, account, StringComparison.Ordinal))
                    {
                        return;
                    }

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (ChatTranscript != null &&
                        ChatTranscript.IsWelcomeVisible &&
                        GetActiveOrSelectedProvider() == provider)
                    {
                        ShowChatWelcome(workspace);
                    }
                }
                finally
                {
                    _codexAccountLabelLookups.TryRemove(provider, out _);
                }
            }).FileAndForget("claudecode/nativemode/codexaccountlabel");
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Starts a short-lived Codex app-server, completes its JSONL handshake and calls
        /// <c>account/read</c>. This deliberately never reads or decodes credentials itself.
        /// </summary>
        private async Task<string> GetSignedInCodexAccountLabelAsync(AiProvider provider)
        {
            JsonLineProcessHost host = null;
            EventHandler<string> onLine = null;

            try
            {
                bool isWsl = provider == AiProvider.Codex;
                string executable = ResolveNativeProviderExecutable(provider, "codex");
                string freshPath = GetFreshPathFromRegistry();
                string fileName;
                string arguments;

                if (isWsl)
                {
                    fileName = "wsl.exe";
                    arguments = "bash -ic " +
                        QuoteForWindowsCommandArgument(QuoteForBash(executable) + " app-server");
                    freshPath = string.Empty;
                }
                else
                {
                    executable = ResolveExecutableOnPath(executable, freshPath);
                    bool isBatch = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

                    fileName = isBatch ? "cmd.exe" : executable;
                    arguments = isBatch
                        ? "/c " + QuoteForWindowsCommandArgument(executable + " app-server")
                        : "app-server";
                }

                var options = new JsonLineProcessOptions
                {
                    FileName = fileName,
                    Arguments = arguments
                };

                if (!string.IsNullOrWhiteSpace(freshPath))
                {
                    options.EnvironmentOverrides["PATH"] = freshPath;
                }

                var initializeResponse = new TaskCompletionSource<JObject>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var accountResponse = new TaskCompletionSource<JObject>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                host = new JsonLineProcessHost(options);
                onLine = delegate (object sender, string line)
                {
                    try
                    {
                        JObject message = JObject.Parse(line);
                        int? id = (int?)message["id"];

                        if (id == 0)
                        {
                            initializeResponse.TrySetResult(message);
                        }
                        else if (id == 1)
                        {
                            accountResponse.TrySetResult(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Codex account probe ignored a non-JSON line: {ex.Message}");
                    }
                };
                host.LineReceived += onLine;

                await host.StartAsync(CancellationToken.None).ConfigureAwait(false);

                var initialize = new JObject
                {
                    ["method"] = "initialize",
                    ["id"] = 0,
                    ["params"] = new JObject
                    {
                        ["clientInfo"] = new JObject
                        {
                            ["name"] = "claude_code_extension_visual_studio",
                            ["title"] = "Claude Code Extension for Visual Studio",
                            ["version"] = "1.0"
                        }
                    }
                };

                Task timeout = Task.Delay(CodexAccountLookupTimeoutMs);
                await host.WriteLineAsync(
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        initialize,
                        Newtonsoft.Json.Formatting.None),
                    CancellationToken.None).ConfigureAwait(false);

                if (await Task.WhenAny(initializeResponse.Task, timeout).ConfigureAwait(false)
                    != initializeResponse.Task)
                {
                    return null;
                }

                JObject initMessage = await initializeResponse.Task.ConfigureAwait(false);
                if (initMessage["error"] != null)
                {
                    return null;
                }

                var initialized = new JObject
                {
                    ["method"] = "initialized",
                    ["params"] = new JObject()
                };
                var accountRead = new JObject
                {
                    ["method"] = "account/read",
                    ["id"] = 1,
                    ["params"] = new JObject { ["refreshToken"] = false }
                };

                await host.WriteLineAsync(
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        initialized,
                        Newtonsoft.Json.Formatting.None),
                    CancellationToken.None).ConfigureAwait(false);
                await host.WriteLineAsync(
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        accountRead,
                        Newtonsoft.Json.Formatting.None),
                    CancellationToken.None).ConfigureAwait(false);

                if (await Task.WhenAny(accountResponse.Task, timeout).ConfigureAwait(false)
                    != accountResponse.Task)
                {
                    return null;
                }

                JObject response = await accountResponse.Task.ConfigureAwait(false);
                JToken account = response["result"]?["account"];
                if (account == null || account.Type == JTokenType.Null)
                {
                    return null;
                }

                string type = (string)account["type"];
                string email = (string)account["email"];

                if (!string.IsNullOrWhiteSpace(email))
                {
                    return email;
                }

                if (string.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase))
                {
                    return L("OpenAI API key");
                }

                if (string.Equals(type, "chatgpt", StringComparison.OrdinalIgnoreCase))
                {
                    return "ChatGPT";
                }

                if (string.Equals(type, "amazonBedrock", StringComparison.OrdinalIgnoreCase))
                {
                    return "Amazon Bedrock";
                }

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSignedInCodexAccountLabelAsync failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (host != null && onLine != null)
                {
                    host.LineReceived -= onLine;
                }

                host?.Dispose();
            }
        }

        /// <summary>
        /// Builds the "--version" command for a provider, resolving the executable exactly the way the
        /// session launch does so the version reported is the one that is actually running.
        /// </summary>
        private bool TryBuildVersionCommand(AiProvider provider, out string fileName, out string arguments,
            out string pathOverride)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            pathOverride = GetFreshPathFromRegistry();

            bool isWsl = provider == AiProvider.ClaudeCodeWSL
                || provider == AiProvider.Codex
                || provider == AiProvider.CursorAgent
                || provider == AiProvider.Devin;

            string executable;

            switch (provider)
            {
                case AiProvider.ClaudeCode:
                    executable = ResolveNativeClaudeExecutable();
                    break;

                case AiProvider.ClaudeCodeWSL:
                    executable = "claude";
                    break;

                case AiProvider.Codex:
                case AiProvider.CodexNative:
                    executable = ResolveNativeProviderExecutable(provider, "codex");
                    break;

                case AiProvider.CursorAgent:
                case AiProvider.CursorAgentNative:
                    executable = ResolveNativeCursorExecutable(provider, isWsl, pathOverride);
                    break;

                case AiProvider.OpenCode:
                case AiProvider.Devin:
                case AiProvider.DevinNative:
                case AiProvider.Reasonix:
                case AiProvider.QwenCode:
                case AiProvider.Grok:
                    executable = ResolveNativeProviderExecutable(provider, GetAcpDefaultCommand(provider));
                    break;

                case AiProvider.Pi:
                    executable = ResolveNativeProviderExecutable(provider, "pi");
                    break;

                case AiProvider.Antigravity:
                    executable = ResolveNativeProviderExecutable(provider, "agy");
                    break;

                default:
                    return false;
            }

            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            if (isWsl)
            {
                // A login shell for the same reason the session launch uses one: the CLI is usually
                // installed by a version manager that only puts it on PATH from the profile scripts.
                fileName = "wsl.exe";
                arguments = "bash -lic \"" + executable + " --version\"";
                pathOverride = string.Empty;
                return true;
            }

            fileName = ResolveExecutableOnPath(executable, pathOverride);
            arguments = "--version";

            return !string.IsNullOrWhiteSpace(fileName);
        }

        /// <summary>
        /// Runs the probe and pulls a version number out of whatever it prints. Reads both pipes
        /// asynchronously: a CLI that fills one of them while nobody drains it blocks forever, and this
        /// runs on a pool thread that would then never come back.
        /// </summary>
        private static string ReadCliVersion(string fileName, string arguments, string pathOverride)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrWhiteSpace(pathOverride))
                {
                    startInfo.EnvironmentVariables["PATH"] = pathOverride;
                }

                var output = new StringBuilder();

                using (var process = new Process { StartInfo = startInfo })
                {
                    DataReceivedEventHandler collect = delegate (object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;

                        lock (output)
                        {
                            output.AppendLine(e.Data);
                        }
                    };

                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;

                    if (!process.Start())
                    {
                        return string.Empty;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(CliVersionTimeoutMs))
                    {
                        try
                        {
                            ProcessTree.Kill(process.Id);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Version probe could not be stopped: {ex.Message}");
                        }

                        return string.Empty;
                    }

                    lock (output)
                    {
                        return ExtractVersion(output.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Version probe for '{fileName}' failed: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// First version-shaped token in the output. The CLIs disagree on the format — bare
        /// "2.1.220", "codex-cli 0.5.0", "cursor-agent version 1.2.3" — so the number is picked out
        /// rather than the line being trusted whole.
        /// </summary>
        private static string ExtractVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            Match match = Regex.Match(output, @"\d+\.\d+(\.\d+)*([-+][0-9A-Za-z.\-]+)?");

            return match.Success ? match.Value : string.Empty;
        }

        #endregion

        #region Composer Selectors

        /// <summary>
        /// Provider name for the composer's agent selector, qualified by platform. The compact name is
        /// ambiguous here: the Windows and WSL builds of the same agent are separate entries in the
        /// list, and without the suffix the menu shows the same caption twice.
        /// </summary>
        private string GetChatProviderDisplayName(AiProvider? provider)
        {
            string name = GetProviderDisplayName(provider);

            switch (provider)
            {
                case AiProvider.ClaudeCodeWSL:
                case AiProvider.Codex:
                case AiProvider.CursorAgent:
                case AiProvider.Devin:
                    return name + " (WSL)";

                case AiProvider.CodexNative:
                case AiProvider.CursorAgentNative:
                case AiProvider.DevinNative:
                    return name + " (native)";

                default:
                    return name;
            }
        }

        private string GetChatModelLabel(AiProvider? provider)
        {
            if (!IsClaudeProvider(provider) && ProviderHasModelCatalog(provider))
            {
                string label = GetSelectedProviderModelLabel(provider);

                // Nothing chosen: the agent runs on its own default, which the extension cannot name.
                return string.IsNullOrWhiteSpace(label) ? L("Model") : label;
            }

            switch (_settings?.SelectedClaudeModel)
            {
                case ClaudeModel.Opus: return "Opus";
                case ClaudeModel.Sonnet: return "Sonnet";
                case ClaudeModel.Haiku: return "Haiku";
                case ClaudeModel.OpusPlan: return "Opus Plan";
                default: return "Fable";
            }
        }

        /// <summary>Same caption, read from a parallel tab's own model snapshot instead of the global settings.</summary>
        private string GetChatModelLabel(NativeChatSessionState session)
        {
            AiProvider provider = session.SelectedProvider;

            if (!IsClaudeProvider(provider) && ProviderHasModelCatalog(provider))
            {
                string label = GetSelectedProviderModelLabel(provider, session.SelectedModel);
                return string.IsNullOrWhiteSpace(label) ? L("Model") : label;
            }

            switch (session.SelectedClaudeModel)
            {
                case ClaudeModel.Opus: return "Opus";
                case ClaudeModel.Sonnet: return "Sonnet";
                case ClaudeModel.Haiku: return "Haiku";
                case ClaudeModel.OpusPlan: return "Opus Plan";
                default: return "Fable";
            }
        }

        private string GetChatEffortLabel()
        {
            return GetChatEffortLabel(_settings != null ? _settings.SelectedEffortLevel : EffortLevel.High);
        }

        private static string GetChatEffortLabel(EffortLevel level)
        {
            switch (level)
            {
                case EffortLevel.XHigh: return "Extra High";
                case EffortLevel.Ultracode: return "Ultracode";
                default: return level.ToString();
            }
        }

        private string GetChatCodexReasoningLabel()
        {
            CodexReasoningLevel level = _settings != null
                ? _settings.SelectedCodexReasoningLevel
                : CodexReasoningLevel.Default;
            return GetCodexReasoningLabel(level);
        }

        /// <summary>
        /// Captions of the effort slider stops, in slider order, so the popup can name the level under
        /// the thumb before it is applied.
        /// </summary>
        private static string[] GetChatEffortStopLabels()
        {
            return Array.ConvertAll(_effortSliderOrder, GetChatEffortLabel);
        }

        private static string[] GetChatCodexReasoningStopLabels()
        {
            return Array.ConvertAll(_codexReasoningSliderOrder, GetCodexReasoningLabel);
        }

        /// <summary>
        /// Caption for the permission selector, or null for agents whose permission handling this
        /// extension does not drive (the button is hidden for those).
        /// </summary>
        private string GetChatPermissionLabel(AiProvider? provider)
        {
            bool? skipping = GetChatPermissionSkipFlag(provider);
            if (!skipping.HasValue)
            {
                return null;
            }

            if (IsClaudeProvider(provider))
            {
                return GetClaudePermissionLabel(GetNativeClaudePermissionChoice(skipping.Value));
            }

            return skipping.Value ? L("Skip permissions") : L("Ask permission");
        }

        /// <summary>
        /// The Claude permission mode the panel session runs in: the saved choice, unless the plan
        /// approved in this conversation has already moved it out of plan mode (#181). The launch, the
        /// composer caption and the menu checkmarks all read this, so they cannot disagree.
        /// </summary>
        private ClaudePermissionChoice GetNativeClaudePermissionChoice(bool skipping)
        {
            return _nativePlanExitChoice ?? ClaudeCommandBuilder.ResolvePermissionChoice(
                _settings?.ClaudePlanMode == true, skipping, _settings?.ClaudeAutoPermissions == true,
                _settings?.ClaudeManualMode == true);
        }

        /// <summary>The parallel-tab counterpart of <see cref="GetNativeClaudePermissionChoice(bool)"/>, read from that tab's own snapshot.</summary>
        private static ClaudePermissionChoice GetNativeClaudePermissionChoice(NativeChatSessionState session)
        {
            return session.PlanExitChoice ?? ClaudeCommandBuilder.ResolvePermissionChoice(
                session.PlanMode, session.SkipPermissions, session.AutoPermissions, session.ManualMode);
        }

        /// <summary>
        /// Caption for a resolved Claude permission state. Goes through
        /// <see cref="ClaudeCommandBuilder.ResolvePermissionChoice"/> rather than testing the four
        /// flags in its own order, so the caption always names the mode the session was launched with
        /// (v188.0: an auto-before-skip order read "Auto" over a skip-permissions launch).
        /// </summary>
        private static string GetClaudePermissionLabel(ClaudePermissionChoice choice)
        {
            switch (choice)
            {
                case ClaudePermissionChoice.PlanMode: return L("Plan mode");
                case ClaudePermissionChoice.SkipPermissions: return L("Skip permissions");
                case ClaudePermissionChoice.Auto: return L("Auto mode");
                case ClaudePermissionChoice.ManualMode: return L("Manual mode");
                default: return L("Accept edits");
            }
        }

        /// <summary>Same caption, read from a parallel tab's own permission/plan-mode snapshot instead of the global settings.</summary>
        private string GetChatPermissionLabel(NativeChatSessionState session)
        {
            AiProvider provider = session.SelectedProvider;

            // GetChatPermissionSkipFlag(provider) only used here to ask "does this extension drive
            // permissions for this provider at all" — the same provider set for every session — not
            // to read its (global) value, which the session's own SkipPermissions replaces below.
            if (GetChatPermissionSkipFlag(provider) == null)
            {
                return null;
            }

            if (IsClaudeProvider(provider))
            {
                return GetClaudePermissionLabel(GetNativeClaudePermissionChoice(session));
            }

            return session.SkipPermissions ? L("Skip permissions") : L("Ask permission");
        }

        private bool? GetChatPermissionSkipFlag(AiProvider? provider)
        {
            if (_settings == null)
            {
                return null;
            }

            if (IsClaudeProvider(provider)) return _settings.ClaudeDangerouslySkipPermissions;
            if (IsCodexProvider(provider)) return _settings.CodexFullAuto;
            if (IsCursorAgentProvider(provider)) return _settings.CursorAgentAutoRun;
            if (provider == AiProvider.Antigravity) return _settings.AntigravityDangerouslySkipPermissions;
            if (provider == AiProvider.Devin || provider == AiProvider.DevinNative) return _settings.DevinDangerousMode;
            if (provider == AiProvider.QwenCode) return _settings.QwenCodeYoloMode;
            if (provider == AiProvider.Grok) return _settings.GrokAlwaysApprove;

            return null;
        }

        private void SetChatPermissionSkipFlag(AiProvider? provider, bool skip)
        {
            if (_settings == null) return;

            if (IsClaudeProvider(provider)) _settings.ClaudeDangerouslySkipPermissions = skip;
            else if (IsCodexProvider(provider)) _settings.CodexFullAuto = skip;
            else if (IsCursorAgentProvider(provider)) _settings.CursorAgentAutoRun = skip;
            else if (provider == AiProvider.Antigravity) _settings.AntigravityDangerouslySkipPermissions = skip;
            else if (provider == AiProvider.Devin || provider == AiProvider.DevinNative) _settings.DevinDangerousMode = skip;
            else if (provider == AiProvider.QwenCode) _settings.QwenCodeYoloMode = skip;
            else if (provider == AiProvider.Grok) _settings.GrokAlwaysApprove = skip;
        }

        #endregion

        #region Composer Send and Attachments

        /// <summary>
        /// Sends what the user typed in the tab. The text is handed to the panel's prompt box and the
        /// existing send path runs untouched, so attachments, prompt history, diff tracking and the
        /// "On Agent Finish" bookkeeping behave exactly as they do from the panel.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerSendRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // The central send path may still be copying attachments or opening the Changes view.
                // Leave the text in the composer until that short preparation window ends instead of
                // clearing it and then having SendButton_Click reject it via its re-entrancy guard.
                if (_isSendingPrompt)
                {
                    return;
                }

                string text = ChatTranscript.ComposerText;
                if (string.IsNullOrWhiteSpace(text) && attachedImagePaths.Count == 0)
                {
                    return;
                }

                PromptTextBox.Text = text;
                ChatTranscript.ComposerText = string.Empty;
                ChatTranscript.ComposerAttachmentsPanel.Children.Clear();

                SendButton_Click(this, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat composer send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Raised by the composer's 📎 button. Mirrors the panel's own AttachDropdownButton — drag-and-
        /// drop and Ctrl+V paste already attach files and images, but a picker is still the only way to
        /// browse to a file outside the editor. Reuses <see cref="OnComposerFilesDropped"/> for the
        /// owner-resolution, existence checks and chip refresh, so a pick behaves exactly like a drop.
        /// </summary>
        private void OnComposerAttachRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string[] chosen = PickAttachmentFiles();
            if (chosen == null || chosen.Length == 0)
            {
                return;
            }

            OnComposerFilesDropped(sender, chosen);
        }

        /// <summary>
        /// 📎 → "Insert editor selection" in the composer. Stages the snippet in the composer that
        /// raised it (not the panel's prompt box, which is hidden while the chat is in its own tab),
        /// using the same "File: … (lines …)" format as the panel's menu. Does not send.
        /// </summary>
        private void OnComposerInsertSelectionRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // Docked in the panel (ActionsOnly) the composer has no text input — the panel's prompt
                // box is the live one, so the panel's own handler is the right target.
                if (!(sender is ChatTranscriptView transcript) || !transcript.HasComposerInput)
                {
                    GrabSelectionButton_Click(sender, null);
                    return;
                }

                if (!TryGetActiveEditorSelection(out string code, out string filePath, out int startLine, out int endLine))
                {
                    return;
                }

                string snippetBody = BuildCodeSnippetText(code, filePath, startLine, endLine);
                transcript.InsertTextAtComposerCaret(PrependSeparatorIfNeeded(snippetBody, transcript.ComposerText));
                transcript.FocusComposer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error inserting editor selection into composer: {ex.Message}");
            }
        }

        /// <summary>
        /// 📎 → "Insert active file path" in the composer: the composer counterpart to the panel's
        /// "Insert Active File Path" (issue #127), which users of the chat tab could not reach
        /// (issue #174 follow-up).
        /// </summary>
        private void OnComposerInsertActiveFilePathRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(sender is ChatTranscriptView transcript) || !transcript.HasComposerInput)
                {
                    InsertActiveFilePathMenuItem_Click(sender, null);
                    return;
                }

                string insert = BuildActiveFilePathReference();
                if (insert == null)
                {
                    return;
                }

                transcript.InsertTextAtComposerCaret(insert);
                transcript.FocusComposer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error inserting active file path into composer: {ex.Message}");
            }
        }

        private void OnComposerFilesDropped(object sender, string[] files)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            if (owner != null)
            {
                AddSessionAttachments(owner, files);
                return;
            }

            bool any = false;
            foreach (string path in files)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (Directory.Exists(path)) continue;
                if (!File.Exists(path)) continue;
                if (attachedImagePaths.Contains(path)) continue;

                attachedImagePaths.Add(path);
                any = true;
            }

            if (any)
            {
                UpdateImageDropDisplay();
            }
        }

        /// <summary>
        /// Stages files in one chat tab's own attachment list. Folders and paths that no longer exist
        /// are skipped, as they are in the panel — an agent can't be handed a directory.
        /// </summary>
        private void AddSessionAttachments(NativeChatSessionState session, IEnumerable<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session == null || paths == null)
            {
                return;
            }

            bool any = false;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (Directory.Exists(path)) continue;
                if (!File.Exists(path)) continue;
                if (session.AttachedFiles.Contains(path)) continue;

                session.AttachedFiles.Add(path);
                any = true;
            }

            if (any)
            {
                UpdateSessionAttachmentChips(session);
            }
        }

        /// <summary>
        /// Redraws every attachment strip — the panel's, the default chat's and each parallel tab's —
        /// after the icon style changed, since Modern draws images as cards instead of chips.
        /// </summary>
        private void RefreshAllAttachmentChips()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            UpdateImageDropDisplay();

            List<NativeChatSessionState> sessions;
            lock (_sessionLock)
            {
                sessions = new List<NativeChatSessionState>(_nativeSessions.Values);
            }

            foreach (NativeChatSessionState session in sessions)
            {
                UpdateSessionAttachmentChips(session);
            }
        }

        /// <summary>Redraws the attachment strip of one chat tab from that session's own list.</summary>
        private void UpdateSessionAttachmentChips(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session?.ChatTranscript == null)
            {
                return;
            }

            Panel host = session.ChatTranscript.ComposerAttachmentsPanel;
            host.Children.Clear();

            foreach (string path in session.AttachedFiles.ToList())
            {
                host.Children.Add(CreateAttachmentChip(path, p =>
                {
                    session.AttachedFiles.Remove(p);
                    UpdateSessionAttachmentChips(session);
                }));
            }

            host.Margin = new Thickness(0, 0, 0, host.Children.Count > 0 ? 4 : 0);
        }

        /// <summary>
        /// Mirrors the panel's attachment chips into the composer. They are separate elements because a
        /// WPF element cannot appear in two visual trees, but both read the same list.
        /// </summary>
        private void UpdateComposerAttachmentChips()
        {
            if (ChatTranscript == null || !_chatIsInTab)
            {
                return;
            }

            Panel host = ChatTranscript.ComposerAttachmentsPanel;
            host.Children.Clear();

            foreach (string path in attachedImagePaths)
            {
                host.Children.Add(CreateAttachmentChip(path));
            }

            host.Margin = new Thickness(0, 0, 0, host.Children.Count > 0 ? 4 : 0);
        }

        #endregion

        #region Composer Selectors

        /// <summary>
        /// Opens a <see cref="ContextMenu"/> anchored to a button that may live in a different window
        /// than the menu was declared in — a <see cref="ContextMenu"/> is its own popup, so retargeting
        /// <see cref="ContextMenu.PlacementTarget"/> across visual trees works. Shared by the composer's
        /// selector/config buttons (which open upward, since the composer sits at the bottom of the
        /// chat) and the panel toolbar's own dropdown buttons (which open downward).
        /// </summary>
        private static void OpenMenuAt(
            ContextMenu menu,
            UIElement anchor,
            System.Windows.Controls.Primitives.PlacementMode placement = System.Windows.Controls.Primitives.PlacementMode.Bottom)
        {
            if (menu == null || anchor == null)
            {
                return;
            }

            menu.PlacementTarget = anchor;
            menu.Placement = placement;
            menu.IsOpen = true;
        }

        private void OnComposerSelectorClicked(object sender, ChatSelector selector)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // sender is the transcript itself (v163.0), so the same handler can tell whether the click
            // came from the panel/default composer or a parallel tab's own; the anchor button is
            // recovered from the selector rather than passed through as the sender.
            var transcript = sender as ChatTranscriptView;
            UIElement anchor = transcript?.GetSelectorAnchor(selector);
            if (anchor == null)
            {
                return;
            }

            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            ContextMenu menu;

            if (owner != null)
            {
                // Model, permission and provider all relaunch this tab's own session
                // (RelaunchSessionAsync) rather than the panel's terminal-backed one — a provider switch
                // here never touches RestartTerminalWithSelectedProviderAsync, which only ever targets
                // the default session's embedded terminal (issue #171: the menu used to silently no-op
                // on every tab but the first because this case just returned).
                switch (selector)
                {
                    case ChatSelector.Provider: menu = BuildChatProviderMenu(owner); break;
                    case ChatSelector.Model: menu = BuildChatModelMenu(owner); break;
                    default: menu = BuildChatPermissionMenu(owner); break;
                }
            }
            else
            {
                // Effort is not here: it opens the pill slider inside the transcript view and reports
                // back through EffortChanged.
                switch (selector)
                {
                    case ChatSelector.Provider: menu = BuildChatProviderMenu(); break;
                    case ChatSelector.Model: menu = BuildChatModelMenu(); break;
                    default: menu = BuildChatPermissionMenu(); break;
                }
            }

            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            OpenMenuAt(menu, anchor, System.Windows.Controls.Primitives.PlacementMode.Top);
        }

        /// <summary>
        /// Opens the panel's actual ⚙/☰/⚡ <see cref="ContextMenu"/> anchored to its mirror button in
        /// the composer, rather than building a second copy of the menu contents. Works from any chat
        /// tab (default or parallel session) the same way, since the menus themselves resolve the
        /// focused session where that matters.
        /// </summary>
        private void OnComposerConfigMenuClicked(object sender, ChatConfigMenu menu)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var transcript = sender as ChatTranscriptView;
            UIElement anchor = transcript?.GetConfigMenuAnchor(menu);
            if (anchor == null)
            {
                return;
            }

            // The panel's menus act on the focused chat (e.g. Session History resumes into it), so
            // the tab whose mirror was clicked has to be that chat even if focus never entered it.
            MarkNativeSessionFocused(ResolveSessionFromSender(sender)?.SessionId);

            ContextMenu target;
            switch (menu)
            {
                case ChatConfigMenu.Tools: target = ToolsContextMenu; break;
                case ChatConfigMenu.CustomCommands: target = CustomCommandsContextMenu; break;
                default: target = ProviderContextMenu; break;
            }

            OpenMenuAt(target, anchor, System.Windows.Controls.Primitives.PlacementMode.Top);
        }

        /// <summary>
        /// Invokes whatever the panel's own promoted <see cref="ToolbarButton"/> would do, by raising
        /// its real <see cref="Button.Click"/> event — this stays correct if a handler changes, with no
        /// second switch statement to keep in sync. See <see cref="RefreshToolbarLayout"/> for how the
        /// composer's button list (<see cref="ChatTranscriptView.SetPromotedButtons"/>) is populated.
        /// </summary>
        private void OnComposerPromotedButtonClicked(object sender, string toolbarButtonName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var transcript = sender as ChatTranscriptView;
            UIElement anchor = transcript?.GetPromotedButtonAnchor(toolbarButtonName);
            if (anchor == null)
            {
                return;
            }

            if (!Enum.TryParse(toolbarButtonName, out ToolbarButton button))
            {
                return;
            }

            // See OnComposerConfigMenuClicked: the panel's handler targets the focused chat.
            MarkNativeSessionFocused(ResolveSessionFromSender(sender)?.SessionId);

            Button panelButton = GetToolbarButtonControl(button);
            panelButton?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }

        /// <summary>
        /// Builds a themed dropdown for the composer. The menus are rebuilt on every click because
        /// their contents depend on the running agent, which can change between two clicks.
        /// </summary>
        private ContextMenu CreateComposerMenu()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var menu = new ContextMenu
            {
                Background = themeBg,
                Foreground = themeFg
            };

            // Rounded, borderless chrome with no icon gutter: the default menu draws a checkbox column
            // and a square white frame, which look nothing like the rest of the composer.
            var style = ChatTranscript?.TryFindResource("ComposerMenuStyle") as Style;
            if (style != null)
            {
                menu.Style = style;
            }

            return menu;
        }

        /// <summary>
        /// Adds one entry to a composer dropdown. The parent is an <see cref="ItemsControl"/> rather
        /// than the menu itself so the same themed entry can go into a submenu (see
        /// <see cref="AddComposerSubmenu"/>), which is how a long model list is broken up.
        /// </summary>
        private MenuItem AddComposerMenuItem(ItemsControl menu, string header, bool isChecked, RoutedEventHandler onClick)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = isChecked,
                StaysOpenOnClick = false,
                Background = themeBg,
                Foreground = themeFg
            };

            var style = ChatTranscript?.TryFindResource("ComposerMenuItemStyle") as Style;
            if (style != null)
            {
                item.Style = style;
            }

            item.Click += onClick;
            menu.Items.Add(item);

            return item;
        }

        /// <summary>
        /// Adds a submenu header to a composer dropdown and returns it, so entries can be added to it
        /// with <see cref="AddComposerMenuItem"/>.
        /// </summary>
        private MenuItem AddComposerSubmenu(ItemsControl menu, string header)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = false,
                Background = themeBg,
                Foreground = themeFg
            };

            var style = ChatTranscript?.TryFindResource("ComposerSubmenuStyle") as Style;
            if (style != null)
            {
                item.Style = style;
            }

            menu.Items.Add(item);

            return item;
        }

        /// <summary>
        /// The agent selector is shown only when its menu offers a choice: two or more enabled agents
        /// that run in the chat, or a tab running an agent outside that list (resumed from Session
        /// History, say), which must keep a way back. With the default single agent it is just noise.
        /// Same list as <see cref="BuildChatProviderMenu()"/>.
        /// </summary>
        internal static bool ShouldShowChatProviderSelector(IEnumerable<AiProvider> visibleProviders, AiProvider? running)
        {
            List<AiProvider> choices = (visibleProviders ?? Enumerable.Empty<AiProvider>())
                .Where(SupportsNativeMode)
                .Distinct()
                .ToList();
            if (choices.Count == 0)
            {
                choices.Add(AiProvider.ClaudeCode);
            }

            return choices.Count > 1 || (running.HasValue && !choices.Contains(running.Value));
        }

        private ContextMenu BuildChatProviderMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            List<AiProvider> visible = _settings?.VisibleProviders;
            if (visible == null || visible.Count == 0)
            {
                visible = new List<AiProvider> { AiProvider.ClaudeCode };
            }

            foreach (AiProvider provider in visible)
            {
                // An agent with no structured channel would drop the user back into the terminal the
                // moment it started, which is not what clicking an entry in the chat tab should do.
                if (!SupportsNativeMode(provider)) continue;

                AiProvider current = provider;
                AddComposerMenuItem(menu, GetChatProviderDisplayName(provider), provider == active,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderSelected(current); });
            }

            return menu;
        }

        /// <summary>
        /// The parallel-tab counterpart of <see cref="BuildChatProviderMenu()"/>: checked against and
        /// written to <see cref="NativeChatSessionState.SelectedProvider"/> instead of
        /// <c>_settings.SelectedProvider</c>, so each tab keeps its own agent (issue #171).
        /// </summary>
        private ContextMenu BuildChatProviderMenu(NativeChatSessionState session)
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider active = session.SelectedProvider;

            List<AiProvider> visible = _settings?.VisibleProviders;
            if (visible == null || visible.Count == 0)
            {
                visible = new List<AiProvider> { AiProvider.ClaudeCode };
            }

            foreach (AiProvider provider in visible)
            {
                // An agent with no structured channel would drop the user back into the terminal the
                // moment it started, which is not what clicking an entry in the chat tab should do.
                if (!SupportsNativeMode(provider)) continue;

                AiProvider current = provider;
                AddComposerMenuItem(menu, GetChatProviderDisplayName(provider), provider == active,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderSelectedForSession(session, current); });
            }

            return menu;
        }

        private ContextMenu BuildChatModelMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            if (active != null && !IsClaudeProvider(active) && ProviderHasModelCatalog(active))
            {
                FillChatModelMenu(menu, active.Value);

                return menu;
            }

            ClaudeModel selected = _settings != null ? _settings.SelectedClaudeModel : ClaudeModel.Sonnet;

            // Fable needs usage credits — left out on a zero balance unless it is the current pick.
            if (selected == ClaudeModel.Fable || IsFableModelOffered())
            {
                AddComposerMenuItem(menu, "Fable", selected == ClaudeModel.Fable,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Fable); });
            }
            AddComposerMenuItem(menu, "Opus", selected == ClaudeModel.Opus,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Opus); });
            AddComposerMenuItem(menu, "Sonnet", selected == ClaudeModel.Sonnet,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Sonnet); });
            AddComposerMenuItem(menu, "Haiku", selected == ClaudeModel.Haiku,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Haiku); });
            AddComposerMenuItem(menu, "Opus Plan", selected == ClaudeModel.OpusPlan,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.OpusPlan); });

            return menu;
        }

        /// <summary>The parallel-tab counterpart of <see cref="BuildChatModelMenu()"/>, read from that tab's own selection.</summary>
        private ContextMenu BuildChatModelMenu(NativeChatSessionState session)
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider provider = session.SelectedProvider;

            if (!IsClaudeProvider(provider) && ProviderHasModelCatalog(provider))
            {
                FillChatModelMenu(menu, provider, session);

                return menu;
            }

            ClaudeModel selected = session.SelectedClaudeModel;

            if (selected == ClaudeModel.Fable || IsFableModelOffered())
            {
                AddComposerMenuItem(menu, "Fable", selected == ClaudeModel.Fable,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelectedForSession(session, ClaudeModel.Fable); });
            }
            AddComposerMenuItem(menu, "Opus", selected == ClaudeModel.Opus,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelectedForSession(session, ClaudeModel.Opus); });
            AddComposerMenuItem(menu, "Sonnet", selected == ClaudeModel.Sonnet,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelectedForSession(session, ClaudeModel.Sonnet); });
            AddComposerMenuItem(menu, "Haiku", selected == ClaudeModel.Haiku,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelectedForSession(session, ClaudeModel.Haiku); });
            AddComposerMenuItem(menu, "Opus Plan", selected == ClaudeModel.OpusPlan,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelectedForSession(session, ClaudeModel.OpusPlan); });

            return menu;
        }

        /// <summary>
        /// Fills the composer's model dropdown with the agent's own models, plus an entry that leaves
        /// the model to the agent. A stale or missing list is re-read from the CLI in the background
        /// and dropped into the same menu when it arrives, so the dropdown opens instantly either way.
        /// </summary>
        private void FillChatModelMenu(ContextMenu menu, AiProvider provider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string selected = GetSelectedProviderModelId(provider);
            List<Agents.ModelOption> models = GetCachedProviderModels(provider);

            if (ProviderUsesModelPicker(provider))
            {
                FillChatModelPickerEntries(menu, provider, models, selected,
                    picked => OnChatProviderModelSelected(picked));
            }
            else
            {
                List<Agents.ModelGroup> groups = Agents.ModelCatalogGrouping.Group(models);

                // Repeated at the top when the list is grouped: inside a submenu the checked entry is
                // invisible until the right one is opened.
                if (groups.Exists(g => g.IsSubmenu) && !string.IsNullOrWhiteSpace(selected))
                {
                    AddChatModelMenuItem(menu, GetSelectedModelOption(provider, models, selected), selected);
                }

                foreach (Agents.ModelGroup group in groups)
                {
                    ItemsControl parent = group.IsSubmenu ? AddComposerSubmenu(menu, group.Name) : (ItemsControl)menu;

                    foreach (Agents.ModelOption model in group.Models)
                    {
                        AddChatModelMenuItem(parent, model, selected);
                    }
                }
            }

            AddComposerMenuItem(menu, L("Agent default"), string.IsNullOrWhiteSpace(selected),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelected(string.Empty); });

            if (!ShouldRefreshProviderModels(provider)) return;

            MenuItem loading = AddComposerMenuItem(menu, L("Loading models…"), false, delegate { });
            loading.IsEnabled = false;

            _ = RefreshProviderModelsAsync(provider).ContinueWith(
                delegate
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    // Only worth doing while the dropdown is still on screen; otherwise the next
                    // click rebuilds it from the cache that was just filled.
                    if (!menu.IsOpen || GetActiveOrSelectedProvider() != provider) return;

                    menu.Items.Clear();
                    FillChatModelMenu(menu, provider);
                },
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>The parallel-tab counterpart of <see cref="FillChatModelMenu(ContextMenu, AiProvider)"/>.</summary>
        private void FillChatModelMenu(ContextMenu menu, AiProvider provider, NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string selected = session.SelectedModel;
            List<Agents.ModelOption> models = GetCachedProviderModels(provider);

            if (ProviderUsesModelPicker(provider))
            {
                FillChatModelPickerEntries(menu, provider, models, selected,
                    picked => OnChatProviderModelSelectedForSession(session, picked));
            }
            else
            {
                List<Agents.ModelGroup> groups = Agents.ModelCatalogGrouping.Group(models);

                if (groups.Exists(g => g.IsSubmenu) && !string.IsNullOrWhiteSpace(selected))
                {
                    AddChatModelMenuItem(menu, GetSelectedModelOption(provider, models, selected), selected, session);
                }

                foreach (Agents.ModelGroup group in groups)
                {
                    ItemsControl parent = group.IsSubmenu ? AddComposerSubmenu(menu, group.Name) : (ItemsControl)menu;

                    foreach (Agents.ModelOption model in group.Models)
                    {
                        AddChatModelMenuItem(parent, model, selected, session);
                    }
                }
            }

            AddComposerMenuItem(menu, L("Agent default"), string.IsNullOrWhiteSpace(selected),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelectedForSession(session, string.Empty); });

            if (!ShouldRefreshProviderModels(provider)) return;

            MenuItem loading = AddComposerMenuItem(menu, L("Loading models…"), false, delegate { });
            loading.IsEnabled = false;

            // No provider-equality re-check here unlike the global overload: a session's provider
            // cannot change out from under an open dropdown (only a full agent switch does that, and
            // that is panel-only), so "still open" is the only staleness check needed.
            _ = RefreshProviderModelsAsync(provider).ContinueWith(
                delegate
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    if (!menu.IsOpen) return;

                    menu.Items.Clear();
                    FillChatModelMenu(menu, provider, session);
                },
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// The composer's Devin dropdown: the model in use, the starred favorites, and the entry
        /// that opens the searchable picker — the same shape the panel's model menu takes.
        /// </summary>
        private void FillChatModelPickerEntries(ContextMenu menu, AiProvider provider,
            List<Agents.ModelOption> models, string selected, Action<string> onPicked)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.IsNullOrWhiteSpace(selected))
            {
                AddChatModelMenuItem(menu, GetSelectedModelOption(provider, models, selected), selected, onPicked);
            }

            foreach (string favorite in GetFavoriteDevinModelIds())
            {
                if (string.Equals(favorite, selected, StringComparison.OrdinalIgnoreCase)) continue;

                Agents.ModelOption model = FindProviderModel(models, favorite);
                if (model == null) continue;

                AddChatModelMenuItem(menu, model, selected, onPicked);
            }

            AddComposerMenuItem(menu, L("Select Model..."), false,
                delegate
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    string picked = ShowProviderModelPickerDialog(provider, selected);
                    if (picked == null) return;

                    onPicked(picked);
                });
        }

        /// <summary>The picker-backed counterpart: the caller decides what a pick does.</summary>
        private void AddChatModelMenuItem(ItemsControl parent, Agents.ModelOption model, string selected,
            Action<string> onPicked)
        {
            string current = model.Id;

            AddComposerMenuItem(parent, model.BuildMenuCaption(),
                string.Equals(current, selected, StringComparison.OrdinalIgnoreCase),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); onPicked(current); });
        }

        private void AddChatModelMenuItem(ItemsControl parent, Agents.ModelOption model, string selected)
        {
            string current = model.Id;

            AddComposerMenuItem(parent, model.BuildMenuCaption(),
                string.Equals(current, selected, StringComparison.OrdinalIgnoreCase),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelected(current); });
        }

        /// <summary>The parallel-tab counterpart of <see cref="AddChatModelMenuItem(ItemsControl, Agents.ModelOption, string)"/>.</summary>
        private void AddChatModelMenuItem(ItemsControl parent, Agents.ModelOption model, string selected, NativeChatSessionState session)
        {
            string current = model.Id;

            AddComposerMenuItem(parent, model.BuildMenuCaption(),
                string.Equals(current, selected, StringComparison.OrdinalIgnoreCase),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelectedForSession(session, current); });
        }

        /// <summary>
        /// The effort levels as a dropdown, for the typed <c>/effort</c> command. The button opens the
        /// pill slider instead, which is anchored to that button — with the transcript back in the
        /// panel there is no button and nothing to anchor to, and a menu reads the same from either
        /// host.
        /// </summary>
        private ContextMenu BuildChatEffortMenu()
        {
            ContextMenu menu = CreateComposerMenu();

            if (IsCodexProvider(GetActiveOrSelectedProvider()))
            {
                CodexReasoningLevel codexSelected = _settings != null
                    ? _settings.SelectedCodexReasoningLevel
                    : CodexReasoningLevel.Default;

                foreach (CodexReasoningLevel level in _codexReasoningSliderOrder)
                {
                    CodexReasoningLevel current = level;
                    AddComposerMenuItem(menu, GetCodexReasoningLabel(level), level == codexSelected,
                        delegate
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            OnChatCodexReasoningSelected(current);
                        });
                }

                return menu;
            }

            EffortLevel claudeSelected =
                _settings != null ? _settings.SelectedEffortLevel : EffortLevel.High;

            foreach (EffortLevel level in _effortSliderOrder)
            {
                EffortLevel current = level;
                AddComposerMenuItem(menu, GetChatEffortLabel(level), level == claudeSelected,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatEffortSelected(current); });
            }

            return menu;
        }

        private ContextMenu BuildChatPermissionMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            bool? skipping = GetChatPermissionSkipFlag(active);
            if (!skipping.HasValue)
            {
                return menu;
            }

            // Plan mode is a Claude flag: the agent researches and writes a plan, then asks for approval
            // before touching anything. The other agents have no equivalent launch mode.
            if (IsClaudeProvider(active))
            {
                // One resolved state rather than a condition per entry: exactly one of the five is
                // checked, even if the stored flags somehow contradict each other, and the checkmark
                // agrees with both the caption and the launch.
                ClaudePermissionChoice choice = GetNativeClaudePermissionChoice(skipping.Value);

                AddComposerMenuItem(menu, L("Plan mode"), choice == ClaudePermissionChoice.PlanMode,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPlanModeSelected(true); });
                AddComposerMenuItem(menu, L("Skip permissions"), choice == ClaudePermissionChoice.SkipPermissions,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(true); });
                AddComposerMenuItem(menu, L("Auto mode"), choice == ClaudePermissionChoice.Auto,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatAutoPermissionsSelected(); });
                AddComposerMenuItem(menu, L("Accept edits"), choice == ClaudePermissionChoice.AcceptEdits,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatAcceptEditsSelected(); });
                AddComposerMenuItem(menu, L("Manual mode"), choice == ClaudePermissionChoice.ManualMode,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatManualModeSelected(); });

                return menu;
            }

            AddComposerMenuItem(menu, L("Ask permission"), !skipping.Value,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(false); });
            AddComposerMenuItem(menu, L("Skip permissions"), skipping.Value,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(true); });

            return menu;
        }

        /// <summary>The parallel-tab counterpart of <see cref="BuildChatPermissionMenu()"/>, read from that tab's own permission/plan-mode snapshot.</summary>
        private ContextMenu BuildChatPermissionMenu(NativeChatSessionState session)
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider provider = session.SelectedProvider;

            // Only used here to ask "does this extension drive permissions for this provider at all" —
            // the actual flag comes from the session, not the global settings this getter also reads.
            if (GetChatPermissionSkipFlag(provider) == null)
            {
                return menu;
            }

            bool skipping = session.SkipPermissions;

            if (IsClaudeProvider(provider))
            {
                ClaudePermissionChoice choice = GetNativeClaudePermissionChoice(session);

                AddComposerMenuItem(menu, L("Plan mode"), choice == ClaudePermissionChoice.PlanMode,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPlanModeSelectedForSession(session, true); });
                AddComposerMenuItem(menu, L("Skip permissions"), choice == ClaudePermissionChoice.SkipPermissions,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelectedForSession(session, true); });
                AddComposerMenuItem(menu, L("Auto mode"), choice == ClaudePermissionChoice.Auto,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatAutoPermissionsSelectedForSession(session); });
                AddComposerMenuItem(menu, L("Accept edits"), choice == ClaudePermissionChoice.AcceptEdits,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatAcceptEditsSelectedForSession(session); });
                AddComposerMenuItem(menu, L("Manual mode"), choice == ClaudePermissionChoice.ManualMode,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatManualModeSelectedForSession(session); });

                return menu;
            }

            AddComposerMenuItem(menu, L("Ask permission"), !skipping,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelectedForSession(session, false); });
            AddComposerMenuItem(menu, L("Skip permissions"), skipping,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelectedForSession(session, true); });

            return menu;
        }

        #endregion

        #region Typed Selector Commands

        /// <summary>
        /// Handles the commands a user can type instead of clicking: <c>/plan</c> turns plan mode on,
        /// <c>/model</c> and <c>/effort</c> open the pickers the composer buttons open,
        /// <c>/btw</c> asks a side question, and Devin's <c>/usage</c> shows the account's ACU usage.
        /// <para>
        /// They are the extension's own commands, not the CLI's: native mode runs the agent headless,
        /// where these settings are launch flags rather than anything the agent can be told mid-turn.
        /// Sending them on as a prompt is what happened before, and the agent simply answered as if
        /// asked about the word.
        /// </para>
        /// </summary>
        /// <returns>True when the text was a command and was handled, so nothing is sent.</returns>
        private bool TryHandleNativeSelectorCommand(string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return false;
            }

            AiProvider? provider = GetActiveOrSelectedProvider();
            bool isClaude = IsClaudeProvider(provider);
            bool isCodex = IsCodexProvider(provider);
            bool isDevin = provider == AiProvider.Devin || provider == AiProvider.DevinNative;

            // Devin's /usage exists only in its TUI; the headless CLI answers "Unknown command", so the
            // extension reads the account's usage itself (see ShowDevinUsageInChatAsync).
            if (isDevin && string.Equals(prompt.Trim(), "/usage", StringComparison.OrdinalIgnoreCase))
            {
                AddNativeMessage(ChatMessageKind.User, "/usage");
                _ = ShowDevinUsageInChatAsync(provider.Value, openWebPageOnFailure: false);
                return true;
            }

            // A typed /clear does what the Clear Chat button does. Sent on as text, the CLI would answer
            // "Done" and wipe nothing the user can see: native mode shows its own transcript.
            if (IsBareClearCommand(prompt))
            {
                OnComposerClearChatRequested(ChatTranscript, EventArgs.Empty);
                return true;
            }

            if (!isClaude && !isCodex)
            {
                return false;
            }

            string trimmed = prompt.Trim();

            // "/btw <question>" is the one that takes an argument, so it is matched by prefix. The bare
            // "/btw" falls through to the usage line rather than asking an empty question.
            if (trimmed.StartsWith("/btw", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 4 || char.IsWhiteSpace(trimmed[4])))
            {
                if (!isClaude)
                {
                    return false;
                }

                string question = trimmed.Substring(4).Trim();
                if (question.Length == 0)
                {
                    AddNativeMessage(ChatMessageKind.Notice, L("Usage: /btw <your question>"));
                    return true;
                }

                AddNativeMessage(ChatMessageKind.User, trimmed);
                AddNativeMessage(ChatMessageKind.Notice,
                    L("💬 Side question — asked in a forked copy of this conversation, so the work in progress is untouched."));

                _ = AskSideQuestionAsync(question);
                return true;
            }

            // Only the bare command. "/plan the migration" is a prompt about planning, and taking it
            // as a command would silently swallow it.
            switch (trimmed.ToLowerInvariant())
            {
                case "/plan":
                    if (!isClaude)
                    {
                        return false;
                    }

                    if (GetNativeClaudePermissionChoice(_settings?.ClaudeDangerouslySkipPermissions == true) == ClaudePermissionChoice.PlanMode)
                    {
                        AddNativeMessage(ChatMessageKind.Notice, L("🤖 Already in plan mode."));
                    }
                    else
                    {
                        OnChatPlanModeSelected(true);
                    }
                    return true;

                case "/model":
                    ShowChatSelectorMenu(BuildChatModelMenu(), ChatSelector.Model);
                    return true;

                case "/effort":
                    ShowChatSelectorMenu(BuildChatEffortMenu(), ChatSelector.Effort);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Only the bare command: "/clear the logs" is a prompt, not a request to wipe the chat.</summary>
        private static bool IsBareClearCommand(string prompt)
        {
            return string.Equals((prompt ?? string.Empty).Trim(), "/clear", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Opens a composer menu for a typed command. It hangs off the matching composer button when the
        /// chat is in its tab; with the transcript back in the panel there is no composer, so it falls
        /// back to the panel's send button — next to the prompt box the command was typed into.
        /// </summary>
        private void ShowChatSelectorMenu(ContextMenu menu, ChatSelector selector)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            UIElement anchor = ChatTranscript?.GetSelectorAnchor(selector)
                ?? (UIElement)SendPromptButton
                ?? ChatTranscript;

            if (anchor == null)
            {
                return;
            }

            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }

        #endregion

        #region Live Switching

        /// <summary>
        /// Switching agent restarts the conversation: no two agents share a transcript, so this goes
        /// through the ordinary provider-restart path rather than pretending the history carries over.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatProviderSelected(AiProvider provider)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Compared against the agent that is actually running (what the menu checks), not
                // _settings.SelectedProvider: the two drift apart whenever a switch is superseded or a
                // relaunch keeps the running provider, and the settings check then turned every click
                // on the "new" agent into a silent no-op until a solution load restarted from Settings.
                if (_settings == null ||
                    (GetActiveOrSelectedProvider() == provider && _settings.SelectedProvider == provider))
                {
                    return;
                }

                _settings.SelectedProvider = provider;
                UpdateProviderSelection();
                SaveSettings(nameof(ClaudeCodeSettings.SelectedProvider));
                ChatTranscript?.SetStatus(L("Switching to {0}...", GetChatProviderDisplayName(provider)));

                if (!await RestartTerminalWithSelectedProviderAsync())
                {
                    // The user picked another agent while this switch was still waiting for the
                    // previous one to finish starting — Devin's handshake alone takes seconds. That
                    // later switch is what runs, and it reports its own result; saying anything here
                    // would describe an agent the chat is not about to be running.
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Refresh again now that the new session is actually running: the call above ran
                // before the restart, so it still read the outgoing provider off
                // _currentRunningProvider and left the tool window caption on the old name.
                UpdateProviderSelection();
                UpdateChatComposerState();
                UpdateChatTabCaption();

                // RestartTerminalWithSelectedProviderAsync swallows its own failures (falls back to the
                // embedded terminal, or leaves native mode off) without telling this caller — so without
                // this check, a switch that silently failed still claimed success below, which is exactly
                // what "I change agent but nothing happens" looked like: no error, no visible change, and
                // (falsely) no indication anything had gone wrong either.
                if (GetActiveOrSelectedProvider() != provider || ChatTranscript == null || !_chatIsInTab)
                {
                    AddNativeMessage(ChatMessageKind.Error,
                        L("Could not switch to {0} — check that it is installed " +
                          "and reachable, then try again. The embedded terminal may have been used instead.",
                          GetChatProviderDisplayName(provider)));
                    return;
                }

                AddNativeMessage(ChatMessageKind.Notice,
                    L("🤖 Switched to {0} — this starts a new conversation.", GetChatProviderDisplayName(provider)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat provider switch failed: {ex.Message}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessage(ChatMessageKind.Error, L("Could not switch agent: {0}", ex.Message));
            }
        }

        /// <summary>
        /// The parallel-tab counterpart of <see cref="OnChatProviderSelected"/> (issue #171): relaunches
        /// this tab's own session with a different <see cref="IAgentSession"/> instead of restarting the
        /// panel's embedded terminal, and mutates <see cref="NativeChatSessionState.SelectedProvider"/>
        /// instead of <c>Settings</c> — a per-tab pick is never persisted, so the next VS launch starts
        /// every tab from whatever Settings itself holds. Always forces a new conversation: a resume id
        /// from one CLI's session format is meaningless to another agent's launch.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatProviderSelectedForSession(NativeChatSessionState session, AiProvider provider)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || session.SelectedProvider == provider)
                {
                    return;
                }

                ApplyProviderToSession(session, provider);
                UpdateChatComposerState(session);

                await RelaunchSessionAsync(session,
                    L("🤖 Switched to {0} — this starts a new conversation.", GetChatProviderDisplayName(provider)),
                    forceNewSession: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat provider switch failed for session {session?.SessionId}: {ex.Message}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessageToSession(session, ChatMessageKind.Error, L("Could not switch agent: {0}", ex.Message));
            }
        }

        /// <summary>
        /// Points a parallel tab at another provider and resets its model/permission snapshot to that
        /// provider's defaults — never written back to <c>Settings</c>. The caller relaunches.
        /// </summary>
        private void ApplyProviderToSession(NativeChatSessionState session, AiProvider provider)
        {
            session.SelectedProvider = provider;
            session.SelectedModel = GetSelectedProviderModelId(provider);
            session.SkipPermissions = GetChatPermissionSkipFlag(provider) ?? false;
            session.PlanMode = IsClaudeProvider(provider) && _settings?.ClaudePlanMode == true;
            session.AutoPermissions = IsClaudeProvider(provider) && _settings?.ClaudeAutoPermissions == true;
            session.ManualMode = IsClaudeProvider(provider) && _settings?.ClaudeManualMode == true;
            session.PlanExitChoice = null;
        }

        /// <summary>
        /// Switches the Claude model without losing the conversation: the agent is relaunched with the
        /// new model flag and the previous session id, which is the same resume mechanism the adapter
        /// already uses after an interrupt.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatClaudeModelSelected(ClaudeModel model)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.SelectedClaudeModel == model)
                {
                    return;
                }

                _settings.SelectedClaudeModel = model;
                UpdateModelSelection();
                SaveSettings(nameof(ClaudeCodeSettings.SelectedClaudeModel));
                UpdateChatComposerState();

                string modelLabel = GetChatModelLabel(GetActiveOrSelectedProvider());

                if (await TrySwitchClaudeModelAsync(MapClaudeModelArgument(model)))
                {
                    AddNativeMessage(ChatMessageKind.Notice, L("🤖 Switched to {0}.", modelLabel));
                    return;
                }

                await RelaunchNativeSessionAsync(L("🤖 Switched to {0}", modelLabel));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat model switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A model picked for any non-Claude agent. An ACP session that publishes a model picker
        /// (Devin, Open Code) takes the change over the protocol without losing the conversation;
        /// every other agent reads its model at launch, so the user is offered a restart.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatProviderModelSelected(string model)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            AiProvider? provider = GetActiveOrSelectedProvider();
            if (_settings == null || provider == null)
            {
                return;
            }

            SetSelectedProviderModelId(provider.Value, model);
            UpdateModelSelection();
            SaveSettings();
            UpdateChatComposerState();

            string label = string.IsNullOrWhiteSpace(model)
                ? L("the agent's default")
                : GetSelectedProviderModelLabel(provider);

            // Codex and Cursor relaunch a process every turn anyway, so this never needs the restart
            // prompt below — see TrySwitchOneShotModel. "Agent default" cannot be requested over the
            // ACP protocol, though, so that case still falls through to the relaunch prompt there.
            if (TrySwitchOneShotModel(_agentSession, provider.Value, model))
            {
                AddNativeMessage(ChatMessageKind.Notice, L("🤖 Model switched to {0}.", label));
                return;
            }

            if (!string.IsNullOrWhiteSpace(model) && await TrySwitchAcpModelAsync(model))
            {
                AddNativeMessage(ChatMessageKind.Notice, L("🤖 Model switched to {0}.", label));
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                L("Switch the model to {0} and restart the chat so the change takes effect now?", label),
                L("Switch {0} model", GetChatProviderDisplayName(provider)),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await RelaunchNativeSessionAsync(L("🤖 Model switched to {0}", label), forceNewSession: true);
            }
            else
            {
                AddNativeMessage(ChatMessageKind.Notice, L("🤖 Model set to {0} — it applies the next time the agent starts.", label));
            }
        }

        /// <summary>
        /// Asks a live ACP session to change its model. False when there is no such session running or
        /// the agent would not take the model, and the caller then offers the restart instead.
        /// </summary>
        private Task<bool> TrySwitchAcpModelAsync(string model) => TrySwitchAcpModelAsync(_agentSession, model);

        /// <summary>Same live switch, for a specific agent session — a parallel tab's own, not the panel's global one.</summary>
        private async Task<bool> TrySwitchAcpModelAsync(IAgentSession agentSession, string model)
        {
            var acpSession = agentSession as AcpSession;
            if (acpSession == null)
            {
                return false;
            }

            try
            {
                return await acpSession.SetModelAsync(model, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native chat: switching the model on the live session failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the model on a live Codex/Cursor session instead of relaunching. Unlike Claude's
        /// <c>set_model</c> control request or an ACP session's protocol call, this is not a round trip
        /// to the agent at all: Codex and Cursor already start a fresh process every turn
        /// (<see cref="OneShotResumeSession"/>), resumed via the session id the previous turn reported,
        /// so "switching live" is just writing the flag the next turn's process launches with — the same
        /// mechanism <see cref="OnChatCodexReasoningSelected"/> already uses for reasoning effort. Runs
        /// <paramref name="model"/> through <see cref="ResolveOneShotModel"/> first so an empty pick on
        /// Cursor still resolves to "auto" exactly as it would at launch. Returns false only when the
        /// session is not a one-shot session, so the caller falls through to the restart prompt used by
        /// every agent that genuinely reads its model at launch (PI, Antigravity, Reasonix).
        /// </summary>
        private static bool TrySwitchOneShotModel(IAgentSession agentSession, AiProvider provider, string model)
        {
            var oneShot = agentSession as OneShotResumeSession;
            if (oneShot == null)
            {
                return false;
            }

            bool isCursor = provider == AiProvider.CursorAgent || provider == AiProvider.CursorAgentNative;
            oneShot.SetModel(ResolveOneShotModel(model, isCursor));
            return true;
        }

        /// <summary>
        /// Asks a live Claude session to change its model over <c>set_model</c> instead of relaunching.
        /// False when there is no such session running or the CLI declines, and the caller then falls
        /// back to <see cref="RelaunchNativeSessionAsync"/>/<see cref="RelaunchSessionAsync"/> exactly as
        /// it always has. See <see cref="ClaudeStreamJsonSession.SetModelAsync"/> for the measured
        /// cache-hit numbers behind this.
        /// </summary>
        private Task<bool> TrySwitchClaudeModelAsync(string model) => TrySwitchClaudeModelAsync(_agentSession, model);

        /// <summary>Same live switch, for a specific agent session — a parallel tab's own, not the panel's global one.</summary>
        private async Task<bool> TrySwitchClaudeModelAsync(IAgentSession agentSession, string model)
        {
            var claudeSession = agentSession as ClaudeStreamJsonSession;
            if (claudeSession == null || string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            try
            {
                return await claudeSession.SetModelAsync(model, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native chat: switching the Claude model on the live session failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Asks a live Claude session to change its effort over <c>apply_flag_settings</c> instead of
        /// relaunching. "Auto" has no live equivalent (it is the extension's own concept of omitting
        /// <c>--effort</c> at launch) and always returns false so the caller relaunches for that one
        /// value. See <see cref="ClaudeStreamJsonSession.SetEffortAsync"/> for the measured numbers.
        /// </summary>
        private Task<bool> TrySwitchClaudeEffortAsync(string effort) => TrySwitchClaudeEffortAsync(_agentSession, effort);

        /// <summary>Same live switch, for a specific agent session — a parallel tab's own, not the panel's global one.</summary>
        private async Task<bool> TrySwitchClaudeEffortAsync(IAgentSession agentSession, string effort)
        {
            var claudeSession = agentSession as ClaudeStreamJsonSession;
            if (claudeSession == null || string.IsNullOrWhiteSpace(effort))
            {
                return false;
            }

            try
            {
                return await claudeSession.SetEffortAsync(effort, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native chat: switching the Claude effort on the live session failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Asks a live Claude session to change its permission mode over <c>set_permission_mode</c>
        /// instead of relaunching (issue #193): the relaunch killed a turn that was still running, the
        /// live switch lets it finish under the new mode. False for any other agent, no live process
        /// or a CLI that declines — the caller then relaunches as before.
        /// </summary>
        private async Task<bool> TrySwitchClaudePermissionAsync(IAgentSession agentSession, ClaudePermissionChoice choice)
        {
            var claudeSession = agentSession as ClaudeStreamJsonSession;
            if (claudeSession == null)
            {
                return false;
            }

            try
            {
                return await claudeSession.SetPermissionModeAsync(
                    ClaudeCommandBuilder.ToLivePermissionMode(choice), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native chat: switching the Claude permission mode on the live session failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Appended to a permission notice whose switch waits for the plan to end.</summary>
        private static string PlanDeferredNoticeSuffix =>
            L(" once the plan is approved or this response ends — the agent finishes planning first.");

        /// <summary>
        /// Applies a permission/plan-mode pick the handlers below have already saved: live on a Claude
        /// session, otherwise (other agents, or the live switch declined) by relaunching. Leaving plan
        /// mode while the agent is still planning waits instead — see
        /// <see cref="ClaudeCommandBuilder.ShouldWaitForPlanToEnd"/> — and is applied by the plan
        /// approval or by <see cref="ApplyDeferredNativePermissionSwitch"/> at the end of the turn.
        /// </summary>
        private async Task ApplyNativePermissionSwitchAsync(string notice)
        {
            ClaudePermissionChoice choice = GetNativeClaudePermissionChoice(_settings?.ClaudeDangerouslySkipPermissions == true);

            var claudeSession = _agentSession as ClaudeStreamJsonSession;
            if (claudeSession != null)
            {
                if (ClaudeCommandBuilder.ShouldWaitForPlanToEnd(claudeSession.PermissionMode, choice, _nativeTurnInFlight))
                {
                    claudeSession.PendingPlanExitMode = ClaudeCommandBuilder.ToLivePermissionMode(choice);
                    _deferredNativePermissionNotice = notice;
                    _deferredNativePermissionSession = claudeSession;
                    AddNativeMessage(ChatMessageKind.Notice, notice + PlanDeferredNoticeSuffix);
                    return;
                }

                claudeSession.PendingPlanExitMode = null;
            }

            _deferredNativePermissionNotice = null;
            _deferredNativePermissionSession = null;

            if (await TrySwitchClaudePermissionAsync(_agentSession, choice))
            {
                AddNativeMessage(ChatMessageKind.Notice, notice + ".");
                return;
            }

            await RelaunchNativeSessionAsync(notice);
        }

        /// <summary>The parallel-tab counterpart of <see cref="ApplyNativePermissionSwitchAsync"/>.</summary>
        private async Task ApplySessionPermissionSwitchAsync(NativeChatSessionState session, string notice)
        {
            ClaudePermissionChoice choice = GetNativeClaudePermissionChoice(session);

            var claudeSession = session.AgentSession as ClaudeStreamJsonSession;
            if (claudeSession != null)
            {
                if (ClaudeCommandBuilder.ShouldWaitForPlanToEnd(claudeSession.PermissionMode, choice, session.TurnInFlight))
                {
                    claudeSession.PendingPlanExitMode = ClaudeCommandBuilder.ToLivePermissionMode(choice);
                    session.DeferredPermissionNotice = notice;
                    session.DeferredPermissionAgent = claudeSession;
                    AddNativeMessageToSession(session, ChatMessageKind.Notice, notice + PlanDeferredNoticeSuffix);
                    return;
                }

                claudeSession.PendingPlanExitMode = null;
            }

            session.DeferredPermissionNotice = null;
            session.DeferredPermissionAgent = null;

            if (await TrySwitchClaudePermissionAsync(session.AgentSession, choice))
            {
                AddNativeMessageToSession(session, ChatMessageKind.Notice, notice + ".");
                return;
            }

            await RelaunchSessionAsync(session, notice);
        }

        /// <summary>
        /// Called when a panel turn ends: applies a permission pick that was waiting for the plan to
        /// end (issue #193) and was not already carried by a plan approval. The turn is over, so the
        /// relaunch fallback no longer cuts anything short.
        /// </summary>
        private void ApplyDeferredNativePermissionSwitch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string notice = _deferredNativePermissionNotice;
            bool sameSession = ReferenceEquals(_deferredNativePermissionSession, _agentSession);
            _deferredNativePermissionNotice = null;
            _deferredNativePermissionSession = null;

            if (notice == null || !sameSession)
            {
                return;
            }

#pragma warning disable VSSDK007 // Deliberately detached: called from the turn-completed handler, which must not wait on the switch
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ApplyNativePermissionSwitchAsync(notice);
            }).FileAndForget("claudecode/nativechat/deferredpermission");
#pragma warning restore VSSDK007
        }

        /// <summary>The parallel-tab counterpart of <see cref="ApplyDeferredNativePermissionSwitch"/>.</summary>
        private void ApplyDeferredSessionPermissionSwitch(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string notice = session.DeferredPermissionNotice;
            bool sameSession = ReferenceEquals(session.DeferredPermissionAgent, session.AgentSession);
            session.DeferredPermissionNotice = null;
            session.DeferredPermissionAgent = null;

            if (notice == null || !sameSession)
            {
                return;
            }

#pragma warning disable VSSDK007 // Deliberately detached: called from the turn-completed handler, which must not wait on the switch
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ApplySessionPermissionSwitchAsync(session, notice);
            }).FileAndForget("claudecode/nativechat/deferredpermission");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Called when a panel turn ends: "Approve and skip permissions" on a plan card skips the
        /// prompts for that plan only (issue #210) — before, the conversation stayed in Skip
        /// permissions for good. The turn that carried the plan out is over, so the override goes back
        /// to what it was before the approval and the running session follows, live where it can.
        /// A permission pick made since then replaced the override and is left alone.
        /// </summary>
        private void RestoreNativeModeAfterSkippedPlan()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_nativeSkipPlanRestorePending)
            {
                return;
            }

            _nativeSkipPlanRestorePending = false;

            if (_nativePlanExitChoice != ClaudePermissionChoice.SkipPermissions ||
                !(_agentSession is ClaudeStreamJsonSession))
            {
                return;
            }

            _nativePlanExitChoice = _nativeSkipPlanRestoreChoice;
            _nativeSkipPlanRestoreChoice = null;
            UpdateChatComposerState();

            string notice = L("🤖 Plan carried out — back to {0}", GetClaudePermissionLabel(
                GetNativeClaudePermissionChoice(_settings?.ClaudeDangerouslySkipPermissions == true)));

#pragma warning disable VSSDK007 // Deliberately detached: called from the turn-completed handler, which must not wait on the switch
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ApplyNativePermissionSwitchAsync(notice);
            }).FileAndForget("claudecode/nativechat/skipplanrestore");
#pragma warning restore VSSDK007
        }

        /// <summary>The parallel-tab counterpart of <see cref="RestoreNativeModeAfterSkippedPlan"/>.</summary>
        private void RestoreSessionModeAfterSkippedPlan(NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!session.SkipPlanRestorePending)
            {
                return;
            }

            session.SkipPlanRestorePending = false;

            if (session.PlanExitChoice != ClaudePermissionChoice.SkipPermissions ||
                !(session.AgentSession is ClaudeStreamJsonSession))
            {
                return;
            }

            session.PlanExitChoice = session.SkipPlanRestoreChoice;
            session.SkipPlanRestoreChoice = null;
            UpdateChatComposerState(session);

            string notice = L("🤖 Plan carried out — back to {0}", GetClaudePermissionLabel(GetNativeClaudePermissionChoice(session)));

#pragma warning disable VSSDK007 // Deliberately detached: called from the turn-completed handler, which must not wait on the switch
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ApplySessionPermissionSwitchAsync(session, notice);
            }).FileAndForget("claudecode/nativechat/skipplanrestore");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// A plan was approved while a permission pick was waiting for it: plain "Approve" already
        /// carried the pick to the CLI (<see cref="ClaudeStreamJsonSession.PendingPlanExitMode"/>), so
        /// only the notice remains; "Approve and skip permissions" chose its own mode and the pick is
        /// dropped. Either way nothing is left for the end of the turn.
        /// </summary>
        private void CompleteDeferredPermissionSwitchOnPlanApproval(NativeChatSessionState owner, bool approvedIntoSkip)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string notice = owner != null ? owner.DeferredPermissionNotice : _deferredNativePermissionNotice;
            IAgentSession agent = owner != null ? owner.DeferredPermissionAgent : _deferredNativePermissionSession;
            if (notice == null)
            {
                return;
            }

            if (owner != null)
            {
                owner.DeferredPermissionNotice = null;
                owner.DeferredPermissionAgent = null;
            }
            else
            {
                _deferredNativePermissionNotice = null;
                _deferredNativePermissionSession = null;
            }

            var claudeSession = agent as ClaudeStreamJsonSession;
            if (claudeSession != null)
            {
                claudeSession.PendingPlanExitMode = null;
            }

            if (!approvedIntoSkip)
            {
                if (owner != null)
                {
                    AddNativeMessageToSession(owner, ChatMessageKind.Notice, notice + ".");
                }
                else
                {
                    AddNativeMessage(ChatMessageKind.Notice, notice + ".");
                }
            }
        }

        /// <summary>
        /// The composer's effort popup closed on a stop other than the one it opened on. Same slider
        /// order as the panel, so both controls always agree on what "High" means.
        /// </summary>
        private void OnComposerEffortChanged(object sender, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            if (owner != null)
            {
                if (IsCodexProvider(owner.SelectedProvider))
                {
                    OnChatCodexReasoningSelectedForSession(owner, CodexReasoningFromSliderIndex(index));
                }
                else
                {
                    OnChatEffortSelectedForSession(owner, EffortFromSliderIndex(index));
                }
                return;
            }

            if (IsCodexProvider(GetActiveOrSelectedProvider()))
            {
                OnChatCodexReasoningSelected(CodexReasoningFromSliderIndex(index));
            }
            else
            {
                OnChatEffortSelected(EffortFromSliderIndex(index));
            }
        }

        /// <summary>
        /// Switches the effort level. Like the model, it is a launch flag (<c>--effort</c>), so the
        /// session is relaunched and resumed rather than being sent the <c>/effort</c> command.
        /// <para>
        /// The command was the original approach and had two defects: it only holds "for this session
        /// only", so the level was silently lost on every other relaunch while the composer still showed
        /// it, and writing to a live session while the CLI was shutting down after an interrupt made the
        /// expected exit look like a crash.
        /// </para>
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatEffortSelected(EffortLevel level)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.SelectedEffortLevel == level)
                {
                    return;
                }

                _settings.SelectedEffortLevel = level;
                if (!IsSessionOnlyEffort(level))
                {
                    _lastPersistableEffortLevel = level;
                }

                UpdateEffortSelection();
                SaveSettings();
                UpdateChatComposerState();

                string effortLabel = GetChatEffortLabel();

                if (await TrySwitchClaudeEffortAsync(MapEffortArgument(level)))
                {
                    AddNativeMessage(ChatMessageKind.Notice, L("🤖 Effort switched to {0}.", effortLabel));
                    return;
                }

                await RelaunchNativeSessionAsync(L("🤖 Effort switched to {0}", effortLabel));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat effort switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the Codex reasoning override used by subsequent one-shot processes. Because native
        /// Codex starts one process per turn, the same thread can continue without a relaunch.
        /// </summary>
        private void OnChatCodexReasoningSelected(CodexReasoningLevel level)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null || _settings.SelectedCodexReasoningLevel == level)
            {
                return;
            }

            _settings.SelectedCodexReasoningLevel = level;
            SaveSettings();
            UpdateChatComposerState();

            var session = _agentSession as OneShotResumeSession;
            session?.SetReasoningEffort(GetCodexReasoningArgument());

            AddNativeMessage(
                ChatMessageKind.Notice,
                L("🤖 Reasoning switched to {0}.", GetChatCodexReasoningLabel()));
        }

        /// <summary>
        /// Permission handling is a launch flag for every agent that has one, so the switch relaunches
        /// the session — resuming it where the protocol allows, so the conversation is not lost.
        /// Claude is the exception: its running process takes the new mode live, see
        /// <see cref="ApplyNativePermissionSwitchAsync"/>.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPermissionSelected(bool skip)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                AiProvider? provider = GetActiveOrSelectedProvider();
                bool? current = GetChatPermissionSkipFlag(provider);
                bool hadPlanExit = _nativePlanExitChoice.HasValue;
                if (!current.HasValue || (current.Value == skip && !hadPlanExit))
                {
                    return;
                }

                // An explicit pick replaces whatever a plan approval switched this conversation to.
                _nativePlanExitChoice = null;

                SetChatPermissionSkipFlag(provider, skip);

                // Skipping every prompt and planning before acting are opposites; picking one drops
                // the other rather than launching with a contradictory pair of flags. Same for auto and manual.
                if (skip && _settings != null)
                {
                    _settings.ClaudePlanMode = false;
                    _settings.ClaudeAutoPermissions = false;
                    _settings.ClaudeManualMode = false;
                }

                SaveSettings();
                UpdateChatComposerState();

                await ApplyNativePermissionSwitchAsync(skip
                    ? L("🤖 Switched to skipping permission prompts")
                    : L("🤖 Switched to asking for permission"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat permission switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Turns plan mode on or off on the running session, mid-turn included (issue #193), relaunching
        /// — resumed, so the conversation so far still counts as the plan's input — only when the live
        /// switch is not possible.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPlanModeSelected(bool planning)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Picking Plan mode again after an approved plan has to go back to planning even
                // though the saved choice never stopped being Plan mode (#181).
                if (_settings == null || (_settings.ClaudePlanMode == planning && !_nativePlanExitChoice.HasValue))
                {
                    return;
                }

                _nativePlanExitChoice = null;
                _settings.ClaudePlanMode = planning;

                if (planning)
                {
                    // Plan mode is the CLI asking before it acts, which "skip permissions", "auto" or
                    // "manual" would bypass entirely (manual also asks before acting, but as a launch
                    // flag it is still a distinct, mutually exclusive mode from plan mode).
                    _settings.ClaudeDangerouslySkipPermissions = false;
                    _settings.ClaudeAutoPermissions = false;
                    _settings.ClaudeManualMode = false;
                }

                SaveSettings();
                UpdateChatComposerState();

                await ApplyNativePermissionSwitchAsync(planning
                    ? L("🤖 Plan mode on — the agent will propose a plan before making changes")
                    : L("🤖 Plan mode off"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat plan-mode switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// "Accept edits" in the five-way Claude menu (Plan mode / Skip permissions / Auto mode /
        /// Accept edits / Manual mode). Not a single-flag toggle like its siblings — it is a specific
        /// target state (planning, skipping, auto and manual all off) reachable from *any* other state,
        /// so it cannot reuse <see cref="OnChatPlanModeSelected"/> or <see cref="OnChatPermissionSelected"/>:
        /// both only check the one flag they own, so each treated "already there" and no-opped when a
        /// *different* flag was the one still wrong — <c>OnChatPlanModeSelected(false)</c> no-ops coming
        /// from Skip permissions (plan mode was already off), <c>OnChatPermissionSelected(false)</c>
        /// no-ops coming from Plan mode (skip was already off there too) (#150).
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatAcceptEditsSelected()
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null)
                {
                    return;
                }

                AiProvider? provider = GetActiveOrSelectedProvider();
                bool wasSkipping = GetChatPermissionSkipFlag(provider) == true;
                bool wasPlanning = _settings.ClaudePlanMode;
                bool wasAuto = _settings.ClaudeAutoPermissions;
                bool wasManual = _settings.ClaudeManualMode;
                bool hadPlanExit = _nativePlanExitChoice.HasValue;
                if (!wasSkipping && !wasPlanning && !wasAuto && !wasManual && !hadPlanExit)
                {
                    return;
                }

                _nativePlanExitChoice = null;

                _settings.ClaudePlanMode = false;
                _settings.ClaudeAutoPermissions = false;
                _settings.ClaudeManualMode = false;
                if (wasSkipping)
                {
                    SetChatPermissionSkipFlag(provider, false);
                }

                SaveSettings();
                UpdateChatComposerState();

                await ApplyNativePermissionSwitchAsync(L("🤖 Switched to accepting edits"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat permission switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// "Auto mode" in the five-way Claude menu (Plan mode / Skip permissions / Auto mode / Accept
        /// edits / Manual mode): hands the per-tool prompt/allow decision to the CLI's own
        /// <c>--permission-mode auto</c> instead of the extension's "Accept edits" (acceptEdits)
        /// default. Turns the other three off, mirroring <see cref="OnChatPlanModeSelected"/>.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatAutoPermissionsSelected()
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null)
                {
                    return;
                }

                AiProvider? provider = GetActiveOrSelectedProvider();
                bool wasSkipping = GetChatPermissionSkipFlag(provider) == true;

                // Target state, not a toggle — same reasoning as OnChatAcceptEditsSelected. Bailing
                // out on the auto flag alone no-opped while a stray plan-mode or skip flag was still
                // the one being launched, leaving the user unable to reach auto from the menu (#150).
                if (_settings.ClaudeAutoPermissions && !_settings.ClaudePlanMode && !wasSkipping && !_settings.ClaudeManualMode &&
                    !_nativePlanExitChoice.HasValue)
                {
                    return;
                }

                _nativePlanExitChoice = null;

                _settings.ClaudeAutoPermissions = true;
                _settings.ClaudePlanMode = false;
                _settings.ClaudeManualMode = false;
                if (wasSkipping)
                {
                    SetChatPermissionSkipFlag(provider, false);
                }

                SaveSettings();
                UpdateChatComposerState();

                await ApplyNativePermissionSwitchAsync(L("🤖 Switched to auto permissions"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat auto-permission switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// "Manual mode" in the five-way Claude menu (Plan mode / Skip permissions / Auto mode / Accept
        /// edits / Manual mode): launches with the CLI's own <c>--permission-mode manual</c>, asking
        /// before every tool call with no standing allow for any tool — not even file edits, which is
        /// what tells it apart from "Accept edits". Turns the other three off, mirroring
        /// <see cref="OnChatAutoPermissionsSelected"/>.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatManualModeSelected()
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null)
                {
                    return;
                }

                AiProvider? provider = GetActiveOrSelectedProvider();
                bool wasSkipping = GetChatPermissionSkipFlag(provider) == true;

                // Target state, not a toggle — same reasoning as OnChatAutoPermissionsSelected.
                if (_settings.ClaudeManualMode && !_settings.ClaudePlanMode && !wasSkipping && !_settings.ClaudeAutoPermissions &&
                    !_nativePlanExitChoice.HasValue)
                {
                    return;
                }

                _nativePlanExitChoice = null;

                _settings.ClaudeManualMode = true;
                _settings.ClaudePlanMode = false;
                _settings.ClaudeAutoPermissions = false;
                if (wasSkipping)
                {
                    SetChatPermissionSkipFlag(provider, false);
                }

                SaveSettings();
                UpdateChatComposerState();

                await ApplyNativePermissionSwitchAsync(L("🤖 Switched to manual permissions"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat manual-permission switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restarts the agent process with the current settings and drops an inline notice into the
        /// transcript, which is deliberately not cleared: the conversation reads as one continuous
        /// thread with a divider where the switch happened.
        /// <para>
        /// Claude and Codex are relaunched with their resume forms, so the agent keeps the history
        /// too. Adapters without resume support say that the conversation starts over.
        /// </para>
        /// </summary>
        /// <param name="appendAccountLabel">
        /// When true, the signed-in account (<see cref="GetSignedInClaudeAccountLabel"/>) is appended to
        /// <paramref name="notice"/> once the relaunched process is up — read at that point, not before,
        /// so an account switch has the best chance of the CLI's state file already reflecting it.
        /// </param>
        private async Task RelaunchNativeSessionAsync(string notice, bool forceNewSession = false, bool appendAccountLabel = false, Func<Task> midRelaunchAsync = null)
        {
            if (_nativeSwitchInProgress)
            {
                return;
            }

            if (_agentSession == null)
            {
                // Native mode is not running (the panel is on the terminal) — nothing to relaunch.
                return;
            }

            _nativeSwitchInProgress = true;

            // Issue #151 round 11: set when the relaunch died because the agent will not run on the
            // selected model. The rollback to the terminal cannot happen inside the try — it starts a
            // new session and so takes _nativeLifecycleSemaphore, which this method is still holding.
            bool rollbackToTerminal = false;

            // Same lock the agent switches take: a relaunch replaces _agentSession too, so running one
            // while a switch is starting another agent leaves whichever finishes second pointing at a
            // session the other one has already disposed.
            await _nativeLifecycleSemaphore.WaitAsync();

            try
            {
                IAgentSession previous = _agentSession;
                if (previous == null)
                {
                    // An agent switch ended the session while this relaunch waited for the lock; the
                    // switch has already started what the user asked for last.
                    return;
                }

                AiProvider provider = _currentRunningProvider ?? _settings.SelectedProvider;
                // Not SessionId: after a relaunch that never ran a turn, that is still the throwaway
                // id the adapter *asked* for and the CLI never created a transcript for. Resuming it
                // fails the launch, which is what made two consecutive dropdown changes kill the agent.
                string resumeId = previous.ResumableSessionId;
                bool providerCanResume = IsClaudeProvider(provider) ||
                    provider == AiProvider.Codex || provider == AiProvider.CodexNative;
                bool canResume = !forceNewSession && providerCanResume && !string.IsNullOrEmpty(resumeId);

                string workspace = await GetWorkspaceDirectoryAsync();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Without a resume the relaunch starts another conversation: the old topic no longer applies.
                if (!canResume)
                {
                    ResetChatTabTopic(null);
                }
                else
                {
                    SettleChatTabStatusAfterRelaunch(null);
                }

                ChatTranscript.SetBusy(false);
                ChatTranscript.SetQueuedMessageCount(0);
                ChatTranscript.SetStatus(L("Restarting the agent..."));

                // Await it: the replacement session below must not start while the old process is
                // still alive, but the wait no longer blocks the UI thread.
                await DisposeNativeSessionAsync();

                // Lets a caller run something that needs the agent process gone first (e.g. a CLI
                // self-update, whose installer overwrites the executable and fails while it is still
                // running) before the replacement session is created below.
                if (midRelaunchAsync != null)
                {
                    await midRelaunchAsync();
                }

                if (canResume)
                {
                    // CreateClaudeSession consumes this exactly like a resume request coming from the
                    // Session History window.
                    Interlocked.Exchange(ref _pendingResumeSessionId, resumeId);
                }

                IAgentSession session = CreateAgentSession(provider, workspace);
                if (session == null)
                {
                    AddNativeMessage(ChatMessageKind.Error, L("The agent could not be restarted."));
                    ChatTranscript.SetStatus(string.Empty);
                    return;
                }

                _nativeSessionCts = new CancellationTokenSource();
                _agentSession = session;
                session.Received += OnAgentEventReceived;

                // Migrate the title/color to the new agent's id *now*, not only once the CLI confirms
                // one later — UpdateChatTabCaption below runs before any turn has, so without this the
                // tab visibly loses its title/color the instant the switch completes, only to (maybe)
                // get it back on the next message. Keyed on !forceNewSession rather than canResume: a
                // model/effort/permission switch is not the user asking for a fresh start even on a
                // provider whose CLI has no --resume, so the tab's title should survive it regardless —
                // MigrateSessionTitleAndColor itself is the no-op guard when there is nothing to carry
                // over (empty/unknown old id, or a provider that never named this conversation at all).
                // session.SessionId is still just the adapter's throwaway seed at this point, but the
                // CLI can still swap it for a different confirmed one on the first turn, so the field is
                // left set below for that later migration too.
                if (!forceNewSession)
                {
                    MigrateSessionTitleAndColor(previous.SessionId, session.SessionId);
                }
                _nativeSessionMigrationSourceId = session.SessionId;

                _pendingToolCalls.Clear();
                _streamingAssistantMessage = null;
                _streamingThinkingMessage = null;
                _nativeTurnFinishConfig = null;
                _nativeTurnInFlight = false;
                _codexNativePromptQueue.Clear();
                _cancelledCodexNativePromptCount = 0;

                await session.StartAsync(workspace, _nativeSessionCts.Token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _currentRunningProvider = provider;
                ChatTranscript.SetStatus(L("Ready."));

                if (appendAccountLabel)
                {
                    bool isWsl = provider == AiProvider.ClaudeCodeWSL;
                    string account = await GetClaudeAuthStatusEmailAsync(isWsl, _nativeSessionCts.Token)
                        ?? await GetSignedInClaudeAccountLabelAsync(isWsl);

                    // Empty is not cached (see BeginAccountLabelLookup) — a login the user just finished
                    // in a separate console window can still be a moment away from being reflected on
                    // disk, and caching that miss would silence every later welcome card until the next
                    // Change Account click.
                    if (isWsl && !string.IsNullOrEmpty(account))
                    {
                        _wslAccountLabels[provider] = account;
                    }

                    if (!string.IsNullOrEmpty(account))
                    {
                        notice += L(" — now signed in as {0}", account);
                    }
                }

                // No caveat when the provider can resume but there is simply nothing to resume yet
                // (no turn has run), because in that case no history is being lost.
                AddNativeMessage(ChatMessageKind.Notice, canResume || forceNewSession || providerCanResume
                    ? notice
                    : notice + L(" — this agent cannot resume, so the conversation starts over."));

                UpdateChatComposerState();
                UpdateChatTabCaption();

                // Only for "New chat". A model or permission switch keeps the conversation, and opening
                // a fresh-start card in the middle of one would read as if it had been thrown away.
                if (forceNewSession)
                {
                    ShowChatWelcome(workspace);
                }
            }
            catch (AgentModelUnavailableException ex)
            {
                // Issue #151 round 11: a model switch is the most likely way to get here — the picked
                // model is in the catalog but the agent does not list it. Leaving the chat sitting on a
                // dead session would be the worst outcome: the panel's 🤖 menu is hidden in native mode,
                // so the composer would be the only route back and it is attached to the session that
                // just died. Fall back to the terminal below instead, where the model can be changed.
                Debug.WriteLine($"Native mode: relaunch model unavailable: {ex}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ChatTranscript.SetStatus(string.Empty);
                rollbackToTerminal = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: relaunch failed: {ex}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessage(ChatMessageKind.Error, L("The agent could not be restarted: {0}", ex.Message));
                ChatTranscript.SetStatus(string.Empty);
            }
            finally
            {
                _nativeLifecycleSemaphore.Release();
                _nativeSwitchInProgress = false;
            }

            if (rollbackToTerminal)
            {
                // Runs the normal start path, which tries native mode once more, hits the same model
                // and takes its own AgentModelUnavailableException branch — that is what tears the
                // half-dead session down, explains why in the fallback notice, and launches the
                // embedded terminal. Both locks this method held are released by now.
                await RestartTerminalWithSelectedProviderAsync();
            }
        }

        /// <summary>
        /// The parallel-tab counterpart of <see cref="RelaunchNativeSessionAsync"/> (v163.0): restarts
        /// one session's own process with its own model/effort/permission selectors instead of the
        /// global ones, so switching the model in this tab never touches the panel's session or any
        /// other open tab. A faithful port of the original, scoped to <paramref name="session"/> — see
        /// the "ForSession helpers" note on <c>NativeChatSessionState.PendingEvents</c>.
        /// <para>
        /// Guarded by <see cref="NativeChatSessionState.RelaunchLock"/>, not the panel's
        /// <c>_nativeLifecycleSemaphore</c>: that lock also serializes a full agent switch, which never
        /// touches a parallel session, so sharing it would make an unrelated tab's relaunch wait on
        /// this one for nothing. <c>appendAccountLabel</c> is intentionally not offered here — account
        /// switching (Change Account) is panel-only.
        /// </para>
        /// <para>
        /// <paramref name="resumeSessionId"/> is a conversation the user picked in Session History: the
        /// tab switches to it (its own transcript replaced by the resumed one) instead of carrying on
        /// with its current conversation.
        /// </para>
        /// </summary>
        private async Task RelaunchSessionAsync(NativeChatSessionState session, string notice,
            bool forceNewSession = false, Func<Task> midRelaunchAsync = null, string resumeSessionId = null)
        {
            if (session?.AgentSession == null)
            {
                return;
            }

            await session.RelaunchLock.WaitAsync();

            try
            {
                IAgentSession previous = session.AgentSession;
                AiProvider provider = session.SelectedProvider;
                bool isHistoryResume = !string.IsNullOrEmpty(resumeSessionId);

                // Not SessionId — see the identical note on RelaunchNativeSessionAsync: a relaunch that
                // never ran a turn only ever asked for this id, and resuming it fails the launch.
                string resumeId = isHistoryResume ? resumeSessionId : previous.ResumableSessionId;
                bool providerCanResume = isHistoryResume || IsClaudeProvider(provider) ||
                    provider == AiProvider.Codex || provider == AiProvider.CodexNative;
                bool canResume = !forceNewSession && providerCanResume && !string.IsNullOrEmpty(resumeId);

                // A GitHub issue tab keeps running in its own worktree across relaunches.
                string workspace = !string.IsNullOrEmpty(session.WorkspaceOverride)
                    ? session.WorkspaceOverride
                    : await GetWorkspaceDirectoryAsync();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // A new or another conversation (no resume, or one picked from history): the old topic no longer applies.
                if (!canResume || isHistoryResume)
                {
                    ResetChatTabTopic(session);
                }
                else
                {
                    SettleChatTabStatusAfterRelaunch(session);
                }

                ChatTranscriptView transcript = session.ChatTranscript;
                transcript.SetBusy(false);
                transcript.SetQueuedMessageCount(0);
                transcript.SetStatus(L("Restarting the agent..."));

                DisposeSessionAgent(session);

                if (midRelaunchAsync != null)
                {
                    await midRelaunchAsync();
                }

                IAgentSession newAgentSession = CreateAgentSession(
                    provider, workspace, session, canResume ? resumeId : null);
                if (newAgentSession == null)
                {
                    AddNativeMessageToSession(session, ChatMessageKind.Error, L("The agent could not be restarted."));
                    transcript.SetStatus(string.Empty);
                    return;
                }

                session.SessionCts = new CancellationTokenSource();
                session.AgentSession = newAgentSession;
                AttachSessionEventHandler(session);

                // Migrate the title/color to the new agent's id *now*, not only once the CLI confirms
                // one later — UpdateSessionTabCaption below runs before any turn has, so without this
                // the tab visibly loses its title/color the instant the switch completes. Keyed on
                // !forceNewSession rather than canResume — see the identical note on
                // RelaunchNativeSessionAsync. newAgentSession.SessionId is still just the adapter's
                // throwaway seed here, but the CLI can still swap it for a different confirmed one on
                // the first turn, so the field is left set below for that later migration too. A
                // conversation resumed from Session History is a different one with its own title.
                if (!forceNewSession && !isHistoryResume)
                {
                    MigrateSessionTitleAndColor(previous.SessionId, newAgentSession.SessionId);
                }
                session.MigrationSourceSessionId = newAgentSession.SessionId;

                session.PendingToolCalls.Clear();
                session.StreamingAssistantMessage = null;
                session.StreamingThinkingMessage = null;
                session.TurnFinishConfig = null;
                session.TurnInFlight = false;

                // Cleared before StartAsync: an ACP agent resumed via session/load replays the
                // conversation as live events during the start.
                if (isHistoryResume)
                {
                    transcript.Clear();
                }

                await newAgentSession.StartAsync(workspace, session.SessionCts.Token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                transcript.SetStatus(L("Ready."));

                // Same replay the default session does on a Session History resume (see
                // StartNativeModeAsync): the CLI restores the agent's memory but none of the messages.
                bool replayed = isHistoryResume &&
                    ((newAgentSession as AcpSession)?.WasResumedFromLoad == true ||
                     await TryReplayResumedTranscriptAsync(provider, workspace, resumeSessionId, session));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!replayed)
                {
                    AddNativeMessageToSession(session, ChatMessageKind.Notice, canResume || forceNewSession || providerCanResume
                        ? notice
                        : notice + L(" — this agent cannot resume, so the conversation starts over."));
                }

                UpdateChatComposerState(session);
                UpdateSessionTabCaption(session);

                // Only for "New chat" — a model or permission switch keeps the conversation, and
                // opening a fresh-start card in the middle of one would read as if it had been thrown
                // away. The caller (OnComposerClearChatRequested) already cleared the transcript.
                if (forceNewSession)
                {
                    ShowChatWelcome(transcript, workspace);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: relaunch failed for session {session.SessionId}: {ex}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessageToSession(session, ChatMessageKind.Error, L("The agent could not be restarted: {0}", ex.Message));
                session.ChatTranscript?.SetStatus(string.Empty);
            }
            finally
            {
                session.RelaunchLock.Release();
            }
        }

        #region Per-Session Selector Handlers (v163.0)

        /// <summary>
        /// The parallel-tab counterpart of <see cref="OnChatClaudeModelSelected"/>: relaunches this
        /// tab's own session instead of the panel's, and mutates <see cref="NativeChatSessionState.SelectedClaudeModel"/>
        /// instead of <c>Settings</c> — a per-tab pick is never persisted, so the next VS launch starts
        /// every tab from whatever Settings itself holds.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatClaudeModelSelectedForSession(NativeChatSessionState session, ClaudeModel model)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || session.SelectedClaudeModel == model)
                {
                    return;
                }

                session.SelectedClaudeModel = model;
                UpdateChatComposerState(session);

                string modelLabel = GetChatModelLabel(session);

                if (await TrySwitchClaudeModelAsync(session.AgentSession, MapClaudeModelArgument(model)))
                {
                    AddNativeMessageToSession(session, ChatMessageKind.Notice, L("🤖 Switched to {0}.", modelLabel));
                    return;
                }

                await RelaunchSessionAsync(session, L("🤖 Switched to {0}", modelLabel));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat model switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatProviderModelSelected"/>.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatProviderModelSelectedForSession(NativeChatSessionState session, string model)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (session == null)
            {
                return;
            }

            session.SelectedModel = model ?? string.Empty;
            UpdateChatComposerState(session);

            string label = string.IsNullOrWhiteSpace(model)
                ? L("the agent's default")
                : GetSelectedProviderModelLabel(session.SelectedProvider, model);

            if (TrySwitchOneShotModel(session.AgentSession, session.SelectedProvider, model))
            {
                AddNativeMessageToSession(session, ChatMessageKind.Notice, L("🤖 Model switched to {0}.", label));
                return;
            }

            if (!string.IsNullOrWhiteSpace(model) && await TrySwitchAcpModelAsync(session.AgentSession, model))
            {
                AddNativeMessageToSession(session, ChatMessageKind.Notice, L("🤖 Model switched to {0}.", label));
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                L("Switch the model to {0} and restart this chat so the change takes effect now?", label),
                L("Switch {0} model", GetChatProviderDisplayName(session.SelectedProvider)),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await RelaunchSessionAsync(session, L("🤖 Model switched to {0}", label), forceNewSession: true);
            }
            else
            {
                AddNativeMessageToSession(session, ChatMessageKind.Notice,
                    L("🤖 Model set to {0} — it applies the next time this chat's agent starts.", label));
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatEffortSelected"/>. Max/Ultracode need no "session-only" tracking here — a tab's effort is never persisted at all.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatEffortSelectedForSession(NativeChatSessionState session, EffortLevel level)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || session.SelectedEffortLevel == level)
                {
                    return;
                }

                session.SelectedEffortLevel = level;
                UpdateChatComposerState(session);

                string effortLabel = GetChatEffortLabel(level);

                if (await TrySwitchClaudeEffortAsync(session.AgentSession, MapEffortArgument(level)))
                {
                    AddNativeMessageToSession(session, ChatMessageKind.Notice, L("🤖 Effort switched to {0}.", effortLabel));
                    return;
                }

                await RelaunchSessionAsync(session, L("🤖 Effort switched to {0}", effortLabel));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat effort switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatCodexReasoningSelected"/>: Codex stays alive across turns, so this needs no relaunch either.</summary>
        private void OnChatCodexReasoningSelectedForSession(NativeChatSessionState session, CodexReasoningLevel level)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session == null || session.SelectedCodexReasoningLevel == level)
            {
                return;
            }

            session.SelectedCodexReasoningLevel = level;
            UpdateChatComposerState(session);

            var oneShot = session.AgentSession as OneShotResumeSession;
            oneShot?.SetReasoningEffort(MapCodexReasoningArgument(level));

            AddNativeMessageToSession(session, ChatMessageKind.Notice,
                L("🤖 Reasoning switched to {0}.", GetCodexReasoningLabel(level)));
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatPermissionSelected"/>.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPermissionSelectedForSession(NativeChatSessionState session, bool skip)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || (session.SkipPermissions == skip && !session.PlanExitChoice.HasValue))
                {
                    return;
                }

                session.PlanExitChoice = null;
                session.SkipPermissions = skip;

                // Plan mode, auto, manual and skipping every prompt are mutually exclusive; picking one
                // drops the others rather than launching with a contradictory pair of flags.
                if (skip)
                {
                    session.PlanMode = false;
                    session.AutoPermissions = false;
                    session.ManualMode = false;
                }

                UpdateChatComposerState(session);

                await ApplySessionPermissionSwitchAsync(session, skip
                    ? L("🤖 Switched to skipping permission prompts")
                    : L("🤖 Switched to asking for permission"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat permission switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatPlanModeSelected"/>.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPlanModeSelectedForSession(NativeChatSessionState session, bool planning)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || (session.PlanMode == planning && !session.PlanExitChoice.HasValue))
                {
                    return;
                }

                session.PlanExitChoice = null;
                session.PlanMode = planning;

                if (planning)
                {
                    session.SkipPermissions = false;
                    session.AutoPermissions = false;
                    session.ManualMode = false;
                }

                UpdateChatComposerState(session);

                await ApplySessionPermissionSwitchAsync(session, planning
                    ? L("🤖 Plan mode on — the agent will propose a plan before making changes")
                    : L("🤖 Plan mode off"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat plan-mode switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatAcceptEditsSelected"/> — see its doc comment for why this needs its own handler instead of reusing the plan-mode/skip-permissions/auto/manual toggles.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatAcceptEditsSelectedForSession(NativeChatSessionState session)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || (!session.PlanMode && !session.SkipPermissions && !session.AutoPermissions && !session.ManualMode &&
                    !session.PlanExitChoice.HasValue))
                {
                    return;
                }

                session.PlanExitChoice = null;
                session.PlanMode = false;
                session.AutoPermissions = false;
                session.ManualMode = false;
                session.SkipPermissions = false;
                UpdateChatComposerState(session);

                await ApplySessionPermissionSwitchAsync(session, L("🤖 Switched to accepting edits"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat permission switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatAutoPermissionsSelected"/>.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatAutoPermissionsSelectedForSession(NativeChatSessionState session)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || (session.AutoPermissions && !session.PlanMode && !session.SkipPermissions && !session.ManualMode &&
                    !session.PlanExitChoice.HasValue))
                {
                    return;
                }

                session.PlanExitChoice = null;

                session.AutoPermissions = true;
                session.PlanMode = false;
                session.ManualMode = false;
                session.SkipPermissions = false;
                UpdateChatComposerState(session);

                await ApplySessionPermissionSwitchAsync(session, L("🤖 Switched to auto permissions"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat auto-permission switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        /// <summary>The parallel-tab counterpart of <see cref="OnChatManualModeSelected"/>.</summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatManualModeSelectedForSession(NativeChatSessionState session)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (session == null || (session.ManualMode && !session.PlanMode && !session.SkipPermissions && !session.AutoPermissions &&
                    !session.PlanExitChoice.HasValue))
                {
                    return;
                }

                session.PlanExitChoice = null;

                session.ManualMode = true;
                session.PlanMode = false;
                session.AutoPermissions = false;
                session.SkipPermissions = false;
                UpdateChatComposerState(session);

                await ApplySessionPermissionSwitchAsync(session, L("🤖 Switched to manual permissions"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat manual-permission switch failed for session {session?.SessionId}: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Handles "Update Agent" in native mode: there is no console to type the CLI's self-update
        /// command into, so the update runs in its own visible console window instead. The running
        /// agent process is torn down first (its update installer overwrites the same executable, which
        /// fails while that process still holds it open — measured with Devin native), then the method
        /// waits for the user to close the update window before relaunching and resuming the conversation,
        /// so the user has a chance to read the updater's output before the chat comes back.
        /// </summary>
        private async Task UpdateNativeAgentAsync()
        {
            if (!IsNativeModeActive)
            {
                MessageBox.Show(L("The agent is not running."), L("Update Agent"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AiProvider provider = _currentRunningProvider ?? _settings.SelectedProvider;
            string updateCommand = GetNativeAgentUpdateCommand(provider);
            if (string.IsNullOrEmpty(updateCommand))
            {
                MessageBox.Show(L("No update command is known for this agent."), L("Update Agent"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RelaunchNativeSessionAsync(L("🔄️ Agent updated — resuming"), midRelaunchAsync: async () =>
            {
                try
                {
                    using (var updateProcess = StartUpdateWindow(provider, updateCommand))
                    {
                        if (updateProcess == null)
                        {
                            return;
                        }

                        // No timeout: this window stays up until the user reads the updater's output and
                        // closes it themselves, which is the signal to bring the agent back.
                        while (!updateProcess.HasExited)
                        {
                            await Task.Delay(300);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UpdateNativeAgentAsync: updater failed: {ex.Message}");
                    MessageBox.Show(L("Failed to run the updater: {0}", ex.Message), L("Update Agent"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        /// <summary>
        /// Opens the console window that runs the CLI's self-update command. <c>cmd.exe /k</c> hosts every
        /// provider except <see cref="AiProvider.DevinNative"/> — its updater is what Windows Defender was
        /// flagging (see <see cref="GetNativeAgentUpdateCommand"/>), and the fix for that is specific to
        /// it: hosting the command in <c>powershell.exe</c> directly rather than nesting a
        /// <c>powershell -NoProfile -ExecutionPolicy Bypass -Command "..."</c> call inside <c>cmd.exe</c>,
        /// which is exactly the download-and-execute shape the heuristic caught. Running every other
        /// provider through <c>powershell.exe</c> too was tried and broke them: their commands are written
        /// for <c>cmd.exe</c>'s own parser (the WSL providers' <c>wsl bash -lic "..."</c> quoting, `&`
        /// chaining), and PowerShell's argument reassembly does not parse those the same way — the WSL
        /// update silently failed to launch and the relaunch below brought the agent straight back with no
        /// update having run at all. Two other problems apply to both hosts: some locked-down machines
        /// deny a direct <see cref="Process.Start"/> of a shell for a non-elevated Visual Studio (Software
        /// Restriction Policy / AppLocker rules commonly exempt elevated processes) — measured as a
        /// <see cref="Win32Exception"/> "Access is denied" before any window appeared, fixed by falling
        /// back to <c>UseShellExecute = true</c>, which routes through the OS shell launch broker instead
        /// of a raw <c>CreateProcess</c> call and is not subject to the same restriction. And
        /// <c>WorkingDirectory</c> is pinned to the user's own temp folder rather than left to inherit
        /// whatever Visual Studio's current directory happens to be, since that is one more thing that can
        /// be inaccessible to a non-elevated process.
        /// </summary>
        private static Process StartUpdateWindow(AiProvider provider, string updateCommand)
        {
            string exe = provider == AiProvider.DevinNative ? "powershell.exe" : "cmd.exe";
            string arguments = provider == AiProvider.DevinNative
                ? "-NoLogo -NoProfile -NoExit -Command " + updateCommand
                : "/k " + updateCommand;

            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetTempPath()
            };

            try
            {
                return Process.Start(startInfo);
            }
            catch (Win32Exception ex)
            {
                Debug.WriteLine($"StartUpdateWindow: direct launch denied ({ex.Message}), retrying via the shell.");
                startInfo.UseShellExecute = true;
                return Process.Start(startInfo);
            }
        }

        /// <summary>
        /// Devin (native) updater: the official installer script, retried. Its last step copies the new build
        /// over %LOCALAPPDATA%\devin\cli\bin\devin.exe and failed with "used by another process" even with no
        /// devin.exe left running (measured: the file was unlocked again moments later — a transient holder such
        /// as a real-time antivirus scan), so each attempt first stops any straggler, waits, and a failed attempt
        /// is retried instead of leaving the old build in place. Single quotes only: the text is embedded in a
        /// double-quoted <c>powershell -Command "..."</c> in the terminal-mode button as well.
        /// </summary>
        private const string DevinNativeUpdateScript =
            "for ($i=1; $i -le 5; $i++) { " +
            "Get-Process devin -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; " +
            "Start-Sleep -Seconds 2; " +
            "try { irm https://cli.devin.ai/install.ps1 | iex; break } " +
            "catch { Write-Host ('Update attempt ' + $i + ' of 5 failed: ' + $_.Exception.Message) -ForegroundColor Yellow } }";

        /// <summary>
        /// Same self-update command each provider's terminal-mode "Update Agent" button types into the
        /// console (<c>UpdateAgentButton_Click</c>), minus the exit sequence — there is no TUI session to
        /// exit here, the process was already killed by <see cref="DisposeNativeSession"/>.
        /// </summary>
        private static string GetNativeAgentUpdateCommand(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.CodexNative:
                    return "npm install -g @openai/codex@latest";
                case AiProvider.Codex:
                    return "wsl bash -lic \"npm install -g @openai/codex@latest\"";
                case AiProvider.CursorAgentNative:
                    return "agent update";
                case AiProvider.CursorAgent:
                    return "wsl bash -lic \"cursor-agent update\"";
                case AiProvider.ClaudeCodeWSL:
                    return "wsl bash -lic \"claude update\"";
                case AiProvider.ClaudeCode:
                    return "claude update";
                case AiProvider.OpenCode:
                    return "npm i -g opencode-ai";
                case AiProvider.Devin:
                    return "wsl bash -lic \"devin update\"";
                case AiProvider.Pi:
                    return "npm install -g @earendil-works/pi-coding-agent@latest";
                case AiProvider.Antigravity:
                    return "agy update";
                case AiProvider.Reasonix:
                    return "npm i -g reasonix";
                case AiProvider.QwenCode:
                    return "npm install -g @qwen-code/qwen-code@latest";
                case AiProvider.Grok:
                    return "grok update";
                case AiProvider.DevinNative:
                    // `devin update` only prints the install command; the installer overwrites
                    // %LOCALAPPDATA%\devin\cli\bin\devin.exe, so force-kill stragglers first to release
                    // the self-update lock before running the official installer script — exactly the
                    // command Devin's own CLI prints, unwrapped. The earlier nested
                    // `powershell -NoProfile -ExecutionPolicy Bypass -Command "..."` (needed when this ran
                    // inside cmd.exe) is gone now that StartUpdateWindow hosts the update in powershell.exe
                    // directly: neither flag is required for an inline -Command script, and that exact
                    // shape (bypassed policy + download-and-execute) is what Windows Defender flagged.
                    return DevinNativeUpdateScript;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Handles the ⚙ menu's "Change Account" item in native mode. There is no console here to run
        /// <c>/logout</c> against — the terminal-mode equivalent (<c>ChangeAccountMenuItem_Click</c>)
        /// scripts that as keystrokes into the embedded window. <c>claude auth logout</c>/<c>auth login</c>
        /// are the CLI's own auth subcommands (confirmed via <c>claude auth --help</c>): logout clears the
        /// real local credential, login drives the CLI's own OAuth round-trip and updates that same store
        /// when it completes. For <see cref="AiProvider.ClaudeCodeWSL"/> both subcommands run inside the
        /// distro (via <c>wsl.exe bash -lic "claude auth ..."</c>) instead of against the Windows-side
        /// executable — running them on Windows used to "succeed" without errors while leaving the actual
        /// WSL session's credential untouched, since the two providers each authenticate against their own
        /// separate <c>~/.claude.json</c>.
        /// <para>
        /// Two separate browser touchpoints, in order, because they solve two different problems:
        /// (1) <c>https://claude.ai</c> is opened first so the user can consciously sign out of the old
        /// account and into the desired one on the plain site — needed because claude.ai's own OAuth
        /// *authorize* endpoint has no account picker and silently re-approves whoever the browser is
        /// already signed in as, so without this step the CLI's own login below just re-authenticated as
        /// the same old account through the browser's existing session cookies, which is what made the
        /// switch look like it silently did nothing. (2) Only after that confirmation does <c>claude auth
        /// login</c> run, in its own visible console (see the try block below) — that drives the CLI's own
        /// device-code-style round-trip against whichever account is now active in the browser.
        /// </para>
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void ChangeAccountNativeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                AiProvider? activeProvider = GetActiveOrSelectedProvider();
                if (!IsNativeModeActive || !IsClaudeProvider(activeProvider))
                {
                    return;
                }

                bool isWsl = activeProvider == AiProvider.ClaudeCodeWSL;

                // Sign out the embedded usage WebView2 so the new account is picked up there too.
                await SignOutUsageWindowIfActiveAsync();

                // Clear the CLI's local "signed in as" record before relaunching — otherwise the
                // relaunch below reads the same still-there oauthAccount and reports a successful
                // switch to the *old* account (the false "switched" message this was fixing).
                await InvalidateSignedInClaudeAccountLabelAsync(isWsl);

                // ClaudeCodeWSL authenticates through the distro's own claude install: running these
                // against the Windows-side executable (as this used to, unconditionally) logs out/in a
                // credential the live WSL session never uses, so the switch appears to succeed but the
                // running conversation keeps whatever WSL-side account it already had.
                string claudeExe = isWsl ? "wsl.exe" : ResolveNativeClaudeExecutable();
                string logoutArgs = isWsl ? "bash -lic \"claude auth logout\"" : "auth logout";

                try
                {
                    using (var logout = Process.Start(new ProcessStartInfo(claudeExe, logoutArgs)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }))
                    {
                        await WaitForProcessExitAsync(logout, 5000);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ChangeAccountNativeMenuItem_Click: auth logout failed: {ex.Message}");
                }

                // claude.ai's own OAuth authorize page silently re-approves whoever is already signed
                // in there — it has no account picker — so without this step `claude auth login` below
                // just re-authenticated as the same old account via the browser's existing session
                // cookies, which is what made the switch look like it silently did nothing. Sending the
                // user to the plain claude.ai site first, to consciously sign out and into the desired
                // account there, is what gives the CLI's OAuth round-trip a fresh session to approve.
                try
                {
                    Process.Start(new ProcessStartInfo("https://claude.ai") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ChangeAccountNativeMenuItem_Click: could not open browser: {ex.Message}");
                }

                MessageBox.Show(
                    L("Please log out and log in to the desired account in the Claude browser tab that just opened, then click OK to continue."),
                    L("Change Account"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                try
                {
                    // `auth login` needs a real, visible console — measured: it drives an interactive
                    // OAuth round-trip (opens the browser, and if that redirect can't complete on its
                    // own it asks the user to paste a code back into this same window), so it needs
                    // somewhere to print the browser URL/prompt to and somewhere to read a pasted code
                    // from. CreateNoWindow=true gave it neither, which is why the browser never opened
                    // and login could never actually finish even though this method itself hit no error.
                    // Fire-and-forget: not awaited so the UI thread isn't blocked; the process keeps
                    // running after this method returns and is not killed by disposing the managed
                    // wrapper. Wrapped in `cmd /k` so the window (and any error `claude auth login`
                    // prints) stays open after the CLI exits instead of flashing shut.
                    string loginArguments = isWsl
                        ? "/k wsl bash -lic \"claude auth login\""
                        : $"/k \"{claudeExe}\" auth login";

                    Process.Start(new ProcessStartInfo("cmd.exe", loginArguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = false
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ChangeAccountNativeMenuItem_Click: auth login failed: {ex.Message}");
                }

                MessageBox.Show(
                    L("A console window opened to finish signing in to the CLI — complete it there (pasting the code if prompted), then click OK to resume Claude Code."),
                    L("Change Account"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await RelaunchNativeSessionAsync(L("🔑 Switched account — resuming"), appendAccountLabel: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Change account (native) failed: {ex.Message}");
            }
        }

        #endregion
    }
}
