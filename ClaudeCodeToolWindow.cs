/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Tool window class for the Claude Code extension for VS.NET
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS
{
    [Guid("87654321-4321-4321-4321-987654321cba")]
    public class ClaudeCodeToolWindow : ToolWindowPane, IVsWindowFrameNotify
    {
        private ClaudeCodeControl claudeCodeControl;
        private uint _notifyCookie;

        /// <summary>
        /// Fired when the tool window's show state changes (tab activated, shown, hidden, etc.)
        /// The int parameter is the raw __FRAMESHOW value.
        /// </summary>
        public event EventHandler<int> FrameShow;

        public ClaudeCodeToolWindow() : base(null)
        {
            this.Caption = "Claude Code";
            this.BitmapImageMoniker = KnownMonikers.Console;
            claudeCodeControl = new ClaudeCodeControl();
            this.Content = claudeCodeControl;

            // Set up the reference so the control can update the title
            claudeCodeControl.SetToolWindow(this);
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

        public void UpdateTitle(string providerName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.Caption = $"{providerName}";
        }

        #region IVsWindowFrameNotify Implementation

        public int OnShow(int fShow)
        {
            FrameShow?.Invoke(this, fShow);
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
    }
}
