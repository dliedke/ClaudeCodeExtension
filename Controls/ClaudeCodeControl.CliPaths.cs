/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: Custom CLI executable path configuration (per-provider override of detection/launch)
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region CLI Path Metadata

        /// <summary>
        /// Providers that support a custom CLI executable path, with their display name and
        /// whether they run inside WSL (which changes path quoting and validation rules).
        /// </summary>
        private static readonly (AiProvider Provider, string DisplayName, bool IsWsl)[] CliPathProviders =
        {
            (AiProvider.ClaudeCode,        "Claude Code",        false),
            (AiProvider.ClaudeCodeWSL,     "Claude Code (WSL)",  true),
            (AiProvider.CodexNative,       "Codex",              false),
            (AiProvider.Codex,             "Codex (WSL)",        true),
            (AiProvider.CursorAgentNative, "Cursor Agent",       false),
            (AiProvider.CursorAgent,       "Cursor Agent (WSL)", true),
            (AiProvider.OpenCode,          "Open Code",          false),
            (AiProvider.Devin,          "Devin (WSL)",     true),
            (AiProvider.Pi,                "PI",                 false),
            (AiProvider.Antigravity,       "Antigravity",        false),
            (AiProvider.Reasonix,          "Reasonix",           false),
            (AiProvider.DevinNative,       "Devin",              false),
        };

        #endregion

        #region CLI Path Resolution Helpers

        /// <summary>
        /// Returns the user-configured custom executable path for a provider, or null when
        /// none is set (empty/whitespace entries are treated as unset).
        /// </summary>
        private string GetCustomExecutablePath(AiProvider provider)
        {
            if (_settings?.CustomExecutablePaths != null &&
                _settings.CustomExecutablePaths.TryGetValue(provider, out var path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                return path.Trim();
            }
            return null;
        }

        /// <summary>
        /// Returns the executable token to launch for a provider: the configured custom path
        /// (properly quoted) when set, otherwise the supplied default command. Native paths are
        /// quoted for cmd.exe; WSL paths are quoted as a single Bash word because they are later
        /// embedded inside a <c>bash -lic</c> command.
        /// </summary>
        private string ResolveProviderExecutable(AiProvider provider, string defaultCommand, bool isWsl = false)
        {
            string custom = GetCustomExecutablePath(provider);
            if (string.IsNullOrWhiteSpace(custom))
            {
                return defaultCommand;
            }

            custom = TrimMatchingQuotes(custom.Trim());

            if (isWsl)
            {
                if (ContainsUnsafeWslExecutableCharacter(custom))
                {
                    Debug.WriteLine($"Ignoring custom WSL executable for {provider}: unsupported quote/control character.");
                    return defaultCommand;
                }

                return QuoteForBash(custom);
            }

            return QuoteForWindowsCommandArgument(custom);
        }

        private static string TrimMatchingQuotes(string value)
        {
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static bool ContainsUnsafeWslExecutableCharacter(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            return value.IndexOf('"') >= 0 ||
                   value.IndexOf('\r') >= 0 ||
                   value.IndexOf('\n') >= 0 ||
                   value.IndexOf('\0') >= 0;
        }

        internal static string QuoteForBash(string value)
        {
            if (value == null)
            {
                return "''";
            }

            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private static string QuoteForWindowsCommandArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var quoted = new StringBuilder();
            quoted.Append('"');

            int backslashCount = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (c == '"')
                {
                    quoted.Append('\\', backslashCount * 2 + 1);
                    quoted.Append('"');
                    backslashCount = 0;
                    continue;
                }

                quoted.Append('\\', backslashCount);
                backslashCount = 0;
                quoted.Append(c);
            }

            quoted.Append('\\', backslashCount * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        /// <summary>
        /// Whether a provider has a usable custom executable path configured. Used by detection
        /// so a tool installed outside PATH (but pointed to here) is still reported as available.
        /// Native paths are validated with File.Exists; WSL paths are trusted as-is (a Linux
        /// path can't be probed from Windows).
        /// </summary>
        private bool CustomExecutableConfigured(AiProvider provider, bool isWsl)
        {
            string custom = GetCustomExecutablePath(provider);
            if (string.IsNullOrWhiteSpace(custom))
            {
                return false;
            }

            if (isWsl)
            {
                return true;
            }

            try
            {
                return File.Exists(TrimMatchingQuotes(custom.Trim()));
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region CLI Paths Settings Tab

        /// <summary>
        /// Builds the "CLI Paths" tab content into the supplied stack panel and returns the
        /// per-provider editors so the host (consolidated Settings dialog) can apply changes on OK.
        /// One aligned row per provider: label | path text box | Browse (native only). The Browse
        /// column width is reserved on every row so the text boxes line up even for WSL providers
        /// (which have no Browse button).
        /// </summary>
        private Dictionary<AiProvider, TextBox> BuildCliPathsTabContent(StackPanel stack, Brush themeBg, Brush themeFg)
        {
            if (_settings.CustomExecutablePaths == null)
            {
                _settings.CustomExecutablePaths = new Dictionary<AiProvider, string>();
            }

            Style buttonStyle = GetDialogButtonStyle();

            stack.Children.Add(MakeSectionHeader("Custom CLI executable paths", themeFg));
            stack.Children.Add(new TextBlock
            {
                Text = "Paths left empty use the default detection (PATH / built-in install location).\n" +
                       "Set a path only to point a provider at a specific executable instead.\n" +
                       "Native providers expect a full Windows path (e.g. C:\\Tools\\claude.exe). " +
                       "WSL providers expect a Linux path or command (e.g. /home/me/.local/bin/claude).",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 8)
            });

            // Reserve a fixed width for the Browse column on every row so the text boxes align.
            const double browseColumnWidth = 88;

            var editors = new Dictionary<AiProvider, TextBox>();
            foreach (var p in CliPathProviders)
            {
                var rowGrid = new Grid { Margin = new Thickness(4, 0, 0, 8) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(browseColumnWidth) });

                var nameLabel = new TextBlock
                {
                    Text = p.DisplayName,
                    Foreground = themeFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(nameLabel, 0);
                rowGrid.Children.Add(nameLabel);

                _settings.CustomExecutablePaths.TryGetValue(p.Provider, out var current);
                var textBox = new TextBox
                {
                    Text = current ?? "",
                    Background = themeBg,
                    Foreground = themeFg,
                    BorderBrush = themeFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Height = 24
                };
                Grid.SetColumn(textBox, 1);
                rowGrid.Children.Add(textBox);

                // Validation: highlight a native path in red when the file does not exist.
                bool isWsl = p.IsWsl;
                textBox.TextChanged += (s, args) =>
                {
                    string val = textBox.Text.Trim().Trim('"');
                    if (string.IsNullOrEmpty(val) || isWsl)
                    {
                        textBox.Foreground = themeFg;
                        return;
                    }
                    bool exists;
                    try { exists = File.Exists(val); } catch { exists = false; }
                    textBox.Foreground = exists ? themeFg : Brushes.Red;
                };

                // Browse button only makes sense for native (Windows) executables.
                if (!p.IsWsl)
                {
                    var browse = new Button
                    {
                        Content = "Browse...",
                        Width = 80,
                        Height = 24,
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    if (buttonStyle != null) { browse.Style = buttonStyle; }
                    else { browse.Background = themeBg; browse.Foreground = themeFg; browse.BorderBrush = themeFg; }

                    var tb = textBox;
                    browse.Click += (s, args) =>
                    {
                        var ofd = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = $"Select {p.DisplayName} executable",
                            Filter = "Executables (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
                            CheckFileExists = true
                        };
                        try
                        {
                            string existing = tb.Text.Trim().Trim('"');
                            if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
                            {
                                ofd.InitialDirectory = Path.GetDirectoryName(existing);
                                ofd.FileName = Path.GetFileName(existing);
                            }
                        }
                        catch { }

                        if (ofd.ShowDialog() == true)
                        {
                            tb.Text = ofd.FileName;
                        }
                    };
                    Grid.SetColumn(browse, 2);
                    rowGrid.Children.Add(browse);
                }

                editors[p.Provider] = textBox;
                stack.Children.Add(rowGrid);
            }

            return editors;
        }

        /// <summary>
        /// Applies edits collected by <see cref="BuildCliPathsTabContent"/> into
        /// _settings.CustomExecutablePaths. Returns true when at least one path actually changed
        /// (so the host can drop the provider cache and restart the terminal).
        /// </summary>
        private List<AiProvider> ApplyCliPathChanges(Dictionary<AiProvider, TextBox> editors)
        {
            var changed = new List<AiProvider>();
            if (editors == null || _settings?.CustomExecutablePaths == null) return changed;

            foreach (var p in CliPathProviders)
            {
                if (!editors.TryGetValue(p.Provider, out var tb)) continue;

                string newValue = tb.Text.Trim();
                _settings.CustomExecutablePaths.TryGetValue(p.Provider, out var oldValue);
                oldValue = oldValue ?? "";

                if (newValue == oldValue) continue;

                changed.Add(p.Provider);
                if (string.IsNullOrWhiteSpace(newValue))
                {
                    _settings.CustomExecutablePaths.Remove(p.Provider);
                }
                else
                {
                    _settings.CustomExecutablePaths[p.Provider] = newValue;
                }
            }

            return changed;
        }

        /// <summary>
        /// Confirms before closing the dialog when one or more native (non-WSL) providers point at a
        /// path that doesn't exist on disk. WSL paths can't be probed from Windows, so they're skipped.
        /// Returns true to allow the dialog to close (no bad paths, or the user chose to save anyway);
        /// false to keep it open so the user can fix the path. Must be called on the UI thread.
        /// </summary>
        private bool ConfirmCliPathsBeforeClose(Dictionary<AiProvider, TextBox> editors)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (editors == null) return true;

            var invalid = new List<string>();
            foreach (var p in CliPathProviders)
            {
                if (p.IsWsl) continue;
                if (!editors.TryGetValue(p.Provider, out var tb)) continue;

                string val = tb.Text.Trim().Trim('"');
                if (string.IsNullOrEmpty(val)) continue;

                bool exists;
                try { exists = File.Exists(val); } catch { exists = false; }
                if (!exists)
                {
                    invalid.Add($"  • {p.DisplayName}: {val}");
                }
            }

            if (invalid.Count == 0) return true;

            var result = MessageBox.Show(
                "These CLI executable paths don't exist on disk:\n\n" +
                string.Join("\n", invalid) +
                "\n\nThe agent will fail to launch if a path is wrong. Save these paths anyway?",
                "CLI Path Not Found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return result == MessageBoxResult.Yes;
        }

        #endregion
    }
}
