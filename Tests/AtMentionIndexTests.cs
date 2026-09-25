/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the "@" file picker index — settings parsing, file-type / folder filtering,
 *          derived folders and shallow-first ordering (issue #174).
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class AtMentionIndexTests
    {
        private static List<string> Build(IEnumerable<string> files, string fileTypes = "", string excluded = "",
            bool unity = false, int max = 50000)
        {
            var filter = ClaudeCodeControl.CreateAtMentionFilter(fileTypes, excluded, unity);
            return ClaudeCodeControl.BuildAtMentionIndex(files, filter, max);
        }

        [TestMethod]
        public void FileTypes_AcceptDotBareAndWildcardForms()
        {
            var filter = ClaudeCodeControl.CreateAtMentionFilter(".cs, lua;*.shader", "", false);

            CollectionAssert.AreEquivalent(new[] { ".cs", ".lua", ".shader" }, filter.FileTypes.ToList());
            Assert.IsTrue(filter.IncludesFile("Assets/Scripts/Player.CS"));
            Assert.IsTrue(filter.IncludesFile("Assets/Lua/boot.lua"));
            Assert.IsFalse(filter.IncludesFile("Assets/Textures/grass.png"));
        }

        [TestMethod]
        public void FileTypes_OnlyListMatchingFilesAndTheFoldersHoldingThem()
        {
            var index = Build(new[]
            {
                "Assets/Scripts/Player.cs",
                "Assets/Textures/grass.png",
                "Assets/Lua/boot.lua",
                "README.md"
            }, fileTypes: ".cs, .lua");

            CollectionAssert.AreEqual(new[]
            {
                "Assets/",
                "Assets/Lua/",
                "Assets/Scripts/",
                "Assets/Lua/boot.lua",
                "Assets/Scripts/Player.cs"
            }, index);
        }

        [TestMethod]
        public void EmptyFileTypes_ListsEveryFile()
        {
            var index = Build(new[] { "a.png", "b.cs" });

            CollectionAssert.AreEqual(new[] { "a.png", "b.cs" }, index);
        }

        [TestMethod]
        public void ExcludedFolders_BareNameMatchesAnyDepth_PathMatchesOnlyThere()
        {
            var index = Build(new[]
            {
                "Assets/Plugins/x.cs",
                "Packages2/Plugins/y.cs",
                "Assets/ThirdParty/z.cs",
                "Other/ThirdParty/w.cs",
                "Assets/Third Party/v.cs",
                "Assets/Scripts/keep.cs"
            }, excluded: "Plugins, Assets\\ThirdParty/; Third Party");

            Assert.IsFalse(index.Any(e => e.Contains("Plugins")));
            Assert.IsFalse(index.Contains("Assets/ThirdParty/z.cs"));
            Assert.IsFalse(index.Contains("Assets/Third Party/v.cs"));
            Assert.IsTrue(index.Contains("Other/ThirdParty/w.cs"));
            Assert.IsTrue(index.Contains("Assets/Scripts/keep.cs"));
        }

        [TestMethod]
        public void BuiltInIgnoredFolders_AreAlwaysSkipped()
        {
            var index = Build(new[] { "src/app.cs", "src/bin/Debug/app.dll", "node_modules/pkg/index.js" });

            CollectionAssert.AreEqual(new[] { "src/", "src/app.cs" }, index);
        }

        [TestMethod]
        public void UnityProject_SkipsGeneratedRootFoldersAndMetaFiles()
        {
            var index = Build(new[]
            {
                "Library/ScriptAssemblies/Assembly-CSharp.dll",
                "Temp/x.tmp",
                "Assets/Scripts/Player.cs",
                "Assets/Scripts/Player.cs.meta",
                "Assets/Library/keep.cs"
            }, unity: true);

            CollectionAssert.AreEqual(new[]
            {
                "Assets/",
                "Assets/Library/",
                "Assets/Scripts/",
                "Assets/Library/keep.cs",
                "Assets/Scripts/Player.cs"
            }, index);
        }

        [TestMethod]
        public void NonUnityProject_KeepsLibraryFolderAndMetaFiles()
        {
            var index = Build(new[] { "Library/a.cs", "b.meta" });

            CollectionAssert.Contains(index, "Library/a.cs");
            CollectionAssert.Contains(index, "b.meta");
        }

        [TestMethod]
        public void Cap_AppliesToKeptFilesNotToFilteredOnes()
        {
            // 10 000 non-matching files ahead of the scripts must not use up the budget (issue #174).
            var files = Enumerable.Range(0, 10000).Select(i => "Assets/Art/tex" + i + ".png")
                .Concat(new[] { "Assets/Scripts/A.cs", "Assets/Scripts/B.cs" });

            var index = Build(files, fileTypes: ".cs", max: 5);

            CollectionAssert.Contains(index, "Assets/Scripts/A.cs");
            CollectionAssert.Contains(index, "Assets/Scripts/B.cs");
        }

        [TestMethod]
        public void Ordering_IsShallowFirstWithFoldersBeforeFiles()
        {
            var index = Build(new[] { "z/deep/file.cs", "b.cs", "a/c.cs" });

            CollectionAssert.AreEqual(new[] { "a/", "z/", "b.cs", "z/deep/", "a/c.cs", "z/deep/file.cs" }, index);
        }

        [TestMethod]
        public void BackslashAndDotSlashPaths_AreNormalized()
        {
            var index = Build(new[] { "src\\a.cs", "./b.cs" });

            CollectionAssert.AreEqual(new[] { "src/", "b.cs", "src/a.cs" }, index);
        }
    }
}
