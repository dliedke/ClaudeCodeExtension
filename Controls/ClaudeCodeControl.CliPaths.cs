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
            (AiProvider.Windsurf,          "Windsurf (WSL)",     true),
            (AiProvider.Pi,                "PI",                 false),
            (AiProvider.Antigravity,       "Antigravity",        false),
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
        /// double-quoted for cmd.exe; WSL paths are single-quoted only when they contain spaces
        /// (they are embedded inside a double-quoted bash -lic string).
        /// </summary>
        private string ResolveProviderExecutable(AiProvider provider, string defaultCommand, bool isWsl = false)
        {
            string custom = GetCustomExecutablePath(provider);
            if (string.IsNullOrWhiteSpace(custom))
            {
                return defaultCommand;
            }

            custom = custom.Trim().Trim('"');

            if (isWsl)
            {
                return custom.IndexOf(' ') >= 0 ? $"'{custom}'" : custom;
            }

            return $"\"{custom}\"";
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
                return File.Exists(custom.Trim().Trim('"'));
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region CLI Path Configuration Dialog

        /// <summary>
        /// Handles the "Configure CLI Paths..." menu item — lets the user point each provider at a
        /// specific executable instead of relying on PATH / the built-in install location.
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void ConfigureCliPathsMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            if (_settings.CustomExecutablePaths == null)
            {
                _settings.CustomExecutablePaths = new Dictionary<AiProvider, string>();
            }

            bool changed = ShowCliPathsDialog();
            if (!changed) return;

            SaveSettings();

            // New paths change detection results — drop the availability cache so the next
            // check (and menu state) reflects the override.
            ClearProviderCache();

            // Restart so the active provider relaunches with its configured executable.
            try
            {
                await RestartTerminalWithSelectedProviderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error restarting terminal after CLI path change: {ex.Message}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Shows the modal CLI-paths dialog. Returns true when the user pressed OK and at least
        /// one path actually changed; mutates _settings.CustomExecutablePaths on OK.
        /// </summary>
        private bool ShowCliPathsDialog()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);
            Style buttonStyle = GetDialogButtonStyle();

            var dialog = new Window
            {
                Title = "Configure CLI Paths",
                Width = 640,
                Height = 470,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 520,
                MinHeight = 320,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };

            try { dialog.Owner = Application.Current?.MainWindow; } catch { }

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "Point a provider at a specific CLI executable instead of relying on PATH. " +
                       "Leave a field empty to use the default detection (PATH / built-in install location).\n" +
                       "Native providers expect a full Windows path (e.g. C:\\Tools\\claude.exe). " +
                       "WSL providers expect a Linux path or command (e.g. /home/me/.local/bin/claude).",
                TextWrapping = TextWrapping.Wrap,
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroller, 1);
            grid.Children.Add(scroller);

            var rows = new StackPanel { Orientation = Orientation.Vertical };
            scroller.Content = rows;

            // One row per provider: label | textbox | (Browse for native)
            var editors = new Dictionary<AiProvider, TextBox>();
            foreach (var p in CliPathProviders)
            {
                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
                        Margin = new Thickness(8, 0, 0, 0)
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

                        if (ofd.ShowDialog(dialog) == true)
                        {
                            tb.Text = ofd.FileName;
                        }
                    };
                    Grid.SetColumn(browse, 2);
                    rowGrid.Children.Add(browse);
                }

                editors[p.Provider] = textBox;
                rows.Children.Add(rowGrid);
            }

            // Bottom buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new Button { Content = "OK", Width = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };
            if (buttonStyle != null) { okButton.Style = buttonStyle; cancelButton.Style = buttonStyle; }
            else
            {
                okButton.Background = themeBg; okButton.Foreground = themeFg; okButton.BorderBrush = themeFg;
                cancelButton.Background = themeBg; cancelButton.Foreground = themeFg; cancelButton.BorderBrush = themeFg;
            }
            okButton.Click += (s, args) => { dialog.DialogResult = true; };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;

            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            // Apply edits; report whether anything actually changed.
            bool changed = false;
            foreach (var p in CliPathProviders)
            {
                string newValue = editors[p.Provider].Text.Trim();
                _settings.CustomExecutablePaths.TryGetValue(p.Provider, out var oldValue);
                oldValue = oldValue ?? "";

                if (newValue == oldValue) continue;

                changed = true;
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

        #endregion
    }
}
