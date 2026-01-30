/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Diff viewer integration - file change tracking and diff window management
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Fields

        /// <summary>
        /// Tracks file changes in the workspace
        /// </summary>
        private FileChangeTracker _fileChangeTracker;

        /// <summary>
        /// Reference to the diff viewer tool window
        /// </summary>
        private DiffViewerToolWindow _diffViewerWindow;

        /// <summary>
        /// Flag to track if diff tracking is currently active
        /// </summary>
        private bool _isDiffTrackingActive;

        /// <summary>
        /// Guard to prevent auto-reset recursion
        /// </summary>
        private bool _isAutoResetting;

        /// <summary>
        /// Prevents repeated git status checks in a short time window
        /// </summary>
        private DateTime _lastGitStatusCheckUtc = DateTime.MinValue;

        /// <summary>
        /// Last repository root used for git status checks
        /// </summary>
        private string _lastGitStatusRepoRoot;

        /// <summary>
        /// Cached clean state result from the last git status check
        /// </summary>
        private bool _lastGitStatusClean;

        /// <summary>
        /// Throttle window for git status checks in milliseconds
        /// </summary>
        private const int GitStatusThrottleMs = 5000;

        /// <summary>
        /// Timeout for git status command in milliseconds
        /// </summary>
        private const int GitStatusTimeoutMs = 8000;
        private const int GitShowTimeoutMs = 4000;
        private const int MaxGitFileBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Tracks if reset handler is already wired
        /// </summary>
        private bool _diffViewerResetSubscribed;

        /// <summary>
        /// Tracks if visibility handler is already wired
        /// </summary>
        private bool _diffViewerVisibilitySubscribed;

        /// <summary>
        /// Periodic poll timer to detect changes via git status
        /// </summary>
        private DispatcherTimer _gitStatusPollTimer;

        /// <summary>
        /// Poll interval for git status checks in milliseconds (used instead of FileSystemWatcher for git repos)
        /// </summary>
        private const int GitStatusPollIntervalMs = 3000;

        #endregion

        #region Diff Tracking Methods

        /// <summary>
        /// Initializes the file change tracker
        /// </summary>
        private void InitializeDiffTracking()
        {
            if (_fileChangeTracker == null)
            {
                _fileChangeTracker = new FileChangeTracker();
            }
        }

        /// <summary>
        /// Ensures diff tracking is active for the current workspace (git repos only)
        /// </summary>
        private async Task EnsureDiffTrackingStartedAsync(bool openWindow)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string workspaceDir = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrEmpty(workspaceDir))
                {
                    return;
                }

                // Only support git repositories
                string repoRoot = FindGitRepositoryRoot(workspaceDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    return;
                }

                InitializeDiffTracking();

                if (!_isDiffTrackingActive)
                {
                    // Git repo: apply git baseline (reads only changed files from git)
                    string repoRootCopy = repoRoot;
                    await System.Threading.Tasks.Task.Run(() => TryApplyGitBaseline(repoRootCopy));

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _isDiffTrackingActive = true;
                }

                // Ensure tracking is active if window is visible
                if (_diffViewerWindow != null && _diffViewerWindow.IsWindowVisible)
                {
                    EnsureGitStatusPollTimer();
                }

                if (openWindow)
                {
                    await OpenDiffViewerWindowAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting diff tracking: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles file changes event from the tracker
        /// </summary>
        private void OnFilesChanged(object sender, FileChangesEventArgs e)
        {
            try
            {
                // Refresh diff view on UI thread
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await RefreshDiffViewAsync();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling file changes: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes the diff viewer with current changes
        /// </summary>
        private async Task RefreshDiffViewAsync()
        {
            try
            {
                if (_fileChangeTracker == null)
                    return;

                // Run heavy file operations on background thread
                var tracker = _fileChangeTracker;
                var changedFiles = await System.Threading.Tasks.Task.Run(() => tracker.GetChangedFiles());

                // Switch to UI thread for the rest
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!_isAutoResetting && _isDiffTrackingActive && changedFiles.Count > 0)
                {
                    bool shouldAutoReset = await ShouldAutoResetDiffBaselineAsync();
                    if (shouldAutoReset)
                    {
                        await ResetDiffBaselineAsync(true, true, false, false, null, false);
                        return;
                    }
                }

                if (_diffViewerWindow?.DiffViewerControl != null)
                {
                    _diffViewerWindow.DiffViewerControl.UpdateChangedFiles(changedFiles);
                    // Hide reset baseline button for git repos (always use git baseline)
                    _diffViewerWindow.DiffViewerControl.SetResetBaselineVisible(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing diff view: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens or shows the diff viewer tool window
        /// </summary>
        private async Task OpenDiffViewerWindowAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                await EnsureDiffViewerWindowAsync(true);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening diff viewer window: {ex.Message}");
            }
        }

        private async Task EnsureDiffViewerWindowAsync(bool showWindow)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Get the package that provides the tool window
            var vsPackage = await GetPackageAsync();
            if (vsPackage == null)
            {
                return;
            }

            bool createWindow = showWindow;
            _diffViewerWindow = vsPackage.FindToolWindow(typeof(DiffViewerToolWindow), 0, createWindow) as DiffViewerToolWindow;
            if (_diffViewerWindow?.Frame == null)
            {
                if (createWindow)
                {
                }
                return;
            }

            if (_diffViewerWindow.DiffViewerControl != null && !_diffViewerResetSubscribed)
            {
                _diffViewerWindow.DiffViewerControl.ResetRequested += OnDiffViewerResetRequested;
                _diffViewerResetSubscribed = true;
            }

            if (!_diffViewerVisibilitySubscribed)
            {
                _diffViewerWindow.VisibilityChanged += OnDiffViewerVisibilityChanged;
                _diffViewerVisibilitySubscribed = true;
            }

            // Only start polling if window is visible
            if (_diffViewerWindow.IsWindowVisible)
            {
                EnsureGitStatusPollTimer();
            }

            // Hide reset baseline button for git repos (always use git baseline)
            if (_diffViewerWindow.DiffViewerControl != null)
            {
                _diffViewerWindow.DiffViewerControl.SetResetBaselineVisible(false);
            }

            if (showWindow)
            {
                var windowFrame = (IVsWindowFrame)_diffViewerWindow.Frame;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
            }
        }

        private void EnsureGitStatusPollTimer()
        {
            if (_gitStatusPollTimer != null)
                return;

            _gitStatusPollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(GitStatusPollIntervalMs)
            };
            _gitStatusPollTimer.Tick += OnGitStatusPollTimerTick;
            _gitStatusPollTimer.Start();
        }

        private void StopGitStatusPollTimer()
        {
            if (_gitStatusPollTimer == null)
                return;

            _gitStatusPollTimer.Stop();
            _gitStatusPollTimer.Tick -= OnGitStatusPollTimerTick;
            _gitStatusPollTimer = null;
        }

        private void OnGitStatusPollTimerTick(object sender, EventArgs e)
        {
            if (_isAutoResetting || !_isDiffTrackingActive)
                return;

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                try
                {
                    bool shouldAutoReset = await ShouldAutoResetDiffBaselineAsync();
                    if (shouldAutoReset)
                    {
                        await ResetDiffBaselineAsync(true, true, false, false, null, true);
                    }
                    else
                    {
                        // Refresh the diff view on each poll
                        await RefreshDiffViewAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in git status poll: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Gets the extension package instance
        /// </summary>
        private async Task<AsyncPackage> GetPackageAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var shell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
                if (shell == null)
                    return null;

                var packageGuid = new Guid(ClaudeCodeExtension.ClaudeCodeExtensionPackage.PackageGuidString);
                shell.IsPackageLoaded(ref packageGuid, out IVsPackage vsPackage);

                if (vsPackage == null)
                {
                    shell.LoadPackage(ref packageGuid, out vsPackage);
                }

                return vsPackage as AsyncPackage;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting package: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Handles the View Changes button click
        /// </summary>
        private void ViewChangesButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                try
                {
                    await EnsureDiffTrackingStartedAsync(true);

                    // If tracking is active, refresh the view
                    if (_isDiffTrackingActive)
                    {
                        await RefreshDiffViewAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in ViewChangesButton_Click: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Updates the visibility of the View Changes button based on git availability
        /// </summary>
        private async Task UpdateViewChangesButtonVisibilityAsync()
        {
            try
            {
                string workspaceDir = await GetWorkspaceDirectoryAsync();
                string repoRoot = FindGitRepositoryRoot(workspaceDir);
                bool isGitRepo = !string.IsNullOrEmpty(repoRoot);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (ViewChangesButton != null)
                {
                    ViewChangesButton.Visibility = isGitRepo ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating ViewChangesButton visibility: {ex.Message}");
            }
        }

        private void OnDiffViewerResetRequested(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                try
                {
                    // Use git baseline when resetting to properly track changes relative to HEAD
                    await ResetDiffBaselineAsync(true, false, true, true, null, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in OnDiffViewerResetRequested: {ex.Message}");
                    MessageBox.Show("Failed to reset code changes baseline.", "Claude Code", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void OnDiffViewerVisibilityChanged(object sender, bool isVisible)
        {
            if (isVisible)
            {
                // Window became visible - resume tracking and force refresh
                EnsureGitStatusPollTimer();

                // Force a refresh to catch any changes that occurred while hidden
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    try
                    {
                        if (_isAutoResetting)
                            return;

                        // Refresh baseline from git when the window is activated
                        await ResetDiffBaselineAsync(true, false, false, false, null, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error refreshing diff view on visibility change: {ex.Message}");
                    }
                });
            }
            else
            {
                // Window hidden - pause tracking to save resources
                StopGitStatusPollTimer();
            }
        }

        private async Task ResetDiffBaselineAsync(bool refreshView, bool isAutoReset, bool showErrors, bool startTracking, string workspaceDirOverride, bool useGitBaseline)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string workspaceDir = workspaceDirOverride ?? await GetEffectiveWorkspaceDirectoryAsync();

                if (string.IsNullOrEmpty(workspaceDir) || !System.IO.Directory.Exists(workspaceDir))
                {
                    if (showErrors)
                    {
                        MessageBox.Show("Could not determine workspace directory to reset changes.", "Claude Code", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                // Only support git repositories
                string repoRoot = FindGitRepositoryRoot(workspaceDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    return;
                }

                InitializeDiffTracking();
                _isAutoResetting = isAutoReset;

                // Git repo: apply git baseline (reads only changed files from git)
                if (useGitBaseline)
                {
                    string repoRootCopy = repoRoot;
                    await System.Threading.Tasks.Task.Run(() => TryApplyGitBaseline(repoRootCopy));
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (startTracking)
                {
                    _isDiffTrackingActive = true;
                }

                // Hide reset baseline button for git repos (always use git baseline)
                if (_diffViewerWindow?.DiffViewerControl != null)
                {
                    _diffViewerWindow.DiffViewerControl.SetResetBaselineVisible(false);
                }

                // Ensure tracking is active if window is visible
                if (_diffViewerWindow != null && _diffViewerWindow.IsWindowVisible)
                {
                    EnsureGitStatusPollTimer();
                }

                if (refreshView)
                {
                    await RefreshDiffViewAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error resetting diff baseline: {ex.Message}");
                if (showErrors)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show("Failed to reset code changes baseline.", "Claude Code", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                _isAutoResetting = false;
            }
        }

        private async Task<bool> ShouldAutoResetDiffBaselineAsync()
        {
            try
            {
                string workspaceDir = await GetEffectiveWorkspaceDirectoryAsync();

                if (string.IsNullOrEmpty(workspaceDir) || workspaceDir.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase))
                    return false;

                string repoRoot = FindGitRepositoryRoot(workspaceDir);
                if (string.IsNullOrEmpty(repoRoot))
                    return false;

                return IsGitRepositoryClean(repoRoot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking git clean state: {ex.Message}");
                return false;
            }
        }

        private async Task<string> GetEffectiveWorkspaceDirectoryAsync()
        {
            string workspaceDir = _lastWorkspaceDirectory;
            if (!string.IsNullOrEmpty(workspaceDir))
            {
                string documentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.Equals(workspaceDir, documentsDir, StringComparison.OrdinalIgnoreCase))
                {
                    return workspaceDir;
                }
            }

            string resolved = await GetWorkspaceDirectoryAsync();
            return string.IsNullOrEmpty(resolved) ? workspaceDir : resolved;
        }

        private string FindGitRepositoryRoot(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory))
                return null;

            try
            {
                var current = new System.IO.DirectoryInfo(startDirectory);
                while (current != null)
                {
                    string gitPath = System.IO.Path.Combine(current.FullName, ".git");
                    if (System.IO.Directory.Exists(gitPath) || System.IO.File.Exists(gitPath))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error finding git repository root: {ex.Message}");
            }

            return null;
        }

        private bool IsGitRepositoryClean(string repoRoot)
        {
            if (string.IsNullOrEmpty(repoRoot))
                return false;

            var now = DateTime.UtcNow;
            if (string.Equals(repoRoot, _lastGitStatusRepoRoot, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastGitStatusCheckUtc).TotalMilliseconds < GitStatusThrottleMs)
            {
                return _lastGitStatusClean;
            }

            bool isClean = false;
            try
            {
                var processStart = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "status --porcelain",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processStart))
                {
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        bool exited = process.WaitForExit(GitStatusTimeoutMs);
                        if (!exited)
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch
                            {
                                // Ignore failures on kill
                            }
                        }
                        if (process.ExitCode == 0)
                        {
                            isClean = string.IsNullOrWhiteSpace(output);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error running git status: {ex.Message}");
            }

            _lastGitStatusCheckUtc = now;
            _lastGitStatusRepoRoot = repoRoot;
            _lastGitStatusClean = isClean;
            return isClean;
        }

        private bool TryApplyGitBaseline(string repoRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot))
                    return false;

                if (!Directory.Exists(repoRoot))
                    return false;

                string statusOutput = RunGitCommand(repoRoot, "status --porcelain=v1 -z", GitStatusTimeoutMs);
                if (string.IsNullOrEmpty(statusOutput))
                {
                    // No changes - clear the tracker to show empty state
                    _fileChangeTracker.Clear();
                    return true;
                }

                var originalContents = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Collect all files that need their original content fetched
                var filesToFetch = new List<(string fullPath, string relativePath, bool isDeleted)>();

                foreach (var entry in ParseGitStatusEntries(statusOutput))
                {
                    if (entry.IsRenameOrCopy)
                    {
                        string oldFullPath = BuildFullPath(repoRoot, entry.Path);
                        string newFullPath = BuildFullPath(repoRoot, entry.NewPath);

                        // Track files under the git repository root
                        if (IsPathUnderDirectory(oldFullPath, repoRoot) && _fileChangeTracker.IsTrackablePath(oldFullPath))
                        {
                            filesToFetch.Add((oldFullPath, entry.Path, false));
                        }

                        if (IsPathUnderDirectory(newFullPath, repoRoot) && _fileChangeTracker.IsTrackablePath(newFullPath))
                        {
                            lock (createdFiles)
                            {
                                createdFiles.Add(newFullPath);
                            }
                        }

                        continue;
                    }

                    string fullPath = BuildFullPath(repoRoot, entry.Path);
                    // Track files under the git repository root (not just workspace directory)
                    if (!IsPathUnderDirectory(fullPath, repoRoot) || !_fileChangeTracker.IsTrackablePath(fullPath))
                        continue;

                    if (entry.IsUntracked || entry.IsAdded)
                    {
                        lock (createdFiles)
                        {
                            createdFiles.Add(fullPath);
                        }
                        continue;
                    }

                    if (entry.IsDeleted)
                    {
                        filesToFetch.Add((fullPath, entry.Path, true));
                        lock (deletedFiles)
                        {
                            deletedFiles.Add(fullPath);
                        }
                        continue;
                    }

                    if (entry.IsModified || entry.IsTypeChanged || entry.IsUnmerged)
                    {
                        filesToFetch.Add((fullPath, entry.Path, false));
                    }
                }

                // Fetch original contents in parallel for better performance
                if (filesToFetch.Count > 0)
                {
                    var parallelOptions = new System.Threading.Tasks.ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(filesToFetch.Count, Environment.ProcessorCount * 2)
                    };

                    System.Threading.Tasks.Parallel.ForEach(filesToFetch, parallelOptions, fileInfo =>
                    {
                        string original = ReadGitFile(repoRoot, fileInfo.relativePath);
                        if (original != null)
                        {
                            originalContents[fileInfo.fullPath] = original;
                        }
                    });
                }

                if (originalContents.Count == 0 && createdFiles.Count == 0 && deletedFiles.Count == 0)
                {
                    // No trackable changes - clear the tracker to show empty state
                    _fileChangeTracker.Clear();
                    return true;
                }

                _fileChangeTracker.SetBaseline(repoRoot, originalContents, createdFiles, deletedFiles);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying git baseline: {ex.Message}");
                return false;
            }
        }

        private string RunGitCommand(string workingDirectory, string arguments, int timeoutMs)
        {
            try
            {
                var processStart = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processStart))
                {
                    if (process == null)
                        return null;

                    string output = process.StandardOutput.ReadToEnd();
                    bool exited = process.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Ignore failures on kill
                        }
                        return null;
                    }
                    if (process.ExitCode != 0)
                        return null;

                    return output;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error running git command: {ex.Message}");
                return null;
            }
        }

        private string ReadGitFile(string repoRoot, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;

            string gitPath = relativePath.Replace("\\", "/");
            string output = RunGitCommand(repoRoot, $"show HEAD:{gitPath}", GitShowTimeoutMs);
            if (output != null && output.Length > MaxGitFileBytes)
            {
                return null;
            }
            return output;
        }

        private static string BuildFullPath(string repoRoot, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return relativePath;

            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(repoRoot, normalized));
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
                return false;

            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<GitStatusEntry> ParseGitStatusEntries(string output)
        {
            if (string.IsNullOrEmpty(output))
                yield break;

            string[] parts = output.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part))
                    continue;

                if (part.Length < 3)
                    continue;

                string status = part.Substring(0, 2);
                string path = part.Substring(3);

                bool isRenameOrCopy = status.IndexOf('R') >= 0 || status.IndexOf('C') >= 0;
                if (isRenameOrCopy && i + 1 < parts.Length)
                {
                    string newPath = parts[++i];
                    yield return new GitStatusEntry(status, path, newPath);
                }
                else
                {
                    yield return new GitStatusEntry(status, path, null);
                }
            }
        }

        private sealed class GitStatusEntry
        {
            public GitStatusEntry(string status, string path, string newPath)
            {
                Status = status;
                Path = path;
                NewPath = newPath;
            }

            public string Status { get; }
            public string Path { get; }
            public string NewPath { get; }

            public bool IsRenameOrCopy => Status.IndexOf('R') >= 0 || Status.IndexOf('C') >= 0;
            public bool IsUntracked => Status == "??";
            public bool IsAdded => Status.IndexOf('A') >= 0;
            public bool IsDeleted => Status.IndexOf('D') >= 0;
            public bool IsModified => Status.IndexOf('M') >= 0;
            public bool IsTypeChanged => Status.IndexOf('T') >= 0;
            public bool IsUnmerged => Status.IndexOf('U') >= 0;
        }

        /// <summary>
        /// Cleans up diff tracking resources
        /// </summary>
        private void CleanupDiffTracking()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_fileChangeTracker != null)
                {
                    _fileChangeTracker.Dispose();
                    _fileChangeTracker = null;
                }
                if (_diffViewerWindow != null)
                {
                    if (_diffViewerVisibilitySubscribed)
                    {
                        _diffViewerWindow.VisibilityChanged -= OnDiffViewerVisibilityChanged;
                    }

                    // Close the diff viewer window
                    try
                    {
                        if (_diffViewerWindow.Frame is IVsWindowFrame windowFrame)
                        {
                            windowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error closing diff viewer window: {ex.Message}");
                    }
                    _diffViewerWindow = null;
                }
                _isDiffTrackingActive = false;
                _diffViewerResetSubscribed = false;
                _diffViewerVisibilitySubscribed = false;
                StopGitStatusPollTimer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up diff tracking: {ex.Message}");
            }
        }

        #endregion
    }
}
