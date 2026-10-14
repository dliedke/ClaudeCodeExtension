/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the throttled Text change notifications of a streaming chat row: the first chunk shows at once,
 *          later ones are announced together, reads always see everything appended, and Complete flushes.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatStreamingThrottleTests
    {
        private TimeSpan _originalInterval;

        [TestInitialize]
        public void SaveInterval()
        {
            _originalInterval = ChatMessageViewModel.StreamingPublishInterval;
        }

        [TestCleanup]
        public void RestoreInterval()
        {
            ChatMessageViewModel.StreamingPublishInterval = _originalInterval;
        }

        private static List<string> TrackChanges(ChatMessageViewModel message)
        {
            var changes = new List<string>();
            message.PropertyChanged += (s, e) => changes.Add(e.PropertyName);
            return changes;
        }

        [TestMethod]
        public void FirstChunkIsAnnouncedAtOnceAndTheRestAreHeldBack()
        {
            // Long enough that no test machine can stall past it between two Append calls.
            ChatMessageViewModel.StreamingPublishInterval = TimeSpan.FromHours(1);

            var message = new ChatMessageViewModel(ChatMessageKind.Assistant) { IsStreaming = true };
            List<string> changes = TrackChanges(message);

            message.Append("Hel");
            message.Append("lo");
            message.Append(" world");

            Assert.AreEqual(1, changes.FindAll(name => name == nameof(ChatMessageViewModel.Text)).Count);

            // A read never sees a stale value, announced or not.
            Assert.AreEqual("Hello world", message.Text);
        }

        [TestMethod]
        public void CompleteAnnouncesTheHeldBackTextAndRendersAllOfIt()
        {
            ChatMessageViewModel.StreamingPublishInterval = TimeSpan.FromHours(1);

            var message = new ChatMessageViewModel(ChatMessageKind.Assistant) { IsStreaming = true };
            message.Append("**bold**");
            List<string> changes = TrackChanges(message);
            message.Append(" and the rest");

            message.Complete();

            Assert.AreEqual(1, changes.FindAll(name => name == nameof(ChatMessageViewModel.Text)).Count);
            Assert.AreEqual("**bold** and the rest", message.RenderedText);
            Assert.IsFalse(message.IsStreaming);
        }

        [TestMethod]
        public void EveryChunkIsAnnouncedWithoutAnInterval()
        {
            ChatMessageViewModel.StreamingPublishInterval = TimeSpan.Zero;

            var message = new ChatMessageViewModel(ChatMessageKind.Assistant) { IsStreaming = true };
            List<string> changes = TrackChanges(message);

            message.Append("a");
            message.Append("b");
            message.Append("c");

            Assert.AreEqual(3, changes.FindAll(name => name == nameof(ChatMessageViewModel.Text)).Count);
            Assert.AreEqual("abc", message.Text);
        }

        [TestMethod]
        public void ReplaceTextWinsOverHeldBackChunks()
        {
            ChatMessageViewModel.StreamingPublishInterval = TimeSpan.FromHours(1);

            var message = new ChatMessageViewModel(ChatMessageKind.Assistant) { IsStreaming = true };
            message.Append("draft");
            message.Append(" still streaming");

            message.ReplaceText("final");
            message.Complete();

            Assert.AreEqual("final", message.Text);
            Assert.AreEqual("final", message.RenderedText);
        }
    }
}
