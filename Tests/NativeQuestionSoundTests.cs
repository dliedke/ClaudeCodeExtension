/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for question-sound eligibility in native Claude Code chat
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class NativeQuestionSoundTests
    {
        [TestMethod]
        public void EnabledClaudeNativeInteractionPlaysTheQuestionSound()
        {
            var config = new AgentFinishConfig
            {
                Enabled = true,
                PlayQuestionSound = true
            };

            foreach (AgentInteractionKind kind in new[]
            {
                AgentInteractionKind.Question,
                AgentInteractionKind.PlanReview,
                AgentInteractionKind.ToolApproval
            })
            {
                Assert.IsTrue(ClaudeCodeControl.ShouldPlayNativeQuestionSound(
                    AiProvider.ClaudeCode,
                    config,
                    new AgentInteractionRequest(null) { Kind = kind }));

                Assert.IsTrue(ClaudeCodeControl.ShouldPlayNativeQuestionSound(
                    AiProvider.ClaudeCodeWSL,
                    config,
                    new AgentInteractionRequest(null) { Kind = kind }));
            }
        }

        [TestMethod]
        public void DisabledOrNonClaudeInteractionDoesNotPlayTheQuestionSound()
        {
            var enabled = new AgentFinishConfig
            {
                Enabled = true,
                PlayQuestionSound = true
            };
            var request = new AgentInteractionRequest(null)
            {
                Kind = AgentInteractionKind.Question
            };

            Assert.IsFalse(ClaudeCodeControl.ShouldPlayNativeQuestionSound(
                AiProvider.CodexNative,
                enabled,
                request));
            Assert.IsFalse(ClaudeCodeControl.ShouldPlayNativeQuestionSound(
                AiProvider.ClaudeCode,
                new AgentFinishConfig { Enabled = false, PlayQuestionSound = true },
                request));
            Assert.IsFalse(ClaudeCodeControl.ShouldPlayNativeQuestionSound(
                AiProvider.ClaudeCode,
                new AgentFinishConfig { Enabled = true, PlayQuestionSound = false },
                request));
            Assert.IsFalse(ClaudeCodeControl.ShouldPlayNativeQuestionSound(
                AiProvider.ClaudeCode,
                enabled,
                null));
        }
    }
}
