/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Keeps the Claude Code tool window - and any tab it has spun off (detached terminal,
 *          native-mode chat tabs) - visible while the user's project is running under the debugger
 *          (issue #130: the panel was getting hidden by VS's own layout/auto-hide behavior while
 *          debugging, leaving the user with no way to interact with it or type into it).
 *
 *          Two guards keep that from fighting the user's own layout choices (issue #141: an
 *          auto-hidden panel slid itself open on every single debug step):
 *            1. The restore runs once per debug session, not on every run-mode transition, so
 *               stepping and Continue never re-trigger it.
 *            2. Frames that VS still considers open are left completely alone. A panel that is
 *               collapsed to the side (auto-hide) or parked behind another tab is open as far as
 *               VS is concerned - the user put it there on purpose, so it stays there.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Fields

        /// <summary>
        /// VS debugger-event sink. Held in a field so the COM event subscription isn't garbage
        /// collected for the lifetime of the control (DTE event objects are otherwise collectible).
        /// </summary>
        private EnvDTE.DebuggerEvents _debugVisibilityEvents;

        /// <summary>
        /// True once visibility has been re-asserted for the debug session currently in progress.
        /// Reset when the debugger goes back to design mode, so the next F5 gets one restore and
        /// stepping/Continue inside a session get none (issue #141).
        /// </summary>
        private bool _debugVisibilityHandledForSession;

        /// <summary>
        /// Delay before the second (and last) restore pass of a debug session. VS can apply its
        /// debug window layout slightly after the debugger reports run mode, so a single immediate
        /// pass isn't always enough to undo it.
        /// </summary>
        private const int DebugVisibilityRecheckDelayMs = 700;

        #endregion

        #region Subscribe / Unsubscribe

        /// <summary>
        /// Subscribes to Visual Studio debugger run-mode events (once). Always on - unlike the
        /// auto-send features, keeping the extension visible while debugging isn't something a user
        /// would want to opt out of.
        /// </summary>
        private void InitializeDebugVisibility()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_debugVisibilityEvents != null) return;

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Events == null) return;

                _debugVisibilityEvents = dte.Events.DebuggerEvents;
                _debugVisibilityEvents.OnEnterRunMode += OnDebugEnterRunMode;
                _debugVisibilityEvents.OnEnterDesignMode += OnDebugEnterDesignMode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeDebugVisibility error: {ex.Message}");
            }
        }

        /// <summary>Unsubscribes from debugger events. Called during control cleanup.</summary>
        private void DisposeDebugVisibility()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_debugVisibilityEvents != null)
                {
                    _debugVisibilityEvents.OnEnterRunMode -= OnDebugEnterRunMode;
                    _debugVisibilityEvents.OnEnterDesignMode -= OnDebugEnterDesignMode;
                    _debugVisibilityEvents = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DisposeDebugVisibility error: {ex.Message}");
            }
        }

        #endregion

        #region Run-mode handling

        /// <summary>
        /// Fires every time the debuggee starts or resumes running (F5, Continue after a breakpoint,
        /// and once per step). Only the first one of a session does anything: it undoes whatever VS's
        /// own layout logic did to the panel when the debugger started. Later ones are ignored, so
        /// stepping doesn't keep poking at the window layout (issue #141).
        /// </summary>
        private void OnDebugEnterRunMode(EnvDTE.dbgEventReason reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_debugVisibilityHandledForSession) return;
            _debugVisibilityHandledForSession = true;

#pragma warning disable VSSDK007, VSTHRD110
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowExtensionAndCreatedTabsAsync();

                // VS may apply its debug layout just after run mode is reported - one more pass
                // catches that. Still bounded to two passes per session, so nothing flickers.
                await Task.Delay(DebugVisibilityRecheckDelayMs);
                await ShowExtensionAndCreatedTabsAsync();
            });
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Fires when the debug session ends and VS returns to design mode. Re-arms the one-shot
        /// restore for the next session.
        /// </summary>
        private void OnDebugEnterDesignMode(EnvDTE.dbgEventReason reason)
        {
            _debugVisibilityHandledForSession = false;
        }

        /// <summary>
        /// Restores the main Claude Code tool window plus every tab it has spun off (the detached
        /// terminal tab and every native-mode chat tab, parallel sessions included) if - and only if -
        /// VS closed/hid the frame. Uses ShowNoActivate so the currently focused editor/document keeps
        /// input focus: only visibility is restored, the user isn't yanked away from what they were doing.
        /// </summary>
        private async Task ShowExtensionAndCreatedTabsAsync()
        {
            try
            {
                var package = await GetPackageAsync();
                if (package == null) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Don't create the window: if the user never opened the panel, or closed it before
                // starting the debug session, debugging is no reason to conjure it back up.
                var mainWindow = package.FindToolWindow(typeof(ClaudeCodeToolWindow), 0, false);
                RestoreFrameIfHiddenByVS(mainWindow?.Frame as IVsWindowFrame);

                if (_isTerminalDetached)
                {
                    RestoreFrameIfHiddenByVS(_detachedTerminalWindow?.Frame as IVsWindowFrame);
                }

                RestoreFrameIfHiddenByVS(_nativeChatWindow?.Frame as IVsWindowFrame);

                foreach (var pair in _nativeSessions)
                {
                    RestoreFrameIfHiddenByVS(pair.Value?.Window?.Frame as IVsWindowFrame);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowExtensionAndCreatedTabsAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows a frame only when VS is holding it closed/hidden. A frame VS reports as visible is
        /// left untouched even if it isn't on screen right now - that means the user auto-hid it
        /// (collapsed to the side) or left it behind another tab, and sliding it open on their behalf
        /// is exactly the behavior issue #141 reported.
        /// </summary>
        private static void RestoreFrameIfHiddenByVS(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (frame == null) return;

            try
            {
                if (frame.IsVisible() == VSConstants.S_OK) return;

                frame.ShowNoActivate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestoreFrameIfHiddenByVS error: {ex.Message}");
            }
        }

        #endregion
    }
}
