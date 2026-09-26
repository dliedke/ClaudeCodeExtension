/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the TFVC pre-edit checkout: the hook wire format, the parser callback and the pure helpers
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using ClaudeCodeVS;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class TfvcCheckoutTests
    {
        private const string HookCallbackLine =
            "{\"type\":\"control_request\",\"request_id\":\"r1\",\"request\":{\"subtype\":\"hook_callback\"," +
            "\"callback_id\":\"cc_before_file_edit\",\"input\":{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Write\"," +
            "\"tool_input\":{\"file_path\":\"C:\\\\src\\\\a.cs\",\"content\":\"x\"}}}}";

        [TestMethod]
        public void InitializeRequest_RegistersAPreToolUseHookForTheEditTools()
        {
            JObject request = JObject.Parse(JsonConvert.SerializeObject(ClaudeEditHook.BuildInitializeRequest()));

            Assert.AreEqual("control_request", (string)request["type"]);
            Assert.AreEqual("initialize", (string)request["request"]["subtype"]);

            JToken hook = request["request"]["hooks"]["PreToolUse"][0];
            Assert.AreEqual("Write|Edit|MultiEdit|NotebookEdit", (string)hook["matcher"]);
            Assert.AreEqual(ClaudeEditHook.CallbackId, (string)hook["hookCallbackIds"][0]);
        }

        [TestMethod]
        public void ExtractPaths_ReadsFilePathAndNotebookPath()
        {
            var write = JObject.Parse("{\"tool_input\":{\"file_path\":\"C:\\\\a.cs\"}}");
            var notebook = JObject.Parse("{\"tool_input\":{\"notebook_path\":\"C:\\\\n.ipynb\"}}");

            CollectionAssert.AreEqual(new[] { "C:\\a.cs" }, new List<string>(ClaudeEditHook.ExtractPaths(write)));
            CollectionAssert.AreEqual(new[] { "C:\\n.ipynb" }, new List<string>(ClaudeEditHook.ExtractPaths(notebook)));
        }

        [TestMethod]
        public void ExtractPaths_IsEmptyWhenNothingNamesAFile()
        {
            Assert.AreEqual(0, ClaudeEditHook.ExtractPaths(null).Count);
            Assert.AreEqual(0, ClaudeEditHook.ExtractPaths(new JObject()).Count);
            Assert.AreEqual(0, ClaudeEditHook.ExtractPaths(JObject.Parse("{\"tool_input\":{\"file_path\":42}}")).Count);
        }

        [TestMethod]
        public void DenyOutput_CarriesThePreToolUseDecisionAndTheReason()
        {
            JObject output = JObject.Parse(JsonConvert.SerializeObject(ClaudeEditHook.Deny("locked")));

            Assert.AreEqual("PreToolUse", (string)output["hookSpecificOutput"]["hookEventName"]);
            Assert.AreEqual("deny", (string)output["hookSpecificOutput"]["permissionDecision"]);
            Assert.AreEqual("locked", (string)output["hookSpecificOutput"]["permissionDecisionReason"]);
            Assert.AreEqual("{}", JsonConvert.SerializeObject(ClaudeEditHook.Allow()));
        }

        [TestMethod]
        public void Parser_RaisesTheHookCallbackWithTheRequestIdAndInput()
        {
            string requestId = null;
            JObject input = null;
            var parser = new ClaudeStreamParser(true)
            {
                HookCallbackReceived = (id, hookInput) => { requestId = id; input = hookInput; }
            };

            IReadOnlyList<AgentEvent> events = parser.Parse(HookCallbackLine);

            Assert.AreEqual(0, events.Count, "A hook callback is plumbing, not something the chat renders.");
            Assert.AreEqual("r1", requestId);
            CollectionAssert.AreEqual(new[] { "C:\\src\\a.cs" }, new List<string>(ClaudeEditHook.ExtractPaths(input)));
        }

        [TestMethod]
        public void Parser_IgnoresAHookCallbackRegisteredByAnotherParty()
        {
            bool raised = false;
            var parser = new ClaudeStreamParser(true) { HookCallbackReceived = (id, hookInput) => raised = true };

            parser.Parse(HookCallbackLine.Replace("cc_before_file_edit", "someone_else"));

            Assert.IsFalse(raised);
        }

        [TestMethod]
        public void BoundSolutionText_RecognisesTheTeamFoundationSection()
        {
            const string bound = "Global\r\n\tGlobalSection(TeamFoundationVersionControl) = preSolution\r\n\t\tSccNumberOfProjects = 1\r\n\tEndGlobalSection\r\nEndGlobal";
            const string git = "Global\r\n\tGlobalSection(SolutionProperties) = preSolution\r\n\tEndGlobalSection\r\nEndGlobal";

            Assert.IsTrue(TfvcSolution.IsBoundSolutionText(bound));
            Assert.IsFalse(TfvcSolution.IsBoundSolutionText(git));
            Assert.IsFalse(TfvcSolution.IsBoundSolutionText(null));
        }

        [TestMethod]
        public void BoundSolutionNear_FindsTheSolutionInTheWorkspaceOrAParent()
        {
            string root = Path.Combine(Path.GetTempPath(), "cc-tfvc-" + Guid.NewGuid().ToString("N"));
            string child = Path.Combine(root, "src", "App");
            Directory.CreateDirectory(child);

            try
            {
                Assert.IsFalse(TfvcSolution.IsBoundSolutionNear(child), "No solution yet.");

                File.WriteAllText(Path.Combine(root, "App.sln"), "GlobalSection(TeamFoundationVersionControl) = preSolution");

                Assert.IsTrue(TfvcSolution.IsBoundSolutionNear(root));
                Assert.IsTrue(TfvcSolution.IsBoundSolutionNear(child), "A custom working directory inside the solution folder still counts.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ToWindowsPath_MapsWslMountsBackToDrives()
        {
            Assert.AreEqual("C:\\dir\\file.cs", TfvcSolution.ToWindowsPath("/mnt/c/dir/file.cs", true));
            Assert.AreEqual("D:\\", TfvcSolution.ToWindowsPath("/mnt/d/", true));
            Assert.AreEqual("/home/me/file.cs", TfvcSolution.ToWindowsPath("/home/me/file.cs", true));
            Assert.AreEqual("/mnt/c/dir/file.cs", TfvcSolution.ToWindowsPath("/mnt/c/dir/file.cs", false));
            Assert.AreEqual("C:\\dir\\file.cs", TfvcSolution.ToWindowsPath("C:\\dir\\file.cs", true));
        }

        [TestMethod]
        public void IsReadOnlyFile_OnlyForAnExistingReadOnlyFile()
        {
            string file = Path.GetTempFileName();

            try
            {
                Assert.IsFalse(TfvcSolution.IsReadOnlyFile(file));

                File.SetAttributes(file, FileAttributes.ReadOnly);
                Assert.IsTrue(TfvcSolution.IsReadOnlyFile(file));
                Assert.IsFalse(TfvcSolution.IsReadOnlyFile(file + ".missing"));
            }
            finally
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
        }

        [TestMethod]
        public void FailureReason_TellsTheAgentNotToClearTheReadOnlyFlag()
        {
            string reason = TfvcSolution.BuildFailureReason(new[] { "C:\\a.cs", "C:\\b.cs" });

            StringAssert.Contains(reason, "C:\\a.cs, C:\\b.cs");
            StringAssert.Contains(reason, "Do not change the file's read-only attribute");
        }
    }
}
