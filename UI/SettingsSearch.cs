/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: Settings dialog search: text matching (pure, unit-tested) and the filter that hides the settings, sections and tabs that do not match
 * ***********************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Matching rules of the Settings search. Case, accents and repeated spaces are ignored, and every
    /// word of the query has to appear somewhere in the setting's text (in any order).
    /// </summary>
    internal static class SettingsSearch
    {
        /// <summary>Marks a section header (<c>Tag</c>), so the filter keeps it with the settings under it.</summary>
        public static readonly object SectionHeaderTag = new object();

        /// <summary>Lowercase, without diacritics ("Ação" → "acao"), whitespace collapsed to single spaces.</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string decomposed = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            bool lastWasSpace = true;

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }

            return sb.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
        }

        /// <summary>The query's words, normalized. Empty for a blank query.</summary>
        public static string[] Terms(string query)
        {
            string normalized = Normalize(query);
            return normalized.Length == 0
                ? new string[0]
                : normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>True when every term appears in <paramref name="normalizedText"/> (already <see cref="Normalize"/>d).</summary>
        public static bool Matches(string normalizedText, string[] terms)
        {
            if (terms == null || terms.Length == 0)
            {
                return true;
            }

            if (string.IsNullOrEmpty(normalizedText))
            {
                return false;
            }

            foreach (string term in terms)
            {
                if (normalizedText.IndexOf(term, StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Filters the Settings dialog's tabs by a query. A setting matches on its caption, its tooltip and
    /// the choices of its combo boxes, in the language shown and in English. Settings that do not match
    /// are collapsed, a section header stays while anything under it matches (and a matching header keeps
    /// its whole section), and tabs left with nothing are hidden. A plain text line right after a setting
    /// is its hint and goes with it. Only what was visible is touched: anything the dialog itself hides
    /// (a setting that does not apply) stays as it is, and clearing the query restores the rest.
    /// </summary>
    internal sealed class SettingsSearchFilter
    {
        private readonly TabControl _tabs;
        private readonly List<UIElement> _hidden = new List<UIElement>();

        public SettingsSearchFilter(TabControl tabs)
        {
            _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        }

        /// <summary>Applies <paramref name="query"/>; returns how many tabs have a match (all of them for a blank query).</summary>
        public int Apply(string query)
        {
            foreach (UIElement element in _hidden)
            {
                element.Visibility = Visibility.Visible;
            }

            _hidden.Clear();

            string[] terms = SettingsSearch.Terms(query);
            var tabItems = _tabs.Items.OfType<TabItem>().ToList();
            if (terms.Length == 0)
            {
                return tabItems.Count;
            }

            int matchingTabs = 0;
            TabItem firstMatch = null;

            foreach (TabItem tab in tabItems)
            {
                if (tab.Visibility != Visibility.Visible)
                {
                    continue;
                }

                // A tab whose name matches ("Toolbar") is shown whole.
                bool any = SettingsSearch.Matches(SettingsSearch.Normalize(HeaderText(tab)), terms)
                    || (tab.Content is ScrollViewer page && page.Content is Panel pageStack
                        ? FilterContainer(pageStack, terms)
                        : Matches(tab.Content as DependencyObject, terms));

                if (tab.Content is ScrollViewer scroller)
                {
                    scroller.ScrollToTop();
                }

                if (any)
                {
                    matchingTabs++;
                    if (firstMatch == null) firstMatch = tab;
                }
                else
                {
                    Hide(tab);
                }
            }

            if (firstMatch != null && (_tabs.SelectedItem as UIElement)?.Visibility != Visibility.Visible)
            {
                _tabs.SelectedItem = firstMatch;
            }

            return matchingTabs;
        }

        /// <summary>
        /// Filters the children of a vertical panel of settings; true when anything in it is left visible.
        /// </summary>
        private bool FilterContainer(Panel panel, string[] terms)
        {
            var children = panel.Children.OfType<UIElement>().Where(c => c.Visibility == Visibility.Visible).ToList();

            bool anyVisible = false;
            UIElement header = null;
            bool headerMatches = false;
            bool sectionHasMatch = false;

            void CloseSection()
            {
                if (header != null && !headerMatches && !sectionHasMatch)
                {
                    Hide(header);
                }
            }

            for (int i = 0; i < children.Count; i++)
            {
                UIElement child = children[i];

                if (IsSectionHeader(child))
                {
                    CloseSection();
                    header = child;
                    headerMatches = Matches(child, terms);
                    sectionHasMatch = false;
                    anyVisible |= headerMatches;
                    continue;
                }

                // The setting plus the hint lines right after it.
                var group = new List<UIElement> { child };
                while (i + 1 < children.Count && IsHintLine(children[i + 1]) && !IsHintLine(child))
                {
                    group.Add(children[++i]);
                }

                bool matched;
                if (headerMatches)
                {
                    matched = true;
                }
                else if (group.Count == 1 && AsContainer(child) is Panel inner)
                {
                    matched = FilterContainer(inner, terms);
                }
                else
                {
                    matched = group.Any(element => Matches(element, terms));
                }

                if (matched)
                {
                    sectionHasMatch = true;
                    anyVisible = true;
                }
                else
                {
                    group.ForEach(Hide);
                }
            }

            CloseSection();
            return anyVisible;
        }

        private static string HeaderText(TabItem tab)
        {
            var text = new StringBuilder();
            CollectText(tab.Header, text, 0);
            return text.ToString();
        }

        private void Hide(UIElement element)
        {
            element.Visibility = Visibility.Collapsed;
            _hidden.Add(element);
        }

        private static bool IsSectionHeader(UIElement element)
        {
            return element is FrameworkElement fe && ReferenceEquals(fe.Tag, SettingsSearch.SectionHeaderTag);
        }

        /// <summary>A plain line of text (not a header): the explanation of the setting before it.</summary>
        private static bool IsHintLine(UIElement element)
        {
            return element is TextBlock && !IsSectionHeader(element);
        }

        /// <summary>
        /// The panel to filter inside <paramref name="element"/>, or null when it is one setting: a group of
        /// settings is a vertical stack (possibly in a border) holding a section header, a check box or a
        /// radio button, or another such group. A label over a text box stays one setting.
        /// </summary>
        private static Panel AsContainer(UIElement element)
        {
            while (element is Decorator decorator)
            {
                element = decorator.Child;
            }

            if (!(element is StackPanel stack) || stack.Orientation != Orientation.Vertical)
            {
                return null;
            }

            foreach (UIElement child in stack.Children)
            {
                if (ToggleButtonLike(child) || IsSectionHeader(child) || AsContainer(child) != null)
                {
                    return stack;
                }
            }

            return null;
        }

        private static bool ToggleButtonLike(UIElement element)
        {
            return element is CheckBox || element is RadioButton;
        }

        private static bool Matches(DependencyObject element, string[] terms)
        {
            if (element == null)
            {
                return false;
            }

            var text = new StringBuilder();
            CollectText(element, text, 0);
            return SettingsSearch.Matches(SettingsSearch.Normalize(text.ToString()), terms);
        }

        /// <summary>Caption, tooltip, combo choices and child text of an element, each also in English.</summary>
        private static void CollectText(object node, StringBuilder text, int depth)
        {
            if (node == null || depth > 24)
            {
                return;
            }

            switch (node)
            {
                case string s:
                    Append(text, s);
                    return;
                case TextBox box:
                    // What the user typed is not the setting's name; its tooltip is.
                    CollectText(box.ToolTip, text, depth + 1);
                    return;
                case TextBlock block:
                    Append(text, block.Text);
                    CollectText(block.ToolTip, text, depth + 1);
                    return;
                case ToolTip tip:
                    CollectText(tip.Content, text, depth + 1);
                    return;
            }

            if (node is FrameworkElement fe)
            {
                CollectText(fe.ToolTip, text, depth + 1);
            }

            if (node is HeaderedContentControl headered)
            {
                CollectText(headered.Header, text, depth + 1);
            }

            if (node is ContentControl content)
            {
                CollectText(content.Content, text, depth + 1);
                return;
            }

            if (node is ItemsControl items)
            {
                foreach (object item in items.Items)
                {
                    CollectText(item, text, depth + 1);
                }

                return;
            }

            if (node is Decorator decorator)
            {
                CollectText(decorator.Child, text, depth + 1);
                return;
            }

            if (node is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                {
                    CollectText(child, text, depth + 1);
                }
            }
        }

        private static void Append(StringBuilder text, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            text.Append(value).Append('\n');

            string english = Loc.English(value);
            if (english != null)
            {
                text.Append(english).Append('\n');
            }
        }
    }
}
