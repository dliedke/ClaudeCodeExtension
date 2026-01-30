/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Workspace and solution directory management
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Workspace Fields

        /// <summary>
        /// Solution events handler for detecting workspace changes
        /// </summary>
        private IVsSolutionEvents solutionEvents;

        /// <summary>
        /// Cookie for solution events registration
        /// </summary>
        private uint solutionEventsCookie;

        /// <summary>
        /// Last known workspace directory path
        /// </summary>
        private string _lastWorkspaceDirectory;

        #endregion

        #region Workspace Initialization

        /// <summary>
        /// Sets up solution events to detect when solutions are opened/closed
        /// </summary>
        private void SetupSolutionEvents()
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                    if (solution != null)
                    {
                        solutionEvents = new SolutionEventsHandler(this);
                        solution.AdviseSolutionEvents(solutionEvents, out solutionEventsCookie);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up solution events: {ex.Message}");
            }
        }

        #endregion

        #region Workspace Directory Management

        /// <summary>
        /// Gets the current workspace directory (solution or project directory)
        /// </summary>
        /// <returns>The workspace directory path, or My Documents as fallback</returns>
        private async Task<string> GetWorkspaceDirectoryAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Try to get solution directory from DTE
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution?.FullName != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    string solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                    if (Directory.Exists(solutionDir))
                    {
                        return solutionDir;
                    }
                }

                // Try to get active project directory
                if (dte?.ActiveDocument?.ProjectItem?.ContainingProject?.FullName != null)
                {
                    string projectDir = Path.GetDirectoryName(dte.ActiveDocument.ProjectItem.ContainingProject.FullName);
                    if (Directory.Exists(projectDir))
                    {
                        return projectDir;
                    }
                }

                // Try to get solution directory from IVsSolution
                var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                if (solution != null)
                {
                    solution.GetSolutionInfo(out string solutionDir, out string solutionFile, out string userOptsFile);
                    if (!string.IsNullOrEmpty(solutionDir) && Directory.Exists(solutionDir))
                    {
                        return solutionDir;
                    }
                }

                // Check if current directory contains solution or project files
                string currentDir = Environment.CurrentDirectory;
                if (Directory.Exists(currentDir) &&
                    (Directory.GetFiles(currentDir, "*.sln").Length > 0 ||
                     Directory.GetFiles(currentDir, "*.csproj").Length > 0 ||
                     Directory.GetFiles(currentDir, "*.vbproj").Length > 0))
                {
                    return currentDir;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting workspace directory: {ex.Message}");
            }

            // Fallback to My Documents
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        /// <summary>
        /// Handles workspace directory changes (solution opened/closed)
        /// Restarts the terminal in the new workspace directory
        /// </summary>
        public async Task OnWorkspaceDirectoryChangedAsync(bool forceDiffReset = false)
        {
            try
            {
                string newWorkspaceDir = await GetWorkspaceDirectoryAsync();
                bool workspaceChanged = _lastWorkspaceDirectory != newWorkspaceDir;
                bool resetDiff = forceDiffReset || workspaceChanged;

                // Update View Changes button visibility based on git availability
                await UpdateViewChangesButtonVisibilityAsync();

                // If terminal hasn't been initialized yet, initialize it now
                if (cmdProcess == null)
                {
                    _lastWorkspaceDirectory = newWorkspaceDir;
                    await InitializeTerminalAsync();
                    if (resetDiff)
                    {
                        bool refreshView = _diffViewerWindow != null;
                        if (!refreshView)
                        {
                            await EnsureDiffViewerWindowAsync(false);
                            refreshView = _diffViewerWindow != null;
                        }
                        await ResetDiffBaselineAsync(refreshView, false, false, true, newWorkspaceDir, true);
                    }
                    else
                    {
                        await EnsureDiffTrackingStartedAsync(false);
                    }
                    return;
                }

                // Only restart if the directory actually changed
                if (workspaceChanged)
                {
                    _lastWorkspaceDirectory = newWorkspaceDir;


                    // Get the selected provider from settings
                    AiProvider? selectedProvider = _settings?.SelectedProvider;
                    bool providerAvailable = false;


                    // Check if the selected provider is available
                    switch (selectedProvider)
                    {
                        case AiProvider.CursorAgent:
                            bool wslAvailable = await IsWslInstalledAsync();
                            if (wslAvailable)
                            {
                                providerAvailable = await IsCursorAgentInstalledInWslAsync();
                            }
                            break;

                        case AiProvider.Codex:
                            providerAvailable = await IsCodexCmdAvailableAsync();
                            break;

                        case AiProvider.ClaudeCodeWSL:
                            providerAvailable = await IsClaudeCodeWSLAvailableAsync();
                            break;

                        case AiProvider.ClaudeCode:
                            providerAvailable = await IsClaudeCmdAvailableAsync();
                            break;
                    }

                    // Switch to main thread for UI operations
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Restart with the selected provider if available, otherwise show message and use regular CMD
                    if (providerAvailable)
                    {
                        await StartEmbeddedTerminalAsync(selectedProvider);
                    }
                    else
                    {

                        // Show installation instructions if not already shown
                        switch (selectedProvider)
                        {
                            case AiProvider.CursorAgent:
                                if (!_cursorAgentNotificationShown)
                                {
                                    _cursorAgentNotificationShown = true;
                                    ShowCursorAgentInstallationInstructions();
                                }
                                break;
                            case AiProvider.Codex:
                                if (!_codexNotificationShown)
                                {
                                    _codexNotificationShown = true;
                                    ShowCodexInstallationInstructions();
                                }
                                break;
                            case AiProvider.ClaudeCodeWSL:
                                if (!_claudeCodeWSLNotificationShown)
                                {
                                    _claudeCodeWSLNotificationShown = true;
                                    ShowClaudeCodeWSLInstallationInstructions();
                                }
                                break;
                            case AiProvider.ClaudeCode:
                                if (!_claudeNotificationShown)
                                {
                                    _claudeNotificationShown = true;
                                    ShowClaudeInstallationInstructions();
                                }
                                break;
                        }

                        await StartEmbeddedTerminalAsync(null); // Regular CMD
                    }
                }

                if (resetDiff)
                {
                    bool refreshView = _diffViewerWindow != null;
                    if (!refreshView)
                    {
                        await EnsureDiffViewerWindowAsync(false);
                        refreshView = _diffViewerWindow != null;
                    }
                    await ResetDiffBaselineAsync(refreshView, false, false, true, newWorkspaceDir, true);
                }
                else
                {
                    await EnsureDiffTrackingStartedAsync(false);
                    if (_diffViewerWindow != null)
                    {
                        await RefreshDiffViewAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling workspace directory change: {ex.Message}");
            }
        }

        #endregion
    }
}
