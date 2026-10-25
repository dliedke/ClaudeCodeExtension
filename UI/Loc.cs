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

        /// <summary>
        /// The languages shipped with the extension, in the order the Chat language list shows them, with the
        /// name each one gives itself (fixed here: Windows' culture names vary by version and read "Indonesia").
        /// Every one except English has an embedded <c>lang/&lt;code&gt;.json</c>.
        /// </summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> BuiltInLanguages = new[]
        {
            new KeyValuePair<string, string>("en", "English"),
            new KeyValuePair<string, string>("zh-Hans", "简体中文"),
            new KeyValuePair<string, string>("ja", "日本語"),
            new KeyValuePair<string, string>("pt-BR", "Português (Brasil)"),
            new KeyValuePair<string, string>("es", "Español"),
            new KeyValuePair<string, string>("ko", "한국어"),
            new KeyValuePair<string, string>("de", "Deutsch"),
            new KeyValuePair<string, string>("fr", "Français"),
            new KeyValuePair<string, string>("ru", "Русский"),
            new KeyValuePair<string, string>("zh-Hant", "繁體中文"),
            new KeyValuePair<string, string>("id", "Bahasa Indonesia"),
            new KeyValuePair<string, string>("tr", "Türkçe"),
            new KeyValuePair<string, string>("vi", "Tiếng Việt"),
            new KeyValuePair<string, string>("pl", "Polski"),
            new KeyValuePair<string, string>("it", "Italiano"),
            new KeyValuePair<string, string>("uk", "Українська"),
            new KeyValuePair<string, string>("ar", "العربية"),
            new KeyValuePair<string, string>("hi", "हिन्दी"),
            new KeyValuePair<string, string>("th", "ไทย"),
            new KeyValuePair<string, string>("nl", "Nederlands"),
        };

        private static Dictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);
        private static CultureInfo _culture = CultureInfo.InvariantCulture;

        /// <summary>Translation → English of the language before the last switch, for <see cref="Retranslate"/>.</summary>
        private static Dictionary<string, string> _previousToEnglish = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Raised after every <see cref="SetLanguage"/>, on the caller's thread. The XAML captions
        /// (<see cref="LocExtension"/>) follow it on their own; code-built text is refreshed by its owner.
        /// </summary>
        public static event EventHandler LanguageChanged;

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
        /// Text an earlier <see cref="L(string)"/> returned in the language before the last switch, in the
        /// current one — for a caption stored as plain text (a status line) that has to follow a switch.
        /// Formatted text and anything that was never a translation come back unchanged.
        /// </summary>
        public static string Retranslate(string shown)
        {
            if (string.IsNullOrEmpty(shown))
            {
                return shown ?? string.Empty;
            }

            return L(_previousToEnglish.TryGetValue(shown, out string english) ? english : shown);
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
        /// The language code a setting stands for. Automatic takes the first of <paramref name="uiCulture"/>
        /// and its parents that has a file ("zh-TW" → "zh-Hant", "it-IT" → "it"), then another variant of the
        /// same language ("pt-PT" → "pt-BR"), else its two-letter language.
        /// </summary>
        public static string ResolveLanguage(string setting, CultureInfo uiCulture)
        {
            return ResolveLanguage(setting, uiCulture, AvailableLanguages());
        }

        /// <summary><see cref="ResolveLanguage(string, CultureInfo)"/> against a given language list. For the tests.</summary>
        internal static string ResolveLanguage(string setting, CultureInfo uiCulture, IList<string> available)
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

            // .NET Framework chains zh-TW → zh-CHT → zh-Hant → zh, so the script files are found here.
            for (CultureInfo culture = uiCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
            {
                string exact = available.FirstOrDefault(l => string.Equals(l, culture.Name, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            string language = uiCulture.TwoLetterISOLanguageName;
            if (IsEnglish(language))
            {
                return EnglishLanguage;
            }

            return available.FirstOrDefault(l => string.Equals(TwoLetterLanguage(l), language, StringComparison.OrdinalIgnoreCase))
                ?? language;
        }

        /// <summary>
        /// Every language with a translation file: embedded ones plus the user folder's. Built-in languages
        /// come first in <see cref="BuiltInLanguages"/> order, other files after them alphabetically.
        /// </summary>
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

            return codes.OrderBy(BuiltInOrder).ToList();
        }

        /// <summary>A language's own name for itself ("Italiano"), or the code when unknown.</summary>
        public static string DisplayName(string code)
        {
            string builtIn = BuiltInLanguages.FirstOrDefault(l => string.Equals(l.Key, code, StringComparison.OrdinalIgnoreCase)).Value;
            if (builtIn != null)
            {
                return builtIn;
            }

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
            // Two keys can share a translation; the first one wins, which still reads right after the switch.
            var previousToEnglish = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in _strings)
            {
                if (!string.IsNullOrEmpty(pair.Value) && !previousToEnglish.ContainsKey(pair.Value))
                {
                    previousToEnglish.Add(pair.Value, pair.Key);
                }
            }

            _previousToEnglish = previousToEnglish;
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

            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        private static bool IsEnglish(string code)
        {
            return string.IsNullOrEmpty(code) || code.StartsWith(EnglishLanguage, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Position in <see cref="BuiltInLanguages"/>; files of other languages sort after all of them.</summary>
        private static int BuiltInOrder(string code)
        {
            for (int i = 0; i < BuiltInLanguages.Count; i++)
            {
                if (string.Equals(BuiltInLanguages[i].Key, code, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return BuiltInLanguages.Count;
        }

        /// <summary>"pt-BR" → "pt", "zh-Hant" → "zh"; the code's first part when it is no known culture.</summary>
        private static string TwoLetterLanguage(string code)
        {
            try
            {
                return CultureInfo.GetCultureInfo(code).TwoLetterISOLanguageName;
            }
            catch (Exception)
            {
                int dash = code.IndexOf('-');
                return dash > 0 ? code.Substring(0, dash) : code;
            }
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
