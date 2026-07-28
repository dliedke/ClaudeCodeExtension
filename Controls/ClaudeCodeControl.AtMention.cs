/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "@" file/folder mention, wired to both the terminal-mode prompt box and the native
 *          chat composer. Typing "@" (at the start of a word) opens a popup listing the
 *          workspace's files and folders; typing filters it, Up/Down + Enter/Tab (or a mouse
 *          click) inserts the workspace-relative path. Picking a folder keeps the popup open so
 *          the user can drill into it. Paths are inserted workspace-relative with forward
 *          slashes, which resolve for every agent (the terminal's working directory is the
 *          workspace), so no WSL conversion needed. The two text boxes each get their own popup
 *          (an <see cref="AtMentionTarget"/>) but share one workspace index.
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

        /// <summary>Per-text-box popup state. The panel's prompt box and the native chat composer
        /// each own one, so they never fight over which box the popup is anchored to.</summary>
        private sealed class AtMentionTarget
        {
            public readonly TextBox TextBox;
            public Popup Popup;
            public ListBox ListBox;
            public int MentionStart = -1;    // index of the triggering '@' in the text box's text
            public bool SuppressTextChanged; // guards programmatic edits from re-triggering

            public AtMentionTarget(TextBox textBox)
            {
                TextBox = textBox;
            }
        }

        private AtMentionTarget _atPanelTarget;
        private AtMentionTarget _atComposerTarget;

        // Shared across both targets: keyed by workspace, not by text box.
        private List<string> _atEntries;       // workspace-relative paths ('/' separated, folders end with '/')
        private string _atEntriesRoot;
        private DateTime _atEntriesBuiltUtc;
        private bool _atEntriesBuilding;

        private AtMentionTarget GetAtMentionPanelTarget()
        {
            return _atPanelTarget ?? (_atPanelTarget = new AtMentionTarget(PromptTextBox));
        }

        private AtMentionTarget GetAtMentionComposerTarget()
        {
            return _atComposerTarget ?? (_atComposerTarget = new AtMentionTarget(ChatTranscript.ComposerInputBox));
        }

        #endregion

        #region TextChanged + Key Handling

        /// <summary>
        /// Prompt-box TextChanged handler (wired in XAML). Re-evaluates whether an "@" mention is
        /// being typed under the caret and shows/filters/hides the picker accordingly.
        /// </summary>
        private void PromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AtMentionTarget target = GetAtMentionPanelTarget();
            if (target.SuppressTextChanged) return;
            UpdateAtMentionPopup(target);
        }

        /// <summary>Native chat composer's equivalent of <see cref="PromptTextBox_TextChanged"/>, wired
        /// from <c>WireChatComposer</c> onto <c>ChatTranscript.ComposerInputBox</c>.</summary>
        private void ComposerInput_AtMentionTextChanged(object sender, TextChangedEventArgs e)
        {
            AtMentionTarget target = GetAtMentionComposerTarget();
            if (target.SuppressTextChanged) return;
            UpdateAtMentionPopup(target);
        }

        /// <summary>Native chat composer's equivalent of <see cref="HandleAtMentionKey"/>, wired to
        /// <c>ChatTranscriptView.ComposerPreviewKeyDown</c> so it runs before that view's own
        /// Enter-sends / history-navigation handling.</summary>
        private void ComposerInput_AtMentionPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleAtMentionKey(GetAtMentionComposerTarget(), e);
        }

        /// <summary>
        /// Intercepts navigation/commit keys while the picker is open. Returns true (and marks the
        /// event handled) when the key was consumed, so the caller can return before its own
        /// Enter-sends-prompt / history-navigation logic runs.
        /// </summary>
        private bool HandleAtMentionKey(AtMentionTarget target, KeyEventArgs e)
        {
            if (target.Popup == null || !target.Popup.IsOpen) return false;

            switch (e.Key)
            {
                case Key.Down:
                    MoveAtSelection(target, 1);
                    e.Handled = true;
                    return true;
                case Key.Up:
                    MoveAtSelection(target, -1);
                    e.Handled = true;
                    return true;
                case Key.Enter:
                case Key.Tab:
                    // Swallow the key regardless; only insert when entries are ready.
                    if (_atEntries != null && target.ListBox?.SelectedItem is string)
                        CommitAtSelection(target);
                    e.Handled = true;
                    return true;
                case Key.Escape:
                    HideAtPopup(target);
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
        private void UpdateAtMentionPopup(AtMentionTarget target)
        {
            try
            {
                TextBox box = target.TextBox;
                if (box == null) { HideAtPopup(target); return; }

                string text = box.Text ?? string.Empty;
                int caret = box.CaretIndex;
                if (caret < 0 || caret > text.Length) { HideAtPopup(target); return; }

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
                if (at < 0) { HideAtPopup(target); return; }

                target.MentionStart = at;
                string query = text.Substring(at + 1, caret - at - 1);

                if (_atEntries == null)
                {
                    ShowAtIndexing(target);
                    _ = EnsureThenRefilterAsync(target);
                    return;
                }

                // Refresh a stale index in the background, but show current results immediately.
                if ((DateTime.UtcNow - _atEntriesBuiltUtc) > AtEntriesTtl && !_atEntriesBuilding)
                    _ = EnsureThenRefilterAsync(target);

                FilterAndShowAtPopup(target, query);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateAtMentionPopup error: {ex.Message}");
            }
        }

        private void FilterAndShowAtPopup(AtMentionTarget target, string query)
        {
            EnsureAtPopup(target);
            var items = RankAtEntries(query);
            target.ListBox.ItemsSource = items;
            if (items.Count == 0) { HideAtPopup(target); return; }

            target.ListBox.SelectedIndex = 0;
            if (!target.Popup.IsOpen)
            {
                PositionAtPopup(target);
                target.Popup.IsOpen = true;
            }
        }

        private void ShowAtIndexing(AtMentionTarget target)
        {
            EnsureAtPopup(target);
            target.ListBox.ItemsSource = new List<string> { "Indexing workspace…" };
            target.ListBox.SelectedIndex = -1;
            if (!target.Popup.IsOpen)
            {
                PositionAtPopup(target);
                target.Popup.IsOpen = true;
            }
        }

        private void MoveAtSelection(AtMentionTarget target, int delta)
        {
            if (target.ListBox == null || target.ListBox.Items.Count == 0) return;
            int n = target.ListBox.Items.Count;
            int i = target.ListBox.SelectedIndex + delta;
            if (i < 0) i = 0;
            if (i >= n) i = n - 1;
            target.ListBox.SelectedIndex = i;
            if (target.ListBox.SelectedItem != null) target.ListBox.ScrollIntoView(target.ListBox.SelectedItem);
        }

        /// <summary>
        /// Replaces the typed "@query" with "@&lt;relative-path&gt;". A file gets a trailing space and
        /// closes the popup; a folder is left without a space and re-opens the picker so the user
        /// can keep drilling into it.
        /// </summary>
        private void CommitAtSelection(AtMentionTarget target)
        {
            try
            {
                if (_atEntries == null) return;
                string sel = target.ListBox?.SelectedItem as string;
                if (string.IsNullOrEmpty(sel)) { HideAtPopup(target); return; }

                TextBox box = target.TextBox;
                int caret = box.CaretIndex;
                if (target.MentionStart < 0 || target.MentionStart > box.Text.Length || caret < target.MentionStart)
                {
                    HideAtPopup(target);
                    return;
                }

                bool isDir = sel.EndsWith("/", StringComparison.Ordinal);
                string insert = "@" + sel + (isDir ? string.Empty : " ");

                target.SuppressTextChanged = true;
                box.Select(target.MentionStart, caret - target.MentionStart);
                box.SelectedText = insert;
                box.CaretIndex = target.MentionStart + insert.Length;
                target.SuppressTextChanged = false;

                box.Focus();

                if (isDir) UpdateAtMentionPopup(target);
                else HideAtPopup(target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CommitAtSelection error: {ex.Message}");
                target.SuppressTextChanged = false;
                HideAtPopup(target);
            }
        }

        private void HideAtPopup(AtMentionTarget target)
        {
            target.MentionStart = -1;
            if (target.Popup != null) target.Popup.IsOpen = false;
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

        private async Task EnsureThenRefilterAsync(AtMentionTarget target)
        {
            try
            {
                await EnsureAtEntriesAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (target.MentionStart >= 0) UpdateAtMentionPopup(target);
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

        private void EnsureAtPopup(AtMentionTarget target)
        {
            if (target.Popup != null) return;

            GetThemeBrushes(out Brush bg, out Brush fg);
            Brush hover = ComputeAtHoverBrush(bg);

            var listBox = new ListBox
            {
                Background = bg,
                Foreground = fg,
                BorderThickness = new Thickness(0),
                MaxHeight = 240,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(listBox, false);

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
            listBox.ItemContainerStyle = itemStyle;

            listBox.PreviewMouseLeftButtonUp += (s, e) =>
            {
                // The scrollbar added for horizontal scrolling lives inside the ListBox's visual
                // tree too, so a plain "click landed inside the ListBox" check would also fire when
                // dragging/clicking the scrollbar. Only commit when the click actually hit a row.
                if (_atEntries != null && listBox.SelectedItem is string
                    && FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null)
                {
                    CommitAtSelection(target);
                    e.Handled = true;
                }
            };

            var border = new Border
            {
                Child = listBox,
                Background = bg,
                BorderBrush = fg,
                BorderThickness = new Thickness(1)
            };

            var popup = new Popup
            {
                Child = border,
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Placement = PlacementMode.RelativePoint,
                PlacementTarget = target.TextBox,
                MinWidth = 360,
                MaxWidth = 600
            };

            // Close when the text box truly loses keyboard focus, but not when focus moves into
            // the popup itself (a mouse click on a row), so the click can commit first.
            target.TextBox.LostKeyboardFocus += (s, e) =>
            {
                if (IsInsideAtPopup(target, e.NewFocus as DependencyObject)) return;
                HideAtPopup(target);
            };

            target.ListBox = listBox;
            target.Popup = popup;
        }

        private void PositionAtPopup(AtMentionTarget target)
        {
            try
            {
                TextBox box = target.TextBox;
                int idx = Math.Max(0, Math.Min(target.MentionStart, box.Text.Length));
                Rect r = box.GetRectFromCharacterIndex(idx);
                if (!r.IsEmpty)
                {
                    target.Popup.HorizontalOffset = r.X;
                    target.Popup.VerticalOffset = r.Bottom;
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PositionAtPopup error: {ex.Message}");
            }

            target.Popup.HorizontalOffset = 0;
            target.Popup.VerticalOffset = target.TextBox.ActualHeight;
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

        private bool IsInsideAtPopup(AtMentionTarget target, DependencyObject node)
        {
            try
            {
                while (node != null)
                {
                    if (ReferenceEquals(node, target.ListBox)) return true;
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
