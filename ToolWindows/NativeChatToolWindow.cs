/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Document-area tab that hosts the native-mode chat transcript
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Hosts the native-mode conversation in the central document area, like an open file, so the
    /// chat gets the full editor width instead of the narrow tool-window strip.
    /// <para>
    /// The pane owns no UI of its own: <c>ClaudeCodeControl</c> re-parents its single
    /// <c>ChatTranscriptView</c> in and out of here, so the conversation survives the move.
    /// </para>
    /// </summary>
    [Guid("C3D4E5F6-A7B8-9012-CDEF-AB3456789012")]
    public class NativeChatToolWindow : ToolWindowPane, IVsWindowFrameNotify, IVsWindowFrameNotify2
    {
        private readonly Grid _host;
        private uint _notifyCookie;

        /// <summary>Fired when the user closes the tab, so the chat can go back into the panel.</summary>
        public event EventHandler Closed;

        public NativeChatToolWindow() : base(null)
        {
            this.Caption = "Claude Code Chat";
            this.BitmapImageMoniker = KnownMonikers.Comment;

            _host = new Grid();
            this.Content = _host;
        }

        /// <summary>
        /// Puts the transcript into the tab (or takes it out again when passed null). The caller is
        /// responsible for detaching the element from its previous parent first — a WPF element can
        /// only live in one visual tree.
        /// </summary>
        public void SetChatContent(UIElement content)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _host.Children.Clear();

            if (content != null)
            {
                _host.Children.Add(content);
            }
        }

        /// <summary>True when the transcript is currently parented here.</summary>
        public bool HasChatContent
        {
            get { return _host.Children.Count > 0; }
        }

        public void UpdateCaption(string caption)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                this.Caption = caption;
                if (Frame is IVsWindowFrame windowFrame)
                {
                    ErrorHandler.ThrowOnFailure(
                        windowFrame.SetProperty((int)__VSFPROPID.VSFPROPID_Caption, caption));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating native chat caption: {ex.Message}");
            }
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Frame is IVsWindowFrame2 windowFrame2)
            {
                windowFrame2.Advise(this, out _notifyCookie);
            }
        }

        protected override void OnClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_notifyCookie != 0 && Frame is IVsWindowFrame2 windowFrame2)
            {
                windowFrame2.Unadvise(_notifyCookie);
                _notifyCookie = 0;
            }

            base.OnClose();
        }

        #region IVsWindowFrameNotify

        public int OnShow(int fShow)
        {
            return VSConstants.S_OK;
        }

        public int OnMove()
        {
            return VSConstants.S_OK;
        }

        public int OnSize()
        {
            return VSConstants.S_OK;
        }

        public int OnDockableChange(int fDockable)
        {
            return VSConstants.S_OK;
        }

        #endregion

        #region IVsWindowFrameNotify2

        /// <summary>
        /// Closing the tab must not lose the conversation: the control moves the transcript back into
        /// the panel, where the running session keeps streaming into it.
        /// </summary>
        public int OnClose(ref uint pgrfSaveOptions)
        {
            Closed?.Invoke(this, EventArgs.Empty);

            return VSConstants.S_OK;
        }

        #endregion
    }
}
