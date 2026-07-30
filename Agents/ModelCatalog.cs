/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Parses the model lists the provider CLIs print, so the model menu can offer real models
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// One entry of a provider's model list: the id passed to the CLI and the caption shown to the
    /// user. Persisted inside the settings file as part of the model cache, so it needs setters.
    /// </summary>
    public class ModelOption
    {
        /// <summary>Value handed to the CLI (<c>--model</c>, <c>-m</c>, ACP config option).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Caption shown in the model menu. Falls back to the id when the CLI prints no name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The caption, never empty — menus bind to this.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Id : Name; }
        }

        public override string ToString() { return DisplayName; }
    }

    /// <summary>
    /// A family of models shown as one submenu, or — when <see cref="Name"/> is empty — entries that
    /// belong at the top level of the menu.
    /// </summary>
    public class ModelGroup
    {
        /// <summary>Submenu caption, empty for entries shown directly in the menu.</summary>
        public string Name { get; set; } = string.Empty;

        public List<ModelOption> Models { get; set; } = new List<ModelOption>();

        public bool IsSubmenu
        {
            get { return !string.IsNullOrEmpty(Name); }
        }
    }

    /// <summary>
    /// Splits a model list into the submenus the model menu shows. Short lists stay flat; a long one
    /// is grouped by model family, because <c>cursor-agent</c> lists 193 models and a flat menu of
    /// that length cannot be used.
    /// </summary>
    public static class ModelCatalogGrouping
    {
        /// <summary>A list longer than this is broken into submenus.</summary>
        public const int FlatListLimit = 25;

        public static List<ModelGroup> Group(IEnumerable<ModelOption> models)
        {
            var all = new List<ModelOption>();
            if (models != null) all.AddRange(models);

            var grouped = new List<ModelGroup>();

            if (all.Count <= FlatListLimit)
            {
                if (all.Count > 0) grouped.Add(new ModelGroup { Models = all });
                return grouped;
            }

            var byKey = new Dictionary<string, ModelGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (ModelOption model in all)
            {
                string key = GetGroupKey(model.Id);

                ModelGroup group;
                if (!byKey.TryGetValue(key, out group))
                {
                    group = new ModelGroup { Name = key };
                    byKey[key] = group;

                    // Kept in the order the CLI printed them, which is the order it considers useful.
                    grouped.Add(group);
                }

                group.Models.Add(model);
            }

            // A family with a single model reads better as a plain entry than as a submenu of one.
            foreach (ModelGroup group in grouped)
            {
                if (group.Models.Count == 1) group.Name = string.Empty;
            }

            return grouped;
        }

        /// <summary>
        /// The family an id belongs to: the provider half of the "provider/model" ids PI and Open Code
        /// print, and the first two dash-separated segments otherwise. Two rather than one, measured
        /// against cursor-agent's list: one segment puts 88 of its 193 models under "claude", two
        /// split them into "claude-opus", "claude-sonnet" and "claude-fable".
        /// </summary>
        public static string GetGroupKey(string id)
        {
            string value = (id ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;

            int slash = value.IndexOf('/');
            if (slash > 0) return value.Substring(0, slash);

            int first = value.IndexOf('-');
            if (first <= 0) return value;

            int second = value.IndexOf('-', first + 1);

            return second < 0 ? value : value.Substring(0, second);
        }
    }

    /// <summary>
    /// Turns the output of the CLIs' model-listing commands into <see cref="ModelOption"/> lists.
    /// <para>
    /// Pure and side-effect free on purpose: the commands themselves were measured once per CLI
    /// (<c>codex debug models</c>, <c>cursor-agent --list-models</c>, <c>pi --list-models</c>,
    /// <c>agy models</c>, <c>opencode models</c>) and what is left is text handling, which is what
    /// the unit suite covers.
    /// </para>
    /// </summary>
    public static class ModelCatalogParsers
    {
        /// <summary>Colour/cursor escapes a CLI writes when it does not detect a redirected stdout.</summary>
        private static readonly Regex AnsiEscape = new Regex(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

        /// <summary>"id - Display Name", the shape of every <c>cursor-agent --list-models</c> entry.</summary>
        private static readonly Regex IdDashName =
            new Regex(@"^(?<id>[^\s]+)\s+-\s+(?<name>.+?)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// The state marker Cursor appends to whichever model is active — "Auto (current, default)".
        /// Only those two words are stripped: the parentheses in the names themselves are meaningful
        /// ("Fable 5 1M (NO ZDR)") and a marker that goes stale the moment the model changes would be
        /// baked into the cached caption.
        /// </summary>
        private static readonly Regex StateMarker = new Regex(
            @"\s*\((?:current|default)(?:\s*,\s*(?:current|default))*\)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Codex's <c>debug models</c> JSON catalog. Only the models Codex itself lists are kept:
        /// the catalog also carries hidden/deprecated slugs that its own picker does not show.
        /// </summary>
        public static List<ModelOption> ParseCodexCatalog(string json)
        {
            var models = new List<ModelOption>();
            if (string.IsNullOrWhiteSpace(json)) return models;

            try
            {
                JToken root = JToken.Parse(json);
                var entries = root["models"] as JArray;
                if (entries == null) return models;

                foreach (JToken entry in entries)
                {
                    string slug = entry?["slug"]?.ToString();
                    if (string.IsNullOrWhiteSpace(slug)) continue;

                    string visibility = entry["visibility"]?.ToString();
                    if (!string.IsNullOrEmpty(visibility) &&
                        !string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Add(models, slug, entry["display_name"]?.ToString());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Model catalog: could not parse the Codex catalog: {ex.Message}");
            }

            return models;
        }

        /// <summary>
        /// <c>cursor-agent --list-models</c>: an "Available models" banner followed by
        /// "id - Display Name" lines. Anything that does not have that shape is a banner line.
        /// </summary>
        public static List<ModelOption> ParseIdDashNameList(string text)
        {
            var models = new List<ModelOption>();

            foreach (string line in SplitLines(text))
            {
                Match match = IdDashName.Match(line);
                if (!match.Success) continue;

                Add(models, match.Groups["id"].Value, StateMarker.Replace(match.Groups["name"].Value, string.Empty));
            }

            return models;
        }

        /// <summary>
        /// <c>pi --list-models</c>: a column-aligned table whose first two columns are the provider
        /// and the model. PI takes them back as one "provider/id" pattern, which is also what makes
        /// the menu unambiguous when two providers ship the same model name.
        /// </summary>
        public static List<ModelOption> ParsePiModelList(string text)
        {
            var models = new List<ModelOption>();

            foreach (string line in SplitLines(text))
            {
                string[] columns = Regex.Split(line.Trim(), @"\s{2,}|\t+");
                if (columns.Length < 2) continue;

                string provider = columns[0].Trim();
                string model = columns[1].Trim();
                if (provider.Length == 0 || model.Length == 0) continue;

                // The header row, and anything else that is prose rather than a table row.
                if (provider.IndexOf(' ') >= 0 || model.IndexOf(' ') >= 0) continue;
                if (string.Equals(provider, "provider", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(model, "model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(models, provider + "/" + model, null);
            }

            return models;
        }

        /// <summary>
        /// <c>agy models</c> and <c>opencode models</c>: one bare id per line. Lines carrying spaces
        /// are banners or warnings — no CLI prints a model id with a space in it.
        /// </summary>
        public static List<ModelOption> ParsePlainList(string text)
        {
            var models = new List<ModelOption>();

            foreach (string line in SplitLines(text))
            {
                string id = line.Trim();
                if (id.Length == 0 || id.IndexOf(' ') >= 0) continue;
                if (!char.IsLetterOrDigit(id[0])) continue;

                Add(models, id, null);
            }

            return models;
        }

        /// <summary>Splits into lines with the colour escapes and stray carriage returns removed.</summary>
        private static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            foreach (string raw in AnsiEscape.Replace(text, string.Empty).Split('\n'))
            {
                yield return raw.Replace("\r", string.Empty);
            }
        }

        /// <summary>Appends unless the id is already there — WSL profiles can echo a list twice.</summary>
        private static void Add(List<ModelOption> models, string id, string name)
        {
            string trimmedId = (id ?? string.Empty).Trim();
            if (trimmedId.Length == 0) return;

            foreach (ModelOption existing in models)
            {
                if (string.Equals(existing.Id, trimmedId, StringComparison.OrdinalIgnoreCase)) return;
            }

            models.Add(new ModelOption
            {
                Id = trimmedId,
                Name = string.IsNullOrWhiteSpace(name) ? trimmedId : name.Trim()
            });
        }
    }
}
