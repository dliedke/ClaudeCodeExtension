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
        /// v280.0: the chat composer's always-present 🕘 became the Session History toolbar button.
        /// A settings file from before that gets it promoted once, so nobody loses the 🕘.
        /// </summary>
        [TestMethod]
        public void SessionHistoryIsPromotedOnceForSettingsFromBeforeTheMerge()
        {
            var settings = new ClaudeCodeSettings
            {
                VisibleToolbarButtons = new System.Collections.Generic.List<ToolbarButton> { ToolbarButton.RestartAgent },
                SessionHistoryButtonMigrated = false
            };

            ClaudeCodeControl.MigrateSessionHistoryButton(settings);

            CollectionAssert.AreEqual(
                new[] { ToolbarButton.RestartAgent, ToolbarButton.SessionHistory },
                settings.VisibleToolbarButtons);
            Assert.IsTrue(settings.SessionHistoryButtonMigrated);
        }

        /// <summary>Once migrated, un-promoting Session History in Settings → Toolbar is the user's choice and sticks.</summary>
        [TestMethod]
        public void SessionHistoryMigrationDoesNotOverrideTheUsersLaterChoice()
        {
            var settings = new ClaudeCodeSettings
            {
                VisibleToolbarButtons = new System.Collections.Generic.List<ToolbarButton> { ToolbarButton.RestartAgent },
                SessionHistoryButtonMigrated = true
            };

            ClaudeCodeControl.MigrateSessionHistoryButton(settings);

            CollectionAssert.AreEqual(new[] { ToolbarButton.RestartAgent }, settings.VisibleToolbarButtons);
        }

        /// <summary>☰ ⚡ ⚙ only have a position in the toolbar order; they are never "promoted" features.</summary>
        [TestMethod]
        public void ToolbarControlsAreNeverKeptAsPromotedFeatures()
        {
            var settings = new ClaudeCodeSettings
            {
                VisibleToolbarButtons = new System.Collections.Generic.List<ToolbarButton>
                {
                    ToolbarButton.ToolsMenu, ToolbarButton.ViewChanges, ToolbarButton.Settings, ToolbarButton.CustomCommands
                },
                SessionHistoryButtonMigrated = true
            };

            ClaudeCodeControl.MigrateSessionHistoryButton(settings);

            CollectionAssert.AreEqual(new[] { ToolbarButton.ViewChanges }, settings.VisibleToolbarButtons);
            Assert.IsTrue(new ClaudeCodeSettings().ShowToolsMenuButton, "☰ is shown by default.");
        }

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
                SelectedEffortLevel = EffortLevel.Low,
                SelectedCodexReasoningLevel = CodexReasoningLevel.Low
            });

            string diskJson = ClaudeCodeControl.SerializeJsonIndented(new ClaudeCodeSettings
            {
                SelectedProvider = AiProvider.ClaudeCode,
                SelectedClaudeModel = ClaudeModel.Sonnet,
                SelectedDevinModel = "SWE-1.6",
                SelectedEffortLevel = EffortLevel.High,
                SelectedCodexReasoningLevel = CodexReasoningLevel.XHigh
            });

            ClaudeCodeControl.PreserveVolatileFieldsFromDisk(toSave, diskJson);

            Assert.AreEqual((int)AiProvider.ClaudeCode, (int)toSave["SelectedProvider"]);
            Assert.AreEqual((int)ClaudeModel.Sonnet, (int)toSave["SelectedClaudeModel"]);
            Assert.AreEqual("SWE-1.6", (string)toSave["SelectedDevinModel"]);
            Assert.AreEqual((int)EffortLevel.High, (int)toSave["SelectedEffortLevel"]);
            Assert.AreEqual(
                (int)CodexReasoningLevel.XHigh,
                (int)toSave["SelectedCodexReasoningLevel"]);
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

        /// <summary>
        /// Window A saves "all chats in one window" with Settings OK; window B, which still holds the
        /// old value in memory, then saves because its splitter moved. B must write its splitter and
        /// leave A's checkbox alone.
        /// </summary>
        [TestMethod]
        public void MergeChangedSettings_KeepsWhatAnotherWindowSavedAndWritesOnlyThisWindowsChanges()
        {
            var snapshot = JObject.FromObject(new ClaudeCodeSettings { ChatTabsInOneWindow = false, SplitterPosition = 200 });
            var current = JObject.FromObject(new ClaudeCodeSettings { ChatTabsInOneWindow = false, SplitterPosition = 350 });
            string diskJson = ClaudeCodeControl.SerializeJsonIndented(
                new ClaudeCodeSettings { ChatTabsInOneWindow = true, SplitterPosition = 200 });

            JObject merged = ClaudeCodeControl.MergeChangedSettings(current, snapshot, diskJson);

            Assert.IsTrue((bool)merged["ChatTabsInOneWindow"], "Another window's saved setting was undone.");
            Assert.AreEqual(350.0, (double)merged["SplitterPosition"]);
        }

        /// <summary>
        /// Dictionaries (session titles, per-solution settings) merge key by key: two windows naming
        /// different sessions both keep their titles, and a key this window removed goes away.
        /// </summary>
        [TestMethod]
        public void MergeChangedSettings_MergesDictionariesKeyByKey()
        {
            var snapshot = JObject.Parse("{ \"SessionCustomTitles\": { \"a\": \"A\", \"gone\": \"G\" } }");
            var current = JObject.Parse("{ \"SessionCustomTitles\": { \"a\": \"A2\", \"mine\": \"M\" } }");
            string diskJson = "{ \"SessionCustomTitles\": { \"a\": \"A\", \"gone\": \"G\", \"theirs\": \"T\" } }";

            var titles = (JObject)ClaudeCodeControl.MergeChangedSettings(current, snapshot, diskJson)["SessionCustomTitles"];

            Assert.AreEqual("A2", (string)titles["a"]);
            Assert.AreEqual("M", (string)titles["mine"]);
            Assert.AreEqual("T", (string)titles["theirs"]);
            Assert.IsNull(titles["gone"]);
        }

        /// <summary>
        /// A field the file lacks (written by an older version) is filled in from this window, and
        /// a field only the file knows (a newer version's) survives.
        /// </summary>
        [TestMethod]
        public void MergeChangedSettings_FillsMissingFieldsAndKeepsUnknownOnes()
        {
            var snapshot = JObject.Parse("{ \"A\": 1, \"B\": 2 }");
            var current = JObject.Parse("{ \"A\": 1, \"B\": 2 }");

            JObject merged = ClaudeCodeControl.MergeChangedSettings(current, snapshot, "{ \"A\": 5, \"Future\": true }");

            Assert.AreEqual(5, (int)merged["A"]);
            Assert.AreEqual(2, (int)merged["B"]);
            Assert.IsTrue((bool)merged["Future"]);
        }

        /// <summary>
        /// No snapshot (the load failed) or an unreadable file: write everything this window has.
        /// </summary>
        [TestMethod]
        public void MergeChangedSettings_WritesEverythingWithoutSnapshotOrReadableFile()
        {
            var current = JObject.Parse("{ \"A\": 1 }");

            Assert.AreEqual(1, (int)ClaudeCodeControl.MergeChangedSettings(current, null, "{ \"A\": 9 }")["A"]);
            Assert.AreEqual(1, (int)ClaudeCodeControl.MergeChangedSettings(current, JObject.Parse("{ \"A\": 9 }"), "{ not json")["A"]);
            Assert.AreEqual(1, (int)ClaudeCodeControl.MergeChangedSettings(current, JObject.Parse("{ \"A\": 9 }"), null)["A"]);
        }

        /// <summary>
        /// The terminal is launched as UTF-8, so keeping it there is the default; a user who has an
        /// agent workflow that deliberately switches the console code page can turn it off, and that
        /// choice has to survive a save/load round trip.
        /// </summary>
        [TestMethod]
        public void KeepTerminalCodePage_DefaultsToOnAndRoundTrips()
        {
            Assert.IsTrue(new ClaudeCodeSettings().KeepTerminalCodePage);

            var settings = new ClaudeCodeSettings { KeepTerminalCodePage = false };

            string json = ClaudeCodeControl.SerializeJsonIndented(settings);

            Assert.IsFalse((bool)JObject.Parse(json)["KeepTerminalCodePage"]);
        }

        /// <summary>
        /// Issue #151/v170.0: a settings file saved by an older build has no
        /// <see cref="ClaudeCodeSettings.AutoHidePromptInNativeMode"/> property at all, and the
        /// feature it gates (auto-collapsing the panel's prompt box once native mode's chat has
        /// left for its own tab) is meant to be on for everyone by default, not just new installs.
        /// </summary>
        [TestMethod]
        public void AutoHidePromptInNativeMode_DefaultsToOnAndRoundTrips()
        {
            Assert.IsTrue(new ClaudeCodeSettings().AutoHidePromptInNativeMode);

            var deserializedFromOlderFile =
                Newtonsoft.Json.JsonConvert.DeserializeObject<ClaudeCodeSettings>("{ \"SelectedProvider\": 0 }");
            Assert.IsTrue(deserializedFromOlderFile.AutoHidePromptInNativeMode,
                "A settings file predating this setting must still behave as if it were turned on.");

            var settings = new ClaudeCodeSettings { AutoHidePromptInNativeMode = false };

            string json = ClaudeCodeControl.SerializeJsonIndented(settings);

            Assert.IsFalse((bool)JObject.Parse(json)["AutoHidePromptInNativeMode"]);
        }

        /// <summary>
        /// Hiding the panel once native mode's chat is in its own tab is on for everyone, including
        /// users whose settings file predates the option.
        /// </summary>
        [TestMethod]
        public void AutoHidePanelInNativeMode_DefaultsToOnAndRoundTrips()
        {
            Assert.IsTrue(new ClaudeCodeSettings().AutoHidePanelInNativeMode);

            var deserializedFromOlderFile =
                Newtonsoft.Json.JsonConvert.DeserializeObject<ClaudeCodeSettings>("{ \"SelectedProvider\": 0 }");
            Assert.IsTrue(deserializedFromOlderFile.AutoHidePanelInNativeMode,
                "A settings file predating this setting must still behave as if it were turned on.");

            var settings = new ClaudeCodeSettings { AutoHidePanelInNativeMode = false };

            string json = ClaudeCodeControl.SerializeJsonIndented(settings);

            Assert.IsFalse((bool)JObject.Parse(json)["AutoHidePanelInNativeMode"]);
        }

        /// <summary>
        /// The toolbar strip stays left-aligned by default - that is the v177 layout the XAML and
        /// RightButtonsScroller_IsLeftAligned_... pin down. Turning it on is opt-in for users who
        /// want the pre-v177 look back, and an older settings file must not silently move their
        /// toolbar.
        /// </summary>
        [TestMethod]
        public void ToolbarButtonsRightAligned_IsOffByDefaultAndRoundTrips()
        {
            Assert.IsFalse(new ClaudeCodeSettings().ToolbarButtonsRightAligned);

            var fromOlderFile =
                Newtonsoft.Json.JsonConvert.DeserializeObject<ClaudeCodeSettings>("{ \"SelectedProvider\": 0 }");
            Assert.IsFalse(fromOlderFile.ToolbarButtonsRightAligned,
                "A settings file predating this setting must keep the current left-aligned toolbar.");

            var settings = new ClaudeCodeSettings { ToolbarButtonsRightAligned = true };

            string json = ClaudeCodeControl.SerializeJsonIndented(settings);

            Assert.IsTrue((bool)JObject.Parse(json)["ToolbarButtonsRightAligned"]);
        }
    }
}
