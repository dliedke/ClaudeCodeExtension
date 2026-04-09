/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Settings management for Claude Code extension
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using Newtonsoft.Json;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Settings Fields

        /// <summary>
        /// Configuration file name
        /// </summary>
        private const string ConfigurationFileName = "claudecode-settings.json";

        /// <summary>
        /// Full path to the configuration file
        /// </summary>
        private static readonly string ConfigurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension",
            ConfigurationFileName);

        /// <summary>
        /// Current settings instance
        /// </summary>
        private ClaudeCodeSettings _settings;

        /// <summary>
        /// Flag to prevent saving settings during initialization
        /// </summary>
        private bool _isInitializing = true;

        #endregion

        #region Settings Management

        /// <summary>
        /// Loads settings from the configuration file
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(ConfigurationPath))
                {
                    var json = File.ReadAllText(ConfigurationPath);
                    _settings = JsonConvert.DeserializeObject<ClaudeCodeSettings>(json) ?? new ClaudeCodeSettings();
                }
                else
                {
                    _settings = new ClaudeCodeSettings();

                    // Save the default settings to create the file
                    SaveDefaultSettings();
                }

                // Apply loaded settings to UI
                SendWithEnterCheckBox.IsChecked = _settings.SendWithEnter;

                if (_settings.SplitterPosition > 0)
                {
                    SetSplitterPosition(_settings.SplitterPosition);

                    // Re-apply after first layout pass completes — during Loaded the
                    // control may not have its final size yet, so the pixel value can
                    // be overridden by WPF layout recalculation
                    double savedPos = _settings.SplitterPosition;
#pragma warning disable VSTHRD001, VSTHRD110
                    _ = Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => SetSplitterPosition(savedPos)));
#pragma warning restore VSTHRD001, VSTHRD110
                }

                // Only apply if user has explicitly set a font size (0 = use VS default)
                if (_settings.PromptFontSize >= 8)
                {
                    PromptTextBox.FontSize = _settings.PromptFontSize;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading settings: {ex.Message}");
                _settings = new ClaudeCodeSettings();
            }
        }

        /// <summary>
        /// Saves default settings to create the initial configuration file
        /// </summary>
        private void SaveDefaultSettings()
        {
            try
            {
                var directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving default settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves current settings to the configuration file
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                // Don't save settings during initialization to prevent overwriting with default values
                if (_isInitializing)
                {
                    return;
                }

                if (_settings == null)
                    _settings = new ClaudeCodeSettings();

                // Update settings from UI
                _settings.SendWithEnter = SendWithEnterCheckBox.IsChecked == true;

                // Only update splitter position if we can get a valid value (not 0.0)
                // Skip when terminal is detached because the grid layout is collapsed
                // and FindSplitterPosition would return the full control height
                if (!_isTerminalDetached)
                {
                    var splitterPosition = FindSplitterPosition();
                    if (splitterPosition.HasValue && splitterPosition.Value > 0)
                    {
                        _settings.SplitterPosition = splitterPosition.Value;
                    }
                }

                // Save to file
                var directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        #endregion

        #region Splitter Position Management

        /// <summary>
        /// Finds the current splitter position in pixels
        /// </summary>
        /// <returns>The pixel height of the top row, or null if unable to determine</returns>
        private double? FindSplitterPosition()
        {
            try
            {
                var grid = MainGrid;
                if (grid?.RowDefinitions?.Count >= 3 && this.ActualHeight > 0)
                {
                    // ActualHeight is always the real rendered pixel height,
                    // regardless of whether the row uses Star or Pixel GridLength
                    double topHeight = grid.RowDefinitions[0].ActualHeight;
                    if (topHeight > 0)
                    {
                        return topHeight;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error finding splitter position: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Sets the splitter position to the specified pixel height
        /// </summary>
        /// <param name="position">The desired pixel height for the top row</param>
        private void SetSplitterPosition(double position)
        {
            try
            {
                var grid = MainGrid;
                if (grid?.RowDefinitions?.Count >= 3 && position > 0)
                {
                    // Set absolute height for the top row
                    grid.RowDefinitions[0].Height = new GridLength(position, GridUnitType.Pixel);
                    // Keep the bottom row as star to fill remaining space
                    grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting splitter position: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the splitter position after WPF has applied the final layout.
        /// This is required because GridSplitter uses a preview adorner while dragging,
        /// so the row ActualHeight can still be stale inside DragCompleted.
        /// </summary>
        private void SaveSplitterPositionAfterLayout()
        {
#pragma warning disable VSTHRD001, VSTHRD110
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    MainGrid?.UpdateLayout();

                    var splitterPosition = FindSplitterPosition();
                    if (splitterPosition.HasValue && splitterPosition.Value > 0)
                    {
                        if (_settings == null)
                        {
                            _settings = new ClaudeCodeSettings();
                        }

                        _settings.SplitterPosition = splitterPosition.Value;
                    }

                    SaveSettings();
                }));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        /// <summary>
        /// Handles splitter drag completed event to save the new position
        /// </summary>
        private void MainGridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            SaveSplitterPositionAfterLayout();
        }

        #endregion

        #region Settings Application

        /// <summary>
        /// Applies loaded settings to the UI elements
        /// </summary>
        private void ApplyLoadedSettings()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            // Ensure the send button visibility matches the checkbox state
            if (SendWithEnterCheckBox.IsChecked == true)
            {
                SendPromptButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                SendPromptButton.Visibility = Visibility.Visible;
            }

            // Apply layout inversion if enabled
            ApplyLayout();

            // Update provider selection and title
            UpdateProviderSelection();

            // Update model selection
            UpdateModelSelection();

            // Update effort selection
            UpdateEffortSelection();
        }

        #endregion

        #region Layout Inversion

        /// <summary>
        /// Applies or reverts the inverted layout based on settings.
        /// When inverted, the terminal is on top and the prompt area is on the bottom.
        /// </summary>
        private void ApplyLayout()
        {
            try
            {
                bool invert = _settings?.InvertLayout == true;

                if (invert)
                {
                    // Terminal on top (row 0), prompt on bottom (row 2)
                    System.Windows.Controls.Grid.SetRow(PromptSectionGrid, 2);
                    System.Windows.Controls.Grid.SetRow(TerminalGroupBox, 0);

                    // Swap MinHeights to match content
                    MainGrid.RowDefinitions[0].MinHeight = 150;
                    MainGrid.RowDefinitions[2].MinHeight = 80;

                    // Margins: match the regular layout spacing (10px gap across splitter)
                    TerminalGroupBox.Margin = new Thickness(6, 6, 6, 0);
                    PromptSectionGrid.Margin = new Thickness(6, 6, 6, 6);

                    // Hide terminal GroupBox header (redundant with tool window title)
                    TerminalGroupBox.Header = null;

                    // Reorder prompt section: buttons+checkbox on top (near splitter), prompt box below
                    System.Windows.Controls.Grid.SetRow(CheckboxRow, 0);
                    System.Windows.Controls.Grid.SetRow(ControlsRow, 1);
                    System.Windows.Controls.Grid.SetRow(PromptGroupBox, 2);
                    PromptSectionGrid.RowDefinitions[0].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
                    PromptSectionGrid.RowDefinitions[0].MinHeight = 0;
                    PromptSectionGrid.RowDefinitions[2].Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
                    PromptSectionGrid.RowDefinitions[2].MinHeight = 80;

                    // Adjust inner margins for inverted order
                    CheckboxRow.Margin = new Thickness(0, 0, 0, 4);
                    ControlsRow.Margin = new Thickness(0, 0, 0, 4);
                    PromptGroupBox.Margin = new Thickness(0, 2, 0, 0);
                }
                else
                {
                    // Default: Prompt on top (row 0), terminal on bottom (row 2)
                    System.Windows.Controls.Grid.SetRow(PromptSectionGrid, 0);
                    System.Windows.Controls.Grid.SetRow(TerminalGroupBox, 2);

                    // Restore MinHeights
                    MainGrid.RowDefinitions[0].MinHeight = 80;
                    MainGrid.RowDefinitions[2].MinHeight = 150;

                    // Restore margins
                    PromptSectionGrid.Margin = new Thickness(6, 6, 6, 0);
                    TerminalGroupBox.Margin = new Thickness(6);

                    // Restore terminal GroupBox header
                    TerminalGroupBox.Header = new System.Windows.Controls.TextBlock
                    {
                        Text = GetCurrentProviderName(),
                        Opacity = 0.93
                    };

                    // Restore prompt section order: prompt box on top, buttons+checkbox below
                    System.Windows.Controls.Grid.SetRow(PromptGroupBox, 0);
                    System.Windows.Controls.Grid.SetRow(ControlsRow, 1);
                    System.Windows.Controls.Grid.SetRow(CheckboxRow, 2);
                    PromptSectionGrid.RowDefinitions[0].Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
                    PromptSectionGrid.RowDefinitions[0].MinHeight = 80;
                    PromptSectionGrid.RowDefinitions[2].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
                    PromptSectionGrid.RowDefinitions[2].MinHeight = 0;

                    // Restore inner margins
                    CheckboxRow.Margin = new Thickness(0, 4, 0, 2);
                    ControlsRow.Margin = new Thickness(0, 4, 0, 0);
                    PromptGroupBox.Margin = new Thickness(0, 0, 0, 2);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying layout: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the Invert Layout menu item click
        /// </summary>
        private void InvertLayoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            _settings.InvertLayout = InvertLayoutMenuItem.IsChecked;

            // Reset splitter to proportional sizing so layout looks natural after swap
            MainGrid.RowDefinitions[0].Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            MainGrid.RowDefinitions[2].Height = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star);

            if (_settings.InvertLayout)
            {
                // When inverted, terminal (top) gets more space
                MainGrid.RowDefinitions[0].Height = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star);
                MainGrid.RowDefinitions[2].Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            }

            ApplyLayout();
            SaveSettings();
        }

        #endregion
    }
}
