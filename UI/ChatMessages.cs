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
    /// A run of message text that is either prose or a fenced code block.
    /// </summary>
    public class ChatSegment
    {
        public bool IsCode { get; set; }

        /// <summary>Language tag from the opening fence, empty when none was given.</summary>
        public string Language { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// One row of the chat transcript.
    /// <para>
    /// Streaming appends into <see cref="Text"/> and the row renders as plain text; splitting markdown
    /// on every token delta would re-layout the list dozens of times a second. <see cref="Complete"/>
    /// is called when the turn ends and swaps in the parsed <see cref="Segments"/>. That is why the
    /// object is mutated rather than replaced — recreating the item makes the list flicker and drops
    /// the user's text selection.
    /// </para>
    /// </summary>
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        private readonly StringBuilder _buffer = new StringBuilder();
        private string _text = string.Empty;
        private bool _isStreaming;
        private bool _isExpanded;
        private string _header = string.Empty;
        private string _toolResult = string.Empty;
        private bool _isError;

        public ChatMessageViewModel(ChatMessageKind kind)
        {
            Kind = kind;
            Segments = new ObservableCollection<ChatSegment>();
        }

        public ChatMessageKind Kind { get; }

        public bool IsUser { get { return Kind == ChatMessageKind.User; } }

        public string Text
        {
            get { return _text; }
            set { Set(ref _text, value ?? string.Empty, nameof(Text)); }
        }

        /// <summary>Parsed prose/code runs. Populated by <see cref="Complete"/>.</summary>
        public ObservableCollection<ChatSegment> Segments { get; }

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
        /// Marks the row finished and parses its markdown once.
        /// </summary>
        public void Complete()
        {
            IsStreaming = false;

            Segments.Clear();
            foreach (ChatSegment segment in MarkdownSplitter.Split(Text))
            {
                Segments.Add(segment);
            }
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

    /// <summary>
    /// Splits agent output into prose and fenced code blocks.
    /// <para>
    /// Deliberately not a markdown engine: code fences are the only construct whose loss actually hurts
    /// readability in a chat panel, and they are the one thing a plain <c>TextBlock</c> renders badly.
    /// If full fidelity is ever needed, the upgrade path is WebView2, already a project dependency.
    /// </para>
    /// </summary>
    public static class MarkdownSplitter
    {
        public static IReadOnlyList<ChatSegment> Split(string text)
        {
            var segments = new List<ChatSegment>();
            if (string.IsNullOrEmpty(text))
            {
                return segments;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var current = new StringBuilder();
            bool inCode = false;
            string language = string.Empty;

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    Flush(segments, current, inCode, language);

                    if (inCode)
                    {
                        inCode = false;
                        language = string.Empty;
                    }
                    else
                    {
                        inCode = true;
                        language = trimmed.Substring(3).Trim();
                    }

                    continue;
                }

                if (current.Length > 0)
                {
                    current.Append('\n');
                }
                current.Append(line);
            }

            // An unterminated fence is normal when a turn is interrupted mid-code-block; emit what we
            // have rather than dropping it.
            Flush(segments, current, inCode, language);

            return segments;
        }

        private static void Flush(List<ChatSegment> segments, StringBuilder buffer, bool isCode, string language)
        {
            string content = buffer.ToString();
            buffer.Clear();

            // Blank prose between two fences is layout noise; blank lines inside code are meaningful.
            if (!isCode && string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            segments.Add(new ChatSegment
            {
                IsCode = isCode,
                Language = language ?? string.Empty,
                Text = isCode ? content.TrimEnd('\n') : content.Trim('\n')
            });
        }
    }
}
