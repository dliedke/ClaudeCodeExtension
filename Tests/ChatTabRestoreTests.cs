/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Reopen chat tabs when the solution opens": settings defaults, persistence shape, deferred Claude launch
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using System.Threading;
using ClaudeCodeVS;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ChatTabRestoreTests
    {
        [TestMethod]
        public void ReopeningChatTabsIsOnByDefaultWithNothingSaved()
        {
            var fresh = new ClaudeCodeSettings();

            Assert.IsTrue(fresh.ReopenChatTabsOnSolutionOpen);
            Assert.IsNotNull(fresh.WorkspaceChatTabs);
            Assert.AreEqual(0, fresh.WorkspaceChatTabs.Count);
        }

        [TestMethod]
        public void SavedTabsSurviveASettingsRoundTrip()
        {
            var settings = new ClaudeCodeSettings();
            settings.WorkspaceChatTabs["c:\\repo"] = new List<SavedChatTab>
            {
                new SavedChatTab { Provider = AiProvider.ClaudeCode, SessionId = "main-id", IsDefault = true, Title = "Fix build" },
                new SavedChatTab { Provider = AiProvider.CodexNative, SessionId = "thread-1", SelectedModel = "gpt-5", PlanMode = true }
            };

            var loaded = JsonConvert.DeserializeObject<ClaudeCodeSettings>(JsonConvert.SerializeObject(settings));

            List<SavedChatTab> tabs = loaded.WorkspaceChatTabs["c:\\repo"];
            Assert.AreEqual(2, tabs.Count);
            Assert.IsTrue(tabs[0].IsDefault);
            Assert.AreEqual("Fix build", tabs[0].Title);
            Assert.AreEqual(AiProvider.CodexNative, tabs[1].Provider);
            Assert.AreEqual("thread-1", tabs[1].SessionId);
            Assert.AreEqual("gpt-5", tabs[1].SelectedModel);
            Assert.IsTrue(tabs[1].PlanMode);
        }

        [TestMethod]
        public void DeferredClaudeStartLaunchesNothing()
        {
            var events = new List<AgentEvent>();
            var session = new ClaudeStreamJsonSession(new ClaudeSessionOptions
            {
                ExecutablePath = "claude-does-not-exist-" + System.Guid.NewGuid().ToString("N"),
                ResumeSessionId = "abc",
                SessionId = "abc",
                DeferLaunch = true
            });
            session.Received += (s, e) => events.Add(e);

            session.StartAsync(System.IO.Path.GetTempPath(), CancellationToken.None).GetAwaiter().GetResult();

            // A real launch of a missing CLI reports a session error; a deferred start must not try.
            Assert.AreEqual(0, events.Count);
            Assert.IsFalse(session.IsBusy);
            Assert.AreEqual("abc", session.SessionId);
            Assert.AreEqual("abc", session.ResumableSessionId);
            session.Dispose();
        }
    }
}
