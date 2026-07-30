/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for Agent Finish notification summaries
 *
 * *******************************************************************************************************************/

using System;
using System.Globalization;
using System.Threading;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class AgentFinishNotificationTests
    {
        private CultureInfo _originalCulture;

        [TestInitialize]
        public void FixTheCulture()
        {
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        }

        [TestCleanup]
        public void RestoreTheCulture()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        [TestMethod]
        public void CodexNotificationUsesTheDetailedTokenSummary()
        {
            Assert.AreEqual(
                "Agent finished · 2m 22s · 19,829,649 processed · 19,700,000 cached input · 1,234 output",
                ClaudeCodeControl.FormatAgentFinishSummary(
                    TimeSpan.FromSeconds(142),
                    19829649,
                    "19,829,649 processed · 19,700,000 cached input · 1,234 output"));
        }

        [TestMethod]
        public void OtherProvidersRetainTheCompactTokenDelta()
        {
            Assert.AreEqual(
                "Agent finished · 8s · +1,234 tokens",
                ClaudeCodeControl.FormatAgentFinishSummary(
                    TimeSpan.FromSeconds(8),
                    1234,
                    null));
        }
    }
}
