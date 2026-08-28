/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the once-per-session re-read of each agent's model list
 *
 * *******************************************************************************************************************/

using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// A list cached by an earlier Visual Studio session must be shown immediately but re-read once
    /// per session: the CLI can be updated (or the account's entitlements changed) while VS is closed,
    /// and an extension update can start reading fields an older cache does not carry — which is how
    /// the Devin cost tier / context window added in v161.0 stayed invisible behind an unexpired 24h
    /// stamp. The logic lives on a WPF control that cannot be instantiated without a shell, so it is
    /// guarded at the source level in the style of <see cref="DebugVisibilityTests"/>.
    /// </summary>
    [TestClass]
    public class ModelCatalogRefreshTests
    {
        private static string Source => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ModelCatalog.cs");

        [TestMethod]
        public void AListCachedByAnEarlierSessionIsNotTrustedOnItsTimestamp()
        {
            string body = ExtractMethodBody(Source, "private bool ShouldRefreshProviderModels(AiProvider provider)");

            // Without this the TTL alone decides, and a cache written yesterday survives a restart —
            // including a restart onto a build that parses more than the cache holds.
            StringAssert.Contains(body, "if (!ModelCatalogsReadThisSession.Contains(provider)) return true;");
            StringAssert.Contains(body, "ModelCatalogTimeToLive",
                "The within-session TTL must stay: a VS window can be left open for days.");
        }

        [TestMethod]
        public void TheSessionIsMarkedEvenWhenTheListingFails()
        {
            string body = ExtractMethodBody(
                Source, "private async Task<List<ModelOption>> FetchProviderModelsAsync(AiProvider provider, ModelCatalogSource source)");

            // Marking only on success would restart a missing/broken CLI on every menu open and make
            // the user wait out its timeout each time.
            int marked = body.IndexOf("ModelCatalogsReadThisSession.Add(provider);", StringComparison.Ordinal);
            int emptyGuard = body.IndexOf("if (models == null || models.Count == 0)", StringComparison.Ordinal);

            Assert.IsTrue(marked >= 0, "The fetch must record that this session has read the list.");
            Assert.IsTrue(emptyGuard > marked,
                "The session must be marked before the empty-result path returns the previous list.");
        }

        [TestMethod]
        public void OnlyTheActiveAgentIsWarmedUpAtStartup()
        {
            string body = ExtractMethodBody(Source, "private void WarmUpActiveProviderModelCatalog()");

            // Warming every provider would start one process per agent at every VS startup, for lists
            // the user may never open.
            StringAssert.Contains(body, "GetActiveOrSelectedProvider()");
            StringAssert.Contains(body, "ShouldRefreshProviderModels(provider.Value)",
                "The warm-up must respect the same staleness rule as the menus, or it re-reads on every load.");

            StringAssert.Contains(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.cs"),
                "WarmUpActiveProviderModelCatalog();",
                "The startup path must call the warm-up, or the first menu open still shows yesterday's list.");
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Method not found; update this guard with the rename: {signature}");

            int open = source.IndexOf('{', start + signature.Length);
            Assert.IsTrue(open >= 0, $"No body found for: {signature}");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail($"Unbalanced braces while reading: {signature}");
            return string.Empty;
        }
    }
}
