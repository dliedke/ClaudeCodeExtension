/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Splits the "Files attached:" block out of a sent prompt so the chat shows thumbnails instead of temp paths (pure)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// A user prompt as the chat shows it: the text the user typed and the files that went with it.
    /// The agent still receives the full prompt with the "Files attached:" list; only the transcript
    /// row is split, so a bubble shows the pictures instead of long temp-folder paths.
    /// </summary>
    public sealed class PromptAttachments
    {
        /// <summary>Header line written by the send path in front of the attachment list.</summary>
        public const string Header = "Files attached:";

        private static readonly PromptAttachments None = new PromptAttachments(string.Empty, new string[0]);

        private PromptAttachments(string body, IReadOnlyList<string> files)
        {
            Body = body;
            Files = files;
        }

        /// <summary>The prompt without the attachment block.</summary>
        public string Body { get; }

        /// <summary>Attached file paths, as Windows paths (a WSL "/mnt/c/..." path is converted back).</summary>
        public IReadOnlyList<string> Files { get; }

        /// <summary>
        /// Finds a "Files attached:" line followed by "  - path" lines and lifts it out of
        /// <paramref name="text"/>. Text without such a block comes back unchanged with no files. The
        /// block need not be first: a pre-send step (pull conflicts, build errors) may sit in front of it.
        /// </summary>
        public static PromptAttachments Split(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return None;
            }

            if (text.IndexOf(Header, StringComparison.Ordinal) < 0)
            {
                return new PromptAttachments(text, None.Files);
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>(lines.Length);
            var files = new List<string>();
            bool removedBlock = false;

            for (int i = 0; i < lines.Length; i++)
            {
                if (!removedBlock && lines[i].Trim() == Header)
                {
                    int j = i + 1;
                    var blockFiles = new List<string>();
                    while (j < lines.Length && lines[j].TrimStart().StartsWith("- ", StringComparison.Ordinal))
                    {
                        string path = lines[j].TrimStart().Substring(2).Trim();
                        if (path.Length > 0)
                        {
                            blockFiles.Add(ToWindowsPath(path));
                        }
                        j++;
                    }

                    if (blockFiles.Count > 0)
                    {
                        files.AddRange(blockFiles);
                        removedBlock = true;

                        // The send path leaves one blank line after the list.
                        if (j < lines.Length && lines[j].Trim().Length == 0)
                        {
                            j++;
                        }

                        i = j - 1;
                        continue;
                    }
                }

                kept.Add(lines[i]);
            }

            if (!removedBlock)
            {
                return new PromptAttachments(text, None.Files);
            }

            return new PromptAttachments(string.Join("\n", kept).Trim('\n', '\r'), files);
        }

        /// <summary>
        /// "/mnt/c/Users/x/a.png" → "C:\Users\x\a.png", so a WSL provider's prompt still finds the file
        /// on Windows. Any other path is returned as is.
        /// </summary>
        public static string ToWindowsPath(string path)
        {
            if (path != null
                && path.Length >= 7
                && path.StartsWith("/mnt/", StringComparison.Ordinal)
                && char.IsLetter(path[5])
                && path[6] == '/')
            {
                var windows = new StringBuilder();
                windows.Append(char.ToUpperInvariant(path[5])).Append(":\\");
                windows.Append(path.Substring(7).Replace('/', '\\'));
                return windows.ToString();
            }

            return path;
        }
    }
}
