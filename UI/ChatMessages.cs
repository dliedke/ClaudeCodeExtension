/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: View models backing the native-mode chat transcript
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeCodeVS.UI
{
    /// <summary>Status icon in front of a notice row. None keeps the row text-only.</summary>
    public enum ChatNoticeIcon
    {
        None,
        Done,
        Stopped
    }

    /// <summary>What a transcript row represents. Drives which template the list picks.</summary>
    public enum ChatMessageKind
    {
        User,
        Assistant,
        Thinking,
        ToolCall,
        Error,

        /// <summary>Neutral status line: interrupted turn, blocked permissions, mode notices.</summary>
        Notice,

        /// <summary>
        /// A row the user has to act on — a question, a plan awaiting approval, a tool awaiting a yes.
        /// Backed by <see cref="ChatInteractionViewModel"/>.
        /// </summary>
        Interaction
    }

    /// <summary>
    /// Formatting shared by the live status line and the end-of-turn summary, so a turn is never
    /// described one way while it runs and another way once it ends.
    /// </summary>
    public static class ChatFormatting
    {
        /// <summary>
        /// Human-readable elapsed time in whole seconds. A tenth of a second only made the live clock
        /// flicker ("2.1s 2.2s 2.3s…") without telling the user anything.
        /// </summary>
        public static string Duration(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (elapsed.TotalHours >= 1)
            {
                return string.Format("{0}h {1:00}m {2:00}s",
                    (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
            }

            if (elapsed.TotalMinutes >= 1)
            {
                return string.Format("{0}m {1:00}s", (int)elapsed.TotalMinutes, elapsed.Seconds);
            }

            return string.Format("{0}s", (int)elapsed.TotalSeconds);
        }

        /// <summary>
        /// One token figure for the status line and the turn footer.
        /// <para>
        /// The caller supplies the input semantics reported by its provider. Codex uses
        /// <see cref="CodexTokens"/> instead because its input figure includes cached input and would be
        /// misleading without that breakdown.
        /// </para>
        /// </summary>
        public static string Tokens(int inputTokens, int outputTokens)
        {
            return string.Format("{0:N0} tokens", Math.Max(0, inputTokens) + Math.Max(0, outputTokens));
        }

        /// <summary>
        /// Codex's <c>turn.completed</c> breakdown. Its input total includes cached input, so keeping
        /// all three figures together explains a large processed count without pretending every token
        /// was new or generated as visible output.
        /// </summary>
        public static string CodexTokens(int inputTokens, int outputTokens, int cachedInputTokens)
        {
            int safeInput = Math.Max(0, inputTokens);
            int safeOutput = Math.Max(0, outputTokens);
            int safeCached = Math.Min(safeInput, Math.Max(0, cachedInputTokens));

            return string.Format(
                "{0:N0} processed · {1:N0} cached input · {2:N0} output",
                safeInput + safeOutput,
                safeCached,
                safeOutput);
        }
    }

    /// <summary>
    /// One row of the chat transcript.
    /// <para>
    /// Streaming appends into <see cref="Text"/>. An assistant reply's markdown view binds
    /// <see cref="Text"/> with <see cref="IsStreaming"/> as its incremental flag, so it renders the
    /// finished blocks once and only re-renders the unfinished tail; <see cref="Complete"/> ends that
    /// and the view rebuilds the document whole, once. Thinking rows still bind
    /// <see cref="RenderedText"/>, published by <see cref="Complete"/> — they are collapsed while they
    /// stream. That is why the object is mutated rather than replaced — recreating the item makes the
    /// list flicker and drops the user's text selection.
    /// </para>
    /// <para>
    /// <see cref="Append"/> only grows the buffer; the change to <see cref="Text"/> is announced at most
    /// once per <see cref="StreamingPublishInterval"/>. The CLI streams a delta per token, and each
    /// announcement costs the view an update, so announcing every delta left the UI thread behind.
    /// Reading <see cref="Text"/> always returns everything appended so far.
    /// </para>
    /// </summary>
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// Shortest gap between two <see cref="Text"/> change notifications while streaming: at most
        /// 20 a second, which still reads as live typing. Settable for the tests only.
        /// </summary>
        internal static TimeSpan StreamingPublishInterval = TimeSpan.FromMilliseconds(50);

        private readonly StringBuilder _buffer = new StringBuilder();

        /// <summary>The buffer holds text <see cref="_text"/> does not have yet.</summary>
        private bool _bufferAhead;

        /// <summary>Text changed since the last notification, which is still owed.</summary>
        private bool _textNotificationPending;
        private long _lastTextNotification;
        private DispatcherTimer _textNotificationTimer;

        private string _text = string.Empty;
        private string _renderedText = string.Empty;
        private bool _isStreaming;
        private bool _isExpanded;
        private string _header = string.Empty;
        private string _toolResult = string.Empty;
        private bool _isError;
        private bool _isRunning;
        private string _toolIcon = string.Empty;
        private string _toolTarget = string.Empty;
        private string _toolBadge = string.Empty;
        private Brush _toolAccent;

        public ChatMessageViewModel(ChatMessageKind kind)
        {
            Kind = kind;
        }

        public ChatMessageKind Kind { get; }

        public bool IsUser { get { return Kind == ChatMessageKind.User; } }

        public string Text
        {
            get
            {
                if (_bufferAhead)
                {
                    _text = _buffer.ToString();
                    _bufferAhead = false;
                }

                return _text;
            }
            set
            {
                _bufferAhead = false;
                CancelTextNotification();
                Set(ref _text, value ?? string.Empty, nameof(Text));
            }
        }

        /// <summary>
        /// The finished text, published by <see cref="Complete"/>. Bound by the thinking row, whose
        /// markdown is rendered once per message instead of once per streamed token.
        /// </summary>
        public string RenderedText
        {
            get { return _renderedText; }
            private set { Set(ref _renderedText, value ?? string.Empty, nameof(RenderedText)); }
        }

        /// <summary>True while text is still arriving; an assistant row renders its markdown incrementally meanwhile.</summary>
        public bool IsStreaming
        {
            get { return _isStreaming; }
            set { Set(ref _isStreaming, value, nameof(IsStreaming)); }
        }

        /// <summary>Collapsible rows (thinking, tool calls) start closed.</summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                // Built before the change is published, so the card opens with its diff rather than
                // flashing the raw input first.
                if (value)
                {
                    BuildDeferredDiff();
                }

                Set(ref _isExpanded, value, nameof(IsExpanded));
            }
        }

        /// <summary>Title line for collapsible rows, e.g. <c>Read · src\Program.cs</c>.</summary>
        public string Header
        {
            get { return _header; }
            set { Set(ref _header, value ?? string.Empty, nameof(Header)); }
        }

        // --- tool rows ---

        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string ToolInputJson { get; set; } = string.Empty;

        /// <summary>
        /// The unshortened file path a file-scoped tool (Read, Write, Edit, MultiEdit, NotebookEdit)
        /// acted on. Empty for tools with no single file target. Backs the row's "open file" icon.
        /// </summary>
        public string ToolFilePath { get; set; } = string.Empty;

        public bool HasToolFilePath { get { return !string.IsNullOrWhiteSpace(ToolFilePath); } }

        public string ToolResult
        {
            get { return _toolResult; }
            set { Set(ref _toolResult, value ?? string.Empty, nameof(ToolResult)); }
        }

        public bool IsError
        {
            get { return _isError; }
            set { Set(ref _isError, value, nameof(IsError)); }
        }

        private ChatNoticeIcon _noticeIcon;

        /// <summary>Visual Studio status icon drawn in front of a notice row (Modern icon style's turn footer).</summary>
        public ChatNoticeIcon NoticeIcon
        {
            get { return _noticeIcon; }
            set { Set(ref _noticeIcon, value, nameof(NoticeIcon)); }
        }

        /// <summary>True while the tool is still running, so the row can show that it has not answered.</summary>
        public bool IsRunning
        {
            get { return _isRunning; }
            set { Set(ref _isRunning, value, nameof(IsRunning)); }
        }

        /// <summary>Same glyph set as the global status spinner, kept here so a long-running row (most
        /// visibly a subagent Task, which can sit for minutes) reads as busy rather than frozen.</summary>
        private static readonly string[] SpinnerFrames = { "◐", "◓", "◑", "◒" };
        private int _spinnerFrame;
        private string _spinnerGlyph = SpinnerFrames[0];

        /// <summary>Animated glyph shown while <see cref="IsRunning"/> is true. Advanced by the
        /// transcript view's activity timer, one frame per tick, for every row still running.</summary>
        public string SpinnerGlyph
        {
            get { return _spinnerGlyph; }
            private set { Set(ref _spinnerGlyph, value, nameof(SpinnerGlyph)); }
        }

        /// <summary>Moves to the next spinner frame.</summary>
        public void AdvanceSpinner()
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            SpinnerGlyph = SpinnerFrames[_spinnerFrame];
        }

        /// <summary>Glyph shown before the tool name, chosen from the tool's family.</summary>
        public string ToolIcon
        {
            get { return _toolIcon; }
            set { Set(ref _toolIcon, value ?? string.Empty, nameof(ToolIcon)); }
        }

        /// <summary>What the tool acted on: a file, a command, a pattern.</summary>
        public string ToolTarget
        {
            get { return _toolTarget; }
            set { Set(ref _toolTarget, value ?? string.Empty, nameof(ToolTarget)); }
        }

        /// <summary>Right-aligned counter, e.g. <c>+12 -3</c>.</summary>
        public string ToolBadge
        {
            get { return _toolBadge; }
            set { Set(ref _toolBadge, value ?? string.Empty, nameof(ToolBadge)); }
        }

        /// <summary>Accent colour of the row, derived from the tool's family.</summary>
        public Brush ToolAccent
        {
            get { return _toolAccent; }
            set { Set(ref _toolAccent, value, nameof(ToolAccent)); }
        }

        /// <summary>
        /// Rendered diff of an editing tool. Empty for every other tool, which is what
        /// <see cref="HasDiff"/> hides the diff pane on.
        /// </summary>
        public ObservableCollection<ChatDiffLine> Diff { get; } = new ObservableCollection<ChatDiffLine>();

        public bool HasDiff { get { return Diff.Count > 0; } }

        /// <summary>
        /// True when the card falls back to showing the raw input payload — a diff already says what an
        /// editing tool did, and printing the same strings again as JSON underneath it is pure noise.
        /// </summary>
        public bool ShowRawInput
        {
            get { return Diff.Count == 0 && !string.IsNullOrWhiteSpace(ToolInputJson); }
        }

        /// <summary>Replaces the diff rows and notifies, so a card can be filled after it is on screen.</summary>
        public void SetDiff(IEnumerable<ChatDiffLine> lines)
        {
            Diff.Clear();

            if (lines != null)
            {
                foreach (ChatDiffLine line in lines)
                {
                    Diff.Add(line);
                }
            }

            RaisePropertyChanged(nameof(HasDiff));
            RaisePropertyChanged(nameof(ShowRawInput));
        }

        private Func<ChatToolPresentation> _deferredDiff;

        /// <summary>
        /// Leaves the diff to be built when the card is first opened. A replayed transcript creates
        /// hundreds of cards at once and most are never opened; the diff is the costly part of a card.
        /// The factory's <see cref="ChatToolPresentation.Badge"/> (the <c>+12 -3</c> change count, which
        /// comes from the diff) replaces the row's badge at the same time.
        /// </summary>
        public void SetDeferredDiff(Func<ChatToolPresentation> buildPresentation)
        {
            _deferredDiff = buildPresentation;
        }

        private void BuildDeferredDiff()
        {
            Func<ChatToolPresentation> build = _deferredDiff;
            if (build == null)
            {
                return;
            }

            _deferredDiff = null;

            ChatToolPresentation presentation;
            try
            {
                presentation = build();
            }
            catch (Exception)
            {
                // A card whose diff cannot be built still opens, on its raw input.
                return;
            }

            if (presentation == null)
            {
                return;
            }

            SetDiff(presentation.Diff);
            if (!string.IsNullOrEmpty(presentation.Badge))
            {
                ToolBadge = presentation.Badge;
            }
        }

        /// <summary>
        /// Appends a streamed chunk. The first chunk after a quiet <see cref="StreamingPublishInterval"/>
        /// is announced at once, so the reply starts showing without delay; the ones inside the interval
        /// are announced together when it ends (or by <see cref="Complete"/>).
        /// </summary>
        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            _buffer.Append(chunk);
            _bufferAhead = true;

            long now = Stopwatch.GetTimestamp();
            long interval = (long)(StreamingPublishInterval.TotalSeconds * Stopwatch.Frequency);
            if (_lastTextNotification == 0 || now - _lastTextNotification >= interval)
            {
                NotifyText(now);
                return;
            }

            _textNotificationPending = true;
            if (_textNotificationTimer == null)
            {
                _textNotificationTimer = new DispatcherTimer(DispatcherPriority.Normal);
                _textNotificationTimer.Tick += OnTextNotificationTimerTick;
            }

            if (!_textNotificationTimer.IsEnabled)
            {
                _textNotificationTimer.Interval = TimeSpan.FromTicks(
                    Math.Max(1, (interval - (now - _lastTextNotification)) * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                _textNotificationTimer.Start();
            }
        }

        private void OnTextNotificationTimerTick(object sender, EventArgs e)
        {
            _textNotificationTimer.Stop();
            if (_textNotificationPending)
            {
                NotifyText(Stopwatch.GetTimestamp());
            }
        }

        private void NotifyText(long timestamp)
        {
            CancelTextNotification();
            _lastTextNotification = timestamp;
            RaisePropertyChanged(nameof(Text));
        }

        /// <summary>Announces a change still held back by the throttle, if there is one.</summary>
        private void FlushTextNotification()
        {
            if (_textNotificationPending)
            {
                NotifyText(Stopwatch.GetTimestamp());
            }
        }

        private void CancelTextNotification()
        {
            _textNotificationPending = false;
            _textNotificationTimer?.Stop();
        }

        /// <summary>
        /// Swaps the whole text of the row (e.g. a reply shown after cleanup), re-rendering it if the
        /// row has already been completed.
        /// </summary>
        public void ReplaceText(string text)
        {
            _buffer.Clear();
            _buffer.Append(text ?? string.Empty);
            Text = _buffer.ToString();
            if (!IsStreaming)
            {
                RenderedText = Text;
            }
        }

        /// <summary>
        /// Marks the row finished and hands its text to the markdown view, which renders it once.
        /// </summary>
        public void Complete()
        {
            FlushTextNotification();
            IsStreaming = false;
            RenderedText = Text;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Notifies a change from a derived row type.</summary>
        protected void RaisePropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private void Set<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;

            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
