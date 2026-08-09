/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Reads Devin's session history through the CLI's own list command and its ACP server,
 *          rather than its private SQLite store — both are surfaces Devin documents and supports
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>Launch settings for reading Devin's session history.</summary>
    internal sealed class DevinSessionHistoryOptions
    {
        public string ExecutablePath { get; set; } = "devin";

        public bool UseWsl { get; set; }

        public string WorkingDirectory { get; set; } = string.Empty;

        public string WslWorkingDirectory { get; set; } = string.Empty;

        public IDictionary<string, string> EnvironmentOverrides { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One row from <c>devin list --format json</c>.</summary>
    internal sealed class DevinSessionSummary
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        public DateTime LastActivity { get; set; }
    }

    /// <summary>One reconstructed message from a Devin session, replayed over ACP <c>session/load</c>.</summary>
    internal sealed class DevinTranscriptMessage
    {
        public bool IsUser { get; set; }

        public bool IsThought { get; set; }

        public bool IsTool { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>A Devin session's replayed messages.</summary>
    internal sealed class DevinThreadTranscript
    {
        public IList<DevinTranscriptMessage> Messages { get; } = new List<DevinTranscriptMessage>();
    }

    /// <summary>
    /// Talks to the <c>devin</c> CLI for session history: <c>list --format json</c> for the index and
    /// <c>devin acp</c> + <c>session/load</c> for a single transcript. Each call spawns and tears down
    /// its own child process — like <see cref="CodexAppServerClient"/>, this is a short-lived reader,
    /// not a persistent session.
    /// </summary>
    internal static class DevinSessionHistoryClient
    {
        private const int ListTimeoutMs = 15000;
        private const int LoadTimeoutMs = 20000;

        /// <summary>Runs <c>devin list --format json</c> scoped to the working directory.</summary>
        public static async Task<IList<DevinSessionSummary>> ListSessionsAsync(
            DevinSessionHistoryOptions options,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            string stdout = await RunAndCaptureAsync(options, "list --format json", ListTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            return ParseListResult(stdout);
        }

        /// <summary>Parses the JSON array <c>devin list --format json</c> writes to stdout.</summary>
        internal static IList<DevinSessionSummary> ParseListResult(string stdout)
        {
            var result = new List<DevinSessionSummary>();
            if (string.IsNullOrWhiteSpace(stdout)) return result;

            JArray array;
            try
            {
                // Devin CLI output is a bare JSON array; be defensive if a future version wraps it.
                JToken parsed = JToken.Parse(stdout.Trim());
                array = parsed as JArray ?? parsed["sessions"] as JArray;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DevinSessionHistoryClient: list parse failed: {ex.Message}");
                return result;
            }

            if (array == null) return result;

            foreach (JToken token in array)
            {
                JObject session = token as JObject;
                if (session == null) continue;

                string id = session["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                result.Add(new DevinSessionSummary
                {
                    Id = id,
                    Title = session["title"]?.ToString() ?? string.Empty,
                    WorkingDirectory = session["working_directory"]?.ToString() ?? string.Empty,
                    LastActivity = ParseUnixSeconds(session["last_activity_at"])
                });
            }

            return result;
        }

        private static DateTime ParseUnixSeconds(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return DateTime.MinValue;

            double seconds;
            if (!double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                return DateTime.MinValue;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds).LocalDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Replays a stored session over ACP: <c>initialize</c> then <c>session/load</c>. Devin streams
        /// the entire history as ordinary <c>session/update</c> notifications before the load response
        /// arrives — the same shape a live conversation uses — so the same message kinds handled by
        /// <c>AcpSession</c> are collected here into a flat transcript.
        /// </summary>
        public static async Task<DevinThreadTranscript> ReadTranscriptAsync(
            DevinSessionHistoryOptions options,
            string sessionId,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("A Devin session id is required.", nameof(sessionId));
            }

            var acpOptions = new AcpSessionOptions
            {
                ExecutablePath = options.ExecutablePath,
                AcpArgument = "acp",
                UseWsl = options.UseWsl,
                WslWorkingDirectory = options.WslWorkingDirectory,
                DisplayName = "Devin"
            };

            var processOptions = new JsonLineProcessOptions
            {
                FileName = AcpCommandBuilder.GetFileName(acpOptions),
                Arguments = AcpCommandBuilder.GetArguments(acpOptions),
                WorkingDirectory = options.WorkingDirectory ?? string.Empty
            };
            foreach (KeyValuePair<string, string> pair in options.EnvironmentOverrides)
            {
                processOptions.EnvironmentOverrides[pair.Key] = pair.Value;
            }

            var transcript = new DevinThreadTranscript();
            var pending = new Dictionary<long, TaskCompletionSource<JToken>>();
            var stderrTail = new Queue<string>();
            long nextId = 0;

            using (var host = new JsonLineProcessHost(processOptions))
            {
                Func<string, JObject, CancellationToken, Task<JToken>> sendRequest = async (method, parameters, ct) =>
                {
                    long id = Interlocked.Increment(ref nextId);
                    var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (pending) { pending[id] = completion; }

                    try
                    {
                        var request = new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["method"] = method,
                            ["params"] = parameters ?? new JObject()
                        };
                        await host.WriteLineAsync(request.ToString(Newtonsoft.Json.Formatting.None), ct)
                            .ConfigureAwait(false);

                        Task timeout = Task.Delay(LoadTimeoutMs, ct);
                        Task completed = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
                        if (!ReferenceEquals(completed, completion.Task))
                        {
                            ct.ThrowIfCancellationRequested();
                            string tail;
                            lock (stderrTail) { tail = string.Join("\n", stderrTail); }
                            throw new TimeoutException(
                                $"Devin did not answer '{method}' within {LoadTimeoutMs / 1000} seconds." +
                                (string.IsNullOrEmpty(tail) ? string.Empty : "\n" + tail));
                        }

                        return await completion.Task.ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (pending) { pending.Remove(id); }
                    }
                };

                host.ErrorLineReceived += (sender, line) =>
                {
                    lock (stderrTail)
                    {
                        stderrTail.Enqueue(line);
                        while (stderrTail.Count > 20) stderrTail.Dequeue();
                    }
                };

                host.LineReceived += (sender, line) =>
                {
                    JObject message;
                    try
                    {
                        message = JObject.Parse(line);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DevinSessionHistoryClient: bad line: {ex.Message}");
                        return;
                    }

                    string method = message["method"]?.ToString();
                    if (string.Equals(method, "session/update", StringComparison.Ordinal))
                    {
                        AppendUpdate(transcript, message["params"]?["update"]);
                        return;
                    }

                    if (method != null) return; // other notification kinds carry nothing for the transcript

                    JToken idToken = message["id"];
                    long id;
                    if (idToken == null || !long.TryParse(idToken.ToString(), out id)) return;

                    TaskCompletionSource<JToken> completion;
                    lock (pending) { pending.TryGetValue(id, out completion); }
                    if (completion == null) return;

                    JToken error = message["error"];
                    if (error != null && error.Type != JTokenType.Null)
                    {
                        string errorMessage = error["message"]?.ToString() ?? error.ToString(Newtonsoft.Json.Formatting.None);
                        completion.TrySetException(new InvalidOperationException(errorMessage));
                    }
                    else
                    {
                        completion.TrySetResult(message["result"]);
                    }
                };

                await host.StartAsync(cancellationToken).ConfigureAwait(false);

                await sendRequest("initialize", new JObject
                {
                    ["protocolVersion"] = 1,
                    ["clientCapabilities"] = new JObject
                    {
                        ["fs"] = new JObject { ["readTextFile"] = false, ["writeTextFile"] = false }
                    }
                }, cancellationToken).ConfigureAwait(false);

                string cwd = options.UseWsl ? options.WslWorkingDirectory : options.WorkingDirectory;
                await sendRequest("session/load", new JObject
                {
                    ["sessionId"] = sessionId,
                    ["cwd"] = cwd ?? string.Empty,
                    ["mcpServers"] = new JArray()
                }, cancellationToken).ConfigureAwait(false);
            }

            return transcript;
        }

        /// <summary>Internal for direct unit-test coverage of the session/update -> transcript mapping.</summary>
        internal static void AppendUpdate(DevinThreadTranscript transcript, JToken update)
        {
            string kind = update?["sessionUpdate"]?.ToString();
            if (string.IsNullOrEmpty(kind)) return;

            switch (kind)
            {
                case "user_message_chunk":
                    AppendChunk(transcript, update["content"], isUser: true, isThought: false);
                    break;

                case "agent_message_chunk":
                    AppendChunk(transcript, update["content"], isUser: false, isThought: false);
                    break;

                case "agent_thought_chunk":
                    AppendChunk(transcript, update["content"], isUser: false, isThought: true);
                    break;

                default:
                    // tool_call / tool_call_update / mode / usage chatter: not part of the readable
                    // conversation the transcript viewer shows.
                    break;
            }
        }

        private static void AppendChunk(DevinThreadTranscript transcript, JToken content, bool isUser, bool isThought)
        {
            string text = ReadContentText(content);
            if (string.IsNullOrEmpty(text)) return;

            // Consecutive chunks belong to the same message (they stream token by token); merge them
            // with the previous entry when the speaker matches so the transcript reads as sentences.
            if (transcript.Messages.Count > 0)
            {
                DevinTranscriptMessage last = transcript.Messages[transcript.Messages.Count - 1];
                if (last.IsUser == isUser && last.IsThought == isThought)
                {
                    last.Text += text;
                    return;
                }
            }

            transcript.Messages.Add(new DevinTranscriptMessage { IsUser = isUser, IsThought = isThought, Text = text });
        }

        private static string ReadContentText(JToken content)
        {
            if (content == null) return string.Empty;
            if (content.Type == JTokenType.String) return content.ToString();
            return content["text"]?.ToString() ?? string.Empty;
        }

        /// <summary>Runs a plain <c>devin</c> subcommand (no protocol) and returns its captured stdout.</summary>
        private static async Task<string> RunAndCaptureAsync(
            DevinSessionHistoryOptions options,
            string subcommand,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = options.UseWsl ? string.Empty : (options.WorkingDirectory ?? string.Empty)
            };

            if (options.UseWsl)
            {
                var inner = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(options.WslWorkingDirectory))
                {
                    inner.Append("cd ").Append(QuoteForBash(options.WslWorkingDirectory)).Append(" && ");
                }
                inner.Append(options.ExecutablePath).Append(' ').Append(subcommand);

                psi.FileName = "wsl.exe";
                // -i, not -l: a login shell's motd would land ahead of the JSON on stdout.
                psi.Arguments = "bash -ic " + QuoteForWindowsArgument(inner.ToString());
            }
            else
            {
                psi.FileName = options.ExecutablePath;
                psi.Arguments = subcommand;
            }

            foreach (KeyValuePair<string, string> pair in options.EnvironmentOverrides)
            {
                psi.EnvironmentVariables[pair.Key] = pair.Value;
            }

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                bool exited = await Task.Run(() => process.WaitForExit(timeoutMs), cancellationToken)
                    .ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    throw new TimeoutException($"'devin {subcommand}' did not finish within {timeoutMs / 1000} seconds.");
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                        ? $"'devin {subcommand}' exited with code {process.ExitCode}."
                        : stderr.Trim());
                }

                return stdout;
            }
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
