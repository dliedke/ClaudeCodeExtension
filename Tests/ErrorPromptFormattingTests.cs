/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the prompts generated for "Auto-send build errors" and "Auto-send runtime errors".
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ErrorPromptFormattingTests
    {
        private static List<string> Lines(string prefix, int count)
        {
            return Enumerable.Range(1, count).Select(i => $"{prefix} {i}").ToList();
        }

        [TestMethod]
        public void FormatBuildErrorPrompt_UsesSingularWordingForASingleErrorAndWarning()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(
                new List<string> { "App.cs(10,5): CS0103 name not found" },
                new List<string> { "App.cs(12,1): CS0168 unused variable" });

            StringAssert.Contains(prompt, "finished with 1 error and 1 warning.");
        }

        [TestMethod]
        public void FormatBuildErrorPrompt_UsesPluralWordingForMultipleIssues()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(Lines("error", 3), Lines("warning", 2));

            StringAssert.Contains(prompt, "finished with 3 errors and 2 warnings.");
        }

        [TestMethod]
        public void FormatBuildErrorPrompt_OmitsTheWarningsSectionWhenThereAreNone()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(Lines("error", 2), new List<string>());

            StringAssert.Contains(prompt, "finished with 2 errors.");
            Assert.IsFalse(prompt.Contains("Warnings:"), "No warnings means no warnings section.");
        }

        [TestMethod]
        public void FormatBuildErrorPrompt_NumbersEachErrorLine()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(
                new List<string> { "first", "second" }, new List<string>());

            StringAssert.Contains(prompt, "1. first");
            StringAssert.Contains(prompt, "2. second");
        }

        /// <summary>
        /// A build with hundreds of issues must not flood the terminal: the list stops at the cap
        /// and states how many were left out. The remainder count is what makes the truncation
        /// honest rather than a silent cut.
        /// </summary>
        [TestMethod]
        public void FormatBuildErrorPrompt_CapsTheErrorListAndReportsTheRemainder()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(Lines("error", 45), new List<string>());

            StringAssert.Contains(prompt, "40. error 40");
            Assert.IsFalse(prompt.Contains("41. error 41"), "Errors past the cap of 40 must not be listed.");
            StringAssert.Contains(prompt, "... and 5 errors more.");
        }

        [TestMethod]
        public void FormatBuildErrorPrompt_CapsTheWarningListSeparately()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(Lines("error", 1), Lines("warning", 21));

            StringAssert.Contains(prompt, "20. warning 20");
            StringAssert.Contains(prompt, "... and 1 warning more.");
        }

        [TestMethod]
        public void FormatBuildErrorPrompt_AsksForARebuildAfterTheFix()
        {
            string prompt = ClaudeCodeControl.FormatBuildErrorPrompt(Lines("error", 1), new List<string>());

            StringAssert.EndsWith(prompt, "rebuild the solution to confirm the errors are resolved.");
        }

        [TestMethod]
        public void FormatRuntimeErrorPrompt_WrapsTheExceptionDetailsWithInstructions()
        {
            string prompt = ClaudeCodeControl.FormatRuntimeErrorPrompt(
                "System.NullReferenceException: Object reference not set to an instance of an object.");

            StringAssert.StartsWith(prompt, "While running under the Visual Studio debugger");
            StringAssert.Contains(prompt, "System.NullReferenceException");
            StringAssert.EndsWith(prompt, "run the application again to confirm the exception no longer occurs.");
        }
    }
}
