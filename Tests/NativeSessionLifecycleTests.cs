/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the native-mode session lifecycle — one agent at a time, and a CLI that dies says why
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Switching agent used to start the new session straight on top of the running one: the previous
    /// CLI stayed alive, still subscribed to the control's event handler, and its events kept landing
    /// in the chat that had replaced it — which is what "I switch back from Devin and it starts all
    /// kinds of errors" looked like. The starts also overlapped: Devin's handshake takes seconds, and
    /// a switch made during it tore down the session the other switch had just published.
    /// <para>
    /// The lifecycle itself lives on a WPF control that cannot be instantiated without a shell, so it
    /// is guarded at the source level, in the style of <see cref="PackageVersionGuardTests"/>.
    /// </para>
    /// </summary>
    [TestClass]
    public class NativeSessionLifecycleTests
    {
        private static string NativeModeSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs");

        [TestMethod]
        public void NativeStart_IsReachedOnlyThroughTheSerializedEntryPoint()
        {
            string source = NativeModeSource;

            // Definition plus exactly one call — the one inside StartNativeModeAsync, which holds the
            // lifecycle lock. Any other caller would be able to start an agent while another start is
            // still running, which is the race this whole gate exists for.
            int mentions = Regex.Matches(source, @"StartNativeModeCoreAsync\s*\(").Count;
            Assert.AreEqual(2, mentions,
                "StartNativeModeCoreAsync must be called only from StartNativeModeAsync, which serializes starts.");

            string gate = ExtractMethodBody(source, "private async Task<NativeStartOutcome> StartNativeModeAsync()");

            StringAssert.Contains(gate, "_nativeLifecycleSemaphore.WaitAsync()",
                "The native start must be serialized against a concurrent agent switch.");
            StringAssert.Contains(gate, "_nativeLifecycleSemaphore.Release()");
            StringAssert.Contains(gate, "_nativeLaunchTicket",
                "A start superseded by a later switch must skip itself instead of launching an agent nobody asked for.");
            StringAssert.Contains(gate, "EndActiveNativeSessionAsync()",
                "The running session must be ended before a new one is started, or its CLI is orphaned.");
        }

        [TestMethod]
        public void EndingTheSessionAlsoGivesThePanelBackToTheTerminal()
        {
            string body = ExtractMethodBody(NativeModeSource, "private async Task EndActiveNativeSessionAsync()");

            // Without this, switching from a native agent to one that has no structured channel left
            // the dead chat covering the terminal that was launched behind it.
            StringAssert.Contains(body, "ShutdownNativeModeAsync()");
            StringAssert.Contains(body, "ShowNativeTranscript(false)");
        }

        /// <summary>
        /// Returns the text of a method's body, located by its signature and closed by brace matching.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Method not found; update this guard with the rename: {signature}");

            int open = source.IndexOf('{', start + signature.Length);
            Assert.IsTrue(open >= 0, $"No body found for: {signature}");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail($"Unbalanced braces while reading: {signature}");
            return string.Empty;
        }
    }

    /// <summary>
    /// The one-shot adapters (Codex, Cursor Agent) launch a process per turn, so a CLI that refuses to
    /// run dies between the launch and the prompt write — and the only thing that survives that is
    /// "Agent process closed its input pipe", which names none of the reasons why. Like
    /// <see cref="AcpStartDiagnosticsTests"/>, these drive a real child process: the behaviour under
    /// test is what happens to a process that dies.
    /// </summary>
    [TestClass]
    public class OneShotTurnDiagnosticsTests
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

        private string WriteScript(string body)
        {
            string path = Path.Combine(Path.GetTempPath(), "oneshot_probe_" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path, "@echo off" + Environment.NewLine + body + Environment.NewLine);
            _scripts.Add(path);
            return path;
        }

        /// <summary>A protocol that adds nothing to the command line and reads nothing back.</summary>
        private class SilentProtocol : IOneShotTurnProtocol
        {
            public string BuildArguments(OneShotSessionOptions options, string resumeSessionId) => string.Empty;

            public void HandleLine(string line, OneShotTurnSink sink) { }
        }

        [TestMethod]
        public async Task SendAsync_CliDiesBeforeReadingThePrompt_ErrorQuotesItsStderrAsync()
        {
            // Exits without reading a byte of stdin: the prompt write then lands on a broken pipe, and
            // the reason for the death is only in what the CLI printed on its way out.
            string script = WriteScript("echo SESSION_NOT_FOUND 1>&2" + Environment.NewLine + "exit /b 7");

            var errors = new List<string>();
            var options = new OneShotSessionOptions { ExecutablePath = script, DisplayName = "TestAgent" };

            using (var session = new OneShotResumeSession(options, new SilentProtocol()))
            {
                session.Received += (s, e) =>
                {
                    if (e.Kind == AgentEventKind.SessionError)
                    {
                        lock (errors) { errors.Add(e.Text ?? string.Empty); }
                    }
                };

                await session.StartAsync(Path.GetTempPath(), CancellationToken.None);

                try
                {
                    await session.SendAsync("hi", CancellationToken.None);
                }
                catch (Exception)
                {
                    // Whether the write loses the race with the exit (throws) or lands just before it
                    // (reported through the exit code) is timing; the message is what matters.
                }
            }

            string reported;
            lock (errors) { reported = string.Join(" | ", errors); }

            Assert.AreNotEqual(0, errors.Count, "A turn whose CLI died must report an error.");
            StringAssert.Contains(reported, "SESSION_NOT_FOUND",
                "The failure must quote the CLI's own words, not only the pipe-level exception.");
        }
    }
}
