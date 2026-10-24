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
                var block = new MarkdownBlock { IsPlainText = true, Markdown = "hello\nworld" };

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
