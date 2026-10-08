/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Install OpenPets" — the release the Settings → Automation button installs, and the pure parsing around it
 *          (latest-release JSON → Windows installer asset, SHA256SUMS → expected hash). No network or UI here, so the
 *          asset choice and checksum lookup are unit-tested.
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Agents
{
    /// <summary>Latest OpenPets release on GitHub: which Windows installer to fetch and how to verify it.</summary>
    public static class OpenPetsRelease
    {
        /// <summary>GitHub API endpoint for the newest published (non-prerelease) release.</summary>
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/OpenPetsHQ/openpets/releases/latest";

        /// <summary>Downloads are only accepted from this prefix, so a malformed API answer cannot point elsewhere.</summary>
        public const string ReleaseDownloadPrefix = "https://github.com/OpenPetsHQ/openpets/releases/download/";

        /// <summary>Windows x64 installer name ends with this, e.g. "OpenPets-4.0.1-win-x64-setup.exe".</summary>
        public const string WindowsInstallerSuffix = "-win-x64-setup.exe";

        /// <summary>Checksum file published with the installers; one "hash  file name" pair per line.</summary>
        public const string ChecksumsAssetName = "SHA256SUMS";

        /// <summary>One downloadable file attached to a release.</summary>
        public sealed class Asset
        {
            public string Name { get; set; } = string.Empty;

            public string DownloadUrl { get; set; } = string.Empty;

            /// <summary>Size GitHub reports for the file, used only when the download response has no length.</summary>
            public long Size { get; set; }
        }

        /// <summary>The release tag, its Windows installer and (when published) its checksum file.</summary>
        public sealed class Release
        {
            public string Tag { get; set; } = string.Empty;

            public Asset Installer { get; set; }

            /// <summary>Null when the release does not publish a checksum file.</summary>
            public Asset Checksums { get; set; }
        }

        /// <summary>
        /// Reads the GitHub "latest release" JSON and picks the Windows installer. Throws
        /// <see cref="InvalidOperationException"/> when the release has no Windows installer from the
        /// OpenPets download location.
        /// </summary>
        public static Release ParseLatestRelease(string json)
        {
            var root = JObject.Parse(json);
            var assets = (root["assets"] as JArray ?? new JArray())
                .Select(ParseAsset)
                .Where(a => a != null)
                .ToList();

            var installer = assets.FirstOrDefault(a =>
                a.Name.EndsWith(WindowsInstallerSuffix, StringComparison.OrdinalIgnoreCase) &&
                a.Name == Path.GetFileName(a.Name) &&
                a.DownloadUrl.StartsWith(ReleaseDownloadPrefix, StringComparison.OrdinalIgnoreCase));
            if (installer == null)
            {
                throw new InvalidOperationException("The latest OpenPets release has no Windows installer.");
            }

            return new Release
            {
                Tag = (string)root["tag_name"] ?? string.Empty,
                Installer = installer,
                Checksums = assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumsAssetName, StringComparison.Ordinal))
            };
        }

        /// <summary>
        /// Looks up the expected SHA-256 of <paramref name="fileName"/> in SHA256SUMS text. Returns lower-case
        /// hex, or null when the file is not listed or the line is malformed.
        /// </summary>
        public static string FindSha256(string checksumsText, string fileName)
        {
            if (string.IsNullOrEmpty(checksumsText) || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            foreach (string rawLine in checksumsText.Split('\n'))
            {
                string line = rawLine.Trim();
                int separator = line.IndexOf(' ');
                if (separator <= 0)
                {
                    continue;
                }

                // sha256sum marks binary-mode entries with a leading '*' on the name
                string hash = line.Substring(0, separator);
                string name = line.Substring(separator).TrimStart(' ', '*');
                if (hash.Length == 64 && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return hash.ToLowerInvariant();
                }
            }

            return null;
        }

        private static Asset ParseAsset(JToken token)
        {
            string name = (string)token["name"];
            string url = (string)token["browser_download_url"];
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                return null;
            }

            return new Asset
            {
                Name = name,
                DownloadUrl = url,
                Size = (long?)token["size"] ?? 0
            };
        }
    }
}
