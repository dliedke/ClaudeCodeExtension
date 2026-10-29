/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Chat transcript displayed in place of the embedded terminal when native mode is on
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using static ClaudeCodeVS.UI.Loc;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// The conversation view for native mode. Owns only presentation: the parent control feeds it
    /// messages and it never talks to an agent session directly.
    /// </summary>
    public partial class ChatTranscriptView : UserControl
    {
        // 2 device-independent pixels of slack: the scroll offset rarely lands exactly on the extent,
        // and requiring equality would leave auto-scroll permanently off.
        private const double BottomTolerance = 2.0;

        // A scroll offset that moved by less than this is layout jitter, not a reader scrolling.
        private const double ScrollDeltaTolerance = 0.5;

        // After the last wheel/scrollbar input, give the virtualized list this long to settle before
        // following may resume. Fast wheel runs otherwise clear the reader flag at a momentary bottom
        // and a layout change immediately queues a pass that fights the next wheel turn.
        private static readonly TimeSpan ReaderSettleDelay = TimeSpan.FromMilliseconds(350);

        private const double MinZoom = 0.6;
        private const double MaxZoom = 2.5;
        private const double ZoomStep = 0.1;

        private const double MinComposerHeight = 46;
        private const double MaxComposerHeight = 400;

        private ChatMessageViewModel _trackedMessage;
        private ScrollViewer _transcriptScroll;
        private bool _autoScroll = true;

        // Set only by the reader's own input (wheel or the transcript's scrollbar). Layout and
        // virtualization also change the offset while a long reply grows, and those must not read
        // as "the reader scrolled away".
        private bool _readerTookOver;
        private bool _scrollToEndQueued;
        private bool _scrollToEndActive;
        private bool _forceScrollToEnd;
        private int _scrollToEndAttempts;
        private bool _suppressEffortEvent;
        private int _effortIndexOnOpen;
        private int _effortIndex;
        private string[] _effortStopLabels;
        private string _effortTitle = L("Effort");

        private System.Windows.Threading.DispatcherTimer _activityTimer;
        private System.Windows.Threading.DispatcherTimer _readerSettleTimer;
        private DateTime _activityStartedUtc;
        private DateTime _lastVerbChangeUtc;
        private int _spinnerFrame;
        private int _verbIndex;

        /// <summary>Fixed caption, or empty while the rotating verbs are in charge.</summary>
        private string _activityLabel = string.Empty;
        private string _activityDetail = string.Empty;

        public ChatTranscriptView()
        {
            InitializeComponent();
            EffortSliderStops.Attach(EffortPopupSlider);

            Messages = new ObservableCollection<ChatMessageViewModel>();
            Messages.CollectionChanged += OnMessagesChanged;
            MessagesList.ItemsSource = Messages;

            // The list's ScrollViewer is part of its control template, so there is no generated
            // field to wire the event to; the routed event bubbles up from it to the list.
            MessagesList.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(TranscriptScroll_ScrollChanged));

            // Every message is a FlowDocumentScrollViewer, whose own ScrollViewer consumes the
            // wheel even when its scrollbars are disabled. Without this the transcript cannot be
            // scrolled at all while the pointer is over a message. Tunnelling lets the transcript
            // scroll first; a deliberately scrollable nested card (the diff pane) still keeps the
            // wheel while it has somewhere to go.
            MessagesList.AddHandler(PreviewMouseWheelEvent,
                new MouseWheelEventHandler(Transcript_PreviewMouseWheel), true);

            // Dragging the transcript's own scrollbar is also the reader taking over. Layout
            // corrections and virtualization never press it, so this is what tells a real scroll
            // apart from a long reply growing its own height.
            MessagesList.AddHandler(PreviewMouseDownEvent,
                new MouseButtonEventHandler(Transcript_PreviewMouseDown), true);

            Loaded += delegate
            {
                // Resolve the template's ScrollViewer once it exists, so the scroll helpers do not
                // have to search the visual tree on every streamed line.
                MessagesList.ApplyTemplate();
                _ = TranscriptScroll;
                ApplyLinkAccentForTheme();
                ApplyImageThemeBackground();
            };
            Unloaded += delegate { VSColorTheme.ThemeChanged -= OnVSColorThemeChanged; };
            VSColorTheme.ThemeChanged += OnVSColorThemeChanged;
        }

        /// <summary>
        /// The link/quote-border accent (<c>ChatLinkAccentBrush</c>) is a fixed blue rather than a VS
        /// theme brush — no VS key reads well as link text in both themes — so it needs its own
        /// light/dark swap instead of the automatic one <c>DynamicResource</c> gives real theme keys.
        /// Written into <see cref="FrameworkElement.Resources"/> so every <see cref="MarkdownBlock"/>
        /// row picks it up live via its style's <c>DynamicResource</c> setter, current rows and future
        /// ones alike.
        /// </summary>
        private void ApplyLinkAccentForTheme()
        {
            bool isDarkTheme = true;

            try
            {
                var windowBrush = (SolidColorBrush)FindResource(VsBrushes.WindowKey);
                Color color = windowBrush.Color;
                double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
                isDarkTheme = luminance < 0.5;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chat link accent theme detection failed: {ex.Message}");
            }

            Resources["ChatLinkAccentBrush"] = new SolidColorBrush(
                isDarkTheme ? Color.FromRgb(0x5B, 0xB4, 0xFF) : Color.FromRgb(0x1C, 0x8A, 0xE0));
        }

        private void OnVSColorThemeChanged(ThemeChangedEventArgs e)
        {
            ApplyLinkAccentForTheme();
            ApplyImageThemeBackground();
        }

        /// <summary>Transcript rows, oldest first.</summary>
        public ObservableCollection<ChatMessageViewModel> Messages { get; }

        /// <summary>Raised when the user clicks Stop. The parent control aborts the turn.</summary>
        public event EventHandler StopRequested;

        /// <summary>Raised when the composer's text should be sent as a prompt.</summary>
        public event EventHandler SendRequested;

        /// <summary>Files dropped onto the composer, so drag-and-drop attaches like the prompt box does.</summary>
        public event EventHandler<string[]> FilesDropped;

        /// <summary>
        /// Raised by one of the four selector buttons. The sender is the button, so the parent can
        /// anchor its context menu to it; the argument says which selector was clicked.
        /// <para>
        /// Effort is the exception: it opens the built-in pill slider popup and reports through
        /// <see cref="EffortChanged"/> instead, so it never reaches this event.
        /// </para>
        /// </summary>
        public event EventHandler<ChatSelector> SelectorClicked;

        /// <summary>
        /// Raised when the usage button is clicked, as its popup opens. The parent refreshes the Claude Usage
        /// data in the background; the popup is updated again when the new figures arrive.
        /// </summary>
        public event EventHandler UsageRequested;

        // Set from a ring click until the parent calls EndUsageRefresh; the popup's last line shows a rotating ring meanwhile.
        private bool _usageRefreshing;

        // The latest snapshot's "Updated 2:15 PM" line, shown again once the refresh is over.
        private string _usageUpdatedText = string.Empty;

        /// <summary>Raised by the popup's "See detailed breakdown". The parent opens the Claude Usage tab.</summary>
        public event EventHandler UsageDetailsRequested;

        /// <summary>
        /// Raised once the user is done with the effort slider — when the popup closes — carrying the
        /// stop it was left on. Deliberately not raised per stop: applying a level restarts the agent,
        /// so reporting each stop the user passes through would restart it once per stop and lock the
        /// chat up while they are still choosing.
        /// </summary>
        public event EventHandler<int> EffortChanged;

        /// <summary>Raised by the ↻ button: clear and restart the current session.</summary>
        public event EventHandler ClearChatRequested;

        /// <summary>Raised by the ✚ button: open a new parallel chat session.</summary>
        public event EventHandler NewChatRequested;

        /// <summary>Raised by the ✎ button: rename the session currently in the tab.</summary>
        public event EventHandler RenameSessionRequested;

        /// <summary>
        /// Raised by the 📎 button. The parent owns the file dialog and the attachment list, matching
        /// the panel's own AttachDropdownButton — drag-and-drop and Ctrl+V paste already attach files
        /// and images, but a picker is still the only way to browse to a file outside the editor.
        /// </summary>
        public event EventHandler AttachRequested;

        /// <summary>Raised by 📎 → "Insert editor selection": stage the editor selection in this composer.</summary>
        public event EventHandler InsertSelectionRequested;

        /// <summary>Raised by 📎 → "Insert active file path": stage an @-reference to the active document.</summary>
        public event EventHandler InsertActiveFilePathRequested;

        /// <summary>
        /// Raised by the ⚙/☰/⚡ mirror buttons. The sender is this view, so the parent can anchor the
        /// panel's actual context menu (there is no separate copy of its contents) to whichever button
        /// was clicked via <see cref="GetConfigMenuAnchor"/>.
        /// </summary>
        public event EventHandler<ChatConfigMenu> ConfigMenuClicked;

        /// <summary>
        /// Raised by one of the mirrored promoted-toolbar buttons (<see cref="SetPromotedButtons"/>).
        /// The argument is the same id passed into that call, so the parent can map it back to the
        /// toolbar feature and invoke the same click handler the panel button does.
        /// </summary>
        public event EventHandler<string> PromotedButtonClicked;

        /// <summary>Raised when Ctrl+Scroll changes the zoom, so the parent can persist it.</summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Raised when a link rendered inside the transcript's markdown is clicked. The parent decides
        /// what the URL means — a web address opens in the browser, a bare path opens in the editor.
        /// </summary>
        public event EventHandler<string> LinkClicked;

        private void MarkdownBlock_LinkClicked(object sender, string url)
        {
            LinkClicked?.Invoke(this, url);
        }

        /// <summary>
        /// Raised by a tool row's ↗ icon with the file it acted on. A dedicated event rather than
        /// reusing <see cref="LinkClicked"/>: the path here is never a guess (it came straight off the
        /// tool call), so a miss is worth telling the user about, unlike a markdown auto-link that is
        /// often just prose that happened to look like a path.
        /// </summary>
        public event EventHandler<string> ToolFileOpenRequested;

        private void OpenToolFile_Click(object sender, RoutedEventArgs e)
        {
            var message = (sender as FrameworkElement)?.DataContext as ChatMessageViewModel;
            if (message == null || string.IsNullOrWhiteSpace(message.ToolFilePath))
            {
                return;
            }

            // The button already stops this click from also toggling the row's Expander (its own
            // Click handling marks the routed mouse-up event handled before it bubbles that far), but
            // an explicit mark here costs nothing and removes any doubt if that stops being true.
            e.Handled = true;

            ToolFileOpenRequested?.Invoke(this, message.ToolFilePath);
        }

        /// <summary>
        /// Raised on Ctrl+V in the composer. The parent owns the clipboard image pipeline (it also owns
        /// the attachment list), and sets <see cref="ChatPasteEventArgs.Handled"/> when it consumed the
        /// clipboard as an image — otherwise the text box performs its normal text paste.
        /// </summary>
        public event EventHandler<ChatPasteEventArgs> PasteRequested;

        /// <summary>Raised by ↑ on the first line of the composer: show the previous prompt.</summary>
        public event EventHandler HistoryPreviousRequested;

        /// <summary>Raised by ↓ on the last line of the composer: back towards the newest prompt.</summary>
        public event EventHandler HistoryNextRequested;

        /// <summary>Raised when the composer is resized by dragging, so the parent can persist the height.</summary>
        public event EventHandler<double> ComposerHeightChanged;

        /// <summary>Text currently in the composer.</summary>
        public string ComposerText
        {
            get { return ComposerInput.Text; }
            set { ComposerInput.Text = value ?? string.Empty; }
        }

        /// <summary>Chips host for the parent's attachment list. Presentation only — the parent fills it.</summary>
        public System.Windows.Controls.Panel ComposerAttachmentsPanel
        {
            get { return ComposerAttachments; }
        }

        /// <summary>
        /// The composer's raw text box. Exposed (like <see cref="ComposerAttachmentsPanel"/>) so the
        /// parent's "@" file/folder mention popup can drive it directly (caret index, selection,
        /// character-rect positioning) the same way it drives the panel's own prompt box.
        /// </summary>
        public TextBox ComposerInputBox
        {
            get { return ComposerInput; }
        }

        /// <summary>
        /// Raised at the very top of the composer's PreviewKeyDown, before any of this view's own key
        /// handling (Enter-to-send, history navigation, Escape). Lets the parent's "@" mention popup
        /// claim Up/Down/Enter/Tab/Escape while it is open; setting <c>e.Handled</c> stops this view's
        /// own handling too.
        /// </summary>
        public event EventHandler<System.Windows.Input.KeyEventArgs> ComposerPreviewKeyDown;

        /// <summary>
        /// True while the user is typing in the composer. Prompt history writes to whichever prompt box
        /// has the keyboard, so the panel and the chat tab never overwrite each other's text.
        /// </summary>
        public bool ComposerHasFocus
        {
            get { return ComposerBar.Visibility == Visibility.Visible && ComposerInput.IsKeyboardFocusWithin; }
        }

        /// <summary>Mirrors the panel's send-key preference so Enter behaves the same in both places.</summary>
        public bool SendWithEnter
        {
            get { return _sendWithEnter; }
            set
            {
                _sendWithEnter = value;
                UpdateSendButtonVisibility();
            }
        }

        private bool _sendWithEnter = true;

        /// <summary>The transcript holds at least one prompt of the user's: the placeholder shrinks to "Reply…".</summary>
        private bool _hasUserMessage;

        /// <summary>
        /// The ▶ Send button shows only in "Button only" mode (issue #186), matching the panel's own
        /// button. Only the full composer (chat in its own tab) has an input box.
        /// </summary>
        private void UpdateSendButtonVisibility()
        {
            ComposerSendButton.Visibility = _composerMode == ComposerMode.Full && !_sendWithEnter && !_sendWithCtrlEnter
                ? Visibility.Visible
                : Visibility.Collapsed;

            // The full hint is for an empty chat; once the conversation has started a soft "Reply…"
            // is enough, and repeating the key hints under every answer is just noise.
            ComposerPlaceholder.Text = _hasUserMessage
                ? L("Reply…")
                : _sendWithEnter
                ? L("Ask anything · Enter to send · Ctrl+Up/Down for prompt history · Esc to stop")
                : _sendWithCtrlEnter
                    ? L("Ask anything · Ctrl+Enter to send · Ctrl+Up/Down for prompt history · Esc to stop")
                    : L("Ask anything · Ctrl+Up/Down for prompt history · Esc to stop");
        }

        private void ComposerSendButton_Click(object sender, RoutedEventArgs e)
        {
            SendRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Mirrors the panel's Ctrl+Enter preference. Ignored while <see cref="SendWithEnter"/> is on.</summary>
        public bool SendWithCtrlEnter
        {
            get { return _sendWithCtrlEnter; }
            set
            {
                _sendWithCtrlEnter = value;
                UpdateSendButtonVisibility();
            }
        }

        private bool _sendWithCtrlEnter;

        /// <summary>
        /// How much of the composer is showing. <see cref="Hidden"/> outside native mode;
        /// <see cref="ActionsOnly"/> while the chat is docked in the panel, where the panel's own
        /// prompt box sits directly above the transcript and a second input box would just be
        /// confusing — but the agent/model/effort/permission selectors and the mirrored config
        /// buttons still need somewhere to live, since the panel hides its own equivalents in native
        /// mode; <see cref="Full"/> once the chat has its own tab.
        /// </summary>
        public enum ComposerMode
        {
            Hidden,
            ActionsOnly,
            Full
        }

        private ComposerMode _composerMode = ComposerMode.Hidden;
        private bool _toolsMenuHasItems = true;
        private bool _customCommandsHasItems = true;

        /// <summary>
        /// Shows or hides the composer, and — in <see cref="ComposerMode.ActionsOnly"/> — trims it
        /// down to just the selectors and ↻/✚/✎/🎨, collapsing the resize grip, attachment chips,
        /// text input and the mirrored config buttons (⚙/☰/⚡ and the promoted-toolbar mirrors). Those
        /// mirrors only earn their keep once the chat has left for its own tab (<see cref="Full"/>) —
        /// while docked in the panel (<see cref="ActionsOnly"/>) the panel's own copies are right
        /// there in the same window, so showing both would just be clutter.
        /// </summary>
        public void SetComposerMode(ComposerMode mode)
        {
            _composerMode = mode;

            ComposerBar.Visibility = mode == ComposerMode.Hidden ? Visibility.Collapsed : Visibility.Visible;

            Visibility fullOnly = mode == ComposerMode.Full ? Visibility.Visible : Visibility.Collapsed;
            ComposerResizeGrip.Visibility = fullOnly;
            ComposerAttachments.Visibility = fullOnly;
            ComposerInputBorder.Visibility = fullOnly;
            UpdateSendButtonVisibility();
            ComposerSettingsButton.Visibility = fullOnly;
            ComposerSessionHistoryButton.Visibility = fullOnly;
            ComposerPromotedButtons.Visibility = fullOnly;
            UpdateToolsButtonVisibility();
            UpdateCustomCommandsButtonVisibility();

            UpdateClearButtonToolTip();

            // ComposerBar's own Padding reserves room for the resize grip above and the input box
            // below the action row — but in ActionsOnly those rows are collapsed (only the action
            // row shows, docked in the panel), so the full bottom padding was rendering as dead
            // space under the toolbar with nothing left to justify it.
            ComposerBar.Padding = mode == ComposerMode.Full
                ? new Thickness(8, 6, 8, 8)
                : new Thickness(8, 6, 8, 2);

            // The compact layout puts the row inside the input box, which ActionsOnly collapses.
            ApplyToolbarLayout();

            // Docked in the panel the row has far less width than it does in a tab, and the switch
            // between the two does not always change the row's own width — so the tier is re-picked
            // here rather than waiting for a resize that may never come.
            QueueComposerDensityRefresh();
        }

        /// <summary>
        /// Only reachable while native mode is on (Hidden is the only mode outside it), so this is also
        /// the one place that needs to know: "Clear this conversation" doubles as "restart the agent"
        /// here, and the default tooltip doesn't say that.
        /// </summary>
        private void UpdateClearButtonToolTip()
        {
            ComposerClearButton.ToolTip = _composerMode == ComposerMode.Hidden
                ? L("Clear this conversation and start fresh")
                : L("Restart the agent and start a fresh conversation");
        }

        /// <summary>
        /// Puts the text this view builds in code into the current chat language. The XAML captions
        /// follow a switch on their own (<see cref="LocExtension"/>), and the owner refreshes what it
        /// feeds in — selector captions, welcome card, usage figures. Messages already in the
        /// conversation stay as they were written; a card still waiting for an answer is re-captioned.
        /// </summary>
        public void RefreshLanguage()
        {
            UpdateSendButtonVisibility();
            UpdateClearButtonToolTip();

            if (_modernStopLabel != null)
            {
                _modernStopLabel.Text = L("Stop");
            }

            if (_activityTimer != null && _activityTimer.IsEnabled)
            {
                _activityLabel = Loc.Retranslate(_activityLabel);
                RenderActivity();
            }
            else if (!string.IsNullOrEmpty(StatusText.Text))
            {
                StatusText.Text = Loc.Retranslate(StatusText.Text);
            }

            ShowUsageUpdatedText();

            foreach (ChatInteractionViewModel card in Messages.OfType<ChatInteractionViewModel>())
            {
                if (card.IsPending)
                {
                    card.RefreshLanguage();
                }
            }

            QueueComposerDensityRefresh();
        }

        private bool _showNewChatAction = true;

        /// <summary>
        /// False in the single chat window ("Show all chats in one window"), whose tab strip has its
        /// own "+": the composer's ✚ and the ⋯ menu's "New chat session" would only repeat it.
        /// </summary>
        public bool ShowNewChatAction
        {
            get { return _showNewChatAction; }
            set
            {
                if (_showNewChatAction == value)
                {
                    return;
                }

                _showNewChatAction = value;
                ComposerOverflowNewChatItem.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                ApplyComposerDensity(_composerDensity);
                QueueComposerDensityRefresh();
            }
        }

        private bool _compactToolbar;

        /// <summary>
        /// "Compact chat toolbar" (Settings → Native mode): the action row moves inside the input box, under
        /// the text, the way most chat apps lay it out. The session actions (↻ ✚ ✎ 🎨 📎) stay folded
        /// into the ⋯ menu at its left, and ⚡ ☰ ⚙ move to its right end. Only while the composer is
        /// <see cref="ComposerMode.Full"/>: docked in the panel there is no input box to host the row.
        /// </summary>
        public bool CompactToolbar
        {
            get { return _compactToolbar; }
            set
            {
                if (_compactToolbar == value)
                {
                    return;
                }

                _compactToolbar = value;
                ApplyToolbarLayout();
                ApplyComposerDensity(_composerDensity);
                QueueComposerDensityRefresh();
            }
        }

        private bool IsCompactToolbarActive => _compactToolbar && _composerMode == ComposerMode.Full;

        /// <summary>Moves the action row between its own row above the input box and the box's bottom row.</summary>
        private void ApplyToolbarLayout()
        {
            bool compact = IsCompactToolbarActive;
            Panel target = compact ? (Panel)ComposerInputGrid : ComposerLayout;

            if (ComposerActionRow.Parent is Panel current && !ReferenceEquals(current, target))
            {
                current.Children.Remove(ComposerActionRow);
                target.Children.Add(ComposerActionRow);
            }

            Grid.SetRow(ComposerActionRow, 1);
            Grid.SetColumnSpan(ComposerActionRow, compact ? 2 : 1);
            ComposerActionRow.Margin = compact ? new Thickness(-4, 4, -4, 0) : new Thickness(0, 0, 0, 4);

            // Stretched, so the mirrors docked right land at the box's right edge; the classic row
            // keeps its left alignment (see the note on ComposerActionsScroller in the XAML).
            ComposerActionsScroller.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            DockPanel.SetDock(ComposerMirrorGroup, compact ? Dock.Right : Dock.Left);

            // ✚ leads the compact row; the classic row keeps it after ↻, where it has always been.
            int newChatIndex = compact ? 0 : ComposerEssentialGroup.Children.IndexOf(ComposerClearButton) + 1;
            if (ComposerEssentialGroup.Children.IndexOf(ComposerNewChatButton) != newChatIndex)
            {
                ComposerEssentialGroup.Children.Remove(ComposerNewChatButton);
                newChatIndex = compact ? 0 : ComposerEssentialGroup.Children.IndexOf(ComposerClearButton) + 1;
                ComposerEssentialGroup.Children.Insert(newChatIndex, ComposerNewChatButton);
            }
            ComposerMirrorSeparator.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// True when this composer's own text input is the live prompt (<see cref="ComposerMode.Full"/>).
        /// In ActionsOnly the panel's prompt box is the one being typed into.
        /// </summary>
        public bool HasComposerInput => _composerMode == ComposerMode.Full;

        /// <summary>Combines the Full-only gate above with the "menu would be empty" gate below.</summary>
        private void UpdateToolsButtonVisibility()
        {
            ComposerToolsButton.Visibility = _composerMode == ComposerMode.Full && _toolsMenuHasItems
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>Combines the Full-only gate above with the "no commands configured" gate below.</summary>
        private void UpdateCustomCommandsButtonVisibility()
        {
            ComposerCustomCommandsButton.Visibility = _composerMode == ComposerMode.Full && _customCommandsHasItems
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>Captions of the agent / model / effort / permission selectors.</summary>
        public void SetSelectorLabels(string provider, string model, string effort, string permission)
        {
            _providerLabel = provider ?? L("Agent");
            _modelLabel = model ?? L("Model");
            _effortLabel = effort ?? L("Effort");
            _permissionLabel = permission ?? L("Permissions");

            // A longer caption can be what pushes the row past the edge, so the tier is re-picked here
            // rather than only on resize — switching from "Ask" to "Skip permissions" is a resize as far
            // as this row is concerned.
            ApplyComposerDensity(_composerDensity);
            QueueComposerDensityRefresh();
        }

        /// <summary>
        /// The composer button a selector's menu should hang off, or null when there is nothing to hang
        /// it on — native mode is off (<see cref="ComposerMode.Hidden"/>) or that particular selector
        /// does not apply to the running agent. Resolves in <see cref="ComposerMode.ActionsOnly"/> too,
        /// since the action row stays visible there. Used by the typed slash commands, which open the
        /// same menus the buttons do; the caller falls back to its own anchor on null.
        /// </summary>
        public UIElement GetSelectorAnchor(ChatSelector selector)
        {
            if (ComposerBar.Visibility != Visibility.Visible)
            {
                return null;
            }

            Button button;
            switch (selector)
            {
                case ChatSelector.Provider: button = ComposerProviderButton; break;
                case ChatSelector.Model: button = ComposerModelButton; break;
                case ChatSelector.Effort: button = ComposerEffortButton; break;
                default: button = ComposerPermissionButton; break;
            }

            return button != null && button.Visibility == Visibility.Visible ? button : null;
        }

        /// <summary>
        /// Enables or hides the selectors that do not apply to the running agent. The agent selector
        /// is hidden when its menu would offer no choice (a single agent enabled).
        /// </summary>
        public void SetSelectorAvailability(bool provider, bool model, bool effort, bool permission)
        {
            ComposerProviderButton.Visibility = provider ? Visibility.Visible : Visibility.Collapsed;
            ComposerModelButton.Visibility = model ? Visibility.Visible : Visibility.Collapsed;
            ComposerEffortButton.Visibility = effort ? Visibility.Visible : Visibility.Collapsed;
            ComposerPermissionButton.Visibility = permission ? Visibility.Visible : Visibility.Collapsed;

            // Fewer selectors may mean the full captions fit again, so this can widen the row as well
            // as narrow it — the refresh always starts from the roomiest tier.
            QueueComposerDensityRefresh();
        }

        #region Composer density

        /// <summary>
        /// How much room the composer's action row is willing to spend on itself. Ordered widest
        /// first: <see cref="RefreshComposerDensity"/> walks down the list and stops at the first tier
        /// that fits, so a tab that is later widened climbs straight back to <see cref="Full"/>.
        /// </summary>
        private enum ComposerDensity
        {
            /// <summary>Full captions, roomy padding — what a docked-wide or floating tab shows.</summary>
            Full,

            /// <summary>Shortened captions ("Skip" for "Skip permissions") and tighter button padding.</summary>
            Compact,

            /// <summary>Compact, plus ↻/✚/✎/🎨 folded into the single "⋯" menu.</summary>
            Tight
        }

        private static readonly ComposerDensity[] _composerDensityOrder =
        {
            ComposerDensity.Full,
            ComposerDensity.Compact,
            ComposerDensity.Tight
        };

        private ComposerDensity _composerDensity = ComposerDensity.Full;
        private bool _composerDensityRefreshQueued;

        private string _providerLabel = L("Agent");
        private string _modelLabel = L("Model");
        private string _effortLabel = L("Effort");
        private string _permissionLabel = L("Permissions");

        // No vertical padding, matching ComposerSelectorStyle: the buttons are a fixed 22 tall, and
        // padding there only shrinks the content slot below the glyph's own line height — which WPF
        // answers by top-aligning the content and clipping what hangs out the bottom.
        private static readonly Thickness _composerFullPadding = new Thickness(7, 0, 7, 0);
        private static readonly Thickness _composerTightPadding = new Thickness(4, 0, 4, 0);

        private void ComposerActionRow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged)
            {
                return;
            }

            QueueComposerDensityRefresh();
        }

        /// <summary>
        /// Defers the tier decision to the end of the current layout pass. The refresh measures the
        /// button row itself, which cannot be done from inside the arrange it would be reacting to, and
        /// several of its triggers (labels, availability, mode) fire in a burst when the agent changes
        /// — the queue collapses those into one pass.
        /// </summary>
        private void QueueComposerDensityRefresh()
        {
            if (_composerDensityRefreshQueued)
            {
                return;
            }

            _composerDensityRefreshQueued = true;

            // Loaded priority so the row has been arranged (and therefore has an ActualWidth to
            // measure against) before the tier is picked. The VS threading helpers cannot express a
            // dispatcher priority, so the WPF dispatcher is the right tool here — every caller is
            // already on the UI thread.
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _composerDensityRefreshQueued = false;
                RefreshComposerDensity();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
#pragma warning restore VSTHRD001, VSTHRD110
        }

        /// <summary>
        /// Picks the widest density tier whose essential group (session actions + selectors) still fits
        /// the row, so the controls that drive the agent stay readable in a narrow tab instead of being
        /// clipped off the edge. The mirrored config buttons are deliberately not measured: they sit
        /// after the essentials in the same scroller and are meant to be what scrolls out of view, so
        /// promoting a row of toolbar buttons must not cost the selectors their captions.
        /// </summary>
        private void RefreshComposerDensity()
        {
            if (ComposerBar.Visibility != Visibility.Visible)
            {
                return;
            }

            double available = ComposerActionRow.ActualWidth - 2;
            if (available <= 0)
            {
                return;
            }

            foreach (ComposerDensity density in _composerDensityOrder)
            {
                ApplyComposerDensity(density);

                // Unconstrained width on purpose: the group's own natural width is what has to be
                // compared against the row, and the ScrollViewer above it measures with infinity too,
                // so this asks exactly the question the scroller will answer.
                ComposerEssentialGroup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                if (ComposerEssentialGroup.DesiredSize.Width <= available)
                {
                    break;
                }
            }

            // The measurements above ran against a constraint the layout system did not ask for; drop
            // them so the next real pass measures the group the way its parent wants it.
            ComposerEssentialGroup.InvalidateMeasure();
        }

        private void ApplyComposerDensity(ComposerDensity density)
        {
            _composerDensity = density;

            bool full = density == ComposerDensity.Full;
            int maxChars = density == ComposerDensity.Compact
                ? ComposerLabels.CompactMaxChars
                : ComposerLabels.TightMaxChars;

            ApplySelectorCaption(ComposerProviderButton, _providerLabel, L("Change the AI agent"), full, maxChars);
            ApplySelectorCaption(ComposerModelButton, _modelLabel, L("Change the model"), full, maxChars);
            ApplySelectorCaption(ComposerEffortButton, _effortLabel, L("Change the effort level"), full, maxChars);
            ApplySelectorCaption(ComposerPermissionButton, _permissionLabel, L("Change how tool permissions are handled"), full, maxChars);

            Thickness padding = full ? _composerFullPadding : _composerTightPadding;
            ComposerOverflowButton.Padding = padding;
            ComposerClearButton.Padding = padding;
            ComposerNewChatButton.Padding = padding;
            ComposerRenameSessionButton.Padding = padding;
            ComposerColorButton.Padding = padding;
            ComposerAttachButton.Padding = padding;
            ComposerProviderButton.Padding = padding;
            ComposerModelButton.Padding = padding;
            ComposerEffortButton.Padding = padding;
            ComposerPermissionButton.Padding = padding;

            // The five session actions are the only controls here with an equivalent elsewhere in the
            // UI (the ⋯ menu, the tab's own context menu), which is why they are what gives way before
            // the selectors do.
            bool folded = density == ComposerDensity.Tight || IsCompactToolbarActive;
            Visibility unfolded = folded ? Visibility.Collapsed : Visibility.Visible;

            ComposerOverflowButton.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
            ComposerClearButton.Visibility = unfolded;
            // The compact toolbar keeps ✚ out of the ⋯ menu: a new chat is the one session action used
            // often. Only the tightest tier folds it as well.
            ComposerNewChatButton.Visibility = _showNewChatAction
                                               && (!folded || (IsCompactToolbarActive && density != ComposerDensity.Tight))
                ? Visibility.Visible
                : Visibility.Collapsed;
            ComposerRenameSessionButton.Visibility = unfolded;
            ComposerColorButton.Visibility = unfolded;
            ComposerAttachButton.Visibility = unfolded;
            ComposerActionsSeparator.Visibility = unfolded;
        }

        /// <summary>
        /// Sets one selector's caption at the current tier, and — when the caption had to be shortened
        /// — spells the full value out in the tooltip, so a truncated model id is never the only place
        /// the selection is shown.
        /// </summary>
        private static void ApplySelectorCaption(Button button, string label, string tooltip, bool full, int maxChars)
        {
            string caption = full ? label : ComposerLabels.Shorten(label, maxChars);

            button.Content = caption + " ▾";
            button.ToolTip = string.Equals(caption, label, StringComparison.Ordinal)
                ? tooltip
                : tooltip + " (" + label + ")";
        }

        #endregion

        /// <summary>
        /// Mirrors the panel's own "collapse ☰ when its dropdown would be empty" rule
        /// (<c>RefreshToolbarLayout</c>'s <c>anyInDropdown</c>) onto the composer's ☰ mirror, so the
        /// chat tab never offers a menu with nothing in it.
        /// </summary>
        public void SetToolsMenuHasItems(bool hasItems)
        {
            _toolsMenuHasItems = hasItems;
            UpdateToolsButtonVisibility();
        }

        /// <summary>
        /// Mirrors the panel's own "hide ⚡ when no custom commands are configured" rule
        /// (<c>RefreshCustomCommandsButton</c>'s <c>hasAny</c>) onto the composer's ⚡ mirror, so the
        /// chat tab never offers an empty custom-commands menu (issue #151 round 7).
        /// </summary>
        public void SetCustomCommandsMenuHasItems(bool hasItems)
        {
            _customCommandsHasItems = hasItems;
            UpdateCustomCommandsButtonVisibility();
        }

        /// <summary>
        /// The composer button a config menu should hang off, or null while the composer is
        /// <see cref="ComposerMode.Hidden"/>. Same shape as <see cref="GetSelectorAnchor"/>.
        /// </summary>
        public UIElement GetConfigMenuAnchor(ChatConfigMenu menu)
        {
            if (ComposerBar.Visibility != Visibility.Visible)
            {
                return null;
            }

            Button button;
            switch (menu)
            {
                case ChatConfigMenu.Tools: button = ComposerToolsButton; break;
                case ChatConfigMenu.CustomCommands: button = ComposerCustomCommandsButton; break;
                default: button = ComposerSettingsButton; break;
            }

            return button != null && button.Visibility == Visibility.Visible ? button : null;
        }

        /// <summary>
        /// The mirrored promoted-toolbar button with the given id, or null while the composer is
        /// <see cref="ComposerMode.Hidden"/> or no button with that id was passed to
        /// <see cref="SetPromotedButtons"/>.
        /// </summary>
        public UIElement GetPromotedButtonAnchor(string id)
        {
            if (ComposerBar.Visibility != Visibility.Visible
                || ComposerPromotedButtons.Visibility != Visibility.Visible
                || string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (Button button in ComposerPromotedButtons.Children)
            {
                if (id.Equals(button.Tag as string, StringComparison.Ordinal))
                {
                    return button;
                }
            }

            return null;
        }

        /// <summary>
        /// Rebuilds the mirrored promoted-toolbar buttons from the panel's own set. Each entry's
        /// content and tooltip are copied from the panel button that owns it, so there is only one
        /// place (<c>RefreshToolbarLayout</c>) that decides the glyph or wording — this never drifts
        /// out of sync with what the panel itself shows.
        /// </summary>
        /// <summary>The composer's own button for a toolbar id passed to <see cref="SetPromotedButtons"/>, if any.</summary>
        private Button OwnButton(string id)
        {
            switch (id)
            {
                case "CustomCommands": return ComposerCustomCommandsButton;
                case "ToolsMenu": return ComposerToolsButton;
                case "Settings": return ComposerSettingsButton;
                case "SessionHistory": return ComposerSessionHistoryButton;
                default: return null;
            }
        }

        private string OwnButtonId(Button button)
        {
            if (ReferenceEquals(button, ComposerCustomCommandsButton)) return "CustomCommands";
            if (ReferenceEquals(button, ComposerToolsButton)) return "ToolsMenu";
            if (ReferenceEquals(button, ComposerSettingsButton)) return "Settings";
            if (ReferenceEquals(button, ComposerSessionHistoryButton)) return "SessionHistory";
            return null;
        }

        public void SetPromotedButtons(IEnumerable<(string Id, object Content, object ToolTip)> buttons)
        {
            foreach (Button old in ComposerPromotedButtons.Children)
            {
                if (OwnButtonId(old) == null)
                {
                    _classicIconContent.Remove(old);
                }
            }

            ComposerPromotedButtons.Children.Clear();

            if (buttons == null)
            {
                // No order from the panel (terminal mode): the composer's own buttons in their default order.
                ComposerPromotedButtons.Children.Add(ComposerCustomCommandsButton);
                ComposerPromotedButtons.Children.Add(ComposerToolsButton);
                ComposerPromotedButtons.Children.Add(ComposerSettingsButton);
                ComposerPromotedButtons.Children.Add(ComposerSessionHistoryButton);
                return;
            }

            foreach (var entry in buttons)
            {
                // ⚡ ☰ ⚙ 🕘 are this view's own buttons: the entry only says where they go.
                Button own = OwnButton(entry.Id);
                if (own != null)
                {
                    ComposerPromotedButtons.Children.Add(own);
                    continue;
                }

                var button = new Button
                {
                    Style = (Style)FindResource("ComposerSelectorStyle"),
                    Content = entry.Content,
                    ToolTip = entry.ToolTip,
                    FontSize = 13,
                    Tag = entry.Id
                };
                button.Click += (s, e) => PromotedButtonClicked?.Invoke(this, ((Button)s).Tag as string);
                ComposerPromotedButtons.Children.Add(button);

                if (_modernIcons && TryGetPromotedMoniker(entry.Id, out ImageMoniker moniker))
                {
                    SetButtonIcon(button, moniker);
                }
            }
        }

        #region Icon style

        private bool _modernIcons;

        /// <summary>The glyph each button had before the Modern style replaced it, to put it back.</summary>
        private readonly Dictionary<Button, object> _classicIconContent = new Dictionary<Button, object>();

        /// <summary>
        /// Modern icon style: Visual Studio's own icons on the composer toolbar and a rotating ring as
        /// the live spinner, instead of the Unicode/emoji glyphs. The XAML keeps the glyphs, so Classic
        /// is simply the state the view is built in.
        /// </summary>
        public bool UseModernIcons
        {
            get { return _modernIcons; }
            set
            {
                if (_modernIcons == value)
                {
                    return;
                }

                _modernIcons = value;

                // A running tool card shows the ring instead of the ◐◓◑◒ glyph, like the status line.
                Resources["ClassicSpinnerVisibility"] = value ? Visibility.Collapsed : Visibility.Visible;
                Resources["ModernSpinnerVisibility"] = value ? Visibility.Visible : Visibility.Collapsed;

                foreach (var (button, moniker) in ModernToolbarIcons())
                {
                    SetButtonIcon(button, moniker);
                }

                foreach (Button button in ComposerPromotedButtons.Children)
                {
                    if (TryGetPromotedMoniker(button.Tag as string, out ImageMoniker moniker))
                    {
                        SetButtonIcon(button, moniker);
                    }
                }

                ApplyFloatingButtonsStyle();

                if (_activityTimer != null && _activityTimer.IsEnabled)
                {
                    ShowActivitySpinner();
                }
            }
        }

        private object _classicStopContent;
        private object _classicScrollToEndContent;
        private TextBlock _modernStopLabel;

        /// <summary>
        /// Stop and "jump to the latest message": flat Stop with the VS icon and a round floating
        /// jump button in Modern; the plain buttons the XAML declares in Classic.
        /// </summary>
        private void ApplyFloatingButtonsStyle()
        {
            if (_modernIcons)
            {
                // The local value, not the content: Classic's caption is a language binding to put back.
                _classicStopContent = StopButton.ReadLocalValue(ContentControl.ContentProperty);
                _classicScrollToEndContent = ScrollToEndButton.ReadLocalValue(ContentControl.ContentProperty);

                var stopContent = new StackPanel { Orientation = Orientation.Horizontal };
                stopContent.Children.Add(new CrispImage
                {
                    Moniker = KnownMonikers.Stop,
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                _modernStopLabel = new TextBlock { Text = L("Stop"), VerticalAlignment = VerticalAlignment.Center };
                stopContent.Children.Add(_modernStopLabel);

                StopButton.Style = (Style)FindResource("ModernStopButtonStyle");
                StopButton.Content = stopContent;

                // The XAML sizes it for the Classic glyph (26); the round button wants more room.
                ScrollToEndButton.Style = (Style)FindResource("ModernScrollToEndButtonStyle");
                ScrollToEndButton.Width = 34;
                ScrollToEndButton.Height = 34;
                ScrollToEndButton.Content = new CrispImage { Moniker = KnownMonikers.GoToBottom, Width = 18, Height = 18 };
            }
            else if (_classicStopContent != null)
            {
                StopButton.Style = null;
                RestoreLocalValue(StopButton, ContentControl.ContentProperty, _classicStopContent);
                ScrollToEndButton.Style = null;
                ScrollToEndButton.Width = 26;
                ScrollToEndButton.Height = 26;
                RestoreLocalValue(ScrollToEndButton, ContentControl.ContentProperty, _classicScrollToEndContent);
                _classicStopContent = null;
                _classicScrollToEndContent = null;
                _modernStopLabel = null;
            }
        }

        /// <summary>Puts back a value read with <c>ReadLocalValue</c>, a binding as a binding.</summary>
        private static void RestoreLocalValue(DependencyObject target, DependencyProperty property, object saved)
        {
            if (saved is BindingExpressionBase expression)
            {
                BindingOperations.SetBinding(target, property, expression.ParentBindingBase);
            }
            else if (saved == DependencyProperty.UnsetValue)
            {
                target.ClearValue(property);
            }
            else
            {
                target.SetValue(property, saved);
            }
        }

        private IEnumerable<(Button Button, ImageMoniker Moniker)> ModernToolbarIcons()
        {
            yield return (ComposerOverflowButton, KnownMonikers.Ellipsis);
            yield return (ComposerClearButton, KnownMonikers.Restart);
            yield return (ComposerNewChatButton, KnownMonikers.Add);
            yield return (ComposerRenameSessionButton, KnownMonikers.Rename);
            yield return (ComposerColorButton, KnownMonikers.ColorPalette);
            yield return (ComposerAttachButton, KnownMonikers.Attach);
            yield return (ComposerCustomCommandsButton, KnownMonikers.Event);
            yield return (ComposerToolsButton, KnownMonikers.BulletList);
            yield return (ComposerSettingsButton, KnownMonikers.Settings);
            yield return (ComposerSessionHistoryButton, KnownMonikers.History);
            yield return (ComposerSendButton, KnownMonikers.Send);
        }

        /// <summary>The Modern icon of a promoted toolbar button, by its <c>ToolbarButton</c> id.</summary>
        private static bool TryGetPromotedMoniker(string id, out ImageMoniker moniker)
        {
            switch (id)
            {
                case "UpdateAgent": moniker = KnownMonikers.Download; return true;
                case "DetachTerminal": moniker = KnownMonikers.Undock; return true;
                case "ViewChanges": moniker = KnownMonikers.Diff; return true;
                case "SessionHistory": moniker = KnownMonikers.History; return true;
                case "ShowUsage": moniker = KnownMonikers.BarChart; return true;
                case "SetWorkingDirectory": moniker = KnownMonikers.FolderOpened; return true;
                case "SendBuildErrors": moniker = KnownMonikers.BuildErrorList; return true;
                case "GenerateCommitMessage": moniker = KnownMonikers.Commit; return true;
                case "GenerateCommitMessageAndPush": moniker = KnownMonikers.Push; return true;
                case "RecommendModel": moniker = KnownMonikers.IntellisenseLightBulb; return true;
                case "GitHubIssues": moniker = KnownMonikers.Bug; return true;
                default: moniker = default(ImageMoniker); return false;
            }
        }

        private void SetButtonIcon(Button button, ImageMoniker moniker)
        {
            if (_modernIcons)
            {
                if (!_classicIconContent.ContainsKey(button))
                {
                    _classicIconContent[button] = button.Content;
                }

                button.Content = new CrispImage { Moniker = moniker, Width = 16, Height = 16 };
            }
            else if (_classicIconContent.TryGetValue(button, out object classic))
            {
                button.Content = classic;
                _classicIconContent.Remove(button);
            }
        }

        /// <summary>
        /// Tells the VS image service what the icons sit on, so they are inverted for a dark theme
        /// the way VS's own toolbars are. Inherited by every CrispImage in the view.
        /// </summary>
        private void ApplyImageThemeBackground()
        {
            try
            {
                if (TryFindResource(VsBrushes.ToolWindowBackgroundKey) is SolidColorBrush background)
                {
                    ImageThemingUtilities.SetImageBackgroundColor(this, background.Color);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Chat icon theme background failed: {ex.Message}");
            }
        }

        /// <summary>Shows the spinner of the current icon style next to the live status text.</summary>
        private void ShowActivitySpinner()
        {
            StatusSpinner.Visibility = _modernIcons ? Visibility.Collapsed : Visibility.Visible;
            StatusRingSpinner.Visibility = _modernIcons ? Visibility.Visible : Visibility.Collapsed;

            if (_modernIcons)
            {
                var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
            }
            else
            {
                StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        private void HideActivitySpinner()
        {
            StatusSpinner.Visibility = Visibility.Collapsed;
            StatusRingSpinner.Visibility = Visibility.Collapsed;
            StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        #endregion

        public void FocusComposer()
        {
            // In ActionsOnly the text input is collapsed (the panel's own prompt box is still the one
            // being typed into), so there is nothing here to focus.
            if (_composerMode != ComposerMode.Full) return;

            ComposerInput.Focus();
            ComposerInput.CaretIndex = ComposerInput.Text.Length;
        }

        /// <summary>
        /// Shows or hides the Stop button. Called when a turn starts and when it completes.
        /// </summary>
        public void SetBusy(bool busy)
        {
            StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Text on the footer line (token counts, cost, "working…"). Empty hides the bar.
        /// <para>
        /// Stops the live clock: this is the "here is a fixed sentence" entry point, and leaving the
        /// timer running would overwrite that sentence a second later.
        /// </para>
        /// </summary>
        public void SetStatus(string text)
        {
            StopActivityTimer();

            if (string.IsNullOrEmpty(text))
            {
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = string.Empty;
                return;
            }

            StatusText.Text = text;
            StatusBar.Visibility = Visibility.Visible;
        }

        #region Live turn clock

        /// <summary>
        /// Spinner frames. Geometric Shapes rather than the braille cells most CLI spinners use: braille
        /// is absent from Segoe UI and only renders through a fallback font, which lands at a different
        /// size and baseline than the text beside it.
        /// </summary>
        private static readonly string[] SpinnerFrames = { "◐", "◓", "◑", "◒" };

        /// <summary>
        /// What the agent is said to be doing while it works. Rotating wording is the point: an
        /// unchanging line reads as frozen, and the clock alone does not say the agent is still alive.
        /// English keys, translated at render time (<see cref="RenderActivity"/>), so a language change
        /// needs no rebuild of the list.
        /// </summary>
        private static readonly string[] WorkingVerbs =
        {
            N("Working…"), N("Thinking…"), N("Pondering…"), N("Perusing…"), N("Noodling…"), N("Percolating…"),
            N("Crunching…"), N("Mulling…"), N("Tinkering…"), N("Digging…"), N("Puzzling…"), N("Simmering…"),
            N("Cogitating…"), N("Whirring…"), N("Churning…"), N("Deliberating…")
        };

        /// <summary>The one caption shown while the agent works when <see cref="PlainWorkingStatus"/> is on.</summary>
        private static readonly string PlainWorkingVerb = N("Thinking…");

        /// <summary>
        /// "Plain working status" setting: a fixed "Thinking…" instead of the rotating verbs. Shared by
        /// every chat, like the language; the spinner and the clock still show the agent is alive.
        /// </summary>
        public static bool PlainWorkingStatus { get; set; }

        private static readonly Random VerbPicker = new Random();

        /// <summary>Frame rate of the spinner. Fast enough to read as motion, cheap enough to ignore.</summary>
        private static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(120);

        /// <summary>How long each verb stays up before the next one.</summary>
        private static readonly TimeSpan VerbInterval = TimeSpan.FromSeconds(4);

        /// <summary>Prompts accepted while the current turn is running and waiting for their turn.</summary>
        private int _queuedMessageCount;

        /// <summary>
        /// Starts the spinner and the running clock on the status line. A long turn otherwise shows a
        /// motionless "Working..." and gives no way to tell progress from a hang.
        /// </summary>
        public void BeginActivity()
        {
            _activityLabel = string.Empty;
            _activityDetail = string.Empty;
            _activityStartedUtc = DateTime.UtcNow;
            _lastVerbChangeUtc = DateTime.UtcNow;
            _spinnerFrame = 0;
            _verbIndex = VerbPicker.Next(WorkingVerbs.Length);

            if (_activityTimer == null)
            {
                _activityTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                {
                    Interval = SpinnerInterval
                };
                _activityTimer.Tick += delegate { AdvanceActivity(); };
            }

            _activityTimer.Start();
            StatusBar.Visibility = Visibility.Visible;
            ShowActivitySpinner();
            RenderActivity();
        }

        /// <summary>
        /// Pins a fixed caption — "Waiting for your answer...", "Stopping..." — in place of the rotating
        /// verbs. The spinner and the clock keep running: the turn is still open.
        /// </summary>
        public void SetActivityLabel(string label)
        {
            if (_activityTimer == null || !_activityTimer.IsEnabled)
            {
                return;
            }

            _activityLabel = label ?? string.Empty;
            RenderActivity();
        }

        /// <summary>Live token counts, appended after the elapsed time. Empty removes them.</summary>
        public void SetActivityDetail(string detail)
        {
            if (_activityTimer == null || !_activityTimer.IsEnabled)
            {
                return;
            }

            _activityDetail = detail ?? string.Empty;
            RenderActivity();
        }

        /// <summary>
        /// Shows how many follow-up messages are waiting without replacing the live token counts.
        /// </summary>
        public void SetQueuedMessageCount(int count)
        {
            _queuedMessageCount = Math.Max(0, count);

            if (_activityTimer != null && _activityTimer.IsEnabled)
            {
                RenderActivity();
            }
        }

        /// <summary>Stops the clock and leaves the turn's final summary on the line.</summary>
        public void EndActivity(string summary)
        {
            SetStatus(summary);
        }

        /// <summary>How long the current turn has been running. Zero when none is.</summary>
        public TimeSpan ActivityElapsed
        {
            get
            {
                return _activityTimer != null && _activityTimer.IsEnabled
                    ? DateTime.UtcNow - _activityStartedUtc
                    : TimeSpan.Zero;
            }
        }

        /// <summary>One animation frame: spin, and swap the verb when its turn is up.</summary>
        private void AdvanceActivity()
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;

            // Only while no fixed caption is pinned — "Waiting for your answer..." must not drift into
            // "Percolating..." while the user reads the question it is asking about.
            if (_activityLabel.Length == 0 && DateTime.UtcNow - _lastVerbChangeUtc >= VerbInterval)
            {
                _verbIndex = (_verbIndex + 1) % WorkingVerbs.Length;
                _lastVerbChangeUtc = DateTime.UtcNow;
            }

            RenderActivity();

            // Piggyback on the same tick to animate any still-running tool row (e.g. a subagent Task
            // card, which can sit for minutes) instead of a static "still running" glyph. Every tool
            // call happens within a BeginActivity/EndActivity window, so this timer is always live
            // whenever a row can be running.
            foreach (var message in Messages)
            {
                if (message.IsRunning)
                {
                    message.AdvanceSpinner();
                }
            }
        }

        private void RenderActivity()
        {
            StatusSpinner.Text = SpinnerFrames[_spinnerFrame];

            string label = _activityLabel.Length > 0
                ? _activityLabel
                : L(PlainWorkingStatus ? PlainWorkingVerb : WorkingVerbs[_verbIndex]);
            string text = label + "  " + ChatFormatting.Duration(DateTime.UtcNow - _activityStartedUtc);

            if (!string.IsNullOrEmpty(_activityDetail))
            {
                text += "  ·  " + _activityDetail;
            }

            if (_queuedMessageCount > 0)
            {
                text += "  ·  " + (_queuedMessageCount == 1
                    ? L("1 message queued")
                    : L("{0} messages queued", _queuedMessageCount));
            }

            StatusText.Text = text;
        }

        private void StopActivityTimer()
        {
            if (_activityTimer != null)
            {
                _activityTimer.Stop();
            }

            HideActivitySpinner();
        }

        #endregion

        public void Clear()
        {
            StopTracking();
            StopActivityTimer();
            Messages.Clear();
            _autoScroll = true;
            _readerTookOver = false;
            _readerSettleTimer?.Stop();
            _queuedMessageCount = 0;
            ScrollToEndButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Collapsed;

            // The card belongs to a conversation, not to the view: whoever clears the transcript is
            // about to decide whether a new one is starting, and calls ShowWelcome if it is.
            HideWelcome();
        }

        #region Welcome card

        /// <summary>
        /// Fills and shows the card a fresh conversation opens on: what agent is running, against which
        /// folder, and what the chat can do. Calling it again replaces the content, which is how the
        /// caller fills in details that only arrive later (the CLI version, for one).
        /// </summary>
        /// <param name="title">Caption on the frame, e.g. <c>Claude Code v2.1.220</c>.</param>
        /// <param name="facts">Lines under the mascot: model, effort, permissions, workspace.</param>
        /// <param name="tips">Right-hand column, one line each.</param>
        public void ShowWelcome(string title, IEnumerable<string> facts, IEnumerable<string> tips)
        {
            WelcomeTitle.Text = string.IsNullOrWhiteSpace(title) ? L("Chat") : title;
            WelcomeFacts.ItemsSource = ToLines(facts);
            WelcomeTips.ItemsSource = ToLines(tips);
            WelcomeBanner.Visibility = Visibility.Visible;
            QueueWelcomeLayoutRefresh();
        }

        private static readonly Thickness _welcomeLeftSideBySideMargin = new Thickness(6, 0, 14, 0);
        private static readonly Thickness _welcomeStackedMargin = new Thickness(6, 0, 6, 0);

        private bool? _welcomeStacked;
        private bool _welcomeLayoutRefreshQueued;

        private void WelcomeBannerContent_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Height changes count too: a zoom or new content changes how tall the side-by-side card
            // would be. Re-running is idempotent, so the flip's own resize settles after one pass.
            QueueWelcomeLayoutRefresh();
        }

        /// <summary>
        /// Defers the decision past the current layout pass — it measures the card's panels, which
        /// cannot be done from inside the arrange it reacts to — and collapses bursts into one pass.
        /// </summary>
        private void QueueWelcomeLayoutRefresh()
        {
            if (_welcomeLayoutRefreshQueued)
            {
                return;
            }

            _welcomeLayoutRefreshQueued = true;

            // Loaded priority so the card has been arranged and has an ActualWidth. The VS threading
            // helpers cannot express a dispatcher priority; every caller is already on the UI thread.
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _welcomeLayoutRefreshQueued = false;
                RefreshWelcomeLayout();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
#pragma warning restore VSTHRD001, VSTHRD110
        }

        /// <summary>
        /// Moves the tips under the facts once, beside the left column, they would be more than
        /// <see cref="WelcomeCardLayout.StackHeightRatio"/> times as tall as that column is wide. The
        /// left column keeps its natural width either way; what is measured is
        /// the side-by-side layout, never the current one (see <see cref="WelcomeCardLayout.ShouldStack"/>).
        /// </summary>
        private void RefreshWelcomeLayout()
        {
            double cardWidth = WelcomeBannerContent.ActualWidth;
            if (WelcomeBanner.Visibility != Visibility.Visible || cardWidth <= 0)
            {
                return;
            }

            // Unconstrained, as the Auto column measures it side by side; DesiredSize carries the
            // current margins, which are swapped for the side-by-side ones.
            Thickness leftMargin = WelcomeLeftPanel.Margin;
            WelcomeLeftPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double leftWidth = WelcomeLeftPanel.DesiredSize.Width
                - leftMargin.Left - leftMargin.Right
                + _welcomeLeftSideBySideMargin.Left + _welcomeLeftSideBySideMargin.Right;
            double tipsHeight = 0;
            double tipsWidth = WelcomeCardLayout.TipsWidth(cardWidth, leftWidth);
            if (tipsWidth > 0)
            {
                Thickness tipsMargin = WelcomeTipsPanel.Margin;
                WelcomeTipsPanel.Measure(new Size(tipsWidth + tipsMargin.Left + tipsMargin.Right, double.PositiveInfinity));
                tipsHeight = WelcomeTipsPanel.DesiredSize.Height - tipsMargin.Top - tipsMargin.Bottom;
            }

            // The probes measured outside the layout pass; the grid's next pass re-measures both
            // panels with the constraints it actually gives them.
            WelcomeBannerContent.InvalidateMeasure();

            bool stacked = WelcomeCardLayout.ShouldStack(cardWidth, leftWidth, tipsHeight,
                WelcomeRecentPanel.Visibility == Visibility.Visible);
            if (_welcomeStacked == stacked)
            {
                return;
            }

            _welcomeStacked = stacked;
            ApplyWelcomeLayout(stacked);
        }

        /// <summary>
        /// Stacked: facts on top, a horizontal rule, then the tips, each across the full card.
        /// Side by side: the three columns the XAML declares, with the vertical rule between them.
        /// </summary>
        private void ApplyWelcomeLayout(bool stacked)
        {
            Grid.SetColumnSpan(WelcomeLeftPanel, stacked ? 3 : 1);
            WelcomeLeftPanel.Margin = stacked ? _welcomeStackedMargin : _welcomeLeftSideBySideMargin;

            Grid.SetRow(WelcomeDivider, stacked ? 1 : 0);
            Grid.SetColumn(WelcomeDivider, stacked ? 0 : 1);
            Grid.SetColumnSpan(WelcomeDivider, stacked ? 3 : 1);
            WelcomeDivider.Width = stacked ? double.NaN : 1;
            WelcomeDivider.Height = stacked ? 1 : double.NaN;
            WelcomeDivider.Margin = stacked ? new Thickness(6, 10, 6, 10) : new Thickness(0, 2, 14, 2);

            Grid.SetRow(WelcomeTipsPanel, stacked ? 2 : 0);
            Grid.SetColumn(WelcomeTipsPanel, stacked ? 0 : 2);
            Grid.SetColumnSpan(WelcomeTipsPanel, stacked ? 3 : 1);
            WelcomeTipsPanel.Margin = stacked ? _welcomeStackedMargin : new Thickness(0);
        }

        /// <summary>Drops the welcome card. Idempotent.</summary>
        public void HideWelcome()
        {
            WelcomeBanner.Visibility = Visibility.Collapsed;

            // "Show tips" lasts as long as the card it was clicked on.
            _welcomeTipsExpanded = false;
            ApplyWelcomeTipsVisibility();
        }

        /// <summary>Raised by a click on a conversation in the welcome card's recent list.</summary>
        public event EventHandler<WelcomeRecentSession> WelcomeSessionRequested;

        /// <summary>Raised by the welcome card's "All sessions…" link: open Session History.</summary>
        public event EventHandler WelcomeAllSessionsRequested;

        /// <summary>Raised by the composer's 🕘 button beside ⚙: open Session History for this tab.</summary>
        public event EventHandler SessionHistoryRequested;

        private bool _welcomeTipsExpanded;

        /// <summary>
        /// Fills the welcome card's recent list. With conversations to show, they take the right-hand
        /// column and the tips fold away behind a "Show tips" link; an empty or null list brings the
        /// tips back. Independent of <see cref="ShowWelcome"/>, so a re-render of the card keeps it.
        /// </summary>
        public void SetWelcomeRecentSessions(IList<WelcomeRecentSession> sessions)
        {
            bool any = sessions != null && sessions.Count > 0;

            WelcomeRecentList.ItemsSource = any ? sessions : null;
            WelcomeRecentPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ApplyWelcomeTipsVisibility();
            QueueWelcomeLayoutRefresh();
        }

        private void ApplyWelcomeTipsVisibility()
        {
            bool hasRecent = WelcomeRecentPanel.Visibility == Visibility.Visible;

            WelcomeTipsSection.Visibility = !hasRecent || _welcomeTipsExpanded ? Visibility.Visible : Visibility.Collapsed;
            WelcomeTipsSection.Margin = hasRecent ? new Thickness(0, 10, 0, 0) : new Thickness(0);
            WelcomeTipsToggleText.Text = _welcomeTipsExpanded ? L("Hide tips") : L("Show tips");
        }

        private void WelcomeRecentSession_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is WelcomeRecentSession session)
            {
                WelcomeSessionRequested?.Invoke(this, session);
            }
        }

        private void WelcomeAllSessions_Click(object sender, RoutedEventArgs e)
        {
            WelcomeAllSessionsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void WelcomeTipsToggle_Click(object sender, RoutedEventArgs e)
        {
            _welcomeTipsExpanded = !_welcomeTipsExpanded;
            ApplyWelcomeTipsVisibility();
            QueueWelcomeLayoutRefresh();
        }

        /// <summary>
        /// True while the card is on screen. Callers that fill it in stages check this before redrawing,
        /// so a late detail cannot bring back a card the first prompt already dismissed.
        /// </summary>
        public bool IsWelcomeVisible
        {
            get { return WelcomeBanner.Visibility == Visibility.Visible; }
        }

        /// <summary>Copies the caller's sequence, skipping blanks, so the card never shows empty rows.</summary>
        private static List<string> ToLines(IEnumerable<string> source)
        {
            var lines = new List<string>();

            if (source != null)
            {
                foreach (string line in source)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }
            }

            return lines;
        }

        #endregion

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
        }

        #region Composer

        // The row scrolls horizontally but the mouse wheel is vertical by default, and there is no
        // vertical content here for it to act on — without this, hovering the action row and scrolling
        // does nothing instead of the horizontal scroll a user would expect.
        private void ComposerActionsScroller_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (ComposerActionsScroller.ScrollableWidth <= 0)
            {
                return;
            }

            ComposerActionsScroller.ScrollToHorizontalOffset(ComposerActionsScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private const double ComposerActionsScrollStep = 90;

        private void ComposerActionsScrollLeftButton_Click(object sender, RoutedEventArgs e)
        {
            ComposerActionsScroller.ScrollToHorizontalOffset(ComposerActionsScroller.HorizontalOffset - ComposerActionsScrollStep);
        }

        private void ComposerActionsScrollRightButton_Click(object sender, RoutedEventArgs e)
        {
            ComposerActionsScroller.ScrollToHorizontalOffset(ComposerActionsScroller.HorizontalOffset + ComposerActionsScrollStep);
        }

        // Small ◀/▶ buttons stand in for the scrollbar track (too heavy visually for a row this
        // thin), so this is the only signal for whether either end still has something to reach —
        // fires whenever the row's content width, viewport, or offset changes. They toggle
        // Collapsed, not Hidden: both arrow columns are Grid "Auto" width, and the middle column
        // (the scroller) is the row's only Star column, so collapsing an arrow's column only hands its
        // few pixels to the scroller — it doesn't change ComposerActionRow's own width, which is
        // already bounded by ComposerBar. Hidden was tried first, but it reserves an arrow-sized
        // blank gap next to the scroller even when that arrow isn't needed, which read as dead
        // space between the fixed selectors and the mirrored buttons (or after the last one).
        private void ComposerActionsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            bool canScroll = ComposerActionsScroller.ScrollableWidth > 0.5;
            ComposerActionsScrollLeftButton.Visibility = canScroll && ComposerActionsScroller.HorizontalOffset > 0.5
                ? Visibility.Visible
                : Visibility.Collapsed;
            ComposerActionsScrollRightButton.Visibility = canScroll && ComposerActionsScroller.HorizontalOffset < ComposerActionsScroller.ScrollableWidth - 0.5
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ComposerProviderButton_Click(object sender, RoutedEventArgs e)
        {
            // Raised with `this`, not the button — every other composer event does the same so the
            // control-level handler can resolve the owning session from the sender (v163.0); the
            // anchor button itself is recovered from the selector via GetSelectorAnchor.
            SelectorClicked?.Invoke(this, ChatSelector.Provider);
        }

        private void ComposerModelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectorClicked?.Invoke(this, ChatSelector.Model);
        }

        /// <summary>
        /// Effort keeps the pill slider the panel's model menu already uses — a dropdown list of six
        /// levels reads as a different setting than the one users know.
        /// </summary>
        private void ComposerEffortButton_Click(object sender, RoutedEventArgs e)
        {
            EffortPopup.IsOpen = !EffortPopup.IsOpen;
        }

        /// <summary>
        /// Positions the reasoning slider and captions it. <paramref name="index"/> is the slider stop
        /// the parent maps to and from the active provider's reasoning enum.
        /// </summary>
        public void SetEffortSlider(int index, string label, string title = null)
        {
            _effortTitle = string.IsNullOrWhiteSpace(title) ? L("Effort") : title;
            _suppressEffortEvent = true;
            try
            {
                EffortPopupSlider.Value = index;
            }
            finally
            {
                _suppressEffortEvent = false;
            }

            _effortIndex = index;

            // A level applied from elsewhere (panel slider, settings) becomes the baseline the next
            // popup session is compared against — but not while the user is inside the popup, where
            // the baseline is what they opened it on.
            if (!EffortPopup.IsOpen)
            {
                _effortIndexOnOpen = index;
            }

            EffortPopupLabel.Text = string.IsNullOrEmpty(label)
                ? _effortTitle
                : _effortTitle + " (" + label + ")";
            ComposerEffortButton.ToolTip = L("Change the {0} level", _effortTitle.ToLowerInvariant());
        }

        /// <summary>
        /// Captions of the slider stops, in slider order, so the popup can name the level under the
        /// thumb while the user is still moving it — the parent's caption only arrives once the level
        /// has actually been applied.
        /// </summary>
        public void SetEffortStopLabels(string[] labels)
        {
            _effortStopLabels = labels;
            EffortPopupSlider.Maximum = labels == null || labels.Length == 0
                ? 0
                : labels.Length - 1;
        }

        /// <summary>
        /// Tracks the stop and captions it, without reporting anything: the choice is reported when the
        /// popup closes, so passing through stops with the mouse or the arrow keys costs nothing.
        /// </summary>
        private void EffortPopupSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEffortEvent)
            {
                return;
            }

            _effortIndex = (int)Math.Round(e.NewValue);

            if (_effortStopLabels != null && _effortIndex >= 0 && _effortIndex < _effortStopLabels.Length)
            {
                EffortPopupLabel.Text =
                    _effortTitle + " (" + _effortStopLabels[_effortIndex] + ")";
            }
        }

        /// <summary>
        /// The popup is the whole interaction: it opens on the level in force, and only what the user
        /// leaves it on is reported.
        /// </summary>
        private void EffortPopup_Opened(object sender, EventArgs e)
        {
            _effortIndexOnOpen = _effortIndex;
        }

        private void EffortPopup_Closed(object sender, EventArgs e)
        {
            if (_effortIndex == _effortIndexOnOpen)
            {
                return;
            }

            _effortIndexOnOpen = _effortIndex;
            EffortChanged?.Invoke(this, _effortIndex);
        }

        /// <summary>
        /// Opening the popup asks the parent to refresh the Claude Usage data; the popup is filled again
        /// when the new figures arrive, so the numbers never lag the tab by more than one refresh.
        /// </summary>
        private void ComposerUsageButton_Click(object sender, RoutedEventArgs e)
        {
            UsagePopup.IsOpen = !UsagePopup.IsOpen;
            if (UsagePopup.IsOpen)
            {
                _usageRefreshing = true;
                ShowUsageUpdatedText();
                UsageRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Called by the parent once the refresh a ring click started is over — new figures arrived, or it gave
        /// up. Stops the ring beside the snapshot's "Updated" line.
        /// </summary>
        public void EndUsageRefresh()
        {
            _usageRefreshing = false;
            ShowUsageUpdatedText();
        }

        // The last snapshot's "Updated" line, with a small rotating ring beside it while a ring click's refresh is running.
        private void ShowUsageUpdatedText()
        {
            UsageUpdatedText.Text = _usageUpdatedText;
            UsageUpdatedText.Visibility = string.IsNullOrEmpty(_usageUpdatedText) ? Visibility.Collapsed : Visibility.Visible;
            UsageUpdatedRow.Visibility = _usageRefreshing || !string.IsNullOrEmpty(_usageUpdatedText)
                ? Visibility.Visible
                : Visibility.Collapsed;
            UsageRefreshSpinner.Margin = new Thickness(string.IsNullOrEmpty(_usageUpdatedText) ? 0 : 6, 0, 0, 0);

            // Started and stopped only on a change, so new figures landing mid-refresh do not restart the turn.
            bool spinning = UsageRefreshSpinner.Visibility == Visibility.Visible;
            if (_usageRefreshing == spinning)
            {
                return;
            }

            UsageRefreshSpinner.Visibility = _usageRefreshing ? Visibility.Visible : Visibility.Collapsed;
            UsageRefreshRotation.BeginAnimation(RotateTransform.AngleProperty, _usageRefreshing
                ? new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                }
                : null);
        }

        private void UsageDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            UsagePopup.IsOpen = false;
            UsageDetailsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Shows the running-subagent count of this tab's session, or hides the badge for zero.
        /// </summary>
        public void SetSubagentCount(int count, string tooltip)
        {
            Visibility previous = ComposerSubagentBadge.Visibility;
            ComposerSubagentBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (count > 0)
            {
                ComposerSubagentText.Text = "👥 " + count;
                ComposerSubagentBadge.ToolTip = tooltip;
            }

            if (previous != ComposerSubagentBadge.Visibility)
            {
                QueueComposerDensityRefresh();
            }
        }

        /// <summary>
        /// Shows the ring and fills the popup. The ring fills with the context window in use, or with the
        /// session limit when no turn has reported a context window yet. Null or not visible hides the button.
        /// </summary>
        public void SetUsageIndicator(ChatUsageIndicatorData data)
        {
            bool visible = data != null && data.Visible;
            Visibility previous = ComposerUsageButton.Visibility;
            ComposerUsageButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (previous != ComposerUsageButton.Visibility)
            {
                QueueComposerDensityRefresh();
            }

            if (!visible)
            {
                UsagePopup.IsOpen = false;
                ComposerUsageRingArc.Data = null;
                return;
            }

            int ringPercent = data.HasContext
                ? ChatUsageIndicator.ClampPercent(data.ContextPercent)
                : data.HasPlanUsage ? ChatUsageIndicator.ClampPercent(data.SessionPercent) : -1;
            ComposerUsageRingArc.Data = ringPercent < 0
                ? null
                : ChatUsageIndicator.RingArc(ringPercent / 100.0, 7, 6);
            ComposerUsageButton.ToolTip = ringPercent < 0
                ? L("Usage: context window and plan limits")
                : L("Usage: {0}% used", ringPercent);

            UsageContextText.Text = data.HasContext
                ? ChatUsageIndicator.ContextLine(data.ContextTokens, data.ContextWindow)
                : L("No turn yet");
            UsageContextBar.Value = data.HasContext ? ChatUsageIndicator.ClampPercent(data.ContextPercent) : 0;

            UsageNoPlanText.Visibility = data.HasPlanUsage ? Visibility.Collapsed : Visibility.Visible;
            UsagePlanRows.Visibility = data.HasPlanUsage ? Visibility.Visible : Visibility.Collapsed;
            if (data.HasPlanUsage)
            {
                UsageSessionLabel.Text = string.IsNullOrEmpty(data.SessionLabel) ? L("Session limit") : data.SessionLabel;
                SetPlanRow(UsageSessionText, UsageSessionBar, data.SessionPercent, true, data.SessionReset);

                SetPlanRow(UsageWeeklyText, UsageWeeklyBar, data.WeeklyPercent, data.ShowWeekly, data.WeeklyReset);
                UsageWeeklyRow.Visibility = data.ShowWeekly ? Visibility.Visible : Visibility.Collapsed;

                SetPlanRow(UsageExtraText, UsageExtraBar, data.ExtraPercent, data.ShowExtra, data.ExtraSpent, data.ExtraReset);
                UsageExtraRow.Visibility = data.ShowExtra ? Visibility.Visible : Visibility.Collapsed;
            }

            _usageUpdatedText = data.UpdatedText ?? string.Empty;
            ShowUsageUpdatedText();
        }

        /// <summary>
        /// Writes one plan row ("41% used · Resets in 2 h") and its bar. The bar is hidden here; the caller hides
        /// the row's label grid, since the bar sits outside it.
        /// </summary>
        private static void SetPlanRow(TextBlock text, ProgressBar bar, int percent, bool visible, params string[] details)
        {
            bar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                return;
            }

            text.Text = ChatUsageIndicator.PlanRowText(percent, details);
            bar.Value = ChatUsageIndicator.ClampPercent(percent);
        }

        private void ComposerClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearChatRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerNewChatButton_Click(object sender, RoutedEventArgs e)
        {
            NewChatRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerRenameSessionButton_Click(object sender, RoutedEventArgs e)
        {
            RenameSessionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerColorButton_Click(object sender, RoutedEventArgs e)
        {
            ColorPickerRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Rename this chat, as the composer's ✎ does — for the single chat window's tab menu.</summary>
        public void RequestRename()
        {
            RenameSessionRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Pick this chat's color, as the composer's color button does — for the tab menu.</summary>
        public void RequestColorPicker()
        {
            ColorPickerRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Drops the 📎 menu (attach a file / insert editor selection / insert active file path) —
        /// the composer's counterpart to the panel's AttachDropdownButton, which is hidden while
        /// the chat is in its own tab (issue #174 follow-up). Placed above, like every composer dropdown.
        /// </summary>
        private void ComposerAttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (ComposerAttachButton.ContextMenu == null)
            {
                AttachRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            ComposerAttachButton.ContextMenu.PlacementTarget = ComposerAttachButton;
            ComposerAttachButton.ContextMenu.Placement = PlacementMode.Top;
            ComposerAttachButton.ContextMenu.IsOpen = true;
        }

        private void ComposerAttachFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            AttachRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerInsertSelectionMenuItem_Click(object sender, RoutedEventArgs e)
        {
            InsertSelectionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerInsertActiveFilePathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            InsertActiveFilePathRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Drops the folded session actions (↻/✚/✎/🎨/📎) on left-click, in the tightest density tier
        /// where those five buttons are collapsed into this one. Placed above the button for the same
        /// reason every other composer dropdown is: the row sits at the bottom of the tab.
        /// </summary>
        private void ComposerOverflowButton_Click(object sender, RoutedEventArgs e)
        {
            if (ComposerOverflowButton.ContextMenu == null)
            {
                return;
            }

            ComposerOverflowButton.ContextMenu.PlacementTarget = ComposerOverflowButton;
            ComposerOverflowButton.ContextMenu.Placement = PlacementMode.Top;
            ComposerOverflowButton.ContextMenu.IsOpen = true;
        }

        private void SessionTitleBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RenameSessionRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised by the composer's color button and the title bar's swatch: pick a custom accent color for this session.</summary>
        public event EventHandler ColorPickerRequested;

        private void SessionColorSwatch_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Own handler so the click doesn't bubble up to SessionTitleBar and also open the
            // rename dialog.
            e.Handled = true;
            ColorPickerRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Shows or hides the session-name header above the transcript (null or empty hides it). Has
        /// the full width of the chat view, where the VS tab caption truncates long titles.
        /// </summary>
        public void SetSessionTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                SessionTitleBar.Visibility = Visibility.Collapsed;
                SessionTitleText.Text = string.Empty;
            }
            else
            {
                SessionTitleText.Text = title;
                SessionTitleBar.Visibility = Visibility.Visible;
            }
        }

        private static readonly Color DefaultAccentColor = Color.FromRgb(0x1C, 0x8A, 0xE0); // matches ChatAccentBrush's XAML default

        /// <summary>
        /// Applies the session's custom accent color, or resets to the default blue when
        /// <paramref name="hexColor"/> is empty or not a valid "#RRGGBB"/"#RGB". Mutates the shared
        /// <c>ChatAccentBrush</c> resource in place rather than reassigning individual elements'
        /// Foreground/Background: every element in this view that references it via StaticResource
        /// holds the same Brush instance, so changing its Color live-updates all of them at once —
        /// the welcome banner, user message bubble borders, tips list, and the session title/swatch.
        /// </summary>
        public void SetSessionTitleColor(string hexColor)
        {
            Color color = TryParseHexColor(hexColor) ?? DefaultAccentColor;
            if (FindResource("ChatAccentBrush") is SolidColorBrush accentBrush)
            {
                accentBrush.Color = color;
            }
        }

        private static Color? TryParseHexColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;

            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 3)
            {
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }
            if (hex.Length != 6) return null;

            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                return null;
            }

            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        /// <summary>
        /// Height of the prompt box, in device-independent pixels. Persisted by the parent so the
        /// composer comes back the size the user left it.
        /// </summary>
        public double ComposerHeight
        {
            get { return ComposerInput.Height; }
            set
            {
                if (double.IsNaN(value) || value <= 0) return;
                ComposerInput.Height = ClampComposerHeight(value);
            }
        }

        private void ComposerResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            // Dragging the grip up (negative delta) grows the box, because the composer is anchored
            // to the bottom of the view.
            double target = ClampComposerHeight(ComposerInput.ActualHeight - e.VerticalChange);
            if (Math.Abs(target - ComposerInput.Height) < 0.5)
            {
                return;
            }

            ComposerInput.Height = target;
            ComposerHeightChanged?.Invoke(this, target);
        }

        private double ClampComposerHeight(double value)
        {
            // Upper bound follows the window so the composer can never swallow the transcript whole.
            double max = Math.Max(MinComposerHeight, ActualHeight > 0 ? ActualHeight * 0.6 : MaxComposerHeight);
            return Math.Max(MinComposerHeight, Math.Min(max, value));
        }

        /// <summary>Smallest and largest chat font the settings dialog offers.</summary>
        public const double MinChatFontSize = 8;
        public const double MaxChatFontSize = 28;

        /// <summary>
        /// Sets the font of the whole conversation. Unlike the console font this may be proportional:
        /// the transcript is laid out by WPF, not on a character grid, so nothing comes out jumbled.
        /// <para>
        /// Applied as resources rather than as properties because the row templates need the secondary
        /// sizes (headers, badges, code) to move with the base size instead of staying at a fixed 10pt.
        /// </para>
        /// </summary>
        public void SetChatFont(string fontFamily, double fontSizePt)
        {
            double size = double.IsNaN(fontSizePt) || fontSizePt <= 0
                ? 12
                : Math.Max(MinChatFontSize, Math.Min(MaxChatFontSize, fontSizePt));

            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                try
                {
                    // The fallback keeps a face that cannot render a glyph from producing empty boxes.
                    Resources["ChatFontFamily"] = new System.Windows.Media.FontFamily(fontFamily + ", Segoe UI");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Chat font '{fontFamily}' could not be applied: {ex.Message}");
                }
            }

            Resources["ChatFontSize"] = size;
            Resources["ChatSmallFontSize"] = Math.Max(8.0, size - 1);
            Resources["ChatTinyFontSize"] = Math.Max(7.0, size - 2);
        }

        /// <summary>
        /// Zoom factor applied to the whole view. 1.0 is 100%.
        /// </summary>
        public double Zoom
        {
            get { return ZoomTransform.ScaleX; }
            set
            {
                double zoom = ClampZoom(value);
                ZoomTransform.ScaleX = zoom;
                ZoomTransform.ScaleY = zoom;
            }
        }

        /// <summary>
        /// Ctrl+Scroll zooms the entire conversation, not just one font size: the transcript mixes
        /// prose, code blocks and tool output, and scaling only one of them pulls the layout apart.
        /// </summary>
        private void Root_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control)
            {
                return;
            }

            double zoom = ClampZoom(ZoomTransform.ScaleX + (e.Delta > 0 ? ZoomStep : -ZoomStep));
            e.Handled = true;

            if (Math.Abs(zoom - ZoomTransform.ScaleX) < 0.001)
            {
                return;
            }

            ZoomTransform.ScaleX = zoom;
            ZoomTransform.ScaleY = zoom;
            ZoomChanged?.Invoke(this, zoom);
        }

        /// <summary>
        /// Ctrl+0 resets the zoom to 100%, mirroring the browser/IDE convention — Ctrl+Scroll has no
        /// symmetric "reset" gesture of its own.
        /// </summary>
        private void Root_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control)
            {
                return;
            }

            if (e.Key != System.Windows.Input.Key.D0 && e.Key != System.Windows.Input.Key.NumPad0)
            {
                return;
            }

            e.Handled = true;

            if (Math.Abs(ZoomTransform.ScaleX - 1.0) < 0.001)
            {
                return;
            }

            ZoomTransform.ScaleX = 1.0;
            ZoomTransform.ScaleY = 1.0;
            ZoomChanged?.Invoke(this, 1.0);
        }

        private static double ClampZoom(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 1.0;
            return Math.Max(MinZoom, Math.Min(MaxZoom, value));
        }

        private void ComposerPermissionButton_Click(object sender, RoutedEventArgs e)
        {
            SelectorClicked?.Invoke(this, ChatSelector.Permission);
        }

        private void ComposerSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ConfigMenuClicked?.Invoke(this, ChatConfigMenu.Settings);
        }

        private void ComposerSessionHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            SessionHistoryRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerToolsButton_Click(object sender, RoutedEventArgs e)
        {
            ConfigMenuClicked?.Invoke(this, ChatConfigMenu.Tools);
        }

        private void ComposerCustomCommandsButton_Click(object sender, RoutedEventArgs e)
        {
            ConfigMenuClicked?.Invoke(this, ChatConfigMenu.CustomCommands);
        }

        private void ComposerInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            ComposerPlaceholder.Visibility = string.IsNullOrEmpty(ComposerInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Sets the composer text with the caret at the end, so the user can immediately keep typing
        /// or delete the last few words of a recalled prompt (issue #152).
        /// </summary>
        public void SetComposerText(string text)
        {
            ComposerInput.Text = text ?? string.Empty;
            ComposerInput.CaretIndex = ComposerInput.Text.Length;
        }

        /// <summary>
        /// Inserts text into the composer at the caret, preserving any draft already there — the
        /// composer counterpart to the panel prompt box's caret-aware insert, used by "Send Selection
        /// to Claude Code" when the chat is in its own tab.
        /// </summary>
        public void InsertTextAtComposerCaret(string text)
        {
            if (text == null) return;

            int caretIndex = ComposerInput.CaretIndex;
            if (caretIndex < 0 || caretIndex > ComposerInput.Text.Length)
            {
                caretIndex = ComposerInput.Text.Length;
            }

            ComposerInput.Text = ComposerInput.Text.Insert(caretIndex, text);
            ComposerInput.CaretIndex = caretIndex + text.Length;
        }

        /// <summary>
        /// Enter/Shift+Enter/Ctrl+Enter follow the same preference as the panel's prompt box, so the
        /// habit a user already has keeps working in the tab. Escape stops the turn in progress (same
        /// as the Stop button) when one is running; otherwise it is left alone (no unfocus side effect).
        /// </summary>
        private void ComposerInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            ComposerPreviewKeyDown?.Invoke(this, e);
            if (e.Handled) return;

            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (StopButton.Visibility == Visibility.Visible)
                {
                    StopRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
                return;
            }

            bool control = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

            // Ctrl+V is offered to the parent first: an image on the clipboard becomes an attachment,
            // anything else falls through to the text box's own paste.
            if (e.Key == System.Windows.Input.Key.V && control)
            {
                var args = new ChatPasteEventArgs();
                PasteRequested?.Invoke(this, args);

                if (args.Handled)
                {
                    e.Handled = true;
                }

                return;
            }

            // Ctrl+↑/↓ cycle prompt history anywhere in the text, matching the panel's prompt box.
            if (e.Key == System.Windows.Input.Key.Up && control)
            {
                HistoryPreviousRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (e.Key == System.Windows.Input.Key.Down && control)
            {
                HistoryNextRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (e.Key != System.Windows.Input.Key.Enter)
            {
                return;
            }

            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

            bool send = SendWithEnter
                ? !shift && !control
                : SendWithCtrlEnter && control;

            if (!send)
            {
                return;
            }

            e.Handled = true;
            SendRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// True when the caret sits on the first wrapped line. Line indexes are visual, so a long first
        /// paragraph that wraps still counts as one line only where the user actually sees the top.
        /// </summary>
        private bool CaretIsOnFirstComposerLine()
        {
            try
            {
                return ComposerInput.GetLineIndexFromCharacterIndex(ComposerInput.CaretIndex) <= 0;
            }
            catch (Exception)
            {
                // Thrown while the box has never been laid out; the caret is at 0 then anyway.
                return true;
            }
        }

        private bool CaretIsOnLastComposerLine()
        {
            try
            {
                int line = ComposerInput.GetLineIndexFromCharacterIndex(ComposerInput.CaretIndex);
                return line < 0 || line >= ComposerInput.LineCount - 1;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void Composer_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Composer_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                FilesDropped?.Invoke(this, files);
            }

            e.Handled = true;
        }

        #endregion

        #region Interaction cards

        /// <summary>
        /// Raised once an interaction card is answered or declined, so the parent can report it on the
        /// status line. The request itself is already answered by the view model.
        /// </summary>
        public event EventHandler<ChatInteractionViewModel> InteractionResolved;

        /// <summary>
        /// Adds a question / plan / permission card to the transcript and scrolls it into view — an
        /// unanswered card blocks the agent, so it must never land off-screen.
        /// </summary>
        public void AddInteraction(ChatInteractionViewModel interaction)
        {
            if (interaction == null) return;

            Messages.Add(interaction);
            _autoScroll = true;
            _readerTookOver = false;
            _readerSettleTimer?.Stop();
            ScrollToEndIfFollowing();
        }

        /// <summary>Closes every card still waiting, for when the session goes away under them.</summary>
        public void AbandonPendingInteractions()
        {
            foreach (ChatMessageViewModel message in Messages)
            {
                var interaction = message as ChatInteractionViewModel;
                if (interaction != null)
                {
                    interaction.Abandon();
                }
            }
        }

        private void InteractionOption_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            // DataContext is the option; Tag carries the question it belongs to, because single-select
            // has to clear its siblings.
            var option = button.DataContext as ChatOptionViewModel;
            var question = button.Tag as ChatQuestionViewModel;

            if (question != null && option != null)
            {
                question.Select(option);
            }
        }

        private void InteractionSubmit_Click(object sender, RoutedEventArgs e)
        {
            var interaction = ResolveInteraction(sender);
            if (interaction == null) return;

            interaction.Submit();

            if (!interaction.IsPending)
            {
                InteractionResolved?.Invoke(this, interaction);
            }
        }

        private void InteractionReject_Click(object sender, RoutedEventArgs e)
        {
            var interaction = ResolveInteraction(sender);
            if (interaction == null) return;

            interaction.Reject();
            InteractionResolved?.Invoke(this, interaction);
        }

        /// <summary>Drops the "Allow for the rest of this session" menu below the caret on left-click.</summary>
        private void InteractionAllowMenu_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu == null) return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            button.ContextMenu.IsOpen = true;
        }

        private void InteractionAllowForSession_Click(object sender, RoutedEventArgs e)
        {
            var interaction = ResolveInteraction(sender);
            if (interaction == null) return;

            interaction.AllowForSession();

            if (!interaction.IsPending)
            {
                InteractionResolved?.Invoke(this, interaction);
            }
        }

        /// <summary>
        /// The plan card's extra approvals. <c>Tag</c> is <c>skip</c>, a <see cref="ClaudeModel"/> name,
        /// or <c>skip:&lt;model&gt;</c>, so one handler covers every menu entry.
        /// </summary>
        private void InteractionApprovePlan_Click(object sender, RoutedEventArgs e)
        {
            var interaction = ResolveInteraction(sender);
            if (interaction == null) return;

            string tag = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            bool skip = tag.StartsWith("skip", StringComparison.Ordinal);
            string modelName = tag.Contains(":") ? tag.Substring(tag.IndexOf(':') + 1) : (skip ? string.Empty : tag);

            ClaudeModel parsed;
            ClaudeModel? model = Enum.TryParse(modelName, out parsed) ? parsed : (ClaudeModel?)null;

            interaction.ApprovePlan(skip, model);

            if (!interaction.IsPending)
            {
                InteractionResolved?.Invoke(this, interaction);
            }
        }

        private static ChatInteractionViewModel ResolveInteraction(object sender)
        {
            var element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as ChatInteractionViewModel;
        }

        #endregion

        /// <summary>
        /// The ScrollViewer from <see cref="MessagesList"/>'s control template. It cannot be an
        /// <c>x:Name</c> field any more: the list has to own its scroll viewer for virtualization
        /// to work, and that viewer lives in the template. Resolved lazily because the template is
        /// applied after the constructor.
        /// </summary>
        private ScrollViewer TranscriptScroll
        {
            get
            {
                if (_transcriptScroll == null)
                {
                    _transcriptScroll = MessagesList.Template.FindName("TranscriptScroll", MessagesList) as ScrollViewer
                        ?? FindVisualChild<ScrollViewer>(MessagesList);
                }

                return _transcriptScroll;
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                T nested = FindVisualChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void ScrollTranscriptToEnd()
        {
            QueueScrollToEnd();
        }

        /// <summary>
        /// Coalesces scroll-to-end requests onto the dispatcher. A virtualized panel reports an
        /// estimated extent until the last rows are measured, so a single immediate
        /// <see cref="ScrollViewer.ScrollToEnd"/> can stop short; the pass repeats after layout
        /// until it reaches the true bottom (or gives up after a few attempts).
        /// </summary>
        private void QueueScrollToEnd()
        {
            if (!_autoScroll)
            {
                return;
            }

            // Set this before the dispatcher pass, not inside it: adding or growing a row raises
            // ScrollChanged immediately with the old offset and the new (estimated) extent. Without
            // the flag already set, that change cleared following before the first pass ever ran.
            _scrollToEndActive = true;

            if (_scrollToEndQueued)
            {
                return;
            }

            _scrollToEndQueued = true;
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(new Action(ScrollToEndPass),
                System.Windows.Threading.DispatcherPriority.Loaded);
#pragma warning restore VSTHRD001, VSTHRD110
        }

        private void ScrollToEndPass()
        {
            _scrollToEndQueued = false;

            if (!_autoScroll)
            {
                _scrollToEndActive = false;
                _forceScrollToEnd = false;
                _scrollToEndAttempts = 0;
                return;
            }

            // The reader took over after this pass was queued (wheel or scrollbar). Do not yank the
            // view back to the bottom; the next layout/text change follows again once they are back
            // at the newest line.
            if (_readerTookOver)
            {
                _scrollToEndActive = false;
                _forceScrollToEnd = false;
                _scrollToEndAttempts = 0;
                return;
            }

            ScrollViewer viewer = TranscriptScroll;
            if (viewer == null)
            {
                _scrollToEndActive = false;
                _forceScrollToEnd = false;
                return;
            }

            // Keep following on while this pass and its retries run. A virtualized panel's first
            // ScrollToEnd uses an estimated extent and often lands short; without this flag its own
            // ScrollChanged would turn following off before the next pass reaches the real bottom,
            // which is why live replies stopped following the newest row.
            _scrollToEndActive = true;
            viewer.ScrollToEnd();

            bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - BottomTolerance;

            // Bounded retries, as in the web clients' stick-to-bottom implementations: one pass plus
            // one follow-up covers an extent that was still being estimated; the next streamed line
            // requests another pass. Forced scrolls (replay, send, the jump button) keep more room to
            // land on the true bottom.
            int minimumPasses = _forceScrollToEnd ? 3 : 0;
            int maximumPasses = _forceScrollToEnd ? 6 : 2;
            if ((_scrollToEndAttempts < minimumPasses || !atBottom) && _scrollToEndAttempts < maximumPasses)
            {
                _scrollToEndAttempts++;
                QueueScrollToEnd();
                return;
            }

            _scrollToEndAttempts = 0;
            _scrollToEndActive = false;
            _forceScrollToEnd = false;
            if (atBottom)
            {
                _autoScroll = true;
            }
        }

        /// <summary>
        /// Who moved the viewport decides whether following continues. Only the reader's own input
        /// (wheel or the transcript's scrollbar) may turn following off, and only when it lands away
        /// from the bottom; any other change is layout growth — virtualized height estimates, a
        /// markdown block realizing its rows — and keeps the reader's current choice. An offset-based
        /// test could not tell those apart: a layout correction that nudged <c>VerticalOffset</c>
        /// looked like a scroll and halted a long stream halfway.
        /// </summary>
        internal static bool ResolveFollowingAfterScroll(bool following, bool readerTookOver, bool atBottom)
        {
            return readerTookOver ? atBottom : following || atBottom;
        }

        /// <summary>
        /// Scrolls the transcript from the wheel event before a message's own
        /// <see cref="FlowDocumentScrollViewer"/> can consume it. The document viewers have their
        /// scrollbars disabled and are sized to their content, but their internal ScrollViewer still
        /// handles the wheel, which left the chat unscrollable while the pointer was over a message.
        /// </summary>
        private void Transcript_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0)
            {
                return;
            }

            // Shift+wheel scrolls the unwrapped content under the pointer sideways: a code block, raw
            // tool input/output, a tool card's diff. Without it only dragging the scrollbar moved them.
            // Nothing to scroll sideways there: the wheel scrolls the transcript as usual.
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ScrollViewer sideways = FindHorizontalScrollTarget(e.OriginalSource as DependencyObject, TranscriptScroll);
                if (sideways != null)
                {
                    sideways.ScrollToHorizontalOffset(HorizontalWheelTarget(
                        sideways.HorizontalOffset, sideways.ScrollableWidth, e.Delta, SystemParameters.WheelScrollLines));
                    e.Handled = true;
                    return;
                }
            }

            // A wheel turn means the reader is taking over. Stop any scroll-to-end pass that is
            // still chasing a layout; otherwise it would yank the view back to the bottom.
            _scrollToEndActive = false;
            _forceScrollToEnd = false;
            _scrollToEndAttempts = 0;

            ScrollViewer transcript = TranscriptScroll;
            if (transcript == null)
            {
                return;
            }

            // A tool card's diff pane is a real, deliberately scrollable control. Keep the wheel
            // there while it can still move; at its edge, fall through to the conversation.
            ScrollViewer nested = FindAncestorScrollViewer(e.OriginalSource as DependencyObject);
            if (nested != null && !ReferenceEquals(nested, transcript) &&
                nested.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                bool canScroll = e.Delta < 0
                    ? nested.VerticalOffset < nested.ScrollableHeight - 0.5
                    : nested.VerticalOffset > 0.5;
                if (canScroll)
                {
                    return;
                }
            }

            // From here on the wheel moves the transcript itself, not a card's own pane.
            NoteReaderScrolled();

            double lines = SystemParameters.WheelScrollLines;
            double amount = lines < 0
                ? transcript.ViewportHeight
                : Math.Max(1, lines) * 16.0 * (e.Delta / 120.0);
            double target = Math.Max(0.0,
                Math.Min(transcript.ScrollableHeight, transcript.VerticalOffset - amount));

            transcript.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        private void Transcript_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TranscriptScroll == null)
            {
                return;
            }

            ScrollBar bar = FindAncestorScrollBar(e.OriginalSource as DependencyObject);
            if (bar != null && ReferenceEquals(bar.TemplatedParent, TranscriptScroll))
            {
                // Stop a pass that is already queued or running; it would fight the drag.
                _scrollToEndActive = false;
                _forceScrollToEnd = false;
                _scrollToEndAttempts = 0;
                NoteReaderScrolled();
            }
        }

        /// <summary>
        /// Records that the reader moved the transcript themselves and restarts the settle window
        /// during which no scroll-to-end pass may run. When it expires at the bottom, following
        /// resumes; away from the bottom the flag stays set until they come back.
        /// </summary>
        private void NoteReaderScrolled()
        {
            _readerTookOver = true;
            if (_readerSettleTimer == null)
            {
                _readerSettleTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = ReaderSettleDelay
                };
                _readerSettleTimer.Tick += ReaderSettleTimer_Tick;
            }

            _readerSettleTimer.Stop();
            _readerSettleTimer.Start();
        }

        private void ReaderSettleTimer_Tick(object sender, EventArgs e)
        {
            if (_readerSettleTimer != null)
            {
                _readerSettleTimer.Stop();
            }

            ScrollViewer viewer = TranscriptScroll;
            if (viewer == null)
            {
                return;
            }

            bool atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - BottomTolerance;
            if (atBottom)
            {
                // Settled at the newest line: following may resume.
                _readerTookOver = false;
                _autoScroll = true;
            }

            // Away from the bottom the flag stays set, so layout growth cannot pull the reader down.
        }

        private static ScrollBar FindAncestorScrollBar(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ScrollBar bar)
                {
                    return bar;
                }

                source = GetParent(source);
            }

            return null;
        }

        /// <summary>
        /// The closest scroller under the pointer that can move sideways, or null. A document viewer
        /// (<see cref="FlowDocumentScrollViewer"/>, so every <see cref="MarkdownBlock"/>) keeps its
        /// <see cref="ScrollViewer"/> inside its template: the pointer's content walks up past it to the
        /// viewer, so that one is looked up downwards. Scrollers with nothing to scroll sideways are
        /// skipped, so a wrapped reply around a code block does not swallow the wheel.
        /// </summary>
        internal static ScrollViewer FindHorizontalScrollTarget(DependencyObject source, ScrollViewer transcript)
        {
            while (source != null)
            {
                ScrollViewer candidate = source as ScrollViewer
                    ?? (source is FlowDocumentScrollViewer document ? FindVisualChild<ScrollViewer>(document) : null);

                if (candidate != null && !ReferenceEquals(candidate, transcript) &&
                    candidate.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                    candidate.ScrollableWidth > 0.5)
                {
                    return candidate;
                }

                if (ReferenceEquals(source, transcript))
                {
                    return null;
                }

                source = GetParent(source);
            }

            return null;
        }

        /// <summary>
        /// Horizontal offset after one Shift+wheel step: as many pixels as a vertical wheel step moves the
        /// transcript (16 per line), wheel down to the right, clamped to the scrollable width.
        /// </summary>
        internal static double HorizontalWheelTarget(double offset, double scrollableWidth, int delta, int wheelScrollLines)
        {
            double lines = wheelScrollLines <= 0 ? 3 : wheelScrollLines;
            double target = offset - lines * 16.0 * (delta / 120.0);
            return Math.Max(0.0, Math.Min(scrollableWidth, target));
        }

        private static ScrollViewer FindAncestorScrollViewer(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ScrollViewer viewer)
                {
                    return viewer;
                }

                source = GetParent(source);
            }

            return null;
        }

        private static DependencyObject GetParent(DependencyObject source)
        {
            if (source is ContentElement content)
            {
                DependencyObject parent = ContentOperations.GetParent(content);
                if (parent != null)
                {
                    return parent;
                }

                var frameworkContent = content as FrameworkContentElement;
                return frameworkContent?.Parent;
            }

            return source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        /// <summary>
        /// Scrolls to the newest content unless the user has deliberately scrolled up — yanking the
        /// view back down while they are reading an earlier answer is the classic chat-UI annoyance.
        /// </summary>
        public void ScrollToEndIfFollowing()
        {
            // No pass while the reader is in charge of the viewport: it would be cancelled at the
            // next ScrollChanged anyway, and queueing one per streamed line only adds churn.
            if (_autoScroll && !_readerTookOver)
            {
                ScrollTranscriptToEnd();
            }
        }

        /// <summary>
        /// Forces the view to the newest row and keeps it there until the virtualized list has
        /// measured its real extent. Used after a resumed transcript is replayed; unlike
        /// <see cref="ScrollToEndIfFollowing"/> this deliberately overrides a following flag that a
        /// short, estimated scroll may have cleared.
        /// </summary>
        public void ScrollToLatest()
        {
            _autoScroll = true;
            _readerTookOver = false;
            _readerSettleTimer?.Stop();
            _forceScrollToEnd = true;
            _scrollToEndAttempts = 0;
            QueueScrollToEnd();
        }

        private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            bool hasUserMessage = e.Action == NotifyCollectionChangedAction.Add
                ? _hasUserMessage || (e.NewItems != null && e.NewItems.OfType<ChatMessageViewModel>().Any(m => m.IsUser))
                : Messages.Any(m => m.IsUser);
            if (hasUserMessage != _hasUserMessage)
            {
                _hasUserMessage = hasUserMessage;
                UpdateSendButtonVisibility();
            }

            // Only the newest row grows, so one subscription is enough to follow streaming output.
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && e.NewItems.Count > 0)
            {
                StopTracking();
                _trackedMessage = e.NewItems[e.NewItems.Count - 1] as ChatMessageViewModel;
                if (_trackedMessage != null)
                {
                    _trackedMessage.PropertyChanged += OnTrackedMessageChanged;
                }

                // The card survives the notices a session start puts in the transcript ("New chat",
                // "Switched to Sonnet") and only goes away once the user actually says something —
                // otherwise the thing announcing the new conversation would erase itself.
                foreach (object item in e.NewItems)
                {
                    var message = item as ChatMessageViewModel;
                    if (message != null && message.IsUser)
                    {
                        HideWelcome();
                        break;
                    }
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                StopTracking();
            }

            ScrollToEndIfFollowing();
        }

        private void OnTrackedMessageChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.Text) ||
                e.PropertyName == nameof(ChatMessageViewModel.ToolResult))
            {
                // Queued at Background priority so the scroll happens after the new text is laid out;
                // scrolling first would stop short of the line that just arrived. The VS threading
                // helpers cannot express that priority, so the WPF dispatcher is the right tool here —
                // this method is already on the UI thread.
#pragma warning disable VSTHRD001, VSTHRD110
                Dispatcher.BeginInvoke(new Action(ScrollToEndIfFollowing),
                    System.Windows.Threading.DispatcherPriority.Background);
#pragma warning restore VSTHRD001, VSTHRD110
            }
        }

        private void StopTracking()
        {
            if (_trackedMessage != null)
            {
                _trackedMessage.PropertyChanged -= OnTrackedMessageChanged;
                _trackedMessage = null;
            }
        }

        private void TranscriptScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // ScrollChanged bubbles: scrolling inside an expanded card (a long command's horizontal
            // scrollbar, a tool result) reaches this handler with the inner viewer's offsets, which
            // always read as "at the bottom" and would re-arm following while the user is reading.
            if (!ReferenceEquals(e.OriginalSource, TranscriptScroll))
            {
                return;
            }

            // Following is re-armed automatically once the user scrolls back to the bottom.
            bool atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - BottomTolerance;

            // While a scroll-to-end pass and its retries are running, the extent is still being
            // estimated; a pass that lands a little short must not turn following off, or the retry
            // loop stops before the true bottom is reached.
            if (!_scrollToEndActive && !_forceScrollToEnd)
            {
                bool readerInput = _readerTookOver;
                _autoScroll = ResolveFollowingAfterScroll(_autoScroll, readerInput, atBottom);
                // The reader flag is cleared by the settle timer, not here: a fast wheel run can
                // pass through the bottom for a frame, and clearing it here re-armed a pass that
                // then fought the next wheel turn.

                // Deliberately no scroll pass from here. Following is re-requested by the text change
                // that produced the growth; queuing from ScrollChanged as well made the pass's own
                // extent change queue another pass, which is the up/down loop seen while scrolling
                // fast: a virtualized list never reports a stable extent while it realizes rows, so
                // the feedback fed itself until the user stopped touching the wheel.
            }

            ScrollToEndButton.Visibility = atBottom || _scrollToEndActive || _forceScrollToEnd || Messages.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ScrollToEndButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollToLatest();
        }
    }

    /// <summary>
    /// Carries the outcome of a composer paste back to the view: set by the parent when the clipboard
    /// was consumed as an image attachment, so the text box does not also paste its text form.
    /// </summary>
    public class ChatPasteEventArgs : EventArgs
    {
        public bool Handled { get; set; }
    }

    /// <summary>
    /// Accent colour of a tool row, by family. Literal colours rather than theme brushes: "this ran a
    /// command" has to read the same under both the dark and the light theme, and the frozen instances
    /// are shared by every row instead of being allocated per tool call.
    /// </summary>
    public static class ChatToolAccents
    {
        private static readonly System.Windows.Media.Brush Read = Freeze("#4FA3E3");
        private static readonly System.Windows.Media.Brush Edit = Freeze("#D8973C");
        private static readonly System.Windows.Media.Brush Run = Freeze("#A176D6");
        private static readonly System.Windows.Media.Brush Search = Freeze("#3FB8AF");
        private static readonly System.Windows.Media.Brush Web = Freeze("#5B9BD5");
        private static readonly System.Windows.Media.Brush Todo = Freeze("#4EC9B0");
        private static readonly System.Windows.Media.Brush Agent = Freeze("#E36F9E");
        private static readonly System.Windows.Media.Brush Other = Freeze("#8C8C8C");

        public static System.Windows.Media.Brush For(ChatToolCategory category)
        {
            switch (category)
            {
                case ChatToolCategory.Read: return Read;
                case ChatToolCategory.Edit: return Edit;
                case ChatToolCategory.Run: return Run;
                case ChatToolCategory.Search: return Search;
                case ChatToolCategory.Web: return Web;
                case ChatToolCategory.Todo: return Todo;
                case ChatToolCategory.Agent: return Agent;
                default: return Other;
            }
        }

        private static System.Windows.Media.Brush Freeze(string hex)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Which composer dropdown the user clicked.</summary>
    public enum ChatSelector
    {
        Provider,
        Model,
        Effort,
        Permission
    }

    /// <summary>Which of the composer's mirrored config menus the user clicked.</summary>
    public enum ChatConfigMenu
    {
        Settings,
        Tools,
        CustomCommands
    }

    /// <summary>
    /// Picks the row template from <see cref="ChatMessageViewModel.Kind"/>.
    /// </summary>
    public class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }
        public DataTemplate AssistantTemplate { get; set; }
        public DataTemplate ThinkingTemplate { get; set; }
        public DataTemplate ToolTemplate { get; set; }
        public DataTemplate ErrorTemplate { get; set; }
        public DataTemplate NoticeTemplate { get; set; }
        public DataTemplate InteractionTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var message = item as ChatMessageViewModel;
            if (message == null)
            {
                return base.SelectTemplate(item, container);
            }

            switch (message.Kind)
            {
                case ChatMessageKind.User: return UserTemplate;
                case ChatMessageKind.Interaction: return InteractionTemplate;
                case ChatMessageKind.Thinking: return ThinkingTemplate;
                case ChatMessageKind.ToolCall: return ToolTemplate;
                case ChatMessageKind.Error: return ErrorTemplate;
                case ChatMessageKind.Notice: return NoticeTemplate;
                default: return AssistantTemplate;
            }
        }
    }

    /// <summary>Visible when the bound bool is false.</summary>
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool && (bool)value) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Visible when the bound string has content. Hides empty tool inputs and results.
    /// Pass <c>invert</c> as the converter parameter to flip it, which is how the placeholder
    /// over a free-text answer box shows only while the box is empty.
    /// </summary>
    public class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isEmpty = string.IsNullOrWhiteSpace(value as string);

            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            {
                isEmpty = !isEmpty;
            }

            return isEmpty ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
