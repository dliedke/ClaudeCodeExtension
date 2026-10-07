/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: The "/" command list shown while typing in the prompt box (issue #187). Claude Code's terminal UI lists
 *          built-in commands, custom commands (.claude/commands/*.md) and skills (.claude/skills/STAR/SKILL.md)
 *          with their descriptions as soon as "/" is typed; the native chat composer has no TUI, so this
 *          rebuilds that list: it reads the user and project folders, adds the commands the extension handles
 *          itself, and merges the names the CLI announced when the session started. Pure (no WPF, no VS SDK)
 *          so discovery, front-matter parsing and ranking are unit-testable.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCodeVS.Agents
{
    /// <summary>One entry in the "/" picker.</summary>
    internal sealed class SlashCommandEntry
    {
        private const int MaxDisplayDescription = 90;

        /// <summary>Command name without the leading slash.</summary>
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        /// <summary>"project", "user", "built-in" or "session" — shown after the description.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>The row text. The picker's ListBox renders items with <c>ToString</c>.</summary>
        public override string ToString()
        {
            string description = Description ?? string.Empty;
            if (description.Length > MaxDisplayDescription)
            {
                description = description.Substring(0, MaxDisplayDescription - 1) + "…";
            }

            var sb = new StringBuilder("/").Append(Name);
            if (description.Length > 0)
            {
                sb.Append("  —  ").Append(description);
            }

            if (!string.IsNullOrEmpty(Source))
            {
                sb.Append("  (").Append(Source).Append(')');
            }

            return sb.ToString();
        }
    }

    internal static class SlashCommandCatalog
    {
        private const int MaxFilesPerFolder = 500;

        /// <summary>
        /// Commands the extension (or the CLI in every mode) always answers. Everything else a session
        /// supports is learned from the CLI's own announcement, see <see cref="Merge"/>.
        /// </summary>
        public static readonly IReadOnlyList<SlashCommandEntry> BuiltIns = new List<SlashCommandEntry>
        {
            new SlashCommandEntry { Name = "plan", Description = "Switch to plan mode", Source = "built-in" },
            new SlashCommandEntry { Name = "model", Description = "Choose the model", Source = "built-in" },
            new SlashCommandEntry { Name = "effort", Description = "Choose the effort level", Source = "built-in" },
            new SlashCommandEntry { Name = "btw", Description = "Ask a side question without disturbing the work in progress", Source = "built-in" },
            new SlashCommandEntry { Name = "compact", Description = "Summarize the conversation to free up context", Source = "built-in" }
        };

        /// <summary>
        /// Descriptions for the CLI's own commands. The session announces only their names, so without this
        /// they would be listed bare. Deliberately limited to commands with a stable, well-known meaning;
        /// a name not listed here still shows up, just without a description.
        /// </summary>
        private static readonly Dictionary<string, string> KnownDescriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "add-dir", "Add a new working directory" },
            { "agents", "Manage agent configurations" },
            { "clear", "Clear the conversation history and free up context" },
            { "color", "Set the prompt bar color for this session" },
            { "compact", "Summarize the conversation to free up context" },
            { "config", "Open the settings" },
            { "context", "Show current context usage" },
            { "cost", "Show token usage and cost" },
            { "doctor", "Diagnose the installation and settings" },
            { "export", "Export the conversation" },
            { "fast", "Toggle fast mode" },
            { "heapdump", "Write a JavaScript heap dump to disk" },
            { "help", "Show help and the available commands" },
            { "hooks", "Manage hook configurations" },
            { "ide", "Manage IDE integrations" },
            { "init", "Initialize a CLAUDE.md file with codebase documentation" },
            { "insights", "Generate a report analyzing your Claude Code sessions" },
            { "login", "Sign in with your Anthropic account" },
            { "logout", "Sign out of your Anthropic account" },
            { "mcp", "Manage MCP servers" },
            { "memory", "Edit Claude memory files" },
            { "permissions", "Manage tool permission rules" },
            { "rename", "Rename the conversation" },
            { "resume", "Resume a previous conversation" },
            { "review", "Review a pull request" },
            { "rewind", "Restore the conversation to an earlier point" },
            { "security-review", "Complete a security review of the pending changes" },
            { "skills", "List the available skills" },
            { "status", "Show version, model and account status" },
            { "statusline", "Set up the status line" },
            { "usage", "Show plan usage limits" }
        };

        /// <summary>
        /// Reads custom commands and skills from the user's Claude folder and the workspace's. A project
        /// entry replaces a user entry of the same name, like the CLI does. Never throws: unreadable
        /// folders and files are skipped.
        /// </summary>
        public static List<SlashCommandEntry> Discover(string workspaceRoot, string userClaudeDir)
        {
            var byName = new Dictionary<string, SlashCommandEntry>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(userClaudeDir))
            {
                ReadClaudeFolder(userClaudeDir, "user", byName);
            }

            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                ReadClaudeFolder(Path.Combine(workspaceRoot, ".claude"), "project", byName);
            }

            return byName.Values.ToList();
        }

        /// <summary>
        /// Combines the discovered entries with the built-ins and the names the session announced. A
        /// name that already has an entry keeps it (it has a description); a bare announced name is
        /// still worth listing, so the picker offers everything the running CLI accepts.
        /// </summary>
        public static List<SlashCommandEntry> Merge(IEnumerable<SlashCommandEntry> discovered,
            IEnumerable<string> announcedNames)
        {
            var byName = new Dictionary<string, SlashCommandEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (SlashCommandEntry entry in BuiltIns)
            {
                byName[entry.Name] = entry;
            }

            if (discovered != null)
            {
                foreach (SlashCommandEntry entry in discovered)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.Name))
                    {
                        byName[entry.Name] = entry;
                    }
                }
            }

            if (announcedNames != null)
            {
                foreach (string raw in announcedNames)
                {
                    string name = NormalizeName(raw);
                    if (name.Length == 0 || byName.ContainsKey(name))
                    {
                        continue;
                    }

                    bool known = KnownDescriptions.TryGetValue(name, out string description);
                    byName[name] = new SlashCommandEntry
                    {
                        Name = name,
                        Description = known ? description : string.Empty,
                        Source = known ? "built-in" : "session"
                    };
                }
            }

            return byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Entries for the text typed after the "/": name prefix first, then name substring, then
        /// description substring. An empty query lists everything.
        /// </summary>
        public static List<SlashCommandEntry> Rank(IEnumerable<SlashCommandEntry> entries, string query, int max)
        {
            string q = (query ?? string.Empty).Trim();
            var all = entries ?? Enumerable.Empty<SlashCommandEntry>();

            if (q.Length == 0)
            {
                return all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
            }

            var prefix = new List<SlashCommandEntry>();
            var nameContains = new List<SlashCommandEntry>();
            var descriptionContains = new List<SlashCommandEntry>();

            foreach (SlashCommandEntry entry in all.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) prefix.Add(entry);
                else if (entry.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) nameContains.Add(entry);
                else if ((entry.Description ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    descriptionContains.Add(entry);
            }

            return prefix.Concat(nameContains).Concat(descriptionContains).Take(max).ToList();
        }

        /// <summary>
        /// The "/name" token at the start of <paramref name="text"/> when the caret is still inside it, i.e.
        /// the user is typing a command name. Returns false once a space follows the name (arguments) or
        /// when the text does not start with a slash.
        /// </summary>
        public static bool TryGetQuery(string text, int caret, out string query)
        {
            query = string.Empty;
            if (string.IsNullOrEmpty(text) || text[0] != '/' || caret < 1 || caret > text.Length)
            {
                return false;
            }

            for (int i = 1; i < caret; i++)
            {
                if (char.IsWhiteSpace(text[i]) || text[i] == '/')
                {
                    return false;
                }
            }

            query = text.Substring(1, caret - 1);
            return true;
        }

        /// <summary>The user-level Claude folder, honouring <c>CLAUDE_CONFIG_DIR</c> like the CLI.</summary>
        public static string GetUserClaudeDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude");
        }

        internal static string NormalizeName(string raw)
        {
            return (raw ?? string.Empty).Trim().TrimStart('/').Trim();
        }

        private static void ReadClaudeFolder(string claudeDir, string source,
            Dictionary<string, SlashCommandEntry> byName)
        {
            try
            {
                string commandsDir = Path.Combine(claudeDir, "commands");
                if (Directory.Exists(commandsDir))
                {
                    foreach (string file in EnumerateFiles(commandsDir, "*.md"))
                    {
                        AddEntry(byName, ReadFile(file, Path.GetFileNameWithoutExtension(file), source));
                    }
                }

                string skillsDir = Path.Combine(claudeDir, "skills");
                if (Directory.Exists(skillsDir))
                {
                    foreach (string dir in Directory.EnumerateDirectories(skillsDir).Take(MaxFilesPerFolder))
                    {
                        string skillFile = Path.Combine(dir, "SKILL.md");
                        if (File.Exists(skillFile))
                        {
                            AddEntry(byName, ReadFile(skillFile, Path.GetFileName(dir), source));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SlashCommandCatalog.ReadClaudeFolder error: {ex.Message}");
            }
        }

        private static IEnumerable<string> EnumerateFiles(string dir, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Take(MaxFilesPerFolder).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SlashCommandCatalog.EnumerateFiles error: {ex.Message}");
                return Enumerable.Empty<string>();
            }
        }

        private static void AddEntry(Dictionary<string, SlashCommandEntry> byName, SlashCommandEntry entry)
        {
            if (entry != null)
            {
                byName[entry.Name] = entry;
            }
        }

        private static SlashCommandEntry ReadFile(string path, string fallbackName, string source)
        {
            try
            {
                string text = File.ReadAllText(path);
                return ParseMarkdown(text, fallbackName, source);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SlashCommandCatalog.ReadFile error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds an entry from a command/skill markdown file. Uses the front-matter <c>name</c> (skills) and
        /// <c>description</c>; without a description the first non-empty body line stands in, which is what
        /// the CLI shows for a command that has none. Returns null for a skill hidden from the user
        /// (<c>user-invocable: false</c>).
        /// </summary>
        internal static SlashCommandEntry ParseMarkdown(string text, string fallbackName, string source)
        {
            Dictionary<string, string> meta = ParseFrontMatter(text, out string body);

            if (meta.TryGetValue("user-invocable", out string invocable)
                && string.Equals(invocable, "false", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string name = NormalizeName(meta.TryGetValue("name", out string n) ? n : null);
            if (name.Length == 0)
            {
                name = NormalizeName(fallbackName);
            }

            if (name.Length == 0 || name.IndexOfAny(new[] { ' ', '\t' }) >= 0)
            {
                return null;
            }

            meta.TryGetValue("description", out string description);
            if (string.IsNullOrWhiteSpace(description))
            {
                description = FirstBodyLine(body);
            }

            return new SlashCommandEntry
            {
                Name = name,
                Description = (description ?? string.Empty).Trim(),
                Source = source
            };
        }

        /// <summary>
        /// Minimal YAML front-matter reader: top-level <c>key: value</c> pairs, quoted values, and the
        /// block scalars (<c>|</c>, <c>&gt;</c>) and indented continuation lines skills use for long
        /// descriptions. Everything is folded onto a single line.
        /// </summary>
        internal static Dictionary<string, string> ParseFrontMatter(string text, out string body)
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            body = text ?? string.Empty;

            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                return meta;
            }

            int end = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { end = i; break; }
            }

            if (end < 0)
            {
                return meta;
            }

            body = string.Join("\n", lines.Skip(end + 1));

            string key = null;
            var value = new StringBuilder();

            void Flush()
            {
                if (key != null)
                {
                    meta[key] = Unquote(value.ToString().Trim());
                }

                key = null;
                value.Clear();
            }

            for (int i = 1; i < end; i++)
            {
                string line = lines[i];
                bool continuation = line.Length > 0 && char.IsWhiteSpace(line[0]);

                if (continuation && key != null)
                {
                    value.Append(' ').Append(line.Trim());
                    continue;
                }

                int colon = line.IndexOf(':');
                if (continuation || colon <= 0)
                {
                    continue;
                }

                Flush();
                key = line.Substring(0, colon).Trim();
                string inline = line.Substring(colon + 1).Trim();
                bool blockScalar = inline.Length > 0 && (inline[0] == '|' || inline[0] == '>');
                value.Append(blockScalar ? string.Empty : inline);
            }

            Flush();
            return meta;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2
                && ((value[0] == '"' && value[value.Length - 1] == '"')
                    || (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static string FirstBodyLine(string body)
        {
            foreach (string raw in (body ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim().TrimStart('#').Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }

            return string.Empty;
        }
    }
}
