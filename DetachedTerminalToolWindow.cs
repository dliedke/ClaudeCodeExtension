/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Tool window for displaying the detached terminal in a separate VS tab
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Forms.Integration;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Tool window for displaying the detached terminal in a separate VS tab
    /// </summary>
    [Guid("B2C3D4E5-F6A7-8901-BCDE-FA2345678901")]
    public class DetachedTerminalToolWindow : ToolWindowPane, IVsWindowFrameNotify, IVsWindowFrameNotify2
    {
        private WindowsFormsHost _terminalHost;
        private System.Windows.Forms.Panel _terminalPanel;
        private bool _isVisible;
        private uint _notifyCookie;

        /// <summary>
        /// Gets the WinForms panel that hosts the terminal
        /// </summary>
        public System.Windows.Forms.Panel TerminalPanel => _terminalPanel;

        /// <summary>
        /// Gets the WindowsFormsHost element
        /// </summary>
        public WindowsFormsHost TerminalHost => _terminalHost;

        /// <summary>
        /// Gets whether the window is currently visible
        /// </summary>
        public bool IsWindowVisible => _isVisible;

        /// <summary>
        /// Fired when window visibility changes
        /// </summary>
        public event EventHandler<bool> VisibilityChanged;

        /// <summary>
        /// Fired when the window is closed by the user
        /// </summary>
        public event EventHandler Closed;

        public DetachedTerminalToolWindow() : base(null)
        {
            this.Caption = "Claude Code";

            _terminalPanel = new System.Windows.Forms.Panel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                BackColor = System.Drawing.Color.Black
            };

            _terminalHost = new WindowsFormsHost
            {
                Child = _terminalPanel
            };

            this.Content = _terminalHost;
        }

        /// <summary>
        /// Updates the tool window caption
        /// </summary>
        /// <param name="providerName">Name of the current AI provider</param>
        public void UpdateCaption(string providerName)
        {
            try
            {
                this.Caption = providerName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating detached terminal caption: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the terminal panel background color to match VS theme
        /// </summary>
        /// <param name="color">The background color</param>
        public void UpdateTheme(System.Drawing.Color color)
        {
            try
            {
                if (_terminalPanel != null)
                {
                    _terminalPanel.BackColor = color;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating detached terminal theme: {ex.Message}");
            }
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
        /// Note: Closed event is fired from IVsWindowFrameNotify2.OnClose only (not here)
        /// to avoid firing twice
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

            Closed?.Invoke(this, EventArgs.Empty);

            return VSConstants.S_OK;
        }

        #endregion
    }
}
