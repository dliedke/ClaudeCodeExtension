/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
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
        private bool _isProgrammaticScroll = false; // Track programmatic scrolls to avoid disabling auto-scroll
        private DispatcherTimer _autoScrollDisableTimer;
        private int _previousFileCount = 0;
        private const int AutoScrollDisableDelayMs = 3000;

        // Search fields
        private List<SearchMatch> _searchMatches = new List<SearchMatch>();
        private int _currentMatchIndex = -1;
        private string _currentSearchText = "";
        private bool _isSearchActive = false;
        private int _highlightedFileIndex = -1;
        private int _highlightedLineIndex = -1;

        // Scroll position preservation for refresh
        private Dictionary<string, double> _savedScrollPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Wrapper class for DiffLine with file/line indices for highlighting
        /// </summary>
        private class DiffLineWrapper
        {
            public DiffLine Line { get; set; }
            public int FileIndex { get; set; }
            public int LineIndex { get; set; }
        }

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

            // Add keyboard shortcut handler for Ctrl+F
            PreviewKeyDown += OnPreviewKeyDown;

            // Detect user scroll to disable auto-scroll
            Loaded += (s, e) =>
            {
                FileListScrollViewer.ScrollChanged += OnFileListScrollViewerScrollChanged;
            };
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

                // Skip refresh if nothing has changed to prevent screen blinking
                if (!hasNewChanges)
                {
                    return;
                }

                // Reset user-disabled flag when going from empty to having files (fresh start after baseline reset)
                if (_previousFileCount == 0 && newFiles.Count > 0)
                {
                    _autoScrollUserDisabled = false;
                }

                _previousFileCount = newFiles.Count;

                _changedFiles = newFiles;

                // Update UI on UI thread (diffs are pre-computed on background thread by caller)
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
                    _diffLineTemplate = null; // Clear cached template to update colors
                    RefreshUI();
                });
                return;
            }

            DetectTheme();
            _diffLineTemplate = null; // Clear cached template to update colors
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

            // Save scroll positions of existing ListBoxes before clearing
            SaveScrollPositions();

            FileListPanel.Children.Clear();

            if (_changedFiles.Count == 0)
            {
                EmptyStateText.Visibility = Visibility.Visible;
                FileListScrollViewer.Visibility = Visibility.Collapsed;
                SummaryText.Text = "No changes detected";
                AdditionsText.Visibility = Visibility.Collapsed;
                DeletionsText.Visibility = Visibility.Collapsed;
                _savedScrollPositions.Clear();
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
            for (int fileIndex = 0; fileIndex < _changedFiles.Count; fileIndex++)
            {
                var file = _changedFiles[fileIndex];
                var fileItem = CreateFileItem(file, fileIndex);
                FileListPanel.Children.Add(fileItem);
            }

            UpdateExpandCollapseToggleState();

            // Restore scroll positions after layout is complete
            void RestoreAfterLayout(object s, EventArgs args)
            {
                FileListPanel.LayoutUpdated -= RestoreAfterLayout;
                RestoreScrollPositions();
            }
            FileListPanel.LayoutUpdated += RestoreAfterLayout;
        }

        private void SaveScrollPositions()
        {
            _savedScrollPositions.Clear();

            for (int i = 0; i < FileListPanel.Children.Count && i < _changedFiles.Count; i++)
            {
                if (FileListPanel.Children[i] is StackPanel container && container.Children.Count >= 2)
                {
                    if (container.Children[1] is ListBox listBox)
                    {
                        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
                        if (scrollViewer != null)
                        {
                            string filePath = _changedFiles[i].FilePath;
                            _savedScrollPositions[filePath] = scrollViewer.VerticalOffset;
                        }
                    }
                }
            }
        }

        private void RestoreScrollPositions()
        {
            for (int i = 0; i < FileListPanel.Children.Count && i < _changedFiles.Count; i++)
            {
                string filePath = _changedFiles[i].FilePath;
                if (_savedScrollPositions.TryGetValue(filePath, out double scrollPosition) && scrollPosition > 0)
                {
                    if (FileListPanel.Children[i] is StackPanel container && container.Children.Count >= 2)
                    {
                        if (container.Children[1] is ListBox listBox)
                        {
                            var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
                            if (scrollViewer != null)
                            {
                                scrollViewer.ScrollToVerticalOffset(scrollPosition);
                            }
                        }
                    }
                }
            }
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

        private UIElement CreateFileItem(ChangedFile file, int fileIndex)
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

            // Diff content panel (hidden by default) - using virtualized ListBox for performance
            var diffPanel = new ListBox
            {
                Visibility = file.IsExpanded ? Visibility.Visible : Visibility.Collapsed,
                Background = (Brush)FindResource(VsBrushes.ToolWindowBackgroundKey),
                Margin = new Thickness(20, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = (Style)FindResource("DiffLineContainerStyle"),
                // Enable virtualization
                MaxHeight = 600, // Fixed height enables virtualization
            };

            // Configure virtualization
            VirtualizingPanel.SetIsVirtualizing(diffPanel, true);
            VirtualizingPanel.SetVirtualizationMode(diffPanel, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(diffPanel, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(diffPanel, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(diffPanel, ScrollBarVisibility.Auto);

            // Store file index for scroll handling
            diffPanel.Tag = fileIndex;

            // Handle scroll to refresh highlighting for visible items
            diffPanel.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnDiffPanelScrollChanged));

            // Disable mouse wheel on inner ListBox - let parent ScrollViewer handle all scrolling
            // This prevents the nested scroll issue where inner scroll fights with outer scroll
            diffPanel.PreviewMouseWheel += OnDiffPanelPreviewMouseWheel;

            // Only populate diff lines when expanded (lazy loading for performance)
            if (file.IsExpanded && file.DiffLines != null)
            {
                PopulateDiffPanel(diffPanel, file, fileIndex);
            }

            // Click to expand/collapse
            headerBorder.MouseLeftButtonUp += (s, e) =>
            {
                file.IsExpanded = !file.IsExpanded;
                expandIndicator.Text = file.IsExpanded ? "\u25BC" : "\u25B6";
                diffPanel.Visibility = file.IsExpanded ? Visibility.Visible : Visibility.Collapsed;

                // Lazy populate on first expand
                if (file.IsExpanded && diffPanel.ItemsSource == null && file.DiffLines != null)
                {
                    PopulateDiffPanel(diffPanel, file, fileIndex);
                }

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

        private void PopulateDiffPanel(ListBox diffPanel, ChangedFile file, int fileIndex)
        {
            var wrappedLines = new List<DiffLineWrapper>();
            for (int i = 0; i < file.DiffLines.Count; i++)
            {
                wrappedLines.Add(new DiffLineWrapper
                {
                    Line = file.DiffLines[i],
                    FileIndex = fileIndex,
                    LineIndex = i
                });
            }
            diffPanel.ItemsSource = wrappedLines;
            diffPanel.ItemTemplate = CreateDiffLineDataTemplate();
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

        private DataTemplate _diffLineTemplate;

        private DataTemplate CreateDiffLineDataTemplate()
        {
            // Cache the template since it's the same for all lines
            if (_diffLineTemplate != null)
            {
                return _diffLineTemplate;
            }

            // Create a DataTemplate using FrameworkElementFactory
            var template = new DataTemplate(typeof(DiffLineWrapper));

            // Create the factory for the Border (container for consistent height)
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.HeightProperty, 20.0); // Fixed height for virtualization
            borderFactory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnDiffLineLoaded));
            borderFactory.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDiffLineDoubleClick));

            template.VisualTree = borderFactory;
            template.Seal();

            _diffLineTemplate = template;
            return _diffLineTemplate;
        }

        private void OnDiffLineDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (e.ClickCount == 2 && sender is Border border && border.DataContext is DiffLineWrapper wrapper)
            {

                if (wrapper.FileIndex >= 0 && wrapper.FileIndex < _changedFiles.Count)
                {
                    var file = _changedFiles[wrapper.FileIndex];

                    // Determine the target line number: prefer new line number, fall back to old
                    int lineNumber = wrapper.Line.NewLineNumber ?? wrapper.Line.OldLineNumber ?? 0;

                    OpenFileInEditor(file.FilePath, lineNumber);
                }

                e.Handled = true;
            }
        }

        private void OnDiffLineLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border border && border.DataContext is DiffLineWrapper wrapper)
            {
                // Subscribe to DataContextChanged for virtualization recycling (only once)
                if (border.Tag == null)
                {
                    border.Tag = true; // Mark as subscribed
                    border.DataContextChanged += OnDiffLineDataContextChanged;
                }

                // Check if grid already exists (virtualization recycling)
                if (border.Child is Grid existingGrid)
                {
                    // Always update to ensure highlighting is correct after scroll
                    UpdateDiffLineGrid(existingGrid, wrapper);
                    return;
                }

                // Create the grid structure on first load
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Line number
                var lineNum = new TextBlock
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                    FontSize = 11,
                    Padding = new Thickness(4, 2, 4, 2),
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lineNum, 0);
                grid.Children.Add(lineNum);

                // Indicator
                var indicator = new TextBlock
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                    FontSize = 11,
                    Padding = new Thickness(2, 2, 2, 2),
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(indicator, 1);
                grid.Children.Add(indicator);

                // Content
                var content = new TextBlock
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                    FontSize = 11,
                    Padding = new Thickness(4, 2, 4, 2),
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(content, 2);
                grid.Children.Add(content);

                border.Child = grid;
                UpdateDiffLineGrid(grid, wrapper);
            }
        }

        private void OnDiffLineDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Handle virtualization recycling - update content when DataContext changes
            if (sender is Border border && border.Child is Grid grid && e.NewValue is DiffLineWrapper wrapper)
            {
                UpdateDiffLineGrid(grid, wrapper);
            }
        }

        private void OnDiffPanelScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Only refresh when scroll actually changed position (not just layout changes)
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;

            // Only refresh if search is active and we have a highlight
            if (!_isSearchActive || _highlightedFileIndex < 0) return;

            if (sender is ListBox listBox && listBox.Tag is int fileIndex)
            {
                // Only process if this is the file containing the highlight
                if (fileIndex != _highlightedFileIndex) return;

                // Refresh the highlighted line's container
                var container = listBox.ItemContainerGenerator.ContainerFromIndex(_highlightedLineIndex) as ListBoxItem;
                if (container != null)
                {
                    var border = FindVisualChild<Border>(container);
                    if (border?.Child is Grid grid && border.DataContext is DiffLineWrapper wrapper)
                    {
                        UpdateDiffLineGrid(grid, wrapper);
                    }
                }
            }
        }

        private void OnDiffPanelPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is ListBox listBox))
                return;

            // Find the internal ScrollViewer of the ListBox
            var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
            if (scrollViewer == null)
                return;

            bool scrollingUp = e.Delta > 0;
            bool scrollingDown = e.Delta < 0;

            // Check boundaries
            const double tolerance = 1.0;
            bool hasScrollableContent = scrollViewer.ScrollableHeight > tolerance;
            bool innerAtTop = scrollViewer.VerticalOffset < tolerance;
            bool innerAtBottom = !hasScrollableContent ||
                                 scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - tolerance;

            bool parentAtTop = FileListScrollViewer.VerticalOffset < tolerance;
            bool parentAtBottom = FileListScrollViewer.VerticalOffset >=
                                  FileListScrollViewer.ScrollableHeight - tolerance;

            // If inner can scroll, let it handle naturally (don't set e.Handled)
            if (hasScrollableContent && !((scrollingUp && innerAtTop) || (scrollingDown && innerAtBottom)))
            {
                // Let ListBox handle it naturally
                return;
            }

            // Inner at boundary - handle the event to prevent ListBox from doing anything weird
            e.Handled = true;

            // If at absolute limits, do nothing
            if ((scrollingUp && parentAtTop) || (scrollingDown && parentAtBottom))
            {
                return;
            }

            // Scroll the parent
            _isProgrammaticScroll = true;
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    if (scrollingDown)
                        FileListScrollViewer.LineDown();
                    else
                        FileListScrollViewer.LineUp();
                }
            }
            finally
            {
                _isProgrammaticScroll = false;
            }
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;

                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void UpdateDiffLineGrid(Grid grid, DiffLineWrapper wrapper)
        {
            var line = wrapper.Line;

            // Check if this line is the current search highlight using indices
            bool isCurrentSearchMatch = _isSearchActive &&
                                        wrapper.FileIndex == _highlightedFileIndex &&
                                        wrapper.LineIndex == _highlightedLineIndex;

            // Apply colors based on line type
            Brush background;
            Brush foreground;

            if (isCurrentSearchMatch)
            {
                // Use search highlight colors
                background = _isDarkTheme
                    ? (Brush)FindResource("SearchCurrentHighlightDark")
                    : (Brush)FindResource("SearchCurrentHighlight");
                foreground = _isDarkTheme ? Brushes.White : Brushes.Black;
            }
            else
            {
                switch (line.Type)
                {
                    case DiffLineType.Added:
                        background = _isDarkTheme
                            ? (Brush)FindResource("AddedLineBackground")
                            : (Brush)FindResource("AddedLineBackgroundLight");
                        foreground = _isDarkTheme
                            ? (Brush)FindResource("AddedLineForeground")
                            : (Brush)FindResource("AddedLineForegroundLight");
                        break;
                    case DiffLineType.Removed:
                        background = _isDarkTheme
                            ? (Brush)FindResource("RemovedLineBackground")
                            : (Brush)FindResource("RemovedLineBackgroundLight");
                        foreground = _isDarkTheme
                            ? (Brush)FindResource("RemovedLineForeground")
                            : (Brush)FindResource("RemovedLineForegroundLight");
                        break;
                    default:
                        background = Brushes.Transparent;
                        foreground = (Brush)FindResource(VsBrushes.WindowTextKey);
                        break;
                }
            }

            grid.Background = background;

            // Update text values and colors
            if (grid.Children.Count >= 3)
            {
                if (grid.Children[0] is TextBlock lineNum)
                {
                    lineNum.Text = line.DisplayLineNumber;
                    lineNum.Foreground = isCurrentSearchMatch
                        ? foreground
                        : (Brush)FindResource(VsBrushes.GrayTextKey);
                }
                if (grid.Children[1] is TextBlock indicator)
                {
                    indicator.Text = line.Indicator;
                    indicator.Foreground = foreground;
                }
                if (grid.Children[2] is TextBlock content)
                {
                    content.Text = line.Text;
                    content.Foreground = foreground;
                }
            }
        }

        private void OpenFileInEditor(string filePath, int lineNumber = 0)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null && System.IO.File.Exists(filePath))
                {
                    dte.ItemOperations.OpenFile(filePath);

                    if (lineNumber > 0 && dte.ActiveDocument != null)
                    {
                        var selection = dte.ActiveDocument.Selection as EnvDTE.TextSelection;
                        if (selection != null)
                        {
                            selection.GotoLine(lineNumber, false);
                        }
                    }
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

            // Don't auto-scroll if search is active
            if (_isSearchActive)
            {
                return;
            }

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

            // Mark as programmatic scroll to avoid disabling auto-scroll
            _isProgrammaticScroll = true;
            FileListScrollViewer.ScrollToEnd();

            // Reset flag after layout is complete using a one-shot event handler
            void ResetFlag(object s, EventArgs args)
            {
                FileListScrollViewer.LayoutUpdated -= ResetFlag;
                _isProgrammaticScroll = false;
            }
            FileListScrollViewer.LayoutUpdated += ResetFlag;
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

        private void OnFileListScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Only process actual scroll position changes (not layout changes)
            if (e.VerticalChange == 0)
                return;

            // Skip if this is a programmatic scroll
            if (_isProgrammaticScroll)
                return;

            // If auto-scroll is enabled and user scrolled manually, disable it
            if (_autoScrollEnabled)
            {
                _autoScrollEnabled = false;
                _autoScrollUserDisabled = true;
                _autoScrollDisableTimer.Stop();
                UpdateAutoScrollButtonState();
            }
        }

        #endregion

        #region Search Methods

        /// <summary>
        /// Represents a search match in the diff view
        /// </summary>
        private class SearchMatch
        {
            public int FileIndex { get; set; }
            public int LineIndex { get; set; }
            public DiffLine Line { get; set; }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
           
            // Escape to close search
            if (e.Key == Key.Escape && SearchBar.Visibility == Visibility.Visible)
            {
                CloseSearchInternal();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Public method to open search - can be called from tool window
        /// </summary>
        public void ActivateSearch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenSearchInternal();
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (SearchBar.Visibility == Visibility.Visible)
            {
                CloseSearchInternal();
            }
            else
            {
                OpenSearchInternal();
            }
        }

        private void OpenSearchInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Disable auto-scroll when searching
            _autoScrollEnabled = false;
            _autoScrollUserDisabled = true;
            _autoScrollDisableTimer.Stop();
            UpdateAutoScrollButtonState();

            // Expand all files for easier searching
            foreach (var file in _changedFiles)
            {
                file.IsExpanded = true;
            }
            ExpandCollapseAllButton.IsChecked = true;
            RefreshUI();

            _isSearchActive = true;
            SearchBar.Visibility = Visibility.Visible;
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }

        private void CloseSearchInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _isSearchActive = false;
            _highlightedFileIndex = -1;
            _highlightedLineIndex = -1;
            SearchBar.Visibility = Visibility.Collapsed;
            _searchMatches.Clear();
            _currentMatchIndex = -1;
            _currentSearchText = "";
            SearchResultsText.Text = "";

            // Refresh to clear highlights
            if (_changedFiles.Count > 0)
            {
                RefreshUI();
            }
        }

        private void SearchCloseButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CloseSearchInternal();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Search is triggered by Enter key, not real-time
            // Clear results text when text changes to indicate new search needed
            if (_searchMatches.Count > 0 && SearchTextBox.Text != _currentSearchText)
            {
                SearchResultsText.Text = "Press Enter to search";
            }
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter)
            {
                // If search text changed or no search yet, perform new search
                if (SearchTextBox.Text != _currentSearchText || _searchMatches.Count == 0)
                {
                    PerformSearchInternal(SearchTextBox.Text);
                }
                else if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+Enter for previous match
                    NavigateToPreviousMatchInternal();
                }
                else
                {
                    // Enter for next match
                    NavigateToNextMatchInternal();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseSearchInternal();
                e.Handled = true;
            }
        }

        private void SearchPrevButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigateToPreviousMatchInternal();
        }

        private void SearchNextButton_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigateToNextMatchInternal();
        }

        private void PerformSearchInternal(string searchText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Ensure auto-scroll stays disabled during search
            _autoScrollEnabled = false;
            _autoScrollUserDisabled = true;

            // Clear previous highlight before resetting indices
            int prevFileIndex = _highlightedFileIndex;
            int prevLineIndex = _highlightedLineIndex;

            _currentSearchText = searchText;
            _searchMatches.Clear();
            _currentMatchIndex = -1;
            _highlightedFileIndex = -1;
            _highlightedLineIndex = -1;

            // Update the previously highlighted line to remove highlight
            UpdateHighlightForLine(prevFileIndex, prevLineIndex);

            if (string.IsNullOrWhiteSpace(searchText))
            {
                SearchResultsText.Text = "";
                RefreshUI();
                return;
            }

            // Find all matches across all files
            for (int fileIndex = 0; fileIndex < _changedFiles.Count; fileIndex++)
            {
                var file = _changedFiles[fileIndex];
                if (file.DiffLines == null) continue;

                for (int lineIndex = 0; lineIndex < file.DiffLines.Count; lineIndex++)
                {
                    var line = file.DiffLines[lineIndex];
                    if (line.Text != null && line.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _searchMatches.Add(new SearchMatch
                        {
                            FileIndex = fileIndex,
                            LineIndex = lineIndex,
                            Line = line
                        });
                    }
                }
            }

            UpdateSearchResultsTextInternal();

            // Navigate to first match if any
            if (_searchMatches.Count > 0)
            {
                _currentMatchIndex = 0;
                NavigateToCurrentMatch();
            }
            else
            {
                RefreshUI();
            }
        }

        private void NavigateToNextMatchInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_searchMatches.Count == 0) return;

            _currentMatchIndex = (_currentMatchIndex + 1) % _searchMatches.Count;
            NavigateToCurrentMatch();
        }

        private void NavigateToPreviousMatchInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_searchMatches.Count == 0) return;

            _currentMatchIndex = (_currentMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
            NavigateToCurrentMatch();
        }

        private void NavigateToCurrentMatch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_currentMatchIndex < 0 || _currentMatchIndex >= _searchMatches.Count) return;

            // Remember old highlight to clear it
            int oldFileIndex = _highlightedFileIndex;
            int oldLineIndex = _highlightedLineIndex;

            var match = _searchMatches[_currentMatchIndex];

            // Set the highlighted indices
            _highlightedFileIndex = match.FileIndex;
            _highlightedLineIndex = match.LineIndex;

            // Update only the affected lines instead of full UI rebuild
            UpdateHighlightForLine(oldFileIndex, oldLineIndex);
            UpdateHighlightForLine(_highlightedFileIndex, _highlightedLineIndex);

            UpdateSearchResultsTextInternal();

            // Scroll to the match
            ScrollToMatchInternal(match);
        }

        private void UpdateHighlightForLine(int fileIndex, int lineIndex)
        {
            if (fileIndex < 0 || lineIndex < 0 || fileIndex >= FileListPanel.Children.Count) return;

            var fileContainer = FileListPanel.Children[fileIndex] as StackPanel;
            if (fileContainer == null || fileContainer.Children.Count < 2) return;

            var diffPanel = fileContainer.Children[1] as ListBox;
            if (diffPanel == null) return;

            var container = diffPanel.ItemContainerGenerator.ContainerFromIndex(lineIndex) as ListBoxItem;
            if (container != null)
            {
                var border = FindVisualChild<Border>(container);
                if (border?.Child is Grid grid && border.DataContext is DiffLineWrapper wrapper)
                {
                    UpdateDiffLineGrid(grid, wrapper);
                }
            }
        }

        private void ScrollToMatchInternal(SearchMatch match)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Find the file panel in the UI
            if (match.FileIndex >= FileListPanel.Children.Count) return;

            var fileContainer = FileListPanel.Children[match.FileIndex] as StackPanel;
            if (fileContainer == null || fileContainer.Children.Count < 2) return;

            // Get the diff panel (ListBox)
            var diffPanel = fileContainer.Children[1] as ListBox;
            if (diffPanel == null) return;

            // Scroll the ListBox to the matching line
            if (match.LineIndex < diffPanel.Items.Count)
            {
                // First scroll the file container into view
                fileContainer.BringIntoView();

                // Scroll to the item in the ListBox
                diffPanel.ScrollIntoView(diffPanel.Items[match.LineIndex]);
            }
        }

        private void UpdateSearchResultsTextInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_searchMatches.Count == 0)
            {
                SearchResultsText.Text = string.IsNullOrWhiteSpace(_currentSearchText) ? "" : "No results";
            }
            else
            {
                SearchResultsText.Text = $"{_currentMatchIndex + 1} of {_searchMatches.Count}";
            }
        }

        #endregion
    }
}
