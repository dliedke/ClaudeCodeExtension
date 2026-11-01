/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: Settings dialog search: normalization and term matching, English reverse lookup, and the tab/section/hint filtering
 * ***********************/

using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SettingsSearchTests
    {
        [TestCleanup]
        public void BackToEnglish()
        {
            Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
        }

        [TestMethod]
        public void Normalize_IgnoresCaseAccentsAndRepeatedSpaces()
        {
            Assert.AreEqual("so conversas recentes", SettingsSearch.Normalize("  Só   Conversas\tRECENTES "));
            Assert.AreEqual("configuracao", SettingsSearch.Normalize("Configuração"));
            Assert.AreEqual(string.Empty, SettingsSearch.Normalize(null));
        }

        [TestMethod]
        public void Matches_NeedsEveryTermInAnyOrder()
        {
            string text = SettingsSearch.Normalize("Mostrar a contagem de subagentes ativos");

            Assert.IsTrue(SettingsSearch.Matches(text, SettingsSearch.Terms("subagente contagem")));
            Assert.IsTrue(SettingsSearch.Matches(text, SettingsSearch.Terms("ATIVOS")));
            Assert.IsFalse(SettingsSearch.Matches(text, SettingsSearch.Terms("subagente tema")));
            Assert.IsTrue(SettingsSearch.Matches(text, SettingsSearch.Terms("   ")), "A blank query matches everything.");
        }

        [TestMethod]
        public void English_ReversesTheActiveLanguageOnly()
        {
            Loc.SetLanguageFromJson("pt-BR", "{ \"Tips only\": \"Só dicas\" }");

            Assert.AreEqual("Tips only", Loc.English("Só dicas"));
            Assert.IsNull(Loc.English("Tips only"), "English text is not a translation.");

            Loc.SetLanguageFromJson("it", "{ \"Tips only\": \"Solo suggerimenti\" }");
            Assert.IsNull(Loc.English("Só dicas"), "The reverse map follows a language switch.");
            Assert.AreEqual("Tips only", Loc.English("Solo suggerimenti"));
        }

        [TestMethod]
        public void Filter_KeepsMatchesWithTheirHeaderAndHintAndHidesEmptyTabs()
        {
            RunOnSta(() =>
            {
                var tabs = new TabControl();

                var header = new TextBlock { Text = "Display", Tag = SettingsSearch.SectionHeaderTag };
                var tokens = new CheckBox { Content = "Show token counts" };
                var tokensHint = new TextBlock { Text = "Live while the agent works." };
                var thinking = new CheckBox
                {
                    Content = "Plain working status",
                    ToolTip = new ToolTip { Content = new TextBlock { Text = "Always says Thinking" } }
                };
                var appliesNot = new CheckBox { Content = "Show token counts (hidden by the dialog)", Visibility = Visibility.Collapsed };
                StackPanel first = AddTab(tabs, "Native mode", header, tokens, tokensHint, thinking, appliesNot);

                var other = new CheckBox { Content = "Send large prompts as file" };
                AddTab(tabs, "Prompt", other);

                var filter = new SettingsSearchFilter(tabs);

                Assert.AreEqual(1, filter.Apply("token"));
                Assert.AreEqual(Visibility.Visible, header.Visibility);
                Assert.AreEqual(Visibility.Visible, tokens.Visibility);
                Assert.AreEqual(Visibility.Visible, tokensHint.Visibility, "The hint goes with its setting.");
                Assert.AreEqual(Visibility.Collapsed, thinking.Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((TabItem)tabs.Items[1]).Visibility);

                // The tooltip is searched too, and a hint's text finds its setting.
                Assert.AreEqual(1, filter.Apply("thinking"));
                Assert.AreEqual(Visibility.Visible, thinking.Visibility);
                Assert.AreEqual(1, filter.Apply("agent works"));
                Assert.AreEqual(Visibility.Visible, tokens.Visibility);

                // A matching section header keeps its whole section; a matching tab name its whole tab.
                Assert.AreEqual(1, filter.Apply("display"));
                Assert.AreEqual(Visibility.Visible, thinking.Visibility);
                Assert.AreEqual(1, filter.Apply("prompt"));
                Assert.AreEqual(Visibility.Visible, other.Visibility);

                Assert.AreEqual(0, filter.Apply("nothing like this"));

                // Clearing restores everything except what the dialog itself had hidden.
                Assert.AreEqual(2, filter.Apply(string.Empty));
                Assert.AreEqual(Visibility.Visible, thinking.Visibility);
                Assert.AreEqual(Visibility.Visible, ((TabItem)tabs.Items[1]).Visibility);
                Assert.AreEqual(Visibility.Collapsed, appliesNot.Visibility);
                Assert.IsNotNull(first);
            });
        }

        [TestMethod]
        public void Filter_FindsTheTranslatedTextAndTheEnglishOne()
        {
            RunOnSta(() =>
            {
                Loc.SetLanguageFromJson("pt-BR", "{ \"Show token counts\": \"Mostrar contagem de tokens\" }");

                var tabs = new TabControl();
                var tokens = new CheckBox { Content = Loc.L("Show token counts") };
                var other = new CheckBox { Content = "Outra coisa" };
                AddTab(tabs, "Chat", tokens, other);

                var filter = new SettingsSearchFilter(tabs);

                filter.Apply("contagem");
                Assert.AreEqual(Visibility.Visible, tokens.Visibility);
                Assert.AreEqual(Visibility.Collapsed, other.Visibility);

                filter.Apply("token counts");
                Assert.AreEqual(Visibility.Visible, tokens.Visibility, "English names still find the setting.");
                Assert.AreEqual(Visibility.Collapsed, other.Visibility);
            });
        }

        [TestMethod]
        public void Filter_LooksInsideNestedSectionsButKeepsALabelWithItsTextBox()
        {
            RunOnSta(() =>
            {
                var tabs = new TabControl();

                var languageHeader = new TextBlock { Text = "Language", Tag = SettingsSearch.SectionHeaderTag };
                var section = new StackPanel();
                section.Children.Add(languageHeader);
                var autoTitle = new CheckBox { Content = "Name chat tabs automatically" };
                section.Children.Add(autoTitle);

                var labelled = new StackPanel();
                var label = new TextBlock { Text = "File types" };
                var box = new TextBox { Text = "cs;xaml" };
                labelled.Children.Add(label);
                labelled.Children.Add(box);

                AddTab(tabs, "Settings", section, labelled);
                var filter = new SettingsSearchFilter(tabs);

                filter.Apply("file types");
                Assert.AreEqual(Visibility.Visible, labelled.Visibility);
                Assert.AreEqual(Visibility.Visible, box.Visibility, "The text box stays with its label.");
                Assert.AreEqual(Visibility.Collapsed, section.Visibility);

                filter.Apply("chat tabs");
                Assert.AreEqual(Visibility.Visible, section.Visibility);
                Assert.AreEqual(Visibility.Visible, autoTitle.Visibility);
                Assert.AreEqual(Visibility.Visible, languageHeader.Visibility, "A header stays above a match.");
                Assert.AreEqual(Visibility.Collapsed, labelled.Visibility);

                filter.Apply("file types");
                Assert.AreEqual(Visibility.Collapsed, languageHeader.Visibility, "A header with no match under it hides.");

                filter.Apply("xaml");
                Assert.AreEqual(Visibility.Collapsed, labelled.Visibility, "What the user typed is not searched.");
            });
        }

        private static StackPanel AddTab(TabControl tabs, string header, params UIElement[] children)
        {
            var stack = new StackPanel();
            foreach (UIElement child in children)
            {
                stack.Children.Add(child);
            }

            tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { Content = stack } });
            return stack;
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
    }
}
