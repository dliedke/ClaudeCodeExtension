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
using System.Globalization;
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

        /// <summary>
        /// Submenu this model belongs to, when the CLI names its own families (Devin prints
        /// "Claude Opus 5", "SWE-1.6 Fast"). Empty leaves the grouping to
        /// <see cref="ModelCatalogGrouping.GetGroupKey"/>, which derives it from the id.
        /// </summary>
        public string Group { get; set; } = string.Empty;

        /// <summary>How expensive the CLI says this model is ("High cost", "Free"). Empty when unknown.</summary>
        public string CostTier { get; set; } = string.Empty;

        /// <summary>Context window in tokens, 0 when the CLI does not report one.</summary>
        public int ContextTokens { get; set; }

        /// <summary>
        /// The per-1M prices the CLI reports ("$5 / 1M Input · $0.5 / 1M Cached input · $25 / 1M
        /// Output"). Shown in the picker's details pane; empty when the CLI reports none.
        /// </summary>
        public string CostSummary { get; set; } = string.Empty;

        /// <summary>One-line description of what the model is for. Devin reports one for Adaptive.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>The CLI's own "new" marker.</summary>
        public bool IsNew { get; set; }

        /// <summary>The CLI's own "beta" marker.</summary>
        public bool IsBeta { get; set; }

        /// <summary>The caption, never empty — menus bind to this.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Id : Name; }
        }

        /// <summary>
        /// The menu entry: the caption followed by whatever the CLI reported about the model —
        /// "Grok 4.6 Low — 500K · Med cost · New · Beta". Kept out of <see cref="DisplayName"/>,
        /// which is also the composer's button caption and the text of the "Model switched to …"
        /// notice, where the details would only be noise. Falls back to the bare caption for the
        /// agents that report none (every CLI except Devin, today).
        /// </summary>
        public string BuildMenuCaption()
        {
            var details = new List<string>();

            string context = FormatContextWindow(ContextTokens);
            if (context.Length > 0) details.Add(context);

            if (!string.IsNullOrWhiteSpace(CostTier)) details.Add(CostTier.Trim());
            if (IsNew) details.Add("New");
            if (IsBeta) details.Add("Beta");

            return details.Count == 0
                ? DisplayName
                : DisplayName + " — " + string.Join(" · ", details);
        }

        /// <summary>"1M context" for the details pane, empty when the CLI reports no window.</summary>
        public string ContextWindowLabel
        {
            get
            {
                string context = FormatContextWindow(ContextTokens);
                return context.Length == 0 ? string.Empty : context + " context";
            }
        }

        /// <summary>"1M", "500K", "272K" — Devin reports 1047576 and 1048576 for what it shows as 1M.</summary>
        public static string FormatContextWindow(int tokens)
        {
            if (tokens <= 0) return string.Empty;

            if (tokens >= 1000000)
            {
                double millions = Math.Round(tokens / 1000000.0, 1);
                return millions.ToString("0.#", CultureInfo.InvariantCulture) + "M";
            }

            if (tokens >= 1000)
            {
                return Math.Round(tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "K";
            }

            return tokens.ToString(CultureInfo.InvariantCulture);
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
                // A family the CLI named itself beats anything derived from the id: Devin's own
                // "Claude Opus 4.8" and "Claude Opus 5" both reduce to "claude-opus" otherwise, and
                // its 112 models would collapse into a handful of huge submenus.
                string key = string.IsNullOrWhiteSpace(model.Group)
                    ? GetGroupKey(model.Id)
                    : model.Group.Trim();

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
        /// <c>devin models list --format json</c>'s <c>families</c> array — the models the user's own
        /// account is entitled to, which is why nothing hard-coded can be right here (the list was a
        /// hand-maintained setting until v161.0 and went stale the moment Devin shipped a model).
        /// <para>
        /// The id is <c>model_uid</c>: it is what <c>devin --model</c> resolves and, verified against
        /// <c>devin acp</c>, exactly the <c>value</c> the ACP model picker publishes — including the
        /// internal-looking ones (<c>MODEL_PRIVATE_11</c>) that no fuzzy name matches. The family
        /// label rides along as <see cref="ModelOption.Group"/> so the menu can use Devin's own
        /// grouping rather than one guessed from the id.
        /// </para>
        /// </summary>
        public static List<ModelOption> ParseDevinCatalog(string json)
        {
            var models = new List<ModelOption>();
            if (string.IsNullOrWhiteSpace(json)) return models;

            try
            {
                // An update notice can precede the JSON, the same way it can for Reasonix.
                int start = json.IndexOf('{');
                if (start < 0) return models;

                JToken root = JToken.Parse(json.Substring(start));
                var families = root["families"] as JArray;
                if (families == null) return models;

                foreach (JToken family in families)
                {
                    string label = family?["family_label"]?.ToString();
                    var variants = family?["variants"] as JArray;
                    if (variants == null) continue;

                    foreach (JToken variant in variants)
                    {
                        int before = models.Count;
                        Add(models, variant?["model_uid"]?.ToString(), variant?["label"]?.ToString(), label);
                        if (models.Count == before) continue;

                        ModelOption added = models[models.Count - 1];
                        added.CostTier = variant["cost_tier"]?.ToString() ?? string.Empty;
                        added.CostSummary = variant["cost_summary"]?.ToString() ?? string.Empty;
                        added.Description = variant["description"]?.ToString() ?? string.Empty;
                        added.ContextTokens = ReadInt(variant["max_context_tokens"]);
                        added.IsNew = ReadBool(variant["is_new"]);
                        added.IsBeta = ReadBool(variant["is_beta"]);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Model catalog: could not parse the Devin catalog: {ex.Message}");
            }

            return models;
        }

        /// <summary>
        /// <c>reasonix doctor --json</c>'s <c>providers</c> array. Reasonix's <c>--model</c> takes a
        /// **provider name** — the <c>name</c> of a <c>[[providers]]</c> block in the user's
        /// <c>config.toml</c> — not a model id, so `providers[].name` is the only valid set of values
        /// and it differs per machine: a name the user has not configured is rejected with
        /// <c>session/new: unknown model "…"</c>. That is why nothing hard-coded can be right here, and
        /// why a hard-coded list (`deepseek-chat` and friends, which are model ids) broke Reasonix
        /// native mode outright.
        ///
        /// The models the provider serves are shown as the caption, since the name alone
        /// ("deepseek-flash") says nothing about what actually answers. A provider whose API key is
        /// missing is still listed — `key_present` reflects an environment variable the user can set
        /// without touching the config, and hiding the entry would leave no way to pick it.
        /// </summary>
        public static List<ModelOption> ParseReasonixProviders(string json)
        {
            var models = new List<ModelOption>();
            if (string.IsNullOrWhiteSpace(json)) return models;

            try
            {
                // doctor prints one JSON object, but warnings or an update notice can precede it.
                int start = json.IndexOf('{');
                if (start < 0) return models;

                JToken root = JToken.Parse(json.Substring(start));
                var entries = root["providers"] as JArray;
                if (entries == null) return models;

                foreach (JToken entry in entries)
                {
                    string name = entry?["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    Add(models, name, BuildReasonixCaption(name, entry["models"] as JArray));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Model catalog: could not parse the Reasonix providers: {ex.Message}");
            }

            return models;
        }

        /// <summary>"deepseek-flash — deepseek-v4-flash", or just the name when it serves no models.</summary>
        private static string BuildReasonixCaption(string name, JArray servedModels)
        {
            if (servedModels == null || servedModels.Count == 0) return name;

            var served = new List<string>();
            foreach (JToken model in servedModels)
            {
                string id = model?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) served.Add(id.Trim());
            }

            return served.Count == 0 ? name : name + " — " + string.Join(", ", served);
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

        private static int ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;

            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static bool ReadBool(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return false;

            bool value;
            return bool.TryParse(token.ToString(), out value) && value;
        }

        /// <summary>Appends unless the id is already there — WSL profiles can echo a list twice.</summary>
        private static void Add(List<ModelOption> models, string id, string name, string group = null)
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
                Name = string.IsNullOrWhiteSpace(name) ? trimmedId : name.Trim(),
                Group = (group ?? string.Empty).Trim()
            });
        }
    }

    /// <summary>
    /// One block of the model picker: a caption and the models under it. A section with an empty
    /// <see cref="Name"/> is drawn without a header, which is how the pinned entries at the very top
    /// (Adaptive) reach the list.
    /// </summary>
    public class ModelPickerSection
    {
        public string Name { get; set; } = string.Empty;

        public List<ModelOption> Models { get; set; } = new List<ModelOption>();

        public bool HasHeader
        {
            get { return !string.IsNullOrEmpty(Name); }
        }
    }

    /// <summary>
    /// Lays out the searchable model picker: what it shows, in what order, for a given search text.
    /// Pure so it can be unit-tested without a window — the dialog only turns these sections into
    /// rows. Devin lists 158 models in 31 families, which is why the picker exists at all: the menu
    /// buried every one of them two levels deep with no way to search.
    /// </summary>
    public static class ModelPickerView
    {
        /// <summary>
        /// Devin's own name for the family that picks the model per turn. Pinned at the top of the
        /// picker the way Devin Desktop pins it, rather than sorted in among the model families.
        /// </summary>
        public const string AdaptiveFamily = "Adaptive";

        public static List<ModelPickerSection> Build(
            IEnumerable<ModelOption> models, IEnumerable<string> favoriteIds, string search)
        {
            var sections = new List<ModelPickerSection>();

            var all = new List<ModelOption>();
            if (models != null) all.AddRange(models);
            if (all.Count == 0) return sections;

            string[] terms = SplitSearchTerms(search);
            var matched = new List<ModelOption>();
            foreach (ModelOption model in all)
            {
                if (Matches(model, terms)) matched.Add(model);
            }

            if (matched.Count == 0) return sections;

            var pinned = new ModelPickerSection();
            foreach (ModelOption model in matched)
            {
                if (IsAdaptive(model)) pinned.Models.Add(model);
            }
            if (pinned.Models.Count > 0) sections.Add(pinned);

            // Unlike the old "Recently Used" list this is user-curated, not automatic, so a search
            // still narrows it down instead of hiding it outright — a favorite that does not match
            // what was typed drops out the same way a family entry would.
            if (favoriteIds != null)
            {
                var favorites = new ModelPickerSection { Name = "Favorites" };

                foreach (string id in favoriteIds)
                {
                    ModelOption model = Find(matched, id);
                    if (model == null || IsAdaptive(model)) continue;

                    favorites.Models.Add(model);
                }

                if (favorites.Models.Count > 0) sections.Add(favorites);
            }

            // Families in the order the CLI printed them, which is the order it considers useful —
            // its newest models first. A model that is also pinned or favorited stays here too, so
            // the family reads complete.
            var byFamily = new Dictionary<string, ModelPickerSection>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelOption model in matched)
            {
                if (IsAdaptive(model)) continue;

                string name = string.IsNullOrWhiteSpace(model.Group)
                    ? ModelCatalogGrouping.GetGroupKey(model.Id)
                    : model.Group.Trim();

                ModelPickerSection section;
                if (!byFamily.TryGetValue(name, out section))
                {
                    section = new ModelPickerSection { Name = name };
                    byFamily[name] = section;
                    sections.Add(section);
                }

                section.Models.Add(model);
            }

            return sections;
        }

        /// <summary>Whether <paramref name="modelId"/> is in the favorites list, case-insensitively.</summary>
        public static bool IsFavorite(IEnumerable<string> favoriteIds, string modelId)
        {
            if (favoriteIds == null || string.IsNullOrWhiteSpace(modelId)) return false;

            foreach (string id in favoriteIds)
            {
                if (string.Equals((id ?? string.Empty).Trim(), modelId, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Adds or removes <paramref name="modelId"/> from the favorites list — a star click toggles,
        /// it does not just add. A newly-starred model goes to the front, so the most recently
        /// favorited model leads the "Favorites" section the same way it did as "Recently Used".
        /// </summary>
        public static List<string> ToggleFavorite(IEnumerable<string> favoriteIds, string modelId)
        {
            string toggled = (modelId ?? string.Empty).Trim();
            var updated = new List<string>();
            bool removed = false;

            if (favoriteIds != null)
            {
                foreach (string id in favoriteIds)
                {
                    string existing = (id ?? string.Empty).Trim();
                    if (existing.Length == 0) continue;

                    if (string.Equals(existing, toggled, StringComparison.OrdinalIgnoreCase))
                    {
                        removed = true;
                        continue;
                    }

                    updated.Add(existing);
                }
            }

            if (!removed && toggled.Length > 0) updated.Insert(0, toggled);

            return updated;
        }

        /// <summary>
        /// Every term has to match somewhere in the caption, the family or the id, so "opus high"
        /// finds "Claude Opus 5 High" no matter which order the words are typed.
        /// </summary>
        private static bool Matches(ModelOption model, string[] terms)
        {
            if (terms.Length == 0) return true;

            string haystack = (model.DisplayName + " " + model.Group + " " + model.Id).ToLowerInvariant();

            foreach (string term in terms)
            {
                if (haystack.IndexOf(term, StringComparison.Ordinal) < 0) return false;
            }

            return true;
        }

        private static string[] SplitSearchTerms(string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return new string[0];

            return search.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsAdaptive(ModelOption model)
        {
            return string.Equals(model.Group, AdaptiveFamily, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.Id, "adaptive", StringComparison.OrdinalIgnoreCase);
        }

        private static ModelOption Find(List<ModelOption> models, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;

            foreach (ModelOption model in models)
            {
                if (string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)) return model;
            }

            return null;
        }
    }
}
