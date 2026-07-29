/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Renders the markdown an agent writes into a WPF FlowDocument for the native-mode chat
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Colours and metrics the renderer needs. Supplied by <see cref="MarkdownBlock"/> from theme
    /// resources, so the document follows the Visual Studio theme like the rest of the panel.
    /// </summary>
    public class MarkdownStyleOptions
    {
        public double BaseFontSize { get; set; } = 12;
        public FontFamily CodeFontFamily { get; set; } = new FontFamily("Cascadia Mono, Consolas");
        public Brush CodeBackground { get; set; } = Brushes.Transparent;
        public Brush CodeBorderBrush { get; set; } = Brushes.Gray;
        public Brush AccentBrush { get; set; } = Brushes.SteelBlue;
        public Brush MutedBrush { get; set; } = Brushes.Gray;
    }

    /// <summary>
    /// A deliberately small markdown renderer: headings, lists, block quotes, rules, tables, fenced
    /// code and the inline runs (<c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>, links).
    /// <para>
    /// Not a CommonMark implementation and not trying to be one. It covers what the agents actually
    /// emit; anything it does not recognise falls through as plain text, which is exactly what the
    /// transcript showed before it existed, so an unsupported construct can never lose content.
    /// </para>
    /// </summary>
    public static class MarkdownFlowRenderer
    {
        private static readonly Regex HeadingPattern = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RulePattern = new Regex(@"^\s{0,3}([-*_])\s*(\1\s*){2,}$", RegexOptions.Compiled);
        private static readonly Regex BulletPattern = new Regex(@"^(\s*)([-*+])\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex OrderedPattern = new Regex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex QuotePattern = new Regex(@"^\s{0,3}>\s?(.*)$", RegexOptions.Compiled);
        private static readonly Regex TableSeparatorPattern = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

        /// <summary>A bare URL with no markdown brackets around it. Trailing sentence punctuation is stripped separately.</summary>
        private static readonly Regex AutoUrlPattern = new Regex(@"https?://[^\s<>""')\]]+", RegexOptions.Compiled);

        /// <summary>
        /// A bare "path/File.ext:123" or "path/File.ext:123-456" citation with no markdown brackets.
        /// The dot-plus-extension before the colon is required so prose like "ratio 3:1" or a time
        /// "10:41" is never mistaken for a file reference.
        /// </summary>
        private static readonly Regex AutoFileRefPattern = new Regex(
            @"[A-Za-z0-9_][A-Za-z0-9_\-./\\]*\.[A-Za-z0-9]{1,10}:\d+(?:-\d+)?", RegexOptions.Compiled);

        public static FlowDocument Build(string markdown, MarkdownStyleOptions options)
        {
            if (options == null) options = new MarkdownStyleOptions();

            // FontFamily is deliberately left alone: with no local value the document inherits the host's
            // font, which is the whole point of the chat font setting. (Assigning null throws.)
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = options.BaseFontSize
            };

            try
            {
                foreach (Block block in BuildBlocks(markdown ?? string.Empty, options))
                {
                    document.Blocks.Add(block);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Markdown render failed, falling back to plain text: {ex.Message}");

                document.Blocks.Clear();
                document.Blocks.Add(new Paragraph(new Run(markdown ?? string.Empty)) { Margin = new Thickness(0) });
            }

            return document;
        }

        #region Block level

        private static IEnumerable<Block> BuildBlocks(string markdown, MarkdownStyleOptions options)
        {
            var blocks = new List<Block>();
            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                string trimmedStart = line.TrimStart();

                // ---- fenced code ----
                if (trimmedStart.StartsWith("```", StringComparison.Ordinal) ||
                    trimmedStart.StartsWith("~~~", StringComparison.Ordinal))
                {
                    string fence = trimmedStart.Substring(0, 3);
                    string language = trimmedStart.Substring(3).Trim();
                    var code = new List<string>();
                    i++;

                    while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    {
                        code.Add(lines[i]);
                        i++;
                    }

                    // An unterminated fence is what an interrupted turn looks like; emit what arrived.
                    if (i < lines.Length) i++;

                    blocks.Add(BuildCodeBlock(string.Join("\n", code), language, options));
                    continue;
                }

                // ---- heading ----
                Match heading = HeadingPattern.Match(line);
                if (heading.Success)
                {
                    blocks.Add(BuildHeading(heading.Groups[1].Value.Length, heading.Groups[2].Value, options));
                    i++;
                    continue;
                }

                // ---- horizontal rule ----
                if (RulePattern.IsMatch(line))
                {
                    blocks.Add(BuildRule(options));
                    i++;
                    continue;
                }

                // ---- table ----
                if (line.IndexOf('|') >= 0 && i + 1 < lines.Length && TableSeparatorPattern.IsMatch(lines[i + 1]))
                {
                    var rows = new List<string>();
                    string header = line;
                    i += 2;
                    while (i < lines.Length && lines[i].IndexOf('|') >= 0 && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        rows.Add(lines[i]);
                        i++;
                    }

                    blocks.Add(BuildTable(header, rows, options));
                    continue;
                }

                // ---- block quote ----
                if (QuotePattern.IsMatch(line))
                {
                    var quoted = new List<string>();
                    while (i < lines.Length && QuotePattern.IsMatch(lines[i]))
                    {
                        quoted.Add(QuotePattern.Match(lines[i]).Groups[1].Value);
                        i++;
                    }

                    blocks.Add(BuildQuote(string.Join(" ", quoted), options));
                    continue;
                }

                // ---- list ----
                if (BulletPattern.IsMatch(line) || OrderedPattern.IsMatch(line))
                {
                    var items = new List<ListEntry>();
                    while (i < lines.Length)
                    {
                        Match bullet = BulletPattern.Match(lines[i]);
                        Match ordered = bullet.Success ? Match.Empty : OrderedPattern.Match(lines[i]);

                        if (bullet.Success)
                        {
                            items.Add(new ListEntry
                            {
                                Level = IndentLevel(bullet.Groups[1].Value),
                                Ordered = false,
                                Text = bullet.Groups[3].Value
                            });
                            i++;
                        }
                        else if (ordered.Success)
                        {
                            items.Add(new ListEntry
                            {
                                Level = IndentLevel(ordered.Groups[1].Value),
                                Ordered = true,
                                Text = ordered.Groups[3].Value
                            });
                            i++;
                        }
                        else if (items.Count > 0 && !string.IsNullOrWhiteSpace(lines[i]) &&
                                 lines[i].StartsWith(" ", StringComparison.Ordinal) &&
                                 !HeadingPattern.IsMatch(lines[i]))
                        {
                            // Continuation of the previous item, wrapped onto its own line.
                            items[items.Count - 1].Text += " " + lines[i].Trim();
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    int consumed = 0;
                    blocks.Add(BuildList(items, 0, ref consumed, options));
                    continue;
                }

                // ---- paragraph ----
                var paragraphLines = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    string candidate = lines[i];
                    string candidateStart = candidate.TrimStart();

                    if (candidateStart.StartsWith("```", StringComparison.Ordinal) ||
                        candidateStart.StartsWith("~~~", StringComparison.Ordinal) ||
                        HeadingPattern.IsMatch(candidate) ||
                        RulePattern.IsMatch(candidate) ||
                        BulletPattern.IsMatch(candidate) ||
                        OrderedPattern.IsMatch(candidate) ||
                        QuotePattern.IsMatch(candidate))
                    {
                        break;
                    }

                    paragraphLines.Add(candidate);
                    i++;
                }

                if (paragraphLines.Count == 0)
                {
                    // Nothing matched and nothing consumed — emit the line raw rather than spin forever.
                    paragraphLines.Add(lines[i]);
                    i++;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                AppendInlines(paragraph.Inlines, string.Join("\n", paragraphLines), options);
                blocks.Add(paragraph);
            }

            return blocks;
        }

        private class ListEntry
        {
            public int Level;
            public bool Ordered;
            public string Text = string.Empty;
        }

        private static int IndentLevel(string indent)
        {
            if (string.IsNullOrEmpty(indent)) return 0;

            int spaces = 0;
            foreach (char c in indent)
            {
                spaces += c == '\t' ? 4 : 1;
            }

            return Math.Min(4, spaces / 2);
        }

        /// <summary>
        /// Builds one list level, recursing into deeper-indented runs. <paramref name="index"/> walks
        /// the flat item list so a nested run is consumed by the recursive call, not twice.
        /// </summary>
        private static List BuildList(IList<ListEntry> items, int index, ref int consumed, MarkdownStyleOptions options)
        {
            int level = items[index].Level;

            var list = new List
            {
                Margin = new Thickness(level == 0 ? 4 : 0, 0, 0, 6),
                Padding = new Thickness(16, 0, 0, 0),
                MarkerStyle = items[index].Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
            };

            int i = index;
            while (i < items.Count)
            {
                if (items[i].Level < level)
                {
                    break;
                }

                if (items[i].Level > level)
                {
                    int inner = 0;
                    List nested = BuildList(items, i, ref inner, options);

                    if (list.ListItems.Count > 0)
                    {
                        list.ListItems.LastListItem.Blocks.Add(nested);
                    }
                    else
                    {
                        // A run that starts deeper than its parent (the agent skipped a level): give it
                        // an empty item to hang from rather than dropping it.
                        var holder = new ListItem();
                        holder.Blocks.Add(nested);
                        list.ListItems.Add(holder);
                    }

                    i += inner;
                    continue;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                AppendInlines(paragraph.Inlines, items[i].Text, options);
                list.ListItems.Add(new ListItem(paragraph));
                i++;
            }

            consumed = i - index;
            return list;
        }

        private static Block BuildHeading(int level, string text, MarkdownStyleOptions options)
        {
            // Chat headings are structure, not a document title: even an H1 stays close to body size,
            // otherwise one "# Summary" dwarfs the answer under it.
            double[] scale = { 1.35, 1.22, 1.12, 1.05, 1.0, 1.0 };

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, level <= 2 ? 10 : 8, 0, 4),
                FontSize = options.BaseFontSize * scale[Math.Min(scale.Length - 1, Math.Max(0, level - 1))],
                FontWeight = FontWeights.SemiBold
            };

            AppendInlines(paragraph.Inlines, text, options);
            return paragraph;
        }

        private static Block BuildRule(MarkdownStyleOptions options)
        {
            return new BlockUIContainer(new Border
            {
                Height = 1,
                Background = options.CodeBorderBrush,
                Opacity = 0.6,
                Margin = new Thickness(0, 6, 0, 8)
            })
            {
                Margin = new Thickness(0)
            };
        }

        private static Block BuildQuote(string text, MarkdownStyleOptions options)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 2, 0, 2),
                BorderBrush = options.AccentBrush,
                BorderThickness = new Thickness(3, 0, 0, 0)
            };

            AppendInlines(paragraph.Inlines, text, options);
            return paragraph;
        }

        private static Block BuildCodeBlock(string code, string language, MarkdownStyleOptions options)
        {
            var section = new Section { Margin = new Thickness(0, 2, 0, 8) };

            if (!string.IsNullOrEmpty(language))
            {
                section.Blocks.Add(new Paragraph(new Run(language))
                {
                    Margin = new Thickness(2, 0, 0, 2),
                    FontSize = Math.Max(9, options.BaseFontSize - 2),
                    Foreground = options.MutedBrush
                });
            }

            var body = new Paragraph(new Run(code.TrimEnd('\n')))
            {
                Margin = new Thickness(0),
                Padding = new Thickness(9, 7, 9, 7),
                Background = options.CodeBackground,
                BorderBrush = options.CodeBorderBrush,
                BorderThickness = new Thickness(1),
                FontFamily = options.CodeFontFamily,
                FontSize = Math.Max(9, options.BaseFontSize - 1)
            };

            section.Blocks.Add(body);
            return section;
        }

        private static Block BuildTable(string headerLine, IList<string> rowLines, MarkdownStyleOptions options)
        {
            string[] headers = SplitTableRow(headerLine);

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 8) };
            for (int c = 0; c < headers.Length; c++)
            {
                table.Columns.Add(new TableColumn());
            }

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            var headerRow = new TableRow { FontWeight = FontWeights.SemiBold };
            foreach (string header in headers)
            {
                headerRow.Cells.Add(BuildTableCell(header, options, true));
            }
            group.Rows.Add(headerRow);

            foreach (string rowLine in rowLines)
            {
                string[] cells = SplitTableRow(rowLine);
                var row = new TableRow();

                for (int c = 0; c < headers.Length; c++)
                {
                    row.Cells.Add(BuildTableCell(c < cells.Length ? cells[c] : string.Empty, options, false));
                }

                group.Rows.Add(row);
            }

            return table;
        }

        private static TableCell BuildTableCell(string text, MarkdownStyleOptions options, bool isHeader)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            AppendInlines(paragraph.Inlines, text, options);

            return new TableCell(paragraph)
            {
                Padding = new Thickness(8, 4, 8, 4),
                BorderBrush = options.CodeBorderBrush,
                BorderThickness = isHeader ? new Thickness(0, 0, 0, 1) : new Thickness(0)
            };
        }

        private static string[] SplitTableRow(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            string[] cells = trimmed.Split('|');
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = cells[i].Trim();
            }

            return cells;
        }

        #endregion

        #region Inline level

        /// <summary>
        /// Appends the inline runs of one paragraph: inline code, bold, italic and links. A marker with
        /// no closing partner is emitted literally — half-typed emphasis is normal while text streams.
        /// </summary>
        public static void AppendInlines(InlineCollection target, string text, MarkdownStyleOptions options)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var buffer = new StringBuilder();
            int i = 0;

            Action flush = delegate
            {
                if (buffer.Length > 0)
                {
                    target.Add(new Run(buffer.ToString()));
                    buffer.Clear();
                }
            };

            while (i < text.Length)
            {
                char c = text[i];

                if (c == '\n')
                {
                    flush();
                    target.Add(new LineBreak());
                    i++;
                    continue;
                }

                if (c == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close > i + 1)
                    {
                        flush();
                        AppendCodeSpanInlines(target, text.Substring(i + 1, close - i - 1), options);
                        i = close + 1;
                        continue;
                    }
                }

                if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
                {
                    string marker = new string(c, 2);
                    int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        flush();
                        var bold = new Bold();
                        AppendInlines(bold.Inlines, text.Substring(i + 2, close - i - 2), options);
                        target.Add(bold);
                        i = close + 2;
                        continue;
                    }
                }

                if (c == '*' || c == '_')
                {
                    int close = text.IndexOf(c, i + 1);
                    if (close > i + 1 && !char.IsWhiteSpace(text[i + 1]))
                    {
                        flush();
                        var italic = new Italic();
                        AppendInlines(italic.Inlines, text.Substring(i + 1, close - i - 1), options);
                        target.Add(italic);
                        i = close + 1;
                        continue;
                    }
                }

                if (c == '[')
                {
                    int labelEnd = text.IndexOf(']', i + 1);
                    if (labelEnd > i && labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
                    {
                        int urlEnd = text.IndexOf(')', labelEnd + 2);
                        if (urlEnd > labelEnd)
                        {
                            flush();
                            string label = text.Substring(i + 1, labelEnd - i - 1);
                            string url = text.Substring(labelEnd + 2, urlEnd - labelEnd - 2).Trim();

                            target.Add(BuildLink(label, url, options));
                            i = urlEnd + 1;
                            continue;
                        }
                    }
                }

                // Agents do not always bother with [label](url) syntax — a bare "https://…" or a
                // "Logger.cs:41" citation is at least as common as the bracketed form, and both should
                // still be clickable rather than only working when the agent happens to format them.
                if (c == 'h')
                {
                    Match urlMatch = AutoUrlPattern.Match(text, i);
                    if (urlMatch.Success && urlMatch.Index == i)
                    {
                        string url = TrimTrailingPunctuation(urlMatch.Value);
                        flush();
                        target.Add(BuildLink(url, url, options));
                        i += url.Length;
                        continue;
                    }
                }

                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    Match refMatch = AutoFileRefPattern.Match(text, i);
                    if (refMatch.Success && refMatch.Index == i)
                    {
                        flush();
                        target.Add(BuildLink(refMatch.Value, refMatch.Value, options));
                        i += refMatch.Value.Length;
                        continue;
                    }
                }

                buffer.Append(c);
                i++;
            }

            flush();
        }

        /// <summary>
        /// Agents routinely wrap a whole citation in one code span, e.g. "`Program.cs:56 — public async
        /// Task Foo()`" — the file:line prefix still needs to be clickable, but the rest is genuine code
        /// text. Matched URL/file-ref segments become links with no code background (so they read as a
        /// link, not an inert code block); everything else keeps the normal monospace code styling.
        /// </summary>
        private static void AppendCodeSpanInlines(InlineCollection target, string inner, MarkdownStyleOptions options)
        {
            var buffer = new StringBuilder();
            int i = 0;

            Action flush = delegate
            {
                if (buffer.Length > 0)
                {
                    target.Add(new Run(buffer.ToString())
                    {
                        FontFamily = options.CodeFontFamily,
                        Background = options.CodeBackground
                    });
                    buffer.Clear();
                }
            };

            while (i < inner.Length)
            {
                char c = inner[i];

                if (c == 'h')
                {
                    Match urlMatch = AutoUrlPattern.Match(inner, i);
                    if (urlMatch.Success && urlMatch.Index == i)
                    {
                        string url = TrimTrailingPunctuation(urlMatch.Value);
                        flush();
                        var link = (Run)BuildLink(url, url, options);
                        link.FontFamily = options.CodeFontFamily;
                        target.Add(link);
                        i += url.Length;
                        continue;
                    }
                }

                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    Match refMatch = AutoFileRefPattern.Match(inner, i);
                    if (refMatch.Success && refMatch.Index == i)
                    {
                        flush();
                        var link = (Run)BuildLink(refMatch.Value, refMatch.Value, options);
                        link.FontFamily = options.CodeFontFamily;
                        target.Add(link);
                        i += refMatch.Value.Length;
                        continue;
                    }
                }

                buffer.Append(c);
                i++;
            }

            flush();
        }

        /// <summary>Drops sentence punctuation an autodetected URL swept up, e.g. the period ending "...see https://x.com/y." </summary>
        private static string TrimTrailingPunctuation(string url)
        {
            int end = url.Length;
            while (end > 0 && ".,;:!?)]}".IndexOf(url[end - 1]) >= 0)
            {
                end--;
            }

            return url.Substring(0, end);
        }

        /// <summary>
        /// A link is a coloured, underlined run rather than a WPF <see cref="Hyperlink"/>: the document
        /// lives in a read-only rich text box whose whole job is selecting and copying, and a live
        /// hyperlink swallows the click that starts a selection. <see cref="MarkdownBlock"/> instead
        /// hit-tests a plain click (down and up in the same spot) against <see cref="Run.Tag"/>, which
        /// carries the URL, so dragging across the link to select text still works.
        /// </summary>
        private static Inline BuildLink(string label, string url, MarkdownStyleOptions options)
        {
            var run = new Run(string.IsNullOrEmpty(label) ? url : label)
            {
                Foreground = options.AccentBrush,
                TextDecorations = TextDecorations.Underline,
                ToolTip = url,
                Tag = url,
                Cursor = Cursors.Hand
            };

            return run;
        }

        #endregion
    }

    /// <summary>
    /// Read-only rich text box that renders its <see cref="Markdown"/> property. Read-only rather than a
    /// <c>TextBlock</c> because copying an answer is the single most common thing a user does with a
    /// chat, and a <c>TextBlock</c> cannot be selected on .NET Framework.
    /// </summary>
    public class MarkdownBlock : RichTextBox
    {
        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            "Markdown", typeof(string), typeof(MarkdownBlock),
            new PropertyMetadata(string.Empty, OnVisualInputChanged));

        public static readonly DependencyProperty CodeBackgroundProperty = DependencyProperty.Register(
            "CodeBackground", typeof(Brush), typeof(MarkdownBlock),
            new PropertyMetadata(Brushes.Transparent, OnVisualInputChanged));

        public static readonly DependencyProperty CodeBorderBrushProperty = DependencyProperty.Register(
            "CodeBorderBrush", typeof(Brush), typeof(MarkdownBlock),
            new PropertyMetadata(Brushes.Gray, OnVisualInputChanged));

        public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
            "AccentBrush", typeof(Brush), typeof(MarkdownBlock),
            new PropertyMetadata(Brushes.SteelBlue, OnVisualInputChanged));

        public static readonly DependencyProperty CodeFontFamilyProperty = DependencyProperty.Register(
            "CodeFontFamily", typeof(FontFamily), typeof(MarkdownBlock),
            new PropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), OnVisualInputChanged));

        /// <summary>Largest mouse-down-to-up distance, in DIPs, still treated as a click rather than a drag-select.</summary>
        private const double ClickDragTolerance = 3.0;

        private Point _mouseDownPosition;
        private bool _trackingClick;

        public MarkdownBlock()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsDocumentEnabled = false;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            MinHeight = 0;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        /// <summary>Raised when a rendered link is clicked, carrying its URL (or bare file reference).</summary>
        public event EventHandler<string> LinkClicked;

        /// <summary>
        /// Records where the click started. Selection still begins normally underneath this — nothing
        /// here is marked <c>Handled</c> — so a drag over link text keeps working.
        /// </summary>
        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _mouseDownPosition = e.GetPosition(this);
            _trackingClick = true;
            base.OnPreviewMouseLeftButtonDown(e);
        }

        /// <summary>
        /// A plain click (up lands within <see cref="ClickDragTolerance"/> of where it went down) that
        /// lands on a link run fires <see cref="LinkClicked"/>; anything that moved is a selection drag
        /// and is left alone.
        /// </summary>
        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);

            if (!_trackingClick)
            {
                return;
            }

            _trackingClick = false;
            Point upPosition = e.GetPosition(this);

            if (Math.Abs(upPosition.X - _mouseDownPosition.X) > ClickDragTolerance ||
                Math.Abs(upPosition.Y - _mouseDownPosition.Y) > ClickDragTolerance)
            {
                return;
            }

            string url = HitTestLink(upPosition);
            if (!string.IsNullOrEmpty(url))
            {
                LinkClicked?.Invoke(this, url);
            }
        }

        /// <summary>Walks up from the clicked point to the nearest text element carrying a link's URL in its Tag.</summary>
        private string HitTestLink(Point position)
        {
            TextPointer pointer;
            try
            {
                pointer = GetPositionFromPoint(position, true);
            }
            catch (Exception)
            {
                return null;
            }

            DependencyObject element = pointer?.Parent;
            while (element is TextElement current)
            {
                if (current.Tag is string url && !string.IsNullOrEmpty(url))
                {
                    return url;
                }

                element = current.Parent;
            }

            return null;
        }

        public string Markdown
        {
            get { return (string)GetValue(MarkdownProperty); }
            set { SetValue(MarkdownProperty, value); }
        }

        public Brush CodeBackground
        {
            get { return (Brush)GetValue(CodeBackgroundProperty); }
            set { SetValue(CodeBackgroundProperty, value); }
        }

        public Brush CodeBorderBrush
        {
            get { return (Brush)GetValue(CodeBorderBrushProperty); }
            set { SetValue(CodeBorderBrushProperty, value); }
        }

        public Brush AccentBrush
        {
            get { return (Brush)GetValue(AccentBrushProperty); }
            set { SetValue(AccentBrushProperty, value); }
        }

        public FontFamily CodeFontFamily
        {
            get { return (FontFamily)GetValue(CodeFontFamilyProperty); }
            set { SetValue(CodeFontFamilyProperty, value); }
        }

        /// <summary>
        /// Headings scale off the base size, so a font-size change has to rebuild the document — the
        /// scaled sizes are baked into it and would otherwise stay at the previous scale.
        /// </summary>
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.Property == FontSizeProperty)
            {
                Rebuild();
            }
        }

        private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownBlock)d).Rebuild();
        }

        private void Rebuild()
        {
            Document = MarkdownFlowRenderer.Build(Markdown, new MarkdownStyleOptions
            {
                BaseFontSize = FontSize,
                CodeFontFamily = CodeFontFamily,
                CodeBackground = CodeBackground,
                CodeBorderBrush = CodeBorderBrush,
                AccentBrush = AccentBrush,
                MutedBrush = Foreground
            });
        }
    }
}
