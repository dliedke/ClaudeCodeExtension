/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the Claude Code transcript sync of issue #170: title records written on rename, titles read
 *          back from the transcript, the entrypoint rewrite that lists native sessions in the CLI's /resume,
 *          the record timestamps Session History shows as a conversation's last-used time, and keeping a rename
 *          over the old name a still-running CLI writes back.
 *
 * *******************************************************************************************************************/

using System.IO;
using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class SessionTitleSyncTests
    {
        [TestMethod]
        public void BuildClaudeTitleRecords_MatchesWhatTheCliWrites()
        {
            string records = ClaudeCodeControl.BuildClaudeTitleRecords("abc-123", "My session");

            Assert.AreEqual(
                "{\"type\":\"custom-title\",\"customTitle\":\"My session\",\"sessionId\":\"abc-123\"}\n" +
                "{\"type\":\"agent-name\",\"agentName\":\"My session\",\"sessionId\":\"abc-123\"}\n",
                records);
        }

        [TestMethod]
        public void BuildClaudeTitleRecords_EscapesQuotesAndBackslashes()
        {
            string records = ClaudeCodeControl.BuildClaudeTitleRecords("id", "say \"hi\" \\ bye");

            StringAssert.Contains(records, "\"customTitle\":\"say \\\"hi\\\" \\\\ bye\"");
        }

        [TestMethod]
        public void AppendClaudeTitleRecords_KeepsExistingLinesAndAppendsThePair()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"type\":\"user\"}\n");

                ClaudeCodeControl.AppendClaudeTitleRecords(path, "id", "New name");

                string[] lines = File.ReadAllLines(path);
                Assert.AreEqual(3, lines.Length);
                Assert.AreEqual("{\"type\":\"user\"}", lines[0]);
                StringAssert.Contains(lines[1], "\"customTitle\":\"New name\"");
                StringAssert.Contains(lines[2], "\"agentName\":\"New name\"");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void AppendClaudeTitleRecords_StartsANewLineWhenTheFileEndsMidLine()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"type\":\"user\"}");

                ClaudeCodeControl.AppendClaudeTitleRecords(path, "id", "Name");

                string[] lines = File.ReadAllLines(path);
                Assert.AreEqual("{\"type\":\"user\"}", lines[0]);
                StringAssert.Contains(lines[1], "\"type\":\"custom-title\"");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void AppendClaudeTitleRecords_DoesNotCreateAMissingFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "missing-" + System.Guid.NewGuid() + ".jsonl");

            ClaudeCodeControl.AppendClaudeTitleRecords(path, "id", "Name");

            Assert.IsFalse(File.Exists(path));
        }

        private const string NativeLine =
            "{\"type\":\"user\",\"entrypoint\":\"sdk-cli\",\"sessionId\":\"id\"}\n";

        [TestMethod]
        public void PatchClaudeTranscriptEntrypoint_RewritesTheFirstSdkEntrypointInPlace()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, NativeLine + NativeLine);
                long before = new FileInfo(path).Length;

                Assert.IsTrue(ClaudeCodeControl.PatchClaudeTranscriptEntrypoint(path));

                string[] lines = File.ReadAllLines(path);
                Assert.AreEqual(before, new FileInfo(path).Length);
                Assert.AreEqual("cli", (string)JObject.Parse(lines[0])["entrypoint"]);
                Assert.AreEqual("id", (string)JObject.Parse(lines[0])["sessionId"]);
                Assert.AreEqual("sdk-cli", (string)JObject.Parse(lines[1])["entrypoint"]);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void PatchClaudeTranscriptEntrypoint_HandlesShorterSdkValues()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"entrypoint\":\"sdk-ts\",\"x\":1}\n");

                Assert.IsTrue(ClaudeCodeControl.PatchClaudeTranscriptEntrypoint(path));

                Assert.AreEqual("{\"entrypoint\":\"cli\"   ,\"x\":1}\n", File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void PatchClaudeTranscriptEntrypoint_LeavesTerminalSessionsAlone()
        {
            string path = Path.GetTempFileName();
            try
            {
                string content = "{\"type\":\"user\",\"entrypoint\":\"cli\"}\n" + NativeLine;
                File.WriteAllText(path, content);

                Assert.IsFalse(ClaudeCodeControl.PatchClaudeTranscriptEntrypoint(path));
                Assert.AreEqual(content, File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void PatchClaudeTranscriptEntrypoint_FallsBackToTheLastOneInTheTail()
        {
            string path = Path.GetTempFileName();
            try
            {
                // A head with no entrypoint at all: the CLI then goes by the last one in the tail.
                string filler = "{\"type\":\"summary\",\"pad\":\"" + new string('x', ClaudeCodeControl.ClaudeTranscriptScanBytes) + "\"}\n";
                File.WriteAllText(path, filler + NativeLine + NativeLine);

                Assert.IsTrue(ClaudeCodeControl.PatchClaudeTranscriptEntrypoint(path));

                string[] lines = File.ReadAllLines(path);
                Assert.AreEqual("sdk-cli", (string)JObject.Parse(lines[1])["entrypoint"]);
                Assert.AreEqual("cli", (string)JObject.Parse(lines[2])["entrypoint"]);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ReadClaudeTranscriptTitle_ReturnsTheLastNameOrNullWhenUnnamed()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, NativeLine);
                Assert.IsNull(ClaudeCodeControl.ReadClaudeTranscriptTitle(path));

                ClaudeCodeControl.AppendClaudeTitleRecords(path, "id", "First");
                ClaudeCodeControl.AppendClaudeTitleRecords(path, "id", "Second");
                Assert.AreEqual("Second", ClaudeCodeControl.ReadClaudeTranscriptTitle(path));

                ClaudeCodeControl.AppendClaudeTitleRecords(path, "id", "");
                Assert.AreEqual(string.Empty, ClaudeCodeControl.ReadClaudeTranscriptTitle(path));
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ShouldAppendSavedClaudeTitle_RestoresTheRenameOverAStaleCliWrite()
        {
            string[] superseded = { "Auto title" };

            // The running CLI wrote its old name back after the rename.
            Assert.IsTrue(ClaudeCodeControl.ShouldAppendSavedClaudeTitle("Auto title", "My title", superseded));
            // Already current, or the tail has no title at all.
            Assert.IsFalse(ClaudeCodeControl.ShouldAppendSavedClaudeTitle("My title", "My title", superseded));
            Assert.IsTrue(ClaudeCodeControl.ShouldAppendSavedClaudeTitle(null, "My title", superseded));
            // A name given in the CLI itself is not overwritten.
            Assert.IsFalse(ClaudeCodeControl.ShouldAppendSavedClaudeTitle("Renamed in CLI", "My title", superseded));
            // Nothing saved, nothing to write.
            Assert.IsFalse(ClaudeCodeControl.ShouldAppendSavedClaudeTitle("Auto title", "", superseded));
        }

        [TestMethod]
        public void IsSupersededClaudeTitle_MatchesOnlyNamesRenamedAwayFrom()
        {
            string[] superseded = { "Auto title" };

            Assert.IsTrue(ClaudeCodeControl.IsSupersededClaudeTitle("Auto title ", superseded));
            Assert.IsFalse(ClaudeCodeControl.IsSupersededClaudeTitle("Other", superseded));
            Assert.IsFalse(ClaudeCodeControl.IsSupersededClaudeTitle(null, superseded));
            Assert.IsFalse(ClaudeCodeControl.IsSupersededClaudeTitle("Auto title", new string[0]));
        }

        [TestMethod]
        public void ReadTranscriptRecordTime_ReturnsTheRecordTimestampInLocalTime()
        {
            var expected = new System.DateTime(2026, 10, 29, 17, 33, 5, System.DateTimeKind.Utc).ToLocalTime();

            // As Json.NET parses a line by default (the value becomes a date)…
            JObject parsed = JObject.Parse("{\"type\":\"user\",\"timestamp\":\"2026-10-29T17:33:05.000Z\"}");
            Assert.AreEqual(expected, ClaudeCodeControl.ReadTranscriptRecordTime(parsed));

            // …and with the value kept as text.
            var raw = new JObject { ["type"] = "user", ["timestamp"] = "2026-10-29T17:33:05.000Z" };
            Assert.AreEqual(expected, ClaudeCodeControl.ReadTranscriptRecordTime(raw));
        }

        [TestMethod]
        public void ReadTranscriptRecordTime_IsNullWithoutATimestamp()
        {
            Assert.IsNull(ClaudeCodeControl.ReadTranscriptRecordTime(JObject.Parse("{\"type\":\"custom-title\"}")));
            Assert.IsNull(ClaudeCodeControl.ReadTranscriptRecordTime(new JObject { ["timestamp"] = "not a date" }));
        }
    }
}
