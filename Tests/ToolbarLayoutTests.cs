/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the configurable panel toolbar — feature order, button vs ☰ Tools placement, the
 *          Settings → Toolbar tab, reordering — and guards that every control added to the toolbar
 *          is wired into all the places that make it configurable.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ToolbarLayoutTests
    {
        private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace WpfNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        private static ToolbarButton[] AllToolbarButtons =>
            Enum.GetValues(typeof(ToolbarButton)).Cast<ToolbarButton>().ToArray();

        private static ToolbarButton[] Features =>
            AllToolbarButtons.Where(ClaudeCodeControl.IsToolbarFeature).ToArray();

        #region Every toolbar item is configurable

        /// <summary>
        /// The Settings → Toolbar tab builds one row per entry of the effective order, which is the
        /// default order completed with the saved one — so a <see cref="ToolbarButton"/> value missing
        /// from the default order never gets a row and cannot be moved or switched off.
        /// </summary>
        [TestMethod]
        public void DefaultToolbarOrder_ListsEveryToolbarButtonExactlyOnce()
        {
            CollectionAssert.AreEquivalent(AllToolbarButtons, ClaudeCodeControl.DefaultToolbarButtonOrder,
                "Every ToolbarButton must appear exactly once in DefaultToolbarButtonOrder, or the Toolbar settings tab has no row for it.");
        }

        [TestMethod]
        public void SettingsToolbarTab_BuildsOneTaggedRowPerEffectiveOrderEntry()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs"),
                "private (StackPanel RowsPanel, Dictionary<ToolbarButton, CheckBox> Checks) BuildToolbarButtonsTabContent(");

            StringAssert.Contains(body, "foreach (var id in GetEffectiveToolbarOrder())",
                "The Toolbar tab must offer a row for every entry of the effective order.");
            StringAssert.Contains(body, "Tag = id,",
                "Each row must carry its ToolbarButton, which is how the order is read back on OK.");
            StringAssert.Contains(body, "checks[id] = cb;",
                "Each row's checkbox must be collected, or its on/off state is lost on OK.");
        }

        [TestMethod]
        public void ToolbarFeatureMeta_GivesEveryToolbarButtonALabelAndTooltip()
        {
            foreach (ToolbarButton id in AllToolbarButtons)
            {
                var (label, tip) = ClaudeCodeControl.ToolbarFeatureMeta(id);
                Assert.AreNotEqual(id.ToString(), label, $"{id} has no label in the Toolbar settings tab.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(tip), $"{id} has no tooltip in the Toolbar settings tab.");
            }
        }

        /// <summary>
        /// The ask behind this class: anything added to the panel toolbar must be a <see cref="ToolbarButton"/>
        /// mapped to its control, so the Settings → Toolbar tab can move it (and, for features, switch it).
        /// </summary>
        [TestMethod]
        public void EveryPanelToolbarButton_IsAToolbarButtonMappedToItsControl()
        {
            var mapping = ParseCaseReturns(
                ExtractMethodBody(ProviderManagementSource, "private System.Windows.Controls.Button GetToolbarButtonControl(ToolbarButton b)"));

            CollectionAssert.AreEquivalent(AllToolbarButtons.Select(b => b.ToString()).ToList(), mapping.Keys.ToList(),
                "GetToolbarButtonControl must return a control for every ToolbarButton.");

            List<string> xamlButtons = ChildNames(FindByName(ControlXaml, "RightButtonsPanel"), "Button");
            Assert.IsTrue(xamlButtons.Count > 0, "RightButtonsPanel has no Button children; update this guard to the new layout.");

            // v291.0: 📎 and 🤖 joined the order, so there is no exception left — every button in the
            // strip is a ToolbarButton.
            var unmapped = xamlButtons.Where(n => !mapping.Values.Contains(n)).ToList();
            Assert.AreEqual(0, unmapped.Count,
                "These toolbar buttons are not in the ToolbarButton enum, so the Toolbar settings tab cannot move or hide them: " +
                string.Join(", ", unmapped) + ". Add a ToolbarButton value (plus DefaultToolbarButtonOrder, GetToolbarButtonControl, " +
                "ToolbarFeatureMeta).");

            var missingFromXaml = mapping.Values.Where(n => !xamlButtons.Contains(n)).ToList();
            Assert.AreEqual(0, missingFromXaml.Count,
                "GetToolbarButtonControl returns controls that are not in RightButtonsPanel, so reordering skips them: " +
                string.Join(", ", missingFromXaml));
        }

        [TestMethod]
        public void EveryToolsMenuEntry_IsAFeatureMappedToItsMenuItem()
        {
            var mapping = ParseCaseReturns(
                ExtractMethodBody(ProviderManagementSource, "private System.Windows.Controls.MenuItem GetToolbarMenuItemControl(ToolbarButton b)"));

            CollectionAssert.AreEquivalent(Features.Select(b => b.ToString()).ToList(), mapping.Keys.ToList(),
                "Every feature (and only the features) needs a ☰ Tools entry to fall back to when it is not a button.");

            List<string> xamlItems = ChildNames(FindByName(ControlXaml, "ToolsContextMenu"), "MenuItem");
            CollectionAssert.AreEquivalent(mapping.Values.ToList(), xamlItems,
                "The ☰ Tools menu must hold exactly the features' menu items; anything else there cannot be moved or hidden.");
        }

        [TestMethod]
        public void RefreshToolbarLayout_PlacesEveryFeatureAndCountsItsMenuEntry()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");
            var menuItems = ParseCaseReturns(
                ExtractMethodBody(ProviderManagementSource, "private System.Windows.Controls.MenuItem GetToolbarMenuItemControl(ToolbarButton b)"));

            int anyStart = body.IndexOf("bool anyInDropdown =", StringComparison.Ordinal);
            Assert.IsTrue(anyStart >= 0, "anyInDropdown not found; update this guard.");
            string anyInDropdown = body.Substring(anyStart, body.IndexOf(';', anyStart) - anyStart);

            foreach (ToolbarButton id in Features)
            {
                StringAssert.Contains(body, $"Apply(ToolbarButton.{id}, ",
                    $"{id} is never placed as a button or ☰ entry, so it stays in whatever state the XAML left it.");
                StringAssert.Contains(anyInDropdown, $"IsMenuItemVisible({menuItems[id.ToString()]})",
                    $"☰ ignores {id}'s entry when deciding whether it is empty, and can hide with {id} inside.");
            }
        }

        /// <summary>
        /// The chat composer mirrors the toolbar: ☰ ⚡ ⚙ are its own buttons slotted in by id, and every
        /// promoted feature (Restart excepted — the composer has its own ↻) needs a Modern-style icon.
        /// 📎 and 🤖 are panel-only (the composer has its own 📎 and its selectors replace 🤖), so
        /// RefreshToolbarLayout must leave them out of the mirror list instead.
        /// </summary>
        [TestMethod]
        public void ChatComposer_HasACounterpartForEveryToolbarButton()
        {
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");
            string ownButton = ExtractMethodBody(cs, "private Button OwnButton(string id)");
            string moniker = ExtractMethodBody(cs, "private static bool TryGetPromotedMoniker(string id, out ImageMoniker moniker)");

            foreach (ToolbarButton id in AllToolbarButtons.Where(b => !ClaudeCodeControl.IsToolbarFeature(b) && !ClaudeCodeControl.IsPanelOnlyToolbarControl(b)))
            {
                StringAssert.Contains(ownButton, $"case \"{id}\":", $"The composer has no own button for {id}.");
            }

            foreach (ToolbarButton id in AllToolbarButtons.Where(ClaudeCodeControl.IsPanelOnlyToolbarControl))
            {
                Assert.IsFalse(ownButton.Contains($"case \"{id}\":"), $"{id} is panel-only and must not get a composer button.");
            }

            string layout = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");
            StringAssert.Contains(layout, "if (IsPanelOnlyToolbarControl(id)) continue;",
                "Panel-only controls must be skipped before the mirror list is built, or the composer gets a blank button for them.");

            foreach (ToolbarButton id in Features.Where(b => b != ToolbarButton.RestartAgent))
            {
                StringAssert.Contains(moniker, $"case \"{id}\":", $"Promoted {id} has no icon in the Modern icon style.");
            }
        }

        #endregion

        #region Order

        [TestMethod]
        public void NormalizeToolbarOrder_WithoutASavedOrder_IsTheDefault()
        {
            CollectionAssert.AreEqual(ClaudeCodeControl.DefaultToolbarButtonOrder, ClaudeCodeControl.NormalizeToolbarOrder(null));
            CollectionAssert.AreEqual(ClaudeCodeControl.DefaultToolbarButtonOrder, ClaudeCodeControl.NormalizeToolbarOrder(new ToolbarButton[0]));
        }

        [TestMethod]
        public void NormalizeToolbarOrder_KeepsTheSavedRelativeOrder()
        {
            var saved = new[] { ToolbarButton.Settings, ToolbarButton.GitHubIssues, ToolbarButton.UpdateAgent };

            List<ToolbarButton> order = ClaudeCodeControl.NormalizeToolbarOrder(saved);

            CollectionAssert.AreEquivalent(AllToolbarButtons, order, "Every button exactly once.");
            CollectionAssert.AreEqual(saved, order.Where(saved.Contains).ToList(),
                "The buttons the user ordered keep their relative order.");
        }

        /// <summary>
        /// A button the saved order lacks (added in a newer version) lands right before the first of its
        /// default successors the user kept — not at the end. Successor, not predecessor, on purpose:
        /// that is what keeps 📎 next to ☰ and 🤖 next to ⚙ for a user who had moved ☰/⚙ before v291.0.
        /// </summary>
        [TestMethod]
        public void NormalizeToolbarOrder_SlotsMissingButtonsBeforeTheirFirstKeptDefaultSuccessor()
        {
            // The user kept only three buttons, with ⚙ ahead of ☰.
            var saved = new[] { ToolbarButton.GitHubIssues, ToolbarButton.Settings, ToolbarButton.ToolsMenu };

            List<ToolbarButton> order = ClaudeCodeControl.NormalizeToolbarOrder(saved);

            CollectionAssert.AreEqual(new[]
            {
                // Every feature that precedes GitHubIssues by default slots in before it, in default order…
                ToolbarButton.UpdateAgent, ToolbarButton.DetachTerminal, ToolbarButton.RestartAgent,
                ToolbarButton.ViewChanges, ToolbarButton.SessionHistory, ToolbarButton.ShowUsage,
                ToolbarButton.SetWorkingDirectory, ToolbarButton.SendBuildErrors,
                ToolbarButton.GenerateCommitMessage, ToolbarButton.GenerateCommitMessageAndPush,
                ToolbarButton.RecommendModel, ToolbarButton.GitHubIssues,
                // …⚡ and 🤖 before ⚙ (their first kept successor), 📎 before ☰.
                ToolbarButton.CustomCommands, ToolbarButton.ModelMenu, ToolbarButton.Settings,
                ToolbarButton.AttachMenu, ToolbarButton.ToolsMenu
            }, order);
        }

        /// <summary>
        /// v291.0 added 📎 and 🤖 to the order. A pre-v291 saved order (… 📎 ☰ ⚡ 🤖 ⚙ was the implicit
        /// layout) must keep looking the same: 📎 right before ☰, 🤖 right before ⚙.
        /// </summary>
        [TestMethod]
        public void NormalizeToolbarOrder_KeepsAPreV291LayoutUnchanged()
        {
            var saved = ClaudeCodeControl.DefaultToolbarButtonOrder
                .Where(b => !ClaudeCodeControl.IsPanelOnlyToolbarControl(b))
                .Reverse()
                .ToArray();

            List<ToolbarButton> order = ClaudeCodeControl.NormalizeToolbarOrder(saved);

            Assert.AreEqual(order.IndexOf(ToolbarButton.ToolsMenu) - 1, order.IndexOf(ToolbarButton.AttachMenu), "📎 sits right before ☰.");
            Assert.AreEqual(order.IndexOf(ToolbarButton.Settings) - 1, order.IndexOf(ToolbarButton.ModelMenu), "🤖 sits right before ⚙.");
        }

        [TestMethod]
        public void NormalizeToolbarOrder_DropsDuplicatesAndUnknownValues()
        {
            var saved = new[] { ToolbarButton.ViewChanges, (ToolbarButton)999, ToolbarButton.ViewChanges, ToolbarButton.ToolsMenu };

            List<ToolbarButton> order = ClaudeCodeControl.NormalizeToolbarOrder(saved);

            CollectionAssert.AreEquivalent(AllToolbarButtons, order, "Every button exactly once, nothing else.");
            Assert.IsTrue(order.IndexOf(ToolbarButton.ViewChanges) < order.IndexOf(ToolbarButton.ToolsMenu));
        }

        [TestMethod]
        public void DefaultToolbarOrder_KeepsAttachBeforeToolsAndModelBeforeSettings()
        {
            var order = ClaudeCodeControl.DefaultToolbarButtonOrder.ToList();

            Assert.AreEqual(order.IndexOf(ToolbarButton.ToolsMenu) - 1, order.IndexOf(ToolbarButton.AttachMenu),
                "The default layout stays … 📎 ☰ ⚡ 🤖 ⚙.");
            Assert.AreEqual(order.IndexOf(ToolbarButton.Settings) - 1, order.IndexOf(ToolbarButton.ModelMenu));
            Assert.AreEqual(order.IndexOf(ToolbarButton.CustomCommands) - 1, order.IndexOf(ToolbarButton.ToolsMenu));
        }

        #endregion

        #region Button vs ☰ Tools entry

        [TestMethod]
        public void ResolveToolbarPlacement_PromotedFeatureIsOnlyAButton()
        {
            Assert.AreEqual((true, false), ClaudeCodeControl.ResolveToolbarPlacement(constraintOk: true, promoted: true));
        }

        [TestMethod]
        public void ResolveToolbarPlacement_UnpromotedFeatureIsOnlyAMenuEntry()
        {
            Assert.AreEqual((false, true), ClaudeCodeControl.ResolveToolbarPlacement(constraintOk: true, promoted: false));
        }

        [TestMethod]
        public void ResolveToolbarPlacement_FeatureThatDoesNotApplyIsNowhere()
        {
            Assert.AreEqual((false, false), ClaudeCodeControl.ResolveToolbarPlacement(constraintOk: false, promoted: true));
            Assert.AreEqual((false, false), ClaudeCodeControl.ResolveToolbarPlacement(constraintOk: false, promoted: false));
        }

        /// <summary>
        /// 🤖 only drives the console TUI, so native mode hides it — also while the chat is still docked
        /// in the panel, not just once it has its own tab.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_HidesTheModelButtonWheneverNativeModeIsOn()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "bool hasConsole = !IsNativeModeActive;");
            StringAssert.Contains(body, "ModelDropdownButton.Visibility = (hasConsole && hasModelMenu)",
                "🤖 must be gated on a console being present, not only on the chat being detached.");
            Assert.IsTrue(
                body.IndexOf("ModelDropdownButton.Visibility = (hasConsole && hasModelMenu)", StringComparison.Ordinal) <
                body.IndexOf("if (IsChatDetachedToOwnTab)", StringComparison.Ordinal),
                "The native-mode gate must run for the docked chat too, i.e. outside the IsChatDetachedToOwnTab block.");
        }

        #endregion

        #region Settings → Toolbar tab

        [TestMethod]
        public void ResolveToolbarSelection_PromotesOnlyCheckedFeaturesInRowOrder()
        {
            var rows = new[] { ToolbarButton.GitHubIssues, ToolbarButton.ToolsMenu, ToolbarButton.ViewChanges, ToolbarButton.UpdateAgent };
            var isChecked = new Dictionary<ToolbarButton, bool>
            {
                [ToolbarButton.GitHubIssues] = true,
                [ToolbarButton.ToolsMenu] = true,
                [ToolbarButton.ViewChanges] = true,
                [ToolbarButton.UpdateAgent] = false
            };

            var (visible, _) = ClaudeCodeControl.ResolveToolbarSelection(rows, isChecked);

            CollectionAssert.AreEqual(new[] { ToolbarButton.GitHubIssues, ToolbarButton.ViewChanges }, visible);
        }

        [TestMethod]
        public void ResolveToolbarSelection_NeverPromotesTheToolbarsOwnControls()
        {
            var rows = new[] { ToolbarButton.ToolsMenu, ToolbarButton.CustomCommands, ToolbarButton.Settings };
            var isChecked = rows.ToDictionary(b => b, b => true);

            var (visible, _) = ClaudeCodeControl.ResolveToolbarSelection(rows, isChecked);

            Assert.AreEqual(0, visible.Count, "☰ ⚡ ⚙ only have a position; they are never VisibleToolbarButtons entries.");
        }

        [TestMethod]
        public void ResolveToolbarSelection_ToolsMenuFollowsItsCheckboxAndDefaultsToShown()
        {
            var rows = new[] { ToolbarButton.ToolsMenu };

            Assert.IsTrue(ClaudeCodeControl.ResolveToolbarSelection(rows, new Dictionary<ToolbarButton, bool> { [ToolbarButton.ToolsMenu] = true }).ShowToolsMenu);
            Assert.IsFalse(ClaudeCodeControl.ResolveToolbarSelection(rows, new Dictionary<ToolbarButton, bool> { [ToolbarButton.ToolsMenu] = false }).ShowToolsMenu);
            Assert.IsTrue(ClaudeCodeControl.ResolveToolbarSelection(rows, new Dictionary<ToolbarButton, bool>()).ShowToolsMenu,
                "Without a ☰ row the menu must stay shown, or the unpromoted features become unreachable.");
        }

        [TestMethod]
        public void ReadToolbarRowOrder_ReadsTheRowsTopToBottomAndSkipsUntaggedChildren()
        {
            RunOnSta(() =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new Border { Tag = ToolbarButton.Settings });
                panel.Children.Add(new TextBlock { Tag = ToolbarButton.ShowUsage });
                panel.Children.Add(new Border());
                panel.Children.Add(new Border { Tag = ToolbarButton.ViewChanges, Visibility = Visibility.Collapsed });
                panel.Children.Add(new Border { Tag = ToolbarButton.UpdateAgent });

                CollectionAssert.AreEqual(
                    new[] { ToolbarButton.Settings, ToolbarButton.ViewChanges, ToolbarButton.UpdateAgent },
                    ClaudeCodeControl.ReadToolbarRowOrder(panel),
                    "Collapsed rows (Detach in native mode) keep their place so the saved order survives.");
            });
        }

        #endregion

        #region Reordering the panel and the ☰ menu

        [TestMethod]
        public void ApplyToolbarButtonOrder_FollowsTheOrderAndSkipsNullsAndForeignControls()
        {
            RunOnSta(() =>
            {
                var f1 = new Button { Name = "f1" };
                var f2 = new Button { Name = "f2" };
                var f3 = new Button { Name = "f3" };
                var attach = new Button { Name = "attach" };
                var tools = new Button { Name = "tools" };
                var settings = new Button { Name = "settings" };
                var panel = new StackPanel();
                foreach (var b in new[] { f1, f2, f3, attach, tools, settings }) panel.Children.Add(b);

                ClaudeCodeControl.ApplyToolbarButtonOrder(panel,
                    new UIElement[] { f2, settings, null, attach, f1, new Button(), tools, f3 });

                CollectionAssert.AreEqual(
                    new[] { "f2", "settings", "attach", "f1", "tools", "f3" },
                    panel.Children.OfType<Button>().Select(b => b.Name).ToList(),
                    "The order is applied as given; nulls and controls outside the panel are skipped.");
            });
        }

        [TestMethod]
        public void ApplyToolsMenuOrder_FollowsTheOrderAndSkipsForeignItems()
        {
            RunOnSta(() =>
            {
                var m1 = new MenuItem { Name = "m1" };
                var m2 = new MenuItem { Name = "m2" };
                var m3 = new MenuItem { Name = "m3" };
                var menu = new ContextMenu();
                foreach (var m in new[] { m1, m2, m3 }) menu.Items.Add(m);

                ClaudeCodeControl.ApplyToolsMenuOrder(menu, new object[] { m3, null, m1, new MenuItem(), m2 });

                CollectionAssert.AreEqual(new[] { "m3", "m1", "m2" },
                    menu.Items.OfType<MenuItem>().Select(m => m.Name).ToList());
            });
        }

        [TestMethod]
        public void ReorderToolbarControls_UsesTheEffectiveOrderAndTheMappedControls()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void ReorderToolbarControls()");

            StringAssert.Contains(body, "var order = GetEffectiveToolbarOrder();");
            StringAssert.Contains(body, "GetToolbarButtonControl(b)");
            StringAssert.Contains(body, "GetToolbarMenuItemControl(b)");
            Assert.IsFalse(body.Contains("KeepBefore"),
                "v291.0: 📎 and 🤖 are in the order themselves; nothing may be moved next to a neighbour behind the user's back.");
        }

        #endregion

        #region Panel-only controls and the alignment switch (v291.0)

        [TestMethod]
        public void PanelOnlyControls_AreAttachAndModelAndNeverFeatures()
        {
            CollectionAssert.AreEquivalent(new[] { ToolbarButton.AttachMenu, ToolbarButton.ModelMenu },
                AllToolbarButtons.Where(ClaudeCodeControl.IsPanelOnlyToolbarControl).ToList());
            Assert.IsFalse(ClaudeCodeControl.IsToolbarFeature(ToolbarButton.AttachMenu));
            Assert.IsFalse(ClaudeCodeControl.IsToolbarFeature(ToolbarButton.ModelMenu));
        }

        /// <summary>
        /// 📎 and 🤖 rows can be dragged but not switched: their checkbox shows whether the current mode
        /// shows them, and follows the dialog's own terminal/native switch live.
        /// </summary>
        [TestMethod]
        public void SettingsToolbarTab_PanelOnlyRowsAreReadOnlyAndFollowTheModeSwitch()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs"),
                "private (StackPanel RowsPanel, Dictionary<ToolbarButton, CheckBox> Checks) BuildToolbarButtonsTabContent(");

            StringAssert.Contains(body, "IsPanelOnlyToolbarControl(id))", "Panel-only rows must be recognised.");
            StringAssert.Contains(body, "if (id == ToolbarButton.CustomCommands || id == ToolbarButton.Settings || IsPanelOnlyToolbarControl(id))",
                "Their checkbox must be disabled like ⚡'s and ⚙'s.");
            StringAssert.Contains(body, "nativeModeCheck.Checked += syncPanelOnly;");
            StringAssert.Contains(body, "nativeModeCheck.Unchecked += syncPanelOnly;");
            StringAssert.Contains(body, "Text = L(\"Terminal only\"),", "Both rows carry the same mode note.");
            StringAssert.Contains(body, "bool PanelOnlyControlShown() => nativeModeCheck.IsChecked != true;",
                "Both are shown exactly when the embedded terminal is in use.");
        }

        /// <summary>
        /// 📎 used to stay on the panel toolbar in native mode while the chat was docked in the panel,
        /// next to the composer's own 📎 in the action row — a duplicate. Terminal only since v291.0.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_HidesTheAttachButtonInNativeMode()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "AttachDropdownButton.Visibility = hasConsole ? Visibility.Visible : Visibility.Collapsed;");
            Assert.IsFalse(body.Contains("AttachDropdownButton.Visibility = Visibility.Visible;"),
                "📎 must not be forced visible regardless of the mode.");
        }

        /// <summary>
        /// The alignment switch only moves the panel's button strip, which native mode replaces with the
        /// chat tab — so it belongs on the Terminal tab (greyed out in native mode), not on Toolbar.
        /// </summary>
        [TestMethod]
        public void ToolbarAlignmentSwitch_LivesOnTheTerminalTab()
        {
            string cs = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs");

            StringAssert.Contains(cs, "terminalPanelSection.Children.Add(toolbarRightAlignCheck);");
            Assert.IsFalse(cs.Contains("toolbarStack.Children.Add(toolbarRightAlignCheck);"),
                "The switch must not be on the Toolbar tab any more.");
            Assert.IsFalse(cs.Contains("L(\"Alignment\")"), "The Toolbar tab's Alignment header went with it.");

            int header = cs.IndexOf("terminalPanelSection.Children.Add(MakeSectionHeader(L(\"Toolbar alignment\"), themeFg));", System.StringComparison.Ordinal);
            int check = cs.IndexOf("terminalPanelSection.Children.Add(toolbarRightAlignCheck);", System.StringComparison.Ordinal);
            Assert.IsTrue(header >= 0 && header < check, "A lone checkbox under the position radios was easy to miss; it gets its own header.");
        }

        #endregion

        #region Helpers

        private static string ProviderManagementSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ProviderManagement.cs");

        private static XDocument ControlXaml => XDocument.Parse(RepositoryLayout.ReadText("UI", "ClaudeCodeControl.xaml"));

        private static XElement FindByName(XDocument xaml, string name)
        {
            XElement element = xaml.Descendants().FirstOrDefault(e => (string)e.Attribute(XamlNs + "Name") == name);
            Assert.IsNotNull(element, $"x:Name=\"{name}\" not found in ClaudeCodeControl.xaml; update this guard.");
            return element;
        }

        private static List<string> ChildNames(XElement parent, string elementName)
        {
            return parent.Elements(WpfNs + elementName)
                .Select(e => (string)e.Attribute(XamlNs + "Name"))
                .Where(n => n != null)
                .ToList();
        }

        /// <summary>"case ToolbarButton.X: return Y;" lines of a switch, as X → Y.</summary>
        private static Dictionary<string, string> ParseCaseReturns(string body)
        {
            return Regex.Matches(body, @"case ToolbarButton\.(\w+):\s*return (\w+);")
                .Cast<Match>()
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
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
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces in: {signature}");
            return null;
        }

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new AssertFailedException(failure.Message, failure);
            }
        }

        #endregion
    }
}
