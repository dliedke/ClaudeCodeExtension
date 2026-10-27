/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the native chat's text rendering against reintroducing a WPF TextBoxView-backed control, whose
 *          soft-wrap metrics can terminate Visual Studio on .NET Framework (dotnet/wpf#11481, #2314).
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatTextViewTests
    {
        [TestMethod]
        public void CodeBlocksAreSelectableDocumentTextNotATextBox()
        {
            RunOnSta(() =>
            {
                List<Block> blocks = MarkdownFlowRenderer.BuildBlockList("```cs\nvar a = 1;\n  b();\n```", null);
                Section section = blocks.OfType<Section>().Single();
                BlockUIContainer[] containers = section.Blocks.OfType<BlockUIContainer>().ToArray();

                // The header (language label + copy button); a TextBox anywhere in the block would
                // reintroduce the WPF TextBoxView crash path.
                var panel = (Panel)containers[0].Child;
                Assert.IsFalse(panel.Children.OfType<TextBox>().Any(),
                    "A TextBox here would reintroduce the WPF TextBoxView crash path.");

                // The code is its own plain-text document viewer (a MarkdownBlock, not a TextBox), so it
                // selects and copies within the block, with its lines and indentation intact.
                var code = (MarkdownBlock)containers[1].Child;
                Assert.IsTrue(code.IsPlainText);
                Assert.AreEqual("var a = 1;\n  b();", code.Markdown);

                code.DeferRenderingUntilLoaded = false;
                Paragraph paragraph = code.Document.Blocks.OfType<Paragraph>().Single();
                Assert.AreEqual("var a = 1;" + Environment.NewLine + "  b();",
                    new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
            });
        }

        /// <summary>
        /// Long code lines stay on one line and the block scrolls sideways: the code viewer uses the
        /// same no-wrap mode as raw tool output (see <see cref="NoWrapPlainTextGetsAPageAsWideAsItsLongestLine"/>),
        /// in the code font, with vertical scrolling left disabled so the wheel stays with the transcript.
        /// </summary>
        [TestMethod]
        public void CodeBlockUsesTheNoWrapViewerOfToolOutput()
        {
            RunOnSta(() =>
            {
                var options = new MarkdownStyleOptions { BaseFontSize = 14, CodeFontFamily = new FontFamily("Consolas") };
                var code = (MarkdownBlock)MarkdownFlowRenderer.BuildBlockList("```\n" + new string('a', 300) + "\n```", options)
                    .OfType<Section>().Single().Blocks.OfType<BlockUIContainer>().Last().Child;

                Assert.IsTrue(code.IsNoWrap);
                Assert.AreEqual("Consolas", code.FontFamily.Source);
                Assert.AreEqual(13.0, code.FontSize);
                Assert.AreEqual(ScrollBarVisibility.Disabled, code.VerticalScrollBarVisibility);

                code.DeferRenderingUntilLoaded = false;
                Assert.AreEqual(ScrollBarVisibility.Auto, code.HorizontalScrollBarVisibility);
                Assert.IsFalse(double.IsNaN(code.Document.PageWidth), "the page is as wide as the longest line");
            });
        }

        /// <summary>
        /// Visual Studio lays text out in Display mode, which rounds every glyph advance to whole pixels;
        /// on a long line that drifts well past the one-em slack from the Ideal measure, and the end of
        /// the line wrapped inside the no-wrap page. The page must fit the line in both modes.
        /// </summary>
        [TestMethod]
        public void NoWrapPageFitsALongLineInDisplayAndIdealMode()
        {
            RunOnSta(() =>
            {
                const string line = "        var lines = filtered.Select(c => string.Join(\";\", c.Id, c.FirstName, c.LastName, c.Email, " +
                    "c.Phone, c.Region, c.Country, c.City, c.Street, c.PostalCode, c.TotalSpent.ToString(\"C\", culture), " +
                    "c.LastOrderDate.ToString(\"yyyy-MM-dd HH:mm:ss\"), c.LoyaltyTier, c.PreferredLanguage, c.MarketingConsent, " +
                    "c.Notes?.Replace(\";\", \",\").Replace(\"\\n\", \" \")));";

                foreach (string font in new[] { "Cascadia Mono", "Consolas" })
                {
                    foreach (double size in new[] { 12.0, 13.0, 14.0 })
                    {
                        var typeface = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                        double page = MarkdownBlock.MeasureWidestLine(line, typeface, size, 1.0);

                        foreach (TextFormattingMode mode in new[] { TextFormattingMode.Ideal, TextFormattingMode.Display })
                        {
                            double width = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                typeface, size, Brushes.Black, null, mode, 1.0).WidthIncludingTrailingWhitespace;
                            Assert.IsTrue(page >= width, $"{font} {size}px {mode}: line {width:0.#}, page {page}.");
                        }
                    }
                }
            });
        }

        /// <summary>
        /// WPF draws a list marker right-aligned in the list's left padding and the document viewer clips
        /// what spills out to the left: with the old fixed indent "10." lost its 1 at a 16 px chat font.
        /// The indent must hold the widest marker plus the marker gap, in a proportional and a
        /// monospaced font (whose digits are wider).
        /// </summary>
        [TestMethod]
        public void OrderedListIndentFitsTheWidestMarker()
        {
            RunOnSta(() =>
            {
                foreach (string font in new[] { "Segoe UI", "Consolas" })
                {
                    foreach (double size in new[] { 12.0, 16.0, 24.0 })
                    {
                        foreach (int largest in new[] { 9, 10, 99, 100, 999 })
                        {
                            double indent = MarkdownFlowRenderer.OrderedListIndent(largest, size, out double markerOffset);
                            var marker = new FormattedText(largest + ".", CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, new Typeface(font), size, Brushes.Black, 1.0);

                            Assert.IsTrue(markerOffset > 0, "The marker needs a gap before the item text.");
                            Assert.IsTrue(indent >= marker.WidthIncludingTrailingWhitespace + markerOffset,
                                $"\"{largest}.\" in {font} {size}px needs {marker.WidthIncludingTrailingWhitespace + markerOffset:0.#}, got {indent}.");
                        }
                    }
                }
            });
        }

        [TestMethod]
        public void LongOrderedListIsIndentedForItsLastNumber()
        {
            RunOnSta(() =>
            {
                string markdown = string.Join("\n", Enumerable.Range(1, 100).Select(n => n + ". item"));
                var options = new MarkdownStyleOptions { BaseFontSize = 16 };
                List list = MarkdownFlowRenderer.BuildBlockList(markdown, options).OfType<List>().Single();

                Assert.AreEqual(100, list.ListItems.Count);
                Assert.AreEqual(MarkdownFlowRenderer.OrderedListIndent(100, 16, out double markerOffset), list.Padding.Left);
                Assert.AreEqual(markerOffset, list.MarkerOffset);
                Assert.IsTrue(list.Padding.Left > MarkdownFlowRenderer.OrderedListIndent(9, 16, out _),
                    "A three-digit list needs more room than a one-digit one.");
            });
        }

        [TestMethod]
        public void OrderedListKeepsTheNumberItStartsWith()
        {
            RunOnSta(() =>
            {
                List list = MarkdownFlowRenderer.BuildBlockList("Intro\n\n4. four\n5. five", null).OfType<List>().Single();
                Assert.AreEqual(4, list.StartIndex);
                Assert.AreEqual(2, list.ListItems.Count);

                // WPF has no marker 0: a list written from 0 starts at 1 instead of failing to render.
                List fromZero = MarkdownFlowRenderer.BuildBlockList("0. zero\n1. one", null).OfType<List>().Single();
                Assert.AreEqual(1, fromZero.StartIndex);
                Assert.AreEqual(2, fromZero.ListItems.Count);
            });
        }

        [TestMethod]
        public void BulletListIndentIsUnchanged()
        {
            RunOnSta(() =>
            {
                List list = MarkdownFlowRenderer.BuildBlockList("- a\n- b", null).OfType<List>().Single();
                Assert.AreEqual(TextMarkerStyle.Disc, list.MarkerStyle);
                Assert.AreEqual(16.0, list.Padding.Left);
            });
        }

        [TestMethod]
        public void PlainTextLinksAreHyperlinks()
        {
            RunOnSta(() =>
            {
                FlowDocument document = MarkdownFlowRenderer.BuildPlain("see https://example.com now", null);
                Paragraph paragraph = document.Blocks.OfType<Paragraph>().Single();
                Hyperlink link = paragraph.Inlines.OfType<Hyperlink>().Single();

                Assert.AreEqual("https://example.com", link.Tag);
            });
        }

        /// <summary>
        /// A file reference or URL inside a code span becomes a link. Casting that link to Run threw,
        /// and the catch-all fallback then showed the whole reply as one paragraph of raw markdown.
        /// </summary>
        [TestMethod]
        public void LinksInsideCodeSpansKeepTheReplyRendered()
        {
            RunOnSta(() =>
            {
                List<Block> blocks = MarkdownFlowRenderer.BuildBlockList(
                    "**Note** see `Foo.cpp:28` and `https://example.com`", null);
                Paragraph paragraph = blocks.OfType<Paragraph>().Single();

                Assert.IsTrue(paragraph.Inlines.OfType<Bold>().Any(),
                    "The bold text should be rendered, not left as raw markdown by the fallback.");
                CollectionAssert.AreEqual(
                    new[] { "Foo.cpp:28", "https://example.com" },
                    paragraph.Inlines.OfType<Hyperlink>().Select(link => (string)link.Tag).ToArray());
            });
        }

        [TestMethod]
        public void MarkdownBlockRendersPlainTextInADocumentViewer()
        {
            RunOnSta(() =>
            {
                var block = new MarkdownBlock
                {
                    DeferRenderingUntilLoaded = false,
                    IsPlainText = true,
                    Markdown = "hello\nworld"
                };

                Assert.IsNotNull(block.Document);
                string text = new TextRange(block.Document.ContentStart, block.Document.ContentEnd).Text;
                StringAssert.Contains(text, "hello");
                StringAssert.Contains(text, "world");

                // The old RichTextBox scroll members are still used by the GitHub issue detail view.
                Assert.AreEqual(0.0, block.VerticalOffset);
                block.ScrollToHome();
                block.ScrollToVerticalOffset(0.0);
            });
        }

        /// <summary>
        /// Raw tool input/output used to be an unwrapped TextBox that scrolled sideways. A FlowDocument
        /// always wraps at its page width, so no-wrap is a page as wide as the longest line.
        /// </summary>
        [TestMethod]
        public void NoWrapPlainTextGetsAPageAsWideAsItsLongestLine()
        {
            RunOnSta(() =>
            {
                string longLine = "Get-ChildItem -Path C:\\Some\\Very\\Long\\Folder -Recurse -Filter *.cs | Select-Object FullName";
                var block = new MarkdownBlock
                {
                    DeferRenderingUntilLoaded = false,
                    IsPlainText = true,
                    IsNoWrap = true,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Markdown = "short\n" + longLine
                };

                var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double longWidth = new FormattedText(longLine, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 12, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

                Assert.IsTrue(block.Document.PageWidth >= longWidth, "The page must fit the longest line unwrapped.");
                Assert.AreEqual(ScrollBarVisibility.Auto, block.HorizontalScrollBarVisibility);

                // Turning it off goes back to wrapping at the viewer's width.
                block.IsNoWrap = false;
                Assert.IsTrue(double.IsNaN(block.Document.PageWidth));
                Assert.AreEqual(ScrollBarVisibility.Disabled, block.HorizontalScrollBarVisibility);
            });
        }

        [TestMethod]
        public void ToolTextStyleDoesNotWrap()
        {
            string xaml = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml");
            int style = xaml.IndexOf("x:Key=\"ChatToolTextStyle\"", StringComparison.Ordinal);
            int end = xaml.IndexOf("</Style>", style, StringComparison.Ordinal);

            StringAssert.Contains(xaml.Substring(style, end - style), "<Setter Property=\"IsNoWrap\" Value=\"True\"/>");
        }

        /// <summary>
        /// After a Ctrl+Scroll zoom-in the virtualized panel kept arranging rows in slots as wide as the
        /// old viewport, so right-aligned user bubbles and stretched tool cards ran off screen. Rows are
        /// pinned to the viewport width so the stale slot no longer matters.
        /// </summary>
        [TestMethod]
        public void TranscriptRowsArePinnedToTheViewportWidth()
        {
            string xaml = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml");
            int list = xaml.IndexOf("x:Name=\"MessagesList\"", StringComparison.Ordinal);
            int style = xaml.IndexOf("<ItemsControl.ItemContainerStyle>", list, StringComparison.Ordinal);
            int end = xaml.IndexOf("</ItemsControl.ItemContainerStyle>", style, StringComparison.Ordinal);

            Assert.IsTrue(style > list, "MessagesList needs an ItemContainerStyle.");
            string body = xaml.Substring(style, end - style);
            StringAssert.Contains(body, "Property=\"Width\"");
            StringAssert.Contains(body, "Binding ViewportWidth, RelativeSource={RelativeSource AncestorType=ScrollViewer}");
            StringAssert.Contains(body, "<Setter Property=\"HorizontalAlignment\" Value=\"Left\"/>");
        }

        [TestMethod]
        public void MarkdownBlockWaitsForLoadBeforeBuildingItsDocument()
        {
            RunOnSta(() =>
            {
                var block = new MarkdownBlock { Markdown = "hello" };

                Assert.IsNull(block.Document,
                    "A block that has not been loaded yet must not build its document; a DataTemplate " +
                    "may still be about to set IsPlainText, and parsing it first wastes 500-700 ms.");

                block.DeferRenderingUntilLoaded = false;
                Assert.IsNotNull(block.Document);
            });
        }

        [TestMethod]
        public void ToolAndThinkingCardsDeferTheirCollapsedBodies()
        {
            string xaml = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml");
            const string marker = "DeferRenderingWhenCollapsed=\"True\"";

            int count = xaml.Split(new[] { marker }, StringSplitOptions.None).Length - 1;
            Assert.AreEqual(4, count,
                "Both tool-input blocks, the tool-result block, and the thinking block must defer rendering " +
                "while their Expander body is collapsed.");
        }

        /// <summary>
        /// The virtualized list grows its extent long before it realizes every row, and a layout
        /// correction can even nudge the offset. Neither is the reader scrolling, so following must
        /// survive both — an offset-based test is what left the view stuck halfway through an answer.
        /// </summary>
        [TestMethod]
        public void LayoutGrowthAloneKeepsFollowing()
        {
            Assert.IsTrue(ChatTranscriptView.ResolveFollowingAfterScroll(
                following: true, readerTookOver: false, atBottom: false));
        }

        [TestMethod]
        public void ViewportResizeKeepsTheReadersChoice()
        {
            Assert.IsTrue(ChatTranscriptView.ResolveFollowingAfterScroll(
                following: true, readerTookOver: false, atBottom: false));
            Assert.IsFalse(ChatTranscriptView.ResolveFollowingAfterScroll(
                following: false, readerTookOver: false, atBottom: false));
        }

        [TestMethod]
        public void ScrollingAwayStopsFollowingAndScrollingBackReArmsIt()
        {
            Assert.IsFalse(ChatTranscriptView.ResolveFollowingAfterScroll(
                following: true, readerTookOver: true, atBottom: false));
            Assert.IsTrue(ChatTranscriptView.ResolveFollowingAfterScroll(
                following: false, readerTookOver: true, atBottom: true));
        }

        [TestMethod]
        public void ContentGrowthDoesNotReArmAReaderWhoScrolledAway()
        {
            Assert.IsFalse(ChatTranscriptView.ResolveFollowingAfterScroll(
                following: false, readerTookOver: true, atBottom: false));
        }

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new AssertFailedException(failure.Message, failure);
            }
        }
    }
}
