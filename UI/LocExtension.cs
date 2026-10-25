/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: WPF side of the localization: {local:Loc 'English text'} shows Loc.L of the text and follows a
 *          language change at once; LocTree puts a window built in code into a new language while it is open
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// <c>Text="{local:Loc 'Jump to the latest message'}"</c>. On a dependency property (templates and
    /// setters included) it is a one-way binding to <see cref="LocSource"/>, so open chats switch language
    /// without being rebuilt; anywhere else it is the text as translated when the element is built.
    /// Code that later assigns the property replaces the binding and owns the text from then on.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public class LocExtension : MarkupExtension
    {
        public LocExtension()
        {
        }

        public LocExtension(string text)
        {
            Text = text;
        }

        [ConstructorArgument("text")]
        public string Text { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
            if (target == null || !(target.TargetProperty is DependencyProperty || target.TargetObject is Setter))
            {
                return Loc.L(Text);
            }

            var binding = new Binding(nameof(LocSource.Language))
            {
                Source = LocSource.Instance,
                Mode = BindingMode.OneWay,
                Converter = TranslateConverter.Instance,
                ConverterParameter = Text
            };

            return binding.ProvideValue(serviceProvider);
        }

        /// <summary>Ignores the bound language and returns <see cref="Loc.L(string)"/> of the parameter.</summary>
        private sealed class TranslateConverter : IValueConverter
        {
            public static readonly TranslateConverter Instance = new TranslateConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Loc.L(parameter as string);
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }

    /// <summary>
    /// Binding source of <see cref="LocExtension"/>: <see cref="Language"/> changes with every
    /// <see cref="Loc.SetLanguage"/>, which re-runs each binding's translation. WPF listens to it through a
    /// weak event, so the bound elements of closed chats are not kept alive by this singleton.
    /// </summary>
    public sealed class LocSource : INotifyPropertyChanged
    {
        public static readonly LocSource Instance = new LocSource();

        private LocSource()
        {
            Loc.LanguageChanged += (s, e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }

        public string Language { get { return Loc.Language; } }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Puts an open window built in code (the Settings dialog, GitHub Issues) into the language just applied,
    /// without rebuilding it: the window title, every <see cref="TextBlock"/>/<see cref="Run"/> text and every
    /// string Content, Header and ToolTip that was a translation goes through <see cref="Loc.Retranslate"/>.
    /// Text boxes hold user input and are left alone, bound properties already follow on their own, and
    /// formatted captions ("{0} files") keep their text until the window is opened again.
    /// </summary>
    public static class LocTree
    {
        public static void Retranslate(DependencyObject root)
        {
            if (root == null)
            {
                return;
            }

            Visit(root, new HashSet<object>());
        }

        private static void Visit(object node, HashSet<object> seen)
        {
            if (node == null || node is string || !seen.Add(node))
            {
                return;
            }

            if (node is TextBoxBase || node is PasswordBox)
            {
                return;
            }

            if (node is TextBlock textBlock)
            {
                // A plain TextBlock is one run: retranslate its Text. Mixed inlines (bold parts, links)
                // are retranslated run by run so their formatting survives.
                if (textBlock.Inlines.Count <= 1)
                {
                    Set(textBlock, TextBlock.TextProperty, textBlock.Text);
                }
                else
                {
                    foreach (Inline inline in new List<Inline>(textBlock.Inlines))
                    {
                        Visit(inline, seen);
                    }
                }

                VisitToolTip(textBlock, seen);
                return;
            }

            if (node is Run run)
            {
                Set(run, Run.TextProperty, run.Text);
                return;
            }

            if (node is Span span)
            {
                foreach (Inline inline in new List<Inline>(span.Inlines))
                {
                    Visit(inline, seen);
                }

                return;
            }

            if (node is Window window)
            {
                Set(window, Window.TitleProperty, window.Title);
            }

            if (node is HeaderedContentControl headered && headered.Header is string header)
            {
                Set(headered, HeaderedContentControl.HeaderProperty, header);
            }

            if (node is HeaderedItemsControl headeredItems && headeredItems.Header is string itemsHeader)
            {
                Set(headeredItems, HeaderedItemsControl.HeaderProperty, itemsHeader);
            }

            if (node is ContentControl contentControl && contentControl.Content is string content)
            {
                Set(contentControl, ContentControl.ContentProperty, content);
            }

            if (node is FrameworkElement element)
            {
                VisitToolTip(element, seen);
                if (element.ContextMenu != null)
                {
                    Visit(element.ContextMenu, seen);
                }
            }

            if (node is DependencyObject dependencyObject)
            {
                foreach (object child in new List<object>(LogicalTreeHelper.GetChildren(dependencyObject).Cast<object>()))
                {
                    Visit(child, seen);
                }
            }
        }

        private static void VisitToolTip(FrameworkElement element, HashSet<object> seen)
        {
            if (element.ToolTip is string tip)
            {
                Set(element, FrameworkElement.ToolTipProperty, tip);
            }
            else if (element.ToolTip is DependencyObject tipObject)
            {
                Visit(tipObject, seen);
            }
        }

        /// <summary>Replaces a plain-text property with its retranslation; a bound property is left to its binding.</summary>
        private static void Set(DependencyObject target, DependencyProperty property, string shown)
        {
            if (string.IsNullOrEmpty(shown) || BindingOperations.IsDataBound(target, property))
            {
                return;
            }

            string translated = Loc.Retranslate(shown);
            if (!string.Equals(translated, shown, StringComparison.Ordinal))
            {
                target.SetValue(property, translated);
            }
        }
    }
}
