/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the ACP start-failure diagnostics — an agent that dies during the handshake must say why
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
    /// A CLI that cannot run at all fails at the very first <c>initialize</c> write, and the raw
    /// exception from that write ("Agent process closed its input pipe") names none of the reasons why.
    /// These tests drive a real child process — a throwaway .cmd — because the whole point of the
    /// diagnostics is what happens to a process that dies, which no amount of mocking reproduces.
    /// </summary>
    [TestClass]
    public class AcpStartDiagnosticsTests
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

        /// <summary>Writes a .cmd that behaves however the test needs and returns its path.</summary>
        private string WriteScript(string body)
        {
            string path = Path.Combine(Path.GetTempPath(), "acp_probe_" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path, "@echo off" + Environment.NewLine + body + Environment.NewLine);
            _scripts.Add(path);
            return path;
        }

        private static AcpSessionOptions OptionsFor(string script, List<string> log)
        {
            return new AcpSessionOptions
            {
                ExecutablePath = script,
                DisplayName = "TestAgent",
                DiagnosticLog = message =>
                {
                    lock (log) { log.Add(message); }
                }
            };
        }

        [TestMethod]
        public async Task StartAsync_AgentDiesImmediately_ErrorNamesTheAgentAndQuotesStderrAsync()
        {
            // Exits before reading a single byte of stdin, which is exactly the shape of the failure
            // this diagnostic exists for: the handshake write lands on an already-broken pipe.
            string script = WriteScript("echo AUTH_TOKEN_EXPIRED 1>&2" + Environment.NewLine + "exit /b 7");
            var log = new List<string>();

            using (var session = new AcpSession(OptionsFor(script, log)))
            {
                InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => session.StartAsync(Path.GetTempPath(), CancellationToken.None));

                // The point of the whole change: the CLI's own words, not just a pipe-level IOException.
                StringAssert.Contains(failure.Message, "TestAgent");
                StringAssert.Contains(failure.Message, "AUTH_TOKEN_EXPIRED");
            }

            string all = string.Join(Environment.NewLine, log);
            StringAssert.Contains(all, "launching:");
            StringAssert.Contains(all, "start FAILED:");
            StringAssert.Contains(all, "AUTH_TOKEN_EXPIRED");

            // The exit code is what separates "crashed" from "was killed"; it must reach the log.
            StringAssert.Contains(all, "code=7");
        }

        [TestMethod]
        public async Task StartAsync_NoDiagnosticSink_StillFailsCleanlyAsync()
        {
            // DiagnosticLog is optional, and a null sink must not turn a start failure into an NRE.
            string script = WriteScript("exit /b 1");

            using (var session = new AcpSession(new AcpSessionOptions
            {
                ExecutablePath = script,
                DisplayName = "TestAgent"
            }))
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => session.StartAsync(Path.GetTempPath(), CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task StartAsync_ThrowingDiagnosticSink_DoesNotBreakTheLaunchPathAsync()
        {
            // The sink is caller-supplied, so a broken one must never be what fails the session:
            // the start still fails, but for the agent's reason and not the sink's.
            string script = WriteScript("echo REAL_REASON 1>&2" + Environment.NewLine + "exit /b 3");

            using (var session = new AcpSession(new AcpSessionOptions
            {
                ExecutablePath = script,
                DisplayName = "TestAgent",
                DiagnosticLog = _ => throw new InvalidOperationException("sink is broken")
            }))
            {
                InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => session.StartAsync(Path.GetTempPath(), CancellationToken.None));

                StringAssert.Contains(failure.Message, "REAL_REASON");
                Assert.IsFalse(failure.Message.Contains("sink is broken"),
                    "the sink's own failure must not be reported as the agent's");
            }
        }
    }
}
