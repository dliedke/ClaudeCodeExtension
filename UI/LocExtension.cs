/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: XAML side of the chat localization: {local:Loc 'English text'} returns Loc.L of the text
 *
 * *******************************************************************************************************************/

using System;
using System.Windows.Markup;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// <c>Text="{local:Loc 'Jump to the latest message'}"</c>. Evaluated when the element is built, so a
    /// language change shows in chats opened afterwards.
    /// </summary>
    [MarkupExtensionReturnType(typeof(string))]
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
            return Loc.L(Text);
        }
    }
}
