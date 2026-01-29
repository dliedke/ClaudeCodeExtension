/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Theme management and Visual Studio theme integration
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
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

                // Subscribe to VS theme change event instead of polling
                VSColorTheme.ThemeChanged += OnVSThemeChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up theme change events: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles Visual Studio theme changes (fire-and-forget by design)
        /// </summary>
#pragma warning disable VSSDK007 // Intentional fire-and-forget for event handler
        private void OnVSThemeChanged(ThemeChangedEventArgs e)
        {
            Debug.WriteLine("VS theme changed - updating terminal theme");
            // Switch to UI thread using VS threading pattern
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    UpdateTerminalTheme();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error handling theme change: {ex.Message}");
                }
            });
        }
#pragma warning restore VSSDK007

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

                // Ensure terminal window is visible and properly sized when tab is switched back
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    Debug.WriteLine("Control became visible - ensuring terminal window is shown");
                    ShowWindow(terminalHandle, SW_SHOW);
                    ResizeEmbeddedTerminal();
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

        #endregion

        #region Theme Cleanup

        /// <summary>
        /// Unsubscribes from theme change events to prevent memory leaks
        /// </summary>
        private void CleanupThemeEvents()
        {
            try
            {
                VSColorTheme.ThemeChanged -= OnVSThemeChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up theme events: {ex.Message}");
            }
        }

        #endregion
    }
}