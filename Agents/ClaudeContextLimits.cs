/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Resolves the context window and auto-compact point a Claude Code session should use
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// The context window and auto-compact point a Claude Code session should use.
    /// <para>
    /// The CLI reports the window of the model that answered in each result's <c>modelUsage</c>. For an
    /// Anthropic-compatible custom endpoint whose model it does not recognize (DeepSeek, for example)
    /// it falls back to 200k unless the CLI settings/env say otherwise; the chat's usage ring then
    /// claims the conversation is nearly full long before the real 1M window is, and auto-compact
    /// fires at the wrong point. This resolver honors the CLI's own
    /// <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c>/<c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c> settings first and
    /// supplies DeepSeek's known 1M/75% defaults otherwise.
    /// </para>
    /// </summary>
    public static class ClaudeContextLimits
    {
        /// <summary>Environment variable the CLI reads for a custom model's real context window.</summary>
        public const string MaxContextTokensVariable = "CLAUDE_CODE_MAX_CONTEXT_TOKENS";

        /// <summary>Environment variable the CLI reads for the auto-compact window.</summary>
        public const string AutoCompactWindowVariable = "CLAUDE_CODE_AUTO_COMPACT_WINDOW";

        /// <summary>DeepSeek's context window when the CLI settings do not name one.</summary>
        public const int DeepSeekContextWindow = 1_000_000;

        /// <summary>75% of 1 MiB — the auto-compact point that keeps a 1M DeepSeek session well inside its window.</summary>
        public const int DeepSeekAutoCompactWindow = 786_432;

        private static readonly string[] NoModels = new string[0];

        /// <summary>
        /// The effective values. <see cref="ContextWindow"/> and <see cref="AutoCompactWindow"/> are 0
        /// when the CLI's own reported/default value should be left alone.
        /// </summary>
        public sealed class Resolution
        {
            public Resolution(int contextWindow, int autoCompactWindow, bool isDeepSeek,
                bool contextWindowFromSettings, bool autoCompactWindowFromSettings)
            {
                ContextWindow = contextWindow;
                AutoCompactWindow = autoCompactWindow;
                IsDeepSeek = isDeepSeek;
                ContextWindowFromSettings = contextWindowFromSettings;
                AutoCompactWindowFromSettings = autoCompactWindowFromSettings;
            }

            public int ContextWindow { get; }

            public int AutoCompactWindow { get; }

            public bool IsDeepSeek { get; }

            /// <summary>True when <see cref="ContextWindow"/> came from the CLI settings/env, not the provider default.</summary>
            public bool ContextWindowFromSettings { get; }

            /// <summary>True when <see cref="AutoCompactWindow"/> came from the CLI settings/env, not the provider default.</summary>
            public bool AutoCompactWindowFromSettings { get; }
        }

        /// <summary>
        /// Resolves the values for a launch. <paramref name="environment"/> is the CLI's effective
        /// settings/env (see <see cref="ClaudeCliEnvironment"/>); explicit
        /// <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c>/<c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c> always win over
        /// the provider defaults.
        /// </summary>
        public static Resolution Resolve(string baseUrl, IEnumerable<string> modelIds, IDictionary<string, string> environment)
        {
            environment = environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int configuredContext = ReadPositiveInt(environment, MaxContextTokensVariable);
            int configuredAuto = ReadPositiveInt(environment, AutoCompactWindowVariable);
            bool deepSeek = IsDeepSeek(baseUrl, modelIds);
            bool oneMillion = deepSeek || HasOneMillionSuffix(modelIds);

            int context = configuredContext > 0
                ? configuredContext
                : (oneMillion ? DeepSeekContextWindow : 0);

            // Only supply the DeepSeek trigger when the window itself is also a DeepSeek default: with
            // an explicit smaller window, a 786k trigger would be above the window.
            int auto = configuredAuto > 0
                ? configuredAuto
                : (deepSeek && configuredContext <= 0 ? DeepSeekAutoCompactWindow : 0);

            return new Resolution(context, auto, deepSeek,
                configuredContext > 0, configuredAuto > 0);
        }

        /// <summary>
        /// True when the endpoint or any of the session's model ids names DeepSeek. The base URL is the
        /// strongest signal: a user can point the CLI at api.deepseek.com/anthropic while selecting an
        /// alias such as <c>haiku</c>, which the CLI resolves through the settings' model mappings.
        /// </summary>
        public static bool IsDeepSeek(string baseUrl, IEnumerable<string> modelIds)
        {
            if (ContainsDeepSeek(baseUrl))
            {
                return true;
            }

            foreach (string modelId in modelIds ?? NoModels)
            {
                if (ContainsDeepSeek(modelId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when a model id carries the CLI's <c>[1m]</c> 1M-context suffix.</summary>
        public static bool HasOneMillionSuffix(IEnumerable<string> modelIds)
        {
            foreach (string modelId in modelIds ?? NoModels)
            {
                if (!string.IsNullOrWhiteSpace(modelId) &&
                    modelId.Trim().EndsWith("[1m]", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsDeepSeek(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ReadPositiveInt(IDictionary<string, string> environment, string key)
        {
            if (environment.TryGetValue(key, out string text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                value > 0)
            {
                return value;
            }

            return 0;
        }
    }
}
