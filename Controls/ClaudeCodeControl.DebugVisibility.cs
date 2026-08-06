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
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
        /// Fires every time the debuggee starts or resumes running (F5, Continue after a breakpoint).
        /// Re-asserts visibility on the main tool window and every tab it created, undoing whatever
        /// VS's layout/auto-hide logic did while focus moved to the running program.
        /// </summary>
        private void OnDebugEnterRunMode(EnvDTE.dbgEventReason reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

#pragma warning disable VSSDK007, VSTHRD110
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowExtensionAndCreatedTabsAsync();
            });
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Shows the main Claude Code tool window (creating it if it isn't open yet, so it's always
        /// available while debugging) plus every tab it has spun off: the detached terminal tab and
        /// every native-mode chat tab, including parallel sessions. Uses ShowNoActivate so the
        /// currently focused editor/document keeps input focus - only visibility is restored, the
        /// user isn't yanked away from what they were doing.
        /// </summary>
        private async Task ShowExtensionAndCreatedTabsAsync()
        {
            try
            {
                var package = await GetPackageAsync();
                if (package == null) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var mainWindow = package.FindToolWindow(typeof(ClaudeCodeToolWindow), 0, true);
                if (mainWindow?.Frame is IVsWindowFrame mainFrame)
                {
                    mainFrame.ShowNoActivate();
                }

                if (_isTerminalDetached && _detachedTerminalWindow?.Frame is IVsWindowFrame detachedFrame)
                {
                    detachedFrame.ShowNoActivate();
                }

                if (_nativeChatWindow?.Frame is IVsWindowFrame chatFrame)
                {
                    chatFrame.ShowNoActivate();
                }

                foreach (var pair in _nativeSessions)
                {
                    if (pair.Value?.Window?.Frame is IVsWindowFrame sessionFrame)
                    {
                        sessionFrame.ShowNoActivate();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowExtensionAndCreatedTabsAsync error: {ex.Message}");
            }
        }

        #endregion
    }
}
