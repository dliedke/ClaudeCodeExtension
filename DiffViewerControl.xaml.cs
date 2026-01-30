/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Code-behind for the diff viewer control
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
using System.Windows.Threading;
using ClaudeCodeVS.Diff;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Diff viewer control that displays changed files with expandable diffs
    /// </summary>
    public partial class DiffViewerControl : UserControl
    {
        #region Fields

        private List<ChangedFile> _changedFiles;
        private bool _isDarkTheme = true;
        private double _zoomLevel = 1.0;
        private const double ZoomMin = 0.5;
        private const double ZoomMax = 3.0;
        private const double ZoomStep = 0.1;

        // Auto-scroll fields
        private bool _autoScrollEnabled = false;
        private bool _autoScrollUserDisabled = false; // Track if user manually disabled auto-scroll
        private bool _updatingAutoScrollButton = false; // Prevent re-entrant event handling
        private DispatcherTimer _autoScrollDisableTimer;
        private int _previousFileCount = 0;
        private const int AutoScrollDisableDelayMs = 3000;

        #endregion

        #region Constructor

        public DiffViewerControl()
        {
            InitializeComponent();
            _changedFiles = new List<ChangedFile>();

            // Subscribe to theme changes
            VSColorTheme.ThemeChanged += OnThemeChanged;
            Loaded += OnLoaded;

            // Initialize auto-scroll disable timer
            _autoScrollDisableTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AutoScrollDisableDelayMs)
            };
            _autoScrollDisableTimer.Tick += OnAutoScrollDisableTimerTick;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Fired when the user requests a baseline reset
        /// </summary>
        public event EventHandler ResetRequested;

        /// <summary>
        /// Updates the display with new changed files
        /// </summary>
        public void UpdateChangedFiles(List<ChangedFile> changedFiles)
        {
            try
            {
                var newFiles = changedFiles ?? new List<ChangedFile>();
                var expandedState = _changedFiles.ToDictionary(f => f.FilePath, f => f.IsExpanded, StringComparer.OrdinalIgnoreCase);
                bool expandAll = ExpandCollapseAllButton.IsChecked == true;

                foreach (var file in newFiles)
                {
                    if (expandedState.TryGetValue(file.FilePath, out bool isExpanded))
                    {
                        file.IsExpanded = isExpanded;
                    }
                    else
                    {
                        file.IsExpanded = expandAll;
                    }
                }

                // Detect if changes are actively happening (file count changed or content changed)
                bool hasNewChanges = newFiles.Count != _previousFileCount || HasContentChanges(newFiles);

                // Reset user-disabled flag when going from empty to having files (fresh start after baseline reset)
                if (_previousFileCount == 0 && newFiles.Count > 0)
                {
                    _autoScrollUserDisabled = false;
                }

                _previousFileCount = newFiles.Count;

                _changedFiles = newFiles;

                // Compute diffs for all files
                DiffComputer.ComputeDiffs(_changedFiles);

                // Update UI on UI thread
                if (!Dispatcher.CheckAccess())
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        RefreshUI();
                        HandleAutoScroll(hasNewChanges);
                    });
                }
                else
                {
                    RefreshUI();
                    HandleAutoScroll(hasNewChanges);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating changed files: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all displayed changes
        /// </summary>
        public void Clear()
        {
            if (!Dispatcher.CheckAccess())
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _changedFiles.Clear();
                    RefreshUI();
                });
                return;
            }

            _changedFiles.Clear();
            RefreshUI();
        }

        /// <summary>
        /// Shows or hides the reset baseline button
        /// </summary>
        public void SetResetBaselineVisible(bool isVisible)
        {
            if (!Dispatcher.CheckAccess())
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ResetBaselineButton.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                });
                return;
            }

            ResetBaselineButton.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Gets the total stats for the title
        /// </summary>
        public (int fileCount, int linesAdded, int linesRemoved) GetStats()
        {
            int totalAdded = _changedFiles.Sum(f => f.LinesAdded);
            int totalRemoved = _changedFiles.Sum(f => f.LinesRemoved);
            return (_changedFiles.Count, totalAdded, totalRemoved);
        }

        /// <summary>
        /// Enables auto-scroll and expands all files (called when user sends a prompt with auto-open enabled)
        /// </summary>
        public void EnableAutoScrollAndExpandAll()
        {
            if (!Dispatcher.CheckAccess())
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    EnableAutoScrollAndExpandAllInternal();
                });
                return;
            }

            EnableAutoScrollAndExpandAllInternal();
        }

        private void EnableAutoScrollAndExpandAllInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Enable auto-scroll
            _autoScrollEnabled = true;
            _autoScrollUserDisabled = false;
            _autoScrollDisableTimer.Stop();
            _autoScrollDisableTimer.Start();
            UpdateAutoScrollButtonState();

            // Expand all files
            ExpandCollapseAllButton.IsChecked = true;
            foreach (var file in _changedFiles)
            {
                file.IsExpanded = true;
            }
            RefreshUI();

            // Scroll to bottom
            ScrollToBottom();
        }

        #endregion

        #region Private Methods

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DetectTheme();
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    DetectTheme();
                    RefreshUI();
                });
                return;
            }

            DetectTheme();
            RefreshUI();
        }

        private void DetectTheme()
        {
            try
            {
                var brush = (SolidColorBrush)FindResource(VsBrushes.WindowKey);
                var color = brush.Color;
                // If background is dark (luminance < 0.5), we're in dark theme
                double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
                _isDarkTheme = luminance < 0.5;
            }
            catch
            {
                _isDarkTheme = true;
            }
        }

        private void RefreshUI()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FileListPanel.Children.Clear();

            if (_changedFiles.Count == 0)
            {
                EmptyStateText.Visibility = Visibility.Visible;
                FileListScrollViewer.Visibility = Visibility.Collapsed;
                SummaryText.Text = "No changes detected";
                AdditionsText.Visibility = Visibility.Collapsed;
                DeletionsText.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyStateText.Visibility = Visibility.Collapsed;
            FileListScrollViewer.Visibility = Visibility.Visible;
            ExpandCollapseAllButton.IsEnabled = true;

            // Update summary
            int totalAdded = _changedFiles.Sum(f => f.LinesAdded);
            int totalRemoved = _changedFiles.Sum(f => f.LinesRemoved);
            SummaryText.Text = $"{_changedFiles.Count} file{(_changedFiles.Count != 1 ? "s" : "")} changed";
            AdditionsText.Text = $"+{totalAdded}";
            AdditionsText.Visibility = Visibility.Visible;
            DeletionsText.Text = $"-{totalRemoved}";
            DeletionsText.Visibility = Visibility.Visible;

            // Create file items
            foreach (var file in _changedFiles)
            {
                var fileItem = CreateFileItem(file);
                FileListPanel.Children.Add(fileItem);
            }

            UpdateExpandCollapseToggleState();
        }

        private void ResetBaselineButton_Click(object sender, RoutedEventArgs e)
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ExpandCollapseAllButton_Checked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_changedFiles.Count == 0)
            {
                return;
            }

            foreach (var file in _changedFiles)
            {
                file.IsExpanded = true;
            }

            RefreshUI();
        }

        private void ExpandCollapseAllButton_Unchecked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_changedFiles.Count == 0)
            {
                return;
            }

            foreach (var file in _changedFiles)
            {
                file.IsExpanded = false;
            }

            RefreshUI();
        }

        private UIElement CreateFileItem(ChangedFile file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var container = new StackPanel();

            // File header row
            var headerGrid = new Grid
            {
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Expand/collapse indicator
            var expandIndicator = new TextBlock
            {
                Text = file.IsExpanded ? "\u25BC" : "\u25B6", // Down or right triangle
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(VsBrushes.WindowTextKey),
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(expandIndicator, 0);
            headerGrid.Children.Add(expandIndicator);

            // File name and path
            var fileNamePanel = new StackPanel { Orientation = Orientation.Horizontal };

            // Type indicator for new/deleted files
            if (!string.IsNullOrEmpty(file.TypeIndicator))
            {
                var typeText = new TextBlock
                {
                    Text = file.TypeIndicator + " ",
                    FontSize = 11,
                    Foreground = file.Type == ChangeType.Created
                        ? (_isDarkTheme ? Brushes.LightGreen : Brushes.DarkGreen)
                        : (_isDarkTheme ? Brushes.Salmon : Brushes.DarkRed),
                    VerticalAlignment = VerticalAlignment.Center
                };
                fileNamePanel.Children.Add(typeText);
            }

            var fileName = new TextBlock
            {
                Text = file.FileName,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(VsBrushes.WindowTextKey)
            };
            fileNamePanel.Children.Add(fileName);

            if (!string.IsNullOrEmpty(file.RelativePath))
            {
                var relativePath = new TextBlock
                {
                    Text = " " + file.RelativePath,
                    FontSize = 11,
                    Foreground = (Brush)FindResource(VsBrushes.GrayTextKey),
                    VerticalAlignment = VerticalAlignment.Center
                };
                fileNamePanel.Children.Add(relativePath);
            }

            Grid.SetColumn(fileNamePanel, 1);
            headerGrid.Children.Add(fileNamePanel);

            // Changes summary
            var changesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 8, 0)
            };

            var addedText = new TextBlock
            {
                Text = $"+{file.LinesAdded}",
                Foreground = _isDarkTheme
                    ? (Brush)FindResource("AddedLineForeground")
                    : (Brush)FindResource("AddedLineForegroundLight"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            };
            changesPanel.Children.Add(addedText);

            var removedText = new TextBlock
            {
                Text = $"-{file.LinesRemoved}",
                Foreground = _isDarkTheme
                    ? (Brush)FindResource("RemovedLineForeground")
                    : (Brush)FindResource("RemovedLineForegroundLight"),
                FontWeight = FontWeights.SemiBold
            };
            changesPanel.Children.Add(removedText);

            Grid.SetColumn(changesPanel, 2);
            headerGrid.Children.Add(changesPanel);

            // Header border with padding
            var headerBorder = new Border
            {
                Child = headerGrid,
                Padding = new Thickness(4, 6, 4, 6),
                BorderBrush = (Brush)FindResource(VsBrushes.ToolWindowBorderKey),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            // Mouse over effect
            headerBorder.MouseEnter += (s, e) =>
            {
                headerBorder.Background = (Brush)FindResource(VsBrushes.CommandBarHoverOverSelectedKey);
            };
            headerBorder.MouseLeave += (s, e) =>
            {
                headerBorder.Background = Brushes.Transparent;
            };

            // Diff content panel (hidden by default)
            var diffPanel = new StackPanel
            {
                Visibility = file.IsExpanded ? Visibility.Visible : Visibility.Collapsed,
                Background = (Brush)FindResource(VsBrushes.ToolWindowBackgroundKey),
                Margin = new Thickness(20, 0, 0, 0)
            };

            // Populate diff lines
            if (file.DiffLines != null)
            {
                foreach (var line in file.DiffLines)
                {
                    var lineElement = CreateDiffLineElement(line);
                    diffPanel.Children.Add(lineElement);
                }
            }

            // Click to expand/collapse
            headerBorder.MouseLeftButtonUp += (s, e) =>
            {
                file.IsExpanded = !file.IsExpanded;
                expandIndicator.Text = file.IsExpanded ? "\u25BC" : "\u25B6";
                diffPanel.Visibility = file.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                UpdateExpandCollapseToggleState();
            };

            // Double-click to open file
            headerBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenFileInEditor(file.FilePath);
                    e.Handled = true;
                }
            };

            container.Children.Add(headerBorder);
            container.Children.Add(diffPanel);

            return container;
        }

        private void UpdateExpandCollapseToggleState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_changedFiles.Count == 0)
            {
                return;
            }

            bool allExpanded = _changedFiles.All(file => file.IsExpanded);
            ExpandCollapseAllButton.IsChecked = allExpanded;
        }

        private UIElement CreateDiffLineElement(DiffLine line)
        {
            Brush background;
            Brush foreground;
            string indicator;

            switch (line.Type)
            {
                case DiffLineType.Added:
                    background = _isDarkTheme
                        ? (Brush)FindResource("AddedLineBackground")
                        : (Brush)FindResource("AddedLineBackgroundLight");
                    foreground = _isDarkTheme
                        ? (Brush)FindResource("AddedLineForeground")
                        : (Brush)FindResource("AddedLineForegroundLight");
                    indicator = "+";
                    break;
                case DiffLineType.Removed:
                    background = _isDarkTheme
                        ? (Brush)FindResource("RemovedLineBackground")
                        : (Brush)FindResource("RemovedLineBackgroundLight");
                    foreground = _isDarkTheme
                        ? (Brush)FindResource("RemovedLineForeground")
                        : (Brush)FindResource("RemovedLineForegroundLight");
                    indicator = "-";
                    break;
                default:
                    background = Brushes.Transparent;
                    foreground = (Brush)FindResource(VsBrushes.WindowTextKey);
                    indicator = " ";
                    break;
            }

            // Build line number text
            var lineNumText = "";
            if (line.OldLineNumber.HasValue && line.NewLineNumber.HasValue)
            {
                lineNumText = $"{line.NewLineNumber}";
            }
            else if (line.OldLineNumber.HasValue)
            {
                lineNumText = $"{line.OldLineNumber}";
            }
            else if (line.NewLineNumber.HasValue)
            {
                lineNumText = $"{line.NewLineNumber}";
            }

            var grid = new Grid { Background = background };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); // Line number
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // +/- indicator
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Content

            // Line number
            var lineNum = new TextBlock
            {
                Text = lineNumText,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 11,
                Foreground = (Brush)FindResource(VsBrushes.GrayTextKey),
                Padding = new Thickness(4, 2, 4, 2),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lineNum, 0);
            grid.Children.Add(lineNum);

            // Indicator
            var indicatorText = new TextBlock
            {
                Text = indicator,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 11,
                Foreground = foreground,
                Padding = new Thickness(2, 2, 2, 2),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(indicatorText, 1);
            grid.Children.Add(indicatorText);

            // Content
            var content = new TextBlock
            {
                Text = line.Text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 11,
                Foreground = foreground,
                Padding = new Thickness(4, 2, 4, 2),
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(content, 2);
            grid.Children.Add(content);

            return grid;
        }

        private void OpenFileInEditor(string filePath)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null && System.IO.File.Exists(filePath))
                {
                    dte.ItemOperations.OpenFile(filePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening file {filePath}: {ex.Message}");
            }
        }

        private void FileListScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Zoom in/out with CTRL+scroll
                double delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoomLevel + delta));
                ZoomTransform.ScaleX = _zoomLevel;
                ZoomTransform.ScaleY = _zoomLevel;
                e.Handled = true;
            }
        }

        #endregion

        #region Auto-Scroll Methods

        private void HandleAutoScroll(bool hasNewChanges)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (hasNewChanges && _changedFiles.Count > 0)
            {
                // Don't auto-enable if user manually disabled it
                if (_autoScrollUserDisabled)
                {
                    return;
                }

                // Enable auto-scroll when changes start happening
                if (!_autoScrollEnabled)
                {
                    _autoScrollEnabled = true;
                    UpdateAutoScrollButtonState();
                }

                // Reset the disable timer
                _autoScrollDisableTimer.Stop();
                _autoScrollDisableTimer.Start();

                // Scroll to bottom if auto-scroll is enabled
                if (_autoScrollEnabled)
                {
                    ScrollToBottom();
                }
            }
        }

        private bool HasContentChanges(List<ChangedFile> newFiles)
        {
            if (newFiles.Count != _changedFiles.Count)
                return true;

            // Compare total lines added/removed as a quick check for content changes
            int newTotalAdded = newFiles.Sum(f => f.LinesAdded);
            int newTotalRemoved = newFiles.Sum(f => f.LinesRemoved);
            int oldTotalAdded = _changedFiles.Sum(f => f.LinesAdded);
            int oldTotalRemoved = _changedFiles.Sum(f => f.LinesRemoved);

            return newTotalAdded != oldTotalAdded || newTotalRemoved != oldTotalRemoved;
        }

        private void ScrollToBottom()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // ScrollToEnd after layout updates
            FileListScrollViewer.ScrollToEnd();
        }

        private void OnAutoScrollDisableTimerTick(object sender, EventArgs e)
        {
            // DispatcherTimer fires on the UI thread, so we're already on the main thread
            _autoScrollDisableTimer.Stop();

            // Disable auto-scroll after period of inactivity
            // Note: Don't set _autoScrollUserDisabled here - timer auto-disable should allow re-enabling
            _autoScrollEnabled = false;
            UpdateAutoScrollButtonState();
        }

        private void UpdateAutoScrollButtonState()
        {
            // This method is always called from UI thread (either from HandleAutoScroll or from DispatcherTimer)
            _updatingAutoScrollButton = true;
            try
            {
                AutoScrollButton.IsChecked = _autoScrollEnabled;
                AutoScrollButton.ToolTip = _autoScrollEnabled
                    ? "Auto-scroll enabled (click to disable)"
                    : "Auto-scroll disabled (click to enable)";
            }
            finally
            {
                _updatingAutoScrollButton = false;
            }
        }

        private void AutoScrollButton_Checked(object sender, RoutedEventArgs e)
        {
            // Skip if this is a programmatic update
            if (_updatingAutoScrollButton)
                return;

            ThreadHelper.ThrowIfNotOnUIThread();
            _autoScrollEnabled = true;
            _autoScrollUserDisabled = false; // User manually enabled, allow auto-enabling again
            _autoScrollDisableTimer.Stop();
            _autoScrollDisableTimer.Start();
            ScrollToBottom();
        }

        private void AutoScrollButton_Unchecked(object sender, RoutedEventArgs e)
        {
            // Skip if this is a programmatic update
            if (_updatingAutoScrollButton)
                return;

            _autoScrollEnabled = false;
            _autoScrollUserDisabled = true; // User manually disabled, prevent auto-enabling
            _autoScrollDisableTimer.Stop();
        }

        #endregion
    }
}
