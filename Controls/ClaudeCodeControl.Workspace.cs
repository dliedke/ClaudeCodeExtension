/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
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

        /// <summary>
        /// True while a workspace-change evaluation is running (UI-thread only).
        /// </summary>
        private bool _workspaceChangeInProgress;

        /// <summary>
        /// Set when a workspace-change request arrives while another is running; the
        /// in-flight evaluation re-runs once more when it finishes (UI-thread only).
        /// </summary>
        private bool _workspaceChangeRerunRequested;

        /// <summary>
        /// Accumulated forceDiffReset flag for the coalesced re-run (UI-thread only).
        /// </summary>
        private bool _workspaceChangeRerunForceDiffReset;

        /// <summary>
        /// Last workspace directory for which an automatic terminal launch was attempted.
        /// Used to keep delayed solution/project events from retrying the same failed launch
        /// repeatedly during one solution load.
        /// </summary>
        private string _lastTerminalLaunchWorkspaceDirectory;

        #endregion

        #region Workspace Initialization

        /// <summary>
        /// Sets up solution events to detect when solutions are opened/closed
        /// </summary>
        private void SetupSolutionEvents()
        {
            try
            {
#pragma warning disable VSSDK007 // fire-and-forget is intentional during control construction
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                    if (solution != null)
                    {
                        solutionEvents = new SolutionEventsHandler(this);
                        solution.AdviseSolutionEvents(solutionEvents, out solutionEventsCookie);
                    }
                });
#pragma warning restore VSSDK007
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up solution events: {ex.Message}");
            }
        }

        #endregion

        #region Workspace Directory Management

        /// <summary>
        /// Gets the current workspace directory (solution or project directory),
        /// applying the custom working directory setting if configured
        /// </summary>
        /// <returns>The workspace directory path, or My Documents as fallback</returns>
        private async Task<string> GetWorkspaceDirectoryAsync()
        {
            string baseDir = await GetBaseWorkspaceDirectoryAsync();

            // Apply custom working directory if configured
            if (_settings != null && !string.IsNullOrWhiteSpace(_settings.CustomWorkingDirectory))
            {
                try
                {
                    string customDir = _settings.CustomWorkingDirectory.Trim();

                    if (Path.IsPathRooted(customDir))
                    {
                        // Absolute path: use as-is if it exists
                        if (Directory.Exists(customDir))
                        {
                            return customDir;
                        }
                        else
                        {
                            Debug.WriteLine($"Custom working directory does not exist: {customDir}");
                        }
                    }
                    else
                    {
                        // Relative path: resolve against the base workspace directory
                        string resolved = Path.GetFullPath(Path.Combine(baseDir, customDir));
                        if (Directory.Exists(resolved))
                        {
                            return resolved;
                        }
                        else
                        {
                            Debug.WriteLine($"Custom working directory (resolved) does not exist: {resolved}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error resolving custom working directory: {ex.Message}");
                }
            }

            return baseDir;
        }

        /// <summary>
        /// Gets the base workspace directory from the solution or project, before applying custom directory overrides
        /// </summary>
        /// <returns>The base workspace directory path, or My Documents as fallback</returns>
        private async Task<string> GetBaseWorkspaceDirectoryAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Try to get solution directory from DTE
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution?.FullName != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    string solutionFullName = dte.Solution.FullName;

                    // For "Open Folder" mode (e.g. CMake projects), FullName may be a directory path
                    // rather than a .sln file. In that case, use it directly instead of calling
                    // GetDirectoryName which would incorrectly return the parent directory.
                    if (Directory.Exists(solutionFullName) && !File.Exists(solutionFullName))
                    {
                        return solutionFullName;
                    }

                    string solutionDir = Path.GetDirectoryName(solutionFullName);
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
                     Directory.GetFiles(currentDir, "*.vbproj").Length > 0 ||
                     File.Exists(Path.Combine(currentDir, "CMakeLists.txt"))))
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
        /// Normalizes a workspace path for stable comparisons across DTE/IVsSolution sources.
        /// </summary>
        private static string NormalizeWorkspaceDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }

            try
            {
                return Path.GetFullPath(directory.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Compares workspace paths using Windows path semantics.
        /// </summary>
        private static bool WorkspaceDirectoriesEqual(string left, string right)
        {
            return string.Equals(
                NormalizeWorkspaceDirectory(left),
                NormalizeWorkspaceDirectory(right),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Handles workspace directory changes (solution opened/closed)
        /// Restarts the terminal in the new workspace directory
        /// </summary>
        public async Task OnWorkspaceDirectoryChangedAsync(bool forceDiffReset = false)
        {
            // Solution load fires this in a burst: the solution-open event, one project-open
            // event per project, and the control's own startup initialization. Each overlapping
            // call used to queue its own terminal launch, and every queued launch tore down the
            // terminal the previous one had just embedded. That stop/relaunch churn of fresh WSL
            // sessions is what creates the teardown contention behind the blank-panel failures
            // (issue #73). Coalesce instead: one evaluation runs at a time, and a burst collapses
            // into a single trailing re-run that sees the terminal already in the right directory
            // and leaves it alone. All flag access happens on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_workspaceChangeInProgress)
            {
                _workspaceChangeRerunRequested = true;
                _workspaceChangeRerunForceDiffReset |= forceDiffReset;
                return;
            }

            _workspaceChangeInProgress = true;
            try
            {
                do
                {
                    _workspaceChangeRerunRequested = false;
                    forceDiffReset |= _workspaceChangeRerunForceDiffReset;
                    _workspaceChangeRerunForceDiffReset = false;

                    await HandleWorkspaceDirectoryChangedAsync(forceDiffReset);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                } while (_workspaceChangeRerunRequested);
            }
            finally
            {
                _workspaceChangeInProgress = false;
            }
        }

        /// <summary>
        /// Single workspace-change evaluation; only called from the coalescing wrapper above.
        /// </summary>
        private async Task HandleWorkspaceDirectoryChangedAsync(bool forceDiffReset)
        {
            try
            {
                string newWorkspaceDir = NormalizeWorkspaceDirectory(await GetWorkspaceDirectoryAsync());
                bool workspaceChanged = !WorkspaceDirectoriesEqual(_lastWorkspaceDirectory, newWorkspaceDir);
                bool resetDiff = forceDiffReset || workspaceChanged;

                // Update View Changes button visibility based on git availability
                await UpdateViewChangesButtonVisibilityAsync();

                // If terminal hasn't been initialized yet, initialize it now
                if (!HasTerminalLaunchState())
                {
                    if (WorkspaceDirectoriesEqual(_lastTerminalLaunchWorkspaceDirectory, newWorkspaceDir))
                    {
                        LogTerminalLaunch($"skipping repeated automatic terminal launch for workspace={newWorkspaceDir}");
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

                    _lastWorkspaceDirectory = newWorkspaceDir;
                    await InitializeTerminalAsync();

                    // Auto-restore detached state from previous session
                    if (_settings?.IsTerminalDetached == true && !_isTerminalDetached)
                    {
                        await DetachTerminalAsync();
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
                    }
                    return;
                }

                // Only restart if the directory actually changed
                if (workspaceChanged)
                {
                    _lastWorkspaceDirectory = newWorkspaceDir;

                    // Stop the agent-finish watcher and clear any stale notification before
                    // the terminal restarts. Leaving the watcher running lets its console-attach
                    // tick overlap the new terminal launch and break the embedded cmd.
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ResetAgentCompletionWatcher();


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

                        case AiProvider.CursorAgentNative:
                            providerAvailable = await IsCursorAgentNativeAvailableAsync();
                            break;

                        case AiProvider.CodexNative:
                            providerAvailable = await IsCodexNativeAvailableAsync();
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

                        case AiProvider.OpenCode:
                            providerAvailable = await IsOpenCodeAvailableAsync();
                            break;
                        case AiProvider.Devin:
                            bool wslInstalledForDevin = await IsWslInstalledAsync();
                            if (wslInstalledForDevin)
                            {
                                providerAvailable = await IsDevinAvailableAsync();
                            }
                            break;
                        case AiProvider.Pi:
                            providerAvailable = await IsPiAvailableAsync();
                            break;
                        case AiProvider.Antigravity:
                            providerAvailable = await IsAntigravityAvailableAsync();
                            break;
                        case AiProvider.Reasonix:
                            providerAvailable = await IsReasonixAvailableAsync();
                            break;
                        case AiProvider.DevinNative:
                            providerAvailable = await IsDevinNativeAvailableAsync();
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
                            case AiProvider.CursorAgentNative:
                                if (!_cursorAgentNativeNotificationShown)
                                {
                                    _cursorAgentNativeNotificationShown = true;
                                    ShowCursorAgentNativeInstallationInstructions();
                                }
                                break;
                            case AiProvider.CodexNative:
                                if (!_codexNativeNotificationShown)
                                {
                                    _codexNativeNotificationShown = true;
                                    ShowCodexNativeInstallationInstructions();
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
                            case AiProvider.OpenCode:
                                if (!_openCodeNotificationShown)
                                {
                                    _openCodeNotificationShown = true;
                                    ShowOpenCodeInstallationInstructions();
                                }
                                break;
                            case AiProvider.Devin:
                                if (!_devinNotificationShown)
                                {
                                    _devinNotificationShown = true;
                                    ShowDevinInstallationInstructions();
                                }
                                break;
                            case AiProvider.Pi:
                                if (!_piNotificationShown)
                                {
                                    _piNotificationShown = true;
                                    ShowPiInstallationInstructions();
                                }
                                break;
                            case AiProvider.Antigravity:
                                if (!_antigravityNotificationShown)
                                {
                                    _antigravityNotificationShown = true;
                                    ShowAntigravityInstallationInstructions();
                                }
                                break;
                            case AiProvider.Reasonix:
                                if (!_reasonixNotificationShown)
                                {
                                    _reasonixNotificationShown = true;
                                    ShowReasonixInstallationInstructions();
                                }
                                break;
                            case AiProvider.DevinNative:
                                if (!_devinNativeNotificationShown)
                                {
                                    _devinNativeNotificationShown = true;
                                    ShowDevinNativeInstallationInstructions();
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

                // Refresh terminal layout after solution/project changes to fix
                // visual corruption caused by VS re-layout during solution load
                if (forceDiffReset && terminalHandle != IntPtr.Zero)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SchedulePostSolutionLoadTerminalRefresh();
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
