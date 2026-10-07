/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "/" command picker (issue #187) for the terminal-mode prompt box and the native chat composer.
 *          Typing "/" as the first character of a Claude prompt opens the same popup the "@" picker uses
 *          (see ClaudeCodeControl.AtMention.cs), listing built-in commands, custom commands and skills
 *          with their descriptions — what the CLI's own TUI shows, which the native chat does not have.
 *          Discovery and ranking live in Agents/SlashCommandCatalog.cs.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Slash Command Fields

        private const int SlashMaxResults = 1000;
        private static readonly TimeSpan SlashEntriesTtl = TimeSpan.FromSeconds(30);

        private List<SlashCommandEntry> _slashDiscovered;        // custom commands + skills read from disk
        private string _slashDiscoveredRoot;
        private DateTime _slashDiscoveredUtc;
        private bool _slashDiscovering;

        // Names the CLI announced at session start (built-ins, plugin commands, ...), across every session.
        private readonly HashSet<string> _slashAnnounced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Popup

        /// <summary>
        /// Shows the "/" picker when the text box starts with "/name" and the caret is inside that token.
        /// Returns false when this is not a slash command being typed, so the caller falls through to the
        /// "@" logic. Only Claude providers: the other CLIs' commands are not discovered here.
        /// </summary>
        private bool TryUpdateSlashPopup(AtMentionTarget target, string text, int caret)
        {
            if (!SlashCommandCatalog.TryGetQuery(text, caret, out string query)
                || !IsClaudeProvider(GetActiveOrSelectedProvider()))
            {
                target.SlashMode = false;
                return false;
            }

            if (_slashDiscovered == null
                || (DateTime.UtcNow - _slashDiscoveredUtc) > SlashEntriesTtl
                || !string.Equals(_slashDiscoveredRoot, _lastWorkspaceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _ = DiscoverThenRefilterAsync(target);
            }

            List<SlashCommandEntry> items = SlashCommandCatalog.Rank(
                SlashCommandCatalog.Merge(_slashDiscovered, _slashAnnounced), query, SlashMaxResults);
            if (items.Count == 0)
            {
                HideAtPopup(target);
                return true;
            }

            target.SlashMode = true;
            target.MentionStart = 0;
            EnsureAtPopup(target);
            target.ListBox.ItemsSource = items;
            target.ListBox.SelectedIndex = 0;
            if (!target.Popup.IsOpen)
            {
                PositionAtPopup(target);
                target.Popup.IsOpen = true;
            }

            return true;
        }

        /// <summary>True when the typed command is exactly the highlighted one, so Enter should send it
        /// rather than spend a keypress completing a name that is already complete.</summary>
        private static bool IsSlashSelectionComplete(AtMentionTarget target)
        {
            return target.ListBox?.SelectedItem is SlashCommandEntry entry
                && string.Equals(target.TextBox.Text, "/" + entry.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Replaces the typed "/query" with "/name " and closes the picker.</summary>
        private void CommitSlashSelection(AtMentionTarget target)
        {
            try
            {
                if (!(target.ListBox?.SelectedItem is SlashCommandEntry entry))
                {
                    HideAtPopup(target);
                    return;
                }

                var box = target.TextBox;
                int caret = Math.Min(box.CaretIndex, box.Text.Length);
                string insert = "/" + entry.Name + " ";

                target.SuppressTextChanged = true;
                box.Select(0, caret);
                box.SelectedText = insert;
                box.CaretIndex = insert.Length;
                target.SuppressTextChanged = false;

                box.Focus();
                HideAtPopup(target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CommitSlashSelection error: {ex.Message}");
                target.SuppressTextChanged = false;
                HideAtPopup(target);
            }
        }

        #endregion

        #region Discovery

        private async Task DiscoverThenRefilterAsync(AtMentionTarget target)
        {
            if (_slashDiscovering)
            {
                return;
            }

            _slashDiscovering = true;
            try
            {
                string workspace = _lastWorkspaceDirectory;
                string userDir = SlashCommandCatalog.GetUserClaudeDirectory();
                var found = await Task.Run(() => SlashCommandCatalog.Discover(workspace, userDir));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _slashDiscovered = found;
                _slashDiscoveredRoot = workspace;
                _slashDiscoveredUtc = DateTime.UtcNow;

                if (target.SlashMode && target.Popup != null && target.Popup.IsOpen)
                {
                    UpdateAtMentionPopup(target);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiscoverThenRefilterAsync error: {ex.Message}");
            }
            finally
            {
                _slashDiscovering = false;
            }
        }

        /// <summary>Remembers the commands a session announced so the picker offers them from then on.</summary>
        private void RememberAnnouncedSlashCommands(IReadOnlyList<string> names)
        {
            if (names == null)
            {
                return;
            }

            foreach (string raw in names)
            {
                string name = SlashCommandCatalog.NormalizeName(raw);
                if (name.Length > 0)
                {
                    _slashAnnounced.Add(name);
                }
            }
        }

        #endregion
    }
}
