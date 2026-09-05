/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers what an ACP launch does when the agent will not run on the model the user picked
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Issue #151 round 11. A model the agent does not list used to be a notice in the chat and nothing
    /// more: the session stayed up on the agent's own default model while the composer went on showing
    /// the model the user picked, so every answer silently came from somewhere else. It is now a start
    /// failure, which is what lets <c>ClaudeCodeControl</c> roll back to the embedded terminal — the one
    /// surface where the model can actually be changed, since the 🤖 menu is hidden in native mode.
    /// <para>
    /// These drive a real child process (a throwaway .cmd that answers the handshake with canned
    /// JSON-RPC frames), because the behaviour under test lives in the handshake sequence itself.
    /// </para>
    /// </summary>
    [TestClass]
    public class AcpModelSelectionTests
    {
        private readonly List<string> _scripts = new List<string>();

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string script in _scripts)
            {
                try { File.Delete(script); } catch (IOException) { /* the OS still has it open; harmless */ }
            }
        }

        [TestMethod]
        public async Task StartAsync_ModelNotOfferedByTheAgent_FailsInsteadOfRunningOnTheDefaultAsync()
        {
            // What the reporter hit: "swe-1-6" is in the extension's model catalog but not in the list
            // the agent publishes, so nothing can apply it.
            string script = WriteFakeAgent("good-model");

            using (var session = new AcpSession(OptionsFor(script, "swe-1-6")))
            {
                AgentModelUnavailableException failure =
                    await Assert.ThrowsExceptionAsync<AgentModelUnavailableException>(
                        () => session.StartAsync(Path.GetTempPath(), CancellationToken.None));

                Assert.AreEqual("swe-1-6", failure.ModelName);
                Assert.AreEqual("TestAgent", failure.AgentDisplayName);
                StringAssert.Contains(failure.Message, "swe-1-6",
                    "the notice shown before the terminal takes over quotes this message, so it has to " +
                    "name the model the user needs to change.");
            }
        }

        [TestMethod]
        public async Task StartAsync_ModelTheAgentOffers_StartsNormallyAsync()
        {
            string script = WriteFakeAgent("good-model");

            using (var session = new AcpSession(OptionsFor(script, "good-model")))
            {
                await session.StartAsync(Path.GetTempPath(), CancellationToken.None);
                Assert.AreEqual("s-1", session.SessionId);
            }
        }

        [TestMethod]
        public async Task StartAsync_AgentPublishesNoModelPicker_KeepsItsDefaultWithoutFailingAsync()
        {
            // Nothing failed to load here — the agent has no model concept over ACP at all, so refusing
            // to launch it (and dropping the user onto the terminal) would be wrong.
            string script = WriteFakeAgent(offeredModel: null);

            using (var session = new AcpSession(OptionsFor(script, "swe-1-6")))
            {
                await session.StartAsync(Path.GetTempPath(), CancellationToken.None);
                Assert.AreEqual("s-1", session.SessionId);
            }
        }

        /// <summary>
        /// A stand-in ACP agent: reads one request line, answers it, repeats. The response ids are
        /// canned (1 = initialize, 2 = session/new, 3 = session/set_config_option) because
        /// <see cref="AcpSession"/> numbers its requests from 1 and awaits each before sending the next.
        /// <paramref name="offeredModel"/> null publishes no model picker at all.
        /// <para>
        /// The reading half is PowerShell rather than plain batch on purpose: the protocol writes each
        /// request terminated by a bare "\n" (see <c>JsonLineProcessHost.WriteLineAsync</c> — CRLF would
        /// corrupt these agents' line framing), and cmd's own <c>set /p</c> waits for a CR that never
        /// arrives, so a batch-only fake agent hangs forever instead of answering. The .cmd around it is
        /// what makes <c>AcpCommandBuilder</c> take its "npm shim" path and launch through cmd.exe,
        /// which is how the real opencode/reasonix shims are launched.
        /// </para>
        /// </summary>
        private string WriteFakeAgent(string offeredModel)
        {
            string configOptions = offeredModel == null
                ? "[]"
                : "[{\"id\":\"model\",\"options\":[{\"value\":\"" + offeredModel + "\",\"name\":\"Good Model\"}]}]";

            string stem = Path.Combine(Path.GetTempPath(), "acp_model_" + Guid.NewGuid().ToString("N"));
            string agentPath = stem + ".ps1";
            string launcherPath = stem + ".cmd";

            File.WriteAllText(agentPath, string.Join(Environment.NewLine,
                "$null = [Console]::In.ReadLine()",
                "[Console]::Out.WriteLine('{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"TestAgent\"}}}')",
                "$null = [Console]::In.ReadLine()",
                "[Console]::Out.WriteLine('{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"sessionId\":\"s-1\",\"configOptions\":" + configOptions + "}}')",
                "$null = [Console]::In.ReadLine()",
                "[Console]::Out.WriteLine('{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{}}')",
                // Keeps the pipe open so disposing the session is what ends the process, rather than the
                // agent exiting underneath a test that is still running.
                "$null = [Console]::In.ReadLine()"));

            File.WriteAllText(launcherPath, "@echo off" + Environment.NewLine +
                "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + agentPath + "\"" + Environment.NewLine);

            _scripts.Add(agentPath);
            _scripts.Add(launcherPath);
            return launcherPath;
        }

        private static AcpSessionOptions OptionsFor(string script, string modelName)
        {
            return new AcpSessionOptions
            {
                ExecutablePath = script,
                DisplayName = "TestAgent",
                ModelName = modelName
            };
        }
    }
}
