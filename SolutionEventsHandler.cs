/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Solution events handler for detecting solution and project changes
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
        /// Called after a solution is opened
        /// </summary>
        /// <param name="pUnkReserved">Reserved for future use</param>
        /// <param name="fNewSolution">True if this is a new solution being created</param>
        /// <returns>S_OK if successful</returns>
        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            Debug.WriteLine($"Solution opened - fNewSolution: {fNewSolution}, checking if terminal needs to restart");
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                // Add small delay to ensure solution is fully loaded
                await Task.Delay(500);
                await _control.OnWorkspaceDirectoryChangedAsync();
            });
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
            Debug.WriteLine($"Project opened - fAdded: {fAdded}, checking if terminal needs to restart");
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                // Add small delay to ensure project is fully loaded
                await Task.Delay(300);
                await _control.OnWorkspaceDirectoryChangedAsync();
            });
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
        /// Called before a solution is closed
        /// </summary>
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

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