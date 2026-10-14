/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers incremental markdown rendering of a streaming reply: where the finished blocks end, and that
 *          rendering the text in pieces split there gives the same blocks as rendering it whole.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Documents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class IncrementalMarkdownTests
    {
        [TestMethod]
        public void BoundaryIsJustPastTheLastCompleteBlankLine()
        {
            const string text = "First paragraph.\n\nSecond one\nstill going";

            Assert.AreEqual("First paragraph.\n\n".Length, MarkdownFlowRenderer.FindStableBoundary(text, 0));
        }

        [TestMethod]
        public void NoBoundaryWithoutABlankLine()
        {
            Assert.AreEqual(0, MarkdownFlowRenderer.FindStableBoundary("One paragraph\nover two lines", 0));
        }

        [TestMethod]
        public void ABlankLineCountsOnlyOnceItsNewlineArrived()
        {
            // "para\n" + "more" is still one paragraph; the empty last line is not a blank line yet.
            Assert.AreEqual(0, MarkdownFlowRenderer.FindStableBoundary("para\n", 0));
            Assert.AreEqual("para\n\n".Length, MarkdownFlowRenderer.FindStableBoundary("para\n\n", 0));
        }

        [TestMethod]
        public void BlankLinesInsideACodeFenceAreNoBoundary()
        {
            const string open = "Intro.\n\n```cs\nint a;\n\nint b;\n";
            Assert.AreEqual("Intro.\n\n".Length, MarkdownFlowRenderer.FindStableBoundary(open, 0));

            const string closed = open + "```\n\nAfter.";
            Assert.AreEqual((open + "```\n\n").Length, MarkdownFlowRenderer.FindStableBoundary(closed, 0));
        }

        [TestMethod]
        public void TildeFenceClosesOnlyOnTildes()
        {
            const string text = "~~~\n```\n\nstill code\n~~~\n\nAfter";

            Assert.AreEqual("~~~\n```\n\nstill code\n~~~\n\n".Length, MarkdownFlowRenderer.FindStableBoundary(text, 0));
        }

        [TestMethod]
        public void ScanResumesFromAPreviousBoundary()
        {
            const string text = "A.\n\nB.\n\nC";
            int first = MarkdownFlowRenderer.FindStableBoundary("A.\n\n", 0);

            Assert.AreEqual("A.\n\nB.\n\n".Length, MarkdownFlowRenderer.FindStableBoundary(text, first));
            Assert.AreEqual(first, MarkdownFlowRenderer.FindStableBoundary("A.\n\nB.", first));
        }

        [TestMethod]
        public void WindowsLineEndingsWork()
        {
            Assert.AreEqual("A.\r\n\r\n".Length, MarkdownFlowRenderer.FindStableBoundary("A.\r\n\r\nB", 0));
        }

        private const string Sample =
            "# Heading\n" +
            "Intro paragraph with **bold**, `code` and a [link](https://example.com).\n" +
            "Second line of the same paragraph.\n" +
            "\n" +
            "- item one\n" +
            "  continued\n" +
            "- item two\n" +
            "  - nested\n" +
            "\n" +
            "1. first\n" +
            "\n" +
            "2. second\n" +
            "\n" +
            "| A | B |\n" +
            "|---|---|\n" +
            "| 1 | 2 |\n" +
            "\n" +
            "> quoted\n" +
            "> more\n" +
            "\n" +
            "```csharp\n" +
            "var a = 1;\n" +
            "\n" +
            "var b = 2;\n" +
            "```\n" +
            "\n" +
            "---\n" +
            "\n" +
            "Closing paragraph.\n";

        /// <summary>
        /// For every prefix a stream could have shown, the boundary found in that prefix splits the
        /// final text into two parts that render to exactly the blocks of the whole text.
        /// </summary>
        [TestMethod]
        public void RenderingInPiecesAtEveryStreamedBoundaryMatchesRenderingWhole()
        {
            RunOnSta(() =>
            {
                List<string> whole = Describe(MarkdownFlowRenderer.BuildBlockList(Sample, null));
                var checkedBoundaries = new HashSet<int>();

                for (int shown = 0; shown <= Sample.Length; shown++)
                {
                    int boundary = MarkdownFlowRenderer.FindStableBoundary(Sample.Substring(0, shown), 0);
                    if (!checkedBoundaries.Add(boundary)) continue;

                    var pieces = new List<Block>();
                    pieces.AddRange(MarkdownFlowRenderer.BuildBlockList(Sample.Substring(0, boundary), null));
                    pieces.AddRange(MarkdownFlowRenderer.BuildBlockList(Sample.Substring(boundary), null));

                    CollectionAssert.AreEqual(whole, Describe(pieces), "Split at " + boundary);
                }

                Assert.IsTrue(checkedBoundaries.Count > 5, "The sample should produce several boundaries.");
            });
        }

        /// <summary>Block type and text, enough to tell two renders apart.</summary>
        private static List<string> Describe(IEnumerable<Block> blocks)
        {
            var document = new FlowDocument();
            var list = blocks.ToList();
            foreach (Block block in list)
            {
                document.Blocks.Add(block);
            }

            return list.Select(block => block.GetType().Name + ": " +
                new TextRange(block.ContentStart, block.ContentEnd).Text).ToList();
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
