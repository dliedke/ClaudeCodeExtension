/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Tracks file changes using git baseline for diff computation
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Tracks file changes using git baseline, storing original content for diff computation
    /// </summary>
    public class FileChangeTracker : IDisposable
    {
        #region Fields

        private readonly ConcurrentDictionary<string, string> _originalContents;
        private readonly HashSet<string> _createdFiles;
        private readonly HashSet<string> _deletedFiles;
        private readonly object _lock = new object();
        private string _workspaceDirectory;
        private bool _disposed;
        private const int MaxTrackedFileBytes = 4 * 1024 * 1024;

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

        #region Constructor

        public FileChangeTracker()
        {
            _originalContents = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets all changed files with their diffs
        /// </summary>
        public List<ChangedFile> GetChangedFiles()
        {
            var changedFiles = new List<ChangedFile>();

            if (string.IsNullOrEmpty(_workspaceDirectory))
                return changedFiles;

            // Check for modified and deleted files (files that were in git HEAD)
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
                        // File still exists, check if content changed
                        var currentContent = ReadFileContent(filePath);
                        if (currentContent != null && currentContent != originalContent)
                        {
                            // Content was modified
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

            // Check for new files (untracked or added in git)
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

            // Sort by last modified time (oldest first, so newest/last updated appear at bottom)
            return changedFiles.OrderBy(f => GetFileLastModifiedTime(f.FilePath)).ToList();
        }

        /// <summary>
        /// Clears all tracked data
        /// </summary>
        public void Clear()
        {
            _originalContents.Clear();
            lock (_lock)
            {
                _createdFiles.Clear();
                _deletedFiles.Clear();
            }
        }

        /// <summary>
        /// Sets a custom baseline for tracked changes (used for git-based tracking)
        /// </summary>
        public void SetBaseline(string workspaceDirectory, IDictionary<string, string> originalContents, IEnumerable<string> createdFiles, IEnumerable<string> deletedFiles)
        {
            if (string.IsNullOrEmpty(workspaceDirectory))
                return;

            _workspaceDirectory = workspaceDirectory;
            _originalContents.Clear();

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
                // Skip large files
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxTrackedFileBytes)
                    return null;

                // Try to read with shared access using UTF-8 encoding (with BOM detection)
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private DateTime GetFileLastModifiedTime(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    return File.GetLastWriteTimeUtc(filePath);
                }
                // Deleted files: use MinValue so they appear first (oldest)
                return DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

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
