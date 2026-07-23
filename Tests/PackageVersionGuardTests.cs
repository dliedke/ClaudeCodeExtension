/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards against the class of failure behind issue #112 — a dependency whose version
 *          diverges from what Visual Studio itself pre-loads, and JSON writes that depend on a
 *          build-specific Newtonsoft.Json overload.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// These tests never load the extension assembly — they read project metadata and source text.
    /// That keeps them instant and immune to a broken build in the extension itself.
    /// </summary>
    [TestClass]
    public class PackageVersionGuardTests
    {
        /// <summary>
        /// The only Newtonsoft.Json build the extension may reference.
        /// </summary>
        /// <remarks>
        /// Visual Studio pre-loads its own Newtonsoft.Json into the devenv process. Every 13.0.x
        /// build shares AssemblyVersion 13.0.0.0, so VS's copy always wins assembly resolution over
        /// the one shipped in the VSIX — regardless of what the extension compiled against. When
        /// v74 bumped this to 13.0.4, the extension compiled against 13.0.4 but ran against VS's
        /// 13.0.3, and basic actions (submitting a prompt, opening Settings) died with
        /// "Method not found: JToken.ToString(Formatting)" (issue #112, reverted in v75).
        ///
        /// A plain unit test cannot catch this: a test host loads 13.0.4 from its own output folder,
        /// finds the method, and passes green. Pinning the version is the check that actually works.
        /// </remarks>
        private const string RequiredNewtonsoftVersion = "13.0.3";

        [TestMethod]
        public void NewtonsoftJson_IsPinnedToTheBuildVisualStudioLoads()
        {
            var csproj = XDocument.Parse(RepositoryLayout.ReadText("ClaudeCodeExtension.csproj"));

            var newtonsoft = csproj
                .Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .FirstOrDefault(e => string.Equals(
                    (string)e.Attribute("Include"), "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(newtonsoft, "The Newtonsoft.Json PackageReference disappeared from ClaudeCodeExtension.csproj.");

            string version = (string)newtonsoft.Attribute("Version");

            Assert.AreEqual(
                RequiredNewtonsoftVersion,
                version,
                $"Newtonsoft.Json must stay pinned at {RequiredNewtonsoftVersion} — the build Visual Studio " +
                "pre-loads. Any other version compiles fine and then fails at runtime inside VS with " +
                "\"Method not found\" (issue #112). Do not bump this without verifying which build the " +
                "targeted VS versions ship.");
        }

        /// <summary>
        /// Source directories scanned for the risky serialization call.
        /// </summary>
        private static readonly string[] ScannedDirectories = { "Controls", "Package", "UI", "Models", "Diff" };

        /// <summary>
        /// Matches JToken.ToString(Formatting...) — the overload whose presence differs across
        /// Newtonsoft builds. Deliberately does not match plain ToString() or ToString(someString).
        /// </summary>
        private static readonly Regex RiskyToStringOverload =
            new Regex(@"\.ToString\(\s*(Newtonsoft\.Json\.)?Formatting\.", RegexOptions.Compiled);

        [TestMethod]
        public void JsonWrites_DoNotUseTheBuildSpecificToStringOverload()
        {
            var offenders = new List<string>();

            foreach (string directory in ScannedDirectories)
            {
                string fullDirectory = RepositoryLayout.Path(directory);
                if (!Directory.Exists(fullDirectory))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(fullDirectory, "*.cs", SearchOption.AllDirectories))
                {
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];

                        // Skip comments — the fix in Settings.cs documents the banned pattern by name.
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                            trimmed.StartsWith("*", StringComparison.Ordinal) ||
                            trimmed.StartsWith("///", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (RiskyToStringOverload.IsMatch(line))
                        {
                            string relative = file.Substring(RepositoryLayout.Root.Length).TrimStart('\\', '/');
                            offenders.Add($"{relative}:{i + 1}: {trimmed}");
                        }
                    }
                }
            }

            Assert.AreEqual(
                0,
                offenders.Count,
                "JSON must be written through ClaudeCodeControl.SerializeJsonIndented (JsonConvert.SerializeObject), " +
                "never JToken.ToString(Formatting) — that overload resolves against whichever Newtonsoft.Json " +
                "build Visual Studio pre-loaded and threw \"Method not found\" in issue #112. Offending lines:" +
                Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }
    }
}
