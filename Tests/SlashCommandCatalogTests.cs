/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the "/" command picker's discovery, front-matter parsing, merging and ranking (issue #187).
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Linq;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SlashCommandCatalogTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "slash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Write(string relative, string content)
        {
            string path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        [TestMethod]
        public void TryGetQuery_OnlyInsideTheLeadingSlashToken()
        {
            Assert.IsTrue(SlashCommandCatalog.TryGetQuery("/", 1, out string q));
            Assert.AreEqual(string.Empty, q);
            Assert.IsTrue(SlashCommandCatalog.TryGetQuery("/comp", 5, out q));
            Assert.AreEqual("comp", q);
            Assert.IsTrue(SlashCommandCatalog.TryGetQuery("/compact", 3, out q));
            Assert.AreEqual("co", q);

            Assert.IsFalse(SlashCommandCatalog.TryGetQuery("/compact now", 12, out _));
            Assert.IsFalse(SlashCommandCatalog.TryGetQuery("fix /tmp", 8, out _));
            Assert.IsFalse(SlashCommandCatalog.TryGetQuery("/usr/bin", 8, out _));
            Assert.IsFalse(SlashCommandCatalog.TryGetQuery("", 0, out _));
            Assert.IsFalse(SlashCommandCatalog.TryGetQuery("/x", 0, out _));
        }

        [TestMethod]
        public void ParseMarkdown_ReadsQuotedAndBlockDescriptions()
        {
            var quoted = SlashCommandCatalog.ParseMarkdown(
                "---\ndescription: \"Build the docket\"\nallowed-tools: Read\n---\nBody", "docket", "user");
            Assert.AreEqual("docket", quoted.Name);
            Assert.AreEqual("Build the docket", quoted.Description);

            var block = SlashCommandCatalog.ParseMarkdown(
                "---\r\nname: plan-review\r\ndescription: >\r\n  Adversarial plan review.\r\n  Invoke only on request.\r\n---\r\nBody",
                "folder", "project");
            Assert.AreEqual("plan-review", block.Name);
            Assert.AreEqual("Adversarial plan review. Invoke only on request.", block.Description);
        }

        [TestMethod]
        public void ParseMarkdown_FallsBackToFirstBodyLineAndHidesNonInvocableSkills()
        {
            var noMeta = SlashCommandCatalog.ParseMarkdown("# Add a working directory\n\nDetails", "add-dir", "user");
            Assert.AreEqual("add-dir", noMeta.Name);
            Assert.AreEqual("Add a working directory", noMeta.Description);

            Assert.IsNull(SlashCommandCatalog.ParseMarkdown(
                "---\nuser-invocable: false\ndescription: x\n---\n", "hidden", "user"));
        }

        [TestMethod]
        public void Discover_ReadsCommandsAndSkills_ProjectWinsOverUser()
        {
            Write("user/commands/shared.md", "---\ndescription: from user\n---\n");
            Write("user/commands/only-user.md", "---\ndescription: user one\n---\n");
            Write("user/skills/graph/SKILL.md", "---\nname: gsd-graphify\ndescription: Build a graph\n---\n");
            Write("ws/.claude/commands/shared.md", "---\ndescription: from project\n---\n");
            Write("ws/.claude/commands/sub/nested.md", "Nested command\n");

            var found = SlashCommandCatalog.Discover(Path.Combine(_root, "ws"), Path.Combine(_root, "user"));
            var byName = found.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

            Assert.AreEqual("from project", byName["shared"].Description);
            Assert.AreEqual("project", byName["shared"].Source);
            Assert.AreEqual("user one", byName["only-user"].Description);
            Assert.AreEqual("Build a graph", byName["gsd-graphify"].Description);
            Assert.AreEqual("Nested command", byName["nested"].Description);
        }

        [TestMethod]
        public void Discover_MissingFoldersYieldNothing()
        {
            Assert.AreEqual(0, SlashCommandCatalog.Discover(Path.Combine(_root, "nope"), Path.Combine(_root, "nada")).Count);
            Assert.AreEqual(0, SlashCommandCatalog.Discover(null, null).Count);
        }

        [TestMethod]
        public void Merge_KeepsDescribedEntriesAndAddsBareAnnouncedNames()
        {
            var discovered = new[] { new SlashCommandEntry { Name = "compact", Description = "mine", Source = "user" } };
            var merged = SlashCommandCatalog.Merge(discovered, new[] { "/compact", "context", "  ", "PLAN" });

            Assert.AreEqual("mine", merged.Single(e => e.Name == "compact").Description);
            Assert.AreEqual("built-in", merged.Single(e => e.Name == "context").Source);
            Assert.AreEqual("Show current context usage", merged.Single(e => e.Name == "context").Description);

            var unknown = SlashCommandCatalog.Merge(null, new[] { "some-plugin:thing" }).Single(e => e.Name == "some-plugin:thing");
            Assert.AreEqual("session", unknown.Source);
            Assert.AreEqual(string.Empty, unknown.Description);
            Assert.AreEqual(1, merged.Count(e => string.Equals(e.Name, "plan", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void Rank_PrefixThenNameThenDescription()
        {
            var entries = new[]
            {
                new SlashCommandEntry { Name = "dossier-review", Description = "Run the review" },
                new SlashCommandEntry { Name = "review", Description = "Review a PR" },
                new SlashCommandEntry { Name = "docket", Description = "Build the dossier docket" },
                new SlashCommandEntry { Name = "plan-review", Description = "x" }
            };

            CollectionAssert.AreEqual(
                new[] { "review", "dossier-review", "plan-review" },
                SlashCommandCatalog.Rank(entries, "review", 10).Select(e => e.Name).ToArray());

            CollectionAssert.AreEqual(
                new[] { "dossier-review", "docket" },
                SlashCommandCatalog.Rank(entries, "dos", 10).Select(e => e.Name).ToArray());

            Assert.AreEqual(2, SlashCommandCatalog.Rank(entries, "", 2).Count);
        }

        [TestMethod]
        public void ClaudeSessionColor_RecognizesOnlyTheWholeColorNotice()
        {
            Assert.IsTrue(ClaudeSessionColor.TryParseNotice("Session color set to: blue", out string hex));
            Assert.AreEqual("#3B82F6", hex);
            Assert.IsTrue(ClaudeSessionColor.TryParseNotice("  Session color set to: PINK\n", out hex));
            Assert.AreEqual("#F778BA", hex);
            Assert.IsTrue(ClaudeSessionColor.TryParseNotice("Session color reset to default", out hex));
            Assert.AreEqual(string.Empty, hex);

            Assert.IsFalse(ClaudeSessionColor.TryParseNotice("Session color set to: mauve", out _));
            Assert.IsFalse(ClaudeSessionColor.TryParseNotice("It printed Session color set to: blue and then stopped", out _));
            Assert.IsFalse(ClaudeSessionColor.TryParseNotice("Invalid color \"x\". Available colors: red", out _));
            Assert.IsFalse(ClaudeSessionColor.TryParseNotice(null, out _));
        }

        [TestMethod]
        public void ToString_TruncatesLongDescriptions()
        {
            var entry = new SlashCommandEntry { Name = "x", Description = new string('a', 300), Source = "user" };
            string text = entry.ToString();
            Assert.IsTrue(text.StartsWith("/x  —  "));
            Assert.IsTrue(text.Length < 140);
            Assert.IsTrue(text.EndsWith("(user)"));
        }
    }
}
