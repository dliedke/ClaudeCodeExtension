/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the temp launch-script body layout — the code-page decoding failure behind the
 *          Windows Terminal half of issue #138 (non-ASCII characters in the user profile path).
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class LaunchScriptTests
    {
        /// <summary>
        /// Issue #138: cmd.exe decodes a batch line with the console code page active when the
        /// line is read, so "chcp 65001" cannot rescue non-ASCII text on its own line. The chcp
        /// must be emitted on its own first line, with the command chain on the next line, so the
        /// chain (which may hold a UTF-8 path like C:\Users\LarryD’xxx) decodes as UTF-8.
        /// </summary>
        [TestMethod]
        public void BuildLaunchScriptBody_MovesChcpToItsOwnFirstLine()
        {
            string body = ClaudeCodeControl.BuildLaunchScriptBody(
                "/k chcp 65001 >nul && cd /d \"C:\\Users\\LarryD’xxx\\source\" && ping localhost -n 3 >nul && cls && claude");

            string[] lines = body.Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            Assert.AreEqual("@chcp 65001 >nul", lines[0]);
            Assert.AreEqual("cd /d \"C:\\Users\\LarryD’xxx\\source\" && ping localhost -n 3 >nul && cls && claude", lines[1]);
        }

        [TestMethod]
        public void BuildLaunchScriptBody_StripsLeadingSlashK()
        {
            string body = ClaudeCodeControl.BuildLaunchScriptBody("/k chcp 65001 >nul && cd /d \"C:\\proj\"");

            Assert.IsFalse(body.StartsWith("/k"), "the /k switch belongs to cmd.exe, not the script");
            StringAssert.StartsWith(body, "@chcp 65001 >nul\r\n");
        }

        /// <summary>
        /// The WSL launch form also starts with the chcp prefix; the rest of its chain (cls +
        /// wsl bash -lic "...") must survive on the second line unchanged.
        /// </summary>
        [TestMethod]
        public void BuildLaunchScriptBody_PreservesWslChainOnSecondLine()
        {
            string body = ClaudeCodeControl.BuildLaunchScriptBody(
                "/k chcp 65001 >nul && cls && wsl bash -lic \"cd '/mnt/c/proj' && claude\"");

            Assert.AreEqual(
                "@chcp 65001 >nul\r\ncls && wsl bash -lic \"cd '/mnt/c/proj' && claude\"",
                body);
        }

        /// <summary>
        /// A command chain that does not begin with the chcp prefix (none is built today, but the
        /// default CMD branch could change) must pass through untouched rather than gaining a
        /// spurious chcp line.
        /// </summary>
        [TestMethod]
        public void BuildLaunchScriptBody_LeavesBodiesWithoutChcpPrefixUntouched()
        {
            Assert.AreEqual(
                "cd /d \"C:\\proj\"",
                ClaudeCodeControl.BuildLaunchScriptBody("/k cd /d \"C:\\proj\""));
        }
    }
}
