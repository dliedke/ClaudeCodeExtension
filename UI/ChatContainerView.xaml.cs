/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Content of the single "Claude Code Chat" window: a strip of chat tabs (title with status glyph,
 *          close button) over one host grid per chat, of which only the selected one is shown; with no chat
 *          open, a card offering a new chat, today's sessions or the session history.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeCodeVS.UI
{
    /// <summary>One chat's tab in the strip. <see cref="Host"/> is where its transcript is parented.</summary>
    public sealed class ChatContainerTabItem : INotifyPropertyChanged
    {
        private string _caption = string.Empty;
        private bool _isSelected;

        internal ChatContainerTabItem(Grid host)
        {
            Host = host;
        }

        internal Grid Host { get; }

        public string Caption
        {
            get { return _caption; }
            set
            {
                if (_caption == value) return;
                _caption = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            internal set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class ChatContainerView : UserControl
    {
        private readonly ObservableCollection<ChatContainerTabItem> _tabs = new ObservableCollection<ChatContainerTabItem>();

        /// <summary>"Start a new chat" on the card shown while no chat is open.</summary>
        public event EventHandler NewChatRequested;

        /// <summary>"Restore today's sessions" on the same card.</summary>
        public event EventHandler ResumeTodayRequested;

        /// <summary>The Session History icon button beside it.</summary>
        public event EventHandler SessionHistoryRequested;

        /// <summary>A tab became the selected one (by click, keyboard or code).</summary>
        internal event EventHandler<ChatContainerTabItem> TabSelected;

        /// <summary>The user asked to close a tab (✕ or middle click). The owner closes it.</summary>
        internal event EventHandler<ChatContainerTabItem> TabCloseRequested;

        public ChatContainerView()
        {
            InitializeComponent();
            TabStrip.ItemsSource = _tabs;
            TabStrip.PreviewMouseMove += TabStrip_PreviewMouseMove;
            TabStrip.PreviewMouseLeftButtonUp += (s, e) => EndTabDrag();
            TabStrip.LostMouseCapture += (s, e) => EndTabDrag();
            UpdateEmptyState();
        }

        // Drag to reorder: the pressed tab, where the press happened, and whether it became a drag.
        private ChatContainerTabItem _dragItem;
        private Point _dragStart;
        private bool _dragging;

        internal ChatContainerTabItem SelectedTab => _tabs.FirstOrDefault(t => t.IsSelected);

        /// <summary>The selected chat's transcript, or null.</summary>
        internal ChatTranscriptView SelectedTranscript =>
            SelectedTab?.Host.Children.OfType<ChatTranscriptView>().FirstOrDefault();

        internal ChatContainerTabItem AddTab()
        {
            var host = new Grid { Visibility = Visibility.Collapsed };
            ContentHost.Children.Add(host);

            var item = new ChatContainerTabItem(host);
            _tabs.Add(item);
            UpdateEmptyState();
            return item;
        }

        /// <summary>Removes a tab and its host; the neighbor to its right (else left) becomes selected.</summary>
        internal void RemoveTab(ChatContainerTabItem item)
        {
            int index = _tabs.IndexOf(item);
            if (index < 0) return;

            bool wasSelected = item.IsSelected;
            item.Host.Children.Clear();
            ContentHost.Children.Remove(item.Host);
            _tabs.RemoveAt(index);

            if (wasSelected && _tabs.Count > 0)
            {
                Select(_tabs[Math.Min(index, _tabs.Count - 1)], focusComposer: false);
            }

            UpdateEmptyState();
        }

        internal void Select(ChatContainerTabItem item, bool focusComposer)
        {
            if (item == null || !_tabs.Contains(item)) return;

            bool changed = !item.IsSelected;
            foreach (ChatContainerTabItem tab in _tabs)
            {
                tab.IsSelected = ReferenceEquals(tab, item);
                tab.Host.Visibility = tab.IsSelected ? Visibility.Visible : Visibility.Collapsed;
            }

            BringTabIntoView(item);

            if (focusComposer)
            {
                // The host was collapsed until now: focus once it has been laid out.
#pragma warning disable VSTHRD001, VSTHRD110
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    item.Host.Children.OfType<ChatTranscriptView>().FirstOrDefault()?.FocusComposer()));
#pragma warning restore VSTHRD001, VSTHRD110
            }

            if (changed)
            {
                TabSelected?.Invoke(this, item);
            }
        }

        /// <summary>
        /// Scrolls just enough to show the tab, but stops on a tab's left edge so the first visible
        /// tab is never cut (a cut tab reads as a missing one).
        /// </summary>
        private void BringTabIntoView(ChatContainerTabItem item)
        {
            // After layout: a tab just added has no container yet.
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!(TabStrip.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement element))
                {
                    return;
                }

                Rect bounds = element.TransformToAncestor(TabStrip).TransformBounds(new Rect(element.RenderSize));
                double offset = TabScroller.HorizontalOffset;
                double target = GetOffsetToShowTab(GetTabEdges(), bounds.Left, bounds.Right, offset, TabScroller.ViewportWidth);
                if (Math.Abs(target - offset) > 0.5)
                {
                    TabScroller.ScrollToHorizontalOffset(target);
                }
            }));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        /// <summary>
        /// Strip offset that shows the tab spanning <paramref name="tabLeft"/>..<paramref name="tabRight"/>:
        /// unchanged when it is already in view; its own left edge when it is cut on the left; otherwise
        /// the first tab edge far enough right to show it whole. A tab wider than the room is shown
        /// from its own left edge.
        /// </summary>
        internal static double GetOffsetToShowTab(IEnumerable<double> tabEdges, double tabLeft, double tabRight,
                                                  double offset, double viewportWidth)
        {
            if (tabLeft < offset)
            {
                return tabLeft;
            }

            if (tabRight > offset + viewportWidth)
            {
                double needed = tabRight - viewportWidth;
                double edge = tabEdges.Where(x => x >= needed - 0.5).DefaultIfEmpty(tabLeft).Min();
                return Math.Min(edge, tabLeft);
            }

            return offset;
        }

        /// <summary>
        /// Strip offset one whole tab further: the next tab edge right (forward) or left of the
        /// current offset, clamped to 0..<paramref name="scrollableWidth"/>.
        /// </summary>
        internal static double GetOffsetOneTabFurther(IEnumerable<double> tabEdges, double offset,
                                                      double scrollableWidth, bool forward)
        {
            double target = forward
                ? tabEdges.Where(x => x > offset + 0.5).DefaultIfEmpty(scrollableWidth).Min()
                : tabEdges.Where(x => x < offset - 0.5).DefaultIfEmpty(0).Max();
            return Math.Max(0, Math.Min(target, scrollableWidth));
        }

        /// <summary>Left edge of every tab, in strip coordinates, left to right.</summary>
        private List<double> GetTabEdges()
        {
            var edges = new List<double>();
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement element)
                {
                    edges.Add(element.TransformToAncestor(TabStrip).Transform(new Point(0, 0)).X);
                }
            }
            edges.Sort();
            return edges;
        }

        /// <summary>Moves the strip one whole tab: its left edge lands on the previous/next tab's.</summary>
        private void ScrollTabsByOne(bool forward)
        {
            TabScroller.ScrollToHorizontalOffset(
                GetOffsetOneTabFurther(GetTabEdges(), TabScroller.HorizontalOffset, TabScroller.ScrollableWidth, forward));
        }

        private void TabScrollLeftButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollTabsByOne(forward: false);
        }

        private void TabScrollRightButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollTabsByOne(forward: true);
        }

        private void UpdateEmptyState()
        {
            EmptyCard.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatContainerTabItem item)
            {
                Select(item, focusComposer: true);
                _dragItem = item;
                _dragStart = e.GetPosition(TabStrip);
                _dragging = false;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Reorders by dragging a tab sideways. The tab moves past a neighbor once the pointer crosses
        /// that neighbor's middle, so tabs of different widths do not flip back and forth. The order
        /// lasts for this Visual Studio session only.
        /// </summary>
        private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point position = e.GetPosition(TabStrip);
            if (!_dragging)
            {
                if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
                {
                    return;
                }

                _dragging = TabStrip.CaptureMouse();
                if (!_dragging) return;
            }

            int from = _tabs.IndexOf(_dragItem);
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (i == from || !(TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement element))
                {
                    continue;
                }

                Rect bounds = element.TransformToAncestor(TabStrip).TransformBounds(new Rect(element.RenderSize));
                double middle = bounds.Left + bounds.Width / 2;
                if ((i > from && position.X > middle && position.X <= bounds.Right) ||
                    (i < from && position.X < middle && position.X >= bounds.Left))
                {
                    _tabs.Move(from, i);
                    break;
                }
            }
        }

        private void EndTabDrag()
        {
            bool wasDragging = _dragging;
            _dragItem = null;
            _dragging = false;
            if (wasDragging && TabStrip.IsMouseCaptured)
            {
                TabStrip.ReleaseMouseCapture();
            }
        }

        private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is ChatContainerTabItem item)
            {
                TabCloseRequested?.Invoke(this, item);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Right-click on a tab: Rename / Change color (the same requests the composer's ✎ and color
        /// buttons raise, so the owner resolves the chat the usual way), then Close / Close other tabs.
        /// </summary>
        private void Tab_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatContainerTabItem item))
            {
                return;
            }

            e.Handled = true;
            Select(item, focusComposer: false);
            ChatTranscriptView transcript = item.Host.Children.OfType<ChatTranscriptView>().FirstOrDefault();

            var menu = new ContextMenu
            {
                Style = TryFindResource("ComposerMenuStyle") as Style,
                PlacementTarget = sender as UIElement,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
            };
            Style itemStyle = TryFindResource("ComposerMenuItemStyle") as Style;

            MenuItem AddItem(string header, Action onClick, bool enabled = true)
            {
                var menuItem = new MenuItem { Header = header, Style = itemStyle, IsEnabled = enabled };
                menuItem.Click += (s, args) => onClick();
                menu.Items.Add(menuItem);
                return menuItem;
            }

            AddItem(Loc.L("Rename…"), () => transcript?.RequestRename(), transcript != null);
            AddItem(Loc.L("Change this session color"), () => transcript?.RequestColorPicker(), transcript != null);
            menu.Items.Add(new Separator { Style = TryFindResource("ComposerMenuSeparatorStyle") as Style });
            AddItem(Loc.L("Close"), () => TabCloseRequested?.Invoke(this, item));
            AddItem(Loc.L("Close other tabs"), () =>
            {
                foreach (ChatContainerTabItem other in _tabs.Where(t => !ReferenceEquals(t, item)).ToList())
                {
                    TabCloseRequested?.Invoke(this, other);
                }
            }, _tabs.Count > 1);

            menu.IsOpen = true;
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ChatContainerTabItem item)
            {
                TabCloseRequested?.Invoke(this, item);
            }
        }

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            NewChatRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ResumeTodayButton_Click(object sender, RoutedEventArgs e)
        {
            ResumeTodayRequested?.Invoke(this, EventArgs.Empty);
        }

        private void SessionHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            SessionHistoryRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The ▾ and the ◂ ▸ arrows show only while the strip is wider than its room: extent and
        /// viewport both change through ScrollChanged (tabs added/removed, captions changing, the
        /// window resized). An arrow is disabled once the strip is at that end.
        /// </summary>
        private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            bool overflowing = TabScroller.ExtentWidth > TabScroller.ViewportWidth + 0.5;
            Visibility visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
            TabListButton.Visibility = visibility;
            TabScrollLeftButton.Visibility = visibility;
            TabScrollRightButton.Visibility = visibility;
            TabScrollLeftButton.IsEnabled = TabScroller.HorizontalOffset > 0.5;
            TabScrollRightButton.IsEnabled = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;
        }

        /// <summary>Every chat, in strip order, the selected one checked; a click selects it.</summary>
        private void TabListButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                Style = TryFindResource("ComposerMenuStyle") as Style,
                PlacementTarget = TabListButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            Style itemStyle = TryFindResource("ComposerMenuItemStyle") as Style;

            foreach (ChatContainerTabItem item in _tabs)
            {
                var menuItem = new MenuItem
                {
                    Header = item.Caption,
                    Style = itemStyle,
                    IsCheckable = false,
                    IsChecked = item.IsSelected
                };
                ChatContainerTabItem target = item;
                menuItem.Click += (s, args) => Select(target, focusComposer: true);
                menu.Items.Add(menuItem);
            }

            menu.IsOpen = true;
        }

        /// <summary>The wheel steps the strip one whole tab per notch, like the ◂ ▸ arrows.</summary>
        private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0)
            {
                ScrollTabsByOne(forward: e.Delta < 0);
            }
            e.Handled = true;
        }

        /// <summary>Ctrl+PageUp / Ctrl+PageDown move between chats, like the editor's own tabs.</summary>
        private void Container_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || _tabs.Count < 2) return;
            if (e.Key != Key.PageUp && e.Key != Key.PageDown) return;

            int index = _tabs.IndexOf(SelectedTab);
            int step = e.Key == Key.PageDown ? 1 : -1;
            int next = ((index < 0 ? 0 : index) + step + _tabs.Count) % _tabs.Count;
            Select(_tabs[next], focusComposer: true);
            e.Handled = true;
        }
    }
}
