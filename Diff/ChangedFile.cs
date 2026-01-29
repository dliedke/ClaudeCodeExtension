/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Data models for tracking file changes and diff information
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Type of change made to a file
    /// </summary>
    public enum ChangeType
    {
        Created,
        Modified,
        Deleted
    }

    /// <summary>
    /// Type of diff line (context, added, or removed)
    /// </summary>
    public enum DiffLineType
    {
        Context,
        Added,
        Removed
    }

    /// <summary>
    /// Represents a single line in the diff output
    /// </summary>
    public class DiffLine
    {
        /// <summary>
        /// Line number in the original file (null for added lines)
        /// </summary>
        public int? OldLineNumber { get; set; }

        /// <summary>
        /// Line number in the modified file (null for removed lines)
        /// </summary>
        public int? NewLineNumber { get; set; }

        /// <summary>
        /// The text content of the line
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// The type of diff line (Context, Added, or Removed)
        /// </summary>
        public DiffLineType Type { get; set; }
    }

    /// <summary>
    /// Represents a changed file with its diff information
    /// </summary>
    public class ChangedFile : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private string _filePath;
        private string _fileName;
        private string _relativePath;
        private string _originalContent;
        private string _modifiedContent;
        private int _linesAdded;
        private int _linesRemoved;
        private List<DiffLine> _diffLines;
        private ChangeType _type;

        /// <summary>
        /// Full path to the file
        /// </summary>
        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Just the filename without path
        /// </summary>
        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Relative path from workspace root (for display)
        /// </summary>
        public string RelativePath
        {
            get => _relativePath;
            set { _relativePath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Original content before changes (null for new files)
        /// </summary>
        public string OriginalContent
        {
            get => _originalContent;
            set { _originalContent = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Modified content after changes (null for deleted files)
        /// </summary>
        public string ModifiedContent
        {
            get => _modifiedContent;
            set { _modifiedContent = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Number of lines added
        /// </summary>
        public int LinesAdded
        {
            get => _linesAdded;
            set { _linesAdded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChangesSummary)); }
        }

        /// <summary>
        /// Number of lines removed
        /// </summary>
        public int LinesRemoved
        {
            get => _linesRemoved;
            set { _linesRemoved = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChangesSummary)); }
        }

        /// <summary>
        /// Structured diff lines for display
        /// </summary>
        public List<DiffLine> DiffLines
        {
            get => _diffLines;
            set { _diffLines = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether this file's diff is currently expanded in the UI
        /// </summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Type of change (Created, Modified, Deleted)
        /// </summary>
        public ChangeType Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); OnPropertyChanged(nameof(TypeIndicator)); }
        }

        /// <summary>
        /// Display string for changes (+X -Y)
        /// </summary>
        public string ChangesSummary => $"+{LinesAdded} -{LinesRemoved}";

        /// <summary>
        /// Display indicator for change type
        /// </summary>
        public string TypeIndicator
        {
            get
            {
                switch (Type)
                {
                    case ChangeType.Created: return "[new]";
                    case ChangeType.Deleted: return "[del]";
                    default: return "";
                }
            }
        }

        public ChangedFile()
        {
            DiffLines = new List<DiffLine>();
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
