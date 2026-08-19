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
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Chat Tab Fields

        private NativeChatToolWindow _nativeChatWindow;

        /// <summary>True while the transcript is parented into the document tab instead of the panel.</summary>
        private bool _chatIsInTab;

        /// <summary>Composer events are wired once for the lifetime of the control.</summary>
        private bool _composerWired;

        /// <summary>Guards against a second switch being started while a relaunch is in flight.</summary>
        private bool _nativeSwitchInProgress;

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

                if (_nativeChatWindow == null)
                {
                    _nativeChatWindow = package.FindToolWindow(typeof(NativeChatToolWindow), 0, true) as NativeChatToolWindow;
                    if (_nativeChatWindow == null)
                    {
                        Debug.WriteLine("ShowNativeChatTabAsync: could not create the chat tab; the chat stays in the panel.");
                        return;
                    }

                    _nativeChatWindow.Closed += OnNativeChatWindowClosed;
                    _nativeChatWindow.Activated += OnDefaultNativeChatWindowActivated;
                }

                WireChatComposer();

                if (!_chatIsInTab)
                {
                    // A WPF element lives in exactly one visual tree, so it has to leave the panel grid
                    // before the pane can take it.
                    if (TerminalSlotGrid != null && TerminalSlotGrid.Children.Contains(ChatTranscript))
                    {
                        TerminalSlotGrid.Children.Remove(ChatTranscript);
                    }

                    _nativeChatWindow.SetChatContent(ChatTranscript);
                    _chatIsInTab = true;
                }

                ChatTranscript.Visibility = Visibility.Visible;
                ChatTranscript.ShowComposer(true);
                UpdateChatComposerState();
                UpdateChatTabCaption();
                SetPanelTerminalAreaVisible(false);

                var frame = _nativeChatWindow.Frame as IVsWindowFrame;
                frame?.Show();

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
                // would only be a second input box saying the same thing.
                ChatTranscript.ShowComposer(false);
                ChatTranscript.Visibility = IsNativeModeActive ? Visibility.Visible : Visibility.Collapsed;

                SetPanelTerminalAreaVisible(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error returning the chat to the panel: {ex.Message}");
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
            ReturnNativeChatToPanel();

            // The pane is transient: once closed it is gone, so the next open has to build a new one.
            if (_nativeChatWindow != null)
            {
                _nativeChatWindow.Closed -= OnNativeChatWindowClosed;
                _nativeChatWindow.Activated -= OnDefaultNativeChatWindowActivated;
                _nativeChatWindow = null;
            }

            // The Detach control is the way back to the tab, so it has to flip to "detach" again.
            UpdateDetachButtonIcon(false);
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

                session.ChatTranscript.ShowComposer(true);
                UpdateChatComposerState();
                UpdateSessionTabCaption(session);

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
        /// <see cref="WireChatComposer"/>; this is the same list minus the handlers that act on the
        /// single global session (model/effort/provider, which relaunch it).
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

            // Toolbar and composer affordances. These were missing entirely, which is why none of the
            // buttons under the prompt box did anything in a new tab.
            transcript.AttachRequested += OnComposerAttachRequested;
            transcript.FilesDropped += OnComposerFilesDropped;
            transcript.ClearChatRequested += OnComposerClearChatRequested;
            transcript.NewChatRequested += OnComposerNewChatRequested;
            transcript.RenameSessionRequested += OnComposerRenameSessionRequested;
            transcript.ColorPickerRequested += OnComposerColorPickerRequested;
            transcript.ZoomChanged += OnComposerZoomChanged;
            transcript.ComposerHeightChanged += OnComposerHeightChanged;
            transcript.PasteRequested += OnComposerPasteRequested;
            transcript.HistoryPreviousRequested += OnComposerHistoryPreviousRequested;
            transcript.HistoryNextRequested += OnComposerHistoryNextRequested;
            transcript.LinkClicked += OnChatLinkClicked;
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
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling session window close: {ex.Message}");
            }
        }

        /// <summary>
        /// Moves the chat between its tab and the panel. This is what the Detach control does while
        /// native mode is running, and the way back after the user closes the tab.
        /// </summary>
        private async Task ToggleChatTabAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_chatIsInTab)
            {
                CloseNativeChatTab();
                UpdateDetachButtonIcon(false);
                return;
            }

            await ShowNativeChatTabAsync(focusComposer: true);
        }

        private void UpdateChatTabCaption()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_nativeChatWindow == null)
            {
                return;
            }

            string title = GetCurrentNativeSessionTitle();
            _nativeChatWindow.UpdateCaption(BuildChatTabCaption(GetActiveOrSelectedProvider(), title));

            // Also kept as a header above the transcript: the VS tab strip truncates, and with several
            // chats open the header is what tells them apart once a tab is selected.
            ChatTranscript?.SetSessionTitle(title);
            ChatTranscript?.SetSessionTitleColor(GetCurrentNativeSessionColor());
        }

        /// <summary>
        /// Tab caption for a chat: the agent's name plus the session's custom title when it has one,
        /// so several open chats are distinguishable from the tab strip alone.
        /// </summary>
        private string BuildChatTabCaption(AiProvider? provider, string sessionTitle)
        {
            string name = GetProviderDisplayName(provider);
            string caption = string.IsNullOrEmpty(name) ? "Chat" : name + " Chat";

            return string.IsNullOrWhiteSpace(sessionTitle)
                ? caption
                : caption + " — " + sessionTitle.Trim();
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

            session.Window?.UpdateCaption(BuildChatTabCaption(session.SelectedProvider, title));
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

        /// <summary>The user-assigned title color for an agent session id, or empty for the default.</summary>
        private string GetNativeSessionColor(string agentSessionId)
        {
            if (string.IsNullOrEmpty(agentSessionId) || _settings?.SessionTitleColors == null)
            {
                return string.Empty;
            }

            _settings.SessionTitleColors.TryGetValue(agentSessionId, out string color);
            return color ?? string.Empty;
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
            ChatTranscript.AttachRequested += OnComposerAttachRequested;
            ChatTranscript.FilesDropped += OnComposerFilesDropped;
            ChatTranscript.SelectorClicked += OnComposerSelectorClicked;
            ChatTranscript.EffortChanged += OnComposerEffortChanged;
            ChatTranscript.ClearChatRequested += OnComposerClearChatRequested;
            ChatTranscript.NewChatRequested += OnComposerNewChatRequested;
            ChatTranscript.RenameSessionRequested += OnComposerRenameSessionRequested;
            ChatTranscript.ColorPickerRequested += OnComposerColorPickerRequested;
            ChatTranscript.ZoomChanged += OnComposerZoomChanged;
            ChatTranscript.ComposerHeightChanged += OnComposerHeightChanged;
            ChatTranscript.PasteRequested += OnComposerPasteRequested;
            ChatTranscript.HistoryPreviousRequested += OnComposerHistoryPreviousRequested;
            ChatTranscript.HistoryNextRequested += OnComposerHistoryNextRequested;
            ChatTranscript.ComposerPreviewKeyDown += ComposerInput_AtMentionPreviewKeyDown;
            ChatTranscript.ComposerInputBox.TextChanged += ComposerInput_AtMentionTextChanged;
            ChatTranscript.LinkClicked += OnChatLinkClicked;

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
        /// Clears the current conversation and starts fresh.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerClearChatRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_agentSession == null)
                {
                    return;
                }

                if (MessageBox.Show(
                        "Clear this conversation and start fresh?",
                        "Clear Chat",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }

                ChatTranscript.Clear();

                // No resume id is staged, so the relaunch below starts a brand new session.
                await RelaunchNativeSessionAsync("🤖 New chat", forceNewSession: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat clear failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a new parallel chat session. Tries to create a new session with the same provider
        /// as the current one. For now, displays in the same area; future versions will show in separate tabs.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerNewChatRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_agentSession == null)
                {
                    MessageBox.Show("No active session. Start native mode first.", "New Session", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Get workspace
                string workspace = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrWhiteSpace(workspace) || !System.IO.Directory.Exists(workspace))
                {
                    MessageBox.Show("Cannot create new session: no workspace available.", "New Session", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Use same provider as current session
                AiProvider provider = _settings.SelectedProvider;

                // Create new session
                var newSession = CreateAndRegisterSession(provider, workspace);
                if (newSession?.AgentSession == null)
                {
                    MessageBox.Show("Failed to create new session.", "New Session", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Composer/transcript events are wired by ShowSessionInTabAsync below.

                // Clear and start
                newSession.ChatTranscript.Clear();
                newSession.ChatTranscript.SetStatus("Starting new session...");

                // Show new session in its own tab. Marked focused right away rather than waiting for
                // the tab's own activation event, so a build/runtime error landing before the user
                // clicks anything still targets the tab they just opened, not the old one.
                await ShowSessionInTabAsync(newSession, focusComposer: false);
                _lastFocusedNativeSessionId = newSession.SessionId;

                // Configure composer controls for this session
                UpdateChatComposerState(newSession.ChatTranscript);
                // Disable model/effort selectors in new sessions (they relaunche the global session)
                newSession.ChatTranscript.SetSelectorAvailability(model: false, effort: false, permission: true);
                // Apply the same zoom as the main session for consistency
                newSession.ChatTranscript.Zoom = _settings?.NativeChatZoom ?? 1.0;

                // Start the agent
                newSession.SessionCts = new CancellationTokenSource();
                await newSession.AgentSession.StartAsync(workspace, newSession.SessionCts.Token);

                newSession.ChatTranscript.SetStatus("Ready.");
                // Show welcome card in the new session, not the global one
                ShowChatWelcome(newSession.ChatTranscript, workspace);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat new-session failed: {ex.Message}");
                MessageBox.Show($"Failed to create new session: {ex.Message}", "New Session", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                    "This session doesn't have an id to rename yet. Send at least one message first — " +
                    "some agents (e.g. Codex, Antigravity) don't expose a renamable session id at all.",
                    "Rename Session", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    "This session doesn't have an id to color yet. Send at least one message first — " +
                    "some agents (e.g. Codex, Antigravity) don't expose a renamable session id at all.",
                    "Session Color", MessageBoxButton.OK, MessageBoxImage.Information);
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
        /// which of them apply, and the send-key preference.
        /// </summary>
        private void UpdateChatComposerState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || !_chatIsInTab)
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

            transcript.SetSelectorLabels(
                GetChatProviderDisplayName(provider),
                GetChatModelLabel(provider),
                reasoningLabel,
                GetChatPermissionLabel(provider));

            transcript.SetSelectorAvailability(
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
                    "Reasoning");
            }
            else
            {
                transcript.SetEffortStopLabels(GetChatEffortStopLabels());
                transcript.SetEffortSlider(
                    EffortToSliderIndex(
                        _settings != null ? _settings.SelectedEffortLevel : EffortLevel.High),
                    reasoningLabel,
                    "Effort");
            }

            ApplyChatAppearance();
            UpdateComposerAttachmentChips();
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

        private async Task OpenChatFileLinkAsync(string reference)
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
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte == null)
            {
                return;
            }

            dte.ItemOperations.OpenFile(resolved);

            if (line > 0 && dte.ActiveDocument != null)
            {
                var selection = dte.ActiveDocument.Selection as EnvDTE.TextSelection;
                selection?.GotoLine(line, false);
            }
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

            string candidate = path.Replace('/', Path.DirectorySeparatorChar);

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
        }

        /// <summary>
        /// Most rows a replayed transcript may add. A long conversation would otherwise take seconds to
        /// render and bury the composer under thousands of bubbles; the newest ones are the ones worth
        /// showing, so the cap keeps the tail and says how much it dropped.
        /// </summary>
        private const int ResumedTranscriptMaxRows = 200;

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
        /// <returns>False when there is nothing to replay, and the caller shows the welcome card.</returns>
        private async Task<bool> TryReplayResumedTranscriptAsync(AiProvider provider, string workspace, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId == "-c" || ChatTranscript == null)
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

                    int start = Math.Max(0, messages.Count - ResumedTranscriptMaxRows);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AddNativeMessage(ChatMessageKind.Notice, start > 0
                        ? $"🕘 Resumed this Codex conversation — showing the last {messages.Count - start} of {messages.Count} messages."
                        : $"🕘 Resumed this Codex conversation — {messages.Count} earlier messages restored.");

                    for (int index = start; index < messages.Count; index++)
                    {
                        CodexTranscriptMessage message = messages[index];
                        AddNativeMessage(message.IsUser ? ChatMessageKind.User : ChatMessageKind.Assistant,
                            message.Text.TrimEnd());
                    }

                    ChatTranscript.ScrollToEndIfFollowing();
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
                int total = 0;
                List<KeyValuePair<ChatMessageKind, string>> rows =
                    await Task.Run(() => ReadResumedTranscript(path, out total));

                if (rows == null || rows.Count == 0)
                {
                    return false;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                int dropped = total - rows.Count;
                AddNativeMessage(ChatMessageKind.Notice, dropped > 0
                    ? $"🕘 Resumed this conversation — showing the last {rows.Count} of {total} messages."
                    : $"🕘 Resumed this conversation — {rows.Count} earlier messages restored.");

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
                        AddNativeMessage(ChatMessageKind.Notice, "Signed in as " + account);
                    }
                }

                foreach (KeyValuePair<ChatMessageKind, string> row in rows)
                {
                    AddNativeMessage(row.Key, row.Value);
                }

                ChatTranscript.ScrollToEndIfFollowing();
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
        /// Reads a session's JSONL into chat rows, newest-last, capped at
        /// <see cref="ResumedTranscriptMaxRows"/>. <paramref name="total"/> reports how many rows the
        /// file held before the cap, so the caller can say what it left out. Shares the extraction
        /// helpers with the Session History dialog, so both read a transcript the same way — including
        /// skipping the tool-result rows the CLI re-injects as "user" messages.
        /// </summary>
        private static List<KeyValuePair<ChatMessageKind, string>> ReadResumedTranscript(string path, out int total)
        {
            var rows = new List<KeyValuePair<ChatMessageKind, string>>();
            total = 0;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
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

                    bool isUser = type == "user";
                    string text = isUser
                        ? ExtractUserText(message["content"])
                        : ExtractAssistantText(message["content"]);

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    total++;
                    rows.Add(new KeyValuePair<ChatMessageKind, string>(
                        isUser ? ChatMessageKind.User : ChatMessageKind.Assistant, text.TrimEnd()));

                    // Trimming as we go keeps a very long transcript from being held in full.
                    if (rows.Count > ResumedTranscriptMaxRows)
                    {
                        rows.RemoveAt(0);
                    }
                }
            }

            return rows;
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
                facts.Add(GetChatModelLabel(provider) + " with " + GetChatEffortLabel().ToLowerInvariant() + " effort");

                bool isWsl = provider == AiProvider.ClaudeCodeWSL;
                string account = isWsl
                    ? (_wslAccountLabels.TryGetValue(AiProvider.ClaudeCodeWSL, out string cachedAccount) ? cachedAccount : null)
                    : GetSignedInClaudeAccountLabel();

                if (!string.IsNullOrEmpty(account))
                {
                    facts.Add("Signed in as " + account);
                }
            }
            else if (isCodex)
            {
                string model = GetChatModelLabel(provider);
                string modelLabel = string.Equals(model, "Model", StringComparison.Ordinal)
                    ? "Agent default model"
                    : model;
                facts.Add(modelLabel + " with " +
                    GetChatCodexReasoningLabel().ToLowerInvariant() + " reasoning");

                if (provider.HasValue &&
                    _codexAccountLabels.TryGetValue(provider.Value, out string account) &&
                    !string.IsNullOrWhiteSpace(account))
                {
                    facts.Add("Signed in as " + account);
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
                "Press Ctrl+Up/Down in the prompt box for prompt history.",
                "Ctrl+V pastes an image from the clipboard; 📎 attaches files.",
                "Ctrl+Scroll zooms the conversation, and the top edge of the prompt box can be dragged.",
                "The buttons below switch agent, model, effort and permissions mid-conversation."
            };

            if (IsClaudeProvider(provider))
            {
                tips.Add("Type /plan, /model or /effort to switch without leaving the prompt box.");
            }

            tips.Add("✚ starts a new chat.");

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

                // Only worth redrawing while the card this probe belongs to is still the one on screen:
                // the user may already have sent a prompt, or switched to another agent.
                if (ChatTranscript != null &&
                    ChatTranscript.IsWelcomeVisible &&
                    GetActiveOrSelectedProvider() == provider)
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
                    return "OpenAI API key";
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
                return string.IsNullOrWhiteSpace(label) ? "Model" : label;
            }

            switch (_settings?.SelectedClaudeModel)
            {
                case ClaudeModel.Opus: return "Opus";
                case ClaudeModel.Sonnet: return "Sonnet";
                case ClaudeModel.Haiku: return "Haiku";
                case ClaudeModel.OpusPlan: return "Opus Plan";
                default: return "Best";
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

            if (IsClaudeProvider(provider) && _settings?.ClaudePlanMode == true)
            {
                return "Plan mode";
            }

            return skipping.Value ? "Skip permissions" : "Ask permission";
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

        private void OnComposerAttachRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            NativeChatSessionState owner = ResolveSessionFromSender(sender);
            if (owner == null)
            {
                ImageDropBorder_Click(sender, null);
                return;
            }

            string[] chosen = PickAttachmentFiles();
            if (chosen != null)
            {
                AddSessionAttachments(owner, chosen);
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
        }

        #endregion

        #region Composer Selectors

        private void OnComposerSelectorClicked(object sender, ChatSelector selector)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var anchor = sender as Button;
            if (anchor == null)
            {
                return;
            }

            ContextMenu menu;

            // Effort is not here: it opens the pill slider inside the transcript view and reports back
            // through EffortChanged.
            switch (selector)
            {
                case ChatSelector.Provider: menu = BuildChatProviderMenu(); break;
                case ChatSelector.Model: menu = BuildChatModelMenu(); break;
                default: menu = BuildChatPermissionMenu(); break;
            }

            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
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

        private ContextMenu BuildChatModelMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            if (active != null && !IsClaudeProvider(active) && ProviderHasModelCatalog(active))
            {
                FillChatModelMenu(menu, active.Value);

                return menu;
            }

            ClaudeModel selected = _settings != null ? _settings.SelectedClaudeModel : ClaudeModel.Best;

            AddComposerMenuItem(menu, "Best", selected == ClaudeModel.Best,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Best); });
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

        /// <summary>
        /// Fills the composer's model dropdown with the agent's own models, plus an entry that leaves
        /// the model to the agent. A stale or missing list is re-read from the CLI in the background
        /// and dropped into the same menu when it arrives, so the dropdown opens instantly either way.
        /// </summary>
        private void FillChatModelMenu(ContextMenu menu, AiProvider provider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string selected = GetSelectedProviderModelId(provider);
            List<Agents.ModelGroup> groups =
                Agents.ModelCatalogGrouping.Group(GetCachedProviderModels(provider));

            // Repeated at the top when the list is grouped: inside a submenu the checked entry is
            // invisible until the right one is opened.
            if (groups.Exists(g => g.IsSubmenu) && !string.IsNullOrWhiteSpace(selected))
            {
                AddChatModelMenuItem(menu, new Agents.ModelOption
                {
                    Id = selected,
                    Name = GetSelectedProviderModelLabel(provider)
                }, selected);
            }

            foreach (Agents.ModelGroup group in groups)
            {
                ItemsControl parent = group.IsSubmenu ? AddComposerSubmenu(menu, group.Name) : (ItemsControl)menu;

                foreach (Agents.ModelOption model in group.Models)
                {
                    AddChatModelMenuItem(parent, model, selected);
                }
            }

            AddComposerMenuItem(menu, "Agent default", string.IsNullOrWhiteSpace(selected),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelected(string.Empty); });

            if (!ShouldRefreshProviderModels(provider)) return;

            MenuItem loading = AddComposerMenuItem(menu, "Loading models…", false, delegate { });
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

        private void AddChatModelMenuItem(ItemsControl parent, Agents.ModelOption model, string selected)
        {
            string current = model.Id;

            AddComposerMenuItem(parent, model.DisplayName,
                string.Equals(current, selected, StringComparison.OrdinalIgnoreCase),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelected(current); });
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
                bool planning = _settings?.ClaudePlanMode == true;

                AddComposerMenuItem(menu, "Plan mode", planning,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPlanModeSelected(true); });
                AddComposerMenuItem(menu, "Ask permission", !planning && !skipping.Value,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPlanModeSelected(false); });
                AddComposerMenuItem(menu, "Skip permissions", !planning && skipping.Value,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(true); });

                return menu;
            }

            AddComposerMenuItem(menu, "Ask permission", !skipping.Value,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(false); });
            AddComposerMenuItem(menu, "Skip permissions", skipping.Value,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(true); });

            return menu;
        }

        #endregion

        #region Typed Selector Commands

        /// <summary>
        /// Handles the commands a user can type instead of clicking: <c>/plan</c> turns plan mode on,
        /// <c>/model</c> and <c>/effort</c> open the pickers the composer buttons open, and
        /// <c>/btw</c> asks a side question.
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
                    AddNativeMessage(ChatMessageKind.Notice, "Usage: /btw <your question>");
                    return true;
                }

                AddNativeMessage(ChatMessageKind.User, trimmed);
                AddNativeMessage(ChatMessageKind.Notice,
                    "💬 Side question — asked in a forked copy of this conversation, so the work in progress is untouched.");

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

                    if (_settings?.ClaudePlanMode == true)
                    {
                        AddNativeMessage(ChatMessageKind.Notice, "🤖 Already in plan mode.");
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

                if (_settings == null || _settings.SelectedProvider == provider)
                {
                    return;
                }

                _settings.SelectedProvider = provider;
                UpdateProviderSelection();
                SaveSettings();

                await RestartTerminalWithSelectedProviderAsync();

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
                        $"Could not switch to {GetChatProviderDisplayName(provider)} — check that it is installed " +
                        "and reachable, then try again. The embedded terminal may have been used instead.");
                    return;
                }

                AddNativeMessage(ChatMessageKind.Notice,
                    $"🤖 Switched to {GetChatProviderDisplayName(provider)} — this starts a new conversation.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat provider switch failed: {ex.Message}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessage(ChatMessageKind.Error, $"Could not switch agent: {ex.Message}");
            }
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
                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync($"🤖 Switched to {GetChatModelLabel(GetActiveOrSelectedProvider())}");
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
                ? "the agent's default"
                : GetSelectedProviderModelLabel(provider);

            // "Agent default" cannot be requested over the protocol — there is no such option to
            // select — so it always goes through the relaunch below.
            if (!string.IsNullOrWhiteSpace(model) && await TrySwitchAcpModelAsync(model))
            {
                AddNativeMessage(ChatMessageKind.Notice, $"🤖 Model switched to {label}.");
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Switch the model to {label} and restart the chat so the change takes effect now?",
                $"Switch {GetChatProviderDisplayName(provider)} model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await RelaunchNativeSessionAsync($"🤖 Model switched to {label}", forceNewSession: true);
            }
            else
            {
                AddNativeMessage(ChatMessageKind.Notice, $"🤖 Model set to {label} — it applies the next time the agent starts.");
            }
        }

        /// <summary>
        /// Asks a live ACP session to change its model. False when there is no such session running or
        /// the agent would not take the model, and the caller then offers the restart instead.
        /// </summary>
        private async Task<bool> TrySwitchAcpModelAsync(string model)
        {
            var session = _agentSession as AcpSession;
            if (session == null)
            {
                return false;
            }

            try
            {
                return await session.SetModelAsync(model, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native chat: switching the model on the live session failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The composer's effort popup closed on a stop other than the one it opened on. Same slider
        /// order as the panel, so both controls always agree on what "High" means.
        /// </summary>
        private void OnComposerEffortChanged(object sender, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
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

                await RelaunchNativeSessionAsync($"🤖 Effort switched to {GetChatEffortLabel()}");
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
                $"🤖 Reasoning switched to {GetChatCodexReasoningLabel()} for the next turn.");
        }

        /// <summary>
        /// Permission handling is a launch flag for every agent that has one, so the switch relaunches
        /// the session — resuming it where the protocol allows, so the conversation is not lost.
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
                if (!current.HasValue || current.Value == skip)
                {
                    return;
                }

                SetChatPermissionSkipFlag(provider, skip);

                // Skipping every prompt and planning before acting are opposites; picking one drops
                // the other rather than launching with a contradictory pair of flags.
                if (skip && _settings != null)
                {
                    _settings.ClaudePlanMode = false;
                }

                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync(skip
                    ? "🤖 Switched to skipping permission prompts"
                    : "🤖 Switched to asking for permission");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat permission switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Turns plan mode on or off. It is a launch flag, so the session is relaunched — resumed, so
        /// the conversation so far still counts as the plan's input.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPlanModeSelected(bool planning)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.ClaudePlanMode == planning)
                {
                    return;
                }

                _settings.ClaudePlanMode = planning;

                if (planning)
                {
                    // Plan mode is the CLI asking before it acts, which "skip permissions" would
                    // bypass entirely.
                    _settings.ClaudeDangerouslySkipPermissions = false;
                }

                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync(planning
                    ? "🤖 Plan mode on — the agent will propose a plan before making changes"
                    : "🤖 Plan mode off");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat plan-mode switch failed: {ex.Message}");
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

            IAgentSession previous = _agentSession;
            if (previous == null)
            {
                // Native mode is not running (the panel is on the terminal) — nothing to relaunch.
                return;
            }

            _nativeSwitchInProgress = true;

            try
            {
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

                ChatTranscript.SetBusy(false);
                ChatTranscript.SetQueuedMessageCount(0);
                ChatTranscript.SetStatus("Restarting the agent...");

                DisposeNativeSession();

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
                    AddNativeMessage(ChatMessageKind.Error, "The agent could not be restarted.");
                    ChatTranscript.SetStatus(string.Empty);
                    return;
                }

                _nativeSessionCts = new CancellationTokenSource();
                _agentSession = session;
                session.Received += OnAgentEventReceived;

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
                ChatTranscript.SetStatus("Ready.");

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
                        notice += " — now signed in as " + account;
                    }
                }

                // No caveat when the provider can resume but there is simply nothing to resume yet
                // (no turn has run), because in that case no history is being lost.
                AddNativeMessage(ChatMessageKind.Notice, canResume || forceNewSession || providerCanResume
                    ? notice
                    : notice + " — this agent cannot resume, so the conversation starts over.");

                UpdateChatComposerState();
                UpdateChatTabCaption();

                // Only for "New chat". A model or permission switch keeps the conversation, and opening
                // a fresh-start card in the middle of one would read as if it had been thrown away.
                if (forceNewSession)
                {
                    ShowChatWelcome(workspace);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: relaunch failed: {ex}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessage(ChatMessageKind.Error, $"The agent could not be restarted: {ex.Message}");
                ChatTranscript.SetStatus(string.Empty);
            }
            finally
            {
                _nativeSwitchInProgress = false;
            }
        }

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
                MessageBox.Show("The agent is not running.", "Update Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AiProvider provider = _currentRunningProvider ?? _settings.SelectedProvider;
            string updateCommand = GetNativeAgentUpdateCommand(provider);
            if (string.IsNullOrEmpty(updateCommand))
            {
                MessageBox.Show("No update command is known for this agent.", "Update Agent", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RelaunchNativeSessionAsync("🔄️ Agent updated — resuming", midRelaunchAsync: async () =>
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
                    MessageBox.Show($"Failed to run the updater: {ex.Message}", "Update Agent", MessageBoxButton.OK, MessageBoxImage.Error);
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
                case AiProvider.DevinNative:
                    // `devin update` only prints the install command; the installer overwrites
                    // %LOCALAPPDATA%\devin\cli\bin\devin.exe, so force-kill stragglers first to release
                    // the self-update lock before running the official installer script — exactly the
                    // command Devin's own CLI prints, unwrapped. The earlier nested
                    // `powershell -NoProfile -ExecutionPolicy Bypass -Command "..."` (needed when this ran
                    // inside cmd.exe) is gone now that StartUpdateWindow hosts the update in powershell.exe
                    // directly: neither flag is required for an inline -Command script, and that exact
                    // shape (bypassed policy + download-and-execute) is what Windows Defender flagged.
                    return "taskkill /f /im devin.exe; irm https://cli.devin.ai/install.ps1 | iex";
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
                    "Please log out and log in to the desired account in the Claude browser tab that just opened, then click OK to continue.",
                    "Change Account",
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
                    "A console window opened to finish signing in to the CLI — complete it there (pasting the code if prompted), then click OK to resume Claude Code.",
                    "Change Account",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await RelaunchNativeSessionAsync("🔑 Switched account — resuming", appendAccountLabel: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Change account (native) failed: {ex.Message}");
            }
        }

        #endregion
    }
}
