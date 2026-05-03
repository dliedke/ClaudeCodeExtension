/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
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
using Microsoft.VisualStudio.Shell.Interop;

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
        /// Debounce timer for theme change restart prompt
        /// </summary>
        private System.Windows.Threading.DispatcherTimer _themeChangeDebounceTimer;

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
            // Switch to UI thread using VS threading pattern
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    UpdateTerminalTheme();
                    UpdateDetachButtonIcon(_isTerminalDetached);

                    // Debounce theme change restart prompt (event may fire multiple times)
                    if (_themeChangeDebounceTimer == null)
                    {
                        _themeChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer();
                        _themeChangeDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
                        _themeChangeDebounceTimer.Tick += OnThemeChangeDebounceTimerTick;
                    }

                    _themeChangeDebounceTimer.Stop();
                    _themeChangeDebounceTimer.Start();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error handling theme change: {ex.Message}");
                }
            });
        }
#pragma warning restore VSSDK007

        /// <summary>
        /// Debounced handler for theme change restart prompt
        /// </summary>
        private void OnThemeChangeDebounceTimerTick(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _themeChangeDebounceTimer?.Stop();

            // Prompt user to restart terminal if it's running to apply new theme colors
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                var result = MessageBox.Show(
                    "Theme changed. Restart the AI code agent to apply the new terminal colors?",
                    "Theme Changed",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional for restart
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await RestartTerminalWithSelectedProviderAsync();
                    });
#pragma warning restore VSSDK007
                }
            }
        }

        #endregion

        #region Theme Update Logic

        /// <summary>
        /// Handles visibility changes to update theme when control becomes visible
        /// </summary>
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (this.IsVisible)
            {
                // Only update theme when visible - initialization is handled by Loaded event
                UpdateTerminalTheme();

                if (_isTerminalDetached)
                {
                    // When terminal is detached, auto-focus the detached terminal tab
                    // so the user sees the AI output alongside the prompt area
                    if (_detachedTerminalWindow?.Frame is IVsWindowFrame detachedFrame)
                    {
                        detachedFrame.Show();
                    }

                    // Always ensure the terminal handle is visible and properly sized
                    // in the detached panel. The VisibilityChanged event on the detached
                    // window may not fire if it was already considered visible (e.g.,
                    // TabDeactivated was not received), so we must refresh directly.
                    if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                    {
                        ShowWindow(terminalHandle, SW_SHOW);
                        ResizeEmbeddedTerminal();
                    }
                }
                else if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    // Ensure terminal window is visible and properly sized when tab is switched back
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
                }

                // Also update detached panel if terminal is detached
                if (_isTerminalDetached && _detachedTerminalWindow != null)
                {
                    _detachedTerminalWindow.UpdateTheme(newColor);
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

                if (_themeChangeDebounceTimer != null)
                {
                    _themeChangeDebounceTimer.Stop();
                    _themeChangeDebounceTimer = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up theme events: {ex.Message}");
            }
        }

        #endregion
    }
}
