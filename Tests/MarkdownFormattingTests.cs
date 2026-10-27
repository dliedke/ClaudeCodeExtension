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
