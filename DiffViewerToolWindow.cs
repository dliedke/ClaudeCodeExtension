/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Tool window for displaying file diffs in Visual Studio
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Tool window for displaying file changes and diffs
    /// </summary>
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    public class DiffViewerToolWindow : ToolWindowPane, IVsWindowFrameNotify, IVsWindowFrameNotify2
    {
        private DiffViewerControl _diffViewerControl;
        private bool _isVisible;
        private uint _notifyCookie;


        /// <summary>
        /// Gets the diff viewer control
        /// </summary>
        public DiffViewerControl DiffViewerControl => _diffViewerControl;

        /// <summary>
        /// Gets whether the window is currently visible
        /// </summary>
        public bool IsWindowVisible => _isVisible;

        /// <summary>
        /// Fired when window visibility changes
        /// </summary>
        public event EventHandler<bool> VisibilityChanged;

        public DiffViewerToolWindow() : base(null)
        {
            this.Caption = "Code Changes";
            _diffViewerControl = new DiffViewerControl();
            this.Content = _diffViewerControl;
        }

        /// <summary>
        /// Called when the tool window is created - subscribe to frame notifications
        /// </summary>
        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();

            // Subscribe to window frame notifications
            if (Frame is IVsWindowFrame2 windowFrame2)
            {
                windowFrame2.Advise(this, out _notifyCookie);
            }

            // Check initial visibility
            if (Frame is IVsWindowFrame windowFrame)
            {
                _isVisible = windowFrame.IsVisible() == VSConstants.S_OK;
            }
        }

        /// <summary>
        /// Called when the window is closed
        /// </summary>
        protected override void OnClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Unsubscribe from notifications
            if (_notifyCookie != 0 && Frame is IVsWindowFrame2 windowFrame2)
            {
                windowFrame2.Unadvise(_notifyCookie);
                _notifyCookie = 0;
            }

            if (_isVisible)
            {
                _isVisible = false;
                VisibilityChanged?.Invoke(this, false);
            }

            base.OnClose();
        }



        #region IVsWindowFrameNotify Implementation

        public int OnShow(int fShow)
        {
            var frameShow = (__FRAMESHOW)fShow;
            bool nowVisible = frameShow == __FRAMESHOW.FRAMESHOW_WinShown ||
                              frameShow == __FRAMESHOW.FRAMESHOW_WinRestored ||
                              frameShow == __FRAMESHOW.FRAMESHOW_WinMaximized ||
                              frameShow == __FRAMESHOW.FRAMESHOW_TabActivated;

            bool nowHidden = frameShow == __FRAMESHOW.FRAMESHOW_WinHidden ||
                             frameShow == __FRAMESHOW.FRAMESHOW_WinMinimized ||
                             frameShow == __FRAMESHOW.FRAMESHOW_TabDeactivated;

            if (nowVisible && !_isVisible)
            {
                _isVisible = true;
                VisibilityChanged?.Invoke(this, true);
            }
            else if (nowHidden && _isVisible)
            {
                _isVisible = false;
                VisibilityChanged?.Invoke(this, false);
            }

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

        #region IVsWindowFrameNotify2 Implementation

        public int OnClose(ref uint pgrfSaveOptions)
        {
            if (_isVisible)
            {
                _isVisible = false;
                VisibilityChanged?.Invoke(this, false);
            }
            return VSConstants.S_OK;
        }

        #endregion
    }
}
