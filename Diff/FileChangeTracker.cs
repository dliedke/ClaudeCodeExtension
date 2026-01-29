/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Tracks file changes in the workspace using FileSystemWatcher with debouncing
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Tracks file changes in the workspace, storing original content for diff computation
    /// </summary>
    public class FileChangeTracker : IDisposable
    {
        #region Fields

        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, string> _originalContents;
        private readonly ConcurrentDictionary<string, DateTime> _pendingChanges;
        private readonly HashSet<string> _createdFiles;
        private readonly HashSet<string> _deletedFiles;
        private readonly object _lock = new object();
        private Timer _debounceTimer;
        private readonly int _debounceMs = 500;
        private string _workspaceDirectory;
        private bool _isTracking;
        private bool _disposed;

        /// <summary>
        /// File extensions to track (source code files)
        /// </summary>
        private static readonly HashSet<string> TrackedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".xaml", ".xml", ".json", ".config", ".vsixmanifest", ".csproj", ".vbproj", ".fsproj", ".sln", ".props", ".targets",
            ".js", ".ts", ".jsx", ".tsx", ".vue", ".html", ".css", ".scss", ".less",
            ".py", ".rb", ".php", ".java", ".kt", ".scala", ".go", ".rs", ".swift",
            ".c", ".cpp", ".h", ".hpp", ".m", ".mm",
            ".sql", ".sh", ".ps1", ".bat", ".cmd",
            ".yaml", ".yml", ".toml", ".ini", ".md", ".txt", ".resx", ".settings"
        };

        /// <summary>
        /// Directories to ignore
        /// </summary>
        private static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages",
            "dist", "build", "out", "target", "__pycache__", ".cache"
        };

        #endregion

        #region Events

        /// <summary>
        /// Fired when files have changed (after debounce)
        /// </summary>
        public event EventHandler<FileChangesEventArgs> FilesChanged;

        #endregion

        #region Constructor

        public FileChangeTracker()
        {
            _originalContents = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _pendingChanges = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Takes a snapshot of all tracked files in the workspace
        /// </summary>
        public void TakeSnapshot(string workspaceDirectory)
        {
            if (string.IsNullOrEmpty(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
                return;

            _workspaceDirectory = workspaceDirectory;
            _originalContents.Clear();
            _createdFiles.Clear();
            _deletedFiles.Clear();
            _pendingChanges.Clear();


            try
            {
                var files = GetTrackedFiles(workspaceDirectory);
                foreach (var file in files)
                {
                    try
                    {
                        var content = ReadFileContent(file);
                        if (content != null)
                        {
                            _originalContents[file] = content;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error reading file {file}: {ex.Message}");
                    }
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error taking snapshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts watching for file changes
        /// </summary>
        public void StartTracking(string workspaceDirectory)
        {
            if (_isTracking)
                StopTracking();

            if (string.IsNullOrEmpty(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
                return;

            _workspaceDirectory = workspaceDirectory;

            try
            {
                _watcher = new FileSystemWatcher(workspaceDirectory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileCreated;
                _watcher.Deleted += OnFileDeleted;
                _watcher.Renamed += OnFileRenamed;
                _watcher.Error += OnWatcherError;

                _watcher.EnableRaisingEvents = true;
                _isTracking = true;

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting file watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops watching for file changes
        /// </summary>
        public void StopTracking()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileCreated;
                _watcher.Deleted -= OnFileDeleted;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _isTracking = false;

        }

        /// <summary>
        /// Pauses file watching without disposing the watcher
        /// </summary>
        public void Pause()
        {
            if (_watcher != null && _isTracking)
            {
                _watcher.EnableRaisingEvents = false;
            }
        }

        /// <summary>
        /// Resumes file watching after being paused
        /// </summary>
        public void Resume()
        {
            if (_watcher != null && _isTracking)
            {
                _watcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>
        /// Gets all changed files with their diffs
        /// </summary>
        public List<ChangedFile> GetChangedFiles()
        {
            var changedFiles = new List<ChangedFile>();

            if (string.IsNullOrEmpty(_workspaceDirectory))
                return changedFiles;

            // Check for modified and deleted files
            foreach (var kvp in _originalContents)
            {
                var filePath = kvp.Key;
                var originalContent = kvp.Value;

                try
                {
                    if (!File.Exists(filePath))
                    {
                        // File was deleted
                        var changedFile = CreateChangedFile(filePath, originalContent, null, ChangeType.Deleted);
                        changedFiles.Add(changedFile);
                    }
                    else
                    {
                        // Check if content changed
                        var currentContent = ReadFileContent(filePath);
                        if (currentContent != null && currentContent != originalContent)
                        {
                            var changedFile = CreateChangedFile(filePath, originalContent, currentContent, ChangeType.Modified);
                            changedFiles.Add(changedFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking file {filePath}: {ex.Message}");
                }
            }

            // Check for new files
            lock (_lock)
            {
                foreach (var filePath in _createdFiles)
                {
                    if (!_originalContents.ContainsKey(filePath) && File.Exists(filePath))
                    {
                        try
                        {
                            var currentContent = ReadFileContent(filePath);
                            if (currentContent != null)
                            {
                                var changedFile = CreateChangedFile(filePath, null, currentContent, ChangeType.Created);
                                changedFiles.Add(changedFile);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error reading new file {filePath}: {ex.Message}");
                        }
                    }
                }
            }

            return changedFiles.OrderBy(f => f.FileName).ToList();
        }

        /// <summary>
        /// Clears all tracked data
        /// </summary>
        public void Clear()
        {
            _originalContents.Clear();
            _pendingChanges.Clear();
            lock (_lock)
            {
                _createdFiles.Clear();
                _deletedFiles.Clear();
            }
        }

        /// <summary>
        /// Sets a custom baseline for tracked changes
        /// </summary>
        public void SetBaseline(string workspaceDirectory, IDictionary<string, string> originalContents, IEnumerable<string> createdFiles, IEnumerable<string> deletedFiles)
        {
            if (string.IsNullOrEmpty(workspaceDirectory))
                return;

            _workspaceDirectory = workspaceDirectory;
            _originalContents.Clear();
            _pendingChanges.Clear();

            if (originalContents != null)
            {
                foreach (var kvp in originalContents)
                {
                    _originalContents[kvp.Key] = kvp.Value;
                }
            }

            lock (_lock)
            {
                _createdFiles.Clear();
                _deletedFiles.Clear();

                if (createdFiles != null)
                {
                    foreach (var file in createdFiles)
                    {
                        _createdFiles.Add(file);
                    }
                }

                if (deletedFiles != null)
                {
                    foreach (var file in deletedFiles)
                    {
                        _deletedFiles.Add(file);
                    }
                }
            }
        }

        /// <summary>
        /// Determines whether a path should be tracked
        /// </summary>
        public bool IsTrackablePath(string filePath)
        {
            return ShouldTrackFile(filePath);
        }

        #endregion

        #region Private Methods

        private ChangedFile CreateChangedFile(string filePath, string originalContent, string modifiedContent, ChangeType type)
        {
            var fileName = Path.GetFileName(filePath);
            var relativePath = GetRelativePath(filePath);

            return new ChangedFile
            {
                FilePath = filePath,
                FileName = fileName,
                RelativePath = relativePath,
                OriginalContent = originalContent,
                ModifiedContent = modifiedContent,
                Type = type
            };
        }

        private string GetRelativePath(string fullPath)
        {
            if (string.IsNullOrEmpty(_workspaceDirectory))
                return fullPath;

            if (fullPath.StartsWith(_workspaceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullPath.Substring(_workspaceDirectory.Length);
                if (relative.StartsWith("\\") || relative.StartsWith("/"))
                    relative = relative.Substring(1);

                // Return directory part only
                var dir = Path.GetDirectoryName(relative);
                return string.IsNullOrEmpty(dir) ? "" : dir.Replace("\\", "/") + "/";
            }

            return fullPath;
        }

        private IEnumerable<string> GetTrackedFiles(string directory)
        {
            var files = new List<string>();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (ShouldTrackFile(file))
                        files.Add(file);
                }

                foreach (var subDir in Directory.EnumerateDirectories(directory))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (!IgnoredDirectories.Contains(dirName))
                    {
                        files.AddRange(GetTrackedFiles(subDir));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enumerating directory {directory}: {ex.Message}");
            }

            return files;
        }

        private bool ShouldTrackFile(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            if (!TrackedExtensions.Contains(extension))
                return false;

            // Check if file is in an ignored directory
            var pathParts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in pathParts)
            {
                if (IgnoredDirectories.Contains(part))
                    return false;
            }

            return true;
        }

        private string ReadFileContent(string filePath)
        {
            try
            {
                // Skip large files (> 1MB)
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 1024 * 1024)
                    return null;

                // Try to read with shared access
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region File System Event Handlers

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!ShouldTrackFile(e.FullPath))
                return;

            _pendingChanges[e.FullPath] = DateTime.Now;
            ScheduleDebounce();
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            if (!ShouldTrackFile(e.FullPath))
                return;

            lock (_lock)
            {
                _createdFiles.Add(e.FullPath);
                _deletedFiles.Remove(e.FullPath);
            }

            _pendingChanges[e.FullPath] = DateTime.Now;
            ScheduleDebounce();
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            if (!ShouldTrackFile(e.FullPath))
                return;

            lock (_lock)
            {
                _deletedFiles.Add(e.FullPath);
                _createdFiles.Remove(e.FullPath);
            }

            _pendingChanges[e.FullPath] = DateTime.Now;
            ScheduleDebounce();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            // Treat rename as delete old + create new
            if (ShouldTrackFile(e.OldFullPath))
            {
                lock (_lock)
                {
                    _deletedFiles.Add(e.OldFullPath);
                }
                _pendingChanges[e.OldFullPath] = DateTime.Now;
            }

            if (ShouldTrackFile(e.FullPath))
            {
                lock (_lock)
                {
                    _createdFiles.Add(e.FullPath);
                }
                _pendingChanges[e.FullPath] = DateTime.Now;
            }

            ScheduleDebounce();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Debug.WriteLine($"FileSystemWatcher error: {e.GetException().Message}");
        }

        private void ScheduleDebounce()
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, _debounceMs, Timeout.Infinite);
        }

        private void OnDebounceElapsed(object state)
        {
            try
            {
                if (_pendingChanges.Count > 0)
                {
                    _pendingChanges.Clear();
                    FilesChanged?.Invoke(this, new FileChangesEventArgs());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in debounce callback: {ex.Message}");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            StopTracking();
            _disposed = true;
        }

        #endregion
    }

    /// <summary>
    /// Event args for file changes event
    /// </summary>
    public class FileChangesEventArgs : EventArgs
    {
    }
}
