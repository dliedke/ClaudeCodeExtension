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
                    // When a forced theme (Dark/Light) is active, ignore VS theme
                    // changes entirely -- no panel update, no restart prompt.
                    if (_settings?.SelectedThemePreference != ThemePreference.Automatic)
                        return;

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

            // Skip the restart prompt when the user has forced a specific theme --
            // the VS theme change has no effect on the terminal colors in that case.
            if (_settings?.SelectedThemePreference != ThemePreference.Automatic)
                return;

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
                // If a forced theme preference is set, return the corresponding color directly
                if (_settings != null)
                {
                    if (_settings.SelectedThemePreference == ThemePreference.Dark)
                        return System.Drawing.Color.FromArgb(255, 30, 30, 30);
                    if (_settings.SelectedThemePreference == ThemePreference.Light)
                        return System.Drawing.Color.FromArgb(255, 246, 246, 246);
                }

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
            // Apply forced theme resource overrides to the WPF panel
            ApplyForcedThemeResources();

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

        /// <summary>
        /// Resource keys that are overridden when a forced theme is active.
        /// Stored so they can be removed when switching back to Automatic.
        /// </summary>
        private static readonly object[] _themeResourceKeys = new object[]
        {
            VsBrushes.WindowKey,
            VsBrushes.WindowTextKey,
            VsBrushes.ToolWindowBackgroundKey,
            VsBrushes.ToolWindowTextKey,
            VsBrushes.ToolWindowBorderKey,
            VsBrushes.CommandBarHoverOverSelectedKey,
            VsBrushes.CommandBarSelectedKey,
            VsBrushes.CommandBarSelectedBorderKey,
            VsBrushes.GrayTextKey
        };

        /// <summary>
        /// Applies or removes forced theme resource overrides on the UserControl.
        /// When Dark or Light is forced, the VS brush resource keys are overridden
        /// in this control's Resources dictionary so all child WPF elements pick up
        /// the forced colors via DynamicResource. When Automatic, overrides are
        /// removed so VS's native theme resources take effect.
        /// </summary>
        private void ApplyForcedThemeResources()
        {
            try
            {
                var pref = _settings?.SelectedThemePreference ?? ThemePreference.Automatic;

                if (pref == ThemePreference.Automatic)
                {
                    // Remove any forced overrides so VS dynamic resources apply
                    foreach (var key in _themeResourceKeys)
                    {
                        this.Resources.Remove(key);
                    }
                    return;
                }

                SolidColorBrush windowBg, windowFg, toolBg, toolFg, toolBorder;
                SolidColorBrush hoverBg, selectedBg, selectedBorder, grayText;

                if (pref == ThemePreference.Dark)
                {
                    // VS Dark theme palette
                    windowBg       = new SolidColorBrush(Color.FromRgb(30, 30, 30));    // #1E1E1E
                    windowFg       = new SolidColorBrush(Color.FromRgb(241, 241, 241)); // #F1F1F1
                    toolBg         = new SolidColorBrush(Color.FromRgb(37, 37, 38));    // #252526
                    toolFg         = new SolidColorBrush(Color.FromRgb(241, 241, 241)); // #F1F1F1
                    toolBorder     = new SolidColorBrush(Color.FromRgb(63, 63, 70));    // #3F3F46
                    hoverBg        = new SolidColorBrush(Color.FromRgb(62, 62, 64));    // #3E3E40
                    selectedBg     = new SolidColorBrush(Color.FromRgb(80, 80, 80));    // #505050
                    selectedBorder = new SolidColorBrush(Color.FromRgb(0, 122, 204));   // #007ACC
                    grayText       = new SolidColorBrush(Color.FromRgb(157, 157, 157)); // #9D9D9D
                }
                else // Light
                {
                    // VS Light theme palette
                    windowBg       = new SolidColorBrush(Color.FromRgb(246, 246, 246)); // #F6F6F6
                    windowFg       = new SolidColorBrush(Color.FromRgb(30, 30, 30));    // #1E1E1E
                    toolBg         = new SolidColorBrush(Color.FromRgb(238, 238, 242)); // #EEEEF2
                    toolFg         = new SolidColorBrush(Color.FromRgb(30, 30, 30));    // #1E1E1E
                    toolBorder     = new SolidColorBrush(Color.FromRgb(204, 206, 219)); // #CCCEDB
                    hoverBg        = new SolidColorBrush(Color.FromRgb(201, 222, 245)); // #C9DEF5
                    selectedBg     = new SolidColorBrush(Color.FromRgb(184, 214, 251)); // #B8D6FB
                    selectedBorder = new SolidColorBrush(Color.FromRgb(0, 122, 204));   // #007ACC
                    grayText       = new SolidColorBrush(Color.FromRgb(162, 164, 165)); // #A2A4A5
                }

                // Freeze brushes for performance
                windowBg.Freeze(); windowFg.Freeze(); toolBg.Freeze(); toolFg.Freeze();
                toolBorder.Freeze(); hoverBg.Freeze(); selectedBg.Freeze();
                selectedBorder.Freeze(); grayText.Freeze();

                // Override the VS resource keys in this control's dictionary.
                // DynamicResource bindings in child controls will pick these up.
                this.Resources[VsBrushes.WindowKey]                   = windowBg;
                this.Resources[VsBrushes.WindowTextKey]               = windowFg;
                this.Resources[VsBrushes.ToolWindowBackgroundKey]     = toolBg;
                this.Resources[VsBrushes.ToolWindowTextKey]           = toolFg;
                this.Resources[VsBrushes.ToolWindowBorderKey]         = toolBorder;
                this.Resources[VsBrushes.CommandBarHoverOverSelectedKey] = hoverBg;
                this.Resources[VsBrushes.CommandBarSelectedKey]       = selectedBg;
                this.Resources[VsBrushes.CommandBarSelectedBorderKey] = selectedBorder;
                this.Resources[VsBrushes.GrayTextKey]                 = grayText;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying forced theme resources: {ex.Message}");
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
