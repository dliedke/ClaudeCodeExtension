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
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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

        /// <summary>Raised when the effort slider settles on a new stop. The argument is the stop index.</summary>
        public event EventHandler<int> EffortChanged;

        /// <summary>Raised by the ✚ button: start a fresh conversation.</summary>
        public event EventHandler NewChatRequested;

        /// <summary>Raised when Ctrl+Scroll changes the zoom, so the parent can persist it.</summary>
        public event EventHandler<double> ZoomChanged;

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
        /// </summary>
        public void SetStatus(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = string.Empty;
                return;
            }

            StatusText.Text = text;
            StatusBar.Visibility = Visibility.Visible;
        }

        public void Clear()
        {
            StopTracking();
            Messages.Clear();
            _autoScroll = true;
            ScrollToEndButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Collapsed;
        }

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

            EffortPopupLabel.Text = string.IsNullOrEmpty(label) ? "Effort" : "Effort (" + label + ")";
        }

        private void EffortPopupSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEffortEvent)
            {
                return;
            }

            EffortChanged?.Invoke(this, (int)Math.Round(e.NewValue));
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

            if (e.Key != System.Windows.Input.Key.Enter)
            {
                return;
            }

            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

            bool send = SendWithEnter
                ? !shift && !ctrl
                : SendWithCtrlEnter && ctrl;

            if (!send)
            {
                return;
            }

            e.Handled = true;
            SendRequested?.Invoke(this, EventArgs.Empty);
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
