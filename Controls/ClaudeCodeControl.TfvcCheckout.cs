/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Auto-checkout TFVC files" feature (Claude Code, native mode). A TFVC server workspace keeps
 *          every file read-only until it is checked out, and the agent edits files behind Visual Studio's
 *          back, so it either fails or clears the read-only flag itself. Before Claude writes a file, the
 *          native session asks this class to check it out through Visual Studio's own source-control
 *          layer (the same call the editor makes when you type in a committed file).
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Pure helpers for the TFVC pre-edit checkout — no Visual Studio services, so they are unit-tested.
    /// </summary>
    internal static class TfvcSolution
    {
        /// <summary>
        /// The section Visual Studio writes into a solution bound to TFVC source control (it carries the
        /// server URL and the per-project bindings). Git-bound and unbound solutions never have it.
        /// </summary>
        internal const string BindingSection = "GlobalSection(TeamFoundationVersionControl)";

        // The solution normally sits in the workspace folder; a custom working directory may point at a
        // subfolder of it, so a few parents are searched too.
        private const int MaxParentLevels = 4;

        internal static bool IsBoundSolutionText(string solutionText)
        {
            return !string.IsNullOrEmpty(solutionText) &&
                   solutionText.IndexOf(BindingSection, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when a solution in <paramref name="workspace"/> (or a parent folder) is bound to TFVC.</summary>
        internal static bool IsBoundSolutionNear(string workspace)
        {
            try
            {
                string directory = workspace;

                for (int level = 0; level <= MaxParentLevels && !string.IsNullOrEmpty(directory) && Directory.Exists(directory); level++)
                {
                    foreach (string solution in Directory.GetFiles(directory, "*.sln"))
                    {
                        if (IsBoundSolutionText(File.ReadAllText(solution)))
                        {
                            return true;
                        }
                    }

                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TFVC solution detection failed: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// The path Claude reports for a file. Under WSL it is a Linux path (<c>/mnt/c/dir/file</c>) that
        /// Visual Studio cannot use, so it is mapped back to the drive path.
        /// </summary>
        internal static string ToWindowsPath(string path, bool isWsl)
        {
            if (!isWsl || string.IsNullOrEmpty(path) || !path.StartsWith("/mnt/", StringComparison.Ordinal) || path.Length < 7)
            {
                return path;
            }

            char drive = path[5];
            if (!char.IsLetter(drive) || (path.Length > 6 && path[6] != '/'))
            {
                return path;
            }

            string rest = path.Length > 7 ? path.Substring(7).Replace('/', '\\') : string.Empty;
            return char.ToUpperInvariant(drive) + ":\\" + rest;
        }

        /// <summary>An existing file with the read-only flag set — what an unchecked-out TFVC file looks like.</summary>
        internal static bool IsReadOnlyFile(string path)
        {
            try
            {
                return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The reason handed back to the model when a checkout did not go through. It has to say what not
        /// to do as well: the failure mode this feature exists for is the agent clearing the read-only
        /// flag on its own.
        /// </summary>
        internal static string BuildFailureReason(IReadOnlyList<string> files)
        {
            var sb = new StringBuilder("Visual Studio could not check out ");
            sb.Append(string.Join(", ", files));
            sb.Append(" from Team Foundation Version Control (it may be locked or checked out by someone else). ");
            sb.Append("Do not change the file's read-only attribute or try to work around it; stop and tell the user to check the file out.");
            return sb.ToString();
        }
    }

    public partial class ClaudeCodeControl
    {
        /// <summary>
        /// The pre-edit callback for a Claude native session, or null when nothing should be hooked —
        /// the setting is off, or the workspace is not a TFVC-bound solution (so every other repository
        /// pays no per-edit round trip).
        /// </summary>
        private Func<IReadOnlyList<string>, Task<string>> CreateTfvcCheckoutCallback(string workspace, bool isWsl)
        {
            if (_settings?.AutoTfvcCheckout != true || !TfvcSolution.IsBoundSolutionNear(workspace))
            {
                return null;
            }

            return paths => CheckOutFilesBeforeEditAsync(paths, isWsl);
        }

        /// <summary>
        /// Checks out every read-only file among <paramref name="paths"/> through
        /// <c>IVsQueryEditQuerySave2</c> — the routine Visual Studio's editor calls, which hands the file
        /// to whichever source-control provider owns it. Returns null when all files are writable
        /// afterwards, otherwise the reason to give the model. Files that are already writable or do not
        /// exist yet are left alone.
        /// </summary>
        private async Task<string> CheckOutFilesBeforeEditAsync(IReadOnlyList<string> paths, bool isWsl)
        {
            // The service is a UI-thread service; the CLI's callback arrives on a thread-pool thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var queryEdit = Package.GetGlobalService(typeof(SVsQueryEditQuerySave)) as IVsQueryEditQuerySave2;
            if (queryEdit == null)
            {
                return null;
            }

            uint flags = (uint)tagVSQueryEditFlags.QEF_ForceEdit_NoPrompting | (uint)tagVSQueryEditFlags.QEF_DisallowInMemoryEdits;
            var failed = new List<string>();

            foreach (string path in paths)
            {
                string file = TfvcSolution.ToWindowsPath(path, isWsl);
                if (!TfvcSolution.IsReadOnlyFile(file))
                {
                    continue;
                }

                try
                {
                    int hr = queryEdit.QueryEditFiles(flags, 1, new[] { file }, null, null, out uint verdict, out uint moreInfo);
                    Debug.WriteLine($"TFVC checkout '{file}': hr=0x{hr:X8} verdict={verdict} moreInfo=0x{moreInfo:X}");

                    bool checkedOut = ErrorHandler.Succeeded(hr) &&
                                      verdict == (uint)tagVSQueryEditResult.QER_EditOK &&
                                      !TfvcSolution.IsReadOnlyFile(file);
                    if (!checkedOut)
                    {
                        failed.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"TFVC checkout '{file}' threw: {ex}");
                    failed.Add(file);
                }
            }

            return failed.Count == 0 ? null : TfvcSolution.BuildFailureReason(failed);
        }
    }
}
