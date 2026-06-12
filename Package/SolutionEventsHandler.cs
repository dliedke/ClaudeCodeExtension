/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Solution events handler for detecting solution and project changes
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Handles Visual Studio solution events to detect when solutions/projects are opened or closed
    /// Triggers terminal restart when the workspace directory changes
    /// </summary>
    public class SolutionEventsHandler : IVsSolutionEvents
    {
        #region Fields

        /// <summary>
        /// Reference to the main control for callback
        /// </summary>
        private readonly ClaudeCodeControl _control;

        /// <summary>
        /// Monotonic request id used to debounce solution/project-open bursts.
        /// </summary>
        private int _workspaceRefreshRequestId;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the SolutionEventsHandler class
        /// </summary>
        /// <param name="control">The ClaudeCodeControl instance to notify of changes</param>
        public SolutionEventsHandler(ClaudeCodeControl control)
        {
            _control = control;
        }

        #endregion

        #region Solution Event Handlers

        /// <summary>
        /// Queues one workspace refresh after Visual Studio finishes its burst of solution/project events.
        /// </summary>
        private void QueueWorkspaceRefresh(bool forceDiffReset)
        {
            int requestId = Interlocked.Increment(ref _workspaceRefreshRequestId);

#pragma warning disable VSSDK007, VSTHRD110 // Fire-and-forget to avoid blocking the UI thread during solution load
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await Task.Delay(900);
                if (requestId != Volatile.Read(ref _workspaceRefreshRequestId))
                {
                    return;
                }

                await _control.OnWorkspaceDirectoryChangedAsync(forceDiffReset);
            });
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Called after a solution is opened
        /// </summary>
        /// <param name="pUnkReserved">Reserved for future use</param>
        /// <param name="fNewSolution">True if this is a new solution being created</param>
        /// <returns>S_OK if successful</returns>
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            QueueWorkspaceRefresh(true);
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Called after a project is opened or added to the solution
        /// </summary>
        /// <param name="pHierarchy">The project hierarchy</param>
        /// <param name="fAdded">True if the project was added to an existing solution</param>
        /// <returns>S_OK if successful</returns>
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            QueueWorkspaceRefresh(true);
            return VSConstants.S_OK;
        }

        #endregion

        #region Unused Event Handlers (Required by Interface)

        /// <summary>
        /// Called after a solution is closed
        /// </summary>
        public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        /// <summary>
        /// Called after a project is loaded
        /// </summary>
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

        /// <summary>
        /// Called after a project is unloaded
        /// </summary>
        public int OnAfterUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

        /// <summary>
        /// Called before a project is closed
        /// </summary>
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;

        /// <summary>
        /// Called before a solution is closed. Stops the agent-finish watcher and clears any
        /// pending notification up front, so its console-attach tick can't run while the old
        /// terminal is torn down and the next one is launched.
        /// </summary>
        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            try
            {
                Interlocked.Increment(ref _workspaceRefreshRequestId);
                ThreadHelper.ThrowIfNotOnUIThread();
                _control?.ResetAgentCompletionWatcher();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnBeforeCloseSolution error: {ex.Message}");
            }
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Called before a project is unloaded
        /// </summary>
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

        /// <summary>
        /// Called when querying whether a project can be closed
        /// </summary>
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

        /// <summary>
        /// Called when querying whether a solution can be closed
        /// </summary>
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

        /// <summary>
        /// Called when querying whether a project can be unloaded
        /// </summary>
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

        #endregion
    }
}
