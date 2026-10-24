/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Chat UI localization: lookup/fallback/format, translation file sanity, and a coverage report of
 *          untranslated and orphaned strings (reported, never failing the build)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            Assert.AreEqual("it", Loc.ResolveLanguage(Loc.AutomaticLanguage, new System.Globalization.CultureInfo("it-IT")));
            Assert.AreEqual("en", Loc.ResolveLanguage("", new System.Globalization.CultureInfo("en-US")));
            Assert.AreEqual("pt-BR", Loc.ResolveLanguage("pt-BR", new System.Globalization.CultureInfo("it-IT")));
        }

        [TestMethod]
        public void ItalianIsEmbeddedAndAvailable()
        {
            CollectionAssert.Contains(Loc.AvailableLanguages().ToList(), "it");

            Loc.SetLanguage("it");
            Assert.AreEqual("it", Loc.Language);
        }

        [TestMethod]
        public void TranslationFiles_AreValidAndKeepTheirPlaceholders()
        {
            var problems = new List<string>();

            foreach (string file in Directory.GetFiles(RepositoryLayout.Path("lang"), "*.json"))
            {
                JObject json = JObject.Parse(File.ReadAllText(file));
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
        /// Lists strings wrapped in L(...)/{local:Loc ...} that a translation file lacks, and translations whose
        /// English text no longer exists in the code. Reported as Inconclusive, never a failure: a contributor
        /// writing new English text must not have the build blocked by a missing translation.
        /// </summary>
        [TestMethod]
        public void TranslationCoverage_Report()
        {
            HashSet<string> keys = CollectKeysFromSource();
            var report = new List<string>();

            foreach (string file in Directory.GetFiles(RepositoryLayout.Path("lang"), "*.json"))
            {
                var translated = new HashSet<string>(JObject.Parse(File.ReadAllText(file)).Properties().Select(p => p.Name), StringComparer.Ordinal);
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

            if (report.Count > 0)
            {
                Assert.Inconclusive("Translation coverage:\n" + string.Join("\n", report));
            }
        }

        // L("a") or N("a"), including a key split over lines as "a " + "b" (the compiler folds it into one).
        private static readonly Regex CodeKey = new Regex(
            @"\b[LN]\(\s*((?:""(?:[^""\\]|\\.)*""\s*\+\s*)*""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);
        private static readonly Regex StringLiteral = new Regex(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        private static readonly Regex XamlKey = new Regex(@"\{local:Loc\s+'((?:[^'\\]|\\.)*)'\s*\}", RegexOptions.Compiled);

        private static HashSet<string> CollectKeysFromSource()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (string folder in new[] { "UI", "Controls" })
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
                        keys.Add(Regex.Replace(match.Groups[1].Value, @"\\(.)", "$1"));
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
