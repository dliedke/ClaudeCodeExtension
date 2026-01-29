/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Computes diffs between file versions using DiffPlex library
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlexChangeType = DiffPlex.DiffBuilder.Model.ChangeType;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Computes diffs between file versions using DiffPlex library
    /// </summary>
    public static class DiffComputer
    {
        /// <summary>
        /// Number of context lines to show around changes
        /// </summary>
        private const int ContextLines = 3;

        /// <summary>
        /// Computes the diff for a changed file and populates its DiffLines, LinesAdded, and LinesRemoved
        /// </summary>
        public static void ComputeDiff(ChangedFile changedFile)
        {
            if (changedFile == null)
                return;

            try
            {
                var diffLines = new List<DiffLine>();
                int linesAdded = 0;
                int linesRemoved = 0;

                string oldText = changedFile.OriginalContent ?? "";
                string newText = changedFile.ModifiedContent ?? "";

                var diffBuilder = new InlineDiffBuilder(new Differ());
                var diff = diffBuilder.BuildDiffModel(oldText, newText);

                int oldLineNum = 0;
                int newLineNum = 0;

                // Track which lines to include (changes + context)
                var linesToInclude = new HashSet<int>();
                var allDiffLines = new List<Tuple<int, DiffPiece>>();

                // First pass: identify changed lines and mark context
                for (int i = 0; i < diff.Lines.Count; i++)
                {
                    var line = diff.Lines[i];
                    allDiffLines.Add(Tuple.Create(i, line));

                    if (line.Type == DiffPlexChangeType.Inserted || line.Type == DiffPlexChangeType.Deleted)
                    {
                        // Mark this line and surrounding context
                        for (int j = Math.Max(0, i - ContextLines); j <= Math.Min(diff.Lines.Count - 1, i + ContextLines); j++)
                        {
                            linesToInclude.Add(j);
                        }
                    }
                }

                // Second pass: build diff output with line numbers
                int? lastIncludedIndex = null;

                for (int i = 0; i < diff.Lines.Count; i++)
                {
                    var line = diff.Lines[i];

                    // Update line numbers based on change type
                    switch (line.Type)
                    {
                        case DiffPlexChangeType.Unchanged:
                            oldLineNum++;
                            newLineNum++;
                            break;
                        case DiffPlexChangeType.Deleted:
                            oldLineNum++;
                            linesRemoved++;
                            break;
                        case DiffPlexChangeType.Inserted:
                            newLineNum++;
                            linesAdded++;
                            break;
                        case DiffPlexChangeType.Modified:
                            // Modified is treated as delete + insert in inline diff
                            oldLineNum++;
                            newLineNum++;
                            break;
                    }

                    if (!linesToInclude.Contains(i))
                        continue;

                    // Add separator if there's a gap
                    if (lastIncludedIndex.HasValue && i - lastIncludedIndex.Value > 1)
                    {
                        diffLines.Add(new DiffLine
                        {
                            Text = "...",
                            Type = DiffLineType.Context,
                            OldLineNumber = null,
                            NewLineNumber = null
                        });
                    }

                    lastIncludedIndex = i;

                    var diffLine = new DiffLine { Text = line.Text ?? "" };

                    switch (line.Type)
                    {
                        case DiffPlexChangeType.Unchanged:
                        case DiffPlexChangeType.Imaginary:
                            diffLine.Type = DiffLineType.Context;
                            diffLine.OldLineNumber = oldLineNum;
                            diffLine.NewLineNumber = newLineNum;
                            break;

                        case DiffPlexChangeType.Deleted:
                            diffLine.Type = DiffLineType.Removed;
                            diffLine.OldLineNumber = oldLineNum;
                            diffLine.NewLineNumber = null;
                            break;

                        case DiffPlexChangeType.Inserted:
                            diffLine.Type = DiffLineType.Added;
                            diffLine.OldLineNumber = null;
                            diffLine.NewLineNumber = newLineNum;
                            break;

                        case DiffPlexChangeType.Modified:
                            // Treat as context for inline diff (modifications shown separately)
                            diffLine.Type = DiffLineType.Context;
                            diffLine.OldLineNumber = oldLineNum;
                            diffLine.NewLineNumber = newLineNum;
                            break;
                    }

                    diffLines.Add(diffLine);
                }

                changedFile.DiffLines = diffLines;
                changedFile.LinesAdded = linesAdded;
                changedFile.LinesRemoved = linesRemoved;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error computing diff for {changedFile.FilePath}: {ex.Message}");
                changedFile.DiffLines = new List<DiffLine>();
                changedFile.LinesAdded = 0;
                changedFile.LinesRemoved = 0;
            }
        }

        /// <summary>
        /// Computes diffs for all changed files
        /// </summary>
        public static void ComputeDiffs(IEnumerable<ChangedFile> changedFiles)
        {
            foreach (var file in changedFiles)
            {
                ComputeDiff(file);
            }
        }
    }
}
