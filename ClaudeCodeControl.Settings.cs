/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
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
                    Debug.WriteLine($"Loaded settings: SendWithEnter={_settings.SendWithEnter}, SplitterPosition={_settings.SplitterPosition}");
                }
                else
                {
                    _settings = new ClaudeCodeSettings();
                    Debug.WriteLine($"No settings file found, using defaults: SendWithEnter={_settings.SendWithEnter}, SplitterPosition={_settings.SplitterPosition}");

                    // Save the default settings to create the file
                    SaveDefaultSettings();
                }

                // Apply loaded settings to UI
                SendWithEnterCheckBox.IsChecked = _settings.SendWithEnter;
                Debug.WriteLine($"Set checkbox to: {_settings.SendWithEnter}");

                if (_settings.SplitterPosition > 0)
                {
                    SetSplitterPosition(_settings.SplitterPosition);
                    Debug.WriteLine($"Set splitter position to: {_settings.SplitterPosition}");
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
                Debug.WriteLine($"Default settings created at: {ConfigurationPath}");
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
                    Debug.WriteLine("Skipping save during initialization");
                    return;
                }

                if (_settings == null)
                    _settings = new ClaudeCodeSettings();

                // Update settings from UI
                _settings.SendWithEnter = SendWithEnterCheckBox.IsChecked == true;
                Debug.WriteLine($"Saving SendWithEnter: {_settings.SendWithEnter}");
                Debug.WriteLine($"Saving SelectedProvider: {_settings.SelectedProvider}");

                // Only update splitter position if we can get a valid value (not 0.0)
                var splitterPosition = FindSplitterPosition();
                if (splitterPosition.HasValue && splitterPosition.Value > 0)
                {
                    _settings.SplitterPosition = splitterPosition.Value;
                    Debug.WriteLine($"Saving splitter position: {_settings.SplitterPosition}");
                }
                else
                {
                    Debug.WriteLine($"Not saving splitter position, got: {splitterPosition}");
                }

                // Save to file
                var directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
                Debug.WriteLine($"Settings saved to: {ConfigurationPath}");
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
                    var topRow = grid.RowDefinitions[0];
                    var splitterRow = grid.RowDefinitions[1];
                    var bottomRow = grid.RowDefinitions[2];

                    // Calculate the actual height of the top row
                    double topHeight = 0;
                    if (topRow.Height.IsStar)
                    {
                        double totalStars = topRow.Height.Value + bottomRow.Height.Value;
                        topHeight = (topRow.Height.Value / totalStars) * (this.ActualHeight - splitterRow.Height.Value);
                    }
                    else if (topRow.Height.IsAbsolute)
                    {
                        topHeight = topRow.Height.Value;
                    }

                    // Return the actual pixel height for saving
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
        /// Handles splitter drag completed event to save the new position
        /// </summary>
        private void MainGridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            SaveSettings();
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

            // Update provider selection and title
            UpdateProviderSelection();

            // Update model selection
            UpdateModelSelection();
        }

        #endregion
    }
}