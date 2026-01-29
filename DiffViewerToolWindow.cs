/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Tool window for displaying file diffs in Visual Studio
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Tool window for displaying file changes and diffs
    /// </summary>
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    public class DiffViewerToolWindow : ToolWindowPane
    {
        private DiffViewerControl _diffViewerControl;

        /// <summary>
        /// Gets the diff viewer control
        /// </summary>
        public DiffViewerControl DiffViewerControl => _diffViewerControl;

        public DiffViewerToolWindow() : base(null)
        {
            this.Caption = "Code Changes";
            _diffViewerControl = new DiffViewerControl();
            this.Content = _diffViewerControl;
        }

        /// <summary>
        /// Updates the title of the tool window
        /// </summary>
        public void UpdateTitle(string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.Caption = title;
        }

        /// <summary>
        /// Updates the title with file count and change stats
        /// </summary>
        public void UpdateTitleWithStats(int fileCount, int linesAdded, int linesRemoved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (fileCount == 0)
            {
                this.Caption = "Code Changes";
            }
            else
            {
                this.Caption = $"Code Changes ({fileCount} files, +{linesAdded} -{linesRemoved})";
            }
        }
    }
}
