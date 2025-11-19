/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Theme management and Visual Studio theme integration
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Theme Fields

        /// <summary>
        /// Last applied terminal background color to detect theme changes
        /// </summary>
        private System.Drawing.Color _lastTerminalColor = System.Drawing.Color.Black;

        /// <summary>
        /// Timer to periodically check for theme changes
        /// </summary>
        private DispatcherTimer _themeCheckTimer;

        #endregion

        #region Theme Initialization

        /// <summary>
        /// Sets up event handlers for theme change detection
        /// </summary>
        private void SetupThemeChangeEvents()
        {
            try
            {
                // Listen for when the control becomes visible to update theme and initialize terminal
                this.IsVisibleChanged += OnVisibilityChanged;

                // Set up a timer to periodically check for theme changes
                _themeCheckTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _themeCheckTimer.Tick += (s, e) => CheckAndUpdateTheme();
                _themeCheckTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up theme change events: {ex.Message}");
            }
        }

        #endregion

        #region Theme Update Logic

        /// <summary>
        /// Handles visibility changes to update theme when control becomes visible
        /// </summary>
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.IsVisible)
            {
                // Only update theme when visible - initialization is handled by Loaded event
                UpdateTerminalTheme();

                // Restart theme check timer when control becomes visible
                if (_themeCheckTimer != null && !_themeCheckTimer.IsEnabled)
                {
                    _themeCheckTimer.Start();
                    Debug.WriteLine("Theme check timer restarted (control visible)");
                }

                // Ensure terminal window is visible and properly sized when tab is switched back
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    Debug.WriteLine("Control became visible - ensuring terminal window is shown");
                    ShowWindow(terminalHandle, SW_SHOW);
                    ResizeEmbeddedTerminal();
                }
            }
            else
            {
                // Stop theme check timer when control becomes invisible to save resources
                if (_themeCheckTimer != null && _themeCheckTimer.IsEnabled)
                {
                    _themeCheckTimer.Stop();
                    Debug.WriteLine("Theme check timer stopped (control invisible)");
                }
            }
        }

        /// <summary>
        /// Gets the terminal background color from Visual Studio's current theme
        /// </summary>
        /// <returns>The background color for the terminal</returns>
        private System.Drawing.Color GetTerminalBackgroundColor()
        {
            try
            {
                // Get the VS theme color for window background
                var brush = (SolidColorBrush)FindResource(VsBrushes.WindowKey);
                var wpfColor = brush.Color;

                // Convert WPF color to System.Drawing color
                return System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
            }
            catch
            {
                // Fallback to black if theme color cannot be retrieved
                return System.Drawing.Color.Black;
            }
        }

        /// <summary>
        /// Updates the terminal panel's background color to match VS theme
        /// </summary>
        private void UpdateTerminalTheme()
        {
            if (terminalPanel != null)
            {
                var newColor = GetTerminalBackgroundColor();
                if (terminalPanel.BackColor != newColor)
                {
                    terminalPanel.BackColor = newColor;
                    _lastTerminalColor = newColor;
                    Debug.WriteLine($"Terminal theme updated to: {newColor}");
                }
            }
        }

        /// <summary>
        /// Periodically checks if the theme has changed and updates accordingly
        /// </summary>
        private void CheckAndUpdateTheme()
        {
            try
            {
                var currentColor = GetTerminalBackgroundColor();
                if (currentColor != _lastTerminalColor)
                {
                    UpdateTerminalTheme();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking theme: {ex.Message}");
            }
        }

        #endregion
    }
}