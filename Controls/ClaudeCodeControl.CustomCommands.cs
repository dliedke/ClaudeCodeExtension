/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: User-defined custom commands — configuration dialog, toolbar dropdown,
 *          and dispatch into the embedded terminal.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Toolbar Button + Dropdown Population

        /// <summary>
        /// Refreshes the visibility and menu contents of the custom-commands toolbar button.
        /// Hides the button entirely when no custom commands are configured.
        /// </summary>
        private void RefreshCustomCommandsButton()
        {
            try
            {
                if (CustomCommandsButton == null) return;

                var commands = _settings?.CustomCommands;
                bool hasAny = commands != null && commands.Count > 0;

                CustomCommandsButton.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;

                var menu = CustomCommandsButton.ContextMenu;
                if (menu == null) return;

                menu.Items.Clear();
                if (!hasAny) return;

                foreach (var cmd in commands)
                {
                    string label = string.IsNullOrWhiteSpace(cmd.Name) ? cmd.Command : cmd.Name;
                    var item = new MenuItem
                    {
                        Header = label,
                        ToolTip = cmd.Command,
                        Tag = cmd
                    };
                    item.Click += CustomCommandMenuItem_Click;
                    menu.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing custom commands button: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the toolbar custom-commands button click - opens the dropdown menu.
        /// </summary>
        private void CustomCommandsButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Handles a click on a single custom-command entry in the toolbar dropdown.
        /// Sends the configured command literal directly to the active code agent.
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void CustomCommandMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                var item = sender as MenuItem;
                var cmd = item?.Tag as CustomCommand;
                if (cmd == null || string.IsNullOrEmpty(cmd.Command)) return;

                await SendTextToTerminalAsync(cmd.Command);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending custom command: {ex.Message}");
                MessageBox.Show($"Failed to send custom command: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Configure Custom Commands Menu Handler

        /// <summary>
        /// Handles the "Configure Custom Commands..." menu item click.
        /// Opens the management dialog and refreshes the toolbar button on close.
        /// </summary>
        private void ConfigureCustomCommandsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_settings == null) _settings = new ClaudeCodeSettings();
                if (_settings.CustomCommands == null) _settings.CustomCommands = new List<CustomCommand>();

                ShowCustomCommandsDialog();

                SaveSettings();
                RefreshCustomCommandsButton();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error configuring custom commands: {ex.Message}");
                MessageBox.Show($"Error configuring custom commands: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Configuration Dialog

        /// <summary>
        /// Resolves VS theme brushes for use inside programmatically-built dialogs.
        /// Falls back to system colors when the resource lookup fails.
        /// </summary>
        private void GetThemeBrushes(out Brush background, out Brush foreground)
        {
            try
            {
                background = (SolidColorBrush)FindResource(VsBrushes.WindowKey);
                foreground = (SolidColorBrush)FindResource(VsBrushes.WindowTextKey);
            }
            catch
            {
                background = SystemColors.WindowBrush;
                foreground = SystemColors.WindowTextBrush;
            }
        }

        /// <summary>
        /// Resolves the shared <c>AdaptiveButtonStyle</c> from the user control's resources
        /// so dialog buttons get VS-theme-aware hover and pressed states instead of the
        /// default Aero light-blue (which washes out white text in dark theme).
        /// </summary>
        private Style GetDialogButtonStyle()
        {
            try
            {
                return TryFindResource("AdaptiveButtonStyle") as Style;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Shows the modal dialog for adding, editing, and removing custom commands.
        /// Mutates _settings.CustomCommands in place; caller is responsible for saving.
        /// </summary>
        private void ShowCustomCommandsDialog()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = "Configure Custom Commands",
                Width = 600,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 480,
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

            var label = new TextBlock
            {
                Text = "Custom commands appear in the toolbar dropdown next to the agent menu. " +
                       "Clicking one sends the configured text directly to the active code agent — " +
                       "useful for slash commands (e.g. /codex-review) or canned prompts.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            // Center: list of commands + side buttons
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

            // Render each entry as "Name — Command"
            Action refreshList = () =>
            {
                listBox.Items.Clear();
                foreach (var cmd in _settings.CustomCommands)
                {
                    string display = string.IsNullOrWhiteSpace(cmd.Name)
                        ? cmd.Command
                        : $"{cmd.Name}  —  {cmd.Command}";
                    var lbi = new ListBoxItem
                    {
                        Content = display,
                        Tag = cmd,
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

            // Add - opens the Name + Command input dialog
            addButton.Click += (s, args) =>
            {
                var newCmd = ShowCustomCommandEditorDialog(null, dialog);
                if (newCmd != null)
                {
                    _settings.CustomCommands.Add(newCmd);
                    refreshList();
                    listBox.SelectedIndex = _settings.CustomCommands.Count - 1;
                }
            };

            // Edit - opens the editor pre-filled with the selected entry
            editButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.CustomCommands.Count) return;
                var existing = _settings.CustomCommands[idx];
                var edited = ShowCustomCommandEditorDialog(existing, dialog);
                if (edited != null)
                {
                    _settings.CustomCommands[idx] = edited;
                    refreshList();
                    listBox.SelectedIndex = idx;
                }
            };

            // Remove - confirms then deletes
            removeButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.CustomCommands.Count) return;

                var cmd = _settings.CustomCommands[idx];
                string display = string.IsNullOrWhiteSpace(cmd.Name) ? cmd.Command : cmd.Name;
                var result = MessageBox.Show($"Remove custom command \"{display}\"?",
                    "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _settings.CustomCommands.RemoveAt(idx);
                    refreshList();
                    if (_settings.CustomCommands.Count > 0)
                    {
                        listBox.SelectedIndex = Math.Min(idx, _settings.CustomCommands.Count - 1);
                    }
                }
            };

            moveUpButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx <= 0) return;
                var item = _settings.CustomCommands[idx];
                _settings.CustomCommands.RemoveAt(idx);
                _settings.CustomCommands.Insert(idx - 1, item);
                refreshList();
                listBox.SelectedIndex = idx - 1;
            };

            moveDownButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.CustomCommands.Count - 1) return;
                var item = _settings.CustomCommands[idx];
                _settings.CustomCommands.RemoveAt(idx);
                _settings.CustomCommands.Insert(idx + 1, item);
                refreshList();
                listBox.SelectedIndex = idx + 1;
            };

            // Double-click an entry to edit
            listBox.MouseDoubleClick += (s, args) => editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Bottom button row
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
        /// Shows the small dialog for adding or editing a single custom command.
        /// </summary>
        /// <param name="existing">The command to edit, or null to add a new one.</param>
        /// <param name="owner">The parent dialog window for centering/modality.</param>
        /// <returns>A new or edited <see cref="CustomCommand"/>, or null if cancelled.</returns>
        private CustomCommand ShowCustomCommandEditorDialog(CustomCommand existing, Window owner)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = existing == null ? "Add Custom Command" : "Edit Custom Command",
                Width = 520,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Owner = owner
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameLabel = new TextBlock
            {
                Text = "Name (shown in dropdown):",
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
                Text = "Command (sent to agent verbatim — slash command or prompt):",
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
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(cmdBox, 3);
            Grid.SetRowSpan(cmdBox, 2);
            grid.Children.Add(cmdBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 5);

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

            CustomCommand result = null;
            okButton.Click += (s, args) =>
            {
                string nm = nameBox.Text?.Trim() ?? "";
                string cm = cmdBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(cm))
                {
                    MessageBox.Show("Command text cannot be empty.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                result = new CustomCommand
                {
                    Name = string.IsNullOrEmpty(nm) ? cm : nm,
                    Command = cm
                };
                dialog.DialogResult = true;
            };

            dialog.Content = grid;
            dialog.Loaded += (s, args) => nameBox.Focus();
            dialog.ShowDialog();
            return result;
        }

        #endregion

        #region Configure Devin Models Dialog

        /// <summary>
        /// Shows the modal dialog for adding, editing, removing, and reordering the Devin model
        /// list shown in the model menu. Mutates _settings.DevinModels in place; the caller is
        /// responsible for saving and refreshing the menu.
        /// </summary>
        private void ShowDevinModelsDialog()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = "Configure Devin Models",
                Width = 520,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                MinWidth = 420,
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

            var label = new TextBlock
            {
                Text = "These models appear in the model (🤖) menu when Devin is the active agent. " +
                       "Selecting one switches the model live via /model \"<name>\". Enter the exact " +
                       "name Devin expects.",
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
                foreach (var model in _settings.DevinModels)
                {
                    listBox.Items.Add(new ListBoxItem
                    {
                        Content = model,
                        Tag = model,
                        Background = themeBg,
                        Foreground = themeFg
                    });
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
                string newModel = ShowDevinModelEditorDialog(null, dialog);
                if (!string.IsNullOrWhiteSpace(newModel) && !_settings.DevinModels.Contains(newModel))
                {
                    _settings.DevinModels.Add(newModel);
                    refreshList();
                    listBox.SelectedIndex = _settings.DevinModels.Count - 1;
                }
            };

            editButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.DevinModels.Count) return;
                string edited = ShowDevinModelEditorDialog(_settings.DevinModels[idx], dialog);
                if (!string.IsNullOrWhiteSpace(edited))
                {
                    _settings.DevinModels[idx] = edited;
                    refreshList();
                    listBox.SelectedIndex = idx;
                }
            };

            removeButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.DevinModels.Count) return;
                var result = MessageBox.Show($"Remove model \"{_settings.DevinModels[idx]}\"?",
                    "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _settings.DevinModels.RemoveAt(idx);
                    refreshList();
                    if (_settings.DevinModels.Count > 0)
                    {
                        listBox.SelectedIndex = Math.Min(idx, _settings.DevinModels.Count - 1);
                    }
                }
            };

            moveUpButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx <= 0) return;
                var item = _settings.DevinModels[idx];
                _settings.DevinModels.RemoveAt(idx);
                _settings.DevinModels.Insert(idx - 1, item);
                refreshList();
                listBox.SelectedIndex = idx - 1;
            };

            moveDownButton.Click += (s, args) =>
            {
                int idx = listBox.SelectedIndex;
                if (idx < 0 || idx >= _settings.DevinModels.Count - 1) return;
                var item = _settings.DevinModels[idx];
                _settings.DevinModels.RemoveAt(idx);
                _settings.DevinModels.Insert(idx + 1, item);
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
        /// Shows the small dialog for adding or editing a single Devin model name.
        /// </summary>
        /// <param name="existing">The model name to edit, or null to add a new one.</param>
        /// <param name="owner">The parent dialog window for centering/modality.</param>
        /// <returns>The trimmed model name, or null if cancelled or empty.</returns>
        private string ShowDevinModelEditorDialog(string existing, Window owner)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = existing == null ? "Add Devin Model" : "Edit Devin Model",
                Width = 460,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false,
                Owner = owner
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameLabel = new TextBlock
            {
                Text = "Model name (sent as /model \"<name>\"):",
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(nameLabel, 0);
            grid.Children.Add(nameLabel);

            var nameBox = new TextBox
            {
                Text = existing ?? "",
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(nameBox, 1);
            grid.Children.Add(nameBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 3);

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

            string result = null;
            okButton.Click += (s, args) =>
            {
                string nm = nameBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(nm))
                {
                    MessageBox.Show("Model name cannot be empty.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                result = nm;
                dialog.DialogResult = true;
            };

            dialog.Content = grid;
            dialog.Loaded += (s, args) => nameBox.Focus();
            dialog.ShowDialog();
            return result;
        }

        #endregion
    }
}
