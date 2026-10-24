/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the settings/env merge used to resolve a custom model's context window
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ClaudeCliEnvironmentTests
    {
        [TestMethod]
        public void SettingsLayerOverProcessAndManagedWinsWithoutCopyingSecrets()
        {
            string root = Path.Combine(Path.GetTempPath(), "cc-cli-env-" + Guid.NewGuid().ToString("N"));
            string userDirectory = Path.Combine(root, "user");
            string projectDirectory = Path.Combine(root, "project");
            string managedSettings = Path.Combine(root, "managed-settings.json");

            try
            {
                Directory.CreateDirectory(userDirectory);
                Directory.CreateDirectory(Path.Combine(projectDirectory, ".claude"));

                File.WriteAllText(Path.Combine(userDirectory, "settings.json"),
                    "{\"env\":{\"ANTHROPIC_MODEL\":\"user-model\",\"ANTHROPIC_AUTH_TOKEN\":\"secret\"}}");
                File.WriteAllText(Path.Combine(projectDirectory, ".claude", "settings.json"),
                    "{\"env\":{\"ANTHROPIC_MODEL\":\"project-model\"}}");
                File.WriteAllText(Path.Combine(projectDirectory, ".claude", "settings.local.json"),
                    "{\"env\":{\"ANTHROPIC_MODEL\":\"local-model\",\"CLAUDE_CODE_MAX_CONTEXT_TOKENS\":\"1000000\"}}");
                File.WriteAllText(managedSettings,
                    "{\"env\":{\"ANTHROPIC_MODEL\":\"managed-model\"}}");

                var processEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ANTHROPIC_MODEL"] = "process-model",
                    ["ANTHROPIC_AUTH_TOKEN"] = "process-secret"
                };

                IDictionary<string, string> environment = ClaudeCliEnvironment.Read(
                    userDirectory, projectDirectory, managedSettings, processEnvironment);

                Assert.AreEqual("managed-model", environment["ANTHROPIC_MODEL"]);
                Assert.AreEqual("1000000", environment[ClaudeContextLimits.MaxContextTokensVariable]);
                Assert.IsFalse(environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
