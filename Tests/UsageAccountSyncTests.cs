/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the Claude CLI account detection that keeps the usage bars off a previous account
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

        [TestMethod]
        public void ParseClaudeAccountId_CombinesAccountAndOrganization()
        {
            string json = "{\"numStartups\":3,\"oauthAccount\":{\"accountUuid\":\"acc-1\",\"organizationUuid\":\"org-1\"," +
                          "\"emailAddress\":\"a@example.com\"}}";

            Assert.AreEqual("acc-1/org-1", ClaudeCodeControl.ParseClaudeAccountId(json));
        }

        [TestMethod]
        public void ParseClaudeAccountId_FallsBackToTheEmailAddress()
        {
            Assert.AreEqual("a@example.com",
                ClaudeCodeControl.ParseClaudeAccountId("{\"oauthAccount\":{\"emailAddress\":\"a@example.com\"}}"));
            Assert.AreEqual("acc-1",
                ClaudeCodeControl.ParseClaudeAccountId("{\"oauthAccount\":{\"accountUuid\":\"acc-1\"}}"));
        }

        /// <summary>
        /// Between <c>/logout</c> and <c>/login</c> the CLI has no <c>oauthAccount</c>, and a file caught
        /// mid-save does not parse. Neither may read as an account of its own, or the page would be
        /// signed out for a switch that has not happened.
        /// </summary>
        [TestMethod]
        public void ParseClaudeAccountId_IsNullWhenSignedOutOrUnreadable()
        {
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountId("{\"numStartups\":3}"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountId("{\"oauthAccount\":null}"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountId("{\"oauthAccount\":{}}"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountId("{\"oauthAccount\":{\"accountUuid\":\"acc"));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountId(""));
            Assert.IsNull(ClaudeCodeControl.ParseClaudeAccountId(null));
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

        [TestMethod]
        public void IsClaudeAccountSwitch_OnlyWhenBothAccountsAreKnownAndDiffer()
        {
            Assert.IsTrue(ClaudeCodeControl.IsClaudeAccountSwitch("acc-1/org-1", "acc-2/org-2"));
            Assert.IsTrue(ClaudeCodeControl.IsClaudeAccountSwitch("acc-1/org-1", "acc-1/org-2"), "Another organization of the same login.");
            Assert.IsFalse(ClaudeCodeControl.IsClaudeAccountSwitch("acc-1/org-1", "ACC-1/ORG-1"));
            Assert.IsFalse(ClaudeCodeControl.IsClaudeAccountSwitch(null, "acc-2/org-2"), "First pairing.");
            Assert.IsFalse(ClaudeCodeControl.IsClaudeAccountSwitch("acc-1/org-1", null), "CLI signed out.");
        }

        [TestMethod]
        public void UsageAccountPairing_KeepsOneAccountPerSide()
        {
            string path = Path.Combine(Path.GetTempPath(), "usage-account-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Assert.IsNull(ClaudeCodeControl.ReadUsageAccountPairing(path, "windows"));

                ClaudeCodeControl.WriteUsageAccountPairing(path, "windows", "acc-1/org-1");
                ClaudeCodeControl.WriteUsageAccountPairing(path, "wsl", "acc-2/org-2");
                Assert.AreEqual("acc-1/org-1", ClaudeCodeControl.ReadUsageAccountPairing(path, "windows"));
                Assert.AreEqual("acc-2/org-2", ClaudeCodeControl.ReadUsageAccountPairing(path, "wsl"));

                ClaudeCodeControl.WriteUsageAccountPairing(path, "windows", null);
                Assert.IsNull(ClaudeCodeControl.ReadUsageAccountPairing(path, "windows"));
                Assert.AreEqual("acc-2/org-2", ClaudeCodeControl.ReadUsageAccountPairing(path, "wsl"));

                File.WriteAllText(path, "{not json");
                Assert.IsNull(ClaudeCodeControl.ReadUsageAccountPairing(path, "wsl"));
                ClaudeCodeControl.WriteUsageAccountPairing(path, "wsl", "acc-3/org-3");
                Assert.AreEqual("acc-3/org-3", ClaudeCodeControl.ReadUsageAccountPairing(path, "wsl"));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
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
            StringAssert.Contains(source, "CoreWebView2BrowsingDataKinds.AllSite");
        }

        /// <summary>
        /// A page sitting in the foreground tab is on the wrong account just the same, so the account
        /// check must run before the tick's visibility guard returns.
        /// </summary>
        [TestMethod]
        public void BackgroundTick_ChecksTheAccountBeforeTheVisibilityGuard()
        {
            string source = HostSource;
            int tick = source.IndexOf("private async void OnUsageBackgroundRefreshTimerTick", StringComparison.Ordinal);
            Assert.IsTrue(tick >= 0);

            int sync = source.IndexOf("await SyncUsageWithClaudeAccountAsync()", tick, StringComparison.Ordinal);
            int guard = source.IndexOf("if (_usageToolWindow?.IsWindowVisible == true) return;", tick, StringComparison.Ordinal);
            Assert.IsTrue(sync > tick && guard > sync, "The account check must precede the visibility guard.");
        }
    }
}
