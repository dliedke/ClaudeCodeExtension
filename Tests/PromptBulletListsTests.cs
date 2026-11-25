/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Bullet lists in the prompt box" (issue #269): "- " at the start of a line becomes "• ", a new line
 *          continues the list, a new line on an empty item ends it, Backspace undoes the conversion.
 *
 * *******************************************************************************************************************/

using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class PromptBulletListsTests
    {
        [TestMethod]
        public void DashOrStarAtLineStartTriggers()
        {
            Assert.IsTrue(PromptBulletLists.TryGetBulletTrigger("-", 1, out int start));
            Assert.AreEqual(0, start);

            Assert.IsTrue(PromptBulletLists.TryGetBulletTrigger("first\n  *", 9, out start));
            Assert.AreEqual(8, start);

            Assert.IsTrue(PromptBulletLists.TryGetBulletTrigger("a\r\n\t-", 5, out start));
            Assert.AreEqual(4, start);
        }

        [TestMethod]
        public void DashInsideTextOrFlagsDoesNotTrigger()
        {
            Assert.IsFalse(PromptBulletLists.TryGetBulletTrigger("run git push -", 14, out _), "dash after text");
            Assert.IsFalse(PromptBulletLists.TryGetBulletTrigger("--", 2, out _), "double dash (flag)");
            Assert.IsFalse(PromptBulletLists.TryGetBulletTrigger("a -> b", 3, out _), "arrow");
            Assert.IsFalse(PromptBulletLists.TryGetBulletTrigger("", 0, out _));
            Assert.IsFalse(PromptBulletLists.TryGetBulletTrigger("x", 1, out _));
        }

        [TestMethod]
        public void NewlineAfterItemContinuesWithSameIndent()
        {
            string text = "intro\n  • first\n";
            var action = PromptBulletLists.GetNewlineAction(text, text.Length - 1, out string prefix, out _);

            Assert.AreEqual(PromptBulletLists.NewlineAction.Continue, action);
            Assert.AreEqual("  • ", prefix);
        }

        [TestMethod]
        public void NewlineOnEmptyItemEndsTheList()
        {
            string text = "• first\n• \r\n";
            var action = PromptBulletLists.GetNewlineAction(text, text.Length - 2, out _, out int lineStart);

            Assert.AreEqual(PromptBulletLists.NewlineAction.EndList, action);
            Assert.AreEqual(8, lineStart);
        }

        [TestMethod]
        public void NewlineOnPlainOrDashLineDoesNothing()
        {
            Assert.AreEqual(PromptBulletLists.NewlineAction.None,
                PromptBulletLists.GetNewlineAction("plain\n", 5, out _, out _));
            Assert.AreEqual(PromptBulletLists.NewlineAction.None,
                PromptBulletLists.GetNewlineAction("- pasted markdown\n", 17, out _, out _));
            Assert.AreEqual(PromptBulletLists.NewlineAction.None,
                PromptBulletLists.GetNewlineAction("a • b\n", 5, out _, out _), "bullet not at line start");
        }

        [TestMethod]
        public void MarkerBeforeCaretOnlyAtLineStart()
        {
            Assert.IsTrue(PromptBulletLists.TryGetMarkerBeforeCaret("x\n  • ", 6, out int start));
            Assert.AreEqual(4, start);
            Assert.IsFalse(PromptBulletLists.TryGetMarkerBeforeCaret("a • ", 4, out _));
            Assert.IsFalse(PromptBulletLists.TryGetMarkerBeforeCaret("• item", 7, out _));
        }

        [TestMethod]
        public void TextBoxConvertsContinuesAndEndsTheList()
        {
            RunOnSta(() =>
            {
                Func<bool> previous = PromptBulletLists.IsEnabled;
                try
                {
                    PromptBulletLists.IsEnabled = () => true;
                    var box = new TextBox { AcceptsReturn = true };
                    PromptBulletLists.Attach(box);

                    box.Text = "-";
                    box.CaretIndex = 1;
                    Assert.IsTrue(TypeSpace(box), "space after a leading dash is consumed");
                    Assert.AreEqual("• ", box.Text);
                    Assert.AreEqual(2, box.CaretIndex);

                    // Backspace right after the conversion brings the typed "- " back.
                    Assert.IsTrue(PressBackspace(box));
                    Assert.AreEqual("- ", box.Text);

                    box.Text = "• one";
                    box.CaretIndex = box.Text.Length;
                    box.SelectedText = "\n";
                    Assert.AreEqual("• one\n• ", box.Text, "a new line continues the list");
                    Assert.AreEqual(box.Text.Length, box.CaretIndex);

                    box.SelectedText = "\r\n";
                    Assert.AreEqual("• one\n", box.Text, "a new line on an empty item ends the list");
                    Assert.AreEqual(box.Text.Length, box.CaretIndex);

                    // Backspace after a bullet that was not just converted removes the bullet.
                    box.Text = "• ";
                    box.CaretIndex = 2;
                    Assert.IsTrue(PressBackspace(box));
                    Assert.AreEqual(string.Empty, box.Text);

                    // Off: nothing changes.
                    PromptBulletLists.IsEnabled = () => false;
                    box.Text = "-";
                    box.CaretIndex = 1;
                    Assert.IsFalse(TypeSpace(box));
                    Assert.AreEqual("-", box.Text);
                }
                finally
                {
                    PromptBulletLists.IsEnabled = previous;
                }
            });
        }

        private static bool TypeSpace(TextBox box)
        {
            var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, " "))
            {
                RoutedEvent = UIElement.PreviewTextInputEvent
            };
            box.RaiseEvent(args);
            return args.Handled;
        }

        private static bool PressBackspace(TextBox box)
        {
            var source = new FakePresentationSource();
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Back)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            };
            box.RaiseEvent(args);
            return args.Handled;
        }

        private sealed class FakePresentationSource : PresentationSource
        {
            protected override CompositionTarget GetCompositionTargetCore() { return null; }
            public override Visual RootVisual { get; set; }
            public override bool IsDisposed { get { return false; } }
        }

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new AssertFailedException(failure.Message, failure);
            }
        }
    }
}
