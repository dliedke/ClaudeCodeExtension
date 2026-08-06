/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Small Codex App Server client used to list, read and delete persisted Codex threads
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>Launch settings for the Codex App Server history client.</summary>
    internal sealed class CodexAppServerOptions
    {
        public string ExecutablePath { get; set; } = "codex";

        public bool UseWsl { get; set; }

        public string WorkingDirectory { get; set; } = string.Empty;

        public string WslWorkingDirectory { get; set; } = string.Empty;

        public string ClientVersion { get; set; } = "1.0";

        public IDictionary<string, string> EnvironmentOverrides { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Summary returned by <c>thread/list</c>.</summary>
    internal sealed class CodexThreadSummary
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Preview { get; set; } = string.Empty;

        public string Cwd { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public DateTime LastModified { get; set; }
    }

    /// <summary>A user-visible message reconstructed from a stored Codex thread.</summary>
    internal sealed class CodexTranscriptMessage
    {
        public bool IsUser { get; set; }

        public bool IsTool { get; set; }

        public string Text { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }
    }

    /// <summary>Full stored thread returned by <c>thread/read</c>.</summary>
    internal sealed class CodexThreadTranscript
    {
        public CodexThreadSummary Summary { get; set; } = new CodexThreadSummary();

        public IList<CodexTranscriptMessage> Messages { get; }
            = new List<CodexTranscriptMessage>();
    }

    /// <summary>
    /// Talks to <c>codex app-server --stdio</c> over its documented JSONL protocol. One instance is
    /// intentionally short-lived: the history window creates it for one list/read/delete operation and
    /// closes it immediately, so no background Codex process remains after the window is dismissed.
    /// </summary>
    internal sealed class CodexAppServerClient : IDisposable
    {
        private const int RequestTimeoutMs = 20000;
        private const int ThreadPageSize = 100;
        private const int MaximumThreadPages = 100;

        private readonly CodexAppServerOptions _options;
        private readonly object _responseLock = new object();
        private readonly Dictionary<long, TaskCompletionSource<JObject>> _pendingResponses
            = new Dictionary<long, TaskCompletionSource<JObject>>();
        private readonly Queue<string> _stderrTail = new Queue<string>();

        private JsonLineProcessHost _host;
        private long _nextRequestId;
        private bool _started;
        private bool _disposed;

        public CodexAppServerClient(CodexAppServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>Starts App Server and completes its required initialize/initialized handshake.</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient));
            if (_started) return;

            JsonLineProcessOptions processOptions = BuildProcessOptions(_options);
            var host = new JsonLineProcessHost(processOptions);
            _host = host;

            host.LineReceived += OnLineReceived;
            host.ErrorLineReceived += OnErrorLineReceived;
            host.Exited += OnExited;

            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);

                var initializeParams = new JObject
                {
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = "claudecode_vs",
                        ["title"] = "Claude Code Extension for Visual Studio",
                        ["version"] = string.IsNullOrWhiteSpace(_options.ClientVersion)
                            ? "1.0"
                            : _options.ClientVersion
                    }
                };

                await SendRequestAsync("initialize", initializeParams, cancellationToken).ConfigureAwait(false);
                await host.WriteLineAsync(new JObject
                {
                    ["method"] = "initialized",
                    ["params"] = new JObject()
                }.ToString(Newtonsoft.Json.Formatting.None), cancellationToken).ConfigureAwait(false);

                _started = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Pages through all active stored threads for one exact working directory.</summary>
        public async Task<IList<CodexThreadSummary>> ListThreadsAsync(
            string cwd,
            CancellationToken cancellationToken)
        {
            EnsureStarted();

            var threads = new List<CodexThreadSummary>();
            string cursor = null;

            for (int page = 0; page < MaximumThreadPages; page++)
            {
                var parameters = new JObject
                {
                    ["limit"] = ThreadPageSize,
                    ["archived"] = false
                };
                if (!string.IsNullOrWhiteSpace(cwd)) parameters["cwd"] = cwd;
                if (!string.IsNullOrWhiteSpace(cursor)) parameters["cursor"] = cursor;

                JObject result = await SendRequestAsync("thread/list", parameters, cancellationToken)
                    .ConfigureAwait(false);
                threads.AddRange(ParseThreadListResult(result));

                string nextCursor = result["nextCursor"]?.ToString();
                if (string.IsNullOrWhiteSpace(nextCursor) ||
                    string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                {
                    break;
                }

                cursor = nextCursor;
            }

            return threads;
        }

        /// <summary>Reads a stored thread and includes its complete turn history.</summary>
        public async Task<CodexThreadTranscript> ReadThreadAsync(
            string threadId,
            CancellationToken cancellationToken)
        {
            EnsureStarted();
            if (string.IsNullOrWhiteSpace(threadId))
            {
                throw new ArgumentException("A Codex thread id is required.", nameof(threadId));
            }

            JObject result = await SendRequestAsync("thread/read", new JObject
            {
                ["threadId"] = threadId,
                ["includeTurns"] = true
            }, cancellationToken).ConfigureAwait(false);

            CodexThreadTranscript transcript = ParseThreadReadResult(result);

            // Some stored native threads (notably interrupted/rolled-back ones) are still returned by
            // thread/list with a valid preview and rollout path, but thread/read(includeTurns: true)
            // returns an empty turns array. Codex can resume those threads and their JSONL still holds
            // the visible conversation, so do not make native-mode restoration look like a new chat.
            // WSL paths belong to Linux and are deliberately left to App Server rather than guessed here.
            string rolloutPath = transcript.Summary?.Path;
            if (!_options.UseWsl && transcript.Messages.Count == 0 &&
                !string.IsNullOrWhiteSpace(rolloutPath) && File.Exists(rolloutPath))
            {
                try
                {
                    foreach (CodexTranscriptMessage message in ParseRolloutLines(File.ReadLines(rolloutPath)))
                    {
                        transcript.Messages.Add(message);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Codex App Server: rollout fallback failed for {threadId}: {ex.Message}");
                }
            }

            return transcript;
        }

        /// <summary>Deletes a stored thread through Codex so its rollout and metadata stay consistent.</summary>
        public async Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken)
        {
            EnsureStarted();
            if (string.IsNullOrWhiteSpace(threadId))
            {
                throw new ArgumentException("A Codex thread id is required.", nameof(threadId));
            }

            await SendRequestAsync("thread/delete", new JObject
            {
                ["threadId"] = threadId
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Parses the provider-neutral summaries returned by <c>thread/list</c>.</summary>
        internal static IList<CodexThreadSummary> ParseThreadListResult(JObject result)
        {
            var summaries = new List<CodexThreadSummary>();
            JArray data = result?["data"] as JArray;
            if (data == null) return summaries;

            foreach (JToken token in data)
            {
                JObject thread = token as JObject;
                if (thread == null) continue;

                string id = thread["id"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;

                summaries.Add(ParseSummary(thread));
            }

            return summaries;
        }

        /// <summary>Parses user/agent messages from the stored turns returned by <c>thread/read</c>.</summary>
        internal static CodexThreadTranscript ParseThreadReadResult(JObject result)
        {
            JObject thread = result?["thread"] as JObject;
            if (thread == null)
            {
                throw new InvalidOperationException("Codex returned no thread data.");
            }

            var transcript = new CodexThreadTranscript
            {
                Summary = ParseSummary(thread)
            };

            JArray turns = thread["turns"] as JArray;
            if (turns == null) return transcript;

            foreach (JToken turnToken in turns)
            {
                JObject turn = turnToken as JObject;
                if (turn == null) continue;

                DateTime timestamp = UnixSecondsToLocal((long?)turn["startedAt"] ?? 0);
                JArray items = turn["items"] as JArray;
                if (items == null) continue;

                foreach (JToken itemToken in items)
                {
                    JObject item = itemToken as JObject;
                    if (item == null) continue;

                    CodexTranscriptMessage message = ParseTranscriptItem(item, timestamp);
                    if (message != null && !string.IsNullOrWhiteSpace(message.Text))
                    {
                        transcript.Messages.Add(message);
                    }
                }
            }

            return transcript;
        }

        /// <summary>
        /// Reconstructs user-visible messages from a native Codex rollout when App Server returns no
        /// turns. Event messages are preferred because they exclude injected environment/instruction
        /// messages; response items are retained as compatibility for older rollout formats.
        /// </summary>
        internal static IList<CodexTranscriptMessage> ParseRolloutLines(IEnumerable<string> lines)
        {
            var eventMessages = new List<CodexTranscriptMessage>();
            var responseMessages = new List<CodexTranscriptMessage>();
            if (lines == null) return eventMessages;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject record;
                try
                {
                    record = JObject.Parse(line);
                }
                catch (Exception)
                {
                    continue;
                }

                DateTime timestamp = ParseRolloutTimestamp(record["timestamp"]?.ToString());
                JObject payload = record["payload"] as JObject;
                if (payload == null) continue;

                string recordType = record["type"]?.ToString();
                string payloadType = payload["type"]?.ToString();
                if (recordType == "event_msg" &&
                    (payloadType == "user_message" || payloadType == "agent_message"))
                {
                    string text = payload["message"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        eventMessages.Add(new CodexTranscriptMessage
                        {
                            IsUser = payloadType == "user_message",
                            Text = text,
                            Timestamp = timestamp
                        });
                    }

                    continue;
                }

                if (recordType != "response_item" || payloadType != "message") continue;

                string role = payload["role"]?.ToString();
                if (role != "user" && role != "assistant") continue;

                string responseText = ExtractRolloutResponseText(payload["content"] as JArray);
                if (string.IsNullOrWhiteSpace(responseText) || IsInjectedRolloutMessage(responseText)) continue;

                responseMessages.Add(new CodexTranscriptMessage
                {
                    IsUser = role == "user",
                    Text = responseText,
                    Timestamp = timestamp
                });
            }

            return eventMessages.Count > 0 ? eventMessages : responseMessages;
        }

        /// <summary>Builds the native or WSL process command used for App Server.</summary>
        internal static JsonLineProcessOptions BuildProcessOptions(CodexAppServerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            string executable = string.IsNullOrWhiteSpace(options.ExecutablePath)
                ? "codex"
                : options.ExecutablePath;
            var processOptions = new JsonLineProcessOptions
            {
                WorkingDirectory = options.WorkingDirectory ?? string.Empty
            };

            if (options.UseWsl)
            {
                string inner = string.Empty;
                if (!string.IsNullOrWhiteSpace(options.WslWorkingDirectory))
                {
                    inner = "cd " + QuoteForBash(options.WslWorkingDirectory) + " && ";
                }

                inner += QuoteForBash(executable) + " app-server --stdio";
                processOptions.FileName = "wsl.exe";
                processOptions.Arguments = "bash -ic " + QuoteForWindowsArgument(inner);
            }
            else if (IsBatchScript(executable))
            {
                processOptions.FileName = "cmd.exe";
                processOptions.Arguments = "/c " + QuoteForWindowsArgument(executable + " app-server --stdio");
            }
            else
            {
                processOptions.FileName = executable;
                processOptions.Arguments = "app-server --stdio";
            }

            foreach (KeyValuePair<string, string> pair in options.EnvironmentOverrides)
            {
                processOptions.EnvironmentOverrides[pair.Key] = pair.Value;
            }

            return processOptions;
        }

        private async Task<JObject> SendRequestAsync(
            string method,
            JObject parameters,
            CancellationToken cancellationToken)
        {
            JsonLineProcessHost host = _host;
            if (host == null || !host.IsRunning)
            {
                throw new InvalidOperationException("Codex App Server is not running." + DescribeStderr());
            }

            long id = Interlocked.Increment(ref _nextRequestId);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_responseLock)
            {
                _pendingResponses[id] = completion;
            }

            try
            {
                var request = new JObject
                {
                    ["method"] = method,
                    ["id"] = id,
                    ["params"] = parameters ?? new JObject()
                };

                await host.WriteLineAsync(
                    request.ToString(Newtonsoft.Json.Formatting.None),
                    cancellationToken).ConfigureAwait(false);

                Task timeout = Task.Delay(RequestTimeoutMs, cancellationToken);
                Task completed = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
                if (!ReferenceEquals(completed, completion.Task))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException($"Codex App Server did not answer '{method}' within {RequestTimeoutMs / 1000} seconds."
                        + DescribeStderr());
                }

                JObject response = await completion.Task.ConfigureAwait(false);
                JObject error = response["error"] as JObject;
                if (error != null)
                {
                    string message = error["message"]?.ToString();
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                        ? $"Codex App Server rejected '{method}'."
                        : message);
                }

                return response["result"] as JObject ?? new JObject();
            }
            finally
            {
                lock (_responseLock)
                {
                    _pendingResponses.Remove(id);
                }
            }
        }

        private void OnLineReceived(object sender, string line)
        {
            try
            {
                JObject response = JObject.Parse(line);
                if (!long.TryParse(response["id"]?.ToString(), out long id)) return;

                TaskCompletionSource<JObject> completion;
                lock (_responseLock)
                {
                    _pendingResponses.TryGetValue(id, out completion);
                }

                completion?.TrySetResult(response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Codex App Server: ignoring unreadable output: {ex.Message}");
            }
        }

        private void OnErrorLineReceived(object sender, string line)
        {
            Debug.WriteLine($"Codex App Server [stderr]: {line}");
            lock (_stderrTail)
            {
                _stderrTail.Enqueue(line ?? string.Empty);
                while (_stderrTail.Count > 8) _stderrTail.Dequeue();
            }
        }

        private void OnExited(object sender, int exitCode)
        {
            Exception failure = new InvalidOperationException(
                $"Codex App Server exited with code {exitCode}." + DescribeStderr());
            TaskCompletionSource<JObject>[] pending;

            lock (_responseLock)
            {
                pending = new TaskCompletionSource<JObject>[_pendingResponses.Count];
                _pendingResponses.Values.CopyTo(pending, 0);
            }

            foreach (TaskCompletionSource<JObject> completion in pending)
            {
                completion.TrySetException(failure);
            }
        }

        private static CodexThreadSummary ParseSummary(JObject thread)
        {
            string preview = FlattenPreview(thread?["preview"]?.ToString());
            return new CodexThreadSummary
            {
                Id = thread?["id"]?.ToString() ?? string.Empty,
                Name = thread?["name"]?.ToString() ?? string.Empty,
                Preview = string.IsNullOrWhiteSpace(preview) ? "(no user messages)" : preview,
                Cwd = thread?["cwd"]?.ToString() ?? string.Empty,
                Path = thread?["path"]?.ToString() ?? string.Empty,
                LastModified = UnixSecondsToLocal(
                    (long?)thread?["updatedAt"] ?? (long?)thread?["createdAt"] ?? 0)
            };
        }

        private static CodexTranscriptMessage ParseTranscriptItem(JObject item, DateTime timestamp)
        {
            string type = item?["type"]?.ToString();
            if (type == "userMessage")
            {
                string text = ExtractUserMessageText(item["content"] as JArray);
                return new CodexTranscriptMessage { IsUser = true, Text = text, Timestamp = timestamp };
            }

            if (type == "agentMessage")
            {
                return new CodexTranscriptMessage
                {
                    Text = item["text"]?.ToString() ?? string.Empty,
                    Timestamp = timestamp
                };
            }

            string toolLabel = GetToolLabel(type);
            return string.IsNullOrEmpty(toolLabel)
                ? null
                : new CodexTranscriptMessage
                {
                    IsTool = true,
                    Text = "[tool: " + toolLabel + "]",
                    Timestamp = timestamp
                };
        }

        private static string ExtractUserMessageText(JArray content)
        {
            if (content == null) return string.Empty;

            var text = new StringBuilder();
            foreach (JToken token in content)
            {
                JObject part = token as JObject;
                if (part == null) continue;

                string type = part["type"]?.ToString();
                if (type == "text")
                {
                    string value = part["text"]?.ToString();
                    if (!string.IsNullOrEmpty(value)) text.Append(value);
                }
                else if (type == "image" || type == "localImage")
                {
                    if (text.Length > 0 && text[text.Length - 1] != '\n') text.AppendLine();
                    text.Append("[image]");
                }
            }

            return text.ToString();
        }

        private static string ExtractRolloutResponseText(JArray content)
        {
            if (content == null) return string.Empty;

            var text = new StringBuilder();
            foreach (JToken token in content)
            {
                JObject part = token as JObject;
                if (part == null) continue;

                string type = part["type"]?.ToString();
                if (type == "input_text" || type == "output_text" || type == "text")
                {
                    string value = part["text"]?.ToString();
                    if (!string.IsNullOrEmpty(value)) text.Append(value);
                }
            }

            return text.ToString();
        }

        private static bool IsInjectedRolloutMessage(string text)
        {
            string trimmed = (text ?? string.Empty).TrimStart();
            return trimmed.StartsWith("<environment_context>", StringComparison.Ordinal) ||
                trimmed.StartsWith("<turn_aborted>", StringComparison.Ordinal) ||
                trimmed.StartsWith("<permissions instructions>", StringComparison.Ordinal) ||
                trimmed.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal);
        }

        private static DateTime ParseRolloutTimestamp(string value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp))
            {
                return timestamp.LocalDateTime;
            }

            return DateTime.MinValue;
        }

        private static string GetToolLabel(string type)
        {
            switch (type)
            {
                case "commandExecution": return "command";
                case "fileChange": return "file change";
                case "mcpToolCall": return "MCP";
                case "webSearch": return "web search";
                case "imageGeneration": return "image generation";
                case "collabAgentToolCall": return "subagent";
                default: return string.Empty;
            }
        }

        private static string FlattenPreview(string preview)
        {
            if (string.IsNullOrWhiteSpace(preview)) return string.Empty;

            string flattened = preview.Replace("\r", " ").Replace("\n", " ").Trim();
            while (flattened.Contains("  ")) flattened = flattened.Replace("  ", " ");
            return flattened.Length > 120 ? flattened.Substring(0, 120) + "…" : flattened;
        }

        private static DateTime UnixSecondsToLocal(long seconds)
        {
            if (seconds <= 0) return DateTime.MinValue;
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        private string DescribeStderr()
        {
            string[] lines;
            lock (_stderrTail)
            {
                lines = _stderrTail.ToArray();
            }

            var kept = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = (line ?? string.Empty).Trim();
                if (trimmed.Length == 0 ||
                    trimmed.StartsWith("bash: cannot set terminal process group", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("bash: no job control in this shell", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                kept.Add(trimmed);
            }

            if (kept.Count == 0) return string.Empty;
            string detail = string.Join(" ", kept);
            if (detail.Length > 800) detail = detail.Substring(detail.Length - 800);
            return " " + detail;
        }

        private void EnsureStarted()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient));
            if (!_started) throw new InvalidOperationException("Codex App Server is not initialized.");
        }

        private static bool IsBatchScript(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        }

        private static string QuoteForBash(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";
        }

        private static string QuoteForWindowsArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;

            JsonLineProcessHost host = _host;
            _host = null;
            if (host == null) return;

            host.LineReceived -= OnLineReceived;
            host.ErrorLineReceived -= OnErrorLineReceived;
            host.Exited -= OnExited;

            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Codex App Server: dispose failed: {ex.Message}");
            }
        }
    }
}
