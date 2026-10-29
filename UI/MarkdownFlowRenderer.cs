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
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using static ClaudeCodeVS.UI.Loc;

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
        public Brush TextBrush { get; set; } = Brushes.Black;

        /// <summary>Style for the copy button in each code block's top-right corner; null leaves the button out.</summary>
        public Style CodeCopyButtonStyle { get; set; }

        /// <summary>
        /// When true a code block's viewer builds its document only once it is loaded. Set for the
        /// unfinished tail of a streaming reply, which is rendered again on every update and mostly
        /// thrown away (its code goes into the block already shown); everywhere else the viewer builds
        /// at once, so the block does not lay out empty for a frame and then grow.
        /// </summary>
        internal bool DeferCodeRendering { get; set; }

        internal MarkdownStyleOptions WithDeferredCode()
        {
            var copy = (MarkdownStyleOptions)MemberwiseClone();
            copy.DeferCodeRendering = true;
            return copy;
        }
    }

    /// <summary>
    /// A deliberately small markdown renderer: headings, lists, block quotes, rules, tables, fenced
    /// code, the inline runs (<c>**bold**</c>, <c>*italic*</c>, <c>~~strike~~</c>, <c>`code`</c>, links)
    /// and a handful of bare inline HTML tags.
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
        private static readonly Regex TaskPattern = new Regex(@"^\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
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

            FlowDocument document = CreateDocument(options);
            foreach (Block block in BuildBlockList(markdown, options))
            {
                document.Blocks.Add(block);
            }

            return document;
        }

        /// <summary>An empty document with the renderer's page settings, for callers that fill it themselves.</summary>
        public static FlowDocument CreateDocument(MarkdownStyleOptions options)
        {
            // FontFamily is deliberately left alone: MarkdownBlock binds it to its own font (the chat font
            // setting) when the document is shown. (Assigning null throws.)
            return new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = (options ?? new MarkdownStyleOptions()).BaseFontSize
            };
        }

        /// <summary>
        /// The blocks for one piece of markdown, falling back to a single plain paragraph if the render
        /// fails. Rendering a text in pieces split at <see cref="FindStableBoundary"/> gives the same
        /// blocks as rendering it whole, which is what lets a streaming reply be rendered incrementally.
        /// </summary>
        public static List<Block> BuildBlockList(string markdown, MarkdownStyleOptions options)
        {
            if (options == null) options = new MarkdownStyleOptions();

            try
            {
                return BuildBlocks(markdown ?? string.Empty, options);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Markdown render failed, falling back to plain text: {ex.Message}");
                return new List<Block> { new Paragraph(new Run(markdown ?? string.Empty)) { Margin = new Thickness(0) } };
            }
        }

        /// <summary>
        /// The end of the last finished blocks in a text that is still growing: just past the last
        /// complete blank line that is not inside a fenced code block. Every block the parser knows
        /// (paragraph, list, table, quote, heading, rule) ends at a blank line and none looks back
        /// across one, so the text before the boundary renders the same no matter what is appended —
        /// only a code fence spans blank lines, hence the fence tracking. A blank line counts only once
        /// its newline has arrived: "para\n" followed by "more" is still one paragraph.
        /// </summary>
        /// <param name="text">The text so far.</param>
        /// <param name="start">A previous boundary (or 0) to scan from; it must not be inside a fence.</param>
        /// <returns><paramref name="start"/> when no new boundary was found.</returns>
        public static int FindStableBoundary(string text, int start)
        {
            if (string.IsNullOrEmpty(text) || start < 0 || start >= text.Length)
            {
                return Math.Max(0, Math.Min(start, text?.Length ?? 0));
            }

            int boundary = start;
            string openFence = null;
            int lineStart = start;

            while (lineStart < text.Length)
            {
                int newline = text.IndexOf('\n', lineStart);
                if (newline < 0)
                {
                    // The last line is still being written; it cannot decide anything yet.
                    break;
                }

                if (openFence != null)
                {
                    // Same test the parser uses to close a fence: the trimmed line starts with it.
                    if (LineStartsWith(text, lineStart, newline, openFence))
                    {
                        openFence = null;
                    }
                }
                else if (IsBlankLine(text, lineStart, newline))
                {
                    boundary = newline + 1;
                }
                else if (LineStartsWith(text, lineStart, newline, "```"))
                {
                    openFence = "```";
                }
                else if (LineStartsWith(text, lineStart, newline, "~~~"))
                {
                    openFence = "~~~";
                }

                lineStart = newline + 1;
            }

            return boundary;
        }

        private static bool IsBlankLine(string text, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(text[i])) return false;
            }

            return true;
        }

        /// <summary>Whether the line, with its leading whitespace skipped, starts with <paramref name="prefix"/>.</summary>
        private static bool LineStartsWith(string text, int start, int end, string prefix)
        {
            int i = start;
            while (i < end && char.IsWhiteSpace(text[i])) i++;

            return end - i >= prefix.Length && string.CompareOrdinal(text, i, prefix, 0, prefix.Length) == 0;
        }

        /// <summary>
        /// Plain text with only its http(s) URLs turned into links — for status notices and errors, whose
        /// text is not markdown: an underscore in a path or a "#" in a CLI message must stay literal.
        /// </summary>
        public static FlowDocument BuildPlain(string text, MarkdownStyleOptions options)
        {
            if (options == null) options = new MarkdownStyleOptions();

            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = options.BaseFontSize
            };

            var paragraph = new Paragraph { Margin = new Thickness(0) };
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int l = 0; l < lines.Length; l++)
            {
                if (l > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                string line = lines[l];
                int start = 0;
                foreach (Match match in AutoUrlPattern.Matches(line))
                {
                    if (match.Index < start)
                    {
                        continue;
                    }

                    string url = TrimTrailingPunctuation(match.Value);
                    if (match.Index > start)
                    {
                        paragraph.Inlines.Add(new Run(line.Substring(start, match.Index - start)));
                    }

                    paragraph.Inlines.Add(BuildLink(url, url, options));
                    start = match.Index + url.Length;
                }

                if (start < line.Length)
                {
                    paragraph.Inlines.Add(new Run(line.Substring(start)));
                }
            }

            document.Blocks.Add(paragraph);
            return document;
        }

        #region Block level

        private static List<Block> BuildBlocks(string markdown, MarkdownStyleOptions options)
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
                    string separator = lines[i + 1];
                    i += 2;
                    while (i < lines.Length && lines[i].IndexOf('|') >= 0 && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        rows.Add(lines[i]);
                        i++;
                    }

                    blocks.Add(BuildTable(header, separator, rows, options));
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

                    blocks.Add(BuildQuote(string.Join("\n", quoted), options));
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
                                Number = int.Parse(ordered.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
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

        /// <summary>
        /// The done box: U+2705 (green check) on a light background; on a dark one the softer purple
        /// U+2611 U+FE0F (emoji presentation), which also pairs with the square of the open box there.
        /// </summary>
        internal static string TaskDoneBox(bool lightText)
        {
            return lightText ? "\u2611\ufe0f" : "\u2705";
        }

        /// <summary>
        /// The open box: U+1F532 (dark frame) on a light background, U+1F533 (light frame) on a dark
        /// one, where the dark frame melts into the background. A theme switch changes the block's
        /// foreground, which rebuilds the document, so the box follows the theme.
        /// </summary>
        internal static string TaskOpenBox(bool lightText)
        {
            return lightText ? "\U0001F533" : "\U0001F532";
        }

        /// <summary>Whether a text brush is light, i.e. the text sits on a dark theme. Not a solid colour: false.</summary>
        internal static bool IsLightText(Brush brush)
        {
            if (!(brush is SolidColorBrush solid))
            {
                return false;
            }

            Color c = solid.Color;
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.5;
        }
        private static readonly FontFamily TaskBoxFont = new FontFamily("Segoe UI Emoji");

        private class ListEntry
        {
            public int Level;
            public bool Ordered;
            public int Number;
            public string Text = string.Empty;
        }

        /// <summary>
        /// Indent of a numbered list, sized for its widest marker. WPF draws the marker right-aligned in
        /// the list's left padding and lets whatever does not fit spill out to the left, where the
        /// document viewer clips it: with a fixed indent, "10." showed as "0." (and a sliver of the 1)
        /// at a 16 px chat font, and "100." lost a whole digit. Each character is budgeted at 0.6 em,
        /// which covers monospaced digits too.
        /// </summary>
        /// <param name="largestNumber">The highest number the list shows.</param>
        /// <param name="fontSize">The list's font size in device-independent pixels.</param>
        /// <param name="markerOffset">Gap between the marker and the item text.</param>
        /// <returns>The left padding of the list.</returns>
        internal static double OrderedListIndent(int largestNumber, double fontSize, out double markerOffset)
        {
            int digits = Math.Max(1, largestNumber).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
            markerOffset = Math.Round(0.4 * fontSize);

            // digits + the period
            return markerOffset + Math.Ceiling((digits + 1) * 0.6 * fontSize);
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

            if (items[index].Ordered)
            {
                // A list that continues after a paragraph ("4. ...") keeps its numbers. WPF has no
                // marker 0, so a list written from 0 starts at 1.
                list.StartIndex = Math.Max(1, items[index].Number);
            }

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
                string text = items[i].Text;
                Match task = TaskPattern.Match(text);
                if (task.Success)
                {
                    // A task-list item ("- [ ] todo", "- [x] done") shows a read-only box instead of
                    // its brackets: a check emoji when done, an empty square when open (both by theme, below).
                    // Both come from Segoe UI Emoji, pinned so the chat font cannot change them: emoji
                    // glyphs share one width, so done and open items line up, and the check keeps its
                    // colour (a symbol font such as Segoe UI Symbol only has a monochrome outline of it).
                    bool done = task.Groups[1].Value != " ";
                    bool lightText = IsLightText(options.TextBrush);
                    string box = done ? TaskDoneBox(lightText) : TaskOpenBox(lightText);
                    paragraph.Inlines.Add(new Run(box) { FontFamily = TaskBoxFont });
                    paragraph.Inlines.Add(new Run(" "));
                    text = task.Groups[2].Value;
                }

                AppendInlines(paragraph.Inlines, text, options);
                list.ListItems.Add(new ListItem(paragraph));
                i++;
            }

            if (list.MarkerStyle == TextMarkerStyle.Decimal)
            {
                list.Padding = new Thickness(
                    OrderedListIndent(list.StartIndex + list.ListItems.Count - 1, options.BaseFontSize, out double markerOffset),
                    0, 0, 0);
                list.MarkerOffset = markerOffset;
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

        /// <summary>
        /// A quote is a bordered <see cref="Section"/> whose content (the lines without their "&gt;")
        /// goes through the block parser again, so lists, code, tables and nested quotes work inside it.
        /// </summary>
        private static Block BuildQuote(string text, MarkdownStyleOptions options)
        {
            var section = new Section
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 2, 0, 2),
                BorderBrush = options.AccentBrush,
                BorderThickness = new Thickness(3, 0, 0, 0)
            };

            foreach (Block block in BuildBlocks(text, options))
            {
                section.Blocks.Add(block);
            }

            // The last block's own bottom gap would only widen the bar below the text.
            Block last = section.Blocks.LastBlock;
            if (last != null)
            {
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
            }

            return section;
        }

        private const string CopyCodeGlyph = "⧉";

        private static Block BuildCodeBlock(string code, string language, MarkdownStyleOptions options)
        {
            string body = code.TrimEnd('\n');

            // One bordered section: the top row holds the language label (left) and the copy button (top-right
            // corner); the code below sits in its own document viewer (BuildCodeViewer) so long lines scroll
            // sideways. Not a TextBox: TextBoxView's soft-wrap metrics are what crash the process on
            // .NET Framework (dotnet/wpf#11481). Not a bare TextBlock in the UI container either: the document
            // treats an embedded element as one unselectable object, and a drag that starts on it moves the
            // caret past the block, which scrolled the transcript to the block's end.
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(9, 4, 4, 0) };

            if (!string.IsNullOrEmpty(language))
            {
                header.Children.Add(new TextBlock
                {
                    Text = language,
                    FontSize = Math.Max(9, options.BaseFontSize - 2),
                    Foreground = options.MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            Button copyButton = null;
            if (options.CodeCopyButtonStyle != null)
            {
                copyButton = new Button
                {
                    Content = CopyCodeGlyph,
                    Tag = body,
                    Style = options.CodeCopyButtonStyle
                };
                copyButton.Click += CopyCodeButton_Click;
                DockPanel.SetDock(copyButton, Dock.Right);
                header.Children.Add(copyButton);
            }

            var section = new Section
            {
                Margin = new Thickness(0, 2, 0, 8),
                Background = options.CodeBackground,
                BorderBrush = options.CodeBorderBrush,
                BorderThickness = new Thickness(1)
            };
            MarkdownBlock viewer = BuildCodeViewer(body, options);
            section.Blocks.Add(new BlockUIContainer(header) { Margin = new Thickness(0) });
            section.Blocks.Add(new BlockUIContainer(viewer) { Margin = new Thickness(0) });
            section.Tag = new CodeBlockParts(language, copyButton, viewer);
            return section;
        }

        /// <summary>The parts of a fenced code block that change while its code is still streaming.</summary>
        private sealed class CodeBlockParts
        {
            public CodeBlockParts(string language, Button copyButton, MarkdownBlock viewer)
            {
                Language = language ?? string.Empty;
                CopyButton = copyButton;
                Viewer = viewer;
            }

            public string Language { get; }
            public Button CopyButton { get; }
            public MarkdownBlock Viewer { get; }
        }

        /// <summary>
        /// Gives the code block <paramref name="existing"/> the code of <paramref name="fresh"/>, a newer
        /// render of the same block, instead of swapping the block. A streaming reply renders its open code
        /// block again on every update; a new block lays out from nothing, so the transcript jumped by the
        /// block's height on each chunk, and the old viewer's page width had to be measured again over all
        /// lines. False when either is not a code block or the language differs (a different block).
        /// </summary>
        internal static bool TryUpdateCodeBlock(Block existing, Block fresh)
        {
            if (!(existing?.Tag is CodeBlockParts current) || !(fresh?.Tag is CodeBlockParts next) ||
                !string.Equals(current.Language, next.Language, StringComparison.Ordinal) ||
                (current.CopyButton == null) != (next.CopyButton == null))
            {
                return false;
            }

            string body = next.Viewer.Markdown;
            if (current.CopyButton != null)
            {
                current.CopyButton.Tag = body;
            }

            current.Viewer.Markdown = body;
            return true;
        }

        /// <summary>
        /// The code in its own <see cref="MarkdownBlock"/>, set up like raw tool output (plain text,
        /// <see cref="MarkdownBlock.IsNoWrap"/>): long lines stay on one line and the block scrolls
        /// sideways instead of wrapping. A paragraph of the reply's document cannot do that, since a
        /// FlowDocument wraps every paragraph at its page width. It is a document viewer, not a TextBox
        /// (dotnet/wpf#11481): the code selects and copies within the block, and the viewer handles its
        /// own mouse input, so a drag starting on it does not move the reply's caret. Vertical scrolling
        /// stays disabled, so the wheel keeps scrolling the transcript. Links in the code and the
        /// selection colour come from the reply's block (see <see cref="MarkdownBlock"/>'s Loaded).
        /// Every input is set here, before Markdown, so it can build at once instead of on Loaded
        /// (see <see cref="MarkdownStyleOptions.DeferCodeRendering"/>).
        /// </summary>
        private static MarkdownBlock BuildCodeViewer(string body, MarkdownStyleOptions options)
        {
            return new MarkdownBlock
            {
                DeferRenderingUntilLoaded = options.DeferCodeRendering,
                IsPlainText = true,
                IsNoWrap = true,
                FontFamily = options.CodeFontFamily,
                FontSize = Math.Max(9, options.BaseFontSize - 1),
                Foreground = options.TextBrush,
                AccentBrush = options.AccentBrush,
                Margin = new Thickness(9, 2, 9, 8),
                Markdown = body
            };
        }

        /// <summary>
        /// Copies the block's code (kept in the button's <see cref="FrameworkElement.Tag"/>). The icon
        /// shows a check mark for a moment as confirmation.
        /// </summary>
        private static void CopyCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            try
            {
                Clipboard.SetText((string)button.Tag);
            }
            catch (Exception ex)
            {
                // The clipboard is briefly locked by other apps now and then; a missed copy is not worth a dialog.
                Debug.WriteLine($"Copy code failed: {ex.Message}");
                return;
            }

            button.Content = "✓";
            var resetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            resetTimer.Tick += (s, args) =>
            {
                resetTimer.Stop();
                button.Content = CopyCodeGlyph;
            };
            resetTimer.Start();
        }

        private static Block BuildTable(string headerLine, string separatorLine, IList<string> rowLines, MarkdownStyleOptions options)
        {
            string[] headers = SplitTableRow(headerLine);
            TextAlignment[] alignments = ParseTableAlignments(separatorLine, headers.Length);

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 8) };
            for (int c = 0; c < headers.Length; c++)
            {
                table.Columns.Add(new TableColumn());
            }

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            var headerRow = new TableRow { FontWeight = FontWeights.SemiBold };
            for (int c = 0; c < headers.Length; c++)
            {
                headerRow.Cells.Add(BuildTableCell(headers[c], alignments[c], options));
            }
            group.Rows.Add(headerRow);

            foreach (string rowLine in rowLines)
            {
                string[] cells = SplitTableRow(rowLine);
                var row = new TableRow();

                for (int c = 0; c < headers.Length; c++)
                {
                    row.Cells.Add(BuildTableCell(c < cells.Length ? cells[c] : string.Empty, alignments[c], options));
                }

                group.Rows.Add(row);
            }

            return table;
        }

        /// <summary>
        /// Column alignment from the separator row: ":--" left, ":-:" center, "--:" right. A column
        /// without colons, or one the separator does not cover, stays left like the document.
        /// </summary>
        internal static TextAlignment[] ParseTableAlignments(string separatorLine, int columns)
        {
            string[] specs = SplitTableRow(separatorLine);
            var alignments = new TextAlignment[columns];
            for (int c = 0; c < columns; c++)
            {
                string spec = c < specs.Length ? specs[c] : string.Empty;
                bool left = spec.StartsWith(":", StringComparison.Ordinal);
                bool right = spec.Length > 1 && spec.EndsWith(":", StringComparison.Ordinal);

                alignments[c] = left && right ? TextAlignment.Center
                    : right ? TextAlignment.Right
                    : TextAlignment.Left;
            }

            return alignments;
        }

        private static TableCell BuildTableCell(string text, TextAlignment alignment, MarkdownStyleOptions options)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = alignment };
            AppendInlines(paragraph.Inlines, text, options);

            return new TableCell(paragraph)
            {
                Padding = new Thickness(8, 4, 8, 4),
                BorderBrush = options.CodeBorderBrush,
                BorderThickness = new Thickness(1)
            };
        }

        private static string[] SplitTableRow(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            // An escaped "\|" is a pipe inside the cell, not a column break.
            var cells = new List<string>();
            var cell = new StringBuilder();
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
                {
                    cell.Append('|');
                    i++;
                }
                else if (trimmed[i] == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                }
                else
                {
                    cell.Append(trimmed[i]);
                }
            }

            cells.Add(cell.ToString().Trim());
            return cells.ToArray();
        }

        #endregion

        #region Inline level

        /// <summary>
        /// Appends the inline runs of one paragraph: inline code, bold, italic, strikethrough and links. A marker with
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

                if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
                {
                    buffer.Append(text[i + 1]);
                    i += 2;
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

                if (c == '*' || c == '_')
                {
                    // The run length decides the span: *italic*, **bold**, ***both***. A run whose span
                    // does not close gives up one literal marker and the rest of the run is tried again,
                    // so "**a* b" is "*" + italic "a".
                    int count = Math.Min(MarkerRunLength(text, i), 3);
                    int close = FindEmphasisCloser(text, i + count, c, count);
                    if (close > i + count && !char.IsWhiteSpace(text[i + count]))
                    {
                        flush();
                        Span span = count == 1 ? new Italic() : (Span)new Bold();
                        InlineCollection inner = span.Inlines;
                        if (count == 3)
                        {
                            var italic = new Italic();
                            span.Inlines.Add(italic);
                            inner = italic.Inlines;
                        }

                        AppendInlines(inner, text.Substring(i + count, close - i - count), options);
                        target.Add(span);
                        i = close + count;
                        continue;
                    }
                }

                if (c == '<')
                {
                    Inline html = TryBuildHtmlInline(text, i, options, out int consumed);
                    if (html != null)
                    {
                        flush();
                        target.Add(html);
                        i += consumed;
                        continue;
                    }
                }

                if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
                {
                    int close = FindEmphasisCloser(text, i + 2, '~', 2);
                    if (close > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                    {
                        flush();
                        var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                        AppendInlines(strike.Inlines, text.Substring(i + 2, close - i - 2), options);
                        target.Add(strike);
                        i = close + 2;
                        continue;
                    }
                }

                // Only inline [label](url) links. Reference-style links ("[text][id]" plus an "[id]: url"
                // line) are deliberately not supported: agents almost never write them, and while a reply
                // streams the definition usually arrives after the paragraph using it is already final.
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
        /// CommonMark escapes: a backslash before ASCII punctuation makes it literal ("\*" is a star,
        /// "\#" at a line start is no heading). Before anything else the backslash is kept, so
        /// "C:\Users" reads as written.
        /// </summary>
        internal static bool IsEscapable(char c)
        {
            return @"!""#$%&'()*+,-./:;<=>?@[\]^_`{|}~".IndexOf(c) >= 0;
        }

        /// <summary>
        /// The few HTML tags markdown has no syntax for: &lt;u&gt;, &lt;sub&gt;, &lt;sup&gt;, &lt;kbd&gt; and
        /// &lt;br&gt; (also &lt;br/&gt; and &lt;br /&gt;), plus the basic formatting tags a model writes when it
        /// slips out of markdown: &lt;b&gt;/&lt;strong&gt;, &lt;i&gt;/&lt;em&gt;, &lt;s&gt;/&lt;del&gt;, &lt;ins&gt;, &lt;mark&gt;
        /// and &lt;code&gt;. Only bare tags, no attributes; an unknown or unclosed tag returns null and
        /// stays literal text, as half-streamed markup should.
        /// </summary>
        private static Inline TryBuildHtmlInline(string text, int start, MarkdownStyleOptions options, out int consumed)
        {
            consumed = 0;
            Match open = HtmlTagPattern.Match(text, start);
            if (!open.Success || open.Groups[1].Length > 0)
            {
                return null;
            }

            string name = open.Groups[2].Value.ToLowerInvariant();
            if (name == "br")
            {
                consumed = open.Length;
                return new LineBreak();
            }

            if (open.Groups[3].Length > 0 || Array.IndexOf(HtmlInlineTags, name) < 0)
            {
                return null;
            }

            int contentStart = start + open.Length;
            int close = FindClosingTag(text, contentStart, name, out int closeLength);
            if (close < 0)
            {
                return null;
            }

            string inner = text.Substring(contentStart, close - contentStart);
            consumed = close + closeLength - start;

            if (name == "kbd")
            {
                return BuildKeyCap(inner, options);
            }

            Span span;
            if (name == "code")
            {
                // Like a backtick span: the content is literal, only links inside are recognised.
                span = new Span();
                AppendCodeSpanInlines(span.Inlines, inner, options);
                return span;
            }

            switch (name)
            {
                case "u":
                case "ins":
                    span = new Underline();
                    break;
                case "b":
                case "strong":
                    span = new Bold();
                    break;
                case "i":
                case "em":
                    span = new Italic();
                    break;
                case "s":
                case "del":
                    span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    break;
                case "mark":
                    span = new Span { Background = MarkBrush };
                    break;
                default: // sub, sup
                    span = new Span
                    {
                        BaselineAlignment = name == "sub" ? BaselineAlignment.Subscript : BaselineAlignment.Superscript,
                        FontSize = Math.Round(options.BaseFontSize * 0.75, 1)
                    };
                    break;
            }

            AppendInlines(span.Inlines, inner, options);
            return span;
        }

        private static readonly Regex HtmlTagPattern = new Regex(@"\G<(/?)([A-Za-z]+)\s*(/?)>", RegexOptions.Compiled);
        private static readonly string[] HtmlInlineTags =
            { "u", "sub", "sup", "kbd", "b", "strong", "i", "em", "s", "del", "ins", "mark", "code" };

        /// <summary>&lt;mark&gt; highlight: a translucent yellow that keeps the text readable in light and dark themes.</summary>
        private static readonly Brush MarkBrush = CreateFrozenBrush(Color.FromArgb(0x70, 0xFF, 0xD5, 0x00));

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>Start of the "&lt;/name&gt;" that closes an opening tag, counting nested ones of the same name; -1 if none.</summary>
        private static int FindClosingTag(string text, int from, string name, out int length)
        {
            length = 0;
            int depth = 0;
            int j = text.IndexOf('<', from);
            while (j >= 0)
            {
                Match tag = HtmlTagPattern.Match(text, j);
                if (tag.Success && string.Equals(tag.Groups[2].Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (tag.Groups[1].Length > 0)
                    {
                        if (depth == 0)
                        {
                            length = tag.Length;
                            return j;
                        }

                        depth--;
                    }
                    else if (tag.Groups[3].Length == 0)
                    {
                        depth++;
                    }
                }

                j = text.IndexOf('<', j + 1);
            }

            return -1;
        }

        /// <summary>
        /// &lt;kbd&gt; as a key cap: a rounded box with a heavier bottom edge around the key in the code
        /// font. It is an embedded element, so a copied selection leaves the key's text out.
        /// </summary>
        private static Inline BuildKeyCap(string key, MarkdownStyleOptions options)
        {
            var cap = new Border
            {
                BorderBrush = options.CodeBorderBrush,
                Background = options.CodeBackground,
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = key,
                    FontFamily = options.CodeFontFamily,
                    FontSize = Math.Round(options.BaseFontSize * 0.85, 1)
                }
            };

            return new InlineUIContainer(cap) { BaselineAlignment = BaselineAlignment.Center };
        }

        private static int MarkerRunLength(string text, int start)
        {
            int end = start;
            while (end < text.Length && text[end] == text[start])
            {
                end++;
            }

            return end - start;
        }

        /// <summary>
        /// Finds where a span opened by <paramref name="count"/> markers closes, or -1. Markers are
        /// looked at as whole runs: a run can close the span only if it is long enough and does not
        /// follow whitespace, and its last <paramref name="count"/> characters are the closer — so
        /// "**bold *it***" closes the bold after the italic, not inside it. An italic skips runs of
        /// exactly two (a bold nested in it). Code spans are skipped; their content is literal.
        /// </summary>
        private static int FindEmphasisCloser(string text, int from, char marker, int count)
        {
            int j = from;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == '\\' && j + 1 < text.Length && IsEscapable(text[j + 1]))
                {
                    j += 2;
                    continue;
                }

                if (c == '`')
                {
                    int codeClose = text.IndexOf('`', j + 1);
                    if (codeClose > j + 1)
                    {
                        j = codeClose + 1;
                        continue;
                    }
                }

                if (c != marker)
                {
                    j++;
                    continue;
                }

                int run = MarkerRunLength(text, j);
                bool longEnough = count == 1 ? run != 2 : run >= count;
                if (longEnough && j > from && !char.IsWhiteSpace(text[j - 1]))
                {
                    return j + run - count;
                }

                j += run;
            }

            return -1;
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
                        Inline link = BuildLink(url, url, options);
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
                        Inline link = BuildLink(refMatch.Value, refMatch.Value, options);
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
        /// A link is a real <see cref="Hyperlink"/>: the document lives in a read-only
        /// <see cref="FlowDocumentScrollViewer"/>, which has no public text hit-test API, so the old
        /// Run.Tag plus mouse-position hit-testing is gone. <see cref="MarkdownBlock"/> handles
        /// <see cref="Hyperlink.Click"/> and raises its own <see cref="MarkdownBlock.LinkClicked"/> event.
        /// </summary>
        private static Inline BuildLink(string label, string url, MarkdownStyleOptions options)
        {
            var link = new Hyperlink
            {
                Foreground = options.AccentBrush,
                TextDecorations = TextDecorations.Underline,
                ToolTip = url,
                Tag = url,
                Cursor = Cursors.Hand
            };
            link.Inlines.Add(new Run(string.IsNullOrEmpty(label) ? url : label));
            return link;
        }

        #endregion
    }

    /// <summary>
    /// Read-only document viewer that renders its <see cref="Markdown"/> property.
    /// <para>
    /// A <see cref="FlowDocumentScrollViewer"/> rather than a <c>RichTextBox</c> or a read-only
    /// <c>TextBox</c>: those host a WPF <c>TextBoxView</c>, whose soft-wrap line metrics can fail an
    /// internal invariant and terminate the whole process on .NET Framework 4.8.1
    /// (dotnet/wpf#11481, #2314). A document viewer keeps the text selectable and copyable without
    /// that code path.
    /// </para>
    /// </summary>
    public class MarkdownBlock : FlowDocumentScrollViewer
    {
        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            "Markdown", typeof(string), typeof(MarkdownBlock),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

        /// <summary>
        /// When true, the document is not built until the block is actually visible. Tool cards keep
        /// their raw input/result documents behind a collapsed Expander; building those documents
        /// while the card is closed made scrolling through a large resumed transcript stall.
        /// </summary>
        public static readonly DependencyProperty DeferRenderingWhenCollapsedProperty =
            DependencyProperty.Register("DeferRenderingWhenCollapsed", typeof(bool), typeof(MarkdownBlock),
                new PropertyMetadata(false, OnVisualInputChanged));

        /// <summary>
        /// When true, the document is built after the element is loaded, not when Markdown is first
        /// bound. A DataTemplate sets Markdown and IsPlainText in an order WPF does not guarantee;
        /// building on the first change meant a notice or tool result was parsed as markdown once
        /// (measured 500-700 ms for a 2 KB tool summary) before IsPlainText turned that work into a
        /// throwaway document. Waiting for Loaded sees all the inputs at their final values.
        /// </summary>
        public static readonly DependencyProperty DeferRenderingUntilLoadedProperty =
            DependencyProperty.Register("DeferRenderingUntilLoaded", typeof(bool), typeof(MarkdownBlock),
                new PropertyMetadata(true, OnVisualInputChanged));

        /// <summary>
        /// True while <see cref="Markdown"/> is a reply still being streamed (it only grows). Each change
        /// then appends the newly finished blocks once and re-renders only the unfinished tail, instead
        /// of rebuilding the whole document. Turning it off rebuilds the document whole, once.
        /// </summary>
        public static readonly DependencyProperty IsIncrementalProperty = DependencyProperty.Register(
            "IsIncremental", typeof(bool), typeof(MarkdownBlock),
            new PropertyMetadata(false, OnVisualInputChanged));

        /// <summary>
        /// With <see cref="IsPlainText"/>: lines are not wrapped and the block scrolls sideways instead —
        /// raw tool input and output (commands, JSON, logs) read line by line.
        /// </summary>
        public static readonly DependencyProperty IsNoWrapProperty = DependencyProperty.Register(
            "IsNoWrap", typeof(bool), typeof(MarkdownBlock),
            new PropertyMetadata(false, OnVisualInputChanged));

        public bool IsNoWrap
        {
            get { return (bool)GetValue(IsNoWrapProperty); }
            set { SetValue(IsNoWrapProperty, value); }
        }

        /// <summary>When true the text is shown as-is with only URLs linked (notices, errors), not parsed as markdown.</summary>
        public static readonly DependencyProperty IsPlainTextProperty = DependencyProperty.Register(
            "IsPlainText", typeof(bool), typeof(MarkdownBlock),
            new PropertyMetadata(false, OnVisualInputChanged));

        public static readonly DependencyProperty CodeBackgroundProperty = DependencyProperty.Register(
            "CodeBackground", typeof(Brush), typeof(MarkdownBlock),
            new PropertyMetadata(Brushes.Transparent, OnVisualInputChanged));

        public static readonly DependencyProperty CodeBorderBrushProperty = DependencyProperty.Register(
            "CodeBorderBrush", typeof(Brush), typeof(MarkdownBlock),
            new PropertyMetadata(Brushes.Gray, OnVisualInputChanged));

        public static readonly DependencyProperty CodeCopyButtonStyleProperty = DependencyProperty.Register(
            "CodeCopyButtonStyle", typeof(Style), typeof(MarkdownBlock),
            new PropertyMetadata(null, OnVisualInputChanged));

        public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
            "AccentBrush", typeof(Brush), typeof(MarkdownBlock),
            new PropertyMetadata(Brushes.SteelBlue, OnVisualInputChanged));

        public static readonly DependencyProperty CodeFontFamilyProperty = DependencyProperty.Register(
            "CodeFontFamily", typeof(FontFamily), typeof(MarkdownBlock),
            new PropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), OnVisualInputChanged));

        public MarkdownBlock()
        {
            IsSelectionEnabled = true;
            IsToolBarVisible = false;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            MinHeight = 0;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

            // BuildLink emits Hyperlinks; clicking one must stay inside the chat instead of letting
            // WPF navigate the host process. handledEventsToo because the Hyperlink may mark it handled.
            AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(OnHyperlinkClick), true);

            // A deferred document is built when the Expander that hides it opens (or when a
            // virtualized row scrolls into view), not when the Markdown property is first bound.
            IsVisibleChanged += OnIsVisibleChanged;
            Loaded += OnLoadedForRendering;
            Unloaded += delegate { _isLoaded = false; };

            // The document viewer has no built-in context menu; keep the select-and-copy affordance
            // the old TextBox/RichTextBox supplied.
            var copy = new MenuItem { Header = L("Copy") };
            copy.Click += CopySelection_Click;
            ContextMenu = new ContextMenu();
            ContextMenu.Items.Add(copy);

            // The chat's menu look (ChatStyles.xaml), found once the block sits in the chat's tree; where
            // that style is not available (the GitHub issue view) the stock menu stays.
            ContextMenuOpening += (s, e) =>
            {
                if (ContextMenu != null && ContextMenu.Style == null &&
                    TryFindResource("ModernContextMenuStyle") is Style menuStyle)
                {
                    ContextMenu.Style = menuStyle;
                }
            };
        }

        /// <summary>Raised when a rendered link is clicked, carrying its URL (or bare file reference).</summary>
        public event EventHandler<string> LinkClicked;

        private void OnHyperlinkClick(object sender, RoutedEventArgs e)
        {
            Hyperlink link = e.Source as Hyperlink ?? e.OriginalSource as Hyperlink;
            // The click bubbles on from a nested block's document to the outer one; each block
            // answers only for links of its own document, so a link opens once.
            if (link == null || !ReferenceEquals(DocumentOf(link), Document))
            {
                return;
            }

            if (link.Tag is string url && !string.IsNullOrEmpty(url))
            {
                // A block nested in another one (a code block's own viewer) has no subscriber of its
                // own: hand the click to the reply's block, which the chat wires up.
                MarkdownBlock owner = LinkClicked == null ? FindOuterBlock() : this;
                if (owner == null || owner.LinkClicked == null)
                {
                    return;
                }

                e.Handled = true;
                owner.LinkClicked.Invoke(owner, url);
            }
        }

        private static FlowDocument DocumentOf(DependencyObject element)
        {
            DependencyObject current = element;
            while (current != null && !(current is FlowDocument))
            {
                current = LogicalTreeHelper.GetParent(current);
            }

            return current as FlowDocument;
        }

        /// <summary>The closest <see cref="MarkdownBlock"/> this one is embedded in, or null.</summary>
        private MarkdownBlock FindOuterBlock()
        {
            DependencyObject current = this;
            while (current != null)
            {
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);

                if (current is MarkdownBlock outer)
                {
                    return outer;
                }
            }

            return null;
        }

        private void CopySelection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TextSelection selection = Selection;
                if (selection != null && !selection.IsEmpty)
                {
                    Clipboard.SetText(selection.Text);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy selection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Vertical offset of the document's scroll viewer. <see cref="FlowDocumentScrollViewer"/> does
        /// not expose the scroll API itself; callers that preserve the reader's place (the GitHub issue
        /// detail) keep using the same members they used on the old RichTextBox.
        /// </summary>
        public double VerticalOffset
        {
            get
            {
                ScrollViewer viewer = FindScrollViewer();
                return viewer?.VerticalOffset ?? 0.0;
            }
        }

        public void ScrollToVerticalOffset(double offset)
        {
            // A document assignment is laid out on the next pass; scrolling now would clamp the
            // offset against the previous document's extent. Apply it after the layout pass.
            // The VS threading helpers cannot express a dispatcher priority, and the caller is already on
            // the UI thread, so the WPF dispatcher is the right tool here.
#pragma warning disable VSTHRD001, VSTHRD110
            _ = Dispatcher.BeginInvoke(
                new Action(() => FindScrollViewer()?.ScrollToVerticalOffset(offset)),
                System.Windows.Threading.DispatcherPriority.Loaded);
#pragma warning restore VSTHRD001, VSTHRD110
        }

        public void ScrollToHome()
        {
            FindScrollViewer()?.ScrollToTop();
        }

        private ScrollViewer FindScrollViewer()
        {
            return FindVisualChild<ScrollViewer>(this);
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

        public string Markdown
        {
            get { return (string)GetValue(MarkdownProperty); }
            set { SetValue(MarkdownProperty, value); }
        }

        public bool DeferRenderingWhenCollapsed
        {
            get { return (bool)GetValue(DeferRenderingWhenCollapsedProperty); }
            set { SetValue(DeferRenderingWhenCollapsedProperty, value); }
        }

        public bool DeferRenderingUntilLoaded
        {
            get { return (bool)GetValue(DeferRenderingUntilLoadedProperty); }
            set { SetValue(DeferRenderingUntilLoadedProperty, value); }
        }

        private bool _documentPending;
        private bool _isLoaded;
        private bool _settingDocument;

        private void OnLoadedForRendering(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            // A nested block (a code block's own viewer) selects with the reply's colours, not the
            // system highlight its own default style would give it.
            if (ReadLocalValue(SelectionBrushProperty) == DependencyProperty.UnsetValue && FindOuterBlock() is MarkdownBlock outer)
            {
                SetBinding(SelectionBrushProperty, new Binding(nameof(SelectionBrush)) { Source = outer });
                SetBinding(SelectionOpacityProperty, new Binding(nameof(SelectionOpacity)) { Source = outer });
            }

            if (_documentPending)
            {
                Rebuild();
            }
            else if (IsNoWrap && IsPlainText && _widthFont != null &&
                     _widthFont.Item3 != VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                // A code viewer builds before it is in the window, where the DPI is still the system one;
                // on a monitor with another scale its page came out too narrow and long lines wrapped.
                Rebuild();
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Only the real Loaded event guarantees that the template's local values (IsPlainText
            // above all) have been applied. Becoming visible earlier must not build the document:
            // that was what parsed notices and tool output as markdown before IsPlainText arrived.
            if ((bool)e.NewValue && _isLoaded && _documentPending)
            {
                Rebuild();
            }
        }

        public bool IsPlainText
        {
            get { return (bool)GetValue(IsPlainTextProperty); }
            set { SetValue(IsPlainTextProperty, value); }
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

        public Style CodeCopyButtonStyle
        {
            get { return (Style)GetValue(CodeCopyButtonStyleProperty); }
            set { SetValue(CodeCopyButtonStyleProperty, value); }
        }

        /// <summary>
        /// Headings scale off the base size, so a font-size change has to rebuild the document — the
        /// scaled sizes are baked into it and would otherwise stay at the previous scale.
        /// </summary>
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.Property == DocumentProperty && e.NewValue is FlowDocument document)
            {
                // A FlowDocumentScrollViewer does not pass its font down to the document, which then falls
                // back to WPF's serif document default (Georgia). Bind it so the chat font reaches the text.
                if (document.ReadLocalValue(FlowDocument.FontFamilyProperty) == DependencyProperty.UnsetValue)
                {
                    document.SetBinding(FlowDocument.FontFamilyProperty,
                        new Binding(nameof(FontFamily)) { Source = this });
                }

                // The same document default justifies every paragraph; chat text reads left-aligned
                // (Left is the leading edge, so right-to-left languages still align to the right).
                if (document.ReadLocalValue(FlowDocument.TextAlignmentProperty) == DependencyProperty.UnsetValue)
                {
                    document.TextAlignment = TextAlignment.Left;
                }
            }

            if (e.Property == DocumentProperty && !_settingDocument)
            {
                // A caller can hand the viewer a document it built itself (the GitHub issue detail
                // view does). That document wins: do not replace it with the empty Markdown on Loaded.
                _documentPending = false;
                return;
            }

            // A no-wrap page is measured in the block's font, so a new font needs a new page width.
            if (e.Property == FontSizeProperty || e.Property == ForegroundProperty ||
                (IsNoWrap && e.Property == FontFamilyProperty))
            {
                Rebuild();
            }
        }

        public bool IsIncremental
        {
            get { return (bool)GetValue(IsIncrementalProperty); }
            set { SetValue(IsIncrementalProperty, value); }
        }

        private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownBlock)d).Rebuild();
        }

        private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var block = (MarkdownBlock)d;
            if (block.IsIncremental && !block.IsPlainText && block._incrementalOptions != null)
            {
                block.UpdateIncremental();
            }
            else
            {
                block.Rebuild();
            }
        }

        // ---- incremental (streaming) state ----

        /// <summary>The options the incremental document was started with; null when not incremental.</summary>
        private MarkdownStyleOptions _incrementalOptions;

        /// <summary>The text the document currently shows; its first <see cref="_frozenLength"/> chars are final.</summary>
        private string _incrementalText = string.Empty;

        /// <summary>Length of the leading text whose blocks are in the document for good.</summary>
        private int _frozenLength;

        /// <summary>The blocks of the unfinished tail, replaced on every update.</summary>
        private readonly List<Block> _tailBlocks = new List<Block>();

        private MarkdownStyleOptions CreateOptions()
        {
            return new MarkdownStyleOptions
            {
                BaseFontSize = FontSize,
                CodeFontFamily = CodeFontFamily,
                CodeBackground = CodeBackground,
                CodeBorderBrush = CodeBorderBrush,
                CodeCopyButtonStyle = CodeCopyButtonStyle,
                AccentBrush = AccentBrush,
                MutedBrush = Foreground,
                TextBrush = Foreground
            };
        }

        /// <summary>
        /// Starts the document over in incremental mode. Any later change of style input comes back
        /// here through <see cref="Rebuild"/>, since the finished blocks carry the old style baked in.
        /// </summary>
        private void StartIncremental(MarkdownStyleOptions options)
        {
            _incrementalOptions = options;
            _incrementalText = string.Empty;
            _frozenLength = 0;
            _tailBlocks.Clear();
            _settingDocument = true;
            try
            {
                Document = MarkdownFlowRenderer.CreateDocument(options);
            }
            finally
            {
                _settingDocument = false;
            }

            UpdateIncremental();
        }

        /// <summary>
        /// Brings the document up to <see cref="Markdown"/>: blocks finished since the last update are
        /// appended for good, and the unfinished tail is rendered again in place of its previous blocks.
        /// The cost therefore follows the size of the tail, not of the whole reply.
        /// </summary>
        private void UpdateIncremental()
        {
            string text = Markdown ?? string.Empty;

            // Not an append of what is showing (replaced or cleared text): start over.
            if (text.Length < _frozenLength ||
                string.CompareOrdinal(text, 0, _incrementalText, 0, _frozenLength) != 0)
            {
                StartIncremental(_incrementalOptions);
                return;
            }

            // The text after the old boundary, rendered again: the blocks that became final, then the new tail.
            var fresh = new List<Block>();
            int boundary = MarkdownFlowRenderer.FindStableBoundary(text, _frozenLength);
            if (boundary > _frozenLength)
            {
                fresh.AddRange(MarkdownFlowRenderer.BuildBlockList(
                    text.Substring(_frozenLength, boundary - _frozenLength), _incrementalOptions));
            }

            int frozenCount = fresh.Count;
            fresh.AddRange(MarkdownFlowRenderer.BuildBlockList(text.Substring(boundary), _incrementalOptions.WithDeferredCode()));
            _frozenLength = boundary;

            // Both lists start at the old boundary, so the blocks pair up by position. A code block keeps
            // its place and takes the new code (an open fence grows on every update); every other block
            // is swapped where it stands.
            BlockCollection blocks = Document.Blocks;
            var previousTail = new List<Block>(_tailBlocks);
            _tailBlocks.Clear();
            for (int i = 0; i < fresh.Count; i++)
            {
                Block block = fresh[i];
                if (i < previousTail.Count)
                {
                    if (MarkdownFlowRenderer.TryUpdateCodeBlock(previousTail[i], block))
                    {
                        block = previousTail[i];
                    }
                    else
                    {
                        blocks.InsertBefore(previousTail[i], block);
                        blocks.Remove(previousTail[i]);
                    }
                }
                else
                {
                    blocks.Add(block);
                }

                if (i >= frozenCount)
                {
                    _tailBlocks.Add(block);
                }
            }

            for (int i = fresh.Count; i < previousTail.Count; i++)
            {
                blocks.Remove(previousTail[i]);
            }

            _incrementalText = text;
        }

        private void Rebuild()
        {
            if ((DeferRenderingUntilLoaded && !_isLoaded) ||
                (DeferRenderingWhenCollapsed && !IsVisible))
            {
                _documentPending = true;
                return;
            }

            _documentPending = false;
            MarkdownStyleOptions options = CreateOptions();

            if (IsIncremental && !IsPlainText)
            {
                StartIncremental(options);
                return;
            }

            _incrementalOptions = null;
            _tailBlocks.Clear();

            FlowDocument document = IsPlainText
                ? MarkdownFlowRenderer.BuildPlain(Markdown, options)
                : MarkdownFlowRenderer.Build(Markdown, options);

            bool noWrap = IsNoWrap && IsPlainText;
            if (noWrap)
            {
                // A FlowDocument always wraps at its page width, so "no wrap" means a page as wide as
                // the longest line; the viewer then scrolls it sideways, as the TextBox did before.
                document.PageWidth = MeasurePageWidth(Markdown,
                    new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                // A little room above the text and between it and the horizontal scrollbar, which
                // otherwise sits right against the last line. Vertical only: the page width stays.
                document.PagePadding = new Thickness(0, NoWrapVerticalPadding, 0, NoWrapVerticalPadding);
            }

            HorizontalScrollBarVisibility = noWrap ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

            _settingDocument = true;
            try
            {
                Document = document;
            }
            finally
            {
                _settingDocument = false;
            }
        }

        /// <summary>Space above and below the text of a no-wrap block (code blocks, raw tool input/output).</summary>
        private const double NoWrapVerticalPadding = 4;

        // ---- no-wrap page width, kept across updates ----

        /// <summary>The text <see cref="_widthOfCompleteLines"/> was measured for.</summary>
        private string _widthText;

        /// <summary>End of its complete lines (just past the last newline): only the line after it can still grow.</summary>
        private int _widthCompleteEnd;

        private double _widthOfCompleteLines;
        private Tuple<Typeface, double, double> _widthFont;

        /// <summary>
        /// <see cref="MeasureWidestLine"/> of <paramref name="text"/>, measuring only what is new when the
        /// text grew by an append in the same font: a streaming code block gets its code again on every
        /// update, and measuring every line twice each time made the cost grow with the block.
        /// </summary>
        private double MeasurePageWidth(string text, Typeface typeface, double fontSize, double pixelsPerDip)
        {
            text = text ?? string.Empty;
            var font = Tuple.Create(typeface, fontSize, pixelsPerDip);
            bool appended = _widthText != null && font.Equals(_widthFont) &&
                text.Length >= _widthCompleteEnd &&
                string.CompareOrdinal(text, 0, _widthText, 0, _widthCompleteEnd) == 0;
            if (!appended)
            {
                _widthCompleteEnd = 0;
                _widthOfCompleteLines = 0;
            }

            // The widest of the parts is the widest of the whole: MeasureWidestLine only grows with its widest line.
            int completeEnd = text.LastIndexOf('\n') + 1;
            if (completeEnd > _widthCompleteEnd)
            {
                _widthOfCompleteLines = Math.Max(_widthOfCompleteLines, MeasureWidestLine(
                    text.Substring(_widthCompleteEnd, completeEnd - _widthCompleteEnd), typeface, fontSize, pixelsPerDip));
                _widthCompleteEnd = completeEnd;
            }

            _widthText = text;
            _widthFont = font;
            return Math.Max(_widthOfCompleteLines,
                MeasureWidestLine(text.Substring(completeEnd), typeface, fontSize, pixelsPerDip));
        }

        /// <summary>
        /// Width of the longest line of <paramref name="text"/>, plus one em of slack for the tab-stop
        /// differences between <see cref="FormattedText"/> and the document's own layout. Each line is
        /// measured in both formatting modes and the wider one wins: Visual Studio lays text out in
        /// Display mode, which rounds every glyph advance to whole pixels, so on a long line it drifts
        /// far past one em from the Ideal measure (+134 px on a 330-character line in Cascadia Mono
        /// 13 px) — the page came out too narrow and the line's end wrapped after all. Taking the wider
        /// of the two does not depend on the mode being inherited yet when the document is built.
        /// </summary>
        internal static double MeasureWidestLine(string text, Typeface typeface, double fontSize, double pixelsPerDip)
        {
            double widest = 0;
            foreach (string line in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;

                foreach (TextFormattingMode mode in new[] { TextFormattingMode.Ideal, TextFormattingMode.Display })
                {
                    var formatted = new FormattedText(line, System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, null, mode, pixelsPerDip);
                    widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
                }
            }

            return Math.Ceiling(widest + fontSize);
        }
    }
}
