/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the native-mode tool card presenter (header line and rendered diff)
 *
 * *******************************************************************************************************************/

using System.Linq;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatToolPresentationTests
    {
        [TestMethod]
        public void Read_ShowsTheFileAsTheTarget()
        {
            var result = ChatToolPresenter.Describe("Read", "{\"file_path\":\"C:\\\\repo\\\\Controls\\\\Foo.cs\"}");

            Assert.AreEqual(ChatToolCategory.Read, result.Category);
            Assert.AreEqual("Read", result.Title);
            StringAssert.EndsWith(result.Subtitle, "Controls\\Foo.cs");
            Assert.IsFalse(result.HasDiff);
        }

        [TestMethod]
        public void Read_WithAWindow_ReportsTheLineRange()
        {
            var result = ChatToolPresenter.Describe("Read", "{\"file_path\":\"a.cs\",\"offset\":10,\"limit\":5}");

            Assert.AreEqual("lines 10-14", result.Badge);
        }

        [TestMethod]
        public void Bash_ShowsTheCommandOnOneLine()
        {
            var result = ChatToolPresenter.Describe("Bash", "{\"command\":\"git status\\n  --short\"}");

            Assert.AreEqual(ChatToolCategory.Run, result.Category);
            Assert.AreEqual("git status --short", result.Subtitle);
        }

        [TestMethod]
        public void Edit_BuildsADiffAndCountsTheChange()
        {
            string input = "{\"file_path\":\"a.cs\"," +
                           "\"old_string\":\"one\\ntwo\\nthree\"," +
                           "\"new_string\":\"one\\nTWO\\nthree\"}";

            var result = ChatToolPresenter.Describe("Edit", input);

            Assert.AreEqual(ChatToolCategory.Edit, result.Category);
            Assert.IsTrue(result.HasDiff);
            Assert.AreEqual("+1 -1", result.Badge);

            Assert.IsTrue(result.Diff.Any(l => l.Kind == ChatDiffKind.Added && l.Text == "TWO"));
            Assert.IsTrue(result.Diff.Any(l => l.Kind == ChatDiffKind.Removed && l.Text == "two"));

            // The unchanged neighbours are kept as context so the change has somewhere to sit.
            Assert.IsTrue(result.Diff.Any(l => l.Kind == ChatDiffKind.Context && l.Text == "one"));
        }

        [TestMethod]
        public void Write_CountsEveryLineAsAdded()
        {
            var result = ChatToolPresenter.Describe("Write", "{\"file_path\":\"a.txt\",\"content\":\"a\\nb\\nc\"}");

            Assert.AreEqual("+3", result.Badge);
            Assert.AreEqual(3, result.Diff.Count(l => l.Kind == ChatDiffKind.Added));
        }

        [TestMethod]
        public void MultiEdit_TotalsEveryEditIntoOneCard()
        {
            string input = "{\"file_path\":\"a.cs\",\"edits\":[" +
                           "{\"old_string\":\"a\",\"new_string\":\"A\"}," +
                           "{\"old_string\":\"b\",\"new_string\":\"B\"}]}";

            var result = ChatToolPresenter.Describe("MultiEdit", input);

            Assert.AreEqual("+2 -2", result.Badge);
            Assert.IsTrue(result.Diff.Any(l => l.Kind == ChatDiffKind.Separator && l.Text == "Edit 1 of 2"));
            Assert.IsTrue(result.Diff.Any(l => l.Kind == ChatDiffKind.Separator && l.Text == "Edit 2 of 2"));
        }

        [TestMethod]
        public void TodoWrite_ReportsHowManyAreDone()
        {
            string input = "{\"todos\":[" +
                           "{\"content\":\"one\",\"status\":\"completed\"}," +
                           "{\"content\":\"two\",\"activeForm\":\"Doing two\",\"status\":\"in_progress\"}]}";

            var result = ChatToolPresenter.Describe("TodoWrite", input);

            Assert.AreEqual("1/2", result.Badge);
            Assert.AreEqual("Doing two", result.Subtitle);
        }

        [TestMethod]
        public void McpToolNamesLoseTheirWirePrefix()
        {
            Assert.AreEqual("search_docs", ChatToolPresenter.NormalizeToolName("mcp__library__search_docs"));
            Assert.AreEqual("Read", ChatToolPresenter.NormalizeToolName("Read"));
        }

        /// <summary>
        /// The tool inventory changes with every CLI release, so an unknown name or a payload whose
        /// arguments were renamed must still produce a row instead of throwing.
        /// </summary>
        [TestMethod]
        public void UnknownToolsAndBrokenPayloadsStillProduceARow()
        {
            var unknown = ChatToolPresenter.Describe("SomeFutureTool", "{\"path\":\"x.cs\"}");
            Assert.AreEqual(ChatToolCategory.Other, unknown.Category);
            Assert.AreEqual("SomeFutureTool", unknown.Title);
            Assert.AreEqual("x.cs", unknown.Subtitle);

            var broken = ChatToolPresenter.Describe("Edit", "not json at all");
            Assert.AreEqual("Edit", broken.Title);
            Assert.IsFalse(broken.HasDiff);

            var empty = ChatToolPresenter.Describe(null, null);
            Assert.AreEqual("Tool", empty.Title);
        }

        [TestMethod]
        public void LongPathsKeepTheirLastSegments()
        {
            Assert.AreEqual("…\\b\\c\\d.cs", ChatToolPresenter.ShortenPath("/root/a/b/c/d.cs"));
            Assert.AreEqual("a\\b.cs", ChatToolPresenter.ShortenPath("a/b.cs"));
        }

        [TestMethod]
        public void DurationsReadAsTheCliPrintsThem()
        {
            Assert.AreEqual("0.0s", ChatFormatting.Duration(System.TimeSpan.Zero));
            Assert.AreEqual("12.3s", ChatFormatting.Duration(System.TimeSpan.FromSeconds(12.34)));
            Assert.AreEqual("19m 15s", ChatFormatting.Duration(System.TimeSpan.FromSeconds(19 * 60 + 15)));
            Assert.AreEqual("1h 05m 03s", ChatFormatting.Duration(System.TimeSpan.FromSeconds(3903)));

            // A clock that ran backwards (a machine time change mid-turn) must not print "-3.0s".
            Assert.AreEqual("0.0s", ChatFormatting.Duration(System.TimeSpan.FromSeconds(-3)));
        }

        /// <summary>
        /// One figure, not an in/out/cached breakdown: with prompt caching the input count is almost
        /// always a handful of tokens, and "1 in · 577 out · 25,750 cached" reads as a bug.
        /// </summary>
        [TestMethod]
        public void TokenCountsCollapseToOneFigure()
        {
            Assert.AreEqual("6,912 tokens", ChatFormatting.Tokens(1234, 5678));
            Assert.AreEqual("578 tokens", ChatFormatting.Tokens(1, 577));
            Assert.AreEqual("0 tokens", ChatFormatting.Tokens(0, 0));
        }
    }
}
