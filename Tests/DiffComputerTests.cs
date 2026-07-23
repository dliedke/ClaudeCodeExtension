/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the diff computation that feeds the diff viewer — line classification, line
 *          numbering, added/removed counts and the collapsing of unchanged regions.
 *
 * *******************************************************************************************************************/

using System;
using System.Linq;
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class DiffComputerTests
    {
        private const int ContextLines = 3;

        private static ChangedFile Diff(string original, string modified)
        {
            var file = new ChangedFile
            {
                FilePath = @"C:\GitLab\Project\App.cs",
                OriginalContent = original,
                ModifiedContent = modified
            };

            DiffComputer.ComputeDiff(file);
            return file;
        }

        private static string Numbered(int count, int from = 1)
        {
            return string.Join(Environment.NewLine, Enumerable.Range(from, count).Select(i => $"line {i}"));
        }

        [TestMethod]
        public void ComputeDiff_CountsAddedAndRemovedLines()
        {
            var file = Diff("a\nb\nc", "a\nB1\nB2\nc");

            Assert.AreEqual(2, file.LinesAdded);
            Assert.AreEqual(1, file.LinesRemoved);
        }

        [TestMethod]
        public void ComputeDiff_ClassifiesEachLine()
        {
            var file = Diff("keep\nremove", "keep\nadd");

            Assert.IsTrue(file.DiffLines.Any(l => l.Type == DiffLineType.Context && l.Text == "keep"));
            Assert.IsTrue(file.DiffLines.Any(l => l.Type == DiffLineType.Removed && l.Text == "remove"));
            Assert.IsTrue(file.DiffLines.Any(l => l.Type == DiffLineType.Added && l.Text == "add"));
        }

        /// <summary>
        /// A removed line only exists on the left side and an added line only on the right, so each
        /// carries exactly one line number. Filling in both is what makes a diff gutter lie.
        /// </summary>
        [TestMethod]
        public void ComputeDiff_NumbersRemovedAndAddedLinesOnOneSideOnly()
        {
            var file = Diff("keep\nremove", "keep\nadd");

            var removed = file.DiffLines.Single(l => l.Type == DiffLineType.Removed);
            Assert.AreEqual(2, removed.OldLineNumber);
            Assert.IsNull(removed.NewLineNumber);

            var added = file.DiffLines.Single(l => l.Type == DiffLineType.Added);
            Assert.IsNull(added.OldLineNumber);
            Assert.AreEqual(2, added.NewLineNumber);
        }

        [TestMethod]
        public void ComputeDiff_ProducesNoLinesWhenNothingChanged()
        {
            var file = Diff("same\ncontent", "same\ncontent");

            Assert.AreEqual(0, file.LinesAdded);
            Assert.AreEqual(0, file.LinesRemoved);
            Assert.AreEqual(0, file.DiffLines.Count);
        }

        /// <summary>
        /// Long untouched stretches are collapsed to a "..." marker so a one-line change in a huge
        /// file doesn't render the whole file.
        /// </summary>
        [TestMethod]
        public void ComputeDiff_CollapsesUnchangedRegionsBetweenDistantChanges()
        {
            string original = Numbered(40);
            string modified = original
                .Replace("line 2" + Environment.NewLine, "line 2 CHANGED" + Environment.NewLine)
                .Replace("line 38" + Environment.NewLine, "line 38 CHANGED" + Environment.NewLine);

            var file = Diff(original, modified);

            Assert.IsTrue(file.DiffLines.Any(l => l.Text == "..."), "Expected a collapsed-region marker.");
            Assert.IsTrue(
                file.DiffLines.Count < 40,
                $"Expected the untouched middle to be collapsed, got {file.DiffLines.Count} lines.");
        }

        [TestMethod]
        public void ComputeDiff_KeepsContextLinesAroundAChange()
        {
            string original = Numbered(20);
            string modified = original.Replace("line 10" + Environment.NewLine, "line 10 CHANGED" + Environment.NewLine);

            var file = Diff(original, modified);

            // Three context lines on each side of the change.
            Assert.IsTrue(file.DiffLines.Any(l => l.Text == $"line {10 - ContextLines}"));
            Assert.IsTrue(file.DiffLines.Any(l => l.Text == $"line {10 + ContextLines}"));
            Assert.IsFalse(file.DiffLines.Any(l => l.Text == $"line {10 - ContextLines - 1}"));
        }

        [TestMethod]
        public void ComputeDiff_TreatsNullContentAsEmpty()
        {
            var created = Diff(null, "brand\nnew");
            Assert.AreEqual(2, created.LinesAdded);
            Assert.AreEqual(0, created.LinesRemoved);

            var deleted = Diff("gone\naway", null);
            Assert.AreEqual(0, deleted.LinesAdded);
            Assert.AreEqual(2, deleted.LinesRemoved);
        }

        [TestMethod]
        public void ComputeDiff_IgnoresANullFileInsteadOfThrowing()
        {
            DiffComputer.ComputeDiff(null);
        }

        [TestMethod]
        public void ComputeDiffs_ProcessesEveryFileInTheBatch()
        {
            var files = new[]
            {
                new ChangedFile { FilePath = "a.cs", OriginalContent = "a", ModifiedContent = "b" },
                new ChangedFile { FilePath = "b.cs", OriginalContent = "c", ModifiedContent = "c\nd" }
            };

            DiffComputer.ComputeDiffs(files);

            Assert.AreEqual(1, files[0].LinesAdded);
            Assert.AreEqual(1, files[1].LinesAdded);
        }
    }
}
