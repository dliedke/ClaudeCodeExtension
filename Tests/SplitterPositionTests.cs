/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the saved splitter position against persisting (or restoring) a collapsed prompt section
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Issue #151 round 13. The reporter kept seeing a panel with nothing but the terminal in it — no
    /// prompt box, no controls row, and therefore no ⚙ to fix anything with — and three rounds of
    /// <c>Visibility</c> fixes changed nothing, because nothing was collapsed: their settings file held
    /// <c>SplitterPosition = 6.0</c>, so the whole prompt section was restored six pixels tall.
    /// <para>
    /// That value was measured and saved while the prompt section was collapsed by the native-mode
    /// auto-hide. The save guard only skipped the <c>HidePromptPanel</c> setting, which was off — the
    /// auto-hide collapses the same slot with that setting off, so the collapsed height went straight to
    /// disk and then outlived the session that produced it.
    /// </para>
    /// </summary>
    [TestClass]
    public class SplitterPositionTests
    {
        private static string SettingsSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Settings.cs");
        private static string DetachSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Detach.cs");
        private static string NativeModeSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs");

        [TestMethod]
        public void ShouldPersistSplitterPosition_CollapsedSectionHeightIsNeverSaved()
        {
            // The exact value from the reporter's claudecode-settings.json.
            Assert.IsFalse(ClaudeCodeControl.ShouldPersistSplitterPosition(6.0, false, false),
                "A six-pixel prompt section is a collapsed measurement, not a split anyone dragged.");
            Assert.IsFalse(ClaudeCodeControl.ShouldPersistSplitterPosition(0.0, false, false));
            Assert.IsFalse(ClaudeCodeControl.ShouldPersistSplitterPosition(79.9, false, false),
                "The threshold is the prompt box row's own MinHeight, so anything under it is unusable.");
        }

        [TestMethod]
        public void ShouldPersistSplitterPosition_HiddenPromptBoxOrDetachedTerminalMeasuresSomethingElse()
        {
            // Both states collapse or re-slot the grid, so whatever is measured is not the user's split.
            Assert.IsFalse(ClaudeCodeControl.ShouldPersistSplitterPosition(300.0, true, false),
                "Detached: the prompt slot spans the whole control, which would overwrite the real split.");
            Assert.IsFalse(ClaudeCodeControl.ShouldPersistSplitterPosition(6.0, false, true),
                "Prompt box hidden (setting or native-mode auto-hide): the section is Auto-collapsed.");
        }

        [TestMethod]
        public void ShouldPersistSplitterPosition_ARealSplitIsSaved()
        {
            Assert.IsTrue(ClaudeCodeControl.ShouldPersistSplitterPosition(80.0, false, false));
            Assert.IsTrue(ClaudeCodeControl.ShouldPersistSplitterPosition(236.0, false, false));
        }

        [TestMethod]
        public void ResolveRestoredSplitterPosition_HealsAPositionSavedByAnOlderBuild()
        {
            // Self-healing matters as much as the save guard: the reporter already has 6.0 on disk, and
            // an upgrade that only stopped writing bad values would still open an unusable panel.
            Assert.AreEqual(ClaudeCodeSettings.DefaultSplitterPosition,
                ClaudeCodeControl.ResolveRestoredSplitterPosition(6.0), 0.001);
            Assert.AreEqual(ClaudeCodeSettings.DefaultSplitterPosition,
                ClaudeCodeControl.ResolveRestoredSplitterPosition(79.9), 0.001);
        }

        [TestMethod]
        public void ResolveRestoredSplitterPosition_KeepsARealPositionAndTreatsZeroAsUnset()
        {
            Assert.AreEqual(320.0, ClaudeCodeControl.ResolveRestoredSplitterPosition(320.0), 0.001);
            Assert.AreEqual(80.0, ClaudeCodeControl.ResolveRestoredSplitterPosition(80.0), 0.001);

            // 0 keeps its existing meaning — callers fall back to a proportional star split rather than
            // forcing a pixel height on someone who never set one.
            Assert.AreEqual(0.0, ClaudeCodeControl.ResolveRestoredSplitterPosition(0.0), 0.001);
            Assert.AreEqual(0.0, ClaudeCodeControl.ResolveRestoredSplitterPosition(-5.0), 0.001);
        }

        [TestMethod]
        public void MinUsableSplitterPosition_MatchesThePromptBoxRowMinHeight()
        {
            Assert.AreEqual(80.0, ClaudeCodeControl.MinUsableSplitterPosition, 0.001,
                "ApplyPromptPanelHiddenState restores the prompt box row with MinHeight = 80; the two " +
                "have to agree or a 'usable' position can still be too short to show the box.");
        }

        /// <summary>
        /// The bug was a guard testing the setting instead of the state it implies. Both save paths must
        /// go through the shared predicate so neither can drift back.
        /// </summary>
        [TestMethod]
        public void EverySavePath_GoesThroughTheSharedPredicate()
        {
            string saveSettings = ExtractMethodBody(SettingsSource, "private void SaveSettings(");
            StringAssert.Contains(saveSettings, "ShouldPersistSplitterPosition(splitterPosition.Value, _isTerminalDetached, PromptBoxIsHidden)",
                "SaveSettings must not test _settings.HidePromptPanel directly — that is the round 13 bug.");

            string afterLayout = ExtractMethodBody(SettingsSource, "private void SaveSplitterPositionAfterLayout()");
            StringAssert.Contains(afterLayout, "ShouldPersistSplitterPosition(",
                "The drag-completed save had no guard at all beyond '> 0'.");

            string detachBody = DetachSource;
            StringAssert.Contains(detachBody, "ShouldPersistSplitterPosition(currentPos.Value",
                "Detaching the terminal saves the pre-detach split, so it needs the same predicate.");
        }

        /// <summary>
        /// And every restore path must launder the saved value, or the copy already on the reporter's
        /// disk keeps reproducing the blank panel.
        /// </summary>
        [TestMethod]
        public void EveryRestorePath_LaundersTheSavedValue()
        {
            StringAssert.Contains(ExtractMethodBody(SettingsSource, "private void LoadSettings()"),
                "ResolveRestoredSplitterPosition(_settings.SplitterPosition)",
                "Startup restore must heal a collapsed height instead of applying it.");

            StringAssert.Contains(ExtractMethodBody(SettingsSource, "private void ApplyPromptPanelHiddenState()"),
                "ResolveRestoredSplitterPosition(_settings.SplitterPosition)",
                "Un-hiding the prompt box restores the saved split, which is the moment the user is " +
                "trying to get the panel back — applying 6px there is the worst possible time.");

            StringAssert.Contains(DetachSource, "ResolveRestoredSplitterPosition(_settings.SplitterPosition)",
                "Re-attaching the terminal restores the pre-detach split through the same laundering.");
        }

        /// <summary>
        /// Round 12 forced ⚙ and its two rows Visible after a failed native start. Visible is not the
        /// same as reachable when the section holding them is six pixels tall, which is why the reporter
        /// still saw nothing.
        /// </summary>
        [TestMethod]
        public void TerminalHandOff_RepairsTheSectionSizeAndNotJustVisibility()
        {
            string body = ExtractMethodBody(NativeModeSource, "private void EnsureSettingsButtonReachable()");

            StringAssert.Contains(body, "MenuDropdownButton.Visibility = Visibility.Visible");
            StringAssert.Contains(body, "ApplyPromptPanelHiddenState();",
                "Re-running the sizing decision is what turns a Visible button into a reachable one.");

            int visibility = body.IndexOf("MenuDropdownButton.Visibility", StringComparison.Ordinal);
            int sizing = body.IndexOf("ApplyPromptPanelHiddenState();", StringComparison.Ordinal);
            Assert.IsTrue(visibility >= 0 && sizing > visibility,
                "Size the section after un-collapsing the rows, so the sizing pass measures what is showing.");
        }

        /// <summary>
        /// <c>SetSplitterPosition</c> refuses to resize a collapsed slot (issue #101). That refusal has to
        /// key off the effective hidden state too, or the native-mode auto-hide leaves it resizing a slot
        /// that <c>ApplyPromptPanelHiddenState</c> deliberately collapsed.
        /// </summary>
        [TestMethod]
        public void SetSplitterPosition_SkipsTheCollapsedSlotUsingTheEffectiveHiddenState()
        {
            string body = ExtractMethodBody(SettingsSource, "private void SetSplitterPosition(double position)");

            StringAssert.Contains(body, "if (PromptBoxIsHidden)");
            Assert.IsFalse(body.Contains("_settings?.HidePromptPanel == true"),
                "The setting on its own misses the native-mode auto-hide, which collapses the same slot.");
        }

        /// <summary>
        /// Returns the text of a method's body, located by its signature and closed by brace matching.
        /// </summary>
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
