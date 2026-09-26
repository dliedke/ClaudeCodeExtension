/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Pre-edit hook for the Claude native-mode adapter (source-control checkout before a file is written)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// The wire pieces of the SDK-style <c>PreToolUse</c> hook the Claude adapter registers so the host
    /// can react before the CLI touches a file (TFVC auto-checkout: server workspaces keep files
    /// read-only until checked out, and the agent otherwise clears the flag itself).
    /// <para>
    /// Measured against CLI 2.1.283: a control-channel <c>initialize</c> request that carries
    /// <c>hooks</c> makes the CLI send a <c>hook_callback</c> control request before every matching tool
    /// call and wait for the answer. It fires in <c>acceptEdits</c> and skip-permissions sessions too —
    /// unlike <c>can_use_tool</c>, which only fires for calls the CLI would have prompted for. Answering
    /// with a <c>deny</c> decision stops the write and hands the reason to the model.
    /// </para>
    /// </summary>
    internal static class ClaudeEditHook
    {
        /// <summary>The id the CLI echoes back on every callback registered by <see cref="BuildInitializeRequest"/>.</summary>
        internal const string CallbackId = "cc_before_file_edit";

        /// <summary>Tools that write a file. Bash redirection and <c>sed -i</c> cannot be intercepted this way.</summary>
        internal const string ToolMatcher = "Write|Edit|MultiEdit|NotebookEdit";

        /// <summary>The <c>initialize</c> control request that registers the hook.</summary>
        internal static object BuildInitializeRequest()
        {
            return new
            {
                type = "control_request",
                request_id = "init-" + Guid.NewGuid().ToString("N"),
                request = new
                {
                    subtype = "initialize",
                    hooks = new
                    {
                        PreToolUse = new[]
                        {
                            new { matcher = ToolMatcher, hookCallbackIds = new[] { CallbackId } }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// The files a <c>PreToolUse</c> callback input is about to write: <c>file_path</c> for
        /// Write/Edit/MultiEdit, <c>notebook_path</c> for NotebookEdit. Empty when the input names none.
        /// </summary>
        internal static IReadOnlyList<string> ExtractPaths(JObject hookInput)
        {
            var paths = new List<string>();
            var toolInput = hookInput?["tool_input"] as JObject;
            if (toolInput == null)
            {
                return paths;
            }

            foreach (string key in new[] { "file_path", "notebook_path" })
            {
                string value = toolInput[key]?.Type == JTokenType.String ? (string)toolInput[key] : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    paths.Add(value);
                }
            }

            return paths;
        }

        /// <summary>The hook output for "go ahead" — an empty object leaves the CLI's own decision untouched.</summary>
        internal static object Allow()
        {
            return new { };
        }

        /// <summary>The hook output that blocks the write and shows <paramref name="reason"/> to the model.</summary>
        internal static object Deny(string reason)
        {
            return new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    permissionDecision = "deny",
                    permissionDecisionReason = reason
                }
            };
        }
    }
}
