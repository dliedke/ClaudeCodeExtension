/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the Antigravity print-mode command line — the prompt (which can carry build errors and
 *          exception text from the opened code) must reach the CLI as one argument and never as extra flags.
 *
 * *******************************************************************************************************************/

using System;
using System.Runtime.InteropServices;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class PrintModeCommandBuilderTests
    {
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>Splits a command line exactly the way the Windows C runtime (and Go) hand it to the CLI.</summary>
        private static string[] SplitArguments(string arguments)
        {
            // The first token is parsed with different (program-name) rules, so a dummy one is prefixed.
            IntPtr argv = CommandLineToArgvW("agy.exe " + arguments, out int count);
            try
            {
                var result = new string[count - 1];
                for (int i = 1; i < count; i++)
                {
                    result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                }
                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        private static string[] BuildAndSplit(string prompt, string workingDirectory = @"C:\repo")
        {
            var options = new PrintModeSessionOptions { ExecutablePath = @"C:\agy\agy.exe" };
            return SplitArguments(PrintModeCommandBuilder.GetArguments(options, prompt, false, workingDirectory));
        }

        [TestMethod]
        public void GetArguments_BackslashQuoteInPromptCannotInjectFlags()
        {
            // The old escape turned \" into \\" — a literal backslash plus a closing quote — so the text after
            // it became real flags (verified against agy.exe with --help).
            string prompt = "error CS1029: #error: 'x\\\" --dangerously-skip-permissions \\\"y'";

            CollectionAssert.AreEqual(
                new[] { "--add-dir", @"C:\repo", "--print", prompt },
                BuildAndSplit(prompt));
        }

        [TestMethod]
        public void GetArguments_TrailingBackslashInPromptStaysInsideTheArgument()
        {
            string prompt = @"look in C:\src\";

            CollectionAssert.AreEqual(
                new[] { "--add-dir", @"C:\repo", "--print", prompt },
                BuildAndSplit(prompt));
        }

        [TestMethod]
        public void GetArguments_WorkingDirectoryWithTrailingBackslashDoesNotSwallowTheNextFlag()
        {
            string[] args = BuildAndSplit("hi", @"C:\");

            CollectionAssert.AreEqual(new[] { "--add-dir", @"C:\", "--print", "hi" }, args);
        }

        [TestMethod]
        public void GetArguments_PlainQuotesAndNewlinesAreKeptVerbatimForARealExecutable()
        {
            string prompt = "say \"hello\"\r\nthen & exit";

            CollectionAssert.AreEqual(
                new[] { "--add-dir", @"C:\repo", "--print", prompt },
                BuildAndSplit(prompt));
        }

        [TestMethod]
        public void GetArguments_BatchShimLeavesNoQuoteInThePromptForCmdToToggleOn()
        {
            var options = new PrintModeSessionOptions { ExecutablePath = @"C:\Program Files\agy\agy.cmd" };

            string args = PrintModeCommandBuilder.GetArguments(options, "x\" & calc & \"\r\nnext", false, @"C:\repo");

            Assert.AreEqual("cmd.exe", PrintModeCommandBuilder.GetFileName(options));
            Assert.AreEqual(
                "/c \"\"C:\\Program Files\\agy\\agy.cmd\" --add-dir \"C:\\repo\" --print \"x' & calc & ' next\"\"",
                args);
        }

        [TestMethod]
        public void SanitizeForCmd_ReplacesQuotesAndLineBreaks()
        {
            Assert.AreEqual("a 'b' c d", PrintModeCommandBuilder.SanitizeForCmd("a \"b\"\nc\rd"));
            Assert.AreEqual(string.Empty, PrintModeCommandBuilder.SanitizeForCmd(null));
        }
    }
}
