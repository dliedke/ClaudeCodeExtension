/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Chat UI localization. The English text in the code is the key (L("Turn interrupted.")); translations
 *          come from lang/<culture>.json embedded in the assembly, overridable/extendable by files in
 *          %LocalAppData%\ClaudeCodeExtension\lang. A missing entry falls back to the English text.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Translates the native chat's UI text. Call sites keep the English text and wrap it:
    /// <c>L("Stop")</c>, <c>L("Done in {0}", duration)</c>; XAML uses <c>{local:Loc 'Stop'}</c>.
    /// Import with <c>using static ClaudeCodeVS.UI.Loc;</c>.
    /// </summary>
    public static class Loc
    {
        /// <summary>Setting value that follows the Visual Studio UI language.</summary>
        public const string AutomaticLanguage = "auto";

        /// <summary>The language of the text in the code.</summary>
        public const string EnglishLanguage = "en";

        private const string EmbeddedPrefix = "ClaudeCodeExtension.lang.";

        private static Dictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);
        private static CultureInfo _culture = CultureInfo.InvariantCulture;

        /// <summary>The folder users can drop extra or corrected <c>&lt;culture&gt;.json</c> files into.</summary>
        public static string UserLanguageFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCodeExtension", "lang");
            }
        }

        /// <summary>The active language code ("en", "it", "pt-BR"...).</summary>
        public static string Language { get; private set; } = EnglishLanguage;

        /// <summary>Culture for numbers and dates in the translated text. Invariant for English.</summary>
        public static CultureInfo Culture { get { return _culture; } }

        /// <summary>The translation of <paramref name="english"/>, or the English text itself.</summary>
        public static string L(string english)
        {
            if (string.IsNullOrEmpty(english))
            {
                return english ?? string.Empty;
            }

            return _strings.TryGetValue(english, out string translated) && !string.IsNullOrEmpty(translated)
                ? translated
                : english;
        }

        /// <summary>
        /// Marks English text for translation without translating it yet — for lists built once (static
        /// fields) and passed through <see cref="L(string)"/> when shown. Lets the coverage test see the key.
        /// </summary>
        public static string N(string english)
        {
            return english;
        }

        /// <summary>
        /// <see cref="L(string)"/> of a format string, filled with <paramref name="args"/> in the active
        /// culture. A translation with broken placeholders falls back to the English format.
        /// </summary>
        public static string L(string englishFormat, params object[] args)
        {
            string format = L(englishFormat);

            try
            {
                return string.Format(_culture, format, args);
            }
            catch (FormatException)
            {
                return string.Format(_culture, englishFormat, args);
            }
        }

        /// <summary>
        /// Switches the language. <paramref name="setting"/> is a culture code, <see cref="AutomaticLanguage"/>
        /// (the Visual Studio UI language) or empty (same as automatic).
        /// </summary>
        public static void SetLanguage(string setting)
        {
            string code = ResolveLanguage(setting, CultureInfo.CurrentUICulture);
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!IsEnglish(code))
            {
                string json = ReadEmbedded(code);
                if (json != null) Merge(strings, json);

                string userFile = FindUserFile(code);
                if (userFile != null)
                {
                    try { Merge(strings, File.ReadAllText(userFile)); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Language file '{userFile}' skipped: {ex.Message}"); }
                }
            }

            Apply(code, strings);
        }

        /// <summary>Loads a language straight from JSON text. For the tests.</summary>
        internal static void SetLanguageFromJson(string code, string json)
        {
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(json)) Merge(strings, json);
            Apply(code, strings);
        }

        /// <summary>
        /// The language code a setting stands for. Automatic takes <paramref name="uiCulture"/>'s full name
        /// when a file exists for it, else its two-letter language ("it-IT" → "it").
        /// </summary>
        public static string ResolveLanguage(string setting, CultureInfo uiCulture)
        {
            if (!string.IsNullOrWhiteSpace(setting) &&
                !string.Equals(setting, AutomaticLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return setting.Trim();
            }

            if (uiCulture == null || string.IsNullOrEmpty(uiCulture.Name))
            {
                return EnglishLanguage;
            }

            IList<string> available = AvailableLanguages();
            string match = available.FirstOrDefault(l => string.Equals(l, uiCulture.Name, StringComparison.OrdinalIgnoreCase));
            return match ?? uiCulture.TwoLetterISOLanguageName;
        }

        /// <summary>Every language with a translation file: embedded ones plus the user folder's.</summary>
        public static IList<string> AvailableLanguages()
        {
            var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in typeof(Loc).Assembly.GetManifestResourceNames())
            {
                if (name.StartsWith(EmbeddedPrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    codes.Add(name.Substring(EmbeddedPrefix.Length, name.Length - EmbeddedPrefix.Length - ".json".Length));
                }
            }

            try
            {
                if (Directory.Exists(UserLanguageFolder))
                {
                    foreach (string file in Directory.GetFiles(UserLanguageFolder, "*.json"))
                    {
                        codes.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Language folder scan failed: {ex.Message}");
            }

            return codes.ToList();
        }

        /// <summary>A language's own name for itself ("Italiano"), or the code when unknown.</summary>
        public static string DisplayName(string code)
        {
            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(code);
                string name = culture.NativeName;
                return name.Length > 0 ? char.ToUpper(name[0], culture) + name.Substring(1) : code;
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }

        private static void Apply(string code, Dictionary<string, string> strings)
        {
            Language = string.IsNullOrEmpty(code) ? EnglishLanguage : code;
            _strings = strings;

            try
            {
                _culture = IsEnglish(Language) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(Language);
            }
            catch (CultureNotFoundException)
            {
                _culture = CultureInfo.InvariantCulture;
            }
        }

        private static bool IsEnglish(string code)
        {
            return string.IsNullOrEmpty(code) || code.StartsWith(EnglishLanguage, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Adds the "English": "translation" pairs of <paramref name="json"/>; later files win.</summary>
        private static void Merge(Dictionary<string, string> strings, string json)
        {
            foreach (JProperty property in JObject.Parse(json).Properties())
            {
                if (property.Value.Type == JTokenType.String)
                {
                    strings[property.Name] = (string)property.Value;
                }
            }
        }

        private static string ReadEmbedded(string code)
        {
            Assembly assembly = typeof(Loc).Assembly;
            string resource = assembly.GetManifestResourceNames().FirstOrDefault(n =>
                string.Equals(n, EmbeddedPrefix + code + ".json", StringComparison.OrdinalIgnoreCase));
            if (resource == null)
            {
                return null;
            }

            using (Stream stream = assembly.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static string FindUserFile(string code)
        {
            try
            {
                string file = Path.Combine(UserLanguageFolder, code + ".json");
                return File.Exists(file) ? file : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
