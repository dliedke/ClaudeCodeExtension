/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "@" file/folder mention in the prompt box. Typing "@" (at the start of a word)
 *          opens a popup listing the workspace's files and folders; typing filters it,
 *          Up/Down + Enter/Tab (or a mouse click) inserts the workspace-relative path.
 *          Picking a folder keeps the popup open so the user can drill into it. Paths are
 *          inserted workspace-relative with forward slashes, which resolve for every agent
 *          (the terminal's working directory is the workspace), so no WSL conversion needed.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region At-Mention Fields

        private const int AtMentionMaxResults = 60;
        private const int AtMentionMaxEntries = 8000;
        private static readonly TimeSpan AtEntriesTtl = TimeSpan.FromSeconds(30);

        private static readonly HashSet<string> AtIgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".svn", ".hg", "node_modules", "packages", ".idea", "dist", "out", ".vscode"
        };

        private Popup _atPopup;
        private ListBox _atListBox;
        private List<string> _atEntries;       // workspace-relative paths ('/' separated, folders end with '/')
        private string _atEntriesRoot;
        private DateTime _atEntriesBuiltUtc;
        private bool _atEntriesBuilding;
        private int _atMentionStart = -1;      // index of the triggering '@' in the prompt text
        private bool _atSuppressTextChanged;   // guards programmatic edits from re-triggering

        #endregion

        #region Prompt TextChanged + Key Handling

        /// <summary>
        /// Prompt-box TextChanged handler (wired in XAML). Re-evaluates whether an "@" mention is
        /// being typed under the caret and shows/filters/hides the picker accordingly.
        /// </summary>
        private void PromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_atSuppressTextChanged) return;
            UpdateAtMentionPopup();
        }

        /// <summary>
        /// Intercepts navigation/commit keys while the picker is open. Returns true (and marks the
        /// event handled) when the key was consumed, so the caller can return before its own
        /// Enter-sends-prompt / history-navigation logic runs.
        /// </summary>
        private bool HandleAtMentionKey(KeyEventArgs e)
        {
            if (_atPopup == null || !_atPopup.IsOpen) return false;

            switch (e.Key)
            {
                case Key.Down:
                    MoveAtSelection(1);
                    e.Handled = true;
                    return true;
                case Key.Up:
                    MoveAtSelection(-1);
                    e.Handled = true;
                    return true;
                case Key.Enter:
                case Key.Tab:
                    // Swallow the key regardless; only insert when entries are ready.
                    if (_atEntries != null && _atListBox?.SelectedItem is string)
                        CommitAtSelection();
                    e.Handled = true;
                    return true;
                case Key.Escape:
                    HideAtPopup();
                    e.Handled = true;
                    return true;
            }
            return false;
        }

        #endregion

        #region Popup Update / Filter

        /// <summary>
        /// Detects an "@" mention token immediately before the caret (an "@" at the start of the
        /// text or after whitespace, followed by non-whitespace up to the caret) and shows the
        /// filtered picker, or hides it when there is no such token.
        /// </summary>
        private void UpdateAtMentionPopup()
        {
            try
            {
                if (PromptTextBox == null) { HideAtPopup(); return; }

                string text = PromptTextBox.Text ?? string.Empty;
                int caret = PromptTextBox.CaretIndex;
                if (caret < 0 || caret > text.Length) { HideAtPopup(); return; }

                int at = -1;
                for (int i = caret - 1; i >= 0; i--)
                {
                    char c = text[i];
                    if (c == '@')
                    {
                        if (i == 0 || char.IsWhiteSpace(text[i - 1])) at = i;
                        break;
                    }
                    if (char.IsWhiteSpace(c)) break;
                }
                if (at < 0) { HideAtPopup(); return; }

                _atMentionStart = at;
                string query = text.Substring(at + 1, caret - at - 1);

                if (_atEntries == null)
                {
                    ShowAtIndexing();
                    _ = EnsureThenRefilterAsync();
                    return;
                }

                // Refresh a stale index in the background, but show current results immediately.
                if ((DateTime.UtcNow - _atEntriesBuiltUtc) > AtEntriesTtl && !_atEntriesBuilding)
                    _ = EnsureThenRefilterAsync();

                FilterAndShowAtPopup(query);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateAtMentionPopup error: {ex.Message}");
            }
        }

        private void FilterAndShowAtPopup(string query)
        {
            EnsureAtPopup();
            var items = RankAtEntries(query);
            _atListBox.ItemsSource = items;
            if (items.Count == 0) { HideAtPopup(); return; }

            _atListBox.SelectedIndex = 0;
            if (!_atPopup.IsOpen)
            {
                PositionAtPopup();
                _atPopup.IsOpen = true;
            }
        }

        private void ShowAtIndexing()
        {
            EnsureAtPopup();
            _atListBox.ItemsSource = new List<string> { "Indexing workspace…" };
            _atListBox.SelectedIndex = -1;
            if (!_atPopup.IsOpen)
            {
                PositionAtPopup();
                _atPopup.IsOpen = true;
            }
        }

        private void MoveAtSelection(int delta)
        {
            if (_atListBox == null || _atListBox.Items.Count == 0) return;
            int n = _atListBox.Items.Count;
            int i = _atListBox.SelectedIndex + delta;
            if (i < 0) i = 0;
            if (i >= n) i = n - 1;
            _atListBox.SelectedIndex = i;
            if (_atListBox.SelectedItem != null) _atListBox.ScrollIntoView(_atListBox.SelectedItem);
        }

        /// <summary>
        /// Replaces the typed "@query" with "@&lt;relative-path&gt;". A file gets a trailing space and
        /// closes the popup; a folder is left without a space and re-opens the picker so the user
        /// can keep drilling into it.
        /// </summary>
        private void CommitAtSelection()
        {
            try
            {
                if (_atEntries == null) return;
                string sel = _atListBox?.SelectedItem as string;
                if (string.IsNullOrEmpty(sel)) { HideAtPopup(); return; }

                int caret = PromptTextBox.CaretIndex;
                if (_atMentionStart < 0 || _atMentionStart > PromptTextBox.Text.Length || caret < _atMentionStart)
                {
                    HideAtPopup();
                    return;
                }

                bool isDir = sel.EndsWith("/", StringComparison.Ordinal);
                string insert = "@" + sel + (isDir ? string.Empty : " ");

                _atSuppressTextChanged = true;
                PromptTextBox.Select(_atMentionStart, caret - _atMentionStart);
                PromptTextBox.SelectedText = insert;
                PromptTextBox.CaretIndex = _atMentionStart + insert.Length;
                _atSuppressTextChanged = false;

                PromptTextBox.Focus();

                if (isDir) UpdateAtMentionPopup();
                else HideAtPopup();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CommitAtSelection error: {ex.Message}");
                _atSuppressTextChanged = false;
                HideAtPopup();
            }
        }

        private void HideAtPopup()
        {
            _atMentionStart = -1;
            if (_atPopup != null) _atPopup.IsOpen = false;
        }

        /// <summary>
        /// Ranks entries for the query. A query may contain "/" (folder drill-down): the part after
        /// the last slash matches the entry name, the prefix constrains to that subtree. Name
        /// prefix-matches rank above name/path substring matches.
        /// </summary>
        private List<string> RankAtEntries(string query)
        {
            var all = _atEntries ?? new List<string>();
            string q = (query ?? string.Empty).Replace('\\', '/');

            int ls = q.LastIndexOf('/');
            string prefix = ls >= 0 ? q.Substring(0, ls + 1) : string.Empty;
            string namePart = ls >= 0 ? q.Substring(ls + 1) : q;

            var startsWith = new List<string>();
            var contains = new List<string>();

            foreach (var p in all)
            {
                if (prefix.Length > 0 && !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                if (namePart.Length == 0)
                {
                    startsWith.Add(p);
                }
                else
                {
                    string nm = NameOf(p);
                    if (nm.StartsWith(namePart, StringComparison.OrdinalIgnoreCase)) startsWith.Add(p);
                    else if (nm.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) contains.Add(p);
                    else if (p.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) contains.Add(p);
                }

                if (startsWith.Count >= AtMentionMaxResults) break;
            }

            return startsWith.Concat(contains).Take(AtMentionMaxResults).ToList();
        }

        private static string NameOf(string relPath)
        {
            string t = relPath.TrimEnd('/');
            int s = t.LastIndexOf('/');
            return s >= 0 ? t.Substring(s + 1) : t;
        }

        #endregion

        #region Workspace Indexing

        private async Task EnsureThenRefilterAsync()
        {
            try
            {
                await EnsureAtEntriesAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_atMentionStart >= 0) UpdateAtMentionPopup();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnsureThenRefilterAsync error: {ex.Message}");
            }
        }

        private async Task EnsureAtEntriesAsync()
        {
            string workspace = await GetWorkspaceDirectoryAsync();
            bool fresh = _atEntries != null
                && string.Equals(_atEntriesRoot, workspace, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _atEntriesBuiltUtc) < AtEntriesTtl;
            if (fresh || _atEntriesBuilding) return;

            _atEntriesBuilding = true;
            try
            {
                var list = await Task.Run(() => EnumerateWorkspaceEntries(workspace));
                _atEntries = list;
                _atEntriesRoot = workspace;
                _atEntriesBuiltUtc = DateTime.UtcNow;
            }
            finally
            {
                _atEntriesBuilding = false;
            }
        }

        /// <summary>
        /// Walks the workspace (skipping build/VCS/package folders and symlink reparse points) and
        /// returns workspace-relative paths with '/' separators; folders carry a trailing '/'.
        /// Capped at <see cref="AtMentionMaxEntries"/> so a huge tree can't stall the picker.
        /// </summary>
        private static List<string> EnumerateWorkspaceEntries(string root)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return results;

            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                var stack = new Stack<string>();
                stack.Push(rootFull);

                while (stack.Count > 0 && results.Count < AtMentionMaxEntries)
                {
                    string dir = stack.Pop();

                    string[] subdirs;
                    try { subdirs = Directory.GetDirectories(dir); }
                    catch { subdirs = Array.Empty<string>(); }

                    foreach (string d in subdirs)
                    {
                        if (results.Count >= AtMentionMaxEntries) break;
                        string name = Path.GetFileName(d);
                        if (AtIgnoredDirs.Contains(name)) continue;
                        try
                        {
                            var attr = File.GetAttributes(d);
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { continue; }

                        results.Add(ToRelative(rootFull, d) + "/");
                        stack.Push(d);
                    }

                    if (results.Count >= AtMentionMaxEntries) break;

                    string[] files;
                    try { files = Directory.GetFiles(dir); }
                    catch { files = Array.Empty<string>(); }

                    foreach (string f in files)
                    {
                        if (results.Count >= AtMentionMaxEntries) break;
                        results.Add(ToRelative(rootFull, f));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnumerateWorkspaceEntries error: {ex.Message}");
            }

            return results;
        }

        private static string ToRelative(string root, string full)
        {
            string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/')
                : full;
            return rel.Replace('\\', '/');
        }

        #endregion

        #region Popup Construction

        private void EnsureAtPopup()
        {
            if (_atPopup != null) return;

            GetThemeBrushes(out Brush bg, out Brush fg);
            Brush hover = ComputeAtHoverBrush(bg);

            _atListBox = new ListBox
            {
                Background = bg,
                Foreground = fg,
                BorderThickness = new Thickness(0),
                MaxHeight = 240,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_atListBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(_atListBox, false);

            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, fg));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            itemStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(".")));
            var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hover));
            selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, fg));
            itemStyle.Triggers.Add(selTrigger);
            var overTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            overTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hover));
            itemStyle.Triggers.Add(overTrigger);
            _atListBox.ItemContainerStyle = itemStyle;

            _atListBox.PreviewMouseLeftButtonUp += (s, e) =>
            {
                // The scrollbar added for horizontal scrolling lives inside the ListBox's visual
                // tree too, so a plain "click landed inside the ListBox" check would also fire when
                // dragging/clicking the scrollbar. Only commit when the click actually hit a row.
                if (_atEntries != null && _atListBox.SelectedItem is string
                    && FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null)
                {
                    CommitAtSelection();
                    e.Handled = true;
                }
            };

            var border = new Border
            {
                Child = _atListBox,
                Background = bg,
                BorderBrush = fg,
                BorderThickness = new Thickness(1)
            };

            _atPopup = new Popup
            {
                Child = border,
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Placement = PlacementMode.RelativePoint,
                PlacementTarget = PromptTextBox,
                MinWidth = 360,
                MaxWidth = 600
            };

            // Close when the prompt box truly loses keyboard focus, but not when focus moves into
            // the popup itself (a mouse click on a row), so the click can commit first.
            PromptTextBox.LostKeyboardFocus += (s, e) =>
            {
                if (IsInsideAtPopup(e.NewFocus as DependencyObject)) return;
                HideAtPopup();
            };
        }

        private void PositionAtPopup()
        {
            try
            {
                int idx = Math.Max(0, Math.Min(_atMentionStart, PromptTextBox.Text.Length));
                Rect r = PromptTextBox.GetRectFromCharacterIndex(idx);
                if (!r.IsEmpty)
                {
                    _atPopup.HorizontalOffset = r.X;
                    _atPopup.VerticalOffset = r.Bottom;
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PositionAtPopup error: {ex.Message}");
            }

            _atPopup.HorizontalOffset = 0;
            _atPopup.VerticalOffset = PromptTextBox.ActualHeight;
        }

        private static T FindVisualAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            try
            {
                while (node != null)
                {
                    if (node is T match) return match;
                    node = (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                        ? VisualTreeHelper.GetParent(node)
                        : null;
                }
            }
            catch { }
            return null;
        }

        private bool IsInsideAtPopup(DependencyObject node)
        {
            try
            {
                while (node != null)
                {
                    if (ReferenceEquals(node, _atListBox)) return true;
                    DependencyObject parent = null;
                    if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                        parent = VisualTreeHelper.GetParent(node);
                    parent = parent ?? LogicalTreeHelper.GetParent(node);
                    node = parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// A selection/hover background derived from the theme background (lightened for dark
        /// themes, darkened for light ones) so the row stays readable instead of the system blue.
        /// </summary>
        private static Brush ComputeAtHoverBrush(Brush themeBg)
        {
            Color baseColor = (themeBg as SolidColorBrush)?.Color ?? Colors.Gray;
            bool isDark = (baseColor.R + baseColor.G + baseColor.B) < 384;
            const int shift = 36;
            Color hover = isDark
                ? Color.FromRgb(
                    (byte)Math.Min(255, baseColor.R + shift),
                    (byte)Math.Min(255, baseColor.G + shift),
                    (byte)Math.Min(255, baseColor.B + shift))
                : Color.FromRgb(
                    (byte)Math.Max(0, baseColor.R - shift),
                    (byte)Math.Max(0, baseColor.G - shift),
                    (byte)Math.Max(0, baseColor.B - shift));
            return new SolidColorBrush(hover);
        }

        #endregion
    }
}
