/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the markdown formatting the chat renders: strikethrough and the other inline runs.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class MarkdownFormattingTests
    {
        [TestMethod]
        public void DoubleTildeIsStrikethrough()
        {
            RunOnSta(() =>
            {
                Paragraph paragraph = Render("before ~~gone~~ after");
                Span strike = paragraph.Inlines.OfType<Span>().Single();

                Assert.AreEqual(TextDecorations.Strikethrough, strike.TextDecorations);
                Assert.AreEqual("gone", TextOf(strike.Inlines));
                Assert.AreEqual("before gone after", TextOf(paragraph.Inlines));
            });
        }

        [TestMethod]
        public void StrikethroughNestsOtherFormatting()
        {
            RunOnSta(() =>
            {
                Span strike = Render("~~old **bold** text~~").Inlines.OfType<Span>().Single();

                Assert.IsInstanceOfType(strike.Inlines.ElementAt(1), typeof(Bold));
                Assert.AreEqual("old bold text", TextOf(strike.Inlines));
            });
        }

        [TestMethod]
        public void UnclosedOrSingleTildeStaysLiteral()
        {
            RunOnSta(() =>
            {
                Assert.AreEqual("~~half typed", TextOf(Render("~~half typed").Inlines));
                Assert.AreEqual("about ~5 ms or ~ 6", TextOf(Render("about ~5 ms or ~ 6").Inlines));
                Assert.IsFalse(Render("~~half typed").Inlines.OfType<Span>().Any());
            });
        }

        [TestMethod]
        public void TildesInsideCodeSpanAreNotStrikethrough()
        {
            RunOnSta(() =>
            {
                Paragraph paragraph = Render("`~~x~~`");

                Assert.IsFalse(paragraph.Inlines.OfType<Span>().Any(s => s.TextDecorations == TextDecorations.Strikethrough));
                Assert.AreEqual("~~x~~", TextOf(paragraph.Inlines));
            });
        }

        [TestMethod]
        public void TripleAsteriskIsBoldItalic()
        {
            RunOnSta(() =>
            {
                Bold bold = Render("a ***both*** b").Inlines.OfType<Bold>().Single();
                Italic italic = bold.Inlines.OfType<Italic>().Single();

                Assert.AreEqual("both", TextOf(italic.Inlines));
                Assert.AreEqual("a both b", TextOf(Render("a ***both*** b").Inlines));
            });
        }

        [TestMethod]
        public void MixedFormattingLineKeepsEverySpanSeparate()
        {
            RunOnSta(() =>
            {
                Paragraph paragraph = Render(
                    "**bold**, *italic*, ***both***, ~~struck~~ and `code`. Combined: **bold with `code` inside**");
                List<Inline> top = paragraph.Inlines.ToList();

                Assert.AreEqual(
                    "bold, italic, both, struck and code. Combined: bold with code inside",
                    TextOf(top));
                Assert.AreEqual(3, top.OfType<Bold>().Count(), "bold, both, and the combined bold");
                Assert.AreEqual(1, top.OfType<Italic>().Count(), "only *italic* is italic at the top level");
                Assert.AreEqual("bold with code inside", TextOf(top.OfType<Bold>().Last().Inlines));
                Assert.IsTrue(top.OfType<Run>().Any(r => r.Text == ", "), "the separators stay plain text");
            });
        }

        [TestMethod]
        public void BoldClosesAfterANestedItalicThatEndsWithIt()
        {
            RunOnSta(() =>
            {
                Bold bold = Render("**bold *it***").Inlines.OfType<Bold>().Single();

                Assert.AreEqual("it", TextOf(bold.Inlines.OfType<Italic>().Single().Inlines));
                Assert.AreEqual("bold it", TextOf(bold.Inlines));
            });
        }

        [TestMethod]
        public void ItalicSkipsANestedBold()
        {
            RunOnSta(() =>
            {
                Italic italic = Render("*a **b** c*").Inlines.OfType<Italic>().Single();

                Assert.AreEqual("b", TextOf(italic.Inlines.OfType<Bold>().Single().Inlines));
                Assert.AreEqual("a b c", TextOf(italic.Inlines));
            });
        }

        [TestMethod]
        public void MarkerInsideCodeSpanDoesNotCloseEmphasis()
        {
            RunOnSta(() =>
            {
                Bold bold = Render("**see `a**b` here**").Inlines.OfType<Bold>().Single();

                Assert.AreEqual("see a**b here", TextOf(bold.Inlines));
            });
        }

        [TestMethod]
        public void UnclosedEmphasisStaysLiteral()
        {
            RunOnSta(() =>
            {
                Assert.AreEqual("**half typed", TextOf(Render("**half typed").Inlines));
                Assert.AreEqual("2 * 3 * 4", TextOf(Render("2 * 3 * 4").Inlines));
                Assert.IsFalse(Render("2 * 3 * 4").Inlines.OfType<Span>().Any());
            });
        }

        [TestMethod]
        public void TableColumnsFollowTheSeparatorAlignment()
        {
            RunOnSta(() =>
            {
                const string markdown =
                    "| Left | Center | Right | Plain |\n" +
                    "|:-----|:------:|------:|-------|\n" +
                    "| a    | b      | c     | d     |";
                Table table = MarkdownFlowRenderer.BuildBlockList(markdown, null).OfType<Table>().Single();
                var expected = new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Left };

                foreach (TableRow row in table.RowGroups[0].Rows)
                {
                    TextAlignment[] actual = row.Cells.Select(cell => ((Paragraph)cell.Blocks.FirstBlock).TextAlignment).ToArray();
                    CollectionAssert.AreEqual(expected, actual);
                }
            });
        }

        [TestMethod]
        public void SeparatorWithFewerColumnsLeavesTheRestLeft()
        {
            CollectionAssert.AreEqual(
                new[] { TextAlignment.Right, TextAlignment.Left, TextAlignment.Left },
                MarkdownFlowRenderer.ParseTableAlignments("--: | ---", 3));
        }

        [TestMethod]
        public void BackslashEscapesMarkdownPunctuation()
        {
            RunOnSta(() =>
            {
                Paragraph paragraph = Render(@"\*not italic\*, \`not code\`, \~~not struck\~~, a \\ b");

                Assert.AreEqual(@"*not italic*, `not code`, ~~not struck~~, a \ b", TextOf(paragraph.Inlines));
                Assert.IsTrue(paragraph.Inlines.All(inline => inline is Run));
            });
        }

        [TestMethod]
        public void EscapedHashAtLineStartIsNoHeading()
        {
            RunOnSta(() =>
            {
                List<Block> blocks = MarkdownFlowRenderer.BuildBlockList(@"\# not a heading", null);

                Assert.AreEqual("# not a heading", TextOf(((Paragraph)blocks.Single()).Inlines));
                Assert.AreEqual(1, blocks.Count);
            });
        }

        [TestMethod]
        public void EscapedMarkerDoesNotCloseEmphasis()
        {
            RunOnSta(() =>
            {
                Bold bold = Render(@"**a \** b**").Inlines.OfType<Bold>().Single();

                Assert.AreEqual("a ** b", TextOf(bold.Inlines));
            });
        }

        [TestMethod]
        public void BackslashBeforeOtherCharactersIsKept()
        {
            RunOnSta(() =>
            {
                Assert.AreEqual(@"C:\Users\me", TextOf(Render(@"C:\Users\me").Inlines));
                Assert.AreEqual(@"\*", TextOf(Render(@"`\*`").Inlines), "code spans are literal");
            });
        }

        [TestMethod]
        public void EscapedPipeStaysInsideTheTableCell()
        {
            RunOnSta(() =>
            {
                Table table = MarkdownFlowRenderer.BuildBlockList("| a | b |\n|---|---|\n| x \\| y | z |", null)
                    .OfType<Table>().Single();
                TableRow row = table.RowGroups[0].Rows[1];

                Assert.AreEqual(2, row.Cells.Count);
                Assert.AreEqual("x | y", TextOf(((Paragraph)row.Cells[0].Blocks.FirstBlock).Inlines));
            });
        }

        [TestMethod]
        public void TaskListShowsBoxesInsteadOfBullets()
        {
            RunOnSta(() =>
            {
                List list = MarkdownFlowRenderer.BuildBlockList("- [x] done\n- [ ] todo **now**\n- [X] also done", null)
                    .OfType<List>().Single();
                string[] items = list.ListItems.Select(item => TextOf(((Paragraph)item.Blocks.FirstBlock).Inlines)).ToArray();

                Assert.AreEqual(TextMarkerStyle.None, list.MarkerStyle);
                CollectionAssert.AreEqual(new[] { "\u2705 done", "\u2610 todo now", "\u2705 also done" }, items);
            });
        }

        [TestMethod]
        public void MixedListKeepsItsBullets()
        {
            RunOnSta(() =>
            {
                List list = MarkdownFlowRenderer.BuildBlockList("- [ ] todo\n- plain", null).OfType<List>().Single();

                Assert.AreEqual(TextMarkerStyle.Disc, list.MarkerStyle);
                Assert.AreEqual("\u2610 todo", TextOf(((Paragraph)list.ListItems.FirstListItem.Blocks.FirstBlock).Inlines));
            });
        }

        [TestMethod]
        public void BracketsWithoutTaskSyntaxStayLiteral()
        {
            RunOnSta(() =>
            {
                List list = MarkdownFlowRenderer.BuildBlockList("- [link](https://x.y) text\n- [] empty", null)
                    .OfType<List>().Single();

                Assert.AreEqual(TextMarkerStyle.Disc, list.MarkerStyle);
                Assert.AreEqual("[] empty", TextOf(((Paragraph)list.ListItems.LastListItem.Blocks.FirstBlock).Inlines));
            });
        }

        [TestMethod]
        public void OrderedTaskListKeepsItsNumbers()
        {
            RunOnSta(() =>
            {
                List list = MarkdownFlowRenderer.BuildBlockList("1. [x] first\n2. [ ] second", null).OfType<List>().Single();

                Assert.AreEqual(TextMarkerStyle.Decimal, list.MarkerStyle);
                Assert.AreEqual("\u2705 first", TextOf(((Paragraph)list.ListItems.FirstListItem.Blocks.FirstBlock).Inlines));
            });
        }

        [TestMethod]
        public void QuoteIsABorderedSectionWithItsOwnBlocks()
        {
            RunOnSta(() =>
            {
                Section quote = MarkdownFlowRenderer.BuildBlockList("> first **bold**\n>\n> second", null)
                    .OfType<Section>().Single();
                Paragraph[] paragraphs = quote.Blocks.OfType<Paragraph>().ToArray();

                Assert.AreEqual(3.0, quote.BorderThickness.Left);
                Assert.AreEqual(2, paragraphs.Length);
                Assert.AreEqual("first bold", TextOf(paragraphs[0].Inlines));
                Assert.AreEqual("second", TextOf(paragraphs[1].Inlines));
                Assert.AreEqual(0.0, paragraphs[1].Margin.Bottom, "no extra gap above the bar's end");
            });
        }

        [TestMethod]
        public void ListInsideAQuoteIsAList()
        {
            RunOnSta(() =>
            {
                Section quote = MarkdownFlowRenderer.BuildBlockList("> Steps:\n> - one\n> - [x] two", null)
                    .OfType<Section>().Single();
                List list = quote.Blocks.OfType<List>().Single();

                Assert.AreEqual(2, list.ListItems.Count);
                Assert.AreEqual("✅ two", TextOf(((Paragraph)list.ListItems.LastListItem.Blocks.FirstBlock).Inlines));
            });
        }

        [TestMethod]
        public void NestedQuoteIsASectionInsideTheQuote()
        {
            RunOnSta(() =>
            {
                Section outer = MarkdownFlowRenderer.BuildBlockList("> outer\n>\n> > inner\n> > > deepest", null)
                    .OfType<Section>().Single();
                Section inner = outer.Blocks.OfType<Section>().Single();
                Section deepest = inner.Blocks.OfType<Section>().Single();

                Assert.AreEqual("outer", TextOf(outer.Blocks.OfType<Paragraph>().Single().Inlines));
                Assert.AreEqual("inner", TextOf(inner.Blocks.OfType<Paragraph>().Single().Inlines));
                Assert.AreEqual("deepest", TextOf(deepest.Blocks.OfType<Paragraph>().Single().Inlines));
            });
        }

        [TestMethod]
        public void QuoteLinesKeepTheirLineBreaks()
        {
            RunOnSta(() =>
            {
                Section quote = MarkdownFlowRenderer.BuildBlockList("> line one\n> line two", null)
                    .OfType<Section>().Single();

                Assert.AreEqual("line one\nline two", TextOf(quote.Blocks.OfType<Paragraph>().Single().Inlines));
            });
        }

        internal static Paragraph Render(string markdown)
        {
            return MarkdownFlowRenderer.BuildBlockList(markdown, null).OfType<Paragraph>().Single();
        }

        internal static string TextOf(IEnumerable<Inline> inlines)
        {
            var text = new StringBuilder();
            foreach (Inline inline in inlines)
            {
                if (inline is Run run) text.Append(run.Text);
                else if (inline is LineBreak) text.Append('\n');
                else if (inline is Span span) text.Append(TextOf(span.Inlines));
            }

            return text.ToString();
        }

        internal static void RunOnSta(Action action)
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
