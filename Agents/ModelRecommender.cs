/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Recommend AI Model" — the rubric, JSON schema and request sent to the advisor model, and the
 *          parser for its answer. Pure, so the whole contract is unit-tested without a CLI.
 *
 * *******************************************************************************************************************/

using System;
using System.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>The advisor's answer: a Claude model alias, an effort level and a one-sentence reason.</summary>
    public sealed class ModelRecommendation
    {
        /// <summary>One of <see cref="ModelRecommender.Models"/>.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>One of <see cref="ModelRecommender.Efforts"/>.</summary>
        public string Effort { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Everything "Recommend AI Model" sends to the CLI and reads back. The command line itself is
    /// built by <see cref="ClaudeCommandBuilder.GetModelRecommendationArguments"/>.
    /// </summary>
    public static class ModelRecommender
    {
        /// <summary>The model that gives the advice.</summary>
        public const string AdvisorModel = "opus";

        /// <summary>The effort the advice is given at.</summary>
        public const string AdvisorEffort = "xhigh";

        /// <summary>
        /// Prompts longer than this are cut before they are sent: a pasted log says no more about the
        /// size of the task at 200 KB than it does at 30 KB, and every character is paid for.
        /// </summary>
        public const int MaxPromptChars = 30000;

        /// <summary>Models the advisor may pick, cheapest first.</summary>
        public static readonly string[] Models = { "haiku", "sonnet", "opus", "fable" };

        /// <summary>
        /// Efforts the advisor may pick, lowest first. Ultracode is left out on purpose: it is a
        /// workflow mode, not an amount of thinking, and the user can still choose it in the dialog.
        /// </summary>
        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };

        /// <summary>
        /// The rubric, passed as <c>--system-prompt</c>. It has to survive every command line it can
        /// be put on, which is why it is a single line with no <c>&amp; | &lt; &gt; ^ % !</c>, double
        /// quotes, backslashes, dollar signs or backticks: an npm <c>claude.cmd</c> shim re-parses the
        /// arguments through cmd.exe (a newline ends the command there, <c>%</c> expands even inside
        /// quotes), and the WSL path nests it inside <c>bash -lic</c>. Guarded by a unit test.
        /// </summary>
        public static readonly string SystemPrompt =
            "You recommend which Claude model and reasoning effort Claude Code should use for the coding task the user describes. " +
            "Do not perform the task, do not ask questions and do not use tools: answer only with the JSON object. " +
            "Models, from cheapest and fastest to most capable: " +
            "haiku for trivial mechanical edits such as a rename, a typo, a one-line change or a text tweak; " +
            "sonnet for routine, well-scoped work such as a small feature, a focused bug fix, tests or a refactor confined to a few files; " +
            "opus for complex work such as features spanning many files, hard debugging, concurrency, security, performance or design decisions; " +
            "fable only for the most demanding long-horizon work such as large migrations, porting or rewriting a whole application, or deep architecture changes. " +
            "Effort sets how much the model thinks: low for mechanical changes, medium for routine work, high for non-trivial reasoning, " +
            "xhigh for hard problems where mistakes are costly, max only for the hardest problems. " +
            "Pick the cheapest model and the lowest effort that will still do the task reliably and well; when the task is vague or ambiguous, lean one step up. " +
            "Use the project instructions, if there are any, to judge the size and risk of the task. " +
            "The reason is one short sentence a developer can read at a glance.";

        /// <summary>
        /// Passed as <c>--json-schema</c>. The CLI validates the answer against it and returns it as
        /// <c>structured_output</c>, so the enums here are what keeps the model from naming a model
        /// or effort the extension has no entry for.
        /// </summary>
        public static readonly string JsonSchema = BuildJsonSchema();

        private static string BuildJsonSchema()
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["model"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Models) },
                    ["effort"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Efforts) },
                    ["reason"] = new JObject { ["type"] = "string" }
                },
                ["required"] = new JArray("model", "effort", "reason"),
                ["additionalProperties"] = false
            };

            return schema.ToString(Formatting.None);
        }

        /// <summary>
        /// The message written to stdin. The prompt is fenced in tags so an instruction inside it
        /// ("ignore that and do X") reads as the task being sized, not as something to obey.
        /// </summary>
        public static string BuildRequest(string prompt)
        {
            string task = (prompt ?? string.Empty).Trim();
            if (task.Length > MaxPromptChars)
            {
                task = task.Substring(0, MaxPromptChars) + "\n[... truncated]";
            }

            return "Recommend the Claude model and effort for this task.\n\n<task>\n" + task + "\n</task>";
        }

        /// <summary>
        /// Reads the CLI's <c>--output-format json</c> output. Returns null with <paramref name="error"/>
        /// set when there is no result, the CLI reported an error (not logged in, quota) or the answer
        /// names something outside <see cref="Models"/>/<see cref="Efforts"/>.
        /// <para>
        /// The result is looked for line by line from the end: under WSL a login shell's profile can
        /// print to stdout before the CLI does. <c>structured_output</c> is the validated answer; the
        /// <c>result</c> text is only a fallback for a CLI that ignores <c>--json-schema</c>.
        /// </para>
        /// </summary>
        public static ModelRecommendation ParseOutput(string output, out string error)
        {
            error = null;

            JObject envelope = FindResultEnvelope(output);
            if (envelope == null)
            {
                error = "Claude Code returned no answer.";
                return null;
            }

            if (envelope.Value<bool?>("is_error") == true)
            {
                string detail = (envelope.Value<string>("result") ?? string.Empty).Trim();
                error = detail.Length > 0 ? detail : "Claude Code reported an error.";
                return null;
            }

            JObject answer = envelope["structured_output"] as JObject
                ?? TryParseObject(envelope.Value<string>("result"));
            if (answer == null)
            {
                error = "The answer was not in the expected format.";
                return null;
            }

            string model = NormalizeWord(answer.Value<string>("model"));
            string effort = NormalizeWord(answer.Value<string>("effort"));
            if (effort == "extrahigh")
            {
                effort = "xhigh";
            }

            if (!Models.Contains(model) || !Efforts.Contains(effort))
            {
                error = $"Unexpected recommendation: model \"{model}\", effort \"{effort}\".";
                return null;
            }

            return new ModelRecommendation
            {
                Model = model,
                Effort = effort,
                Reason = (answer.Value<string>("reason") ?? string.Empty).Trim()
            };
        }

        private static JObject FindResultEnvelope(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("{", StringComparison.Ordinal))
                {
                    continue;
                }

                JObject candidate = TryParse(line);
                if (candidate != null && (candidate["structured_output"] != null || candidate["result"] != null))
                {
                    return candidate;
                }
            }

            return TryParse(output.Trim());
        }

        /// <summary>Pulls the outermost <c>{...}</c> out of free text, code fences and prose included.</summary>
        private static JObject TryParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            return start >= 0 && end > start ? TryParse(text.Substring(start, end - start + 1)) : null;
        }

        private static JObject TryParse(string json)
        {
            try
            {
                return JToken.Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>"X-High", "Extra High" and " opus " all reduce to the bare lowercase word.</summary>
        private static string NormalizeWord(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        }
    }
}
