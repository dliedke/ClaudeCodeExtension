/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Where one native-mode chat is drawn — its own VS document tab (NativeChatToolWindow) or a tab
 *          inside the single "Claude Code" chat window (ChatContainerToolWindow) — behind one contract,
 *          so the chat code does not care which.
 *
 * *******************************************************************************************************************/

using System;
using System.Windows;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    /// <summary>
    /// The place one chat transcript is shown. All members are UI-thread only.
    /// </summary>
    public interface IChatTabHost
    {
        /// <summary>The user (or VS) closed this chat's tab. The transcript is still parented here
        /// when it fires, so handlers can take it out.</summary>
        event EventHandler Closed;

        /// <summary>This chat's tab was brought to the front.</summary>
        event EventHandler Activated;

        /// <summary>Puts the transcript in (or takes it out with null). The caller detaches it from
        /// its previous parent first.</summary>
        void SetChatContent(UIElement content);

        bool HasChatContent { get; }

        void UpdateCaption(string caption);

        /// <summary>False once VS has torn the tab down; such a host is dropped and rebuilt.</summary>
        bool IsAlive { get; }

        /// <summary>True when VS reports the window holding this chat as visible.</summary>
        bool IsVisible { get; }

        /// <summary>Brings the tab to the front and activates it. Returns an HRESULT.</summary>
        int Show();

        /// <summary>Closes the tab; <see cref="Closed"/> fires on the way.</summary>
        void Close();

        /// <summary>The VS frame holding the chat (the shared window's frame for an inner tab), for
        /// code that restores frames VS hid; null when there is none.</summary>
        IVsWindowFrame WindowFrame { get; }
    }
}
