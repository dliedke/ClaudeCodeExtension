/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Reads the environment the Claude Code CLI will apply from its settings files
 *
 * *******************************************************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// The environment the Claude Code CLI will see for a workspace: the process environment plus the
    /// <c>env</c> blocks of the settings files the CLI merges, in the CLI's precedence order.
    /// <para>
    /// The extension needs this before launch to resolve a custom model's context window and
    /// auto-compact point (see <see cref="ClaudeContextLimits"/>): the CLI applies these settings to
    /// itself, but callers outside it cannot see them otherwise. Only the model/base-url/context
    /// variables the resolver needs are copied — never the auth token.
    /// </para>
    /// </summary>
    public static class ClaudeCliEnvironment
    {
        /// <summary>Overrides <c>~/.claude</c> as the user settings directory (honored by the CLI).</summary>
        public const string UserClaudeDirectoryVariable = "CLAUDE_CONFIG_DIR";

        /// <summary>The effective environment for <paramref name="projectDirectory"/> on this machine.</summary>
        public static IDictionary<string, string> Read(string projectDirectory)
        {
            return Read(ResolveUserClaudeDirectory(), projectDirectory,
                ClaudeStatusLine.ManagedSettingsPath, null);
        }

        /// <summary>
        /// Test seam: explicit directories and managed path instead of this machine's, with an optional
        /// process-environment layer. Precedence is the CLI's, lowest to highest: process environment,
        /// user <c>settings.json</c>, project <c>.claude/settings.json</c>, project
        /// <c>.claude/settings.local.json</c>, managed settings.
        /// </summary>
        internal static IDictionary<string, string> Read(string userClaudeDirectory, string projectDirectory,
            string managedSettingsPath, IDictionary<string, string> processEnvironment)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (processEnvironment != null)
            {
                foreach (KeyValuePair<string, string> pair in processEnvironment)
                {
                    if (IsRelevantVariable(pair.Key))
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }
            else
            {
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    if (entry.Key is string key && entry.Value is string value && IsRelevantVariable(key))
                    {
                        result[key] = value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(userClaudeDirectory))
            {
                MergeSettingsEnv(Path.Combine(userClaudeDirectory, "settings.json"), result);
            }

            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                MergeSettingsEnv(Path.Combine(projectDirectory, ".claude", "settings.json"), result);
                MergeSettingsEnv(Path.Combine(projectDirectory, ".claude", "settings.local.json"), result);
            }

            MergeSettingsEnv(managedSettingsPath, result);

            return result;
        }

        /// <summary>
        /// The user settings directory the CLI uses: <c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>.
        /// </summary>
        public static string ResolveUserClaudeDirectory()
        {
            string configured = Environment.GetEnvironmentVariable(UserClaudeDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        private static void MergeSettingsEnv(string path, IDictionary<string, string> result)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return;
                }

                JObject settings = JObject.Parse(File.ReadAllText(path));
                if (!(settings["env"] is JObject env))
                {
                    return;
                }

                foreach (JProperty property in env.Properties())
                {
                    if (!IsRelevantVariable(property.Name) ||
                        property.Value == null ||
                        property.Value.Type == JTokenType.Null)
                    {
                        continue;
                    }

                    result[property.Name] = property.Value.ToString();
                }
            }
            catch (Exception ex)
            {
                // A half-written or hand-edited settings file must not break the launch.
                Debug.WriteLine($"ClaudeCliEnvironment: cannot read {path}: {ex.Message}");
            }
        }

        private static bool IsRelevantVariable(string name)
        {
            return name.Equals("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(UserClaudeDirectoryVariable, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(ClaudeContextLimits.MaxContextTokensVariable, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(ClaudeContextLimits.AutoCompactWindowVariable, StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("MODEL", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
