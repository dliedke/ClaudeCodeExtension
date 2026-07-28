/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Builds the headless command line used by the Claude Code native-mode adapter
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Everything the Claude adapter needs to launch the CLI. Filled in by the Controls layer, which
    /// owns the settings; kept as plain data so <c>Agents</c> stays free of Visual Studio types.
    /// </summary>
    public class ClaudeSessionOptions
    {
        /// <summary>
        /// Executable to run, unquoted. On Windows this is the resolved
        /// <c>%USERPROFILE%\.local\bin\claude.exe</c>, a user-configured custom path, or plain
        /// <c>claude</c>. Ignored when <see cref="UseWsl"/> is true (the distro resolves it).
        /// </summary>
        public string ExecutablePath { get; set; } = "claude";

        /// <summary>Run inside WSL instead of natively.</summary>
        public bool UseWsl { get; set; }

        /// <summary>
        /// Working directory translated to a WSL path (<c>/mnt/c/...</c>). Required when
        /// <see cref="UseWsl"/> is true; the Windows path still goes to the process itself.
        /// </summary>
        public string WslWorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Session id the extension generates up front. Passing it in makes the CLI name its JSONL
        /// transcript after it, which is what lets the existing Session History window find and resume
        /// a conversation that happened in native mode.
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>When set, resumes that session instead of starting a new one.</summary>
        public string ResumeSessionId { get; set; } = string.Empty;

        /// <summary>Model alias (<c>sonnet</c>, <c>opus</c>, <c>haiku</c>). Empty means the CLI default.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Effort level (<c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, <c>max</c>,
        /// <c>ultracode</c>). Empty means the CLI default.
        /// <para>
        /// A launch flag rather than the <c>/effort</c> slash command: the command sets it "for this
        /// session only", so it was silently lost on every relaunch (model switch, permission switch,
        /// interrupt recovery) while the composer still showed the old level. An unknown value only
        /// earns a warning on stderr, so a future CLI dropping a level degrades to the default instead
        /// of failing the launch.
        /// </para>
        /// </summary>
        public string Effort { get; set; } = string.Empty;

        /// <summary>
        /// Maps the existing <c>ClaudeDangerouslySkipPermissions</c> setting: bypass every check.
        /// Mutually exclusive with <see cref="PermissionMode"/>, and it also turns
        /// <see cref="InteractivePermissions"/> off — there is nothing left to ask about.
        /// </summary>
        public bool DangerouslySkipPermissions { get; set; }

        /// <summary>
        /// Used only when <see cref="DangerouslySkipPermissions"/> is false. <c>acceptEdits</c> is the
        /// default: verified to let file writes through while still gating the riskier tools.
        /// <c>plan</c> puts the session in plan mode.
        /// </summary>
        public string PermissionMode { get; set; } = "acceptEdits";

        /// <summary>
        /// Routes permission decisions back to us over the stdio control channel.
        /// <para>
        /// Measured: this is also what makes the CLI offer <c>AskUserQuestion</c> and
        /// <c>ExitPlanMode</c> at all. Without it neither appears in the tool inventory, so plan mode
        /// can be entered but never approved and the agent replies that the tools are not available in
        /// this environment.
        /// </para>
        /// </summary>
        public bool InteractivePermissions { get; set; } = true;

        /// <summary>
        /// Ask the CLI for token-level deltas. On (the default) the UI streams text as it is produced;
        /// off, each assistant message lands in one piece.
        /// </summary>
        public bool IncludePartialMessages { get; set; } = true;

        /// <summary>
        /// Environment variables for the child process, chiefly the registry-refreshed PATH — a CLI
        /// installed after Visual Studio started is invisible to the PATH we inherited.
        /// </summary>
        public IDictionary<string, string> EnvironmentOverrides { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns <see cref="ClaudeSessionOptions"/> into an executable plus an argument string.
    /// <para>
    /// Separate from the session itself so the argument layout can be unit-tested without launching a
    /// process — the flag set is the part most likely to drift as the CLI evolves.
    /// </para>
    /// </summary>
    public static class ClaudeCommandBuilder
    {
        /// <summary>Executable to hand to <see cref="System.Diagnostics.ProcessStartInfo"/>.</summary>
        public static string GetFileName(ClaudeSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            return options.UseWsl
                ? "wsl.exe"
                : (string.IsNullOrWhiteSpace(options.ExecutablePath) ? "claude" : options.ExecutablePath);
        }

        /// <summary>
        /// Full argument string. For WSL the whole thing is wrapped in
        /// <c>bash -lic "cd &lt;dir&gt; &amp;&amp; claude ..."</c> — a login shell, because the CLI is
        /// usually installed by a version manager that only puts it on PATH from the profile scripts.
        /// </summary>
        public static string GetArguments(ClaudeSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            string flags = BuildFlags(options, options.UseWsl);

            if (!options.UseWsl)
            {
                return flags;
            }

            var inner = new StringBuilder();
            inner.Append("cd ").Append(QuoteForBash(options.WslWorkingDirectory)).Append(" && claude ").Append(flags);

            return "bash -lic " + QuoteForWindowsArgument(inner.ToString());
        }

        private static string BuildFlags(ClaudeSessionOptions options, bool isWsl)
        {
            var sb = new StringBuilder();

            // The four flags that make the CLI a bidirectional JSON peer instead of a TUI:
            // --print drops the interactive renderer, the two --*-format flags select NDJSON in both
            // directions, and --verbose is what makes it emit the per-event stream rather than only a
            // final summary.
            sb.Append("--print --input-format stream-json --output-format stream-json --verbose");

            if (options.IncludePartialMessages)
            {
                sb.Append(" --include-partial-messages");
            }

            // --resume wins over --session-id: passing both makes the CLI reject the launch.
            if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
            {
                sb.Append(" --resume ").Append(QuoteArgument(options.ResumeSessionId, isWsl));
            }
            else if (!string.IsNullOrWhiteSpace(options.SessionId))
            {
                sb.Append(" --session-id ").Append(QuoteArgument(options.SessionId, isWsl));
            }

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                sb.Append(" --model ").Append(QuoteArgument(options.Model, isWsl));
            }

            if (!string.IsNullOrWhiteSpace(options.Effort))
            {
                sb.Append(" --effort ").Append(QuoteArgument(options.Effort, isWsl));
            }

            if (options.DangerouslySkipPermissions)
            {
                sb.Append(" --dangerously-skip-permissions");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(options.PermissionMode))
                {
                    sb.Append(" --permission-mode ").Append(QuoteArgument(options.PermissionMode, isWsl));
                }

                if (options.InteractivePermissions)
                {
                    sb.Append(" --permission-prompt-tool stdio");
                }
            }

            return sb.ToString();
        }

        private static string QuoteArgument(string value, bool isWsl)
        {
            return isWsl ? QuoteForBash(value) : QuoteForWindowsArgument(value);
        }

        /// <summary>Wraps in single quotes, escaping embedded ones the POSIX way.</summary>
        private static string QuoteForBash(string value)
        {
            if (value == null) value = string.Empty;
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        /// <summary>
        /// Quotes for the Windows command-line parser: backslashes immediately before the closing quote
        /// have to be doubled, otherwise they escape it and swallow the rest of the argument.
        /// </summary>
        private static string QuoteForWindowsArgument(string value)
        {
            if (value == null) value = string.Empty;

            var sb = new StringBuilder("\"");
            int backslashes = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', (backslashes * 2) + 1);
                    backslashes = 0;
                    sb.Append('"');
                    continue;
                }

                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2);
            sb.Append('"');

            return sb.ToString();
        }
    }
}
