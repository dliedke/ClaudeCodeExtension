/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers resolving a custom model's context window and auto-compact point from CLI settings/env
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ClaudeContextLimitsTests
    {
        [TestMethod]
        public void DeepSeekEndpointUsesOneMillionAndTheSeventyFivePercentTrigger()
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            ClaudeContextLimits.Resolution limits = ClaudeContextLimits.Resolve(
                "https://api.deepseek.com/anthropic",
                new[] { "haiku", "deepseek-flash[1m]" },
                environment);

            Assert.AreEqual(1000000, limits.ContextWindow);
            Assert.AreEqual(786432, limits.AutoCompactWindow);
            Assert.IsTrue(limits.IsDeepSeek);
            Assert.IsFalse(limits.ContextWindowFromSettings);
            Assert.IsFalse(limits.AutoCompactWindowFromSettings);
        }

        [TestMethod]
        public void ExplicitCliSettingsWinOverTheProviderDefaults()
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ClaudeContextLimits.MaxContextTokensVariable] = "500000",
                [ClaudeContextLimits.AutoCompactWindowVariable] = "400000"
            };

            ClaudeContextLimits.Resolution limits = ClaudeContextLimits.Resolve(
                "https://api.deepseek.com/anthropic",
                new[] { "deepseek-flash" },
                environment);

            Assert.AreEqual(500000, limits.ContextWindow);
            Assert.AreEqual(400000, limits.AutoCompactWindow);
            Assert.IsTrue(limits.ContextWindowFromSettings);
            Assert.IsTrue(limits.AutoCompactWindowFromSettings);
        }

        [TestMethod]
        public void OfficialClaudeModelHasNoOverride()
        {
            ClaudeContextLimits.Resolution limits = ClaudeContextLimits.Resolve(
                "https://api.anthropic.com",
                new[] { "claude-opus-5-5" },
                null);

            Assert.AreEqual(0, limits.ContextWindow);
            Assert.AreEqual(0, limits.AutoCompactWindow);
            Assert.IsFalse(limits.IsDeepSeek);
        }

        [TestMethod]
        public void OneMillionSuffixAddsTheWindowButNotTheDeepSeekTrigger()
        {
            ClaudeContextLimits.Resolution limits = ClaudeContextLimits.Resolve(
                "https://api.anthropic.com",
                new[] { "claude-opus-5-5[1m]" },
                null);

            Assert.AreEqual(1000000, limits.ContextWindow);
            Assert.AreEqual(0, limits.AutoCompactWindow);
            Assert.IsFalse(limits.IsDeepSeek);
        }

        [TestMethod]
        public void DetectionIgnoresInvalidSettingsAndIsCaseInsensitive()
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ClaudeContextLimits.MaxContextTokensVariable] = "not-a-number",
                [ClaudeContextLimits.AutoCompactWindowVariable] = "-1"
            };

            ClaudeContextLimits.Resolution limits = ClaudeContextLimits.Resolve(
                "https://API.DEEPSEEK.COM/anthropic",
                new string[0],
                environment);

            Assert.AreEqual(1000000, limits.ContextWindow);
            Assert.AreEqual(786432, limits.AutoCompactWindow);
        }

        [TestMethod]
        public void ExplicitSmallWindowDoesNotGetTheLargeDeepSeekTrigger()
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ClaudeContextLimits.MaxContextTokensVariable] = "200000"
            };

            ClaudeContextLimits.Resolution limits = ClaudeContextLimits.Resolve(
                "https://api.deepseek.com/anthropic",
                new[] { "deepseek-flash" },
                environment);

            Assert.AreEqual(200000, limits.ContextWindow);
            Assert.AreEqual(0, limits.AutoCompactWindow);
        }
    }
}
