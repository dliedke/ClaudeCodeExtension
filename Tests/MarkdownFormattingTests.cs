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
