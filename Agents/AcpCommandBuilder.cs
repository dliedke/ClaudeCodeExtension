/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Builds the launch command for an ACP (Agent Client Protocol) agent
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Launch settings for an ACP agent. The subcommand is always <c>acp</c> — that is what
    /// <c>opencode</c>, <c>devin</c> and <c>reasonix</c> all expose.
    /// </summary>
    public class AcpSessionOptions
    {
        /// <summary>Executable, unquoted. May be a bare name resolved through PATH.</summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>Subcommand that switches the CLI into ACP mode.</summary>
        public string AcpArgument { get; set; } = "acp";

        public bool UseWsl { get; set; }

        /// <summary>Working directory in Linux form, used only when <see cref="UseWsl"/> is set.</summary>
        public string WslWorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Session mode to select after the handshake (ACP <c>session/set_mode</c>), when the agent
        /// offers modes. Empty leaves the agent's own default in place.
        /// </summary>
        public string ModeId { get; set; } = string.Empty;

        /// <summary>
        /// Model to select after the handshake, given either as the agent's own id
        /// ("claude-opus-4-8-high") or as the name it shows for it ("Claude Opus 4.8 High"). Empty
        /// leaves the agent on its default model.
        /// </summary>
        public string ModelName { get; set; } = string.Empty;

        /// <summary>
        /// Model passed on the launch command line instead, for an agent that publishes no model
        /// picker in its handshake (Reasonix). Written as the full flag — <c>-m deepseek-v4-pro</c>.
        /// Empty for the agents whose model is selected through <see cref="ModelName"/>.
        /// </summary>
        public string ModelLaunchArgument { get; set; } = string.Empty;

        /// <summary>Display name used in messages shown to the user.</summary>
        public string DisplayName { get; set; } = "agent";

        /// <summary>
        /// User-supplied extra flags (Settings → CLI Paths → "Extra launch arguments"), appended
        /// verbatim after the <c>acp</c> subcommand. Empty adds nothing.
        /// </summary>
        public string ExtraArguments { get; set; } = string.Empty;

        /// <summary>
        /// A session id to resume via ACP <c>session/load</c> instead of starting fresh with
        /// <c>session/new</c>. Empty starts a new session. Only meaningful for an agent that
        /// advertises <c>agentCapabilities.loadSession</c> (Devin does); on an agent that does not,
        /// or if the load itself fails, <see cref="AcpSession"/> falls back to <c>session/new</c>.
        /// </summary>
        public string ResumeSessionId { get; set; } = string.Empty;

        /// <summary>
        /// Set for a CLI whose very first-ever invocation blocks on an interactive console prompt
        /// before doing anything else (Reasonix: "Allow anonymous CLI usage statistics? [Y/n]"), with
        /// no trailing newline — which means it never becomes a complete line on stdout, so the normal
        /// line-based ACP handshake would hang forever waiting for a JSON-RPC frame that is never
        /// coming. See <see cref="AcpSession"/>'s priming step.
        /// </summary>
        public bool AnswerFirstRunPromptWithNo { get; set; }

        /// <summary>
        /// Optional diagnostic sink. <see cref="AcpSession"/> reports the launch command, the child's
        /// exit code and its stderr tail here when a start fails — the only way to see any of that on a
        /// Release build, where <c>Debug.WriteLine</c> is compiled out. Wired to the terminal-launch
        /// log by <c>CreateAcpSession</c>; left null in tests. Must never throw.
        /// </summary>
        public Action<string> DiagnosticLog { get; set; }

        public IDictionary<string, string> EnvironmentOverrides { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns <see cref="AcpSessionOptions"/> into the file name / arguments pair that
    /// <see cref="JsonLineProcessHost"/> needs.
    /// </summary>
    public static class AcpCommandBuilder
    {
        /// <summary>
        /// The launch flag that puts Reasonix on a given model, or an empty string when none is picked.
        /// Lives here, and not with the rest of the provider wiring, so it can be covered by a test:
        /// the caller is a WPF/VS-SDK control the unit suite cannot load, which is exactly why shipping
        /// <c>-m</c> here went unnoticed. <c>reasonix acp</c> parses its flags with Go's <c>flag</c>
        /// package, which does **no** prefix matching — <c>-m</c> is rejected with "flag provided but
        /// not defined: -m" and exit code 2 before the ACP server starts, so every native launch with a
        /// model selected died on the first <c>initialize</c> write.
        /// </summary>
        public static string BuildReasonixModelArgument(string modelId)
        {
            return string.IsNullOrWhiteSpace(modelId) ? string.Empty : "-model " + modelId.Trim();
        }

        public static string GetFileName(AcpSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (options.UseWsl)
            {
                return "wsl.exe";
            }

            // opencode and reasonix install as npm shims (.cmd), which CreateProcess cannot execute
            // directly with UseShellExecute off — the command processor has to run them.
            return IsBatchScript(options.ExecutablePath) ? "cmd.exe" : options.ExecutablePath;
        }

        public static string GetArguments(AcpSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            string subcommand = string.IsNullOrWhiteSpace(options.AcpArgument) ? "acp" : options.AcpArgument.Trim();

            if (!string.IsNullOrWhiteSpace(options.ModelLaunchArgument))
            {
                subcommand += " " + options.ModelLaunchArgument.Trim();
            }

            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
            {
                subcommand += " " + options.ExtraArguments.Trim();
            }

            if (options.UseWsl)
            {
                var inner = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(options.WslWorkingDirectory))
                {
                    inner.Append("cd ").Append(QuoteForBash(options.WslWorkingDirectory)).Append(" && ");
                }
                inner.Append(options.ExecutablePath).Append(' ').Append(subcommand);

                // -i loads the profile, without which nvm-managed CLIs are not on PATH; -c runs
                // the command. No -l: a login shell prints motd banners onto stdout, and stdout is
                // the protocol channel here.
                return "bash -ic " + QuoteForWindowsArgument(inner.ToString());
            }

            if (IsBatchScript(options.ExecutablePath))
            {
                // /c ends the shell when the agent exits; the whole command is quoted as one unit so
                // a space in the path cannot split it.
                return "/c " + QuoteForWindowsArgument(options.ExecutablePath + " " + subcommand);
            }

            return subcommand;
        }

        private static bool IsBatchScript(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteForBash(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";
        }

        private static string QuoteForWindowsArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
