/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
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
                        Debug.WriteLine("Solution events registered successfully");
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
        public async Task OnWorkspaceDirectoryChangedAsync()
        {
            try
            {
                string newWorkspaceDir = await GetWorkspaceDirectoryAsync();
                Debug.WriteLine($"OnWorkspaceDirectoryChangedAsync: Current workspace: '{_lastWorkspaceDirectory}', New workspace: '{newWorkspaceDir}', Initialized: {_hasInitialized}");

                // Only restart terminal if we have already initialized
                if (!_hasInitialized)
                {
                    Debug.WriteLine("Extension not yet initialized, skipping terminal restart");
                    return;
                }

                // Only restart if the directory actually changed
                if (_lastWorkspaceDirectory != newWorkspaceDir)
                {
                    Debug.WriteLine($"Workspace directory changed from '{_lastWorkspaceDirectory}' to '{newWorkspaceDir}'");
                    _lastWorkspaceDirectory = newWorkspaceDir;

                    Debug.WriteLine("Restarting terminal for new workspace...");

                    // Determine which provider to use based on settings
                    bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                    bool useCursorAgent = _settings?.SelectedProvider == AiProvider.CursorAgent;
                    bool claudeAvailable = false;
                    bool codexAvailable = false;
                    bool wslAvailable = false;

                    Debug.WriteLine($"User selected provider: {(useCursorAgent ? "Cursor Agent" : useCodex ? "Codex" : "Claude Code")}");

                    if (useCursorAgent)
                    {
                        Debug.WriteLine("Checking WSL and cursor-agent availability for workspace change...");
                        wslAvailable = await IsWslInstalledAsync();
                        if (wslAvailable)
                        {
                            wslAvailable = await IsCursorAgentInstalledInWslAsync();
                        }
                        Debug.WriteLine($"WSL available and cursor-agent installed: {wslAvailable}");
                    }
                    else if (useCodex)
                    {
                        Debug.WriteLine("Checking Codex availability for workspace change...");
                        codexAvailable = await IsCodexCmdAvailableAsync();
                        Debug.WriteLine($"Codex available: {codexAvailable}");
                    }
                    else
                    {
                        Debug.WriteLine("Checking Claude Code availability for workspace change...");
                        claudeAvailable = await IsClaudeCmdAvailableAsync();
                        Debug.WriteLine($"Claude available: {claudeAvailable}");
                    }

                    // Switch to main thread for UI operations
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Restart with the selected provider if available, otherwise show message and use regular CMD
                    if (useCursorAgent)
                    {
                        if (wslAvailable)
                        {
                            Debug.WriteLine("Restarting Cursor Agent terminal in new directory...");
                            await StartEmbeddedTerminalAsync(false, false, true); // Cursor Agent
                        }
                        else
                        {
                            Debug.WriteLine("WSL not available, showing installation instructions...");
                            if (!_cursorAgentNotificationShown)
                            {
                                _cursorAgentNotificationShown = true;
                                ShowCursorAgentInstallationInstructions();
                            }
                            await StartEmbeddedTerminalAsync(false, false, false); // Regular CMD
                        }
                    }
                    else if (useCodex)
                    {
                        if (codexAvailable)
                        {
                            Debug.WriteLine("Restarting Codex terminal in new directory...");
                            await StartEmbeddedTerminalAsync(false, true, false); // Codex
                        }
                        else
                        {
                            Debug.WriteLine("Codex not available, showing installation instructions...");
                            if (!_codexNotificationShown)
                            {
                                _codexNotificationShown = true;
                                ShowCodexInstallationInstructions();
                            }
                            await StartEmbeddedTerminalAsync(false, false, false); // Regular CMD
                        }
                    }
                    else
                    {
                        if (claudeAvailable)
                        {
                            Debug.WriteLine("Restarting Claude Code terminal in new directory...");
                            await StartEmbeddedTerminalAsync(true, false, false); // Claude Code
                        }
                        else
                        {
                            Debug.WriteLine("Claude Code not available, showing installation instructions...");
                            if (!_claudeNotificationShown)
                            {
                                _claudeNotificationShown = true;
                                ShowClaudeInstallationInstructions();
                            }
                            await StartEmbeddedTerminalAsync(false, false, false); // Regular CMD
                        }
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