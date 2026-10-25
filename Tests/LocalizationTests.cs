/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Chat UI localization: lookup/fallback/format, language resolution, live switching, the built-in
 *          language table against lang/ and the csproj, translation file sanity, and full coverage of every
 *          L(...)/{local:Loc ...} string in every language
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class LocalizationTests
    {
        [TestCleanup]
        public void BackToEnglish()
        {
            Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
        }

        [TestMethod]
        public void L_TranslatesKnownTextAndFallsBackToEnglish()
        {
            Loc.SetLanguageFromJson("it", "{ \"Stop\": \"Interrompi\", \"Empty\": \"\" }");

            Assert.AreEqual("Interrompi", Loc.L("Stop"));
            Assert.AreEqual("Not translated", Loc.L("Not translated"));
            Assert.AreEqual("Empty", Loc.L("Empty"), "An empty translation must not blank the UI.");
            Assert.AreEqual(string.Empty, Loc.L(null));
        }

        [TestMethod]
        public void L_FormatsWithTheLanguageCulture()
        {
            Loc.SetLanguageFromJson("it", "{ \"{0:N0} tokens\": \"{0:N0} token\" }");
            Assert.AreEqual("6.684 token", Loc.L("{0:N0} tokens", 6684));

            Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
            Assert.AreEqual("6,684 tokens", Loc.L("{0:N0} tokens", 6684), "English numbers no longer follow the Windows culture.");
        }

        [TestMethod]
        public void L_BrokenTranslatedFormatFallsBackToEnglish()
        {
            Loc.SetLanguageFromJson("it", "{ \"Done in {0}\": \"Fatto in {1}\" }");
            Assert.AreEqual("Done in 5s", Loc.L("Done in {0}", "5s"));
        }

        [TestMethod]
        public void ResolveLanguage_AutomaticFollowsTheUiCulture()
        {
            Assert.AreEqual("it", Loc.ResolveLanguage(Loc.AutomaticLanguage, new CultureInfo("it-IT")));
            Assert.AreEqual("en", Loc.ResolveLanguage("", new CultureInfo("en-US")));
            Assert.AreEqual("pt-BR", Loc.ResolveLanguage("pt-BR", new CultureInfo("it-IT")));
        }

        [DataTestMethod]
        [DataRow("zh-CN", "zh-Hans")]
        [DataRow("zh-SG", "zh-Hans")]
        [DataRow("zh-Hans", "zh-Hans")]
        [DataRow("zh-TW", "zh-Hant")]
        [DataRow("zh-HK", "zh-Hant")]
        [DataRow("zh-MO", "zh-Hant")]
        [DataRow("ja-JP", "ja")]
        [DataRow("ko-KR", "ko")]
        [DataRow("pt-BR", "pt-BR")]
        [DataRow("pt-PT", "pt-BR")]
        [DataRow("es-ES", "es")]
        [DataRow("es-MX", "es")]
        [DataRow("de-DE", "de")]
        [DataRow("de-AT", "de")]
        [DataRow("fr-FR", "fr")]
        [DataRow("fr-CA", "fr")]
        [DataRow("ru-RU", "ru")]
        [DataRow("id-ID", "id")]
        [DataRow("tr-TR", "tr")]
        [DataRow("vi-VN", "vi")]
        [DataRow("pl-PL", "pl")]
        [DataRow("it-IT", "it")]
        [DataRow("uk-UA", "uk")]
        [DataRow("ar-SA", "ar")]
        [DataRow("ar-EG", "ar")]
        [DataRow("hi-IN", "hi")]
        [DataRow("th-TH", "th")]
        [DataRow("nl-NL", "nl")]
        [DataRow("nl-BE", "nl")]
        [DataRow("en-US", "en")]
        [DataRow("en-GB", "en")]
        [DataRow("cs-CZ", "cs")]
        public void ResolveLanguage_AutomaticPicksTheBuiltInLanguageOfTheUiCulture(string uiCulture, string expected)
        {
            Assert.AreEqual(expected, Loc.ResolveLanguage(Loc.AutomaticLanguage, new CultureInfo(uiCulture), BuiltInCodes()));
        }

        [TestMethod]
        public void ResolveLanguage_AnExplicitChoiceIsKept()
        {
            Assert.AreEqual("ja", Loc.ResolveLanguage("ja", new CultureInfo("de-DE"), BuiltInCodes()));
            Assert.AreEqual("en", Loc.ResolveLanguage("en", new CultureInfo("zh-TW"), BuiltInCodes()));
            Assert.AreEqual("zh-Hant", Loc.ResolveLanguage(" zh-Hant ", new CultureInfo("zh-CN"), BuiltInCodes()));
        }

        [TestMethod]
        public void BuiltInLanguages_StartWithEnglishAndHaveTwentyDistinctValidCultures()
        {
            Assert.AreEqual(20, Loc.BuiltInLanguages.Count);
            Assert.AreEqual(Loc.EnglishLanguage, Loc.BuiltInLanguages[0].Key);
            Assert.AreEqual(Loc.BuiltInLanguages.Count,
                Loc.BuiltInLanguages.Select(l => l.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate language code.");

            foreach (KeyValuePair<string, string> language in Loc.BuiltInLanguages)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(language.Value), $"{language.Key} has no display name.");
                Assert.AreEqual(language.Value, Loc.DisplayName(language.Key));
                Assert.AreEqual(language.Key, CultureInfo.GetCultureInfo(language.Key).Name, true,
                    $"{language.Key} is not the name of a .NET culture, so its numbers and dates would not be localized.");
            }
        }

        [TestMethod]
        public void BuiltInLanguages_MatchTheLangFolderAndTheEmbeddedResources()
        {
            var table = new HashSet<string>(BuiltInCodes().Where(c => c != Loc.EnglishLanguage), StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(
                Directory.GetFiles(RepositoryLayout.Path("lang"), "*.json").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            var embedded = new HashSet<string>(
                Regex.Matches(RepositoryLayout.ReadText("ClaudeCodeExtension.csproj"), @"<EmbeddedResource\s+Include=""lang\\([^""]+)\.json""")
                    .Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.OrdinalIgnoreCase);

            Assert.IsTrue(table.SetEquals(files),
                "lang/ and the built-in language table differ: " + string.Join(", ", table.Except(files).Concat(files.Except(table))));
            Assert.IsTrue(table.SetEquals(embedded),
                "The csproj does not embed exactly the built-in languages: " + string.Join(", ", table.Except(embedded).Concat(embedded.Except(table))));
        }

        [TestMethod]
        public void AvailableLanguages_ListsEveryBuiltInLanguageInTableOrder()
        {
            List<string> available = Loc.AvailableLanguages().ToList();
            List<string> builtIn = BuiltInCodes().Where(c => c != Loc.EnglishLanguage).ToList();

            CollectionAssert.AreEqual(builtIn, available.Take(builtIn.Count).ToList(),
                "Every translation must be embedded, and the list must keep the table's order.");
        }

        [TestMethod]
        public void SetLanguage_LoadsEveryEmbeddedTranslation()
        {
            foreach (string code in BuiltInCodes().Where(c => c != Loc.EnglishLanguage))
            {
                Loc.SetLanguage(code);

                Assert.AreEqual(code, Loc.Language);
                Assert.AreEqual(code, Loc.Culture.Name, true);
                Assert.AreNotEqual("Stop", Loc.L("Stop"), $"{code} did not load its translations.");
                Assert.AreNotEqual("Settings", Loc.L("Settings"), $"{code} did not load its translations.");
            }
        }

        [TestMethod]
        public void SetLanguage_RaisesLanguageChangedAndTheBindingSourceFollows()
        {
            int changes = 0;
            var properties = new List<string>();
            EventHandler onChanged = (s, e) => changes++;
            PropertyChangedEventHandler onProperty = (s, e) => properties.Add(e.PropertyName);

            Loc.LanguageChanged += onChanged;
            LocSource.Instance.PropertyChanged += onProperty;
            try
            {
                Loc.SetLanguageFromJson("it", "{ \"Stop\": \"Interrompi\" }");
                Assert.AreEqual(1, changes);
                Assert.AreEqual("it", LocSource.Instance.Language);

                Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
                Assert.AreEqual(2, changes);
                Assert.AreEqual(Loc.EnglishLanguage, LocSource.Instance.Language);

                CollectionAssert.AreEqual(new[] { nameof(LocSource.Language), nameof(LocSource.Language) }, properties);
            }
            finally
            {
                Loc.LanguageChanged -= onChanged;
                LocSource.Instance.PropertyChanged -= onProperty;
            }
        }

        [TestMethod]
        public void SetLanguage_AFailingListenerDoesNotStopTheOthers()
        {
            int reached = 0;
            EventHandler failing = (s, e) => throw new InvalidOperationException("closed window");
            EventHandler counting = (s, e) => reached++;

            Loc.LanguageChanged += failing;
            Loc.LanguageChanged += counting;
            try
            {
                Loc.SetLanguageFromJson("it", "{ \"Stop\": \"Interrompi\" }");
                Assert.AreEqual(1, reached);
                Assert.AreEqual("it", LocSource.Instance.Language);
            }
            finally
            {
                Loc.LanguageChanged -= failing;
                Loc.LanguageChanged -= counting;
            }
        }

        [TestMethod]
        public void LocTree_RetranslatesAnOpenWindowButLeavesInputAndBindingsAlone()
        {
            Exception failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    const string italian = "{ \"Cancel\": \"Annulla\", \"Stop\": \"Interrompi\", \"Close\": \"Chiudi\" }";
                    const string portuguese = "{ \"Cancel\": \"Cancelar\", \"Stop\": \"Parar\", \"Close\": \"Fechar\" }";
                    Loc.SetLanguageFromJson("it", italian);

                    var label = new System.Windows.Controls.TextBlock { Text = Loc.L("Stop") };
                    var button = new System.Windows.Controls.Button { Content = Loc.L("Cancel"), ToolTip = Loc.L("Close") };
                    var input = new System.Windows.Controls.TextBox { Text = "Annulla" };
                    var tab = new System.Windows.Controls.TabItem { Header = Loc.L("Close"), Content = new System.Windows.Controls.TextBlock { Text = Loc.L("Cancel") } };
                    var tabs = new System.Windows.Controls.TabControl();
                    tabs.Items.Add(tab);
                    var root = new System.Windows.Controls.StackPanel();
                    root.Children.Add(label);
                    root.Children.Add(button);
                    root.Children.Add(input);
                    root.Children.Add(tabs);

                    Loc.SetLanguageFromJson("pt-BR", portuguese);
                    LocTree.Retranslate(root);

                    Assert.AreEqual("Parar", label.Text);
                    Assert.AreEqual("Cancelar", button.Content);
                    Assert.AreEqual("Fechar", button.ToolTip);
                    Assert.AreEqual("Annulla", input.Text, "User input is never touched.");
                    Assert.AreEqual("Fechar", tab.Header);
                    Assert.AreEqual("Cancelar", ((System.Windows.Controls.TextBlock)tab.Content).Text, "Content of a tab that was never shown.");
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw failure;
            }
        }

        [TestMethod]
        public void Retranslate_CarriesShownTextIntoTheNewLanguage()
        {
            Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
            Loc.SetLanguageFromJson("it", "{ \"Stop\": \"Interrompi\", \"Ready.\": \"Pronto.\" }");
            Assert.AreEqual("Interrompi", Loc.Retranslate("Stop"), "English → a translation.");

            string shown = Loc.L("Stop");
            Loc.SetLanguageFromJson("pt-BR", "{ \"Stop\": \"Parar\" }");
            Assert.AreEqual("Parar", Loc.Retranslate(shown), "One translation → another.");
            Assert.AreEqual("Ready.", Loc.Retranslate("Pronto."), "Untranslated in the new language → English.");

            Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
            Assert.AreEqual("Stop", Loc.Retranslate("Parar"), "A translation → English.");
        }

        [TestMethod]
        public void Retranslate_LeavesFormattedAndUnknownTextAlone()
        {
            Loc.SetLanguageFromJson("it", "{ \"Done in {0}\": \"Fatto in {0}\" }");
            Loc.SetLanguageFromJson("pt-BR", "{ \"Done in {0}\": \"Concluído em {0}\" }");

            Assert.AreEqual("Fatto in 5s", Loc.Retranslate("Fatto in 5s"));
            Assert.AreEqual("Something else", Loc.Retranslate("Something else"));
            Assert.AreEqual(string.Empty, Loc.Retranslate(null));
        }

        [TestMethod]
        public void TranslationFiles_AreValidAndKeepTheirPlaceholders()
        {
            var problems = new List<string>();

            foreach (string file in LanguageFiles())
            {
                JObject json = ParseStrict(file);
                foreach (JProperty entry in json.Properties())
                {
                    string translated = (string)entry.Value;
                    if (!Placeholders(entry.Name).SetEquals(Placeholders(translated)))
                    {
                        problems.Add($"{Path.GetFileName(file)}: \"{entry.Name}\" -> \"{translated}\"");
                    }
                }
            }

            Assert.AreEqual(0, problems.Count,
                "A translation must use the same {n} placeholders as its English text:\n" + string.Join("\n", problems));
        }

        /// <summary>
        /// Parts of a string that the code glues to other text or that carry meaning on their own: a leading or
        /// trailing space (" (background)" is appended to a name) and a leading icon ("🤖 ", "■ ").
        /// </summary>
        [TestMethod]
        public void TranslationFiles_HaveNoEmptyValuesAndKeepEdgeSpacesAndIcons()
        {
            var problems = new List<string>();

            foreach (string file in LanguageFiles())
            {
                string name = Path.GetFileName(file);
                foreach (JProperty entry in ParseStrict(file).Properties())
                {
                    string english = entry.Name;
                    string translated = entry.Value.Type == JTokenType.String ? (string)entry.Value : null;

                    if (string.IsNullOrWhiteSpace(translated))
                    {
                        problems.Add($"{name}: \"{english}\" is empty");
                        continue;
                    }

                    if (char.IsWhiteSpace(english[0]) != char.IsWhiteSpace(translated[0]) ||
                        char.IsWhiteSpace(english[english.Length - 1]) != char.IsWhiteSpace(translated[translated.Length - 1]))
                    {
                        problems.Add($"{name}: edge spaces of \"{english}\" -> \"{translated}\"");
                    }

                    string icon = LeadingIcon(english);
                    if (icon != null && !translated.StartsWith(icon, StringComparison.Ordinal))
                    {
                        problems.Add($"{name}: icon of \"{english}\" -> \"{translated}\"");
                    }
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        /// <summary>
        /// Every string wrapped in L(...)/N(...)/{local:Loc ...} must be translated in every lang/*.json, and no
        /// file may keep a translation whose English text is gone from the code. New chat text therefore ships
        /// in all languages at once (see "Localize chat text" in docs/SKILLS.md).
        /// </summary>
        [TestMethod]
        public void TranslationCoverage_EveryStringIsTranslatedInEveryLanguage()
        {
            HashSet<string> keys = CollectKeysFromSource();
            var report = new List<string>();

            Assert.IsTrue(keys.Count > 200, $"Only {keys.Count} strings found in the code; the key scanner is probably broken.");

            foreach (string file in LanguageFiles())
            {
                var translated = new HashSet<string>(ParseStrict(file).Properties().Select(p => p.Name), StringComparer.Ordinal);
                List<string> missing = keys.Where(k => !translated.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                List<string> orphans = translated.Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

                if (missing.Count > 0)
                {
                    report.Add($"{Path.GetFileName(file)}: {missing.Count} untranslated\n  " + string.Join("\n  ", missing));
                }

                if (orphans.Count > 0)
                {
                    report.Add($"{Path.GetFileName(file)}: {orphans.Count} no longer in the code\n  " + string.Join("\n  ", orphans));
                }
            }

            Assert.AreEqual(0, report.Count, "Translation coverage:\n" + string.Join("\n", report));
        }

        // L("a") or N("a"), including a key split over lines as "a " + "b" (the compiler folds it into one).
        private static readonly Regex CodeKey = new Regex(
            @"\b[LN]\(\s*((?:""(?:[^""\\]|\\.)*""\s*\+\s*)*""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);
        private static readonly Regex StringLiteral = new Regex(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex XamlKey = new Regex(@"\{local:Loc\s+'((?:[^'\\]|\\.)*)'\s*\}", RegexOptions.Compiled);

        private static List<string> BuiltInCodes()
        {
            return Loc.BuiltInLanguages.Select(l => l.Key).ToList();
        }

        private static string[] LanguageFiles()
        {
            string[] files = Directory.GetFiles(RepositoryLayout.Path("lang"), "*.json");
            Assert.IsTrue(files.Length > 0, "No translation files found.");
            return files;
        }

        /// <summary>Parses a translation file, failing on a key written twice (the runtime would keep the last one).</summary>
        private static JObject ParseStrict(string file)
        {
            try
            {
                return JObject.Parse(File.ReadAllText(file),
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            }
            catch (JsonReaderException ex)
            {
                Assert.Fail($"{Path.GetFileName(file)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>"🤖" of "🤖 Plan mode off"; null when the text starts with a letter, digit, space, quote or bracket.</summary>
        private static string LeadingIcon(string text)
        {
            char first = text[0];
            if (char.IsLetterOrDigit(first) || char.IsWhiteSpace(first) || first == '{' || first == '"' || first == '(')
            {
                return null;
            }

            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        private static HashSet<string> CollectKeysFromSource()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (string folder in new[] { "UI", "Controls", "Diff", "ToolWindows", "Package" })
            {
                foreach (string file in Directory.GetFiles(RepositoryLayout.Path(folder), "*.cs"))
                {
                    if (Path.GetFileName(file) == "Loc.cs") continue;
                    foreach (Match match in CodeKey.Matches(File.ReadAllText(file)))
                    {
                        string key = string.Concat(StringLiteral.Matches(match.Groups[1].Value)
                            .Cast<Match>()
                            .Select(part => Regex.Unescape(part.Groups[1].Value)));
                        keys.Add(key);
                    }
                }

                foreach (string file in Directory.GetFiles(RepositoryLayout.Path(folder), "*.xaml"))
                {
                    foreach (Match match in XamlKey.Matches(File.ReadAllText(file)))
                    {
                        keys.Add(System.Net.WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, @"\\(.)", "$1")));
                    }
                }
            }

            return keys;
        }

        private static HashSet<string> Placeholders(string text)
        {
            return new HashSet<string>(
                Regex.Matches(text ?? string.Empty, @"\{(\d+)").Cast<Match>().Select(m => m.Groups[1].Value));
        }
    }
}
