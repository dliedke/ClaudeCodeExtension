/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Regression tests for provider eligibility of queued prompts in native chat
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class NativePromptQueueTests
    {
        [TestMethod]
        public void CodexWindowsAndWslBothSupportQueuedNativePrompts()
        {
            Assert.IsTrue(ClaudeCodeControl.SupportsQueuedCodexNativeChat(AiProvider.CodexNative));
            Assert.IsTrue(ClaudeCodeControl.SupportsQueuedCodexNativeChat(AiProvider.Codex));
        }

        [TestMethod]
        public void NonCodexProvidersDoNotUseTheCodexPromptQueue()
        {
            Assert.IsFalse(ClaudeCodeControl.SupportsQueuedCodexNativeChat(AiProvider.ClaudeCode));
            Assert.IsFalse(ClaudeCodeControl.SupportsQueuedCodexNativeChat(AiProvider.CursorAgentNative));
            Assert.IsFalse(ClaudeCodeControl.SupportsQueuedCodexNativeChat(null));
        }

        /// <summary>
        /// Devin's follow-ups must not be held back: its ACP agent applies a prompt sent mid-turn to the
        /// work already in flight, which is the whole point of steering it.
        /// </summary>
        [TestMethod]
        public void DevinSteersLiveInsteadOfQueueing()
        {
            Assert.IsTrue(ClaudeCodeControl.SupportsLiveNativeSteering(AiProvider.Devin));
            Assert.IsTrue(ClaudeCodeControl.SupportsLiveNativeSteering(AiProvider.DevinNative));

            Assert.IsFalse(ClaudeCodeControl.SupportsQueuedNativeFollowUps(AiProvider.Devin));
            Assert.IsFalse(ClaudeCodeControl.SupportsQueuedNativeFollowUps(AiProvider.DevinNative));
        }

        [TestMethod]
        public void ProvidersWithoutLiveSteeringKeepTheirOwnPath()
        {
            Assert.IsFalse(ClaudeCodeControl.SupportsLiveNativeSteering(AiProvider.CodexNative));
            Assert.IsFalse(ClaudeCodeControl.SupportsLiveNativeSteering(AiProvider.Codex));
            Assert.IsFalse(ClaudeCodeControl.SupportsLiveNativeSteering(AiProvider.ClaudeCode));
            Assert.IsFalse(ClaudeCodeControl.SupportsLiveNativeSteering(null));
        }

        /// <summary>
        /// Both Codex (queued) and Devin (steered) must release the prompt-submission guard, or Enter
        /// stops working for the rest of the turn.
        /// </summary>
        [TestMethod]
        public void CodexAndDevinBothAcceptFollowUpsWhileBusy()
        {
            Assert.IsTrue(ClaudeCodeControl.AcceptsNativeFollowUpsWhileBusy(AiProvider.CodexNative));
            Assert.IsTrue(ClaudeCodeControl.AcceptsNativeFollowUpsWhileBusy(AiProvider.Codex));
            Assert.IsTrue(ClaudeCodeControl.AcceptsNativeFollowUpsWhileBusy(AiProvider.Devin));
            Assert.IsTrue(ClaudeCodeControl.AcceptsNativeFollowUpsWhileBusy(AiProvider.DevinNative));

            Assert.IsFalse(ClaudeCodeControl.AcceptsNativeFollowUpsWhileBusy(AiProvider.ClaudeCode));
            Assert.IsFalse(ClaudeCodeControl.AcceptsNativeFollowUpsWhileBusy(null));
        }
    }
}
