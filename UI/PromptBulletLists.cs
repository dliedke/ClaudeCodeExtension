/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: "Bullet lists in the prompt box" (issue #269, opt-in): typing "- " or "* " at the start of a
 *          line turns it into a "• " bullet, as Claude.ai and Claude Desktop do. A new line after a
 *          bullet item continues the list, a new line on an empty item ends it, Backspace right after
 *          the conversion brings the typed "- " back and Backspace after a bullet removes the bullet.
 *          The box text is what the agent receives (WYSIWYG). The rules are pure, for the tests; the
 *          attach part wires them to a plain WPF TextBox (panel prompt box and every chat composer).
 * ***********************/

using System;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeCodeVS.UI
{
    internal static class PromptBulletLists
    {
        public const char Bullet = '•';

        /// <summary>The marker a converted line starts with (after its indentation).</summary>
        public const string Marker = "• ";

        /// <summary>Reads the setting live, so a change in Settings needs no re-attach. Off when unset.</summary>
        public static Func<bool> IsEnabled { get; set; }

        private static readonly ConditionalWeakTable<TextBox, State> Attached = new ConditionalWeakTable<TextBox, State>();

        private sealed class State
        {
            public bool Editing;

            // Text and caret right after the last "- " → "• " conversion; Backspace in exactly that
            // state undoes it (the typed "- " comes back), like the input rules of Claude.ai's editor.
            public string ConvertedText;
            public int ConvertedCaret = -1;
            public char ConvertedFrom;
        }

        #region Pure rules

        /// <summary>
        /// True when a space typed at <paramref name="caret"/> should turn the line into a bullet: the
        /// line so far is only indentation and a "-" or "*". <paramref name="markerStart"/> is that
        /// character's index.
        /// </summary>
        internal static bool TryGetBulletTrigger(string text, int caret, out int markerStart)
        {
            markerStart = -1;
            if (text == null || caret <= 0 || caret > text.Length) return false;

            char c = text[caret - 1];
            if (c != '-' && c != '*') return false;

            int lineStart = LineStart(text, caret - 1);
            if (!IsIndentation(text, lineStart, caret - 1)) return false;

            markerStart = caret - 1;
            return true;
        }

        /// <summary>
        /// True when the caret sits right after a bullet marker at the start of its line (indentation,
        /// "•", space). <paramref name="markerStart"/> is the index of the "•".
        /// </summary>
        internal static bool TryGetMarkerBeforeCaret(string text, int caret, out int markerStart)
        {
            markerStart = -1;
            if (text == null || caret < Marker.Length || caret > text.Length) return false;
            if (string.CompareOrdinal(text, caret - Marker.Length, Marker, 0, Marker.Length) != 0) return false;

            int bullet = caret - Marker.Length;
            if (!IsIndentation(text, LineStart(text, bullet), bullet)) return false;

            markerStart = bullet;
            return true;
        }

        internal enum NewlineAction { None, Continue, EndList }

        /// <summary>
        /// What a line break inserted at <paramref name="newlineStart"/> does to the line it ends:
        /// continue a bullet item (<paramref name="prefix"/> is the indentation plus marker to insert
        /// after the break) or, on an item with no text yet, end the list (<paramref name="lineStart"/>
        /// is where that empty item begins).
        /// </summary>
        internal static NewlineAction GetNewlineAction(string text, int newlineStart, out string prefix, out int lineStart)
        {
            prefix = null;
            lineStart = -1;
            if (text == null || newlineStart < 0 || newlineStart > text.Length) return NewlineAction.None;

            int start = LineStart(text, newlineStart);
            int indentEnd = start;
            while (indentEnd < newlineStart && (text[indentEnd] == ' ' || text[indentEnd] == '\t')) indentEnd++;

            if (newlineStart - indentEnd < Marker.Length
                || string.CompareOrdinal(text, indentEnd, Marker, 0, Marker.Length) != 0)
            {
                return NewlineAction.None;
            }

            lineStart = start;
            string itemText = text.Substring(indentEnd + Marker.Length, newlineStart - indentEnd - Marker.Length);
            if (itemText.Trim().Length == 0) return NewlineAction.EndList;

            prefix = text.Substring(start, indentEnd - start) + Marker;
            return NewlineAction.Continue;
        }

        /// <summary>Index of the first character of the line holding <paramref name="index"/>.</summary>
        private static int LineStart(string text, int index)
        {
            int i = Math.Min(index, text.Length);
            while (i > 0 && text[i - 1] != '\n' && text[i - 1] != '\r') i--;
            return i;
        }

        private static bool IsIndentation(string text, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (text[i] != ' ' && text[i] != '\t') return false;
            }
            return true;
        }

        #endregion

        #region TextBox wiring

        /// <summary>Wires the bullet rules to a prompt box. Attaching the same box twice is a no-op.</summary>
        public static void Attach(TextBox box)
        {
            if (box == null) return;

            State state;
            if (Attached.TryGetValue(box, out state)) return;

            state = new State();
            Attached.Add(box, state);

            box.PreviewTextInput += (s, e) => OnPreviewTextInput(box, state, e);
            box.PreviewKeyDown += (s, e) => OnPreviewKeyDown(box, state, e);
            box.TextChanged += (s, e) => OnTextChanged(box, state, e);
        }

        private static bool Enabled()
        {
            try
            {
                return IsEnabled != null && IsEnabled();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void OnPreviewTextInput(TextBox box, State state, TextCompositionEventArgs e)
        {
            if (e.Handled || e.Text != " " || box.SelectionLength != 0 || box.IsReadOnly || !Enabled()) return;

            string text = box.Text;
            int caret = box.CaretIndex;
            if (!TryGetBulletTrigger(text, caret, out int markerStart)) return;

            char from = text[markerStart];
            Replace(box, state, markerStart, caret - markerStart, Marker);

            state.ConvertedText = box.Text;
            state.ConvertedCaret = box.CaretIndex;
            state.ConvertedFrom = from;
            e.Handled = true;
        }

        private static void OnPreviewKeyDown(TextBox box, State state, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Back || Keyboard.Modifiers != ModifierKeys.None) return;
            if (box.SelectionLength != 0 || box.IsReadOnly || !Enabled()) return;

            string text = box.Text;
            int caret = box.CaretIndex;
            if (!TryGetMarkerBeforeCaret(text, caret, out int markerStart)) return;

            bool justConverted = caret == state.ConvertedCaret && string.Equals(text, state.ConvertedText, StringComparison.Ordinal);
            string replacement = justConverted ? state.ConvertedFrom + " " : string.Empty;

            Replace(box, state, markerStart, Marker.Length, replacement);
            e.Handled = true;
        }

        private static void OnTextChanged(TextBox box, State state, TextChangedEventArgs e)
        {
            if (state.Editing) return;

            // Any other edit ends the "just converted" window of the Backspace undo.
            state.ConvertedText = null;
            state.ConvertedCaret = -1;

            if (e.Changes.Count != 1 || box.IsReadOnly || !Enabled()) return;

            TextChange change = null;
            foreach (TextChange c in e.Changes) change = c;
            if (change == null || change.RemovedLength != 0 || change.AddedLength < 1 || change.AddedLength > 2) return;

            string text = box.Text;
            if (change.Offset + change.AddedLength > text.Length) return;

            string added = text.Substring(change.Offset, change.AddedLength);
            if (added != "\n" && added != "\r\n" && added != "\r") return;

            // Typed: the caret is after the break. Inserted through SelectedText (the panel's
            // Shift+Enter): the break itself is still selected. Anything else is not a typed newline.
            int caretAfter = change.Offset + change.AddedLength;
            if (box.SelectionStart + box.SelectionLength != caretAfter) return;

            switch (GetNewlineAction(text, change.Offset, out string prefix, out int lineStart))
            {
                case NewlineAction.Continue:
                    Replace(box, state, caretAfter, 0, prefix);
                    break;

                case NewlineAction.EndList:
                    // The empty item becomes a plain empty line and the caret stays on it.
                    Replace(box, state, lineStart, caretAfter - lineStart, string.Empty);
                    break;
            }
        }

        /// <summary>Replaces a range through the selection, so the edit is one Ctrl+Z step.</summary>
        private static void Replace(TextBox box, State state, int start, int length, string replacement)
        {
            state.Editing = true;
            try
            {
                box.Select(start, length);
                box.SelectedText = replacement;
                box.Select(start + replacement.Length, 0);
            }
            finally
            {
                state.Editing = false;
            }
        }

        #endregion
    }
}
