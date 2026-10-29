/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Session history dialog shared by every provider in ClaudeCodeControl.SessionHistoryProviders.cs
 *          (Claude Code, Codex, Devin), plus Claude's own native/WSL discovery and JSONL parsing
 *          used by its ISessionHistoryProvider implementation
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using static ClaudeCodeVS.UI.Loc;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Path Encoding & Session Directory Discovery

        /// <summary>
        /// Replicates Claude Code's filesystem-safe encoding of a working directory: every
        /// character outside ASCII <c>[a-zA-Z0-9]</c> becomes a single dash. Example:
        /// <c>C:\Users\Daniel_Liedke</c> → <c>C--Users-Daniel-Liedke</c>.
        /// Non-ASCII letters/digits (e.g. Japanese) are also turned into dashes, matching
        /// the CLI — so <c>char.IsLetterOrDigit</c> (Unicode-aware) must NOT be used here.
        /// </summary>
        internal static string EncodeClaudeProjectPath(string workspacePath)
        {
            if (string.IsNullOrEmpty(workspacePath)) return string.Empty;

            var sb = new StringBuilder(workspacePath.Length + 4);
            foreach (char c in workspacePath)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('-');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Resolves the native Claude Code config directory (the <c>.claude</c> root that holds
        /// <c>projects/</c>, <c>todos/</c>, etc.). Honors the <c>CLAUDE_CONFIG_DIR</c> environment
        /// variable that the CLI itself reads, so a relocated store (e.g. on another drive) is found.
        /// Falls back to <c>%UserProfile%\.claude</c> when the variable is unset or blank.
        /// </summary>
        private static string GetClaudeConfigDir()
        {
            string configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                return Environment.ExpandEnvironmentVariables(configDir.Trim());
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".claude");
        }

        /// <summary>
        /// Builds the per-workspace session directory under a native <c>.claude</c> config root,
        /// tolerating a <c>CLAUDE_CONFIG_DIR</c> that was pointed at the <c>projects</c> subfolder
        /// itself instead of the <c>.claude</c> root. The canonical layout is
        /// <c>&lt;configDir&gt;\projects\&lt;encoded&gt;</c>; if that folder is missing but the
        /// config dir already ends in <c>projects</c> and <c>&lt;configDir&gt;\&lt;encoded&gt;</c>
        /// exists, that direct path is returned instead. Falls back to the canonical path when
        /// neither exists so downstream behavior/logging is unchanged.
        /// </summary>
        private static string ResolveNativeProjectsSessionDir(string configDir, string encoded)
        {
            string canonical = Path.Combine(configDir, "projects", encoded);
            if (Directory.Exists(canonical))
            {
                return canonical;
            }

            // User set CLAUDE_CONFIG_DIR to "...\.claude\projects" (one level too deep):
            // treat the config dir itself as the projects root.
            if (string.Equals(Path.GetFileName(configDir.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)), "projects", StringComparison.OrdinalIgnoreCase))
            {
                string direct = Path.Combine(configDir, encoded);
                if (Directory.Exists(direct))
                {
                    return direct;
                }
            }

            return canonical;
        }

        /// <summary>
        /// Resolves the Claude Code projects directory for the active provider and workspace.
        /// For Windows-native Claude Code returns a direct local path; for the WSL provider
        /// shells out to <c>wslpath -w</c> so the directory can still be enumerated through
        /// regular .NET file IO via the <c>\\wsl.localhost\</c> share.
        /// </summary>
        private async Task<string> ResolveSessionDirectoryAsync(AiProvider provider, string workspaceDir)
        {
            if (string.IsNullOrEmpty(workspaceDir)) return null;

            if (provider == AiProvider.ClaudeCode)
            {
                string configDir = GetClaudeConfigDir();
                string encoded = EncodeClaudeProjectPath(workspaceDir);
                return ResolveNativeProjectsSessionDir(configDir, encoded);
            }

            if (provider == AiProvider.ClaudeCodeWSL)
            {
                string wslWorkspace = ConvertToWslPath(workspaceDir);
                string encoded = EncodeClaudeProjectPath(wslWorkspace);
                return await ResolveWslSessionDirectoryAsync(encoded);
            }

            return null;
        }

        /// <summary>
        /// Runs <c>wslpath -w "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/projects/&lt;encoded&gt;"</c> so the
        /// WSL session folder is reachable from native .NET file IO, honoring a relocated config dir
        /// inside WSL. Returns null on any failure (no WSL,
        /// command not found, empty stdout) and the dialog falls back to "no sessions found".
        /// </summary>
        private async Task<string> ResolveWslSessionDirectoryAsync(string encoded)
        {
            try
            {
                string args = $"bash -lic \"wslpath -w \\\"${{CLAUDE_CONFIG_DIR:-$HOME/.claude}}/projects/{encoded}\\\"\"";
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();
                    Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                    bool exited = await Task.Run(() => p.WaitForExit(5000));
                    if (!exited)
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                            // Ignore failures on kill; the caller will fall back to no sessions.
                        }

                        return null;
                    }

                    string stdout = await stdoutTask;
                    await stderrTask;
                    stdout = stdout?.Trim();
                    return string.IsNullOrEmpty(stdout) ? null : stdout;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResolveWslSessionDirectoryAsync error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Creates a short-lived App Server client in the selected Codex environment.</summary>
        private CodexAppServerClient CreateCodexHistoryClient(AiProvider provider, string workspaceDir)
        {
            bool isWsl = provider == AiProvider.Codex;
            string freshPath = GetFreshPathFromRegistry();
            string executable = ResolveNativeProviderExecutable(provider, "codex");
            if (!isWsl)
            {
                executable = ResolveExecutableOnPath(executable, freshPath);
            }

            var options = new CodexAppServerOptions
            {
                UseWsl = isWsl,
                ExecutablePath = executable,
                WorkingDirectory = workspaceDir ?? string.Empty,
                WslWorkingDirectory = isWsl ? ConvertToWslPath(workspaceDir) : string.Empty,
                ClientVersion = typeof(ClaudeCodeControl).Assembly.GetName().Version?.ToString() ?? "1.0"
            };

            if (!isWsl && !string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            return new CodexAppServerClient(options);
        }

        /// <summary>Loads Codex thread summaries through the supported rich-client API.</summary>
        private async Task<List<SessionInfo>> LoadCodexSessionsAsync(
            AiProvider provider,
            string workspaceDir)
        {
            var result = new List<SessionInfo>();
            string cwd = provider == AiProvider.Codex ? ConvertToWslPath(workspaceDir) : workspaceDir;

            using (CodexAppServerClient client = CreateCodexHistoryClient(provider, workspaceDir))
            {
                await client.StartAsync(CancellationToken.None);
                IList<CodexThreadSummary> threads = await client.ListThreadsAsync(cwd, CancellationToken.None);

                foreach (CodexThreadSummary thread in threads)
                {
                    var info = new SessionInfo
                    {
                        SessionId = thread.Id,
                        FilePath = thread.Path,
                        // A Codex-owned thread name is the provider's normal display label, not an
                        // extension rename. Keep CustomTitle reserved for the local override so
                        // clearing one reliably restores Codex's name (or its first-prompt preview).
                        Preview = string.IsNullOrWhiteSpace(thread.Name) ? thread.Preview : thread.Name,
                        CustomTitle = string.Empty,
                        MessageCount = -1,
                        TokenCount = -1,
                        LastModified = thread.LastModified,
                        Cwd = thread.Cwd,
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
            }

            return result.OrderByDescending(session => session.LastModified).ToList();
        }

        /// <summary>Reads a complete Codex thread for View and native-mode transcript replay.</summary>
        private async Task<CodexThreadTranscript> ReadCodexThreadAsync(
            AiProvider provider,
            string workspaceDir,
            string threadId)
        {
            using (CodexAppServerClient client = CreateCodexHistoryClient(provider, workspaceDir))
            {
                await client.StartAsync(CancellationToken.None);
                return await client.ReadThreadAsync(threadId, CancellationToken.None);
            }
        }

        /// <summary>Deletes a Codex thread through App Server so its rollout and metadata stay aligned.</summary>
        private async Task DeleteCodexThreadAsync(
            AiProvider provider,
            string workspaceDir,
            string threadId)
        {
            using (CodexAppServerClient client = CreateCodexHistoryClient(provider, workspaceDir))
            {
                await client.StartAsync(CancellationToken.None);
                await client.DeleteThreadAsync(threadId, CancellationToken.None);
            }
        }

        #endregion

        #region JSONL Parsing

        /// <summary>
        /// Lists every <c>*.jsonl</c> transcript under <paramref name="sessionDir"/> and parses
        /// each in parallel on the thread-pool. Files that fail to parse are silently skipped
        /// so one corrupt transcript can't blank out the entire history list.
        /// </summary>
        private async Task<List<SessionInfo>> LoadSessionsAsync(string sessionDir, AiProvider provider)
        {
            var result = new List<SessionInfo>();
            if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir))
            {
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(sessionDir, "*.jsonl", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadSessionsAsync enumerate error: {ex.Message}");
                return result;
            }

            var parsed = await Task.Run(() =>
            {
                var bag = new List<SessionInfo>();
                foreach (string f in files)
                {
                    try
                    {
                        var info = ParseSessionFile(f, provider);
                        if (info != null) bag.Add(info);

                        // Sessions from native mode (including ones from before this existed) are
                        // listed here, so they get listed in the CLI's own /resume picker too.
                        if (info != null && _claudeResumeVisibleSessions.TryAdd(info.SessionId, true))
                        {
                            PatchClaudeTranscriptEntrypoint(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Skipping unreadable session file {f}: {ex.Message}");
                    }
                }
                return bag;
            });

            // A name given in Claude Code itself (issue #170) replaces the saved one — renames here
            // write to the transcript as well, so its last title record is always the newest.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bool titlesChanged = false;
            foreach (var info in parsed)
            {
                titlesChanged |= ApplyTranscriptTitle(info.SessionId, info.TranscriptTitle);
            }
            if (titlesChanged)
            {
                SaveSettings();
            }

            // Apply any user-assigned custom titles (issue #95) — done on the UI-bound
            // result rather than in the thread-pool parse so settings access stays simple.
            var titles = _settings?.SessionCustomTitles;
            if (titles != null && titles.Count > 0)
            {
                foreach (var info in parsed)
                {
                    if (info.SessionId != null &&
                        titles.TryGetValue(info.SessionId, out string title) &&
                        !string.IsNullOrWhiteSpace(title))
                    {
                        info.CustomTitle = title;
                    }
                }
            }

            result.AddRange(parsed.OrderByDescending(s => s.LastModified));
            return result;
        }

        /// <summary>
        /// The <paramref name="count"/> most recently modified Claude Code conversations of a workspace,
        /// newest first, for the welcome card. Only as many transcripts are parsed as it takes to find
        /// them; ones without a prompt are skipped. Conversations open in a chat tab are kept, so the
        /// card lists the same ones as Session History. Titles are applied the way Session History applies them.
        /// </summary>
        private async Task<List<SessionInfo>> LoadRecentClaudeSessionsAsync(AiProvider provider, string workspaceDir,
            int count)
        {
            string sessionDir = await ResolveSessionDirectoryAsync(provider, workspaceDir);
            if (string.IsNullOrEmpty(sessionDir))
            {
                return new List<SessionInfo>();
            }

            List<SessionInfo> recent = await Task.Run(() =>
            {
                var found = new List<SessionInfo>();
                if (!Directory.Exists(sessionDir)) return found;

                IEnumerable<FileInfo> files = new DirectoryInfo(sessionDir)
                    .GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc);

                foreach (FileInfo file in files)
                {
                    try
                    {
                        SessionInfo info = ParseSessionFile(file.FullName, provider);
                        if (info != null && info.MessageCount > 0) found.Add(info);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Skipping unreadable session file {file.FullName}: {ex.Message}");
                    }

                    if (found.Count >= count) break;
                }

                // Shown by last interaction, which the file write time only approximates.
                return found.OrderByDescending(s => s.LastModified).ToList();
            });

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (SessionInfo info in recent)
            {
                // The transcript's own name is the newest (renames here are written to it too).
                string transcriptTitle = CurrentClaudeTranscriptTitle(info.SessionId, info.TranscriptTitle);
                info.CustomTitle = !string.IsNullOrWhiteSpace(transcriptTitle)
                    ? transcriptTitle
                    : GetNativeSessionTitle(info.SessionId);
            }

            return recent;
        }

        /// <summary>
        /// Reads a single JSONL transcript and extracts the metadata shown in the dialog:
        /// first user-typed prompt (preview), user/assistant turn count, total token usage,
        /// and the originating cwd. Skips system/snapshot/attachment lines because they
        /// inflate counts without representing real conversation turns.
        /// </summary>
        private SessionInfo ParseSessionFile(string filePath, AiProvider provider)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return null;

            string sessionId = Path.GetFileNameWithoutExtension(filePath);
            string preview = string.Empty;
            string cwd = string.Empty;
            int messageCount = 0;
            int tokenCount = 0;
            bool sawCommandInvocation = false;
            string transcriptTitle = null;
            string gitBranch = string.Empty;
            DateTime? lastInteraction = null;

            // Open with FileShare.ReadWrite so we can also peek at the active session
            // that Claude Code itself currently has open for writing.
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    string type = (string)obj["type"];
                    if (string.IsNullOrEmpty(type)) continue;

                    // Name given in Claude Code itself (or synced from here); the last record wins.
                    if (type == "custom-title")
                    {
                        transcriptTitle = (string)obj["customTitle"] ?? string.Empty;
                        continue;
                    }

                    if (string.IsNullOrEmpty(cwd))
                    {
                        cwd = (string)obj["cwd"] ?? string.Empty;
                    }

                    // Every record carries the branch it was written on; the last one is where the
                    // session left off. "HEAD" means a detached checkout, not a branch, so it is skipped
                    // and the last real branch name stays.
                    string recordBranch = (string)obj["gitBranch"];
                    if (!string.IsNullOrEmpty(recordBranch) && recordBranch != "HEAD")
                    {
                        gitBranch = recordBranch;
                    }

                    // The last prompt or reply is when the conversation was last used; the file's own
                    // write time is not, since title records and the /resume entrypoint fix write to it too.
                    if (type == "user" || type == "assistant")
                    {
                        lastInteraction = ReadTranscriptRecordTime(obj) ?? lastInteraction;
                    }

                    // Checked before the type dispatch: skill expansions land as "assistant" lines and
                    // would inflate the count there too. Kept after the cwd read so a session whose
                    // first record is a meta line does not lose its originating directory.
                    if (IsMetaTranscriptLine(obj)) continue;

                    if (type == "user")
                    {
                        // Skip "user" lines whose content is actually a tool_result attachment —
                        // we only want what the human actually typed in the prompt box.
                        var msg = obj["message"];
                        if (msg == null) continue;

                        string text = ExtractUserText(msg["content"]);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        // Slash-command invocations (e.g. "/effort medium") are expanded by the CLI into
                        // a literal <command-name>/<command-message>/<command-args> block rather than
                        // flagged with isMeta. A session made up of nothing but these isn't a real
                        // conversation, so it's excluded below rather than cluttering the history list.
                        if (IsCommandInvocationText(text))
                        {
                            sawCommandInvocation = true;
                            continue;
                        }

                        messageCount++;
                        if (string.IsNullOrEmpty(preview))
                        {
                            // Without what the extension wraps around a prompt (pull conflicts,
                            // attachments); a message that was only that leaves the next one to preview.
                            preview = SessionListPresentation.CleanPromptPreview(text);
                        }
                    }
                    else if (type == "assistant")
                    {
                        messageCount++;
                        var usage = obj["message"]?["usage"];
                        if (usage != null)
                        {
                            int input = (int?)usage["input_tokens"] ?? 0;
                            int output = (int?)usage["output_tokens"] ?? 0;
                            tokenCount += input + output;
                        }
                    }
                }
            }

            // A transcript made up only of slash-command invocations (no genuine user/assistant
            // turn) isn't worth surfacing in the history list — nothing to resume or read back.
            if (messageCount == 0 && sawCommandInvocation)
            {
                return null;
            }

            if (string.IsNullOrEmpty(preview))
            {
                preview = L("(no user messages)");
            }

            return new SessionInfo
            {
                SessionId = sessionId,
                FilePath = filePath,
                Preview = preview,
                GitBranch = gitBranch,
                MessageCount = messageCount,
                TokenCount = tokenCount,
                LastModified = lastInteraction ?? fi.LastWriteTime,
                Cwd = cwd,
                Provider = provider,
                TranscriptTitle = transcriptTitle
            };
        }

        /// <summary>
        /// The local time of a transcript record's <c>timestamp</c> (ISO 8601, UTC), or null when it
        /// has none. Json.NET may already have turned the value into a date while parsing the line.
        /// </summary>
        internal static DateTime? ReadTranscriptRecordTime(JObject record)
        {
            JToken token = record?["timestamp"];
            if (token == null) return null;

            if (token.Type == JTokenType.Date)
            {
                DateTime value = token.Value<DateTime>();
                return value.Kind == DateTimeKind.Unspecified ? value : value.ToLocalTime();
            }

            if (token.Type == JTokenType.String &&
                DateTime.TryParse((string)token, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
            {
                return parsed.Kind == DateTimeKind.Unspecified ? parsed : parsed.ToLocalTime();
            }

            return null;
        }

        /// <summary>
        /// The two records Claude Code itself appends when a session is named (<c>--name</c>, or a
        /// rename inside the CLI): its /resume picker and terminal title read the last pair in the
        /// transcript. Newline-terminated, compact JSON, property order as the CLI writes it.
        /// </summary>
        internal static string BuildClaudeTitleRecords(string sessionId, string title)
        {
            string customTitle = Newtonsoft.Json.JsonConvert.SerializeObject(
                new JObject { ["type"] = "custom-title", ["customTitle"] = title ?? string.Empty, ["sessionId"] = sessionId });
            string agentName = Newtonsoft.Json.JsonConvert.SerializeObject(
                new JObject { ["type"] = "agent-name", ["agentName"] = title ?? string.Empty, ["sessionId"] = sessionId });
            return customTitle + "\n" + agentName + "\n";
        }

        /// <summary>
        /// Pushes a rename into the transcript so Claude Code's own /resume picker shows it too
        /// (issue #170). Append-only — the same thing the CLI does — so it is safe next to a live
        /// session that is writing the file, and the earlier records stay untouched. A file that
        /// does not end in a newline (a write caught mid-line) gets one first so the new records
        /// start on their own line. Best-effort: the local rename has already been saved, so a
        /// failure here is logged, never surfaced.
        /// </summary>
        internal static void AppendClaudeTitleRecords(string filePath, string sessionId, string title)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                string records = BuildClaudeTitleRecords(sessionId, title);
                if (!EndsWithNewline(filePath)) records = "\n" + records;

                using (var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(records);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeCode] Could not sync session title to transcript: {ex.Message}");
            }
        }

        /// <summary>
        /// Rename entry point for callers that only know the session id (the native chat tab's ✎
        /// button): finds the transcript in the workspace's project folder — or, for the Windows
        /// store, in whichever project folder holds it, since a tab can outlive a workspace switch —
        /// and appends the title records. Claude Code only; every other provider is a no-op.
        /// </summary>
        private async Task SyncClaudeSessionTitleAsync(AiProvider provider, string sessionId, string title)
        {
            try
            {
                string path = await FindClaudeTranscriptPathAsync(provider, sessionId);
                if (path == null) return;

                // The name the transcript had (usually the automatic one) is what a running CLI writes back.
                string previous = await Task.Run(() => ReadClaudeTranscriptTitle(path));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MarkClaudeTitleSuperseded(sessionId, previous, title);

                await Task.Run(() => AppendClaudeTitleRecords(path, sessionId, title));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeCode] Could not locate transcript to sync session title: {ex.Message}");
            }
        }

        /// <summary>
        /// The transcript file of a Claude Code session id, or null (not a Claude provider, or no file
        /// yet). Looks in the workspace's project folder first and — for the Windows store — then in
        /// whichever project folder holds it, since a chat tab can outlive a workspace switch.
        /// </summary>
        private async Task<string> FindClaudeTranscriptPathAsync(AiProvider provider, string sessionId)
        {
            if (!IsClaudeCodeSessionHistoryProvider(provider) || string.IsNullOrEmpty(sessionId)) return null;

            string fileName = sessionId + ".jsonl";
            string dir = await ResolveSessionDirectoryAsync(provider, await GetWorkspaceDirectoryAsync());

            return await Task.Run(() =>
            {
                string path = dir != null ? Path.Combine(dir, fileName) : null;
                if (path != null && File.Exists(path)) return path;
                if (provider != AiProvider.ClaudeCode) return null;

                string projects = Path.Combine(GetClaudeConfigDir(), "projects");
                return Directory.Exists(projects)
                    ? Directory.EnumerateDirectories(projects)
                        .Select(d => Path.Combine(d, fileName))
                        .FirstOrDefault(File.Exists)
                    : null;
            });
        }

        /// <summary>Session ids whose transcript already reads as a terminal session (see below).</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _claudeResumeVisibleSessions =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Session ids whose transcript title was already checked by a chat tab.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _claudeTitleAdoptedSessions =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Called after each native-mode Claude turn: makes the session show up in the CLI's own
        /// /resume picker (see <see cref="PatchClaudeTranscriptEntrypoint"/>). Once a transcript is
        /// settled it is not read again for the rest of the VS session.
        /// </summary>
        private async Task MakeNativeSessionResumableInCliAsync(AiProvider provider, string sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId) || _claudeResumeVisibleSessions.ContainsKey(sessionId)) return;

                string path = await FindClaudeTranscriptPathAsync(provider, sessionId);
                if (path == null) return;

                await Task.Run(() => PatchClaudeTranscriptEntrypoint(path));
                _claudeResumeVisibleSessions[sessionId] = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeCode] Could not make native session visible to the CLI: {ex.Message}");
            }
        }

        /// <summary>
        /// Titles renamed away from in this VS session, per session id. A Claude Code process that was
        /// already running keeps the name it started with and appends it again after every turn, so a
        /// transcript whose last title is one of these is stale, not a rename made in the CLI.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, bool>> _claudeSupersededTitles =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records that <paramref name="oldTitle"/> was replaced by <paramref name="newTitle"/> here.</summary>
        private void MarkClaudeTitleSuperseded(string sessionId, string oldTitle, string newTitle)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            var titles = _claudeSupersededTitles.GetOrAdd(sessionId,
                _ => new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal));

            // Renaming back to an earlier name makes that one current again.
            titles.TryRemove((newTitle ?? string.Empty).Trim(), out _);

            string old = (oldTitle ?? string.Empty).Trim();
            if (old.Length > 0 && !string.Equals(old, (newTitle ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                titles[old] = true;
            }
        }

        /// <summary>The transcript's title, or null when it is one renamed away from here (a stale write).</summary>
        private string CurrentClaudeTranscriptTitle(string sessionId, string transcriptTitle)
        {
            return IsSupersededClaudeTitle(transcriptTitle, GetSupersededClaudeTitles(sessionId)) ? null : transcriptTitle;
        }

        private ICollection<string> GetSupersededClaudeTitles(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && _claudeSupersededTitles.TryGetValue(sessionId, out var titles)
                ? titles.Keys
                : (ICollection<string>)Array.Empty<string>();
        }

        /// <summary>True when <paramref name="transcriptTitle"/> is one of the names renamed away from.</summary>
        internal static bool IsSupersededClaudeTitle(string transcriptTitle, ICollection<string> supersededTitles)
        {
            if (transcriptTitle == null || supersededTitles == null || supersededTitles.Count == 0) return false;
            return supersededTitles.Contains(transcriptTitle.Trim());
        }

        /// <summary>
        /// Whether the saved title has to be appended to the transcript again: the tail has no title
        /// record (Claude Code's /resume picker only scans the last <see cref="ClaudeTranscriptScanBytes"/>),
        /// or its last one is a name renamed away from that a running CLI wrote back.
        /// </summary>
        internal static bool ShouldAppendSavedClaudeTitle(string transcriptTitle, string savedTitle, ICollection<string> supersededTitles)
        {
            if (string.IsNullOrWhiteSpace(savedTitle)) return false;
            if (transcriptTitle == null) return true;

            return !string.Equals(transcriptTitle.Trim(), savedTitle.Trim(), StringComparison.Ordinal)
                && IsSupersededClaudeTitle(transcriptTitle, supersededTitles);
        }

        /// <summary>
        /// Keeps the session's saved name (given by hand or generated from the first prompt) as the
        /// transcript's last title record, which is what Claude Code's /resume picker and Session History
        /// read (see <see cref="ShouldAppendSavedClaudeTitle"/>). Checked again a moment later, because the
        /// CLI can write its own title record just after the turn ends. Called after each native turn and
        /// when an automatic name is saved; cheap when there is nothing to do.
        /// </summary>
        private async Task KeepClaudeTitleInTranscriptAsync(AiProvider provider, string sessionId)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                string path = await FindClaudeTranscriptPathAsync(provider, sessionId);
                if (path == null) return;

                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass > 0) await Task.Delay(3000);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    string title = GetNativeSessionTitle(sessionId);
                    var superseded = GetSupersededClaudeTitles(sessionId).ToList();

                    await Task.Run(() =>
                    {
                        if (ShouldAppendSavedClaudeTitle(ReadClaudeTranscriptTitle(path), title, superseded))
                        {
                            AppendClaudeTitleRecords(path, sessionId, title);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeCode] Could not keep session title in transcript: {ex.Message}");
            }
        }

        /// <summary>
        /// Picks up a name given to the session in Claude Code itself (issue #170, the other
        /// direction): when the transcript's last title record differs from the saved title, the saved
        /// one is replaced and <paramref name="onChanged"/> refreshes the tab. Runs once per session id.
        /// </summary>
        private async Task AdoptClaudeTranscriptTitleAsync(AiProvider provider, string sessionId, Action onChanged)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId) || !_claudeTitleAdoptedSessions.TryAdd(sessionId, true)) return;

                string path = await FindClaudeTranscriptPathAsync(provider, sessionId);
                if (path == null) return;

                string transcriptTitle = await Task.Run(() => ReadClaudeTranscriptTitle(path));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (ApplyTranscriptTitle(sessionId, transcriptTitle))
                {
                    SaveSettings();
                    onChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeCode] Could not read session title from transcript: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes the saved title follow the transcript's (null = the transcript never named the
        /// session, so the saved title stands). Returns true when the saved titles changed.
        /// </summary>
        private bool ApplyTranscriptTitle(string sessionId, string transcriptTitle)
        {
            if (transcriptTitle == null || string.IsNullOrEmpty(sessionId) || _settings == null) return false;

            // A name renamed away from here, written back by a CLI that was still running.
            if (CurrentClaudeTranscriptTitle(sessionId, transcriptTitle) == null) return false;

            if (_settings.SessionCustomTitles == null)
            {
                _settings.SessionCustomTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string newTitle = transcriptTitle.Trim();
            _settings.SessionCustomTitles.TryGetValue(sessionId, out string savedTitle);

            if (string.IsNullOrEmpty(newTitle))
            {
                SetSessionAutoTitle(sessionId, false);
                return _settings.SessionCustomTitles.Remove(sessionId);
            }

            if (string.Equals(savedTitle, newTitle, StringComparison.Ordinal)) return false;

            // Named in Claude Code itself: a name the user gave it, no longer the automatic one.
            _settings.SessionCustomTitles[sessionId] = newTitle;
            SetSessionAutoTitle(sessionId, false);
            return true;
        }

        /// <summary>
        /// True when the session's saved title is the automatic one of a new chat rather than a name
        /// the user gave it. "Renamed only" and the chat's title bar leave those out.
        /// </summary>
        private bool IsAutoSessionTitle(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && _settings?.SessionAutoTitleIds?.Contains(sessionId) == true;
        }

        /// <summary>Marks the session's saved title as automatic, or as a name the user gave it. Not saved here.</summary>
        private void SetSessionAutoTitle(string sessionId, bool isAuto)
        {
            if (string.IsNullOrEmpty(sessionId) || _settings == null) return;

            if (_settings.SessionAutoTitleIds == null)
            {
                _settings.SessionAutoTitleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (isAuto) _settings.SessionAutoTitleIds.Add(sessionId);
            else _settings.SessionAutoTitleIds.Remove(sessionId);
        }

        /// <summary>
        /// The session name Claude Code keeps in a transcript: the last <c>custom-title</c> record, the
        /// same one its /resume picker shows. Null when the file has none, empty when the last one
        /// cleared the name. Only the file's tail is scanned — the CLI appends the record, so a current
        /// one is always near the end, and transcripts can run to many megabytes.
        /// </summary>
        internal static string ReadClaudeTranscriptTitle(string filePath)
        {
            string title = null;
            foreach (string line in ReadTailLines(filePath, ClaudeTranscriptScanBytes))
            {
                if (line.IndexOf("\"custom-title\"", StringComparison.Ordinal) < 0) continue;

                JObject obj;
                try { obj = JObject.Parse(line); }
                catch { continue; }

                if ((string)obj["type"] == "custom-title")
                {
                    title = (string)obj["customTitle"] ?? string.Empty;
                }
            }
            return title;
        }

        /// <summary>How much of a transcript's head and tail Claude Code reads for its picker metadata.</summary>
        internal const int ClaudeTranscriptScanBytes = 65536;

        private static readonly byte[] EntrypointKey = Encoding.ASCII.GetBytes("\"entrypoint\":\"");

        private static readonly HashSet<string> SdkEntrypoints =
            new HashSet<string>(StringComparer.Ordinal) { "sdk-cli", "sdk-ts", "sdk-py" };

        /// <summary>
        /// Native mode drives Claude Code headless, so every line it writes is tagged
        /// <c>"entrypoint":"sdk-cli"</c> — and the CLI's /resume picker hides sessions tagged with an
        /// SDK entrypoint. The picker decides on the first entrypoint in the file's first 64 KB (or the
        /// last one in its tail when the head has none), so rewriting just that one value to
        /// <c>cli</c> makes the session listed like any terminal session. The value is padded with
        /// spaces to its original length — JSON whitespace, still valid — so the file never changes
        /// size and a live CLI appending to it is unaffected. Returns true when it rewrote a value.
        /// </summary>
        internal static bool PatchClaudeTranscriptEntrypoint(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = fs.Length;
                    if (length == 0) return false;

                    long windowStart = 0;
                    byte[] window = ReadBytes(fs, 0, (int)Math.Min(length, ClaudeTranscriptScanBytes));
                    int keyAt = IndexOf(window, EntrypointKey, fromEnd: false);

                    if (keyAt < 0)
                    {
                        windowStart = Math.Max(0, length - ClaudeTranscriptScanBytes);
                        window = ReadBytes(fs, windowStart, (int)(length - windowStart));
                        keyAt = IndexOf(window, EntrypointKey, fromEnd: true);
                        if (keyAt < 0) return false;
                    }

                    int valueStart = keyAt + EntrypointKey.Length;
                    int valueEnd = Array.IndexOf(window, (byte)'"', valueStart);
                    if (valueEnd < 0) return false;

                    string value = Encoding.ASCII.GetString(window, valueStart, valueEnd - valueStart);
                    if (!SdkEntrypoints.Contains(value)) return false;

                    // "sdk-cli"  ->  "cli"      (closing quote moves left, spaces fill the gap)
                    byte[] replacement = Encoding.ASCII.GetBytes("cli\"" + new string(' ', value.Length - 3));
                    fs.Seek(windowStart + valueStart, SeekOrigin.Begin);
                    fs.Write(replacement, 0, replacement.Length);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeCode] Could not patch transcript entrypoint: {ex.Message}");
                return false;
            }
        }

        private static byte[] ReadBytes(FileStream fs, long offset, int count)
        {
            var buffer = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < count) Array.Resize(ref buffer, read);
            return buffer;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, bool fromEnd)
        {
            int last = haystack.Length - needle.Length;
            for (int i = fromEnd ? last : 0; fromEnd ? i >= 0 : i <= last; i += fromEnd ? -1 : 1)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>The complete lines in the last <paramref name="maxBytes"/> of a file (a line cut by the window is dropped).</summary>
        private static IEnumerable<string> ReadTailLines(string filePath, int maxBytes)
        {
            byte[] tail;
            bool cut;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                cut = start > 0;
                tail = ReadBytes(fs, start, (int)(fs.Length - start));
            }

            string[] lines = new UTF8Encoding(false).GetString(tail).Split('\n');
            return cut ? lines.Skip(1) : lines;
        }

        private static bool EndsWithNewline(string filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return true;
                fs.Seek(-1, SeekOrigin.End);
                return fs.ReadByte() == '\n';
            }
        }

        /// <summary>
        /// True for transcript lines the CLI injected on the user's behalf — local-command caveats
        /// and skill expansions — which Claude Code flags with a top-level <c>isMeta</c> and its own
        /// UI hides. They arrive as ordinary "user"/"assistant" lines carrying real text, so neither
        /// the type filter nor the tool-result check below catches them.
        /// <para>
        /// Only the JSON boolean <c>true</c> counts. A plain <c>(bool?)token == true</c> would not do:
        /// Newtonsoft coerces the *string* "true" to true, and throws <c>FormatException</c> on a
        /// string it cannot parse — so a future CLI writing anything but a boolean here would either
        /// change what the history shows or break reading the file.
        /// </para>
        /// </summary>
        internal static bool IsMetaTranscriptLine(JObject obj)
        {
            JToken meta = obj?["isMeta"];
            return meta != null && meta.Type == JTokenType.Boolean && meta.Value<bool>();
        }

        /// <summary>
        /// True for a user message that is purely a slash-command invocation the CLI expanded
        /// into text (e.g. typing <c>/effort medium</c> becomes a literal
        /// <c>&lt;command-name&gt;/effort&lt;/command-name&gt;</c> block), rather than isMeta-flagged
        /// like caveats/skill expansions. Checked on the trimmed start so a leading blank line in
        /// the stored text doesn't defeat the match.
        /// </summary>
        internal static bool IsCommandInvocationText(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                text.TrimStart().StartsWith("<command-name>", StringComparison.Ordinal);
        }

        /// <summary>
        /// Claude Code transcripts store user content either as a plain string or as an
        /// array of typed parts (text + image refs). This collapses both shapes to a single
        /// preview string while ignoring tool-result re-injections that would otherwise
        /// surface as the "first" user message.
        /// </summary>
        private static string ExtractUserText(JToken content)
        {
            if (content == null) return string.Empty;

            if (content.Type == JTokenType.String)
            {
                return ((string)content) ?? string.Empty;
            }

            if (content.Type == JTokenType.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.Children())
                {
                    string partType = (string)part["type"];
                    if (partType == "text")
                    {
                        string text = (string)part["text"];
                        if (!string.IsNullOrEmpty(text)) sb.Append(text);
                    }
                    // Skip "tool_result" / "image" parts — not human input.
                }
                return sb.ToString();
            }

            return string.Empty;
        }

        #endregion

        #region Toolbar Button + Dialog

        /// <summary>
        /// Refreshes the History button after a provider or layout change. The button stays visible;
        /// its click handler explains when the selected provider has no supported session store.
        /// </summary>
        private void RefreshSessionHistoryButton()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Session History placement (button vs Tools-menu entry) is handled centrally so it
                // stays in sync with the other features. It's always offered now; the click handler
                // explains when the active agent doesn't support resumable sessions.
                RefreshToolbarLayout();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing session history button: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true only for Claude Code providers that store transcripts in Claude's
        /// resumable session-history format.
        /// </summary>
        private static bool IsClaudeCodeSessionHistoryProvider(AiProvider? provider)
        {
            return provider == AiProvider.ClaudeCode || provider == AiProvider.ClaudeCodeWSL;
        }

        /// <summary>Returns true for the Windows and WSL Codex stores exposed by App Server.</summary>
        private static bool IsCodexSessionHistoryProvider(AiProvider? provider)
        {
            return provider == AiProvider.CodexNative || provider == AiProvider.Codex;
        }

        /// <summary>Providers whose persisted conversations can be listed and resumed here.</summary>
        private bool SupportsSessionHistory(AiProvider? provider)
        {
            return ResolveSessionHistoryProvider(provider) != null;
        }

        private string GetSessionHistoryProviderName(AiProvider provider)
        {
            ISessionHistoryProvider adapter = ResolveSessionHistoryProvider(provider);
            return adapter != null ? adapter.DisplayName(provider) : "Code Agent";
        }

        /// <summary>
        /// Builds the two-line row content for a session: the title — the session's name (given by
        /// hand or generated from the first prompt), else the first prompt itself — over a dim line
        /// with when, the counts, the git branch and whether a chat tab already shows it.
        /// <paramref name="timeOnly"/> is set under the Today/Yesterday bands, which already say the day.
        /// </summary>
        private static FrameworkElement CreateSessionItemContent(SessionInfo s, Brush themeFg, bool timeOnly, bool isOpen)
        {
            bool titled = !string.IsNullOrWhiteSpace(s.CustomTitle);

            var parts = new List<string>
            {
                s.LastModified.ToString(timeOnly ? "t" : "g", Loc.Culture)
            };
            if (s.MessageCount >= 0)
            {
                parts.Add(s.MessageCount == 1 ? L("1 message") : L("{0:N0} messages", s.MessageCount));
            }
            if (s.TokenCount >= 0)
            {
                parts.Add(L("{0} tokens", ChatUsageIndicator.FormatTokens(s.TokenCount)));
            }
            if (!string.IsNullOrEmpty(s.GitBranch))
            {
                parts.Add(s.GitBranch);
            }
            if (isOpen)
            {
                parts.Add(L("already open"));
            }

            var stack = new StackPanel { Orientation = Orientation.Vertical };

            stack.Children.Add(new TextBlock
            {
                Text = s.DisplayTitle,
                Foreground = themeFg,
                FontSize = 13,
                FontWeight = titled ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", parts),
                Foreground = themeFg,
                Opacity = 0.6,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });

            return stack;
        }

        /// <summary>
        /// The non-selectable row that opens a date band in the list: a small caption followed by a
        /// thin rule. Disabled so the keyboard and the mouse skip it; it carries no
        /// <see cref="SessionInfo"/>, so no action ever sees it as a session.
        /// </summary>
        private static ListBoxItem CreateSessionGroupHeader(SessionDateGroup group, Brush themeFg)
        {
            string caption;
            switch (group)
            {
                case SessionDateGroup.Today: caption = L("Today"); break;
                case SessionDateGroup.Yesterday: caption = L("Yesterday"); break;
                case SessionDateGroup.Last7Days: caption = L("Previous 7 days"); break;
                case SessionDateGroup.Last30Days: caption = L("Previous 30 days"); break;
                default: caption = L("Older"); break;
            }

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = themeFg,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.7
            });

            var rule = new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = themeFg,
                Opacity = 0.18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 1, 0, 0)
            };
            Grid.SetColumn(rule, 1);
            row.Children.Add(rule);

            return new ListBoxItem
            {
                Content = row,
                IsEnabled = false,
                Focusable = false,
                IsHitTestVisible = false,
                Padding = new Thickness(4, 12, 4, 4)
            };
        }

        /// <summary>
        /// Row look of the session list: rounded hover/selection background derived from the theme
        /// background (lighter on dark themes, darker on light ones) instead of the stock WPF
        /// gray box with its white focus border.
        /// </summary>
        private static Style CreateSessionRowStyle(Brush themeBg)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 7)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, ShiftThemeBrush(themeBg, 16)));
            style.Triggers.Add(hover);

            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, ShiftThemeBrush(themeBg, 34)));
            style.Triggers.Add(selected);

            return style;
        }

        /// <summary>The theme background moved <paramref name="shift"/> steps toward the foreground.</summary>
        private static Brush ShiftThemeBrush(Brush themeBg, int shift)
        {
            Color c = (themeBg as SolidColorBrush)?.Color ?? Colors.Gray;
            int delta = (c.R + c.G + c.B) < 384 ? shift : -shift;
            Func<byte, byte> move = v => (byte)Math.Max(0, Math.Min(255, v + delta));
            return new SolidColorBrush(Color.FromRgb(move(c.R), move(c.G), move(c.B)));
        }

        /// <summary>
        /// Builds the hover tooltip for a session list item. Shows the original auto-generated
        /// preview alongside the ids when a custom title is in use so it isn't lost.
        /// </summary>
        private static string BuildSessionTooltip(SessionInfo s)
        {
            string tip = L("Session: {0}", s.SessionId) + "\n" + L("File: {0}", s.FilePath) + "\n" + L("Folder: {0}", s.Cwd);
            if (!string.IsNullOrEmpty(s.GitBranch))
            {
                tip += "\n" + L("Branch: {0}", s.GitBranch);
            }
            if (!string.IsNullOrWhiteSpace(s.CustomTitle))
            {
                tip += "\n" + L("First prompt: {0}", s.Preview);
            }
            return tip;
        }

        /// <summary>
        /// Starts intentional fire-and-forget session-history work from void WPF event handlers.
        /// </summary>
        private static void StartSessionHistoryTask(Func<Task> asyncAction, string errorHandlerName)
        {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional for WPF event handlers
            ThreadHelper.JoinableTaskFactory.RunAsync(asyncAction).FileAndForget(errorHandlerName);
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Toolbar button click handler — opens the session-history dialog.
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void SessionHistoryButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ShowSessionHistoryDialogAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening session history: {ex.Message}");
                MessageBox.Show(L("Error opening session history: {0}", ex.Message),
                    L("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Builds and shows the session-history modal. The list is loaded asynchronously
        /// after the dialog is visible so the user gets a "Loading…" indicator instead
        /// of a frozen UI on cold reads (network shares, large numbers of transcripts).
        /// </summary>
        private async Task ShowSessionHistoryDialogAsync()
        {
            // Opened from a parallel chat tab, the dialog lists and resumes that tab's conversations;
            // null means the default session (panel / first chat tab), which resumes as before.
            NativeChatSessionState targetTab = IsNativeModeActive ? GetSession(ResolveFocusedNativeSessionId()) : null;

            AiProvider? selected = targetTab != null ? targetTab.SelectedProvider : _settings?.SelectedProvider;
            ISessionHistoryProvider adapter = ResolveSessionHistoryProvider(selected);
            if (adapter == null)
            {
                MessageBox.Show(
                    L("Session history is available for Claude Code, Codex and Devin (native or WSL). " +
                      "Switch the active code agent to one of those providers first."),
                    L("Session History"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // A GitHub issue tab runs in its own worktree, and the CLI only resumes from the same cwd.
            string workspaceDir = !string.IsNullOrEmpty(targetTab?.WorkspaceOverride)
                ? targetTab.WorkspaceOverride
                : await GetWorkspaceDirectoryAsync();
            if (string.IsNullOrEmpty(workspaceDir))
            {
                MessageBox.Show(L("Could not determine the current workspace directory."),
                    L("Session History"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = L("{0} Session History — {1}", GetSessionHistoryProviderName(selected.Value), workspaceDir),
                Width = 760,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 560,
                MinHeight = 320,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = Application.Current?.MainWindow; } catch { }
            // VS-themed title bar, scrollbars and controls (v276.0): the stock Windows chrome and
            // controls made the dialog look out of place next to the IDE.
            ThemedDialog.PrepareResources(dialog);

            var grid = new Grid { Margin = new Thickness(14, 6, 14, 14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // filter bar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

            // --- Row 0: filter bar (search + sort) ---
            var filterBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchBox = new TextBox
            {
                Style = ThemedDialog.GetStyle(dialog, "ThemedDialogSearchBoxStyle"),
                Tag = L("Search…"),
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = L("Type to filter by title, first prompt, branch, or date. Double-click a session to resume; " +
                            "right-click or press F2 to rename.")
            };
            Grid.SetColumn(searchBox, 0);
            filterBar.Children.Add(searchBox);

            // Sort selector (issue #114) — lets the user reliably order the list instead of
            // relying on the implicit last-modified ordering. Tag holds the persisted key.
            var sortLabel = new TextBlock
            {
                Text = L("Sort:"),
                Foreground = themeFg,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            // Use the shared flat themed ComboBox templates so the dropdown popup honors the
            // dark/light theme (the default WPF popup paints a hardcoded white background, which
            // left the light-on-white items unreadable in dark mode).
            var comboRes = BuildThemedComboResources(themeBg, themeFg);
            var sortCombo = MakeThemedComboBox(comboRes, themeFg);
            sortCombo.MinWidth = 160;
            sortCombo.Height = 30;
            sortCombo.ToolTip = L("Choose how sessions are ordered. Your choice is remembered across restarts.");

            var sortOptions = new[]
            {
                new { Key = "modified", Label = L("Last modified") },
                new { Key = "oldest",   Label = L("Oldest first") },
                new { Key = "tokens",   Label = L("Most tokens") },
                new { Key = "messages", Label = L("Most messages") },
                new { Key = "title",    Label = L("Title (A–Z)") }
            };
            string savedSort = string.IsNullOrWhiteSpace(_settings?.SessionHistorySortMode)
                ? "modified" : _settings.SessionHistorySortMode;
            foreach (var opt in sortOptions)
            {
                var item = new ComboBoxItem { Content = opt.Label, Tag = opt.Key, Foreground = themeFg };
                if (comboRes["cbi"] is Style cbiStyle) item.Style = cbiStyle;
                sortCombo.Items.Add(item);
                if (string.Equals(opt.Key, savedSort, StringComparison.OrdinalIgnoreCase))
                {
                    sortCombo.SelectedItem = item;
                }
            }
            if (sortCombo.SelectedItem == null && sortCombo.Items.Count > 0)
            {
                sortCombo.SelectedIndex = 0;
            }

            var renamedOnlyCheck = new CheckBox
            {
                Content = L("Renamed only"),
                Foreground = themeFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                IsChecked = _settings?.SessionHistoryRenamedOnly == true,
                ToolTip = L("Show only sessions you have given a custom title.")
            };

            var rightFilterPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            rightFilterPanel.Children.Add(sortLabel);
            rightFilterPanel.Children.Add(sortCombo);
            rightFilterPanel.Children.Add(renamedOnlyCheck);
            Grid.SetColumn(rightFilterPanel, 1);
            filterBar.Children.Add(rightFilterPanel);

            Grid.SetRow(filterBar, 0);
            grid.Children.Add(filterBar);

            // --- Row 1: session list, in a rounded frame ---
            var listBox = new ListBox
            {
                Background = Brushes.Transparent,
                Foreground = themeFg,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            // No horizontal scrolling — long titles ellipsize to the panel width instead of overflowing.
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
            listBox.ItemContainerStyle = CreateSessionRowStyle(themeBg);
            Border listFrame = ThemedDialog.ListFrame(listBox);
            Grid.SetRow(listFrame, 1);
            grid.Children.Add(listFrame);

            var loading = new TextBlock
            {
                Text = L("Loading sessions…"),
                Foreground = themeFg,
                FontStyle = FontStyles.Italic,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75
            };
            Grid.SetRow(loading, 1);
            grid.Children.Add(loading);

            // --- Row 2: button row ---
            Style buttonStyle = ThemedDialog.GetStyle(dialog, "ThemedDialogButtonStyle");
            Func<string, Button> mkBtn = (text) => new Button
            {
                Content = text,
                Style = buttonStyle,
                Margin = new Thickness(8, 0, 0, 0)
            };

            // Only the two actions that matter stay as buttons on the right; View, Rename, Delete and
            // Refresh live in the list-icon menu beside them (the same menu as a right-click on a row).
            var resumeButton = mkBtn(L("Resume"));
            resumeButton.Style = ThemedDialog.GetStyle(dialog, "ThemedDialogPrimaryButtonStyle");
            var closeButton = mkBtn(L("Close"));
            closeButton.IsCancel = true;
            // The action used most: Enter resumes the selected session, from the list or the filter box.
            resumeButton.IsDefault = true;

            var moreButton = new Button
            {
                Content = "", // BulletedList
                Style = ThemedDialog.GetStyle(dialog, "ThemedDialogIconButtonStyle"),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = L("More actions")
            };

            // Resumes the newest conversation, whatever the filters show; wired below with the list.
            var resumeLastButton = mkBtn(L("Resume Last Session"));
            resumeLastButton.Margin = new Thickness(0);
            resumeLastButton.IsEnabled = false;

            // Reopens today's conversations, each in a tab of its own; wired below with the list.
            var resumeTodayButton = mkBtn(L("Resume Today's Sessions"));
            resumeTodayButton.IsEnabled = false;

            var buttonBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(buttonBar, 2);
            DockPanel.SetDock(resumeLastButton, Dock.Left);
            buttonBar.Children.Add(resumeLastButton);
            DockPanel.SetDock(resumeTodayButton, Dock.Left);
            buttonBar.Children.Add(resumeTodayButton);

            var rightActions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(rightActions, Dock.Right);
            rightActions.Children.Add(resumeButton);
            rightActions.Children.Add(closeButton);
            rightActions.Children.Add(moreButton);
            buttonBar.Children.Add(rightActions);

            grid.Children.Add(buttonBar);

            dialog.Content = grid;
            ThemedDialog.ApplyChrome(dialog);

            // Full set of sessions for the workspace; the visible list is a filtered view of this.
            var loadedSessions = new List<SessionInfo>();

            // Today's conversations not already open in a chat tab, oldest first, so the most recent one
            // is opened last and ends up in front. Native mode only: the terminal has no chat tabs.
            Func<List<SessionInfo>> todaysSessionsToReopen = () =>
            {
                if (!IsNativeModeActive) return new List<SessionInfo>();
                DateTime now = DateTime.Now;
                return loadedSessions
                    .Where(s => SessionListPresentation.GetDateGroup(s.LastModified, now) == SessionDateGroup.Today
                                && !TryFindOpenChatTab(s.SessionId, out _))
                    .OrderBy(s => s.LastModified)
                    .ToList();
            };

            // Rebuilds the visible list from loadedSessions, applying the search text, and preserves
            // the current selection where possible. Sorted by date, the rows sit under date bands.
            Action applyFilter = () =>
            {
                string previouslySelectedId = (listBox.SelectedItem as ListBoxItem)?.Tag is SessionInfo psi
                    ? psi.SessionId : null;

                string query = (searchBox.Text ?? string.Empty).Trim();
                bool renamedOnly = renamedOnlyCheck.IsChecked == true;

                listBox.Items.Clear();

                string sortKey = (sortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "modified";
                IEnumerable<SessionInfo> orderedSessions;
                switch (sortKey)
                {
                    case "oldest":
                        orderedSessions = loadedSessions.OrderBy(s => s.LastModified);
                        break;
                    case "tokens":
                        orderedSessions = loadedSessions.OrderByDescending(s => s.TokenCount)
                                                        .ThenByDescending(s => s.LastModified);
                        break;
                    case "messages":
                        orderedSessions = loadedSessions.OrderByDescending(s => s.MessageCount)
                                                        .ThenByDescending(s => s.LastModified);
                        break;
                    case "title":
                        // Named sessions first, alphabetical; unnamed fall to the bottom by recency.
                        orderedSessions = loadedSessions
                            .OrderBy(s => string.IsNullOrWhiteSpace(s.CustomTitle) ? 1 : 0)
                            .ThenBy(s => s.CustomTitle, StringComparer.OrdinalIgnoreCase)
                            .ThenByDescending(s => s.LastModified);
                        break;
                    default: // "modified"
                        orderedSessions = loadedSessions.OrderByDescending(s => s.LastModified);
                        break;
                }

                bool groupByDate = sortKey == "modified" || sortKey == "oldest";
                DateTime now = DateTime.Now;
                SessionDateGroup? currentGroup = null;

                int reselectIndex = -1;
                int firstSessionIndex = -1;
                foreach (var s in orderedSessions)
                {
                    // The automatic title of a new chat is not a name the user gave it.
                    bool isRenamed = !string.IsNullOrWhiteSpace(s.CustomTitle) && !IsAutoSessionTitle(s.SessionId);
                    if (renamedOnly && !isRenamed) continue;

                    if (query.Length > 0)
                    {
                        string haystack = $"{s.CustomTitle}\n{s.Preview}\n{s.GitBranch}\n{s.LastModified:yyyy-MM-dd HH:mm}";
                        if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }

                    if (groupByDate)
                    {
                        SessionDateGroup group = SessionListPresentation.GetDateGroup(s.LastModified, now);
                        if (group != currentGroup)
                        {
                            currentGroup = group;
                            listBox.Items.Add(CreateSessionGroupHeader(group, themeFg));
                        }
                    }

                    bool timeOnly = groupByDate &&
                        (currentGroup == SessionDateGroup.Today || currentGroup == SessionDateGroup.Yesterday);
                    var lbi = new ListBoxItem
                    {
                        Content = CreateSessionItemContent(s, themeFg, timeOnly, TryFindOpenChatTab(s.SessionId, out _)),
                        Tag = s,
                        ToolTip = BuildSessionTooltip(s)
                    };
                    if (s.SessionId == previouslySelectedId) reselectIndex = listBox.Items.Count;
                    if (firstSessionIndex < 0) firstSessionIndex = listBox.Items.Count;
                    listBox.Items.Add(lbi);
                }

                if (firstSessionIndex < 0)
                {
                    loading.Visibility = Visibility.Visible;
                    loading.Text = loadedSessions.Count == 0
                        ? loading.Text  // keep the "no sessions on disk" message set by the loader
                        : L("No sessions match the current filter.");
                }
                else
                {
                    loading.Visibility = Visibility.Collapsed;
                    listBox.SelectedIndex = reselectIndex >= 0 ? reselectIndex : firstSessionIndex;
                }

                resumeLastButton.IsEnabled = loadedSessions.Count > 0;
                resumeTodayButton.IsEnabled = todaysSessionsToReopen().Count > 0;
            };

            // Loader — populates loadedSessions once parsing finishes, then renders via the filter.
            Func<Task> loadAsync = async () =>
            {
                loadedSessions.Clear();
                listBox.Items.Clear();
                loading.Visibility = Visibility.Visible;
                loading.Text = L("Loading sessions…");

                SessionHistoryListResult listResult;
                try
                {
                    listResult = await adapter.ListAsync(selected.Value, workspaceDir);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Session history load failed: {ex}");
                    loading.Visibility = Visibility.Visible;
                    loading.Text = L("Could not load {0} sessions:\n{1}", GetSessionHistoryProviderName(selected.Value), ex.Message);
                    return;
                }

                List<SessionInfo> sessions = listResult.Sessions ?? new List<SessionInfo>();
                if (sessions.Count == 0)
                {
                    loading.Visibility = Visibility.Visible;
                    loading.Text = listResult.EmptyMessage ?? L("No sessions were found for this workspace.");
                    return;
                }

                loadedSessions.AddRange(sessions);
                applyFilter();
            };

            // Persist the chosen sort order so it survives across sessions (issue #114).
            Action saveSortMode = () =>
            {
                string key = (sortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "modified";
                if (_settings != null && !string.Equals(_settings.SessionHistorySortMode, key, StringComparison.Ordinal))
                {
                    _settings.SessionHistorySortMode = key;
                    SaveSettings();
                }
                applyFilter();
            };

            // Persist the "Renamed only" toggle so it survives across sessions (issue #95).
            Action saveRenamedOnly = () =>
            {
                bool on = renamedOnlyCheck.IsChecked == true;
                if (_settings != null && _settings.SessionHistoryRenamedOnly != on)
                {
                    _settings.SessionHistoryRenamedOnly = on;
                    SaveSettings();
                }
                applyFilter();
            };

            searchBox.TextChanged += (s, args) => applyFilter();
            renamedOnlyCheck.Checked += (s, args) => saveRenamedOnly();
            renamedOnlyCheck.Unchecked += (s, args) => saveRenamedOnly();
            sortCombo.SelectionChanged += (s, args) => saveSortMode();

            // Wire up buttons.
            Func<ListBoxItem> currentSelectionItem = () => listBox.SelectedItem as ListBoxItem;
            Func<SessionInfo> currentSelection = () => currentSelectionItem()?.Tag as SessionInfo;

            // Rename the selected session (issue #95). Persisted in settings keyed by
            // session UUID so it survives restarts; an empty title clears the override.
            Action renameSelected = () =>
            {
                var sel = currentSelection();
                if (sel == null) return;

                string newTitle = ShowRenameSessionDialog(sel, dialog);
                if (newTitle == null) return; // cancelled

                newTitle = newTitle.Trim();
                if (_settings.SessionCustomTitles == null)
                {
                    _settings.SessionCustomTitles =
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                // An open chat tab's CLI may still write either old name back.
                MarkClaudeTitleSuperseded(sel.SessionId, sel.CustomTitle, newTitle);
                MarkClaudeTitleSuperseded(sel.SessionId, sel.TranscriptTitle, newTitle);

                if (string.IsNullOrEmpty(newTitle))
                {
                    _settings.SessionCustomTitles.Remove(sel.SessionId);
                    sel.CustomTitle = string.Empty;
                }
                else
                {
                    _settings.SessionCustomTitles[sel.SessionId] = newTitle;
                    sel.CustomTitle = newTitle;
                }
                SetSessionAutoTitle(sel.SessionId, false);

                SaveSettings();

                // Issue #170: also hand the name to Claude Code so its own /resume picker shows it.
                if (IsClaudeCodeSessionHistoryProvider(sel.Provider))
                {
                    AppendClaudeTitleRecords(sel.FilePath, sel.SessionId, newTitle);
                }

                applyFilter(); // re-render so the new title (and any filter) updates
            };

            // Delete the selected transcript (and any custom title it carried). Codex deletion goes
            // through App Server so its rollout file and state database cannot drift apart.
            Func<Task> deleteSelectedAsync = async () =>
            {
                var sel = currentSelection();
                if (sel == null || !adapter.CanDelete) return;

                var confirm = MessageBox.Show(
                    L("Delete this session transcript?\n\n{0}{1}\n\nThis cannot be undone.", sel.FilePath, adapter.DeleteConfirmationSuffix),
                    L("Delete Session"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                try
                {
                    await adapter.DeleteAsync(sel, workspaceDir);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    bool hadTitle = _settings.SessionCustomTitles?.Remove(sel.SessionId) == true;
                    bool hadAutoMark = _settings.SessionAutoTitleIds?.Remove(sel.SessionId) == true;
                    if (hadTitle || hadAutoMark)
                    {
                        SaveSettings();
                    }
                    loadedSessions.Remove(sel);
                    applyFilter();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L("Failed to delete: {0}", ex.Message), L("Error"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            // Open the selected transcript, rendered as a readable conversation, as a read-only
            // document in Visual Studio. The history closes first: it is modal, so the document
            // would otherwise open behind it, out of reach.
            Action viewSelected = () =>
            {
                var sel = currentSelection();
                if (sel == null || !adapter.CanViewTranscript) return;
                dialog.DialogResult = true;
                StartSessionHistoryTask(() => ViewSessionTranscriptAsync(adapter, sel, workspaceDir),
                    "claudecode/sessionhistory/view");
            };

            // One menu for the secondary actions, opened by the list-icon button and by a right-click
            // on a row (issue #95): View / Rename / Delete, then Refresh.
            var listContextMenu = new ContextMenu();
            ApplyModernMenuStyle(listContextMenu);
            var viewMenuItem = new MenuItem { Header = L("View as Text") };
            viewMenuItem.Click += (s, args) => viewSelected();
            var renameMenuItem = new MenuItem { Header = L("Rename…"), InputGestureText = "F2" };
            renameMenuItem.Click += (s, args) => renameSelected();
            var deleteMenuItem = new MenuItem { Header = L("Delete") };
            deleteMenuItem.Click += (s, args) =>
                StartSessionHistoryTask(deleteSelectedAsync, "claudecode/sessionhistory/delete");
            resumeTodayButton.Click += (s, args) =>
            {
                List<SessionInfo> toReopen = todaysSessionsToReopen();
                if (toReopen.Count == 0) return;

                // Each one starts its own agent process, so a long list is confirmed first.
                if (toReopen.Count > 5 &&
                    MessageBox.Show(dialog, L("Reopen {0} sessions? Each one starts its own agent.", toReopen.Count),
                        L("Session History"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                dialog.DialogResult = true;
                StartSessionHistoryTask(async () =>
                {
                    foreach (SessionInfo session in toReopen)
                    {
                        await ResumeSessionInNewTabAsync(session, targetTab, workspaceDir);
                    }
                }, "claudecode/sessionhistory/resumetoday");
            };

            var refreshMenuItem = new MenuItem { Header = L("Refresh List") };
            refreshMenuItem.Click += (s, args) =>
                StartSessionHistoryTask(loadAsync, "claudecode/sessionhistory/refresh");
            listContextMenu.Items.Add(viewMenuItem);
            listContextMenu.Items.Add(renameMenuItem);
            listContextMenu.Items.Add(deleteMenuItem);
            listContextMenu.Items.Add(new Separator());
            listContextMenu.Items.Add(refreshMenuItem);
            // Row actions need a selected row; capability gating (e.g. Devin has no delete verb) is
            // fixed for the dialog's lifetime, since the provider cannot change while it is open.
            listContextMenu.Opened += (s, args) =>
            {
                bool hasSel = currentSelection() != null;
                viewMenuItem.IsEnabled = hasSel && adapter.CanViewTranscript;
                renameMenuItem.IsEnabled = hasSel;
                deleteMenuItem.IsEnabled = hasSel && adapter.CanDelete;
                deleteMenuItem.ToolTip = adapter.CanDelete
                    ? null
                    : L("{0} does not support deleting sessions from here.", GetSessionHistoryProviderName(selected.Value));
            };
            ToolTipService.SetShowOnDisabled(deleteMenuItem, true);
            listBox.ContextMenu = listContextMenu;

            moreButton.Click += (s, args) =>
            {
                listContextMenu.PlacementTarget = moreButton;
                listContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                listContextMenu.IsOpen = true;
            };
            // WPF only anchors a context menu to the clicked element when no target is set, so the
            // button's anchor must go, or the next right-click on a row would open it at the button.
            listContextMenu.Closed += (s, args) =>
            {
                listContextMenu.ClearValue(ContextMenu.PlacementTargetProperty);
                listContextMenu.ClearValue(ContextMenu.PlacementProperty);
            };

            // WPF Click handlers must be void-returning, so async work is fired off via
            // JoinableTaskFactory rather than `async (s, args) =>` lambdas (which would
            // swallow exceptions and trip VSTHRD101).
            // Asks first (with this dialog as owner, so Cancel returns to the list) where the session
            // goes; the dialog only closes once there is something to do.
            Action<SessionInfo> resumeSession = sel =>
            {
                if (sel == null) return;

                HistoryResumeTarget target = ChooseHistoryResumeTarget(sel, targetTab, dialog, out NativeChatSessionState openTab);
                if (target == HistoryResumeTarget.Cancel) return;

                dialog.DialogResult = true;
                StartSessionHistoryTask(() => ResumeFromHistoryAsync(sel, targetTab, workspaceDir, target, openTab),
                    "claudecode/sessionhistory");
            };
            Action resumeSelected = () => resumeSession(currentSelection());

            resumeButton.Click += (s, args) => resumeSelected();

            // The newest conversation of the workspace, ignoring the search and "Renamed only".
            resumeLastButton.Click += (s, args) =>
                resumeSession(loadedSessions.OrderByDescending(session => session.LastModified).FirstOrDefault());

            listBox.MouseDoubleClick += (s, args) => resumeSelected();

            // F2 renames the selected session (issue #95) — the familiar shell rename key.
            listBox.PreviewKeyDown += (s, args) =>
            {
                if (args.Key == Key.F2)
                {
                    args.Handled = true;
                    renameSelected();
                }
            };

            closeButton.Click += (s, args) => { dialog.DialogResult = false; };

            dialog.Loaded += (s, args) =>
            {
                StartSessionHistoryTask(loadAsync, "claudecode/sessionhistory/load");
            };
            dialog.ShowDialog();
        }

        /// <summary>
        /// Shows a small themed prompt for renaming a session (issue #95). Returns the new
        /// title (which may be empty to clear the override) or null if the user cancelled.
        /// </summary>
        private string ShowRenameSessionDialog(SessionInfo session, Window owner)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = L("Rename Session"),
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Owner = owner
            };
            ThemedDialog.PrepareResources(dialog);

            var stack = new StackPanel { Margin = new Thickness(16, 6, 16, 16) };

            stack.Children.Add(new TextBlock
            {
                Text = L("Custom title (leave empty to restore the auto-generated preview):"),
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var titleBox = new TextBox
            {
                Text = session.CustomTitle ?? string.Empty,
                Style = ThemedDialog.GetStyle(dialog, "ThemedDialogTextBoxStyle"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            stack.Children.Add(titleBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button
            {
                Content = L("OK"),
                Style = ThemedDialog.GetStyle(dialog, "ThemedDialogPrimaryButtonStyle"),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = L("Cancel"),
                Style = ThemedDialog.GetStyle(dialog, "ThemedDialogButtonStyle"),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stack.Children.Add(buttonPanel);

            okButton.Click += (s, args) => { dialog.DialogResult = true; };

            dialog.Content = stack;
            ThemedDialog.ApplyChrome(dialog);
            dialog.Loaded += (s, args) => { titleBox.SelectAll(); titleBox.Focus(); };

            bool? ok = dialog.ShowDialog();
            return ok == true ? (titleBox.Text ?? string.Empty) : null;
        }

        #endregion

        #region Transcript Viewer

        /// <summary>
        /// Renders the selected session's JSONL transcript as a readable, plain-text
        /// conversation, writes it to a temp file named after the session, and opens it as a
        /// read-only document in Visual Studio (the default text editor when DTE is unavailable).
        /// The original transcript is never modified.
        /// </summary>
        private async Task ViewSessionTranscriptAsync(ISessionHistoryProvider adapter, SessionInfo session, string workspaceDir)
        {
            if (session == null || adapter == null) return;

            try
            {
                string transcript = await adapter.ReadTranscriptAsync(session, workspaceDir);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string tempDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeExtension", "SessionView");
                Directory.CreateDirectory(tempDir);

                // The file name is the document tab's caption: the session's title, plus the start of
                // its id so two sessions with the same title never share a file.
                string outPath = Path.Combine(tempDir, BuildTranscriptFileName(session));
                if (File.Exists(outPath))
                {
                    File.SetAttributes(outPath, FileAttributes.Normal);
                }
                File.WriteAllText(outPath, transcript, new UTF8Encoding(false));
                // Read-only on disk, so the editor opens it locked and nobody mistakes it for the session.
                File.SetAttributes(outPath, FileAttributes.ReadOnly);

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null)
                {
                    dte.ItemOperations.OpenFile(outPath);
                }
                else
                {
                    Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ViewSessionTranscript error: {ex.Message}");
                MessageBox.Show(L("Could not open the session transcript:\n{0}", ex.Message),
                    L("View Session"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>"&lt;title&gt; (1a2b3c4d).txt", with characters Windows rejects in file names replaced.</summary>
        internal static string BuildTranscriptFileName(SessionInfo session)
        {
            string title = (session?.DisplayTitle ?? string.Empty).Trim();
            var name = new StringBuilder(title.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in title)
            {
                name.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
            }

            string clean = name.ToString().Trim().TrimEnd('.');
            if (clean.Length > 60) clean = clean.Substring(0, 60).TrimEnd();
            if (clean.Length == 0) clean = "session";

            string id = session?.SessionId ?? string.Empty;
            string shortId = id.Length > 8 ? id.Substring(0, 8) : id;
            return shortId.Length > 0 ? $"{clean} ({shortId}).txt" : clean + ".txt";
        }

        /// <summary>Formats the provider-neutral messages returned by Codex App Server.</summary>
        private static string BuildReadableCodexTranscript(
            SessionInfo session,
            CodexThreadTranscript transcript)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Title:     {NormalizeTranscriptTitle(session.DisplayTitle)}");
            sb.AppendLine($"Session:   {session.SessionId}");
            sb.AppendLine($"Directory: {session.Cwd}");
            sb.AppendLine($"Modified:  {session.LastModified:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Messages:  {transcript?.Messages?.Count(message => message != null && !message.IsTool) ?? 0}");
            sb.AppendLine($"File:      {session.FilePath}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            if (transcript?.Messages == null) return sb.ToString();

            foreach (CodexTranscriptMessage message in transcript.Messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Text)) continue;

                string stamp = message.Timestamp == DateTime.MinValue
                    ? string.Empty
                    : $" [{message.Timestamp:yyyy-MM-dd HH:mm}]";
                string speaker = message.IsUser ? "USER" : message.IsTool ? "TOOL" : "ASSISTANT";
                sb.AppendLine($"{speaker}{stamp}:");
                sb.AppendLine(message.Text.TrimEnd());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Squeezes a session name into the single line the exported header expects. Preview text
        /// is already flattened and capped, but a custom title comes straight from the rename
        /// dialog and is only trimmed, so a pasted multi-line value would break the header shape.
        /// Blank input yields a placeholder so the field is never left empty.
        /// </summary>
        internal static string NormalizeTranscriptTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return L("(untitled session)");

            var sb = new StringBuilder(title.Length);
            bool pendingSpace = false;
            foreach (char c in title)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(c);
            }

            string flat = sb.ToString();

            // Same 120-char maximum ParseSessionFile applies to the preview, so both sources of a
            // title share one cap and the result stays well inside GitHub's issue-title limit.
            return flat.Length > 120 ? flat.Substring(0, 120) + "…" : flat;
        }

        /// <summary>
        /// Walks the JSONL transcript and produces a readable conversation: a short header
        /// followed by each user/assistant turn in order. Assistant tool calls are shown as
        /// compact <c>[tool: Name]</c> markers; tool-result re-injections and system/snapshot
        /// lines are skipped so the output reads like the actual dialog rather than raw JSON.
        /// </summary>
        private static string BuildReadableTranscript(SessionInfo session)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Title:     {NormalizeTranscriptTitle(session.DisplayTitle)}");
            sb.AppendLine($"Session:   {session.SessionId}");
            sb.AppendLine($"Directory: {session.Cwd}");
            sb.AppendLine($"Modified:  {session.LastModified:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Messages:  {session.MessageCount}   Tokens: {session.TokenCount}");
            sb.AppendLine($"File:      {session.FilePath}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            using (var fs = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    string type = (string)obj["type"];
                    if (type != "user" && type != "assistant") continue;

                    // CLI-injected line, not something either side said — the largest one seen in the
                    // wild is 863 KB of skill prompt, which would drown the exported transcript.
                    if (IsMetaTranscriptLine(obj)) continue;

                    string stamp = FormatTranscriptTimestamp((string)obj["timestamp"]);
                    var msg = obj["message"];
                    if (msg == null) continue;

                    if (type == "user")
                    {
                        string text = ExtractUserText(msg["content"]);
                        if (string.IsNullOrWhiteSpace(text)) continue; // tool_result re-injection

                        sb.AppendLine($"USER{stamp}:");
                        sb.AppendLine(text.TrimEnd());
                        sb.AppendLine();
                    }
                    else
                    {
                        string text = ExtractAssistantText(msg["content"]);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        sb.AppendLine($"ASSISTANT{stamp}:");
                        sb.AppendLine(text.TrimEnd());
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Collapses an assistant message's content (string or typed-parts array) to readable
        /// text, turning <c>tool_use</c> parts into a one-line <c>[tool: Name]</c> marker so the
        /// flow of actions stays visible without dumping raw tool arguments.
        /// </summary>
        private static string ExtractAssistantText(JToken content)
        {
            if (content == null) return string.Empty;

            if (content.Type == JTokenType.String)
            {
                return ((string)content) ?? string.Empty;
            }

            if (content.Type == JTokenType.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.Children())
                {
                    if ((string)part["type"] == "tool_use")
                    {
                        string toolName = (string)part["name"];
                        sb.AppendLine($"[tool: {(string.IsNullOrEmpty(toolName) ? "?" : toolName)}]");
                    }
                    else
                    {
                        AppendAssistantTextPart(sb, part);
                    }
                }
                return sb.ToString();
            }

            return string.Empty;
        }

        /// <summary>
        /// Appends an assistant <c>text</c> or <c>thinking</c> part as readable text; any other part
        /// type is ignored. Shared by the exported transcript and the chat's resume replay.
        /// </summary>
        private static void AppendAssistantTextPart(StringBuilder sb, JToken part)
        {
            string partType = (string)part["type"];
            if (partType == "text")
            {
                string text = (string)part["text"];
                if (!string.IsNullOrEmpty(text)) sb.AppendLine(text);
            }
            else if (partType == "thinking")
            {
                string thinking = (string)part["thinking"];
                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    sb.AppendLine("[thinking]");
                    sb.AppendLine(thinking);
                }
            }
        }

        /// <summary>
        /// Formats a transcript ISO timestamp as <c> [yyyy-MM-dd HH:mm]</c> in local time.
        /// Returns an empty string when the stamp is missing or unparseable so the caller
        /// still gets a clean "USER:" / "ASSISTANT:" header.
        /// </summary>
        private static string FormatTranscriptTimestamp(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return string.Empty;

            if (DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
            {
                return $" [{dto.LocalDateTime:yyyy-MM-dd HH:mm}]";
            }

            return string.Empty;
        }

        #endregion

        #region Resume Integration

        /// <summary>Where a conversation picked in Session History is resumed.</summary>
        private enum HistoryResumeTarget
        {
            Cancel,
            SwitchToOpenTab, // a chat tab already shows it: bring that tab forward, never resume it twice
            CurrentTab,
            NewTab
        }

        /// <summary>
        /// Decides where <paramref name="session"/> goes. A conversation already open in a chat tab is
        /// only brought forward — resuming it again would leave two CLIs writing one transcript. When
        /// the tab the history was opened from already holds a conversation, the user picks between a
        /// new tab and that tab; an empty tab with an unsent draft gets a new tab without asking.
        /// Terminal mode has no chat tabs and always resumes in place.
        /// </summary>
        private HistoryResumeTarget ChooseHistoryResumeTarget(SessionInfo session, NativeChatSessionState targetTab,
            Window owner, out NativeChatSessionState openTab)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            openTab = null;

            if (!IsNativeModeActive)
            {
                return HistoryResumeTarget.CurrentTab;
            }

            if (TryFindOpenChatTab(session.SessionId, out openTab))
            {
                return HistoryResumeTarget.SwitchToOpenTab;
            }

            if (!ChatTabHasConversation(targetTab))
            {
                // A draft in the composer is usually a new topic: keep it and open the conversation beside it.
                return ChatTabHasDraft(targetTab) ? HistoryResumeTarget.NewTab : HistoryResumeTarget.CurrentTab;
            }

            bool? newTab = AskResumeInNewTab(session, owner);
            if (newTab == null) return HistoryResumeTarget.Cancel;
            return newTab.Value ? HistoryResumeTarget.NewTab : HistoryResumeTarget.CurrentTab;
        }

        /// <summary>True when the chat tab (null = the main chat) shows at least one prompt the user sent.</summary>
        private bool ChatTabHasConversation(NativeChatSessionState tab)
        {
            ChatTranscriptView transcript = tab != null ? tab.ChatTranscript : ChatTranscript;
            return transcript != null && transcript.Messages.Any(m => m.IsUser);
        }

        /// <summary>True when the chat tab (null = the main chat) has unsent text or attachments in its composer.</summary>
        private bool ChatTabHasDraft(NativeChatSessionState tab)
        {
            ChatTranscriptView transcript = tab != null ? tab.ChatTranscript : ChatTranscript;
            if (transcript != null && !string.IsNullOrWhiteSpace(transcript.ComposerText))
            {
                return true;
            }

            return tab != null ? tab.AttachedFiles.Count > 0 : attachedImagePaths.Count > 0;
        }

        private async Task ResumeFromHistoryAsync(SessionInfo session, NativeChatSessionState targetTab, string workspaceDir,
            HistoryResumeTarget target, NativeChatSessionState openTab)
        {
            switch (target)
            {
                case HistoryResumeTarget.SwitchToOpenTab:
                    await SwitchToOpenChatTabAsync(openTab);
                    break;
                case HistoryResumeTarget.NewTab:
                    await ResumeSessionInNewTabAsync(session, targetTab, workspaceDir);
                    break;
                case HistoryResumeTarget.CurrentTab:
                    await ResumeSessionAsync(session, targetTab);
                    break;
            }
        }

        /// <summary>
        /// Opens <paramref name="session"/> in a new chat tab, leaving the current conversation alone.
        /// The new tab copies the model/effort/permission choices of the tab it was asked from when
        /// that tab runs the same agent; otherwise it starts from the settings' defaults. A GitHub
        /// issue tab's worktree carries over, since the CLI only resumes from the same directory.
        /// </summary>
        private async Task ResumeSessionInNewTabAsync(SessionInfo session, NativeChatSessionState fromTab, string workspaceDir)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            AiProvider fromProvider = fromTab?.SelectedProvider ?? _currentRunningProvider ?? _settings.SelectedProvider;
            NativeChatSessionState seed = fromProvider == session.Provider ? fromTab : null;

            try
            {
                NativeChatSessionState tab = await OpenResumedChatTabAsync(session.Provider, workspaceDir, session.SessionId,
                    focus: true, configure: state =>
                    {
                        if (seed != null)
                        {
                            state.SelectedModel = seed.SelectedModel;
                            state.SelectedClaudeModel = seed.SelectedClaudeModel;
                            state.SelectedEffortLevel = seed.SelectedEffortLevel;
                            state.SelectedCodexReasoningLevel = seed.SelectedCodexReasoningLevel;
                            state.SkipPermissions = seed.SkipPermissions;
                            state.AutoPermissions = seed.AutoPermissions;
                            state.ManualMode = seed.ManualMode;
                        }
                        state.WorkspaceOverride = fromTab?.WorkspaceOverride;
                    });

                if (tab == null)
                {
                    MessageBox.Show(L("Failed to create new session."), L("Session History"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RecordOpenChatTabs();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Resume in new tab failed: {ex}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show(L("Failed to create new session: {0}", ex.Message), L("Session History"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Asks whether a conversation picked in Session History opens in a new tab (true) or replaces
        /// the conversation in the current one (false); null when cancelled. "New tab" is the default
        /// button: it is the choice that loses nothing.
        /// </summary>
        private bool? AskResumeInNewTab(SessionInfo session, Window owner)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = L("Resume Conversation"),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = owner; } catch { }
            ThemedDialog.PrepareResources(dialog);

            var stack = new StackPanel { Margin = new Thickness(16, 6, 16, 16) };
            stack.Children.Add(new TextBlock
            {
                Text = L("This chat tab already has a conversation. Where do you want to resume \"{0}\"?",
                    session.DisplayTitle),
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            stack.Children.Add(new TextBlock
            {
                Text = L("Resuming in this tab closes its current conversation; you can reopen it later from Session History."),
                Foreground = themeFg,
                Opacity = 0.65,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Style buttonStyle = ThemedDialog.GetStyle(dialog, "ThemedDialogButtonStyle");
            Func<string, Button> mkBtn = text => new Button { Content = text, Style = buttonStyle, Margin = new Thickness(8, 0, 0, 0) };

            bool? choice = null;
            var newTabButton = mkBtn(L("New Tab"));
            newTabButton.Style = ThemedDialog.GetStyle(dialog, "ThemedDialogPrimaryButtonStyle");
            newTabButton.Margin = new Thickness(0);
            newTabButton.IsDefault = true;
            newTabButton.Click += (s, e) => { choice = true; dialog.DialogResult = true; };
            var thisTabButton = mkBtn(L("This Tab"));
            thisTabButton.Click += (s, e) => { choice = false; dialog.DialogResult = true; };
            var cancelButton = mkBtn(L("Cancel"));
            cancelButton.IsCancel = true;

            buttons.Children.Add(newTabButton);
            buttons.Children.Add(thisTabButton);
            buttons.Children.Add(cancelButton);
            stack.Children.Add(buttons);

            dialog.Content = stack;
            ThemedDialog.ApplyChrome(dialog);
            dialog.Loaded += (s, e) => newTabButton.Focus();
            dialog.ShowDialog();
            return choice;
        }

        /// <summary>
        /// Stops the current agent (if any), arms the one-shot resume token consumed by the selected
        /// provider's terminal/native command builder, and restarts with the matching environment.
        /// Switching native↔WSL is intentional because the two environments own separate stores.
        /// With <paramref name="targetTab"/> (the parallel chat tab the dialog was opened from) only
        /// that tab is relaunched into the conversation; the panel's session is left alone.
        /// </summary>
        private async Task ResumeSessionAsync(SessionInfo session, NativeChatSessionState targetTab = null)
        {
            if (session == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // The tab may have been closed while the dialog was open; the default session takes it then.
            if (targetTab != null && GetSession(targetTab.SessionId) == targetTab)
            {
                await ResumeSessionInTabAsync(session, targetTab);
                return;
            }

            _settings.SelectedProvider = session.Provider;
            SaveSettings();

            _pendingResumeSessionId = session.SessionId;

            // A plan approved in the conversation being left does not carry over to this one (#181).
            _nativePlanExitChoice = null;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            UpdateProviderSelection();

            await RestartTerminalWithSelectedProviderAsync();
        }

        /// <summary>
        /// Resumes a Session History conversation in a parallel chat tab: switches the tab to the
        /// session's provider if needed (per-tab, never written to <c>Settings</c>) and relaunches only
        /// that tab with the session's id. The shared <c>_pendingResumeSessionId</c> is not touched —
        /// it belongs to the default session's next launch.
        /// </summary>
        private async Task ResumeSessionInTabAsync(SessionInfo session, NativeChatSessionState targetTab)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (targetTab.SelectedProvider != session.Provider)
            {
                ApplyProviderToSession(targetTab, session.Provider);
            }

            // A plan approved in the conversation being left does not carry over to this one (#181).
            targetTab.PlanExitChoice = null;
            UpdateChatComposerState(targetTab);
            BringSessionTabToFront(targetTab);

            await RelaunchSessionAsync(targetTab,
                L("🕘 Resumed the {0} session.", GetSessionHistoryProviderName(session.Provider)),
                resumeSessionId: session.SessionId);
        }

        #endregion
    }
}
