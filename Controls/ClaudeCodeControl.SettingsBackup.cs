/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Settings → Backup tab: save the whole extension configuration (every setting, custom
 *          commands, On Agent Finish global + per-solution, toolbar, CLI paths, …) to a JSON file
 *          and load it back, replacing the current configuration.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Export Format

        /// <summary>Marker identifying a file written by "Save All Settings to File".</summary>
        internal const string ConfigurationExportFormat = "ClaudeCodeExtension.Configuration";

        private const string ExportFormatKey = "Format";
        private const string ExportSettingsKey = "Settings";

        /// <summary>
        /// Wraps a serialized <see cref="ClaudeCodeSettings"/> in the export envelope (format marker,
        /// extension version, timestamp). The settings object is the exact shape of
        /// claudecode-settings.json, so everything stored there travels — including unknown
        /// properties a newer version added (<see cref="ClaudeCodeSettings.AdditionalData"/>).
        /// </summary>
        internal static JObject BuildConfigurationExport(JObject settings, string extensionVersion, DateTime exportedUtc)
        {
            return new JObject
            {
                [ExportFormatKey] = ConfigurationExportFormat,
                ["FormatVersion"] = 1,
                ["ExtensionVersion"] = extensionVersion ?? string.Empty,
                ["ExportedUtc"] = exportedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                [ExportSettingsKey] = settings ?? new JObject()
            };
        }

        /// <summary>
        /// Reads a file chosen for import and returns the settings object to write to disk, or null
        /// with a user-facing <paramref name="error"/>. Accepts the export envelope and also a bare
        /// claudecode-settings.json copied from another machine. Rejects JSON that has none of the
        /// settings properties, so an unrelated file can't silently reset everything to defaults.
        /// </summary>
        internal static JObject ParseConfigurationImport(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "The file is empty.";
                return null;
            }

            JObject root;
            try
            {
                // DateParseHandling.None keeps date strings (e.g. model catalog timestamps) verbatim
                // instead of round-tripping them through DateTime.
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader);
                }
            }
            catch (Exception ex)
            {
                error = $"The file is not a valid settings file: {ex.Message}";
                return null;
            }

            JObject settings = root;
            if (string.Equals((string)root[ExportFormatKey], ConfigurationExportFormat, StringComparison.Ordinal))
            {
                settings = root[ExportSettingsKey] as JObject;
                if (settings == null)
                {
                    error = "The file has no settings section.";
                    return null;
                }
            }

            var knownNames = typeof(ClaudeCodeSettings).GetProperties()
                .Where(p => p.Name != nameof(ClaudeCodeSettings.AdditionalData))
                .Select(p => p.Name);
            if (!knownNames.Any(n => settings.Property(n) != null))
            {
                error = "The file does not contain Claude Code Extension settings.";
                return null;
            }

            try
            {
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ObjectCreationHandling = ObjectCreationHandling.Replace
                });
                if (settings.ToObject<ClaudeCodeSettings>(serializer) == null)
                {
                    error = "The settings in the file could not be read.";
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = $"The settings in the file could not be read: {ex.Message}";
                return null;
            }

            return settings;
        }

        #endregion

        #region Backup Tab

        /// <summary>
        /// Builds the "Backup" settings tab. Save writes the current configuration right away; Load
        /// validates the chosen file, confirms, then hands it to <paramref name="onImportConfirmed"/>
        /// — the caller closes the dialog (discarding its unsaved edits) and applies the import via
        /// <see cref="ApplyImportedConfigurationAsync"/>.
        /// </summary>
        private void BuildSettingsBackupTabContent(StackPanel stack, Brush themeBg, Brush themeFg, Action<JObject> onImportConfirmed)
        {
            Style buttonStyle = GetDialogButtonStyle();

            Button MakeButton(string text)
            {
                var b = new Button
                {
                    Content = text,
                    Height = 30,
                    MinWidth = 220,
                    Padding = new Thickness(14, 0, 14, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(4, 2, 0, 4)
                };
                if (buttonStyle != null) b.Style = buttonStyle;
                else { b.Background = themeBg; b.Foreground = themeFg; b.BorderBrush = themeFg; }
                return b;
            }

            TextBlock MakeHint(string text) => new TextBlock
            {
                Text = text,
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 6)
            };

            // ---- Save ----
            stack.Children.Add(MakeSectionHeader("Save configuration", themeFg));
            stack.Children.Add(MakeHint(
                "Saves the entire extension configuration to a JSON file: every setting in this window, custom " +
                "commands, On Agent Finish (global and per solution), visible agents and models, toolbar layout, " +
                "CLI paths, prompt history and session titles. Changes not yet confirmed with OK are not included."));
            var saveButton = MakeButton("Save All Settings to File...");
            stack.Children.Add(saveButton);

            // ---- Load ----
            stack.Children.Add(MakeSectionHeader("Load configuration", themeFg));
            stack.Children.Add(MakeHint(
                "Replaces the entire extension configuration with a saved file (a claudecode-settings.json copied " +
                "from another machine also works). The current configuration is backed up next to the settings file " +
                "first, and the code agent restarts to apply the loaded settings."));
            var loadButton = MakeButton("Load All Settings from File...");
            stack.Children.Add(loadButton);

            // ---- Folder ----
            stack.Children.Add(MakeSectionHeader("Settings folder", themeFg));
            stack.Children.Add(MakeHint(ConfigurationPath));
            var openFolderButton = MakeButton("Open Settings Folder");
            stack.Children.Add(openFolderButton);

            saveButton.Click += (s, args) => ExportConfigurationToFile(Window.GetWindow(stack));

            loadButton.Click += (s, args) =>
            {
                var owner = Window.GetWindow(stack);
                var imported = PickConfigurationImport(owner);
                if (imported == null) return;

                var confirm = MessageBox.Show(owner,
                    "Replace ALL current extension settings with the ones in this file?\n\n" +
                    "Unsaved changes in the Settings window are discarded, the current configuration is " +
                    "backed up next to the settings file, and the code agent restarts.\n\n" +
                    "Close any other Visual Studio windows first — they keep their own settings in memory " +
                    "and would overwrite the loaded ones the next time they save.",
                    "Load All Settings",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                onImportConfirmed?.Invoke(imported);
            };

            openFolderButton.Click += (s, args) =>
            {
                try
                {
                    string dir = Path.GetDirectoryName(ConfigurationPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error opening settings folder: {ex.Message}");
                }
            };
        }

        #endregion

        #region Save / Load

        /// <summary>
        /// Asks for a target file and writes the current configuration to it in the export envelope.
        /// Uses the in-memory settings (this window's live provider/model picks included) rather than
        /// the disk file, which may hold another VS window's volatile selections.
        /// </summary>
        private void ExportConfigurationToFile(Window owner)
        {
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save All Settings",
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = ".json",
                    FileName = $"ClaudeCodeExtension-settings-{DateTime.Now:yyyyMMdd}.json",
                    OverwritePrompt = true
                };
                if (sfd.ShowDialog(owner) != true) return;

                var settingsObj = JObject.FromObject(_settings ?? new ClaudeCodeSettings());

                // Same rule as SaveSettings: session-only effort levels never persist.
                if (_settings != null && IsSessionOnlyEffort(_settings.SelectedEffortLevel))
                {
                    settingsObj[nameof(ClaudeCodeSettings.SelectedEffortLevel)] = (int)_lastPersistableEffortLevel;
                }

                var version = typeof(ClaudeCodeControl).Assembly.GetName().Version;
                string versionText = version == null ? string.Empty : $"{version.Major}.{version.Minor}";

                File.WriteAllText(sfd.FileName,
                    SerializeJsonIndented(BuildConfigurationExport(settingsObj, versionText, DateTime.UtcNow)));

                MessageBox.Show(owner, $"Settings saved to:\n{sfd.FileName}", "Save All Settings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error exporting settings: {ex.Message}");
                MessageBox.Show(owner, $"Failed to save settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Asks for a file to load and validates it. Returns the settings object, or null when the
        /// user cancelled or the file was rejected (the reason is shown).
        /// </summary>
        private JObject PickConfigurationImport(Window owner)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Load All Settings",
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    CheckFileExists = true
                };
                if (ofd.ShowDialog(owner) != true) return null;

                var imported = ParseConfigurationImport(File.ReadAllText(ofd.FileName), out string error);
                if (imported == null)
                {
                    MessageBox.Show(owner, error, "Load All Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return imported;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading settings import: {ex.Message}");
                MessageBox.Show(owner, $"Failed to read the settings file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// Replaces the settings file with <paramref name="importedSettings"/> (after backing the
        /// current one up), re-runs the normal load path — so dedupe, retired-value cleanup and
        /// migrations apply to imported files too — re-applies every live setting, and restarts the
        /// code agent so terminal type, fonts, provider, CLI paths and native mode take effect.
        /// </summary>
        private async System.Threading.Tasks.Task ApplyImportedConfigurationAsync(JObject importedSettings)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string backupPath = null;
            try
            {
                string dir = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(ConfigurationPath))
                {
                    backupPath = Path.Combine(dir,
                        $"{Path.GetFileNameWithoutExtension(ConfigurationPath)}.before-load-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(ConfigurationPath, backupPath, overwrite: true);
                }

                File.WriteAllText(ConfigurationPath, SerializeJsonIndented(importedSettings));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error writing imported settings: {ex.Message}");
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                LoadSettings();
                ApplyLoadedSettings();

                ApplyChatAppearance();
                ApplyChatSendKeySettings();
                RefreshNativeSessionColors();
                UpdateTerminalTheme();
                RefreshToolbarLayout();
                ClearProviderCache();
                RefreshWatchedAgentFinishConfig();

                UpdateInlineUsagePanelVisibility();
                if (_usageToolWindow?.IsWindowVisible != true)
                    StartUsageBackgroundRefreshTimer();
                _usageToolWindow?.UsageControl?.ApplyAutoRefreshSeconds(_settings.UsageAutoRefreshSeconds);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying imported settings: {ex.Message}");
            }

            try
            {
                await RestartTerminalWithSelectedProviderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error restarting terminal after settings load: {ex.Message}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Settings were loaded, but the code agent failed to restart: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Debug.WriteLine($"Settings loaded from file; previous configuration backed up to {backupPath ?? "(none)"}");
        }

        #endregion
    }
}
