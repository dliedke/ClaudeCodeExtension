/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the Settings → Backup save/load file format and the custom command clone naming.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SettingsBackupTests
    {
        [TestMethod]
        public void Export_RoundTripsCustomCommandsAndPerSolutionAgentFinish()
        {
            var settings = new ClaudeCodeSettings { SelectedProvider = AiProvider.CodexNative };
            settings.CustomCommands.Add(new CustomCommand { Name = "Review", Command = "/codex-review" });
            settings.ProjectAgentFinish = new Dictionary<string, AgentFinishConfig>
            {
                ["MySolution"] = new AgentFinishConfig { Enabled = true, Action = AgentFinishActionType.BuildSolution }
            };

            var export = ClaudeCodeControl.BuildConfigurationExport(
                JObject.FromObject(settings), "211.0", new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
            string json = ClaudeCodeControl.SerializeJsonIndented(export);

            var imported = ClaudeCodeControl.ParseConfigurationImport(json, out string error);

            Assert.IsNull(error);
            var restored = imported.ToObject<ClaudeCodeSettings>();
            Assert.AreEqual(AiProvider.CodexNative, restored.SelectedProvider);
            Assert.AreEqual("/codex-review", restored.CustomCommands.Find(c => c.Name == "Review")?.Command);
            Assert.IsTrue(restored.ProjectAgentFinish["MySolution"].Enabled);
            Assert.AreEqual(AgentFinishActionType.BuildSolution, restored.ProjectAgentFinish["MySolution"].Action);
        }

        [TestMethod]
        public void Import_AcceptsBareSettingsFile()
        {
            string json = ClaudeCodeControl.SerializeJsonIndented(new ClaudeCodeSettings { SendWithEnter = false });

            var imported = ClaudeCodeControl.ParseConfigurationImport(json, out string error);

            Assert.IsNull(error);
            Assert.AreEqual(false, (bool)imported["SendWithEnter"]);
        }

        [TestMethod]
        public void Import_KeepsUnknownPropertiesAndDateStringsVerbatim()
        {
            const string json = "{ \"SendWithEnter\": true, \"FutureSetting\": \"2026-09-30T12:00:00Z\" }";

            var imported = ClaudeCodeControl.ParseConfigurationImport(json, out string error);

            Assert.IsNull(error);
            Assert.AreEqual(JTokenType.String, imported["FutureSetting"].Type);
            Assert.AreEqual("2026-09-30T12:00:00Z", (string)imported["FutureSetting"]);
        }

        [TestMethod]
        public void Import_RejectsUnrelatedJsonAndGarbage()
        {
            Assert.IsNull(ClaudeCodeControl.ParseConfigurationImport("{ \"name\": \"package\" }", out string e1));
            Assert.IsNotNull(e1);
            Assert.IsNull(ClaudeCodeControl.ParseConfigurationImport("not json", out string e2));
            Assert.IsNotNull(e2);
            Assert.IsNull(ClaudeCodeControl.ParseConfigurationImport("[1,2]", out string e3));
            Assert.IsNotNull(e3);
            Assert.IsNull(ClaudeCodeControl.ParseConfigurationImport("", out string e4));
            Assert.IsNotNull(e4);
        }

        [TestMethod]
        public void Import_RejectsEnvelopeWithoutSettings()
        {
            string json = "{ \"Format\": \"" + ClaudeCodeControl.ConfigurationExportFormat + "\" }";

            Assert.IsNull(ClaudeCodeControl.ParseConfigurationImport(json, out string error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void BuildCloneName_PicksFirstFreeCopySuffix()
        {
            Assert.AreEqual("Commit (copy)",
                ClaudeCodeControl.BuildCloneName("Commit", new[] { "Commit" }));
            Assert.AreEqual("Commit (copy 2)",
                ClaudeCodeControl.BuildCloneName("Commit", new[] { "Commit", "Commit (copy)" }));
            Assert.AreEqual("Commit (copy 3)",
                ClaudeCodeControl.BuildCloneName("Commit", new[] { "commit (COPY)", "Commit (copy 2)", null }));
        }
    }
}
