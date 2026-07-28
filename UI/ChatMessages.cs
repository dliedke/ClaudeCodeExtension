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
using System.Text;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
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
        /// Human-readable elapsed time. Seconds keep a decimal because most turns are short; minutes
        /// and hours drop it, since "19m 15.4s" is false precision on a twenty-minute turn.
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

            return string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.0}s", elapsed.TotalSeconds);
        }

        /// <summary>
        /// One token figure for the status line and the turn footer.
        /// <para>
        /// Deliberately a single number rather than an in/out/cached breakdown: with prompt caching the
        /// input count is almost always a handful of tokens, so "1 in · 577 out · 25,750 cached" reads
        /// as a bug rather than as information. Cache reads are excluded — they are context the model
        /// did not have to process again.
        /// </para>
        /// </summary>
        public static string Tokens(int inputTokens, int outputTokens)
        {
            return string.Format("{0:N0} tokens", Math.Max(0, inputTokens) + Math.Max(0, outputTokens));
        }
    }

    /// <summary>
    /// One row of the chat transcript.
    /// <para>
    /// Streaming appends into <see cref="Text"/> and the row renders as plain text; building a
    /// <c>FlowDocument</c> on every token delta would re-layout the list dozens of times a second.
    /// <see cref="Complete"/> is called when the turn ends and publishes <see cref="RenderedText"/>,
    /// which is what the markdown view binds to. That is why the object is mutated rather than
    /// replaced — recreating the item makes the list flicker and drops the user's text selection.
    /// </para>
    /// </summary>
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        private readonly StringBuilder _buffer = new StringBuilder();
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
            get { return _text; }
            set { Set(ref _text, value ?? string.Empty, nameof(Text)); }
        }

        /// <summary>
        /// The finished text, published by <see cref="Complete"/> and bound by the markdown view. Kept
        /// apart from <see cref="Text"/> so that rendering happens once per message instead of once per
        /// streamed token.
        /// </summary>
        public string RenderedText
        {
            get { return _renderedText; }
            private set { Set(ref _renderedText, value ?? string.Empty, nameof(RenderedText)); }
        }

        /// <summary>True while text is still arriving; the row shows raw text and a caret cue.</summary>
        public bool IsStreaming
        {
            get { return _isStreaming; }
            set { Set(ref _isStreaming, value, nameof(IsStreaming)); }
        }

        /// <summary>Collapsible rows (thinking, tool calls) start closed.</summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { Set(ref _isExpanded, value, nameof(IsExpanded)); }
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

        /// <summary>True while the tool is still running, so the row can show that it has not answered.</summary>
        public bool IsRunning
        {
            get { return _isRunning; }
            set { Set(ref _isRunning, value, nameof(IsRunning)); }
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

        /// <summary>Appends a streamed chunk.</summary>
        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            _buffer.Append(chunk);
            Text = _buffer.ToString();
        }

        /// <summary>
        /// Marks the row finished and hands its text to the markdown view, which renders it once.
        /// </summary>
        public void Complete()
        {
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
