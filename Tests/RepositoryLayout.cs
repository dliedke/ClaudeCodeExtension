/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Locates the repository root from the test output directory so the guard tests can read
 *          the project metadata files (csproj, vsixmanifest, AssemblyInfo, README) as data.
 *
 * *******************************************************************************************************************/

using System;
using System.IO;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Resolves repository paths for tests. Tests run from Tests\bin\&lt;config&gt;\net472, so the root
    /// is found by walking up until the solution file appears rather than by counting directory levels
    /// (which breaks whenever the output path changes).
    /// </summary>
    internal static class RepositoryLayout
    {
        private const string SolutionFileName = "ClaudeCodeExtension.sln";

        private static readonly Lazy<string> _root = new Lazy<string>(FindRoot);

        /// <summary>Absolute path to the repository root.</summary>
        public static string Root => _root.Value;

        /// <summary>Absolute path to a file or directory relative to the repository root.</summary>
        public static string Path(params string[] relativeSegments)
        {
            string combined = Root;
            foreach (string segment in relativeSegments)
            {
                combined = System.IO.Path.Combine(combined, segment);
            }
            return combined;
        }

        /// <summary>Reads a repository file as text.</summary>
        public static string ReadText(params string[] relativeSegments)
        {
            string full = Path(relativeSegments);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    $"Expected repository file not found: {full}. " +
                    "If the file was renamed or moved, update the guard test with it.", full);
            }
            return File.ReadAllText(full);
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, SolutionFileName)))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate {SolutionFileName} walking up from {AppDomain.CurrentDomain.BaseDirectory}.");
        }
    }
}
