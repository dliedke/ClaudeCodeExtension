/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Makes the mandatory version-bump rule from CLAUDE.md executable — AssemblyInfo, the VSIX
 *          manifest and the README release notes must all agree, and the minor must stay ".0".
 *
 * *******************************************************************************************************************/

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// A shipped VSIX whose manifest version does not match the assembly, or whose README has no
    /// entry for the release, is a silent packaging bug: the marketplace shows one version and the
    /// installed extension reports another. These read the three sources and compare them.
    /// </summary>
    [TestClass]
    public class VersionConsistencyTests
    {
        // Skips the commented-out "[assembly: AssemblyVersion("1.0.*")]" sample line that ships in
        // the default AssemblyInfo.cs template.
        private static readonly Regex AssemblyVersionPattern =
            new Regex(@"^\s*\[assembly:\s*AssemblyVersion\(""(?<version>[\d\.\*]+)""\)\]", RegexOptions.Multiline);

        private static readonly Regex AssemblyFileVersionPattern =
            new Regex(@"^\s*\[assembly:\s*AssemblyFileVersion\(""(?<version>[\d\.\*]+)""\)\]", RegexOptions.Multiline);

        private static readonly Regex ReadmeVersionHeadingPattern =
            new Regex(@"^###\s+Version\s+(?<version>\d+\.\d+)\s*$", RegexOptions.Multiline);

        [TestMethod]
        public void AssemblyManifestAndReadme_AllDeclareTheSameVersion()
        {
            string assemblyVersion = ReadAssemblyVersion();
            string fileVersion = ReadAssemblyFileVersion();
            string manifestVersion = ReadManifestVersion();
            string readmeVersion = ReadLatestReadmeVersion();

            Assert.AreEqual(
                assemblyVersion,
                fileVersion,
                "AssemblyVersion and AssemblyFileVersion in Properties/AssemblyInfo.cs must match.");

            // AssemblyVersion is "X.Y.0.0"; the manifest and README use "X.Y".
            string shortAssemblyVersion = ToShortVersion(assemblyVersion);

            Assert.AreEqual(
                shortAssemblyVersion,
                manifestVersion,
                "source.extension.vsixmanifest <Identity Version> must match AssemblyInfo.cs. " +
                "Per CLAUDE.md every release bumps all three version sources together.");

            Assert.AreEqual(
                shortAssemblyVersion,
                readmeVersion,
                "The newest '### Version X.0' entry in README.md must match the version being built. " +
                "Per CLAUDE.md every code change adds a release-notes entry.");
        }

        [TestMethod]
        public void Version_UsesTheMajorOnlyScheme()
        {
            string shortVersion = ToShortVersion(ReadAssemblyVersion());
            string[] parts = shortVersion.Split('.');

            Assert.AreEqual(
                "0",
                parts[1],
                $"Version {shortVersion} breaks the versioning scheme: since 11.0 every release bumps the " +
                "MAJOR by one and keeps the minor at '.0' (22.0 -> 23.0 -> 24.0). See CLAUDE.md.");
        }

        [TestMethod]
        public void ReadmeVersionHistory_IsInDescendingOrder()
        {
            string readme = RepositoryLayout.ReadText("README.md");

            var versions = ReadmeVersionHeadingPattern
                .Matches(readme)
                .Cast<Match>()
                .Select(m => m.Groups["version"].Value)
                .ToList();

            Assert.IsTrue(versions.Count > 1, "README.md should list more than one released version.");

            for (int i = 1; i < versions.Count; i++)
            {
                var previous = Version.Parse(versions[i - 1]);
                var current = Version.Parse(versions[i]);

                Assert.IsTrue(
                    previous > current,
                    $"README.md version history must run newest-first, but '### Version {versions[i - 1]}' " +
                    $"is listed above '### Version {versions[i]}'. New entries go at the top of ## Version History.");
            }
        }

        private static string ReadAssemblyVersion()
        {
            string text = RepositoryLayout.ReadText("Properties", "AssemblyInfo.cs");
            var match = AssemblyVersionPattern.Match(text);
            Assert.IsTrue(match.Success, "Could not find an active [assembly: AssemblyVersion(...)] in Properties/AssemblyInfo.cs.");
            return match.Groups["version"].Value;
        }

        private static string ReadAssemblyFileVersion()
        {
            string text = RepositoryLayout.ReadText("Properties", "AssemblyInfo.cs");
            var match = AssemblyFileVersionPattern.Match(text);
            Assert.IsTrue(match.Success, "Could not find an active [assembly: AssemblyFileVersion(...)] in Properties/AssemblyInfo.cs.");
            return match.Groups["version"].Value;
        }

        private static string ReadManifestVersion()
        {
            var manifest = XDocument.Parse(RepositoryLayout.ReadText("source.extension.vsixmanifest"));

            var identity = manifest
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Identity");

            Assert.IsNotNull(identity, "No <Identity> element in source.extension.vsixmanifest.");

            string version = (string)identity.Attribute("Version");
            Assert.IsFalse(string.IsNullOrWhiteSpace(version), "<Identity> has no Version attribute.");
            return version;
        }

        private static string ReadLatestReadmeVersion()
        {
            string readme = RepositoryLayout.ReadText("README.md");
            var match = ReadmeVersionHeadingPattern.Match(readme);
            Assert.IsTrue(match.Success, "No '### Version X.Y' heading found in README.md.");
            return match.Groups["version"].Value;
        }

        /// <summary>Reduces "77.0.0.0" to "77.0" for comparison against the manifest and README.</summary>
        private static string ToShortVersion(string fourPartVersion)
        {
            string[] parts = fourPartVersion.Split('.');
            Assert.IsTrue(parts.Length >= 2, $"Unexpected version format: {fourPartVersion}");
            return $"{parts[0]}.{parts[1]}";
        }
    }
}
