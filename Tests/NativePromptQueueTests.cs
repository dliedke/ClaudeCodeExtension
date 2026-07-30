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
    }
}
