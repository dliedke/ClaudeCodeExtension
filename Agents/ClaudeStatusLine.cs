/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Runs the user's Claude Code status line command for native mode, which has no CLI footer
 *          to show it: settings resolution, the stdin JSON, the shell, and output cleanup (issue #188)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// What Claude Code would send a status line command on stdin, rebuilt from the stream-json events.
    /// <para>
    /// Headless Claude never runs the status line, so the extension does it. Fields follow
    /// https://code.claude.com/docs/en/statusline; the ones the stream does not carry (lines changed,
    /// vim mode, PR, worktree) are left out, as the CLI itself leaves out fields it has no value for.
    /// </para>
    /// </summary>
    public class ClaudeStatusLineInput
    {
        public string Cwd { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string OutputStyle { get; set; } = string.Empty;
        public bool FastMode { get; set; }

        /// <summary>Measured: the result's <c>total_cost_usd</c> and <c>duration_api_ms</c> are already session totals.</summary>
        public double TotalCostUsd { get; set; }
        public long TotalApiDurationMs { get; set; }

        /// <summary>Per-turn <c>duration_ms</c>, summed here.</summary>
        public long TotalDurationMs { get; set; }

        /// <summary>Last request of the last turn — the conversation as it now stands.</summary>
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheCreationTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public bool HasCurrentUsage { get; set; }
        public int ContextWindowSize { get; set; }

        /// <summary>A copy taken on the reader thread, so the UI never reads a half-updated turn.</summary>
        public ClaudeStatusLineInput Clone()
        {
            return (ClaudeStatusLineInput)MemberwiseClone();
        }

        /// <summary>0-100, null until a rate_limit_event reports the window.</summary>
        public double? FiveHourUsedPercentage { get; set; }
        public long FiveHourResetsAt { get; set; }
        public double? SevenDayUsedPercentage { get; set; }
        public long SevenDayResetsAt { get; set; }

        /// <summary>
        /// The stdin document. <paramref name="effortLevel"/> comes from the chat's effort selector (the
        /// stream does not echo it) and is left out when empty, as the CLI does for models without effort.
        /// </summary>
        public string Build(string effortLevel, string userClaudeDirectory)
        {
            var root = new JObject
            {
                ["cwd"] = Cwd,
                ["session_id"] = SessionId
            };

            if (!string.IsNullOrEmpty(userClaudeDirectory) && !string.IsNullOrEmpty(SessionId) && !string.IsNullOrEmpty(Cwd))
            {
                // A WSL session's config folder is a Linux path, and so is its transcript.
                root["transcript_path"] = userClaudeDirectory.StartsWith("/", StringComparison.Ordinal)
                    ? userClaudeDirectory.TrimEnd('/') + "/projects/" + EncodeProjectPath(Cwd) + "/" + SessionId + ".jsonl"
                    : Path.Combine(userClaudeDirectory, "projects", EncodeProjectPath(Cwd), SessionId + ".jsonl");
            }

            root["model"] = new JObject
            {
                ["id"] = ModelId,
                ["display_name"] = ClaudeStatusLine.ModelDisplayName(ModelId)
            };
            root["workspace"] = new JObject
            {
                ["current_dir"] = Cwd,
                ["project_dir"] = Cwd,
                ["added_dirs"] = new JArray()
            };
            root["version"] = Version;
            root["output_style"] = new JObject { ["name"] = string.IsNullOrEmpty(OutputStyle) ? "default" : OutputStyle };
            root["cost"] = new JObject
            {
                ["total_cost_usd"] = TotalCostUsd,
                ["total_duration_ms"] = TotalDurationMs,
                ["total_api_duration_ms"] = TotalApiDurationMs,
                ["total_lines_added"] = 0,
                ["total_lines_removed"] = 0
            };

            int totalInput = InputTokens + CacheCreationTokens + CacheReadTokens;
            var context = new JObject
            {
                ["total_input_tokens"] = totalInput,
                ["total_output_tokens"] = OutputTokens,
                ["context_window_size"] = ContextWindowSize
            };

            // Same formula as the CLI: input only, output excluded.
            if (HasCurrentUsage && ContextWindowSize > 0)
            {
                int used = (int)Math.Round(Math.Min(100.0, totalInput * 100.0 / ContextWindowSize));
                context["used_percentage"] = used;
                context["remaining_percentage"] = 100 - used;
                context["current_usage"] = new JObject
                {
                    ["input_tokens"] = InputTokens,
                    ["output_tokens"] = OutputTokens,
                    ["cache_creation_input_tokens"] = CacheCreationTokens,
                    ["cache_read_input_tokens"] = CacheReadTokens
                };
            }
            else
            {
                context["used_percentage"] = null;
                context["remaining_percentage"] = null;
                context["current_usage"] = null;
            }

            root["context_window"] = context;
            root["exceeds_200k_tokens"] = totalInput + OutputTokens > 200000;
            root["fast_mode"] = FastMode;

            if (!string.IsNullOrEmpty(effortLevel))
            {
                root["effort"] = new JObject { ["level"] = effortLevel };
            }

            var rateLimits = new JObject();
            if (FiveHourUsedPercentage.HasValue)
            {
                rateLimits["five_hour"] = new JObject
                {
                    ["used_percentage"] = FiveHourUsedPercentage.Value,
                    ["resets_at"] = FiveHourResetsAt
                };
            }
            if (SevenDayUsedPercentage.HasValue)
            {
                rateLimits["seven_day"] = new JObject
                {
                    ["used_percentage"] = SevenDayUsedPercentage.Value,
                    ["resets_at"] = SevenDayResetsAt
                };
            }
            if (rateLimits.Count > 0)
            {
                root["rate_limits"] = rateLimits;
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(root);
        }

        /// <summary>The CLI's project-folder encoding: every character outside ASCII letters and digits becomes '-'.</summary>
        internal static string EncodeProjectPath(string path)
        {
            var sb = new StringBuilder(path.Length);
            foreach (char c in path)
            {
                bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                sb.Append(keep ? c : '-');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Finds and runs the <c>statusLine</c> command the user configured for Claude Code.
    /// </summary>
    public static class ClaudeStatusLine
    {
        /// <summary>Claude Code's Windows managed-settings location.</summary>
        public const string ManagedSettingsPath = @"C:\Program Files\ClaudeCode\managed-settings.json";

        // A status line is meant to be instant; one that hangs must not leave processes behind.
        private const int TimeoutMs = 10000;

        /// <summary>
        /// The <c>statusLine.command</c> in effect for <paramref name="projectDirectory"/>, or null when
        /// none is configured. Precedence is the CLI's: managed, then the project's
        /// <c>.claude/settings.local.json</c>, the project's <c>.claude/settings.json</c>, and the user's
        /// <c>settings.json</c>. <c>disableAllHooks</c> (outside managed settings) or managed
        /// <c>allowManagedHooksOnly</c> limit it to a managed status line, as the CLI does.
        /// </summary>
        public static string ResolveCommand(string managedSettingsPath, string userClaudeDirectory, string projectDirectory)
        {
            JObject managed = ReadSettings(managedSettingsPath);

            var others = new List<JObject>();
            if (!string.IsNullOrEmpty(projectDirectory))
            {
                others.Add(ReadSettings(Path.Combine(projectDirectory, ".claude", "settings.local.json")));
                others.Add(ReadSettings(Path.Combine(projectDirectory, ".claude", "settings.json")));
            }
            if (!string.IsNullOrEmpty(userClaudeDirectory))
            {
                others.Add(ReadSettings(Path.Combine(userClaudeDirectory, "settings.json")));
            }

            string managedCommand = CommandFrom(managed);
            if (managedCommand != null)
            {
                return managedCommand;
            }

            if ((bool?)managed?["allowManagedHooksOnly"] == true)
            {
                return null;
            }

            foreach (JObject settings in others)
            {
                JToken disable = settings?["disableAllHooks"];
                if (disable != null && disable.Type == JTokenType.Boolean)
                {
                    if ((bool)disable) return null;
                    break;
                }
            }

            foreach (JObject settings in others)
            {
                if (settings?["statusLine"] != null)
                {
                    // The highest-precedence file that sets statusLine decides, even when its value is unusable.
                    return CommandFrom(settings);
                }
            }

            return null;
        }

        private static string CommandFrom(JObject settings)
        {
            var statusLine = settings?["statusLine"] as JObject;
            if (statusLine == null)
            {
                return null;
            }

            string type = (string)statusLine["type"];
            string command = (string)statusLine["command"];
            if ((type != null && type != "command") || string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            return command;
        }

        private static JObject ReadSettings(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }

                return JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // A half-written or hand-edited settings file must not break the chat.
                Debug.WriteLine($"ClaudeStatusLine: cannot read {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// "claude-opus-5-5" → "Opus 5.5", "claude-sonnet-4-5-20250929" → "Sonnet 4.5",
        /// "claude-opus-5-5[1m]" → "Opus 5.5 (1M context)" — the names the CLI shows.
        /// </summary>
        public static string ModelDisplayName(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return string.Empty;
            }

            string id = modelId.Trim();
            string suffix = string.Empty;
            int bracket = id.IndexOf('[');
            if (bracket > 0)
            {
                if (id.Substring(bracket).Equals("[1m]", StringComparison.OrdinalIgnoreCase))
                {
                    suffix = " (1M context)";
                }
                id = id.Substring(0, bracket);
            }

            string[] parts = id.Split('-');
            string family = null;
            var version = new List<string>();

            foreach (string part in parts)
            {
                if (part.Equals("claude", StringComparison.OrdinalIgnoreCase) || part.Length == 0)
                {
                    continue;
                }

                if (IsDigits(part))
                {
                    // Version parts are short; a long run of digits is the release date.
                    if (part.Length <= 2) version.Add(part);
                    continue;
                }

                if (family == null)
                {
                    family = char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant();
                }
            }

            if (family == null)
            {
                return modelId;
            }

            return (version.Count > 0 ? family + " " + string.Join(".", version) : family) + suffix;
        }

        private static bool IsDigits(string value)
        {
            foreach (char c in value)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static readonly Regex EscapeSequence = new Regex(
            @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)" +   // OSC (hyperlinks, titles)
            @"|\x1B\[[0-?]*[ -/]*[@-~]" +            // CSI (colors, cursor)
            @"|\x1B[@-Z\\-_]",                        // other two-character escapes
            RegexOptions.Compiled);

        /// <summary>
        /// The script's output as plain text: escape sequences (ANSI colors, OSC 8 links) removed,
        /// trailing blanks trimmed, line breaks kept — a status line may span several rows.
        /// </summary>
        public static string CleanOutput(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            string text = EscapeSequence.Replace(raw, string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

            var lines = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                var sb = new StringBuilder(line.Length);
                foreach (char c in line)
                {
                    if (c == '\t' || !char.IsControl(c)) sb.Append(c);
                }
                lines.Add(sb.ToString().TrimEnd());
            }

            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);

            return string.Join("\n", lines);
        }

        /// <summary>Claude Code's managed-settings location on Linux.</summary>
        public const string WslManagedSettingsPath = "/etc/claude-code/managed-settings.json";

        private static readonly object WslLocationLock = new object();
        private static Task<WslLocation> _wslLocation;

        /// <summary>
        /// Where the default WSL distro keeps things, seen from Windows: its root share
        /// (<c>\\wsl.localhost\Ubuntu\</c>) and the Linux path of the Claude config folder
        /// (<c>$CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>). Asked once per VS session; a failed
        /// probe is not kept, so the next turn tries again.
        /// </summary>
        public static async Task<WslLocation> GetWslLocationAsync()
        {
            Task<WslLocation> probe;
            lock (WslLocationLock)
            {
                if (_wslLocation == null)
                {
                    _wslLocation = Task.Run(ProbeWslLocationAsync);
                }
                probe = _wslLocation;
            }

            WslLocation location = await probe.ConfigureAwait(false);
            if (location == null)
            {
                lock (WslLocationLock)
                {
                    // Only this probe's own entry; a newer one may already be running.
                    if (_wslLocation == probe)
                    {
                        _wslLocation = null;
                    }
                }
            }
            return location;
        }

        private static async Task<WslLocation> ProbeWslLocationAsync()
        {
            // Marked lines: an interactive login shell may print its own text from the profile scripts.
            const string script = "printf '@@%s\\n' \"$(wslpath -w /)\" \"${CLAUDE_CONFIG_DIR:-$HOME/.claude}\"";

            var startInfo = NewStartInfo(Environment.CurrentDirectory);
            startInfo.FileName = "wsl.exe";
            startInfo.Arguments = "-e bash -lic " + ClaudeCommandBuilder.QuoteForWindowsArgument(script); // -e: see RunInWslAsync

            string output = await RunProcessAsync(startInfo, string.Empty, CancellationToken.None).ConfigureAwait(false);
            return WslLocation.Parse(output);
        }

        /// <summary>
        /// Git Bash, the shell Claude Code itself uses for status lines on Windows when it is installed:
        /// <c>CLAUDE_CODE_GIT_BASH_PATH</c>, then the bash next to <c>git.exe</c> on PATH, then the
        /// default install folder. Null when none exists.
        /// </summary>
        public static string FindGitBash()
        {
            string configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()))
            {
                return configured.Trim();
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    string trimmed = dir.Trim().Trim('"');
                    if (trimmed.Length == 0 || !File.Exists(Path.Combine(trimmed, "git.exe")))
                    {
                        continue;
                    }

                    // <git>\cmd\git.exe or <git>\bin\git.exe → <git>\bin\bash.exe
                    string gitRoot = Path.GetDirectoryName(trimmed.TrimEnd('\\', '/'));
                    string bash = gitRoot != null ? Path.Combine(gitRoot, "bin", "bash.exe") : null;
                    if (bash != null && File.Exists(bash))
                    {
                        return bash;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }

            foreach (string candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                @"C:\Program Files\Git\bin\bash.exe"
            })
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        /// <summary>
        /// Runs <paramref name="command"/> the way Claude Code does — through Git Bash, else PowerShell —
        /// with <paramref name="inputJson"/> on stdin and <c>COLUMNS</c> set. Returns the cleaned
        /// output, or null when the command fails, exits non-zero, prints nothing or times out: the CLI
        /// leaves its status line blank in all of those cases.
        /// </summary>
        public static async Task<string> RunAsync(string command, string inputJson, string workingDirectory,
            int columns, CancellationToken cancellationToken)
        {
            var startInfo = NewStartInfo(Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory);

            string bash = FindGitBash();
            if (bash != null)
            {
                startInfo.FileName = bash;
                startInfo.Arguments = "-c " + ClaudeCommandBuilder.QuoteForWindowsArgument(command);
            }
            else
            {
                // EncodedCommand sidesteps every quoting rule between here and PowerShell's parser.
                startInfo.FileName = "powershell.exe";
                startInfo.Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            }

            startInfo.EnvironmentVariables["COLUMNS"] = Math.Max(20, columns).ToString(CultureInfo.InvariantCulture);
            startInfo.EnvironmentVariables["LINES"] = "24";

            return await RunProcessAsync(startInfo, inputJson, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// <see cref="RunAsync"/> for Claude Code (WSL): the command runs in the default distro under
        /// the same login shell native mode launches <c>claude</c> with, so it sees the PATH (jq, node
        /// version managers) the CLI itself would hand it. The inner <c>bash -c</c> is the non-interactive
        /// shell the CLI uses for status lines; <paramref name="wslWorkingDirectory"/> is a Linux path.
        /// </summary>
        public static async Task<string> RunInWslAsync(string command, string inputJson, string wslWorkingDirectory,
            int columns, CancellationToken cancellationToken)
        {
            string inner = "cd " + ClaudeCommandBuilder.QuoteForBash(wslWorkingDirectory) +
                " && export COLUMNS=" + Math.Max(20, columns).ToString(CultureInfo.InvariantCulture) + " LINES=24" +
                " && exec bash -c " + ClaudeCommandBuilder.QuoteForBash(command);

            // -e hands bash its argument as is. Without it wsl.exe re-joins the arguments and runs them
            // through the distro's shell a second time, and any double quote in the command breaks
            // (measured: "unexpected EOF while looking for matching '\"'").
            var startInfo = NewStartInfo(Environment.CurrentDirectory);
            startInfo.FileName = "wsl.exe";
            startInfo.Arguments = "-e bash -lic " + ClaudeCommandBuilder.QuoteForWindowsArgument(inner);

            return await RunProcessAsync(startInfo, inputJson, cancellationToken).ConfigureAwait(false);
        }

        private static ProcessStartInfo NewStartInfo(string workingDirectory)
        {
            return new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = workingDirectory
            };
        }

        private static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, string inputJson,
            CancellationToken cancellationToken)
        {
            Process process = null;
            try
            {
                process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                try
                {
                    using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                    {
                        await stdin.WriteAsync(inputJson ?? "{}").ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    // A command that never reads stdin (a plain echo) may exit before the write lands.
                }

                Task finished = Task.WhenAll(stdout, stderr);
                Task winner = await Task.WhenAny(finished, Task.Delay(TimeoutMs, cancellationToken)).ConfigureAwait(false);
                if (winner != finished || !process.WaitForExit(TimeoutMs))
                {
                    Debug.WriteLine("ClaudeStatusLine: command timed out or was cancelled");
                    ProcessTree.Kill(process.Id);
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    Debug.WriteLine($"ClaudeStatusLine: exit {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
                    return null;
                }

                string output = CleanOutput(await stdout.ConfigureAwait(false));
                return output.Length > 0 ? output : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClaudeStatusLine: {ex.Message}");
                if (process != null)
                {
                    try { ProcessTree.Kill(process.Id); } catch (Exception) { }
                }
                return null;
            }
            finally
            {
                process?.Dispose();
            }
        }
    }

    /// <summary>The default WSL distro's root share and Claude config folder, from <see cref="ClaudeStatusLine.GetWslLocationAsync"/>.</summary>
    public sealed class WslLocation
    {
        /// <summary><c>\\wsl.localhost\&lt;distro&gt;\</c></summary>
        public string RootShare { get; private set; }

        /// <summary>Linux path, e.g. <c>/home/me/.claude</c>.</summary>
        public string ConfigDirectory { get; private set; }

        public WslLocation(string rootShare, string configDirectory)
        {
            RootShare = rootShare;
            ConfigDirectory = configDirectory;
        }

        /// <summary>The two <c>@@</c>-marked lines of the probe, or null when either is missing.</summary>
        internal static WslLocation Parse(string probeOutput)
        {
            var values = new List<string>();
            foreach (string line in (probeOutput ?? string.Empty).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("@@", StringComparison.Ordinal) && trimmed.Length > 2)
                {
                    values.Add(trimmed.Substring(2));
                }
            }

            if (values.Count != 2 || !values[0].StartsWith(@"\\", StringComparison.Ordinal) ||
                !values[1].StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            return new WslLocation(values[0], values[1]);
        }

        /// <summary>
        /// A Linux path as Windows file IO can open it: <c>/mnt/c/x</c> → <c>C:\x</c> (directly, not
        /// through the slower share), anything else under the distro's root share.
        /// </summary>
        public string ToWindowsPath(string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith("/", StringComparison.Ordinal))
            {
                return linuxPath;
            }

            if (linuxPath.Length >= 6 && linuxPath.StartsWith("/mnt/", StringComparison.Ordinal) &&
                char.IsLetter(linuxPath[5]) && (linuxPath.Length == 6 || linuxPath[6] == '/'))
            {
                string rest = linuxPath.Length > 7 ? linuxPath.Substring(7) : string.Empty;
                return char.ToUpperInvariant(linuxPath[5]) + @":\" + rest.Replace('/', '\\');
            }

            return RootShare.TrimEnd('\\') + linuxPath.Replace('/', '\\');
        }
    }
}
