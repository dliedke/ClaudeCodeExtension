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
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatTextViewTests
    {
        [TestMethod]
        public void CodeBlocksUseTextBlockInsteadOfTextBox()
        {
            RunOnSta(() =>
            {
                List<Block> blocks = MarkdownFlowRenderer.BuildBlockList("```cs\nvar a = 1;\n```", null);
                BlockUIContainer container = blocks.OfType<BlockUIContainer>().Single();
                var border = container.Child as Border;
                var grid = border?.Child as Grid;

                Assert.IsNotNull(grid, "The code block should keep its bordered Grid layout.");
                Assert.IsTrue(grid.Children.OfType<TextBlock>().Any(), "The code body should be a TextBlock.");
                Assert.IsFalse(grid.Children.OfType<TextBox>().Any(),
                    "A TextBox here would reintroduce the WPF TextBoxView crash path.");
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
