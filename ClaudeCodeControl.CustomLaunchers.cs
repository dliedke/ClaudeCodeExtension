/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: User-defined custom Claude Code launchers — configuration dialog and helpers
 *          for starting Claude Code routed through a local model (e.g. Ollama).
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Provider Menu Integration

        /// <summary>
        /// Rebuilds the dynamic custom-launcher entries inside the provider context menu.
        /// Items are inserted directly after <c>CustomLaunchersSeparator</c>, replacing
        /// any previous dynamic entries (identified by <see cref="CustomClaudeLauncher"/> tag).
        /// </summary>
        private void RebuildCustomLauncherMenuItems()
        {
            try
            {
                var menu = ProviderContextMenu;
                if (menu == null || CustomLaunchersSeparator == null) return;

                // Remove existing dynamic items
                var existing = menu.Items.OfType<MenuItem>()
                    .Where(mi => mi.Tag is CustomClaudeLauncher)
                    .ToList();
                foreach (var mi in existing) menu.Items.Remove(mi);

                var launchers = _settings?.CustomClaudeLaunchers;
                if (launchers == null || launchers.Count == 0) return;

                int sepIdx = menu.Items.IndexOf(CustomLaunchersSeparator);
                if (sepIdx < 0) return;

                int insertAt = sepIdx + 1;
                string activeName = _settings.SelectedCustomLauncherName ?? "";
                bool launcherActive = _settings.SelectedProvider == AiProvider.CustomClaudeLauncher;

                foreach (var launcher in launchers)
                {
                    if (launcher == null || string.IsNullOrWhiteSpace(launcher.Name)) continue;
                    var mi = new MenuItem
                    {
                        Header = launcher.Name,
                        ToolTip = launcher.Command,
                        Tag = launcher,
                        IsCheckable = true,
                        IsChecked = launcherActive && string.Equals(activeName, launcher.Name, StringComparison.OrdinalIgnoreCase)
                    };
                    mi.Click += CustomLauncherMenuItem_Click;
                    menu.Items.Insert(insertAt++, mi);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error rebuilding custom launcher menu items: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a click on a dynamic custom launcher menu item — switches the active
        /// provider to <see cref="AiProvider.CustomClaudeLauncher"/> and restarts the terminal.
        /// </summary>
#pragma warning disable VSTHRD100 // async void event handler
        private async void CustomLauncherMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var item = sender as MenuItem;
                var launcher = item?.Tag as CustomClaudeLauncher;
                if (launcher == null || _settings == null) return;

                _settings.SelectedProvider = AiProvider.CustomClaudeLauncher;
                _settings.SelectedCustomLauncherName = launcher.Name;
                UpdateProviderSelection();
                SaveSettings();

                await StartEmbeddedTerminalAsync(AiProvider.CustomClaudeLauncher);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error switching to custom launcher: {ex.Message}");
                MessageBox.Show($"Failed to start custom launcher: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Returns the currently active custom launcher, or null when none is
        /// selected or the saved name no longer matches any configured entry.
        /// </summary>
        private CustomClaudeLauncher GetActiveCustomLauncher()
        {
            if (_settings?.CustomClaudeLaunchers == null) return null;
            string name = _settings.SelectedCustomLauncherName;
            if (string.IsNullOrEmpty(name)) return null;
            return _settings.CustomClaudeLaunchers.FirstOrDefault(l =>
                string.Equals(l?.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Configure Custom Launchers Menu Handler

        /// <summary>
        /// Handles the "Configure Claude Launchers..." menu item click.
        /// Opens the management dialog and saves changes on close.
        /// </summary>
        private void ConfigureCustomLaunchersMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_settings == null) _settings = new ClaudeCodeSettings();
                if (_settings.CustomClaudeLaunchers == null)
                    _settings.CustomClaudeLaunchers = new List<CustomClaudeLauncher>();

                ShowCustomLaunchersDialog();

                // If active launcher was removed/renamed, clear selection back to stock Claude Code.
                if (_settings.SelectedProvider == AiProvider.CustomClaudeLauncher
                    && GetActiveCustomLauncher() == null)
                {
                    _settings.SelectedProvider = AiProvider.ClaudeCode;
                    _settings.SelectedCustomLauncherName = "";
                }

                SaveSettings();
                UpdateProviderSelection();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error configuring custom launchers: {ex.Message}");
                MessageBox.Show($"Error configuring custom launchers: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Configuration Dialog

        /// <summary>
        /// Shows the modal dialog for adding, editing, and removing custom Claude launchers.
        /// </summary>
        private void ShowCustomLaunchersDialog()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = "Configure Claude Launchers",
                Width = 640,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 520,
                MinHeight = 360,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };

            try { dialog.Owner = Application.Current?.MainWindow; } catch { }

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Custom Claude Code launchers run any command you want to start Claude Code — " +
                       "typically pointed at a local model via Ollama. Each entry appears in the provider " +
                       "menu after Windsurf. Cloud usage tracking and model selection are hidden when one " +
                       "is active.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var listGrid = new Grid();
            listGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            listGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(listGrid, 1);
            grid.Children.Add(listGrid);

            var listBox = new ListBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(listBox, 0);
            listGrid.Children.Add(listBox);

            Action refreshList = () =>
            {
                listBox.Items.Clear();
                foreach (var launcher in _settings.CustomClaudeLaunchers)
                {
                    string display = string.IsNullOrWhiteSpace(launcher.Name)
                        ? launcher.Command
                        : $"{launcher.Name}  —  {launcher.Command}";
                    var lbi = new ListBoxItem
                    {
                        Content = display,
                        Tag = launcher,
                        Background = themeBg,
                        Foreground = themeFg
                    };
                    listBox.Items.Add(lbi);
                }
            };
            refreshList();

            var sideStack = new StackPanel { Orientation = Orientation.Vertical, Width = 90 };
            Grid.SetColumn(sideStack, 1);
            listGrid.Children.Add(sideStack);

            Style buttonStyle = GetDialogButtonStyle();

            Func<string, Button> makeSideButton = (text) =>
            {
                var b = new Button
                {
                    Content = text,
                    Height = 28,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                if (buttonStyle != null)
                {
                    b.Style = buttonStyle;
                }
                else
                {
                    b.Background = themeBg;
                    b.Foreground = themeFg;
                    b.BorderBrush = themeFg;
                }
                return b;
            };

            var addButton = makeSideButton("Add...");
            var editButton = makeSideButton("Edit...");
            var removeButton = makeSideButton("Remove");
            var moveUpButton = makeSideButton("Move Up");
            var moveDownButton = makeSideButton("Move Down");

            sideStack.Children.Add(addButton);
            sideStack.Children.Add(editButton);
            sideStack.Children.Add(removeButton);
            sideStack.Children.Add(moveUpButton);
            sideStack.Children.Add(moveDownButton);

            addButton.Click += (s, args) =>
            {
                var newLauncher = ShowCustomLauncherEditorDialog(null, dialog);
                if (newLauncher != null)
                {
                    if (!ValidateUniqueName(newLauncher.Name, -1))
                    {
                        MessageBox.Show("A launcher with that name already exists.",
                            "Duplicate Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    _settings.CustomClaudeLaunchers.Add(newLauncher);
                    refreshList();
                    listBox.SelectedIndex = _settings.CustomClaudeLaunchers.Count - 1;
                }
            };

            editButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.CustomClaudeLaunchers.Count) return;
                var existing = _settings.CustomClaudeLaunchers[idx];
                string oldName = existing.Name;
                var edited = ShowCustomLauncherEditorDialog(existing, dialog);
                if (edited != null)
                {
                    if (!ValidateUniqueName(edited.Name, idx))
                    {
                        MessageBox.Show("A launcher with that name already exists.",
                            "Duplicate Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    _settings.CustomClaudeLaunchers[idx] = edited;
                    // Track rename of currently-active launcher
                    if (string.Equals(_settings.SelectedCustomLauncherName, oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.SelectedCustomLauncherName = edited.Name;
                    }
                    refreshList();
                    listBox.SelectedIndex = idx;
                }
            };

            removeButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.CustomClaudeLaunchers.Count) return;

                var launcher = _settings.CustomClaudeLaunchers[idx];
                string display = string.IsNullOrWhiteSpace(launcher.Name) ? launcher.Command : launcher.Name;
                var result = MessageBox.Show($"Remove launcher \"{display}\"?",
                    "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _settings.CustomClaudeLaunchers.RemoveAt(idx);
                    refreshList();
                    if (_settings.CustomClaudeLaunchers.Count > 0)
                    {
                        listBox.SelectedIndex = Math.Min(idx, _settings.CustomClaudeLaunchers.Count - 1);
                    }
                }
            };

            moveUpButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx <= 0) return;
                var item = _settings.CustomClaudeLaunchers[idx];
                _settings.CustomClaudeLaunchers.RemoveAt(idx);
                _settings.CustomClaudeLaunchers.Insert(idx - 1, item);
                refreshList();
                listBox.SelectedIndex = idx - 1;
            };

            moveDownButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.CustomClaudeLaunchers.Count - 1) return;
                var item = _settings.CustomClaudeLaunchers[idx];
                _settings.CustomClaudeLaunchers.RemoveAt(idx);
                _settings.CustomClaudeLaunchers.Insert(idx + 1, item);
                refreshList();
                listBox.SelectedIndex = idx + 1;
            };

            listBox.MouseDoubleClick += (s, args) => editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var bottomPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(bottomPanel, 2);

            var closeButton = new Button
            {
                Content = "Close",
                Width = 80,
                Height = 26,
                IsDefault = true,
                IsCancel = true
            };
            if (buttonStyle != null)
            {
                closeButton.Style = buttonStyle;
            }
            else
            {
                closeButton.Background = themeBg;
                closeButton.Foreground = themeFg;
                closeButton.BorderBrush = themeFg;
            }
            closeButton.Click += (s, args) => { dialog.DialogResult = true; };
            bottomPanel.Children.Add(closeButton);
            grid.Children.Add(bottomPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Validates that <paramref name="name"/> is unique among configured launchers,
        /// ignoring the entry at <paramref name="excludeIndex"/> (use -1 when adding new).
        /// </summary>
        private bool ValidateUniqueName(string name, int excludeIndex)
        {
            if (string.IsNullOrWhiteSpace(name)) return true; // empty handled elsewhere
            for (int i = 0; i < _settings.CustomClaudeLaunchers.Count; i++)
            {
                if (i == excludeIndex) continue;
                if (string.Equals(_settings.CustomClaudeLaunchers[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Shows the editor for a single launcher (Add/Edit). Includes a readonly
        /// instructions textbox describing how to install Ollama and a local model.
        /// </summary>
        private CustomClaudeLauncher ShowCustomLauncherEditorDialog(CustomClaudeLauncher existing, Window owner)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = existing == null ? "Add Claude Launcher" : "Edit Claude Launcher",
                Width = 640,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 520,
                MinHeight = 420,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Owner = owner
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // name label
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // name box
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // cmd label
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // cmd box
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // instructions label
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // instructions box
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

            var nameLabel = new TextBlock
            {
                Text = "Name (shown in provider menu):",
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(nameLabel, 0);
            grid.Children.Add(nameLabel);

            var nameBox = new TextBox
            {
                Text = existing?.Name ?? "",
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(nameBox, 1);
            grid.Children.Add(nameBox);

            var cmdLabel = new TextBlock
            {
                Text = "Command (run to start Claude Code, e.g. ollama launch claude --model qwen3-coder:30b):",
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(cmdLabel, 2);
            grid.Children.Add(cmdLabel);

            var cmdBox = new TextBox
            {
                Text = existing?.Command ?? "",
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(cmdBox, 3);
            grid.Children.Add(cmdBox);

            var instrLabel = new TextBlock
            {
                Text = "Instructions (copy/paste reference):",
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(instrLabel, 4);
            grid.Children.Add(instrLabel);

            var instrBox = new TextBox
            {
                Text = GetCustomLauncherInstructions(),
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(instrBox, 5);
            grid.Children.Add(instrBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 6);

            Style editorButtonStyle = GetDialogButtonStyle();

            var okButton = new Button
            {
                Content = "OK",
                Width = 75,
                Height = 25,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 75,
                Height = 25,
                IsCancel = true
            };
            if (editorButtonStyle != null)
            {
                okButton.Style = editorButtonStyle;
                cancelButton.Style = editorButtonStyle;
            }
            else
            {
                okButton.Background = themeBg;
                okButton.Foreground = themeFg;
                okButton.BorderBrush = themeFg;
                cancelButton.Background = themeBg;
                cancelButton.Foreground = themeFg;
                cancelButton.BorderBrush = themeFg;
            }
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            grid.Children.Add(buttonPanel);

            CustomClaudeLauncher result = null;
            okButton.Click += (s, args) =>
            {
                string nm = nameBox.Text?.Trim() ?? "";
                string cm = cmdBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(nm))
                {
                    MessageBox.Show("Name cannot be empty.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(cm))
                {
                    MessageBox.Show("Command cannot be empty.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                result = new CustomClaudeLauncher
                {
                    Name = nm,
                    Command = cm
                };
                dialog.DialogResult = true;
            };

            dialog.Content = grid;
            dialog.Loaded += (s, args) => nameBox.Focus();
            dialog.ShowDialog();
            return result;
        }

        /// <summary>
        /// Returns the static instructions text shown in the launcher editor.
        /// </summary>
        private static string GetCustomLauncherInstructions()
        {
            return
@"1) Install Ollama:
   Open PowerShell as Admin and run:
   irm https://ollama.com/install.ps1 | iex

2) Install local coding model:
   Open cmd and run:
   ollama pull qwen3-coder:30b

3) Configure command above to start Claude Code with the local coding model, e.g.:
   ollama launch claude --model qwen3-coder:30b

Notes:
- The command runs in your solution's working directory.
- Claude usage tracking, model selection and the update agent button are hidden
  while a custom launcher is active (cloud features don't apply to local models).
- You can configure multiple launchers (one per model) and switch between them
  from the provider menu.";
        }

        #endregion
    }
}
