/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Turns a native-mode tool call into what the transcript shows for it (title, target, diff)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Family a tool belongs to. Only drives the colour of the row's accent, so the transcript can be
    /// skimmed for "what did it change" without reading every header.
    /// </summary>
    public enum ChatToolCategory
    {
        Other,
        Read,
        Edit,
        Run,
        Search,
        Web,
        Todo,
        Agent
    }

    /// <summary>One rendered diff row inside a tool card.</summary>
    public enum ChatDiffKind
    {
        Context,
        Added,
        Removed,

        /// <summary>Gap marker between two hunks, or the title of one edit inside a MultiEdit.</summary>
        Separator
    }

    public class ChatDiffLine
    {
        public ChatDiffKind Kind { get; set; }

        /// <summary>Gutter character: <c>+</c>, <c>-</c> or a space. Part of the text the user copies.</summary>
        public string Marker { get; set; } = " ";

        public string Text { get; set; } = string.Empty;

        public bool IsAdded { get { return Kind == ChatDiffKind.Added; } }
        public bool IsRemoved { get { return Kind == ChatDiffKind.Removed; } }
        public bool IsSeparator { get { return Kind == ChatDiffKind.Separator; } }
    }

    /// <summary>
    /// Everything the transcript needs to draw a tool call. Built once when the call starts.
    /// </summary>
    public class ChatToolPresentation
    {
        public ChatToolCategory Category { get; set; } = ChatToolCategory.Other;

        /// <summary>Single glyph shown before the title. Text, not an icon font — the panel has no image assets.</summary>
        public string Icon { get; set; } = "•";

        /// <summary>The tool's own name, as the agent called it.</summary>
        public string Title { get; set; } = "Tool";

        /// <summary>What it acted on: a file, a command, a pattern. Empty when the tool has no obvious target.</summary>
        public string Subtitle { get; set; } = string.Empty;

        /// <summary>Right-aligned summary, e.g. <c>+12 -3</c>. Empty when there is nothing to count.</summary>
        public string Badge { get; set; } = string.Empty;

        /// <summary>Rendered diff for the editing tools. Empty for everything else.</summary>
        public IReadOnlyList<ChatDiffLine> Diff { get; set; } = new List<ChatDiffLine>();

        public bool HasDiff { get { return Diff != null && Diff.Count > 0; } }
    }

    /// <summary>
    /// Describes a tool call from its name and input payload.
    /// <para>
    /// Deliberately free of WPF and of the VS SDK so it can be unit-tested, and deliberately tolerant:
    /// an unknown tool, a malformed payload or a renamed argument must degrade to "name + raw JSON"
    /// rather than throw, because the tool inventory changes with every CLI release.
    /// </para>
    /// </summary>
    public static class ChatToolPresenter
    {
        /// <summary>Diff rows kept per tool card. A whole-file rewrite would otherwise stall the layout.</summary>
        private const int MaxDiffLines = 400;

        /// <summary>Unchanged rows kept on each side of a change.</summary>
        private const int ContextLines = 2;

        /// <summary>Characters of a command or pattern shown in the collapsed header.</summary>
        private const int MaxSubtitleLength = 160;

        public static ChatToolPresentation Describe(string toolName, string inputJson)
        {
            var result = new ChatToolPresentation
            {
                Title = string.IsNullOrWhiteSpace(toolName) ? "Tool" : NormalizeToolName(toolName)
            };

            JObject input = TryParse(inputJson);

            try
            {
                Populate(result, result.Title, input);
            }
            catch (Exception)
            {
                // A tool whose arguments changed shape must still render as a row.
            }

            if (string.IsNullOrEmpty(result.Icon) || result.Icon == "•")
            {
                result.Icon = IconFor(result.Category);
            }

            return result;
        }

        /// <summary>
        /// Strips the MCP prefix (<c>mcp__server__tool</c>) and any namespace, so a server-provided tool
        /// reads as its own name instead of a wire identifier.
        /// </summary>
        public static string NormalizeToolName(string toolName)
        {
            string name = toolName.Trim();

            int marker = name.LastIndexOf("__", StringComparison.Ordinal);
            if (marker >= 0 && marker + 2 < name.Length)
            {
                name = name.Substring(marker + 2);
            }

            return name;
        }

        private static void Populate(ChatToolPresentation result, string name, JObject input)
        {
            switch (name.ToLowerInvariant())
            {
                case "read":
                case "notebookread":
                    result.Category = ChatToolCategory.Read;
                    result.Subtitle = ShortenPath(GetString(input, "file_path", "notebook_path", "path", "filePath"));
                    result.Badge = FormatReadRange(input);
                    return;

                case "write":
                    result.Category = ChatToolCategory.Edit;
                    result.Subtitle = ShortenPath(GetString(input, "file_path", "path", "filePath"));
                    ApplyDiff(result, string.Empty, GetString(input, "content", "contents", "text"));
                    return;

                case "edit":
                case "str_replace":
                case "str_replace_editor":
                    result.Category = ChatToolCategory.Edit;
                    result.Subtitle = ShortenPath(GetString(input, "file_path", "path", "filePath"));
                    ApplyDiff(result,
                        GetString(input, "old_string", "oldString", "old_str"),
                        GetString(input, "new_string", "newString", "new_str"));
                    return;

                case "multiedit":
                    result.Category = ChatToolCategory.Edit;
                    result.Subtitle = ShortenPath(GetString(input, "file_path", "path", "filePath"));
                    ApplyMultiEditDiff(result, input);
                    return;

                case "notebookedit":
                    result.Category = ChatToolCategory.Edit;
                    result.Subtitle = ShortenPath(GetString(input, "notebook_path", "file_path", "path"));
                    ApplyDiff(result, string.Empty, GetString(input, "new_source", "source", "content"));
                    return;

                case "bash":
                case "bashoutput":
                case "shell":
                case "run_command":
                case "execute":
                    result.Category = ChatToolCategory.Run;
                    result.Subtitle = Flatten(GetString(input, "command", "cmd", "script", "bash_id"));
                    return;

                case "killshell":
                case "killbash":
                    result.Category = ChatToolCategory.Run;
                    result.Subtitle = GetString(input, "shell_id", "bash_id", "id");
                    return;

                case "glob":
                    result.Category = ChatToolCategory.Search;
                    result.Subtitle = JoinNonEmpty(" in ",
                        GetString(input, "pattern", "glob"),
                        ShortenPath(GetString(input, "path", "dir")));
                    return;

                case "grep":
                case "search":
                    result.Category = ChatToolCategory.Search;
                    result.Subtitle = JoinNonEmpty(" in ",
                        Flatten(GetString(input, "pattern", "query", "regex")),
                        ShortenPath(GetString(input, "path", "glob", "dir")));
                    return;

                case "webfetch":
                case "fetch":
                    result.Category = ChatToolCategory.Web;
                    result.Subtitle = Flatten(GetString(input, "url", "uri"));
                    return;

                case "websearch":
                    result.Category = ChatToolCategory.Web;
                    result.Subtitle = Flatten(GetString(input, "query", "q"));
                    return;

                case "todowrite":
                    result.Category = ChatToolCategory.Todo;
                    ApplyTodos(result, input);
                    return;

                case "task":
                case "agent":
                    result.Category = ChatToolCategory.Agent;
                    result.Subtitle = Flatten(GetString(input, "description", "prompt", "subagent_type"));
                    result.Badge = GetString(input, "subagent_type");
                    return;

                case "exitplanmode":
                    result.Category = ChatToolCategory.Agent;
                    result.Subtitle = "Plan ready for review";
                    return;

                case "askuserquestion":
                    result.Category = ChatToolCategory.Agent;
                    result.Subtitle = "Waiting for your answer";
                    return;

                default:
                    result.Category = ChatToolCategory.Other;
                    result.Subtitle = Flatten(GetString(input,
                        "file_path", "path", "command", "query", "pattern", "url", "description", "prompt"));
                    return;
            }
        }

        private static string IconFor(ChatToolCategory category)
        {
            switch (category)
            {
                case ChatToolCategory.Read: return "▤";
                case ChatToolCategory.Edit: return "✎";
                case ChatToolCategory.Run: return "▶";
                case ChatToolCategory.Search: return "⌕";
                case ChatToolCategory.Web: return "◍";
                case ChatToolCategory.Todo: return "☑";
                case ChatToolCategory.Agent: return "✦";
                default: return "◆";
            }
        }

        #region Diff

        private static void ApplyDiff(ChatToolPresentation result, string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText) && string.IsNullOrEmpty(newText))
            {
                return;
            }

            var lines = new List<ChatDiffLine>();
            int added, removed;
            AppendDiff(lines, oldText, newText, out added, out removed);

            result.Diff = lines;
            result.Badge = FormatChangeCount(added, removed);
        }

        /// <summary>
        /// One card for all the edits of a MultiEdit, each introduced by a separator row — the file is
        /// the same, so N collapsed cards would only make the same path harder to find N times.
        /// </summary>
        private static void ApplyMultiEditDiff(ChatToolPresentation result, JObject input)
        {
            JArray edits = input?["edits"] as JArray;
            if (edits == null || edits.Count == 0)
            {
                return;
            }

            var lines = new List<ChatDiffLine>();
            int totalAdded = 0;
            int totalRemoved = 0;
            int index = 0;

            foreach (JToken token in edits)
            {
                var edit = token as JObject;
                if (edit == null) continue;

                index++;

                if (lines.Count > 0)
                {
                    lines.Add(new ChatDiffLine { Kind = ChatDiffKind.Separator, Marker = string.Empty, Text = string.Empty });
                }

                lines.Add(new ChatDiffLine
                {
                    Kind = ChatDiffKind.Separator,
                    Marker = string.Empty,
                    Text = "Edit " + index + " of " + edits.Count
                });

                int added, removed;
                AppendDiff(lines,
                    GetString(edit, "old_string", "oldString", "old_str"),
                    GetString(edit, "new_string", "newString", "new_str"),
                    out added, out removed);

                totalAdded += added;
                totalRemoved += removed;

                if (lines.Count >= MaxDiffLines) break;
            }

            result.Diff = lines;
            result.Badge = FormatChangeCount(totalAdded, totalRemoved);
        }

        /// <summary>
        /// Appends a unified diff of two fragments, keeping only changed rows plus a little context.
        /// No line numbers: the payload is a fragment of the file, so any number shown would be wrong.
        /// </summary>
        private static void AppendDiff(List<ChatDiffLine> target, string oldText, string newText, out int added, out int removed)
        {
            added = 0;
            removed = 0;

            var model = new InlineDiffBuilder(new Differ())
                .BuildDiffModel(oldText ?? string.Empty, newText ?? string.Empty);

            List<DiffPiece> pieces = model.Lines;

            // Rows worth showing: every change, plus ContextLines unchanged rows around it.
            var keep = new bool[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Type == ChangeType.Inserted || pieces[i].Type == ChangeType.Deleted ||
                    pieces[i].Type == ChangeType.Modified)
                {
                    for (int j = Math.Max(0, i - ContextLines); j <= Math.Min(pieces.Count - 1, i + ContextLines); j++)
                    {
                        keep[j] = true;
                    }
                }
            }

            bool anyChange = keep.Any(k => k);
            if (!anyChange)
            {
                return;
            }

            bool gap = false;

            for (int i = 0; i < pieces.Count; i++)
            {
                if (!keep[i])
                {
                    gap = true;
                    continue;
                }

                if (gap && target.Count > 0)
                {
                    target.Add(new ChatDiffLine { Kind = ChatDiffKind.Separator, Marker = string.Empty, Text = "⋯" });
                }
                gap = false;

                if (target.Count >= MaxDiffLines)
                {
                    target.Add(new ChatDiffLine
                    {
                        Kind = ChatDiffKind.Separator,
                        Marker = string.Empty,
                        Text = "⋯ diff truncated"
                    });
                    break;
                }

                DiffPiece piece = pieces[i];
                string text = piece.Text ?? string.Empty;

                switch (piece.Type)
                {
                    case ChangeType.Inserted:
                        added++;
                        target.Add(new ChatDiffLine { Kind = ChatDiffKind.Added, Marker = "+", Text = text });
                        break;

                    case ChangeType.Deleted:
                        removed++;
                        target.Add(new ChatDiffLine { Kind = ChatDiffKind.Removed, Marker = "-", Text = text });
                        break;

                    case ChangeType.Modified:
                        added++;
                        removed++;
                        target.Add(new ChatDiffLine { Kind = ChatDiffKind.Removed, Marker = "-", Text = text });
                        target.Add(new ChatDiffLine { Kind = ChatDiffKind.Added, Marker = "+", Text = text });
                        break;

                    default:
                        target.Add(new ChatDiffLine { Kind = ChatDiffKind.Context, Marker = " ", Text = text });
                        break;
                }
            }
        }

        private static string FormatChangeCount(int added, int removed)
        {
            if (added == 0 && removed == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (added > 0) parts.Add("+" + added);
            if (removed > 0) parts.Add("-" + removed);

            return string.Join(" ", parts);
        }

        #endregion

        #region Payload helpers

        private static void ApplyTodos(ChatToolPresentation result, JObject input)
        {
            JArray todos = input?["todos"] as JArray;
            if (todos == null || todos.Count == 0)
            {
                result.Subtitle = "Task list updated";
                return;
            }

            int done = 0;
            string active = string.Empty;

            foreach (JToken token in todos)
            {
                var todo = token as JObject;
                if (todo == null) continue;

                string status = GetString(todo, "status");
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    done++;
                }
                else if (string.IsNullOrEmpty(active) &&
                         string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
                {
                    active = GetString(todo, "activeForm", "content");
                }
            }

            result.Subtitle = string.IsNullOrEmpty(active) ? "Task list updated" : Flatten(active);
            result.Badge = done + "/" + todos.Count;
        }

        /// <summary>Read's optional window, so a partial read does not look like a whole-file read.</summary>
        private static string FormatReadRange(JObject input)
        {
            string offset = GetString(input, "offset");
            string limit = GetString(input, "limit");

            if (string.IsNullOrEmpty(offset) && string.IsNullOrEmpty(limit))
            {
                return string.Empty;
            }

            int start;
            int count;
            if (!int.TryParse(offset, out start)) start = 1;
            if (!int.TryParse(limit, out count) || count <= 0) return "from " + start;

            return "lines " + start + "-" + (start + count - 1);
        }

        private static JObject TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JObject.Parse(json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetString(JObject input, params string[] names)
        {
            if (input == null)
            {
                return string.Empty;
            }

            foreach (string name in names)
            {
                JToken token;
                if (input.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out token) &&
                    token != null && token.Type != JTokenType.Null)
                {
                    return token.Type == JTokenType.String ? token.Value<string>() ?? string.Empty : token.ToString();
                }
            }

            return string.Empty;
        }

        private static string JoinNonEmpty(string separator, string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? string.Empty;
            if (string.IsNullOrEmpty(second)) return first;

            return first + separator + second;
        }

        /// <summary>
        /// Collapses a multi-line value onto the single line the collapsed header has room for.
        /// </summary>
        public static string Flatten(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            bool lastWasSpace = false;

            foreach (char c in value)
            {
                bool isSpace = char.IsWhiteSpace(c);
                if (isSpace)
                {
                    if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                }
                else
                {
                    builder.Append(c);
                }
                lastWasSpace = isSpace;
            }

            string flat = builder.ToString().Trim();

            return flat.Length <= MaxSubtitleLength ? flat : flat.Substring(0, MaxSubtitleLength - 1) + "…";
        }

        /// <summary>
        /// Keeps the last few path segments. A full absolute path pushes the interesting end of the
        /// name off the row, and the workspace prefix is the same on every card anyway.
        /// </summary>
        public static string ShortenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim().Replace('/', '\\');
            string[] parts = trimmed.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length <= 3)
            {
                return trimmed;
            }

            return "…\\" + string.Join("\\", parts.Skip(parts.Length - 3));
        }

        #endregion
    }
}
