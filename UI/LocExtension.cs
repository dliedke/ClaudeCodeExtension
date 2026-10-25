/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: XAML side of the chat localization: {local:Loc 'English text'} shows Loc.L of the text and follows a
 *          language change at once
 *
 * *******************************************************************************************************************/

using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
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
}
