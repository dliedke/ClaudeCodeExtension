/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the inline usage bars against spend-limit-only usage pages and stale snapshots (issue #182),
 *          and the usage-credits balance that decides whether Fable is offered.
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class UsageSnapshotParsingTests
    {
        private static string ControlSource =>
            RepositoryLayout.ReadText("UI", "ClaudeUsageControl.xaml.cs");

        /// <summary>
        /// Issue #182: a usage-based Enterprise seat shows a single spend meter. The scraper
        /// required a weekly row too, so extract() returned null on every tick and the bars sat
        /// on a two-week-old snapshot.
        /// </summary>
        [TestMethod]
        public void Scraper_DoesNotRequireWeeklyRow()
        {
            Assert.IsFalse(ControlSource.Contains("if (!sessionRow || !weeklyRow) return null;"),
                "A page with only one meter must still produce a snapshot.");
            StringAssert.Contains(ControlSource, "NoWeeklyLimit: !weeklyRow");
        }

        [TestMethod]
        public void LegacySnapshot_WithoutNoWeeklyLimit_KeepsWeeklyRow()
        {
            var snap = JsonConvert.DeserializeObject<UsageSnapshot>(
                "{\"SessionLabel\":\"Current session\",\"SessionPercent\":10,\"WeeklyLabel\":\"All models\",\"WeeklyPercent\":20}");
            Assert.IsFalse(snap.NoWeeklyLimit);
        }

        [TestMethod]
        public void SpendOnlySnapshot_RoundTripsNoWeeklyLimit()
        {
            var snap = JsonConvert.DeserializeObject<UsageSnapshot>(
                "{\"SessionLabel\":\"$381.82 of $500.00 spent\",\"SessionPercent\":76,\"WeeklyLabel\":\"\",\"WeeklyPercent\":0,\"NoWeeklyLimit\":true}");
            Assert.IsTrue(snap.NoWeeklyLimit);
            Assert.IsTrue(JsonConvert.DeserializeObject<UsageSnapshot>(JsonConvert.SerializeObject(snap)).NoWeeklyLimit);
        }

        /// <summary>
        /// Fable needs usage credits: a zero balance read from the page hides it, and anything the
        /// scraper could not read (no section, no amount, older cached snapshot) keeps it offered.
        /// </summary>
        [TestMethod]
        public void IsZeroCreditBalance_OnlyForReadZeroAmounts()
        {
            Assert.IsTrue(UsageSnapshot.IsZeroCreditBalance("R$0"));
            Assert.IsTrue(UsageSnapshot.IsZeroCreditBalance("$0.00"));
            Assert.IsTrue(UsageSnapshot.IsZeroCreditBalance("0,00 €"));
            Assert.IsFalse(UsageSnapshot.IsZeroCreditBalance("R$12,50"));
            Assert.IsFalse(UsageSnapshot.IsZeroCreditBalance("$0.01"));
            Assert.IsFalse(UsageSnapshot.IsZeroCreditBalance("$100"));
            Assert.IsFalse(UsageSnapshot.IsZeroCreditBalance(""));
            Assert.IsFalse(UsageSnapshot.IsZeroCreditBalance(null));
            Assert.IsFalse(UsageSnapshot.IsZeroCreditBalance("R$"));
        }

        [TestMethod]
        public void LegacySnapshot_WithoutCreditsBalance_KeepsFableOffered()
        {
            var snap = JsonConvert.DeserializeObject<UsageSnapshot>(
                "{\"SessionLabel\":\"Current session\",\"SessionPercent\":10}");
            Assert.IsFalse(snap.HasNoUsageCredits());

            snap = JsonConvert.DeserializeObject<UsageSnapshot>(
                "{\"SessionLabel\":\"Current session\",\"SessionPercent\":10,\"UsageCreditsBalance\":\"R$0\"}");
            Assert.IsTrue(snap.HasNoUsageCredits());
        }

        [TestMethod]
        public void Scraper_ReadsUsageCreditsBalance()
        {
            StringAssert.Contains(ControlSource, "getElementById('extra-usage-credits-section')");
            StringAssert.Contains(ControlSource, "UsageCreditsBalance: readUsageCreditsBalance()");
        }

        [TestMethod]
        public void IsUsageSnapshotStale_FlagsOldTimestampsOnly()
        {
            var now = new DateTime(2026, 10, 1, 16, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(ClaudeCodeControl.IsUsageSnapshotStale("2026-09-16T14:33:42.6308672Z", now));
            Assert.IsTrue(ClaudeCodeControl.IsUsageSnapshotStale(now.AddMinutes(-31).ToString("o"), now));
            Assert.IsFalse(ClaudeCodeControl.IsUsageSnapshotStale(now.AddMinutes(-2).ToString("o"), now));
            Assert.IsFalse(ClaudeCodeControl.IsUsageSnapshotStale(null, now));
            Assert.IsFalse(ClaudeCodeControl.IsUsageSnapshotStale("", now));
            Assert.IsFalse(ClaudeCodeControl.IsUsageSnapshotStale("not a date", now));
        }

        /// <summary>
        /// claude.ai turned settings into a dialog: /settings/usage now lands on /new#settings/usage.
        /// The tab read /new as a post-login landing and navigated back to the usage URL on every
        /// load — a reload every few seconds that also undid Switch Account.
        /// </summary>
        [TestMethod]
        public void UsagePage_RecognizesTheSettingsDialogFragment()
        {
            Assert.IsTrue(ClaudeUsagePage.IsUsagePage(new Uri("https://claude.ai/settings/usage")));
            Assert.IsTrue(ClaudeUsagePage.IsUsagePage(new Uri("https://claude.ai/new#settings/usage")));
            Assert.IsTrue(ClaudeUsagePage.IsUsagePage(new Uri("https://claude.ai/chat/abc#settings/usage")));
            Assert.IsFalse(ClaudeUsagePage.IsUsagePage(new Uri("https://claude.ai/new")));
            Assert.IsFalse(ClaudeUsagePage.IsUsagePage(new Uri("https://claude.ai/new#settings/general")));
            Assert.IsFalse(ClaudeUsagePage.IsUsagePage(new Uri("https://example.com/settings/usage")));
            Assert.IsFalse(ClaudeUsagePage.IsUsagePage(null));
        }

        [TestMethod]
        public void UsagePage_AuthPaths()
        {
            Assert.IsTrue(ClaudeUsagePage.IsAuthPath("/login"));
            Assert.IsTrue(ClaudeUsagePage.IsAuthPath("/logout"));
            Assert.IsTrue(ClaudeUsagePage.IsAuthPath("/magic-link"));
            Assert.IsFalse(ClaudeUsagePage.IsAuthPath("/new"));
            Assert.IsFalse(ClaudeUsagePage.IsAuthPath("/"));
            Assert.IsFalse(ClaudeUsagePage.IsAuthPath(null));
        }

        /// <summary>The redirect must leave the usage view alone however claude.ai addresses it.</summary>
        [TestMethod]
        public void Redirect_SkipsTheUsageView()
        {
            StringAssert.Contains(ControlSource, "if (ClaudeUsagePage.IsUsagePage(uri))");
        }

        /// <summary>
        /// Switch Account opens claude.ai's own "Log out" link. Its old path, claude.ai's account
        /// switcher, starts an add-account flow that locks the e-mail and greys out Google sign-in.
        /// </summary>
        [TestMethod]
        public void SwitchAccount_OpensTheLogoutLink()
        {
            StringAssert.Contains(ControlSource, "private const string LogoutUrl = \"https://claude.ai/logout\";");
            StringAssert.Contains(ControlSource, "core.Navigate(LogoutUrl);");
            Assert.IsFalse(ControlSource.Contains("user-menu-button"), "The account menu is no longer opened.");
            Assert.IsTrue(ClaudeUsagePage.IsAuthPath("/logout"), "The post-login redirect must treat /logout as a sign-out.");
            StringAssert.Contains(ControlSource, "await ClearClaudeSiteDataAsync(core);",
                "Switch Account must clear claude.ai's leftover add-account state before logging out.");
        }

        /// <summary>
        /// Switch Account clears only claude.ai/anthropic.com cookies; Google's sign-in cookies stay.
        /// </summary>
        [TestMethod]
        public void SwitchAccount_ClearsOnlyClaudeSiteCookies()
        {
            Assert.IsTrue(ClaudeUsagePage.IsClaudeSiteCookieDomain("claude.ai"));
            Assert.IsTrue(ClaudeUsagePage.IsClaudeSiteCookieDomain(".claude.ai"));
            Assert.IsTrue(ClaudeUsagePage.IsClaudeSiteCookieDomain("a-cdn.claude.ai"));
            Assert.IsTrue(ClaudeUsagePage.IsClaudeSiteCookieDomain(".anthropic.com"));
            Assert.IsFalse(ClaudeUsagePage.IsClaudeSiteCookieDomain("accounts.google.com"));
            Assert.IsFalse(ClaudeUsagePage.IsClaudeSiteCookieDomain(".google.com"));
            Assert.IsFalse(ClaudeUsagePage.IsClaudeSiteCookieDomain("notclaude.ai"));
            Assert.IsFalse(ClaudeUsagePage.IsClaudeSiteCookieDomain(null));
        }

        [TestMethod]
        public void Snapshot_CarriesTheAccountEmail()
        {
            StringAssert.Contains(ControlSource, "AccountEmail: accountEmail");
            var snap = JsonConvert.DeserializeObject<UsageSnapshot>("{\"SessionPercent\":5,\"AccountEmail\":\"a@b.c\"}");
            Assert.AreEqual("a@b.c", snap.AccountEmail);
            Assert.AreEqual("", JsonConvert.DeserializeObject<UsageSnapshot>("{\"SessionPercent\":5}").AccountEmail,
                "Snapshots cached before the field existed read as empty.");
        }
    }
}
