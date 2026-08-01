/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the model-list parsers and the ACP launch flag that carries a model
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;

using ClaudeCodeVS.Agents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Covers the text handling behind the model menu. The sample outputs are trimmed copies of what
    /// the real CLIs printed when each listing command was measured.
    /// </summary>
    [TestClass]
    public class ModelCatalogTests
    {
        #region Codex

        [TestMethod]
        public void ParseCodexCatalog_ReadsSlugAndDisplayName()
        {
            const string json = @"{
              ""models"": [
                { ""slug"": ""gpt-5.6-sol"", ""display_name"": ""GPT-5.6-Sol"", ""visibility"": ""list"" },
                { ""slug"": ""gpt-5.6-codex"", ""display_name"": ""GPT-5.6-Codex"", ""visibility"": ""list"" }
              ]
            }";

            List<ModelOption> models = ModelCatalogParsers.ParseCodexCatalog(json);

            Assert.AreEqual(2, models.Count);
            Assert.AreEqual("gpt-5.6-sol", models[0].Id);
            Assert.AreEqual("GPT-5.6-Sol", models[0].DisplayName);
        }

        [TestMethod]
        public void ParseCodexCatalog_SkipsModelsCodexItselfHides()
        {
            const string json = @"{
              ""models"": [
                { ""slug"": ""gpt-5.6-sol"", ""display_name"": ""GPT-5.6-Sol"", ""visibility"": ""list"" },
                { ""slug"": ""gpt-5-legacy"", ""display_name"": ""Legacy"", ""visibility"": ""hidden"" }
              ]
            }";

            List<ModelOption> models = ModelCatalogParsers.ParseCodexCatalog(json);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("gpt-5.6-sol", models[0].Id);
        }

        [TestMethod]
        public void ParseCodexCatalog_ReturnsEmptyForOutputThatIsNotTheCatalog()
        {
            Assert.AreEqual(0, ModelCatalogParsers.ParseCodexCatalog("codex: unknown command 'debug'").Count);
            Assert.AreEqual(0, ModelCatalogParsers.ParseCodexCatalog(string.Empty).Count);
            Assert.AreEqual(0, ModelCatalogParsers.ParseCodexCatalog(null).Count);
        }

        #endregion

        #region Cursor Agent

        [TestMethod]
        public void ParseIdDashNameList_TakesTheEntriesAndDropsTheBanner()
        {
            const string text =
                "Available models:\n" +
                "auto - Auto\n" +
                "claude-4.5-sonnet - Claude 4.5 Sonnet\n" +
                "gpt-5 - GPT-5\n";

            List<ModelOption> models = ModelCatalogParsers.ParseIdDashNameList(text);

            CollectionAssert.AreEqual(
                new[] { "auto", "claude-4.5-sonnet", "gpt-5" },
                models.Select(m => m.Id).ToArray());
            Assert.AreEqual("Claude 4.5 Sonnet", models[1].DisplayName);
        }

        [TestMethod]
        public void ParseIdDashNameList_IgnoresColourEscapesAndCarriageReturns()
        {
            const string text = "\u001B[1mAvailable models:\u001B[0m\r\n\u001B[32mgpt-5\u001B[0m - GPT-5\r\n";

            List<ModelOption> models = ModelCatalogParsers.ParseIdDashNameList(text);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("gpt-5", models[0].Id);
            Assert.AreEqual("GPT-5", models[0].DisplayName);
        }

        [TestMethod]
        public void ParseIdDashNameList_DropsTheMarkerCursorPutsOnTheActiveModel()
        {
            const string text =
                "auto - Auto (current, default)\n" +
                "claude-fable-5-high - Fable 5 1M (NO ZDR)\n";

            List<ModelOption> models = ModelCatalogParsers.ParseIdDashNameList(text);

            // The state marker goes; a parenthesis that is part of the name stays.
            Assert.AreEqual("Auto", models[0].DisplayName);
            Assert.AreEqual("Fable 5 1M (NO ZDR)", models[1].DisplayName);
        }

        #endregion

        #region PI

        [TestMethod]
        public void ParsePiModelList_JoinsProviderAndModelTheWayPiTakesThemBack()
        {
            const string text =
                "provider     model              context   max-out\n" +
                "anthropic    claude-opus-5      200000    64000\n" +
                "openai       gpt-5.6            400000    128000\n";

            List<ModelOption> models = ModelCatalogParsers.ParsePiModelList(text);

            CollectionAssert.AreEqual(
                new[] { "anthropic/claude-opus-5", "openai/gpt-5.6" },
                models.Select(m => m.Id).ToArray());
        }

        [TestMethod]
        public void ParsePiModelList_DropsTheHeaderRowAndProseLines()
        {
            const string text =
                "provider     model\n" +
                "No models are configured yet\n" +
                "anthropic    claude-opus-5\n";

            List<ModelOption> models = ModelCatalogParsers.ParsePiModelList(text);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("anthropic/claude-opus-5", models[0].Id);
        }

        #endregion

        #region Antigravity / Open Code

        [TestMethod]
        public void ParsePlainList_TakesOneIdPerLine()
        {
            const string text = "gemini-3-pro\ngemini-3-flash\nclaude-opus-5\n";

            List<ModelOption> models = ModelCatalogParsers.ParsePlainList(text);

            CollectionAssert.AreEqual(
                new[] { "gemini-3-pro", "gemini-3-flash", "claude-opus-5" },
                models.Select(m => m.Id).ToArray());
        }

        [TestMethod]
        public void ParsePlainList_DropsBannersAndWarnings()
        {
            const string text =
                "Available models:\n" +
                "opencode/big-pickle\n" +
                "  ⚠ update available\n" +
                "\n";

            List<ModelOption> models = ModelCatalogParsers.ParsePlainList(text);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("opencode/big-pickle", models[0].Id);
        }

        [TestMethod]
        public void ParsePlainList_ListsEachModelOnce()
        {
            // A WSL profile that echoes its command prints the list twice.
            const string text = "gemini-3-pro\ngemini-3-pro\n";

            Assert.AreEqual(1, ModelCatalogParsers.ParsePlainList(text).Count);
        }

        #endregion

        #region Grouping

        /// <summary>A list of the given length, ids shaped like the ones cursor-agent prints.</summary>
        private static List<ModelOption> Models(params string[] ids)
        {
            return ids.Select(id => new ModelOption { Id = id, Name = id }).ToList();
        }

        [TestMethod]
        public void Group_LeavesAShortListFlat()
        {
            List<ModelGroup> groups = ModelCatalogGrouping.Group(
                Models("gpt-5.6-sol", "gpt-5.6-codex", "gpt-5.4-high"));

            Assert.AreEqual(1, groups.Count);
            Assert.IsFalse(groups[0].IsSubmenu);
            Assert.AreEqual(3, groups[0].Models.Count);
        }

        [TestMethod]
        public void Group_ReturnsNothingForAnEmptyList()
        {
            Assert.AreEqual(0, ModelCatalogGrouping.Group(Models()).Count);
            Assert.AreEqual(0, ModelCatalogGrouping.Group(null).Count);
        }

        [TestMethod]
        public void Group_SplitsALongListByFamilyKeepingTheClisOrder()
        {
            var ids = new List<string>();
            for (int i = 0; i < 14; i++) ids.Add("claude-opus-4-" + i);
            for (int i = 0; i < 14; i++) ids.Add("gpt-5.6-" + i);

            List<ModelGroup> groups = ModelCatalogGrouping.Group(Models(ids.ToArray()));

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("claude-opus", groups[0].Name);
            Assert.AreEqual("gpt-5.6", groups[1].Name);
            Assert.AreEqual(14, groups[0].Models.Count);
            Assert.AreEqual("claude-opus-4-0", groups[0].Models[0].Id);
        }

        [TestMethod]
        public void Group_ShowsAFamilyOfOneAsAPlainEntry()
        {
            var ids = new List<string> { "auto" };
            for (int i = 0; i < 26; i++) ids.Add("claude-opus-4-" + i);

            List<ModelGroup> groups = ModelCatalogGrouping.Group(Models(ids.ToArray()));

            Assert.AreEqual(2, groups.Count);
            Assert.IsFalse(groups[0].IsSubmenu);
            Assert.AreEqual("auto", groups[0].Models[0].Id);
            Assert.AreEqual("claude-opus", groups[1].Name);
        }

        [TestMethod]
        public void GetGroupKey_TakesTheProviderHalfOfAProviderSlashModelId()
        {
            Assert.AreEqual("anthropic", ModelCatalogGrouping.GetGroupKey("anthropic/claude-opus-5"));
            Assert.AreEqual("opencode", ModelCatalogGrouping.GetGroupKey("opencode/big-pickle"));
        }

        [TestMethod]
        public void GetGroupKey_TakesTwoSegmentsOfADashedId()
        {
            Assert.AreEqual("claude-opus", ModelCatalogGrouping.GetGroupKey("claude-opus-4-8-high"));
            Assert.AreEqual("gemini-3.6", ModelCatalogGrouping.GetGroupKey("gemini-3.6-flash-high"));
            Assert.AreEqual("gpt-5", ModelCatalogGrouping.GetGroupKey("gpt-5"));
            Assert.AreEqual("auto", ModelCatalogGrouping.GetGroupKey("auto"));
            Assert.AreEqual(string.Empty, ModelCatalogGrouping.GetGroupKey(null));
        }

        #endregion

        #region Reasonix providers

        /// <summary>Trimmed from a real `reasonix doctor --json` run (v1.18.0).</summary>
        private const string ReasonixDoctorJson = @"{
  ""version"": ""v1.18.0"",
  ""config"": { ""default_model"": ""deepseek-flash"" },
  ""providers"": [
    { ""name"": ""deepseek-flash"", ""models"": [""deepseek-v4-flash""], ""key_present"": true, ""is_default"": true },
    { ""name"": ""deepseek-pro"", ""models"": [""deepseek-v4-pro""], ""key_present"": false, ""is_default"": false }
  ],
  ""warnings"": []
}";

        [TestMethod]
        public void ReasonixProviders_AreListedByProviderNameNotModelId()
        {
            List<ModelOption> models = ModelCatalogParsers.ParseReasonixProviders(ReasonixDoctorJson);

            // The ids must be the provider names: those are what --model accepts. Listing
            // "deepseek-v4-flash" instead is exactly the mistake that broke native Reasonix.
            Assert.AreEqual(2, models.Count);
            Assert.AreEqual("deepseek-flash", models[0].Id);
            Assert.AreEqual("deepseek-pro", models[1].Id);
        }

        [TestMethod]
        public void ReasonixProviders_ShowTheServedModelInTheCaption()
        {
            List<ModelOption> models = ModelCatalogParsers.ParseReasonixProviders(ReasonixDoctorJson);

            Assert.AreEqual("deepseek-flash — deepseek-v4-flash", models[0].DisplayName);
        }

        [TestMethod]
        public void ReasonixProviders_KeepAProviderWhoseApiKeyIsMissing()
        {
            // key_present is an environment variable away from being true; hiding the entry would
            // leave the user no way to select it once the key is set.
            List<ModelOption> models = ModelCatalogParsers.ParseReasonixProviders(ReasonixDoctorJson);

            Assert.IsTrue(models.Exists(m => m.Id == "deepseek-pro"));
        }

        [TestMethod]
        public void ReasonixProviders_SkipAnyBannerPrintedBeforeTheJson()
        {
            List<ModelOption> models = ModelCatalogParsers.ParseReasonixProviders(
                "A new version of reasonix is available!" + Environment.NewLine + ReasonixDoctorJson);

            Assert.AreEqual(2, models.Count);
        }

        [TestMethod]
        public void ReasonixProviders_ReturnNothingForUnusableOutput()
        {
            // An empty list keeps the previous catalog (FetchProviderModelsAsync) and leaves the
            // launch flag empty, which starts Reasonix on its own configured default.
            Assert.AreEqual(0, ModelCatalogParsers.ParseReasonixProviders(null).Count);
            Assert.AreEqual(0, ModelCatalogParsers.ParseReasonixProviders("command not found").Count);
            Assert.AreEqual(0, ModelCatalogParsers.ParseReasonixProviders("{\"version\":\"v1.18.0\"}").Count);
        }

        #endregion

        #region ACP launch flag

        [TestMethod]
        public void AcpArguments_CarryTheModelForAnAgentThatPublishesNoPicker()
        {
            var options = new AcpSessionOptions
            {
                ExecutablePath = @"C:\npm\reasonix.cmd",
                ModelLaunchArgument = AcpCommandBuilder.BuildReasonixModelArgument("deepseek-v4-pro")
            };

            Assert.AreEqual("/c \"C:\\npm\\reasonix.cmd acp -model deepseek-v4-pro\"",
                AcpCommandBuilder.GetArguments(options));
        }

        [TestMethod]
        public void ReasonixModelArgument_SpellsTheFlagOutInFull()
        {
            // "reasonix acp" parses flags with Go's flag package: no prefix matching, so "-m" exits
            // with code 2 before the ACP server starts. Shipping that abbreviation broke every native
            // Reasonix launch that had a model picked, and the failure looked like a dead pipe.
            Assert.AreEqual("-model deepseek-chat", AcpCommandBuilder.BuildReasonixModelArgument("deepseek-chat"));
        }

        [TestMethod]
        public void ReasonixModelArgument_IsEmptyWhenNoModelIsPicked()
        {
            // An empty flag is what leaves the CLI on its own config default; "-model" alone would
            // swallow the next token as its value.
            Assert.AreEqual(string.Empty, AcpCommandBuilder.BuildReasonixModelArgument(null));
            Assert.AreEqual(string.Empty, AcpCommandBuilder.BuildReasonixModelArgument("   "));
        }

        [TestMethod]
        public void AcpArguments_AreUnchangedWithoutAModel()
        {
            var options = new AcpSessionOptions { ExecutablePath = "opencode.exe" };

            Assert.AreEqual("acp", AcpCommandBuilder.GetArguments(options));
        }

        [TestMethod]
        public void AcpArguments_CarryTheModelInsideWsl()
        {
            var options = new AcpSessionOptions
            {
                ExecutablePath = "devin",
                UseWsl = true,
                WslWorkingDirectory = "/mnt/c/work",
                ModelLaunchArgument = "-m swe-1.6"
            };

            Assert.AreEqual("bash -ic \"cd '/mnt/c/work' && devin acp -m swe-1.6\"",
                AcpCommandBuilder.GetArguments(options));
        }

        #endregion
    }
}
