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
    }
}
