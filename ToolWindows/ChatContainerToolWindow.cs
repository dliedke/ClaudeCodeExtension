/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: The single "Claude Code" chat window (Settings → "Show all chats in one window"): one VS tool
 *          window whose position VS remembers, holding every native-mode chat as an inner tab.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Unlike <see cref="NativeChatToolWindow"/> (one transient document tab per chat, placed wherever VS
    /// decides and never remembered), this window is registered as a regular tool window, so VS keeps
    /// where the user docked it — in the design layout and the debug layout alike. Closing it only hides
    /// it: the chats inside keep running and are all there when it is shown again.
    /// </summary>
    [Guid("5B0D3C61-8E2A-4F7B-9C14-2D6E8A1F0B37")]
    public class ChatContainerToolWindow : ToolWindowPane
    {
        private readonly ChatContainerView _view;
        private readonly Dictionary<int, ChatContainerTab> _tabsByWindowId = new Dictionary<int, ChatContainerTab>();

        public ChatContainerToolWindow() : base(null)
        {
            Caption = "Claude Code";
            BitmapImageMoniker = KnownMonikers.Comment;

            _view = new ChatContainerView();
            _view.TabSelected += (s, item) => FindTab(item)?.RaiseActivated();
            _view.TabCloseRequested += (s, item) => ((IChatTabHost)FindTab(item))?.Close();
            _view.NewChatRequested += OnViewNewChatRequested;
            Content = _view;
        }

        /// <summary>The "+" in the tab strip; <see cref="SelectedTranscript"/> says which chat it was next to.</summary>
        internal event EventHandler NewChatRequested;

        /// <summary>
        /// VS restores this window with its layout, before the Claude Code panel — which runs the chats —
        /// has loaded. Nothing is listening then, so "+" opens the panel, which starts the chat here.
        /// </summary>
        private void OnViewNewChatRequested(object sender, EventArgs e)
        {
            if (NewChatRequested != null)
            {
                NewChatRequested(this, e);
                return;
            }

            if (Package is AsyncPackage package)
            {
#pragma warning disable VSSDK007 // Fire-and-forget from a click handler
                package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await package.ShowToolWindowAsync(typeof(ClaudeCodeToolWindow), 0, true, package.DisposalToken);
                }).FileAndForget("claudecode/chatcontainer/openpanel");
#pragma warning restore VSSDK007
            }
        }

        internal ChatTranscriptView SelectedTranscript => _view.SelectedTranscript;

        /// <summary>Follows Settings → chat icon style (Modern / Classic) for the "+".</summary>
        internal void SetModernIcons(bool modern) => _view.SetModernIcons(modern);

        /// <summary>
        /// The inner tab for one chat, created on first use. <paramref name="windowId"/> plays the same
        /// role as the instance id of <see cref="NativeChatToolWindow"/>: 0 is the main chat.
        /// </summary>
        internal IChatTabHost GetOrCreateTab(int windowId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_tabsByWindowId.TryGetValue(windowId, out ChatContainerTab existing) && !existing.IsClosed)
            {
                return existing;
            }

            var tab = new ChatContainerTab(this, _view, _view.AddTab());
            _tabsByWindowId[windowId] = tab;
            return tab;
        }

        internal void Forget(ChatContainerTab tab)
        {
            foreach (var pair in _tabsByWindowId)
            {
                if (ReferenceEquals(pair.Value, tab))
                {
                    _tabsByWindowId.Remove(pair.Key);
                    return;
                }
            }
        }

        private ChatContainerTab FindTab(ChatContainerTabItem item)
        {
            foreach (ChatContainerTab tab in _tabsByWindowId.Values)
            {
                if (ReferenceEquals(tab.Item, item))
                {
                    return tab;
                }
            }

            return null;
        }

        internal IVsWindowFrame WindowFrame => Frame as IVsWindowFrame;
    }

    /// <summary>One chat inside <see cref="ChatContainerToolWindow"/>.</summary>
    internal sealed class ChatContainerTab : IChatTabHost
    {
        private readonly ChatContainerToolWindow _window;
        private readonly ChatContainerView _view;

        internal ChatContainerTab(ChatContainerToolWindow window, ChatContainerView view, ChatContainerTabItem item)
        {
            _window = window;
            _view = view;
            Item = item;
        }

        internal ChatContainerTabItem Item { get; }

        internal bool IsClosed { get; private set; }

        public event EventHandler Closed;

        public event EventHandler Activated;

        internal void RaiseActivated() => Activated?.Invoke(this, EventArgs.Empty);

        public void SetChatContent(UIElement content)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // The window's own "+" replaces the composer's ✚ while a chat lives here.
            foreach (ChatTranscriptView leaving in Item.Host.Children.OfType<ChatTranscriptView>())
            {
                leaving.ShowNewChatAction = true;
            }

            Item.Host.Children.Clear();
            if (content != null)
            {
                Item.Host.Children.Add(content);
                if (content is ChatTranscriptView arriving)
                {
                    arriving.ShowNewChatAction = false;
                }
            }
        }

        public bool HasChatContent => Item.Host.Children.Count > 0;

        public void UpdateCaption(string caption)
        {
            Item.Caption = caption;
        }

        public bool IsAlive => !IsClosed && _window.WindowFrame != null;

        public bool IsVisible
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return IsAlive && _window.WindowFrame.IsVisible() == VSConstants.S_OK;
            }
        }

        public int Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsAlive)
            {
                return VSConstants.E_FAIL;
            }

            _view.Select(Item, focusComposer: false);
            return _window.WindowFrame.Show();
        }

        /// <summary>Same order as a VS frame closing: <see cref="Closed"/> fires while the transcript is
        /// still parented here, then the tab goes.</summary>
        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsClosed)
            {
                return;
            }

            IsClosed = true;
            Closed?.Invoke(this, EventArgs.Empty);
            _view.RemoveTab(Item);
            _window.Forget(this);
        }

        public IVsWindowFrame WindowFrame => IsClosed ? null : _window.WindowFrame;
    }
}
