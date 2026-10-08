/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the "Install latest OpenPets" release parsing: Windows installer choice from the GitHub
 *          release JSON, and the SHA256SUMS lookup.
 *
 * *******************************************************************************************************************/

using System;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class OpenPetsReleaseTests
    {
        private const string Hash = "198c0802da4f2d8e486fbbd648a9e6cbd483340e049d79d2048750a8c3d6d05f";
        private const string Base = "https://github.com/OpenPetsHQ/openpets/releases/download/v4.0.1/";

        private const string ReleaseJson = @"{
  ""tag_name"": ""v4.0.1"",
  ""assets"": [
    { ""name"": ""OpenPets-4.0.1-linux-amd64.deb"", ""size"": 1, ""browser_download_url"": """ + Base + @"OpenPets-4.0.1-linux-amd64.deb"" },
    { ""name"": ""OpenPets-4.0.1-win-x64-setup.exe"", ""size"": 184184856, ""browser_download_url"": """ + Base + @"OpenPets-4.0.1-win-x64-setup.exe"" },
    { ""name"": ""SHA256SUMS"", ""size"": 871, ""browser_download_url"": """ + Base + @"SHA256SUMS"" }
  ]
}";

        [TestMethod]
        public void ParseLatestRelease_PicksWindowsInstallerAndChecksums()
        {
            var release = OpenPetsRelease.ParseLatestRelease(ReleaseJson);

            Assert.AreEqual("v4.0.1", release.Tag);
            Assert.AreEqual("OpenPets-4.0.1-win-x64-setup.exe", release.Installer.Name);
            Assert.AreEqual(184184856L, release.Installer.Size);
            Assert.AreEqual(Base + "OpenPets-4.0.1-win-x64-setup.exe", release.Installer.DownloadUrl);
            Assert.IsNotNull(release.Checksums);
            Assert.AreEqual(Base + "SHA256SUMS", release.Checksums.DownloadUrl);
        }

        [TestMethod]
        public void ParseLatestRelease_WithoutWindowsInstaller_Throws()
        {
            const string json = @"{ ""tag_name"": ""v4.0.1"", ""assets"": [
  { ""name"": ""OpenPets-4.0.1-mac-arm64.dmg"", ""size"": 1, ""browser_download_url"": """ + Base + @"OpenPets-4.0.1-mac-arm64.dmg"" } ] }";

            Assert.ThrowsException<InvalidOperationException>(() => OpenPetsRelease.ParseLatestRelease(json));
        }

        [TestMethod]
        public void ParseLatestRelease_IgnoresInstallerOutsideOpenPetsDownloads()
        {
            const string json = @"{ ""tag_name"": ""v4.0.1"", ""assets"": [
  { ""name"": ""OpenPets-4.0.1-win-x64-setup.exe"", ""size"": 1, ""browser_download_url"": ""https://example.com/OpenPets-4.0.1-win-x64-setup.exe"" } ] }";

            Assert.ThrowsException<InvalidOperationException>(() => OpenPetsRelease.ParseLatestRelease(json));
        }

        [TestMethod]
        public void ParseLatestRelease_WithoutSha256Sums_LeavesChecksumsNull()
        {
            const string json = @"{ ""tag_name"": ""v4.0.1"", ""assets"": [
  { ""name"": ""OpenPets-4.0.1-win-x64-setup.exe"", ""size"": 5, ""browser_download_url"": """ + Base + @"OpenPets-4.0.1-win-x64-setup.exe"" } ] }";

            var release = OpenPetsRelease.ParseLatestRelease(json);

            Assert.IsNull(release.Checksums);
        }

        [TestMethod]
        public void FindSha256_ReturnsHashForNamedFile()
        {
            string sums = "9d0f9c4883af78fd54522d1f280f5ef356d7c696b8af63fa16e22000d09d106e  OpenPets-4.0.1-linux-amd64.deb\n" +
                          Hash.ToUpperInvariant() + "  OpenPets-4.0.1-win-x64-setup.exe\n";

            Assert.AreEqual(Hash, OpenPetsRelease.FindSha256(sums, "OpenPets-4.0.1-win-x64-setup.exe"));
        }

        [TestMethod]
        public void FindSha256_AcceptsBinaryModeStarAndCrLf()
        {
            string sums = Hash + " *OpenPets-4.0.1-win-x64-setup.exe\r\n";

            Assert.AreEqual(Hash, OpenPetsRelease.FindSha256(sums, "OpenPets-4.0.1-win-x64-setup.exe"));
        }

        [TestMethod]
        public void FindSha256_ReturnsNullWhenFileNotListed()
        {
            string sums = Hash + "  OpenPets-4.0.1-mac-arm64.dmg\n";

            Assert.IsNull(OpenPetsRelease.FindSha256(sums, "OpenPets-4.0.1-win-x64-setup.exe"));
        }

        [TestMethod]
        public void FindSha256_RejectsMalformedHash()
        {
            Assert.IsNull(OpenPetsRelease.FindSha256("abc123  OpenPets-4.0.1-win-x64-setup.exe\n", "OpenPets-4.0.1-win-x64-setup.exe"));
            Assert.IsNull(OpenPetsRelease.FindSha256(null, "OpenPets-4.0.1-win-x64-setup.exe"));
        }
    }
}
