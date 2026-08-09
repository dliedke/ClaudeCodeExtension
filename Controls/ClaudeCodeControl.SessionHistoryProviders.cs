/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Per-CLI session-history adapters. The dialog talks to this seam instead of branching on
 *          AiProvider, so a new agent is one class rather than an extra arm on five switches
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Session History Provider Seam

        /// <summary>
        /// A listing plus the message to show when it came back empty. The message belongs to the
        /// provider because only it knows *why* nothing was found — an unreachable WSL share reads
        /// very differently from a workspace that genuinely has no sessions.
        /// </summary>
        internal sealed class SessionHistoryListResult
        {
            public List<SessionInfo> Sessions { get; set; }
            public string EmptyMessage { get; set; }
        }

        /// <summary>
        /// Everything the Session History dialog needs from one CLI. Capability flags exist because
        /// the agents genuinely differ: Codex can delete a thread, Devin has no delete verb at all,
        /// and only Claude and Devin accept a "continue the last conversation" switch.
        /// </summary>
        internal interface ISessionHistoryProvider
        {
            bool Supports(AiProvider provider);

            /// <summary>Label used in the dialog title and in error text.</summary>
            string DisplayName(AiProvider provider);

            /// <summary>True when <see cref="ReadTranscriptAsync"/> can render a conversation.</summary>
            bool CanViewTranscript { get; }

            /// <summary>True when <see cref="DeleteAsync"/> is implemented; otherwise Delete is disabled.</summary>
            bool CanDelete { get; }

            /// <summary>
            /// True when the CLI has its own "continue last conversation" switch, so Resume Last can
            /// arm the <c>"-c"</c> sentinel. False means the newest session id has to be resolved first.
            /// </summary>
            bool SupportsContinueSentinel { get; }

            /// <summary>Extra warning appended to the delete confirmation, or empty.</summary>
            string DeleteConfirmationSuffix { get; }

            Task<SessionHistoryListResult> ListAsync(AiProvider provider, string workspaceDir);

            Task<string> ReadTranscriptAsync(SessionInfo session, string workspaceDir);

            Task DeleteAsync(SessionInfo session, string workspaceDir);
        }

        private ISessionHistoryProvider[] _sessionHistoryProviders;

        /// <summary>
        /// The registry. Order is irrelevant — each adapter claims a disjoint set of providers.
        /// </summary>
        private ISessionHistoryProvider[] SessionHistoryProviders
        {
            get
            {
                if (_sessionHistoryProviders == null)
                {
                    _sessionHistoryProviders = new ISessionHistoryProvider[]
                    {
                        new ClaudeSessionHistoryProvider(this),
                        new CodexSessionHistoryProvider(this),
                        new DevinSessionHistoryProvider(this)
                    };
                }

                return _sessionHistoryProviders;
            }
        }

        /// <summary>Returns the adapter that owns <paramref name="provider"/>, or null.</summary>
        private ISessionHistoryProvider ResolveSessionHistoryProvider(AiProvider? provider)
        {
            if (provider == null) return null;

            foreach (ISessionHistoryProvider candidate in SessionHistoryProviders)
            {
                if (candidate.Supports(provider.Value)) return candidate;
            }

            return null;
        }

        /// <summary>Claude Code's per-workspace JSONL transcripts, native and WSL.</summary>
        private sealed class ClaudeSessionHistoryProvider : ISessionHistoryProvider
        {
            private readonly ClaudeCodeControl _owner;

            public ClaudeSessionHistoryProvider(ClaudeCodeControl owner)
            {
                _owner = owner;
            }

            public bool Supports(AiProvider provider)
            {
                return IsClaudeCodeSessionHistoryProvider(provider);
            }

            public string DisplayName(AiProvider provider)
            {
                return provider == AiProvider.ClaudeCodeWSL ? "Claude Code (WSL)" : "Claude Code";
            }

            public bool CanViewTranscript { get { return true; } }

            public bool CanDelete { get { return true; } }

            public bool SupportsContinueSentinel { get { return true; } }

            public string DeleteConfirmationSuffix { get { return string.Empty; } }

            public async Task<SessionHistoryListResult> ListAsync(AiProvider provider, string workspaceDir)
            {
                string sessionDir = await _owner.ResolveSessionDirectoryAsync(provider, workspaceDir);
                List<SessionInfo> sessions = await _owner.LoadSessionsAsync(sessionDir, provider);

                return new SessionHistoryListResult
                {
                    Sessions = sessions,
                    EmptyMessage = string.IsNullOrEmpty(sessionDir)
                        ? "WSL not available or Claude Code project folder unreachable."
                        : $"No sessions found in:\n{sessionDir}"
                };
            }

            public Task<string> ReadTranscriptAsync(SessionInfo session, string workspaceDir)
            {
                return Task.Run(() => BuildReadableTranscript(session));
            }

            public Task DeleteAsync(SessionInfo session, string workspaceDir)
            {
                return Task.Run(() => File.Delete(session.FilePath));
            }
        }

        /// <summary>Codex threads, read through the App Server JSON-RPC surface.</summary>
        private sealed class CodexSessionHistoryProvider : ISessionHistoryProvider
        {
            private readonly ClaudeCodeControl _owner;

            public CodexSessionHistoryProvider(ClaudeCodeControl owner)
            {
                _owner = owner;
            }

            public bool Supports(AiProvider provider)
            {
                return IsCodexSessionHistoryProvider(provider);
            }

            public string DisplayName(AiProvider provider)
            {
                return provider == AiProvider.Codex ? "Codex (WSL)" : "Codex";
            }

            public bool CanViewTranscript { get { return true; } }

            public bool CanDelete { get { return true; } }

            // Codex's own "resume --last" is not reachable from native mode, so Resume Last resolves
            // an explicit id instead and both modes then behave identically.
            public bool SupportsContinueSentinel { get { return false; } }

            public string DeleteConfirmationSuffix
            {
                get { return "\n\nCodex may also delete threads branched from this one."; }
            }

            public async Task<SessionHistoryListResult> ListAsync(AiProvider provider, string workspaceDir)
            {
                List<SessionInfo> sessions = await _owner.LoadCodexSessionsAsync(provider, workspaceDir);

                return new SessionHistoryListResult
                {
                    Sessions = sessions,
                    EmptyMessage = "No Codex sessions were found for this workspace."
                };
            }

            public async Task<string> ReadTranscriptAsync(SessionInfo session, string workspaceDir)
            {
                CodexThreadTranscript transcript = await _owner.ReadCodexThreadAsync(
                    session.Provider, workspaceDir, session.SessionId);

                return BuildReadableCodexTranscript(session, transcript);
            }

            public Task DeleteAsync(SessionInfo session, string workspaceDir)
            {
                return _owner.DeleteCodexThreadAsync(session.Provider, workspaceDir, session.SessionId);
            }
        }

        /// <summary>
        /// Devin sessions. Listing uses the CLI's own <c>list --format json</c> rather than its
        /// SQLite store, and the transcript comes over ACP <c>session/load</c> — both supported
        /// surfaces, so nothing here depends on Devin's on-disk layout.
        /// </summary>
        private sealed class DevinSessionHistoryProvider : ISessionHistoryProvider
        {
            private readonly ClaudeCodeControl _owner;

            public DevinSessionHistoryProvider(ClaudeCodeControl owner)
            {
                _owner = owner;
            }

            public bool Supports(AiProvider provider)
            {
                return provider == AiProvider.Devin || provider == AiProvider.DevinNative;
            }

            public string DisplayName(AiProvider provider)
            {
                return provider == AiProvider.Devin ? "Devin (WSL)" : "Devin";
            }

            public bool CanViewTranscript { get { return true; } }

            // Neither the CLI nor Devin's ACP server exposes a delete verb, and removing rows from
            // sessions.db behind the agent's back would desynchronise its own state.
            public bool CanDelete { get { return false; } }

            public bool SupportsContinueSentinel { get { return true; } }

            public string DeleteConfirmationSuffix { get { return string.Empty; } }

            public async Task<SessionHistoryListResult> ListAsync(AiProvider provider, string workspaceDir)
            {
                List<SessionInfo> sessions = await _owner.LoadDevinSessionsAsync(provider, workspaceDir);

                return new SessionHistoryListResult
                {
                    Sessions = sessions,
                    EmptyMessage = "No Devin sessions were found for this workspace."
                };
            }

            public async Task<string> ReadTranscriptAsync(SessionInfo session, string workspaceDir)
            {
                DevinThreadTranscript transcript = await _owner.ReadDevinTranscriptAsync(
                    session.Provider, workspaceDir, session.SessionId, CancellationToken.None);

                return BuildReadableDevinTranscript(session, transcript);
            }

            public Task DeleteAsync(SessionInfo session, string workspaceDir)
            {
                throw new NotSupportedException("Devin sessions cannot be deleted from the CLI.");
            }
        }

        #endregion

        #region Devin History Client Wiring

        /// <summary>Builds the launch options for a short-lived Devin history read (list or transcript).</summary>
        private DevinSessionHistoryOptions CreateDevinHistoryOptions(AiProvider provider, string workspaceDir)
        {
            bool isWsl = provider == AiProvider.Devin;
            string freshPath = GetFreshPathFromRegistry();
            string executable = ResolveNativeProviderExecutable(provider, "devin");
            if (!isWsl)
            {
                executable = ResolveExecutableOnPath(executable, freshPath);
            }

            var options = new DevinSessionHistoryOptions
            {
                UseWsl = isWsl,
                ExecutablePath = executable,
                WorkingDirectory = workspaceDir ?? string.Empty,
                WslWorkingDirectory = isWsl ? ConvertToWslPath(workspaceDir) : string.Empty
            };

            if (!isWsl && !string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            return options;
        }

        /// <summary>
        /// Lists Devin sessions scoped to the workspace via <c>devin list --format json</c>. The CLI
        /// already filters by its own working directory; the equality check below is defense in depth
        /// against a future CLI version that stops doing so, not the primary filter.
        /// </summary>
        private async Task<List<SessionInfo>> LoadDevinSessionsAsync(AiProvider provider, string workspaceDir)
        {
            var result = new List<SessionInfo>();
            DevinSessionHistoryOptions options = CreateDevinHistoryOptions(provider, workspaceDir);
            string expectedCwd = options.UseWsl ? options.WslWorkingDirectory : workspaceDir;

            IList<DevinSessionSummary> sessions = await DevinSessionHistoryClient
                .ListSessionsAsync(options, CancellationToken.None);

            foreach (DevinSessionSummary session in sessions)
            {
                if (!string.IsNullOrWhiteSpace(session.WorkingDirectory) &&
                    !string.IsNullOrWhiteSpace(expectedCwd) &&
                    !PathsLooselyMatch(session.WorkingDirectory, expectedCwd))
                {
                    continue;
                }

                var info = new SessionInfo
                {
                    SessionId = session.Id,
                    FilePath = string.Empty,
                    Preview = string.IsNullOrWhiteSpace(session.Title) ? "(untitled session)" : session.Title,
                    CustomTitle = string.Empty,
                    MessageCount = -1,
                    TokenCount = -1,
                    LastModified = session.LastActivity,
                    Cwd = session.WorkingDirectory,
                    Provider = provider
                };

                if (_settings?.SessionCustomTitles != null &&
                    _settings.SessionCustomTitles.TryGetValue(info.SessionId, out string title) &&
                    !string.IsNullOrWhiteSpace(title))
                {
                    info.CustomTitle = title;
                }

                result.Add(info);
            }

            return result.OrderByDescending(session => session.LastModified).ToList();
        }

        /// <summary>Loose path comparison across WSL/native separator and case conventions.</summary>
        private static bool PathsLooselyMatch(string a, string b)
        {
            string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads a Devin session's transcript by replaying it over ACP <c>session/load</c>.</summary>
        private async Task<DevinThreadTranscript> ReadDevinTranscriptAsync(
            AiProvider provider, string workspaceDir, string sessionId, CancellationToken cancellationToken)
        {
            DevinSessionHistoryOptions options = CreateDevinHistoryOptions(provider, workspaceDir);
            return await DevinSessionHistoryClient.ReadTranscriptAsync(options, sessionId, cancellationToken);
        }

        /// <summary>Formats a replayed Devin transcript the same way Claude/Codex transcripts are shown.</summary>
        private static string BuildReadableDevinTranscript(SessionInfo session, DevinThreadTranscript transcript)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Title:     {NormalizeTranscriptTitle(session.DisplayTitle)}");
            sb.AppendLine($"Session:   {session.SessionId}");
            sb.AppendLine($"Directory: {session.Cwd}");
            sb.AppendLine($"Modified:  {session.LastModified:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Messages:  {transcript?.Messages?.Count(m => m != null && !m.IsThought) ?? 0}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            if (transcript?.Messages == null) return sb.ToString();

            foreach (DevinTranscriptMessage message in transcript.Messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Text)) continue;

                string speaker = message.IsUser ? "USER" : message.IsThought ? "THINKING" : "ASSISTANT";
                sb.AppendLine($"{speaker}:");
                sb.AppendLine(message.Text.TrimEnd());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        #endregion
    }
}
