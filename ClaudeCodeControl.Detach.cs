/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Terminal detach/attach logic - allows detaching the terminal to a separate VS tool window tab
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Detach Fields

        /// <summary>
        /// Reference to the detached terminal tool window
        /// </summary>
        private DetachedTerminalToolWindow _detachedTerminalWindow;

        /// <summary>
        /// The WinForms panel inside the detached tool window
        /// </summary>
        private System.Windows.Forms.Panel _detachedTerminalPanel;

        /// <summary>
        /// Whether the terminal is currently detached
        /// </summary>
        private bool _isTerminalDetached;

        /// <summary>
        /// Guard flag to prevent double-subscribing to Closed event
        /// </summary>
        private bool _detachedClosedSubscribed;

        /// <summary>
        /// Guard flag to prevent double-subscribing to VisibilityChanged event
        /// </summary>
        private bool _detachedVisibilitySubscribed;

        #endregion

        #region Active Panel Property

        /// <summary>
        /// Returns the currently active terminal panel (detached or main)
        /// </summary>
        private System.Windows.Forms.Panel ActiveTerminalPanel
        {
            get
            {
                if (_isTerminalDetached && _detachedTerminalPanel != null)
                {
                    return _detachedTerminalPanel;
                }
                return terminalPanel;
            }
        }

        #endregion

        #region Detach/Attach Methods

        /// <summary>
        /// Detaches the terminal from the main control into a separate VS tool window tab
        /// </summary>
        private async System.Threading.Tasks.Task DetachTerminalAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
            {
                return;
            }

            try
            {
                // Get the VS package to find/create the tool window
                var package = await GetPackageAsync();
                if (package == null)
                {
                    Debug.WriteLine("DetachTerminalAsync: Could not get package");
                    return;
                }

                // Find or create the detached terminal tool window
                _detachedTerminalWindow = package.FindToolWindow(typeof(DetachedTerminalToolWindow), 0, true) as DetachedTerminalToolWindow;
                if (_detachedTerminalWindow == null)
                {
                    Debug.WriteLine("DetachTerminalAsync: Could not create detached terminal tool window");
                    return;
                }

                _detachedTerminalPanel = _detachedTerminalWindow.TerminalPanel;

                // Set background color to match current theme
                var bgColor = GetTerminalBackgroundColor();
                _detachedTerminalPanel.BackColor = bgColor;

                // Subscribe to Closed event (with guard)
                if (!_detachedClosedSubscribed)
                {
                    _detachedTerminalWindow.Closed += OnDetachedWindowClosed;
                    _detachedClosedSubscribed = true;
                }

                // Subscribe to VisibilityChanged event (with guard)
                if (!_detachedVisibilitySubscribed)
                {
                    _detachedTerminalWindow.VisibilityChanged += OnDetachedVisibilityChanged;
                    _detachedVisibilitySubscribed = true;
                }

                // Wire resize event to keep terminal fitting the panel
                _detachedTerminalPanel.Resize += DetachedPanel_Resize;

                // Show the tool window FIRST so the panel gets its dimensions
                var frame = _detachedTerminalWindow.Frame as IVsWindowFrame;
                frame?.Show();

                // Update caption with current provider name
                string providerName = GetCurrentProviderName();
                _detachedTerminalWindow.UpdateCaption(providerName);

                // Wait for the tool window to be fully laid out before re-parenting
                await System.Threading.Tasks.Task.Delay(300);

                // Set detached flag BEFORE re-parent so ActiveTerminalPanel returns the correct panel
                _isTerminalDetached = true;

                // Now re-parent the terminal handle to the detached panel (panel has dimensions now)
                SetParent(terminalHandle, _detachedTerminalPanel.Handle);
                ShowWindow(terminalHandle, SW_SHOW);
                ResizeEmbeddedTerminal();

                // Retry resize after short delays to ensure terminal fills the panel
                await System.Threading.Tasks.Task.Delay(200);
                ResizeEmbeddedTerminal();
                await System.Threading.Tasks.Task.Delay(300);
                ResizeEmbeddedTerminal();

                // Save current splitter position and expand prompt area slightly
                if (_settings != null)
                {
                    var currentPos = FindSplitterPosition();
                    if (currentPos.HasValue && currentPos.Value > 0)
                    {
                        _settings.SplitterPosition = currentPos.Value;
                        // Expand prompt area by 80px for more comfortable editing while detached
                        SetSplitterPosition(currentPos.Value + 80);
                    }
                }

                // Hide terminal area (terminal moved to separate tab)
                // Keep splitter visible so user can resize the prompt area freely
                TerminalGroupBox.Visibility = Visibility.Collapsed;
                int terminalRow = (_settings?.InvertLayout == true) ? 0 : 2;
                MainGrid.RowDefinitions[terminalRow].MinHeight = 0;
                MainGrid.UpdateLayout();

                // Update button icon and tooltip to show "attach" (arrow pointing right into box)
                UpdateDetachButtonIcon(true);
                DetachTerminalButton.ToolTip = "Attach Terminal Back to Main Panel";

                // Save state
                if (_settings != null)
                {
                    _settings.IsTerminalDetached = true;
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error detaching terminal: {ex.Message}");
            }
        }

        /// <summary>
        /// Attaches the terminal back from the detached tool window to the main control
        /// </summary>
        /// <param name="skipCloseFrame">If true, does not close the detached window frame (used when window is already closing)</param>
        private async System.Threading.Tasks.Task AttachTerminalAsync(bool skipCloseFrame = false)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Guard against double-fire (Closed event can fire from multiple paths)
            if (!_isTerminalDetached)
            {
                return;
            }

            try
            {
                // Mark as not detached immediately to prevent re-entry
                _isTerminalDetached = false;

                // Restore main terminal area
                TerminalGroupBox.Visibility = Visibility.Visible;
                int terminalRow = (_settings?.InvertLayout == true) ? 0 : 2;
                MainGrid.RowDefinitions[terminalRow].MinHeight = 150;

                // Restore splitter to pre-detach position
                if (_settings != null && _settings.SplitterPosition > 0)
                {
                    SetSplitterPosition(_settings.SplitterPosition);
                }

                // Force layout recalculation
                MainGrid.UpdateLayout();

                // Re-parent the terminal handle back to the main panel
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle) && terminalPanel != null)
                {
                    SetParent(terminalHandle, terminalPanel.Handle);
                    ShowWindow(terminalHandle, SW_SHOW);
                    ResizeEmbeddedTerminal();
                }

                // Retry resize after short delays to ensure terminal fills the panel
                // (WPF-to-WinForms layout propagation may not be immediate)
                await System.Threading.Tasks.Task.Delay(200);
                ResizeEmbeddedTerminal();
                await System.Threading.Tasks.Task.Delay(300);
                ResizeEmbeddedTerminal();

                // Unwire events from detached window
                if (_detachedTerminalWindow != null)
                {
                    if (_detachedClosedSubscribed)
                    {
                        _detachedTerminalWindow.Closed -= OnDetachedWindowClosed;
                        _detachedClosedSubscribed = false;
                    }
                    if (_detachedVisibilitySubscribed)
                    {
                        _detachedTerminalWindow.VisibilityChanged -= OnDetachedVisibilityChanged;
                        _detachedVisibilitySubscribed = false;
                    }
                }

                // Unwire resize from detached panel
                if (_detachedTerminalPanel != null)
                {
                    _detachedTerminalPanel.Resize -= DetachedPanel_Resize;
                }

                // Close detached window frame if needed
                if (!skipCloseFrame && _detachedTerminalWindow?.Frame is IVsWindowFrame windowFrame)
                {
                    windowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                }

                // Clear references
                _detachedTerminalPanel = null;
                _detachedTerminalWindow = null;

                // Update button icon and tooltip to show "detach" (arrow pointing left out of box)
                UpdateDetachButtonIcon(false);
                DetachTerminalButton.ToolTip = "Detach Terminal to Separate Tab";

                // Save state
                if (_settings != null)
                {
                    _settings.IsTerminalDetached = false;
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error attaching terminal: {ex.Message}");
            }
        }

        /// <summary>
        /// Toggles between detached and attached terminal states
        /// </summary>
        private async System.Threading.Tasks.Task ToggleDetachAsync()
        {
            if (_isTerminalDetached)
            {
                await AttachTerminalAsync();
            }
            else
            {
                await DetachTerminalAsync();
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles the main tool window frame show notifications (tab activated, shown, etc.).
        /// This is more reliable than WPF IsVisibleChanged because it fires on every VS
        /// tab activation, even when WPF considers the control already visible.
        /// </summary>
        private void OnToolWindowFrameShow(object sender, int fShow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var frameShow = (__FRAMESHOW)fShow;

            bool activated = frameShow == __FRAMESHOW.FRAMESHOW_WinShown ||
                             frameShow == __FRAMESHOW.FRAMESHOW_WinRestored ||
                             frameShow == __FRAMESHOW.FRAMESHOW_WinMaximized ||
                             frameShow == __FRAMESHOW.FRAMESHOW_TabActivated;

            if (activated && _isTerminalDetached)
            {
                if (_detachedTerminalWindow?.Frame is IVsWindowFrame detachedFrame)
                {
                    detachedFrame.Show();
                }

                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    ShowWindow(terminalHandle, SW_SHOW);
                    ResizeEmbeddedTerminal();
                }
            }
        }

        /// <summary>
        /// Handles the detached window being closed by the user - re-attaches the terminal
        /// </summary>
        private void OnDetachedWindowClosed(object sender, EventArgs e)
        {
#pragma warning disable VSSDK007, VSTHRD110
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await AttachTerminalAsync(skipCloseFrame: true);
            });
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Handles visibility changes of the detached terminal window
        /// </summary>
        private void OnDetachedVisibilityChanged(object sender, bool isVisible)
        {
            if (isVisible && terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                ShowWindow(terminalHandle, SW_SHOW);
                ResizeEmbeddedTerminal();
            }
        }

        /// <summary>
        /// Handles resize of the detached panel to keep terminal fitting
        /// </summary>
        private void DetachedPanel_Resize(object sender, EventArgs e)
        {
            ResizeEmbeddedTerminal();
        }

        /// <summary>
        /// Handles the detach button click
        /// </summary>
        private void DetachTerminalButton_Click(object sender, RoutedEventArgs e)
        {
#pragma warning disable VSSDK007, VSTHRD110
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await ToggleDetachAsync();
            });
#pragma warning restore VSSDK007, VSTHRD110
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Updates the detach button icon: arrow left (detach) or arrow right (attach)
        /// </summary>
        /// <param name="isDetached">True = show attach icon (arrow right into box), False = show detach icon (arrow left out of box)</param>
        private void UpdateDetachButtonIcon(bool isDetached)
        {
            try
            {
                var canvas = new Canvas { Width = 16, Height = 14 };
                var iconBrush = (Brush)FindResource(VsBrushes.ToolWindowTextKey);

                var rect = new Rectangle
                {
                    Width = 10,
                    Height = 12,
                    Stroke = iconBrush,
                    StrokeThickness = 1.5,
                    Fill = Brushes.Transparent
                };

                if (isDetached)
                {
                    // Attach icon: box on left, arrow pointing right into box
                    Canvas.SetLeft(rect, 1);
                    Canvas.SetTop(rect, 1);
                    canvas.Children.Add(rect);

                    var line = new Line { X1 = 8, Y1 = 7, X2 = 15, Y2 = 7, Stroke = iconBrush, StrokeThickness = 1.5 };
                    canvas.Children.Add(line);

                    var arrow = new Polyline
                    {
                        Points = new PointCollection { new Point(12, 4), new Point(15, 7), new Point(12, 10) },
                        Stroke = iconBrush,
                        StrokeThickness = 1.5,
                        Fill = Brushes.Transparent
                    };
                    canvas.Children.Add(arrow);
                }
                else
                {
                    // Detach icon: box on right, arrow pointing left out of box
                    Canvas.SetLeft(rect, 5);
                    Canvas.SetTop(rect, 1);
                    canvas.Children.Add(rect);

                    var line = new Line { X1 = 8, Y1 = 7, X2 = 1, Y2 = 7, Stroke = iconBrush, StrokeThickness = 1.5 };
                    canvas.Children.Add(line);

                    var arrow = new Polyline
                    {
                        Points = new PointCollection { new Point(4, 4), new Point(1, 7), new Point(4, 10) },
                        Stroke = iconBrush,
                        StrokeThickness = 1.5,
                        Fill = Brushes.Transparent
                    };
                    canvas.Children.Add(arrow);
                }

                DetachButtonIcon.Child = canvas;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating detach button icon: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the display name of the currently selected provider
        /// </summary>
        private string GetCurrentProviderName()
        {
            if (_settings == null) return "Claude Code";

            switch (_settings.SelectedProvider)
            {
                case AiProvider.ClaudeCode:
                case AiProvider.ClaudeCodeWSL:
                    return "Claude Code";
                case AiProvider.CodexNative:
                case AiProvider.Codex:
                    return "Codex";
                case AiProvider.CursorAgentNative:
                case AiProvider.CursorAgent:
                    return "Cursor Agent";
                case AiProvider.QwenCode:
                    return "Qwen Code";
                case AiProvider.OpenCode:
                    return "Open Code";
                case AiProvider.Windsurf:
                    return "Windsurf";
                default:
                    return "Claude Code";
            }
        }

        #endregion
    }
}
