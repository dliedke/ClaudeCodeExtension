/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the Claude CLI account e-mail read for the usage popup and the usage page sign-out paths
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class UsageAccountSyncTests
    {
        private static string ControlSource =>
            RepositoryLayout.ReadText("UI", "ClaudeUsageControl.xaml.cs");

        private static string HostSource =>
            RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Usage.cs");

        /// <summary>The composer usage popup shows the CLI's own account, not the usage page's login.</summary>
        [TestMethod]
        public void ParseClaudeAccountEmail_ReadsTheCliAccount()
        {
            Assert.AreEqual("a@example.com", ClaudeCodeControl.ParseClaudeAccountEmail(
                "{\"oauthAccount\":{\"accountUuid\":\"acc-1\",\"emailAddress\":\" a@example.com \"}}"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountEmail("{\"oauthAccount\":{\"accountUuid\":\"acc-1\"}}"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountEmail("{\"numStartups\":3}"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountEmail("not json"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountEmail(null));
            StringAssert.Contains(RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ChatUsageIndicator.cs"),
                "data.AccountEmail = GetClaudeCliAccountEmail(provider)");
        }

        /// <summary>
        /// A CLI with <c>CLAUDE_CONFIG_DIR</c> set keeps <c>.claude.json</c> in that folder and never
        /// writes the one in the home directory, so watching the home copy would miss every switch.
        /// </summary>
        [TestMethod]
        public void GetClaudeJsonPath_FollowsARelocatedConfigDir()
        {
            const string profile = @"C:\Users\someone";

            Assert.AreEqual(@"C:\Users\someone\.claude.json", ClaudeCodeControl.GetClaudeJsonPath(null, profile));
            Assert.AreEqual(@"C:\Users\someone\.claude.json", ClaudeCodeControl.GetClaudeJsonPath("  ", profile));
            Assert.AreEqual(@"D:\claude-data\.claude.json", ClaudeCodeControl.GetClaudeJsonPath(@" D:\claude-data ", profile));

            string temp = Environment.GetEnvironmentVariable("TEMP");
            Assert.AreEqual(Path.Combine(temp, "claude", ".claude.json"),
                ClaudeCodeControl.GetClaudeJsonPath(@"%TEMP%\claude", profile));

            StringAssert.Contains(HostSource, "ResolveWslPathAsync(\"${CLAUDE_CONFIG_DIR:-$HOME}/.claude.json\")");
        }

        /// <summary>
        /// The claude.ai session lives in the persistent WebView2 profile. Signing out only a live
        /// instance left the old account's cookie in place whenever none was alive, and the next
        /// rebuild signed straight back in as the old account.
        /// </summary>
        [TestMethod]
        public void SignOut_ClearsTheProfileEvenWithoutALiveInstance()
        {
            string source = ControlSource;

            StringAssert.Contains(source, "_clearProfileOnNextBuild = true;");
            StringAssert.Contains(source, "await EnsureAliveAsync(offscreen: !_isHostVisible);");
            StringAssert.Contains(source, "Storage.clearDataForOrigin");
        }

        /// <summary>Only claude.ai / anthropic.com cookies are deleted; Google's remembered accounts stay.</summary>
        [TestMethod]
        public void SignOut_KeepsGoogleCookies()
        {
            Assert.IsTrue(ClaudeUsageControl.IsClaudeSiteCookieDomain("claude.ai"));
            Assert.IsTrue(ClaudeUsageControl.IsClaudeSiteCookieDomain(".claude.ai"));
            Assert.IsTrue(ClaudeUsageControl.IsClaudeSiteCookieDomain("console.anthropic.com"));
            Assert.IsFalse(ClaudeUsageControl.IsClaudeSiteCookieDomain(".google.com"));
            Assert.IsFalse(ClaudeUsageControl.IsClaudeSiteCookieDomain("accounts.google.com"));
            Assert.IsFalse(ClaudeUsageControl.IsClaudeSiteCookieDomain("notclaude.ai"));
            Assert.IsFalse(ClaudeUsageControl.IsClaudeSiteCookieDomain(null));
        }

        /// <summary>
        /// The automatic sign-out on a detected CLI account change kept discarding fresh Google
        /// sign-ins on the usage page; only the explicit Change Account / Sign Out paths sign it out.
        /// </summary>
        [TestMethod]
        public void UsagePage_IsNeverSignedOutByTheAccountCheck()
        {
            string source = HostSource;
            int start = source.IndexOf("private async Task RefreshClaudeCliAccountAsync()", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            int end = source.IndexOf("private async Task RefreshClaudeCliAccountEmailAsync", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            Assert.IsFalse(source.Substring(start, end - start).Contains("SignOut"), "The account check must not sign the page out.");
            Assert.IsFalse(source.Contains("usage-account.json"), "The account pairing file is gone.");
        }
    }
}
