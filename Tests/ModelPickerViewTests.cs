/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the searchable model picker's layout: sections, search, and the favorites list
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Linq;

using ClaudeCodeVS.Agents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Covers what the Devin model picker shows. The sample list mirrors the shape of a real
    /// <c>devin models list --format json</c> run: an Adaptive family of one, then model families
    /// whose variants are effort levels of the same model.
    /// </summary>
    [TestClass]
    public class ModelPickerViewTests
    {
        private static List<ModelOption> BuildCatalog()
        {
            return new List<ModelOption>
            {
                new ModelOption { Id = "claude-opus-5-medium", Name = "Claude Opus 5 Medium", Group = "Claude Opus 5", ContextTokens = 1000000, CostTier = "High cost" },
                new ModelOption { Id = "claude-opus-5-high", Name = "Claude Opus 5 High", Group = "Claude Opus 5", ContextTokens = 1000000, CostTier = "High cost" },
                new ModelOption { Id = "adaptive", Name = "Adaptive", Group = "Adaptive", Description = "Automatically balances quality and cost" },
                new ModelOption { Id = "claude-sonnet-5-high", Name = "Claude Sonnet 5 High", Group = "Claude Sonnet 5", ContextTokens = 1000000, CostTier = "Med cost" },
                new ModelOption { Id = "swe-1.6", Name = "SWE-1.6", Group = "SWE-1.6" }
            };
        }

        [TestMethod]
        public void Build_PinsAdaptiveAtTheTopWithoutAHeader()
        {
            List<ModelPickerSection> sections = ModelPickerView.Build(BuildCatalog(), null, null);

            Assert.IsFalse(sections[0].HasHeader);
            Assert.AreEqual("adaptive", sections[0].Models.Single().Id);

            // And never a second time among the families.
            Assert.IsFalse(sections.Skip(1).SelectMany(s => s.Models).Any(m => m.Id == "adaptive"));
        }

        [TestMethod]
        public void Build_KeepsTheFamiliesInTheOrderTheCliPrintedThem()
        {
            List<ModelPickerSection> sections = ModelPickerView.Build(BuildCatalog(), null, null);

            CollectionAssert.AreEqual(
                new[] { "Claude Opus 5", "Claude Sonnet 5", "SWE-1.6" },
                sections.Where(s => s.HasHeader).Select(s => s.Name).ToArray());
        }

        [TestMethod]
        public void Build_ShowsFavoritesAboveTheFamilies()
        {
            var favorites = new[] { "claude-sonnet-5-high", "claude-opus-5-high" };

            List<ModelPickerSection> sections = ModelPickerView.Build(BuildCatalog(), favorites, null);

            Assert.AreEqual("Favorites", sections[1].Name);
            CollectionAssert.AreEqual(favorites, sections[1].Models.Select(m => m.Id).ToArray());

            // A favorite still appears under its own family, so the family reads complete.
            Assert.IsTrue(sections.Any(s => s.Name == "Claude Sonnet 5" && s.Models.Any(m => m.Id == "claude-sonnet-5-high")));
        }

        [TestMethod]
        public void Build_IgnoresAFavoriteTheCliNoLongerLists()
        {
            List<ModelPickerSection> sections =
                ModelPickerView.Build(BuildCatalog(), new[] { "claude-opus-4-retired", "swe-1.6" }, null);

            Assert.AreEqual("swe-1.6", sections[1].Models.Single().Id);
        }

        [TestMethod]
        public void Build_NarrowsFavoritesByTheSearchInsteadOfHidingThem()
        {
            // Favorites are user-curated, unlike the automatic "Recently Used" list this replaced, so
            // a search still shows the ones that match rather than dropping the whole section.
            List<ModelPickerSection> sections =
                ModelPickerView.Build(BuildCatalog(), new[] { "swe-1.6", "claude-opus-5-high" }, "opus");

            // No pinned Adaptive section here — "opus" does not match it — so Favorites leads.
            Assert.AreEqual("Favorites", sections[0].Name);
            Assert.AreEqual("claude-opus-5-high", sections[0].Models.Single().Id);
        }

        [TestMethod]
        public void Build_DropsTheFavoritesSectionWhenNoneMatchTheSearch()
        {
            List<ModelPickerSection> sections =
                ModelPickerView.Build(BuildCatalog(), new[] { "swe-1.6" }, "opus");

            Assert.IsFalse(sections.Any(s => s.Name == "Favorites"));
        }

        [TestMethod]
        public void Build_MatchesEveryTypedWordInAnyOrder()
        {
            List<ModelPickerSection> sections = ModelPickerView.Build(BuildCatalog(), null, "high opus");

            Assert.AreEqual("claude-opus-5-high", sections.SelectMany(s => s.Models).Single().Id);
        }

        [TestMethod]
        public void Build_SearchesTheIdAndTheFamilyToo()
        {
            // The id is what the user sees in the details pane and what Devin's own docs use.
            Assert.AreEqual("swe-1.6", ModelPickerView.Build(BuildCatalog(), null, "swe-1.6").SelectMany(s => s.Models).Single().Id);

            Assert.AreEqual(2, ModelPickerView.Build(BuildCatalog(), null, "Claude Opus 5").SelectMany(s => s.Models).Count());
        }

        [TestMethod]
        public void Build_ReturnsNothingWhenNothingMatches()
        {
            Assert.AreEqual(0, ModelPickerView.Build(BuildCatalog(), null, "gemini").Count);
            Assert.AreEqual(0, ModelPickerView.Build(new List<ModelOption>(), null, null).Count);
            Assert.AreEqual(0, ModelPickerView.Build(null, null, null).Count);
        }

        [TestMethod]
        public void ToggleFavorite_AddsANewFavoriteToTheFront()
        {
            List<string> favorites = ModelPickerView.ToggleFavorite(new[] { "b", "a" }, "c");

            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, favorites.ToArray());
        }

        [TestMethod]
        public void ToggleFavorite_RemovesAnExistingFavorite()
        {
            List<string> favorites = ModelPickerView.ToggleFavorite(new[] { "a", "b", "c" }, "b");

            CollectionAssert.AreEqual(new[] { "a", "c" }, favorites.ToArray());
        }

        [TestMethod]
        public void ToggleFavorite_IsNotCappedTheWayRecentPicksWere()
        {
            var existing = new[] { "a", "b", "c", "d", "e" };

            List<string> favorites = ModelPickerView.ToggleFavorite(existing, "f");

            Assert.AreEqual(6, favorites.Count);
            Assert.AreEqual("f", favorites[0]);
        }

        [TestMethod]
        public void ToggleFavorite_IgnoresTheAgentDefaultEntry()
        {
            // "Agent default" is an empty id, not a model, and must not push a blank row into the list.
            List<string> favorites = ModelPickerView.ToggleFavorite(new[] { "a" }, string.Empty);

            CollectionAssert.AreEqual(new[] { "a" }, favorites.ToArray());
        }

        [TestMethod]
        public void IsFavorite_IsCaseInsensitiveAndNullSafe()
        {
            Assert.IsTrue(ModelPickerView.IsFavorite(new[] { "Claude-Opus-5-High" }, "claude-opus-5-high"));
            Assert.IsFalse(ModelPickerView.IsFavorite(new[] { "swe-1.6" }, "claude-opus-5-high"));
            Assert.IsFalse(ModelPickerView.IsFavorite(null, "claude-opus-5-high"));
            Assert.IsFalse(ModelPickerView.IsFavorite(new[] { "a" }, null));
        }

        [TestMethod]
        public void ContextWindowLabel_ReadsTheWayDevinsOwnPickerDoes()
        {
            Assert.AreEqual("1M context", new ModelOption { ContextTokens = 1048576 }.ContextWindowLabel);
            Assert.AreEqual("272K context", new ModelOption { ContextTokens = 272000 }.ContextWindowLabel);
            Assert.AreEqual(string.Empty, new ModelOption().ContextWindowLabel);
        }
    }
}
