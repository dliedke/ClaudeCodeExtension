/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Tool window hosting an embedded claude.ai/settings/usage page in a WebView2 control
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
    /// <summary>
    /// Tool window that embeds claude.ai/settings/usage so the user can monitor
    /// session/weekly limits without leaving Visual Studio.
    /// </summary>
    [Guid("C3D4E5F6-A7B8-9012-CDEF-123456789AB1")]
    public class ClaudeUsageToolWindow : ToolWindowPane, IVsWindowFrameNotify, IVsWindowFrameNotify2
    {
        private readonly ClaudeUsageControl _control;
        private bool _isVisible;
        private bool _closedByUserRaised;
        private uint _notifyCookie;
        private bool _closeIsIntentional;

        public ClaudeUsageControl UsageControl => _control;
        public bool IsWindowVisible => _isVisible;

        public event EventHandler<bool> VisibilityChanged;
        public event EventHandler ClosedByUser;

        /// <summary>
        /// Destroys the tool window for real (WebView2 disposed, window removed).
        /// Used by the toolbar toggle button. X-button closes are intercepted and
        /// converted to <see cref="IVsWindowFrame.Hide"/> so the scraper keeps running.
        /// </summary>
        public void ForceClose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _closeIsIntentional = true;
            if (Frame is IVsWindowFrame frame)
                frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        public ClaudeUsageToolWindow() : base(null)
        {
            this.Caption = "Claude Usage";
            this.BitmapImageMoniker = KnownMonikers.BarChart;
            _control = new ClaudeUsageControl();
            this.Content = _control;
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Frame is IVsWindowFrame2 windowFrame2)
            {
                windowFrame2.Advise(this, out _notifyCookie);
            }

            if (Frame is IVsWindowFrame windowFrame)
            {
                _isVisible = windowFrame.IsVisible() == VSConstants.S_OK;
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

            try { _control?.Cleanup(); } catch { }

            if (_isVisible)
            {
                _isVisible = false;
                VisibilityChanged?.Invoke(this, false);
            }

#pragma warning disable VSTHRD010
            RaiseClosedByUserIfNeeded();
#pragma warning restore VSTHRD010
            base.OnClose();
        }

        #region IVsWindowFrameNotify

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
                _closedByUserRaised = false; // Allow ClosedByUser to fire again on the next close
                _control?.OnWindowBecameVisible(); // Prime WebView2 cursor if created while hidden
                VisibilityChanged?.Invoke(this, true);
            }
            else if (nowHidden && _isVisible)
            {
                _isVisible = false;
                VisibilityChanged?.Invoke(this, false);
            }

            return VSConstants.S_OK;
        }

        public int OnMove() => VSConstants.S_OK;
        public int OnSize() => VSConstants.S_OK;
        public int OnDockableChange(int fDockable) => VSConstants.S_OK;

        #endregion

        #region IVsWindowFrameNotify2

        public int OnClose(ref uint pgrfSaveOptions)
        {
#pragma warning disable VSTHRD010 // IVsWindowFrameNotify2.OnClose is called on the UI thread by VS
            // Real close from ForceClose() or VS shutdown — let it proceed
            if (_closeIsIntentional || IsVisualStudioShuttingDown())
            {
                _closeIsIntentional = false;
                if (_isVisible)
                {
                    _isVisible = false;
                    VisibilityChanged?.Invoke(this, false);
                }
                RaiseClosedByUserIfNeeded();
                return VSConstants.S_OK;
            }

            // X-button close: hide instead of destroy so WebView2 scraper keeps running.
            // Return E_ABORT to cancel the actual frame destruction.
            if (_isVisible)
            {
                _isVisible = false;
                VisibilityChanged?.Invoke(this, false);
            }
            _closedByUserRaised = false; // Allow re-firing on future closes
            RaiseClosedByUserIfNeeded();
            if (Frame is IVsWindowFrame frame)
            {
                try { frame.Hide(); } catch { }
            }
            return VSConstants.E_ABORT;
#pragma warning restore VSTHRD010
        }

        #endregion

        private void RaiseClosedByUserIfNeeded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_closedByUserRaised || IsVisualStudioShuttingDown())
            {
                return;
            }

            _closedByUserRaised = true;
            ClosedByUser?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsVisualStudioShuttingDown()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var shell = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell;
                if (shell == null)
                {
                    return false;
                }

                if (shell.GetProperty((int)__VSSPROPID6.VSSPROPID_ShutdownStarted, out object shutdownStarted) == VSConstants.S_OK &&
                    shutdownStarted is bool isShuttingDown &&
                    isShuttingDown)
                {
                    return true;
                }

                if (shell.GetProperty((int)__VSSPROPID.VSSPROPID_Zombie, out object isZombie) == VSConstants.S_OK &&
                    isZombie is bool zombie &&
                    zombie)
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
