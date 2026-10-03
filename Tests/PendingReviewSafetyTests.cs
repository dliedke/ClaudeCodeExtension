/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the pending review's safety net (issue #183 review fixes): the confirmation wording, in particular
 *          the unsaved-edits warning, and source-level guards for the parts that need a running Visual Studio
 *          (VS message boxes, editor-aware Undo, Compare, turn-end reveal, turn-off confirmation).
 *
 * *******************************************************************************************************************/

using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class PendingReviewSafetyTests
    {
        // ---- Confirmation wording ------------------------------------------------------------------

        [TestMethod]
        public void UndoQuestion_NamesTheFileAndTheKindOfChange()
        {
            StringAssert.StartsWith(PendingReviewMessages.BuildUndoQuestion("a.txt", ChangeType.Modified, false), "Undo the agent's changes to a.txt?");
            StringAssert.StartsWith(PendingReviewMessages.BuildUndoQuestion("a.txt", ChangeType.Created, false), "Delete a.txt?");
            StringAssert.StartsWith(PendingReviewMessages.BuildUndoQuestion("a.txt", ChangeType.Deleted, false), "Restore a.txt?");
        }

        [TestMethod]
        public void UndoQuestion_WarnsOnlyWhenTheFileHasUnsavedEdits()
        {
            string clean = PendingReviewMessages.BuildUndoQuestion("notas.txt", ChangeType.Modified, false);
            string dirty = PendingReviewMessages.BuildUndoQuestion("notas.txt", ChangeType.Modified, true);

            Assert.IsFalse(clean.Contains("unsaved"), clean);
            StringAssert.Contains(dirty, "notas.txt has unsaved changes in the editor");
            StringAssert.Contains(dirty, "including any you made yourself");
        }

        [TestMethod]
        public void UndoQuestion_WarnsForACreatedFileWithUnsavedEdits()
        {
            string question = PendingReviewMessages.BuildUndoQuestion("temporal.txt", ChangeType.Created, true);

            StringAssert.StartsWith(question, "Delete temporal.txt?");
            StringAssert.Contains(question, "temporal.txt has unsaved changes in the editor");
        }

        [TestMethod]
        public void UndoAllQuestion_CountsFilesAndListsOnlyTheUnsavedOnes()
        {
            string none = PendingReviewMessages.BuildUndoAllQuestion(3, new string[0]);
            string some = PendingReviewMessages.BuildUndoAllQuestion(3, new[] { "a.txt", "b.txt" });

            StringAssert.StartsWith(none, "Undo the agent's changes to all 3 pending files?");
            Assert.IsFalse(none.Contains("unsaved"), none);
            StringAssert.Contains(some, "a.txt, b.txt have unsaved changes in the editor");
        }

        [TestMethod]
        public void UndoAllQuestion_UsesTheSingularForOneFile()
        {
            StringAssert.StartsWith(PendingReviewMessages.BuildUndoAllQuestion(1, null), "Undo the agent's changes to all 1 pending file?");
        }

        [TestMethod]
        public void UnsavedEditsWarning_SummarizesLongLists()
        {
            string[] names = Enumerable.Range(1, PendingReviewMessages.MaxListedFiles + 3).Select(i => $"f{i}.cs").ToArray();

            string warning = PendingReviewMessages.UnsavedEditsWarning(names);

            StringAssert.Contains(warning, $"f{PendingReviewMessages.MaxListedFiles}.cs and 3 more");
            Assert.IsFalse(warning.Contains($"f{PendingReviewMessages.MaxListedFiles + 1}.cs"), warning);
        }

        [TestMethod]
        public void UnsavedEditsWarning_IsEmptyWithoutFiles()
        {
            Assert.AreEqual(string.Empty, PendingReviewMessages.UnsavedEditsWarning(null));
            Assert.AreEqual(string.Empty, PendingReviewMessages.UnsavedEditsWarning(new[] { "", null }));
        }

        [TestMethod]
        public void TurnOffQuestion_SaysTheListIsDiscardedButFilesAreKept()
        {
            string one = PendingReviewMessages.BuildTurnOffQuestion(1);
            string many = PendingReviewMessages.BuildTurnOffQuestion(4);

            StringAssert.StartsWith(one, "1 file is still pending review.");
            StringAssert.StartsWith(many, "4 files are still pending review.");
            StringAssert.Contains(many, "discards the review list");
            StringAssert.Contains(many, "The files keep their current content");
        }

        // ---- Source-level guards (these paths need a running Visual Studio) -------------------------

        private static string PendingReviewSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.PendingReview.cs");

        [TestMethod]
        public void PendingReview_AsksThroughVisualStudiosMessageBox_NotWpf()
        {
            // A WPF MessageBox left open ~20 s made Visual Studio report the extension as unresponsive.
            string source = PendingReviewSource;

            Assert.IsFalse(Regex.IsMatch(source, @"\bMessageBox\.Show\("), "Use ConfirmPendingReviewAction / ShowPendingReviewWarning instead of System.Windows.MessageBox.");
            StringAssert.Contains(source, "VsShellUtilities.ShowMessageBox(");
        }

        [TestMethod]
        public void PendingReview_UndoGoesThroughTheEditor_NotOnlyTheDisk()
        {
            string source = PendingReviewSource;
            string restore = Slice(source, "private bool TryRestorePendingFile(", "private static void CloseDocumentDiscardingChanges(");

            // Writing only the disk let an unsaved editor put the agent's change back on the next save.
            StringAssert.Contains(restore, "RestoreFileContent(path, baseline.Content)");
            StringAssert.Contains(restore, "CloseDocumentDiscardingChanges(path)");
            Assert.IsFalse(restore.Contains("File.WriteAllBytes"), "Restore through RestoreFileContent so open editors are reloaded.");

            string reload = Slice(source, "private static void RestoreFileContent(", "#endregion");
            StringAssert.Contains(reload, "IgnoreFileChanges(1)");
            StringAssert.Contains(reload, "ReloadDocData(");
            StringAssert.Contains(reload, "IgnoreFileChanges(0)");
        }

        [TestMethod]
        public void PendingReview_UndoWarnsAboutUnsavedEditsBeforeAsking()
        {
            string source = PendingReviewSource;
            string single = Slice(source, "private void OnDiffViewerUndoRequested(", "private void OnDiffViewerUndoAllRequested(");
            string all = Slice(source, "private void OnDiffViewerUndoAllRequested(", "private void OnDiffViewerCompareRequested(");

            StringAssert.Contains(single, "FindOpenDocumentsWithUnsavedChanges(");
            StringAssert.Contains(single, "PendingReviewMessages.BuildUndoQuestion(");
            StringAssert.Contains(all, "FindOpenDocumentsWithUnsavedChanges(");
            StringAssert.Contains(all, "PendingReviewMessages.BuildUndoAllQuestion(");
        }

        [TestMethod]
        public void PendingReview_CompareOpensVisualStudiosDiffWindow()
        {
            string viewer = RepositoryLayout.ReadText("UI", "DiffViewerControl.xaml.cs");
            string source = PendingReviewSource;

            StringAssert.Contains(viewer, "CompareRequested?.Invoke(this, file)");
            StringAssert.Contains(source, "control.CompareRequested += OnDiffViewerCompareRequested;");
            StringAssert.Contains(source, "OpenComparisonWindow2(");
        }

        [TestMethod]
        public void PendingReview_TurnEndRevealsTheChangesView()
        {
            string endTurn = Slice(PendingReviewSource, "private void EndPendingReviewTurn(", "private async Task RevealPendingReviewAfterTurnAsync(");

            StringAssert.Contains(endTurn, "RevealPendingReviewAfterTurnAsync(tracker, pendingCount)");
        }

        [TestMethod]
        public void Settings_AsksBeforeTurningReviewTrackingOffWithFilesPending()
        {
            string dialog = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs");

            StringAssert.Contains(dialog, "PendingReviewMessages.BuildTurnOffQuestion(pendingCount)");
        }

        [TestMethod]
        public void TestScript_RestoresPackagesBeforeBuilding()
        {
            // A fresh clone has no project.assets.json; without -restore the first run fails with NETSDK1004.
            StringAssert.Contains(RepositoryLayout.ReadText("test.cmd"), "-restore -t:Build");
        }

        private static string Slice(string source, string startMarker, string endMarker)
        {
            int start = source.IndexOf(startMarker, System.StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Marker not found: {startMarker}");
            int end = source.IndexOf(endMarker, start + startMarker.Length, System.StringComparison.Ordinal);
            Assert.IsTrue(end > start, $"Marker not found after {startMarker}: {endMarker}");
            return source.Substring(start, end - start);
        }
    }
}
