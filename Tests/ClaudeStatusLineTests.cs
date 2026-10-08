/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for running the user's Claude Code status line in native mode (settings precedence,
 *          model names, output cleanup, the command run itself)
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Threading;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ClaudeStatusLineTests
    {
        private string _root;
        private string _user;
        private string _project;

        [TestInitialize]
        public void CreateFolders()
        {
            _root = Path.Combine(Path.GetTempPath(), "ccx-statusline-" + Guid.NewGuid().ToString("N"));
            _user = Path.Combine(_root, "user");
            _project = Path.Combine(_root, "project");
            Directory.CreateDirectory(_user);
            Directory.CreateDirectory(Path.Combine(_project, ".claude"));
        }

        [TestCleanup]
        public void DeleteFolders()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string Managed { get { return Path.Combine(_root, "managed-settings.json"); } }

        private static void Write(string path, string json)
        {
            File.WriteAllText(path, json);
        }

        private string Resolve()
        {
            return ClaudeStatusLine.ResolveCommand(Managed, _user, _project);
        }

        [TestMethod]
        public void NothingConfiguredMeansNoStatusLine()
        {
            Assert.IsNull(Resolve());

            Write(Path.Combine(_user, "settings.json"), "{\"model\":\"opus\"}");
            Assert.IsNull(Resolve());
        }

        [TestMethod]
        public void TheUsersStatusLineIsUsed()
        {
            Write(Path.Combine(_user, "settings.json"),
                "{\"statusLine\":{\"type\":\"command\",\"command\":\"powershell -NoProfile -File C:/Users/me/.claude/statusline.ps1\"}}");

            Assert.AreEqual("powershell -NoProfile -File C:/Users/me/.claude/statusline.ps1", Resolve());
        }

        [TestMethod]
        public void ProjectSettingsOverrideTheUsersAndLocalOverridesProject()
        {
            Write(Path.Combine(_user, "settings.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"user\"}}");
            Write(Path.Combine(_project, ".claude", "settings.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"project\"}}");
            Assert.AreEqual("project", Resolve());

            Write(Path.Combine(_project, ".claude", "settings.local.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"local\"}}");
            Assert.AreEqual("local", Resolve());

            Write(Managed, "{\"statusLine\":{\"type\":\"command\",\"command\":\"managed\"}}");
            Assert.AreEqual("managed", Resolve());
        }

        [TestMethod]
        public void DisableAllHooksTurnsTheStatusLineOff()
        {
            Write(Path.Combine(_user, "settings.json"),
                "{\"disableAllHooks\":true,\"statusLine\":{\"type\":\"command\",\"command\":\"user\"}}");
            Assert.IsNull(Resolve());

            // A higher-precedence false re-enables it, as in the CLI.
            Write(Path.Combine(_project, ".claude", "settings.json"), "{\"disableAllHooks\":false}");
            Assert.AreEqual("user", Resolve());

            Write(Managed, "{\"allowManagedHooksOnly\":true}");
            Assert.IsNull(Resolve());
        }

        [TestMethod]
        public void ABrokenSettingsFileIsIgnored()
        {
            Write(Path.Combine(_project, ".claude", "settings.json"), "{ not json");
            Write(Path.Combine(_user, "settings.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"user\"}}");

            Assert.AreEqual("user", Resolve());
        }

        [TestMethod]
        public void ModelIdsReadAsTheCliNamesThem()
        {
            Assert.AreEqual("Opus 5.5", ClaudeStatusLine.ModelDisplayName("claude-opus-5-5"));
            Assert.AreEqual("Sonnet 4.5", ClaudeStatusLine.ModelDisplayName("claude-sonnet-4-5-20250929"));
            Assert.AreEqual("Haiku 3.5", ClaudeStatusLine.ModelDisplayName("claude-3-5-haiku-20241022"));
            Assert.AreEqual("Opus 5.5 (1M context)", ClaudeStatusLine.ModelDisplayName("claude-opus-5-5[1m]"));
            Assert.AreEqual(string.Empty, ClaudeStatusLine.ModelDisplayName(null));
        }

        [TestMethod]
        public void OutputLosesEscapeSequencesButKeepsItsLines()
        {
            string raw = "\x1b[32mOpus 5.5\x1b[0m | \x1b]8;;https://example.com\x07link\x1b]8;;\x07  \r\n" +
                         "\x1b[2m██░░ 20%\x1b[0m\r\n\r\n";

            Assert.AreEqual("Opus 5.5 | link\n██░░ 20%", ClaudeStatusLine.CleanOutput(raw));
            Assert.AreEqual(string.Empty, ClaudeStatusLine.CleanOutput("\r\n  \r\n"));
        }

        [TestMethod]
        public void WslProbeOutputIsReadFromItsMarkedLinesOnly()
        {
            // Profile scripts of the interactive login shell may print their own lines around the probe.
            WslLocation location = WslLocation.Parse("Welcome!\n@@\\\\wsl.localhost\\Ubuntu\\\n@@/home/me/.claude\n");
            Assert.IsNotNull(location);
            Assert.AreEqual(@"\\wsl.localhost\Ubuntu\", location.RootShare);
            Assert.AreEqual("/home/me/.claude", location.ConfigDirectory);

            Assert.IsNull(WslLocation.Parse(null));
            Assert.IsNull(WslLocation.Parse("@@\\\\wsl.localhost\\Ubuntu\\\n"));
            Assert.IsNull(WslLocation.Parse("@@C:\\\n@@/home/me/.claude\n"));
        }

        [TestMethod]
        public void WslPathsMapToDrivesOrTheDistroShare()
        {
            var location = new WslLocation(@"\\wsl.localhost\Ubuntu\", "/home/me/.claude");

            Assert.AreEqual(@"C:\GitLab\Repo", location.ToWindowsPath("/mnt/c/GitLab/Repo"));
            Assert.AreEqual(@"D:\", location.ToWindowsPath("/mnt/d"));
            Assert.AreEqual(@"\\wsl.localhost\Ubuntu\home\me\.claude", location.ToWindowsPath("/home/me/.claude"));
            Assert.AreEqual(@"\\wsl.localhost\Ubuntu\etc\claude-code\managed-settings.json",
                location.ToWindowsPath(ClaudeStatusLine.WslManagedSettingsPath));
            Assert.AreEqual(@"\\wsl.localhost\Ubuntu\mnt\wsl", location.ToWindowsPath("/mnt/wsl"));
        }

        [TestMethod]
        public void AWslSessionsTranscriptPathIsALinuxPath()
        {
            var input = new ClaudeStatusLineInput { Cwd = "/mnt/c/GitLab/Repo", SessionId = "s1" };

            string json = input.Build(string.Empty, "/home/me/.claude/");

            StringAssert.Contains(json, "\"transcript_path\":\"/home/me/.claude/projects/-mnt-c-GitLab-Repo/s1.jsonl\"");
        }

        [TestMethod]
        public void TheCommandRunsWithTheJsonOnStdinAndOnlySucceedsOnExitZero()
        {
            // "echo" and "exit" behave alike in Git Bash and PowerShell, whichever this machine has.
            string ok = ClaudeStatusLine.RunAsync("echo hello", "{}", _project, 80, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("hello", ok);

            string failed = ClaudeStatusLine.RunAsync("exit 3", "{}", _project, 80, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsNull(failed);
        }
    }
}
