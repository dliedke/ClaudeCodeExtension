/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers parsing of "git status --porcelain -z" output, which feeds the diff viewer's list
 *          of changed files.
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class GitStatusParsingTests
    {
        private static List<ClaudeCodeControl.GitStatusEntry> Parse(string output)
        {
            return ClaudeCodeControl.ParseGitStatusEntries(output).ToList();
        }

        [TestMethod]
        public void ParseGitStatusEntries_ReadsStatusAndPathOfSimpleEntries()
        {
            var entries = Parse(" M src/App.cs\0?? notes.txt\0 D old/Gone.cs\0");

            Assert.AreEqual(3, entries.Count);

            Assert.AreEqual("src/App.cs", entries[0].Path);
            Assert.IsTrue(entries[0].IsModified);

            Assert.AreEqual("notes.txt", entries[1].Path);
            Assert.IsTrue(entries[1].IsUntracked);

            Assert.AreEqual("old/Gone.cs", entries[2].Path);
            Assert.IsTrue(entries[2].IsDeleted);
        }

        /// <summary>
        /// A rename or copy occupies TWO NUL-separated records: the new path rides on the status
        /// record and the old path follows in the next one. Consuming only the first would leave
        /// the old path to be parsed as a bogus entry of its own.
        /// </summary>
        [TestMethod]
        public void ParseGitStatusEntries_PairsRenameRecordsWithTheirOldPath()
        {
            var entries = Parse("R  new/Renamed.cs\0old/Original.cs\0 M other.cs\0");

            Assert.AreEqual(2, entries.Count);

            Assert.IsTrue(entries[0].IsRenameOrCopy);
            Assert.AreEqual("old/Original.cs", entries[0].Path);
            Assert.AreEqual("new/Renamed.cs", entries[0].NewPath);

            Assert.AreEqual("other.cs", entries[1].Path);
        }

        [TestMethod]
        public void ParseGitStatusEntries_PairsCopyRecordsToo()
        {
            var entries = Parse("C  copy/Dest.cs\0src/Source.cs\0");

            Assert.AreEqual(1, entries.Count);
            Assert.IsTrue(entries[0].IsRenameOrCopy);
            Assert.AreEqual("src/Source.cs", entries[0].Path);
            Assert.AreEqual("copy/Dest.cs", entries[0].NewPath);
        }

        [TestMethod]
        public void ParseGitStatusEntries_RecognizesAddedTypeChangedAndUnmerged()
        {
            var entries = Parse("A  added.cs\0T  type.cs\0UU conflict.cs\0");

            Assert.IsTrue(entries[0].IsAdded);
            Assert.IsTrue(entries[1].IsTypeChanged);
            Assert.IsTrue(entries[2].IsUnmerged);
        }

        /// <summary>
        /// A path containing spaces must survive intact — the record is NUL-delimited, so only the
        /// fixed three-character status prefix may be stripped.
        /// </summary>
        [TestMethod]
        public void ParseGitStatusEntries_KeepsPathsWithSpaces()
        {
            var entries = Parse(" M My Folder/My File.cs\0");

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("My Folder/My File.cs", entries[0].Path);
        }

        [TestMethod]
        public void ParseGitStatusEntries_SkipsEmptyAndTruncatedRecords()
        {
            var entries = Parse("\0 M\0 M ok.cs\0\0");

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("ok.cs", entries[0].Path);
        }

        [TestMethod]
        public void ParseGitStatusEntries_ReturnsNothingForNullOrEmptyOutput()
        {
            Assert.AreEqual(0, Parse(null).Count);
            Assert.AreEqual(0, Parse(string.Empty).Count);
        }
    }
}
