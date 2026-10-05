/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Reads a Devin account's usage (ACUs consumed vs. limit, daily/weekly quota) for native mode, where
 *          the CLI's own /usage slash command is unavailable — `devin acp` and `devin -p` both answer
 *          "Unknown command: /usage". Calls the same GetUserStatus service the CLI uses, with the credentials
 *          the CLI stored on login. Pure (no WPF, no VS SDK) so the parsing and formatting are unit-testable.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>The usage figures <c>GetUserStatus</c> reports. Anything the plan does not meter stays null.</summary>
    internal sealed class DevinUsageInfo
    {
        public string PlanName { get; set; } = string.Empty;

        public double? AcuConsumed { get; set; }

        public double? AcuLimit { get; set; }

        public double? DailyRemainingPercent { get; set; }

        public double? DailyResetUnix { get; set; }

        public double? WeeklyRemainingPercent { get; set; }

        public double? WeeklyResetUnix { get; set; }

        /// <summary>True when the response carried at least one figure worth showing.</summary>
        public bool HasUsageData
        {
            get
            {
                return AcuConsumed.HasValue || DailyRemainingPercent.HasValue || WeeklyRemainingPercent.HasValue;
            }
        }
    }

    /// <summary>
    /// Fetches and formats Devin account usage. The service is undocumented, so every failure is reported
    /// as an <see cref="InvalidOperationException"/> carrying a message fit for the chat, and the caller
    /// falls back to the usage web page.
    /// </summary>
    internal static class DevinUsageClient
    {
        private const string UserStatusPath = "/exa.seat_management_pb.SeatManagementService/GetUserStatus";
        private const int RequestTimeoutSeconds = 15;
        private const int CredentialsReadTimeoutMs = 10000;

        /// <summary>
        /// The service rejects an empty or non-numeric client version (HTTP 400/500) but accepts any dotted
        /// number, so this stands in when the CLI's own version cannot be read.
        /// </summary>
        internal const string FallbackCliVersion = "3000.0.0";

        private static readonly Regex VersionPattern = new Regex(@"\b(\d+(?:\.\d+)+)\b", RegexOptions.Compiled);

        private static readonly Regex TomlStringPattern = new Regex(
            "^\\s*([A-Za-z0-9_\\-]+)\\s*=\\s*(?:\"((?:[^\"\\\\]|\\\\.)*)\"|'([^']*)')",
            RegexOptions.Compiled);

        /// <summary>
        /// Where the Windows CLI keeps its login, in the order to try: the roaming profile first (where it
        /// was measured), then the XDG-style location the Linux build uses.
        /// </summary>
        public static IList<string> GetWindowsCredentialPaths()
        {
            var paths = new List<string>();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(roaming))
            {
                paths.Add(Path.Combine(roaming, "devin", "credentials.toml"));
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                paths.Add(Path.Combine(profile, ".local", "share", "devin", "credentials.toml"));
            }

            return paths;
        }

        /// <summary>Reads the first credentials file that exists on the Windows side, or null.</summary>
        public static string ReadWindowsCredentials()
        {
            foreach (string path in GetWindowsCredentialPaths())
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return File.ReadAllText(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DevinUsageClient: could not read {path}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>Reads the credentials file inside the default WSL distro, or null when it is not there.</summary>
        public static async Task<string> ReadWslCredentialsAsync(CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-e sh -c \"cat \\\"${XDG_DATA_HOME:-$HOME/.local/share}/devin/credentials.toml\\\"\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                bool exited = await Task.Run(() => process.WaitForExit(CredentialsReadTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    return null;
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
            }
        }

        /// <summary>
        /// Pulls the API key and server out of the CLI's credentials file. Only flat <c>key = "value"</c>
        /// lines are read — all this file holds — so no TOML library is needed.
        /// </summary>
        internal static bool TryParseCredentials(string toml, out string apiKey, out Uri server)
        {
            apiKey = null;
            server = null;
            if (string.IsNullOrWhiteSpace(toml)) return false;

            string serverText = null;
            foreach (string line in toml.Split('\n'))
            {
                Match match = TomlStringPattern.Match(line.TrimEnd('\r'));
                if (!match.Success) continue;

                string value = match.Groups[2].Success
                    ? match.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")
                    : match.Groups[3].Value;

                switch (match.Groups[1].Value)
                {
                    case "windsurf_api_key": apiKey = value; break;
                    case "api_server_url": serverText = value; break;
                }
            }

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(serverText)) return false;

            // The key goes to this host, so refuse anything that is not HTTPS.
            if (!Uri.TryCreate(serverText.TrimEnd('/'), UriKind.Absolute, out server)
                || server.Scheme != Uri.UriSchemeHttps)
            {
                server = null;
                return false;
            }

            return true;
        }

        /// <summary>The first dotted number in <c>devin version</c>'s output, e.g. "3000.11.3" from "devin 3000.11.3 (9c80…)".</summary>
        internal static string ExtractVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            Match match = VersionPattern.Match(output);
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static string BuildRequestBody(string apiKey, string cliVersion)
        {
            var metadata = new JObject
            {
                ["apiKey"] = apiKey,
                ["ideName"] = "devin",
                ["ideVersion"] = cliVersion,
                ["extensionVersion"] = cliVersion,
                ["locale"] = "en"
            };
            return new JObject { ["metadata"] = metadata }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Fetches usage with already-read credentials. <paramref name="handler"/> is a test seam.</summary>
        public static async Task<DevinUsageInfo> FetchAsync(
            string credentialsToml,
            string cliVersion,
            CancellationToken cancellationToken,
            HttpMessageHandler handler = null)
        {
            string apiKey;
            Uri server;
            if (!TryParseCredentials(credentialsToml, out apiKey, out server))
            {
                throw new InvalidOperationException(
                    "Devin's login could not be read. Sign in with Devin again, then retry.");
            }

            string version = ExtractVersion(cliVersion) ?? FallbackCliVersion;
            string body = BuildRequestBody(apiKey, version);

            using (var client = handler != null ? new HttpClient(handler, false) : new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds);

                using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, UserStatusPath)))
                {
                    request.Headers.Add("Connect-Protocol-Version", "1");
                    request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");

                    HttpResponseMessage response;
                    try
                    {
                        response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        throw new InvalidOperationException("Devin's usage service could not be reached.");
                    }

                    using (response)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                            || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new InvalidOperationException(
                                "Devin no longer accepts the stored login. Sign in with Devin again, then retry.");
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException(
                                $"Devin's usage service answered HTTP {(int)response.StatusCode}.");
                        }

                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ParseUserStatus(json);
                    }
                }
            }
        }

        /// <summary>Maps a <c>GetUserStatus</c> response onto <see cref="DevinUsageInfo"/>.</summary>
        internal static DevinUsageInfo ParseUserStatus(string json)
        {
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Devin's usage service returned an unreadable response.");
            }

            JObject planStatus = root["userStatus"]?["planStatus"] as JObject;
            if (planStatus == null)
            {
                throw new InvalidOperationException("Devin's usage service returned no plan data.");
            }

            JObject planInfo = (root["planInfo"] ?? planStatus["planInfo"]) as JObject;

            var info = new DevinUsageInfo
            {
                PlanName = planInfo?["planName"]?.ToString() ?? string.Empty,
                AcuConsumed = ReadNumber(planStatus["acuConsumed"]),
                AcuLimit = ReadNumber(planStatus["acuLimit"]),
                DailyRemainingPercent = ReadNumber(planStatus["dailyQuotaRemainingPercent"]),
                DailyResetUnix = ReadNumber(planStatus["dailyQuotaResetAtUnix"]),
                WeeklyRemainingPercent = ReadNumber(planStatus["weeklyQuotaRemainingPercent"]),
                WeeklyResetUnix = ReadNumber(planStatus["weeklyQuotaResetAtUnix"])
            };

            return info;
        }

        /// <summary>Proto-JSON writes 64-bit integers as strings, so a number may arrive as either.</summary>
        private static double? ReadNumber(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;

            double value;
            if (!double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            return value;
        }

        /// <summary>Renders the figures as the multi-line notice shown in the chat.</summary>
        public static string FormatReport(DevinUsageInfo info, DateTime utcNow)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            var text = new StringBuilder();
            text.Append("📊 Devin usage");
            if (!string.IsNullOrEmpty(info.PlanName))
            {
                text.Append(" — ").Append(info.PlanName);
            }

            if (!info.HasUsageData)
            {
                text.Append("\nDevin did not report any usage figures for this account.");
                return text.ToString();
            }

            if (info.AcuConsumed.HasValue)
            {
                text.Append("\nCycle: ").Append(info.AcuConsumed.Value.ToString("0.00", CultureInfo.InvariantCulture));
                if (info.AcuLimit.HasValue && info.AcuLimit.Value > 0)
                {
                    double used = Math.Round(info.AcuConsumed.Value / info.AcuLimit.Value * 100, MidpointRounding.AwayFromZero);
                    text.Append(" of ").Append(info.AcuLimit.Value.ToString("0.00", CultureInfo.InvariantCulture))
                        .Append(" ACUs · ").Append(used.ToString("0", CultureInfo.InvariantCulture)).Append("% used");
                }
                else
                {
                    text.Append(" ACUs used");
                }
            }

            AppendQuota(text, "Daily", info.DailyRemainingPercent, info.DailyResetUnix, utcNow);
            AppendQuota(text, "Weekly", info.WeeklyRemainingPercent, info.WeeklyResetUnix, utcNow);
            return text.ToString();
        }

        private static void AppendQuota(StringBuilder text, string name, double? remainingPercent, double? resetUnix, DateTime utcNow)
        {
            if (!remainingPercent.HasValue) return;

            text.Append('\n').Append(name).Append(": ")
                .Append(remainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture)).Append("% remaining");

            string reset = FormatReset(resetUnix, utcNow);
            if (reset.Length > 0)
            {
                text.Append(", resets in ").Append(reset);
            }
        }

        /// <summary>"2d3h", "5h12m" or "40m" until the reset; empty when there is no usable timestamp.</summary>
        internal static string FormatReset(double? resetUnix, DateTime utcNow)
        {
            if (!resetUnix.HasValue) return string.Empty;

            DateTime reset;
            try
            {
                reset = DateTimeOffset.FromUnixTimeSeconds((long)resetUnix.Value).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }

            int minutes = Math.Max(0, (int)(reset - utcNow).TotalMinutes);
            if (minutes >= 1440) return $"{minutes / 1440}d{minutes % 1440 / 60}h";
            if (minutes >= 60) return $"{minutes / 60}h{minutes % 60}m";
            return $"{minutes}m";
        }
    }
}
