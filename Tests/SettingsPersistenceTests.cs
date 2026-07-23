/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers settings serialization and the cross-instance merge of per-window (volatile)
 *          fields — the code shape behind issue #112.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SettingsPersistenceTests
    {
        /// <summary>
        /// Issue #112: the old code serialized through JToken.ToString(Formatting), a member whose
        /// presence depends on the Newtonsoft.Json build actually loaded. Inside Visual Studio that
        /// is VS's own copy, not the one shipped with the extension, and the call threw "Method not
        /// found" while merely saving settings. Note this test would have passed even with the
        /// broken code — outside VS the extension's own Newtonsoft is loaded. The real guard is the
        /// version pin, plus manual F5 testing inside Visual Studio.
        /// </summary>
        [TestMethod]
        public void SerializeJsonIndented_ProducesIndentedJsonForSettingsObjects()
        {
            var settings = new ClaudeCodeSettings { SelectedProvider = AiProvider.CodexNative };

            string json = ClaudeCodeControl.SerializeJsonIndented(settings);

            StringAssert.Contains(json, "\n");
            StringAssert.Contains(json, "  \"SelectedProvider\":");
            Assert.AreEqual(
                (int)AiProvider.CodexNative,
                (int)JObject.Parse(json)["SelectedProvider"]);
        }

        [TestMethod]
        public void SerializeJsonIndented_AlsoHandlesJsonTokensDirectly()
        {
            var token = JObject.FromObject(new ClaudeCodeSettings());

            string json = ClaudeCodeControl.SerializeJsonIndented(token);

            StringAssert.Contains(json, "  \"SelectedClaudeModel\":");
        }

        /// <summary>
        /// Two Visual Studio windows share one settings file. When the second one saves, the
        /// provider/model/effort the FIRST one picked must survive — otherwise the windows fight
        /// over the selection every time either of them writes.
        /// </summary>
        [TestMethod]
        public void PreserveVolatileFieldsFromDisk_KeepsTheOtherWindowsProviderAndModel()
        {
            var toSave = JObject.FromObject(new ClaudeCodeSettings
            {
                SelectedProvider = AiProvider.OpenCode,
                SelectedClaudeModel = ClaudeModel.Opus,
                SelectedDevinModel = "SWE-9.9",
                SelectedEffortLevel = EffortLevel.Low
            });

            string diskJson = ClaudeCodeControl.SerializeJsonIndented(new ClaudeCodeSettings
            {
                SelectedProvider = AiProvider.ClaudeCode,
                SelectedClaudeModel = ClaudeModel.Sonnet,
                SelectedDevinModel = "SWE-1.6",
                SelectedEffortLevel = EffortLevel.High
            });

            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, diskJson);

            Assert.AreEqual((int)AiProvider.ClaudeCode, (int)toSave["SelectedProvider"]);
            Assert.AreEqual((int)ClaudeModel.Sonnet, (int)toSave["SelectedClaudeModel"]);
            Assert.AreEqual("SWE-1.6", (string)toSave["SelectedDevinModel"]);
            Assert.AreEqual((int)EffortLevel.High, (int)toSave["SelectedEffortLevel"]);
        }

        /// <summary>
        /// Only the per-window fields come back from disk. Everything else the user changed in this
        /// window — a checkbox in Settings, say — has to reach the file.
        /// </summary>
        [TestMethod]
        public void PreserveVolatileFieldsFromDisk_LeavesNonVolatileSettingsAlone()
        {
            var toSave = JObject.FromObject(new ClaudeCodeSettings());
            toSave["SomeDurableSetting"] = "new value";

            string diskJson = "{ \"SelectedProvider\": 0, \"SomeDurableSetting\": \"old value\" }";

            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, diskJson);

            Assert.AreEqual("new value", (string)toSave["SomeDurableSetting"]);
        }

        /// <summary>
        /// A corrupt or truncated settings file must not abort the save — losing every setting is
        /// far worse than losing the cross-instance merge for one write.
        /// </summary>
        [TestMethod]
        public void PreserveVolatileFieldsFromDisk_IgnoresCorruptOrMissingDiskContent()
        {
            var toSave = JObject.FromObject(new ClaudeCodeSettings { SelectedProvider = AiProvider.Pi });

            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, "{ this is not json");
            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, string.Empty);
            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, null);

            Assert.AreEqual((int)AiProvider.Pi, (int)toSave["SelectedProvider"]);
        }

        [TestMethod]
        public void PreserveVolatileFieldsFromDisk_KeepsThisWindowsValueWhenTheDiskFileLacksTheField()
        {
            var toSave = JObject.FromObject(new ClaudeCodeSettings { SelectedProvider = AiProvider.Reasonix });

            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, "{ \"SelectedEffortLevel\": 1 }");

            Assert.AreEqual((int)AiProvider.Reasonix, (int)toSave["SelectedProvider"]);
            Assert.AreEqual(1, (int)toSave["SelectedEffortLevel"]);
        }
    }
}
