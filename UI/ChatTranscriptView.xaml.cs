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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

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

        private const double MinZoom = 0.6;
        private const double MaxZoom = 2.5;
        private const double ZoomStep = 0.1;

        private const double MinComposerHeight = 46;
        private const double MaxComposerHeight = 400;

        private ChatMessageViewModel _trackedMessage;
        private bool _autoScroll = true;
        private bool _suppressEffortEvent;
        private int _effortIndexOnOpen;
        private int _effortIndex;
        private string[] _effortStopLabels;

        private System.Windows.Threading.DispatcherTimer _activityTimer;
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

            Messages = new ObservableCollection<ChatMessageViewModel>();
            Messages.CollectionChanged += OnMessagesChanged;
            MessagesList.ItemsSource = Messages;
        }

        /// <summary>Transcript rows, oldest first.</summary>
        public ObservableCollection<ChatMessageViewModel> Messages { get; }

        /// <summary>Raised when the user clicks Stop. The parent control aborts the turn.</summary>
        public event EventHandler StopRequested;

        /// <summary>Raised when the composer's text should be sent as a prompt.</summary>
        public event EventHandler SendRequested;

        /// <summary>Raised by the paperclip button. The parent owns the file dialog and the list.</summary>
        public event EventHandler AttachRequested;

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
        /// Raised once the user is done with the effort slider — when the popup closes — carrying the
        /// stop it was left on. Deliberately not raised per stop: applying a level restarts the agent,
        /// so reporting each stop the user passes through would restart it once per stop and lock the
        /// chat up while they are still choosing.
        /// </summary>
        public event EventHandler<int> EffortChanged;

        /// <summary>Raised by the ✚ button: start a fresh conversation.</summary>
        public event EventHandler NewChatRequested;

        /// <summary>Raised when Ctrl+Scroll changes the zoom, so the parent can persist it.</summary>
        public event EventHandler<double> ZoomChanged;

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
        /// True while the user is typing in the composer. Prompt history writes to whichever prompt box
        /// has the keyboard, so the panel and the chat tab never overwrite each other's text.
        /// </summary>
        public bool ComposerHasFocus
        {
            get { return ComposerBar.Visibility == Visibility.Visible && ComposerInput.IsKeyboardFocusWithin; }
        }

        /// <summary>Mirrors the panel's send-key preference so Enter behaves the same in both places.</summary>
        public bool SendWithEnter { get; set; } = true;

        /// <summary>Mirrors the panel's Ctrl+Enter preference. Ignored while <see cref="SendWithEnter"/> is on.</summary>
        public bool SendWithCtrlEnter { get; set; }

        /// <summary>
        /// Shows the in-view composer. It is only wanted when the chat is in its own tab: inside the
        /// panel the existing prompt box sits directly above the transcript, and two input boxes one
        /// on top of the other is just confusing.
        /// </summary>
        public void ShowComposer(bool show)
        {
            ComposerBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Captions of the agent / model / effort / permission selectors.</summary>
        public void SetSelectorLabels(string provider, string model, string effort, string permission)
        {
            ComposerProviderButton.Content = (provider ?? "Agent") + " ▾";
            ComposerModelButton.Content = (model ?? "Model") + " ▾";
            ComposerEffortButton.Content = (effort ?? "Effort") + " ▾";
            ComposerPermissionButton.Content = (permission ?? "Permissions") + " ▾";
        }

        /// <summary>
        /// The composer button a selector's menu should hang off, or null when there is nothing to hang
        /// it on — the composer is hidden (the transcript is back in the panel) or that particular
        /// selector does not apply to the running agent. Used by the typed slash commands, which open
        /// the same menus the buttons do; the caller falls back to its own anchor on null.
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

        /// <summary>Enables or hides the selectors that do not apply to the running agent.</summary>
        public void SetSelectorAvailability(bool model, bool effort, bool permission)
        {
            ComposerModelButton.Visibility = model ? Visibility.Visible : Visibility.Collapsed;
            ComposerEffortButton.Visibility = effort ? Visibility.Visible : Visibility.Collapsed;
            ComposerPermissionButton.Visibility = permission ? Visibility.Visible : Visibility.Collapsed;
        }

        public void FocusComposer()
        {
            if (ComposerBar.Visibility != Visibility.Visible) return;

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
        /// </summary>
        private static readonly string[] WorkingVerbs =
        {
            "Working", "Thinking", "Pondering", "Perusing", "Noodling", "Percolating",
            "Crunching", "Mulling", "Tinkering", "Digging", "Puzzling", "Simmering",
            "Cogitating", "Whirring", "Churning", "Deliberating"
        };

        private static readonly Random VerbPicker = new Random();

        /// <summary>Frame rate of the spinner. Fast enough to read as motion, cheap enough to ignore.</summary>
        private static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(120);

        /// <summary>How long each verb stays up before the next one.</summary>
        private static readonly TimeSpan VerbInterval = TimeSpan.FromSeconds(4);

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
            StatusSpinner.Visibility = Visibility.Visible;
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
        }

        private void RenderActivity()
        {
            StatusSpinner.Text = SpinnerFrames[_spinnerFrame];

            string label = _activityLabel.Length > 0 ? _activityLabel : WorkingVerbs[_verbIndex] + "…";
            string text = label + "  " + ChatFormatting.Duration(DateTime.UtcNow - _activityStartedUtc);

            if (!string.IsNullOrEmpty(_activityDetail))
            {
                text += "  ·  " + _activityDetail;
            }

            StatusText.Text = text;
        }

        private void StopActivityTimer()
        {
            if (_activityTimer != null)
            {
                _activityTimer.Stop();
            }

            StatusSpinner.Visibility = Visibility.Collapsed;
        }

        #endregion

        public void Clear()
        {
            StopTracking();
            StopActivityTimer();
            Messages.Clear();
            _autoScroll = true;
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
            WelcomeTitle.Text = string.IsNullOrWhiteSpace(title) ? "Chat" : title;
            WelcomeFacts.ItemsSource = ToLines(facts);
            WelcomeTips.ItemsSource = ToLines(tips);
            WelcomeBanner.Visibility = Visibility.Visible;
        }

        /// <summary>Drops the welcome card. Idempotent.</summary>
        public void HideWelcome()
        {
            WelcomeBanner.Visibility = Visibility.Collapsed;
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

        private void ComposerSendButton_Click(object sender, RoutedEventArgs e)
        {
            SendRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerAttachButton_Click(object sender, RoutedEventArgs e)
        {
            AttachRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ComposerProviderButton_Click(object sender, RoutedEventArgs e)
        {
            SelectorClicked?.Invoke(sender, ChatSelector.Provider);
        }

        private void ComposerModelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectorClicked?.Invoke(sender, ChatSelector.Model);
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
        /// Positions the effort slider and captions it. <paramref name="index"/> is the slider stop
        /// (0..5) the parent maps to and from its own effort enum.
        /// </summary>
        public void SetEffortSlider(int index, string label)
        {
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

            EffortPopupLabel.Text = string.IsNullOrEmpty(label) ? "Effort" : "Effort (" + label + ")";
        }

        /// <summary>
        /// Captions of the slider stops, in slider order, so the popup can name the level under the
        /// thumb while the user is still moving it — the parent's caption only arrives once the level
        /// has actually been applied.
        /// </summary>
        public void SetEffortStopLabels(string[] labels)
        {
            _effortStopLabels = labels;
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
                EffortPopupLabel.Text = "Effort (" + _effortStopLabels[_effortIndex] + ")";
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

        private void ComposerNewChatButton_Click(object sender, RoutedEventArgs e)
        {
            NewChatRequested?.Invoke(this, EventArgs.Empty);
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

        private static double ClampZoom(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 1.0;
            return Math.Max(MinZoom, Math.Min(MaxZoom, value));
        }

        private void ComposerPermissionButton_Click(object sender, RoutedEventArgs e)
        {
            SelectorClicked?.Invoke(sender, ChatSelector.Permission);
        }

        private void ComposerInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            ComposerPlaceholder.Visibility = string.IsNullOrEmpty(ComposerInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Text of the composer, with the caret placed for what comes next: history navigation puts it
        /// at the start so ↑ keeps walking backwards instead of moving inside the recalled prompt.
        /// </summary>
        public void SetComposerText(string text, bool caretAtStart)
        {
            ComposerInput.Text = text ?? string.Empty;
            ComposerInput.CaretIndex = caretAtStart ? 0 : ComposerInput.Text.Length;
        }

        /// <summary>
        /// Enter/Shift+Enter/Ctrl+Enter follow the same preference as the panel's prompt box, so the
        /// habit a user already has keeps working in the tab. Escape drops focus back to the
        /// transcript, matching the hint in the placeholder.
        /// </summary>
        private void ComposerInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                TranscriptScroll.Focus();
                e.Handled = true;
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

            // ↑ on the first line recalls the previous prompt, the way a shell does — the caret is
            // already where the user is looking, so nothing is lost by taking the key. Ctrl+↑/↓ work
            // anywhere in the text, matching the panel's prompt box.
            if (e.Key == System.Windows.Input.Key.Up && (control || CaretIsOnFirstComposerLine()))
            {
                HistoryPreviousRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (e.Key == System.Windows.Input.Key.Down && (control || CaretIsOnLastComposerLine()))
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

        private static ChatInteractionViewModel ResolveInteraction(object sender)
        {
            var element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as ChatInteractionViewModel;
        }

        #endregion

        /// <summary>
        /// Scrolls to the newest content unless the user has deliberately scrolled up — yanking the
        /// view back down while they are reading an earlier answer is the classic chat-UI annoyance.
        /// </summary>
        public void ScrollToEndIfFollowing()
        {
            if (_autoScroll)
            {
                TranscriptScroll.ScrollToEnd();
            }
        }

        private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
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
            // Following is re-armed automatically once the user scrolls back to the bottom.
            bool atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - BottomTolerance;

            _autoScroll = atBottom;
            ScrollToEndButton.Visibility = atBottom || Messages.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ScrollToEndButton_Click(object sender, RoutedEventArgs e)
        {
            _autoScroll = true;
            TranscriptScroll.ScrollToEnd();
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
