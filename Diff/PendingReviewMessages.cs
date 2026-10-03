/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Wording of the "Track agent changes for review" confirmations (Undo, Undo All, turning the setting off)
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Linq;

namespace ClaudeCodeVS.Diff
{
    /// <summary>
    /// Builds the questions the pending review asks before it discards anything. Kept free of VS types so the
    /// wording, and in particular the unsaved-edits warning, is covered by the unit tests.
    /// </summary>
    internal static class PendingReviewMessages
    {
        /// <summary>Most file names listed in a warning before the rest are summarized as "and N more".</summary>
        internal const int MaxListedFiles = 10;

        /// <summary>Question for a single file's Undo.</summary>
        /// <param name="fileName">Name of the pending file.</param>
        /// <param name="type">How the agent changed it.</param>
        /// <param name="hasUnsavedEdits">True when the file is open in an editor with unsaved changes.</param>
        internal static string BuildUndoQuestion(string fileName, ChangeType type, bool hasUnsavedEdits)
        {
            string question;
            switch (type)
            {
                case ChangeType.Created:
                    question = $"Delete {fileName}?\n\nThe agent created this file. Undo removes it from disk.";
                    break;
                case ChangeType.Deleted:
                    question = $"Restore {fileName}?\n\nThe agent deleted this file. Undo puts it back as it was.";
                    break;
                default:
                    question = $"Undo the agent's changes to {fileName}?\n\nThe file goes back to how it was before the agent changed it.";
                    break;
            }

            if (hasUnsavedEdits)
            {
                question += "\n\n" + UnsavedEditsWarning(new[] { fileName });
            }

            return question;
        }

        /// <summary>Question for Undo All.</summary>
        /// <param name="fileCount">Number of pending files.</param>
        /// <param name="unsavedFileNames">Names of the pending files open with unsaved changes (may be empty).</param>
        internal static string BuildUndoAllQuestion(int fileCount, IReadOnlyCollection<string> unsavedFileNames)
        {
            string question =
                $"Undo the agent's changes to all {fileCount} pending file{(fileCount != 1 ? "s" : "")}?\n\n" +
                "Each file goes back to how it was before the agent changed it. Files the agent created are deleted.";

            if (unsavedFileNames != null && unsavedFileNames.Count > 0)
            {
                question += "\n\n" + UnsavedEditsWarning(unsavedFileNames);
            }

            return question;
        }

        /// <summary>Question shown when "Track agent changes for review" is turned off while files are pending.</summary>
        internal static string BuildTurnOffQuestion(int pendingCount)
        {
            return
                $"{pendingCount} file{(pendingCount != 1 ? "s are" : " is")} still pending review.\n\n" +
                "Turning \"Track agent changes for review\" off discards the review list. The files keep their " +
                "current content, but you will no longer be able to Keep or Undo the agent's changes to them.\n\n" +
                "Turn it off anyway?";
        }

        /// <summary>
        /// The part of an Undo question that names open files with unsaved edits: Undo reloads their editors from
        /// disk, so those edits are lost. Saying so up front is what makes "Yes" an informed choice; without it the
        /// edits either vanish on reload or, if kept, get saved back over the Undo later.
        /// </summary>
        internal static string UnsavedEditsWarning(IReadOnlyCollection<string> fileNames)
        {
            List<string> names = (fileNames ?? new string[0]).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (names.Count == 0)
            {
                return string.Empty;
            }

            string list = string.Join(", ", names.Take(MaxListedFiles));
            if (names.Count > MaxListedFiles)
            {
                list += $" and {names.Count - MaxListedFiles} more";
            }

            bool one = names.Count == 1;
            return
                $"Warning: {list} {(one ? "has" : "have")} unsaved changes in the editor. Undo also discards " +
                $"{(one ? "those edits" : "the unsaved edits in these files")}, including any you made yourself. " +
                "Choose No to save or review them first.";
        }
    }
}
