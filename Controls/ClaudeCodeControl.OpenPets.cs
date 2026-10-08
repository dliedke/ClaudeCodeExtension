/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Install latest OpenPets" (Settings → Automation): downloads the Windows installer of the latest OpenPets
 *          release from GitHub with a progress window, verifies it against the release's SHA256SUMS and starts it.
 *
 * *******************************************************************************************************************/

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using ClaudeCodeVS.Agents;

using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        /// <summary>
        /// Handles the Settings → Automation "Install latest OpenPets" button. Started once the Settings dialog has
        /// closed (the button closes it, the same way Install Caveman does). Shows a small progress window with a
        /// Cancel button, downloads the installer into a per-release temp folder, refuses it when its SHA-256 does
        /// not match the release's SHA256SUMS, and then starts it so the user can go through the installer.
        /// </summary>
        private async Task InstallOpenPetsAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var confirm = MessageBox.Show(
                    "This will download the latest OpenPets release for Windows from GitHub (about 180 MB) and then start its installer.\n\n" +
                    "Continue?",
                    "Install OpenPets",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.OK)
                {
                    return;
                }

                var cancellation = new CancellationTokenSource();
                var progressWindow = CreateOpenPetsProgressWindow(cancellation, out TextBlock status, out ProgressBar bar);
                string installerPath;
                try
                {
                    installerPath = await DownloadOpenPetsInstallerAsync(status, bar, cancellation.Token);
                }
                finally
                {
                    progressWindow.Close();
                }

                // UseShellExecute lets Windows raise the elevation prompt if the installer asks for it.
                var startInfo = new ProcessStartInfo(installerPath) { UseShellExecute = true };
                Process.Start(startInfo)?.Dispose();
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("InstallOpenPetsAsync: cancelled by the user");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the user declined the elevation prompt for the installer
                Debug.WriteLine("InstallOpenPetsAsync: installer elevation declined");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InstallOpenPetsAsync failed: {ex.Message}");
                MessageBox.Show($"Failed to install OpenPets: {ex.Message}", "Install OpenPets", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Shows the progress window (non-modal, so the download can await). Closing it or pressing Cancel
        /// cancels the download; the window itself is closed by the caller once the download finishes or fails.
        /// </summary>
        private Window CreateOpenPetsProgressWindow(CancellationTokenSource cancellation, out TextBlock status, out ProgressBar bar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            status = new TextBlock
            {
                Text = "Starting…",
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap
            };

            bar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                IsIndeterminate = true,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 28,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Style buttonStyle = GetDialogButtonStyle();
            if (buttonStyle != null) cancelButton.Style = buttonStyle;
            else { cancelButton.Background = themeBg; cancelButton.Foreground = themeFg; cancelButton.BorderBrush = themeFg; }
            cancelButton.Click += (s, e) => cancellation.Cancel();

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(status);
            root.Children.Add(bar);
            root.Children.Add(cancelButton);

            var window = new Window
            {
                Title = "Install OpenPets",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Content = root
            };
            try { window.Owner = Application.Current?.MainWindow; } catch { }
            window.Closed += (s, e) => cancellation.Cancel();
            window.Show();

            return window;
        }

        /// <summary>
        /// Fetches the latest release, downloads its Windows installer while reporting progress, and checks the
        /// SHA-256 against SHA256SUMS. Returns the path of the verified installer. A partial download is deleted
        /// on failure or cancel. Runs on the UI thread; the transfer itself is async.
        /// </summary>
        private async Task<string> DownloadOpenPetsInstallerAsync(TextBlock status, ProgressBar bar, CancellationToken token)
        {
            // GitHub refuses TLS below 1.2, and the .NET Framework default can still be TLS 1.0/1.1
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeExtension");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                status.Text = "Looking up the latest OpenPets release…";
                string json;
                using (var response = await client.GetAsync(OpenPetsRelease.LatestReleaseApiUrl, token))
                {
                    response.EnsureSuccessStatusCode();
                    json = await response.Content.ReadAsStringAsync();
                }

                OpenPetsRelease.Release release = OpenPetsRelease.ParseLatestRelease(json);

                if (release.Checksums == null)
                {
                    throw new InvalidOperationException("The latest OpenPets release does not publish checksums, so its installer was not downloaded.");
                }

                string checksums;
                using (var response = await client.GetAsync(release.Checksums.DownloadUrl, token))
                {
                    response.EnsureSuccessStatusCode();
                    checksums = await response.Content.ReadAsStringAsync();
                }

                string expectedHash = OpenPetsRelease.FindSha256(checksums, release.Installer.Name);
                if (expectedHash == null)
                {
                    throw new InvalidOperationException("The latest OpenPets release has no checksum for its Windows installer.");
                }

                string folder = Path.Combine(Path.GetTempPath(), "ClaudeCodeExtension", "OpenPets", release.Tag);
                Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, release.Installer.Name);
                string partial = target + ".download";

                try
                {
                    status.Text = $"Downloading OpenPets {release.Tag}…";
                    bar.IsIndeterminate = true;

                    using (var response = await client.GetAsync(release.Installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();

                        long total = response.Content.Headers.ContentLength ?? release.Installer.Size;
                        using (var source = await response.Content.ReadAsStreamAsync())
                        using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            long received = 0;
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            {
                                await file.WriteAsync(buffer, 0, read, token);
                                received += read;

                                bar.IsIndeterminate = total <= 0;
                                if (total > 0)
                                {
                                    bar.Value = received * 100.0 / total;
                                    status.Text = $"Downloading OpenPets {release.Tag}… {FormatMegabytes(received)} of {FormatMegabytes(total)} ({(int)bar.Value}%)";
                                }
                                else
                                {
                                    status.Text = $"Downloading OpenPets {release.Tag}… {FormatMegabytes(received)}";
                                }
                            }
                        }
                    }

                    status.Text = "Checking the download…";
                    bar.IsIndeterminate = true;

                    string actualHash = await Task.Run(() => ComputeSha256(partial));
                    if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The downloaded OpenPets installer does not match the published checksum, so it was not started.");
                    }

                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    File.Move(partial, target);
                    return target;
                }
                catch
                {
                    TryDeleteFile(partial);
                    throw;
                }
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string FormatMegabytes(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MB";
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryDeleteFile failed: {ex.Message}");
            }
        }
    }
}
