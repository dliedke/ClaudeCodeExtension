/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Guards the debug-visibility restore — it undoes what VS hides, never what the user parked
 *
 * *******************************************************************************************************************/

using System;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// Keeping the panel visible while debugging (issue #130) started re-asserting visibility on every
    /// run-mode transition, and stepping through code is a run-mode transition per step: an auto-hidden
    /// panel slid itself open for about a second on every F10, and again on Continue and on stop
    /// (issue #141). The restore is now one-shot per debug session and only touches frames VS itself
    /// is holding hidden.
    /// <para>
    /// The logic lives on a WPF control that cannot be instantiated without a shell, and the states it
    /// reads (auto-hide, tab-behind) exist only in a running IDE, so it is guarded at the source level
    /// in the style of <see cref="NativeSessionLifecycleTests"/>.
    /// </para>
    /// </summary>
    [TestClass]
    public class DebugVisibilityTests
    {
        private static string Source => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.DebugVisibility.cs");

        [TestMethod]
        public void RunModeRestore_RunsOncePerDebugSession()
        {
            string source = Source;

            string runMode = ExtractMethodBody(source, "private void OnDebugEnterRunMode(EnvDTE.dbgEventReason reason)");

            // Without this pair, every step re-runs the restore — which is issue #141.
            StringAssert.Contains(runMode, "if (_debugVisibilityHandledForSession) return;",
                "Stepping raises run mode per step; the restore must be skipped after the first one of a session.");
            StringAssert.Contains(runMode, "_debugVisibilityHandledForSession = true;");

            StringAssert.Contains(source, "_debugVisibilityEvents.OnEnterDesignMode += OnDebugEnterDesignMode;",
                "The one-shot flag must be re-armed when the session ends, or only the first F5 of the IDE session restores.");
            StringAssert.Contains(
                ExtractMethodBody(source, "private void OnDebugEnterDesignMode(EnvDTE.dbgEventReason reason)"),
                "_debugVisibilityHandledForSession = false;");
            StringAssert.Contains(source, "_debugVisibilityEvents.OnEnterDesignMode -= OnDebugEnterDesignMode;",
                "Both debugger subscriptions must be released in DisposeDebugVisibility.");
        }

        [TestMethod]
        public void OnlyFramesVisualStudioHidAreShown()
        {
            string source = Source;

            // ShowNoActivate must be reachable only from the guarded helper. A bare Show/ShowNoActivate
            // anywhere else in this file is what slid the user's auto-hidden panel open.
            int shows = Regex.Matches(source, @"(?<!\w)frame\.Show(?:NoActivate)?\s*\(").Count;
            Assert.AreEqual(1, shows,
                "Frames may only be shown through RestoreFrameIfHiddenByVS, which checks who hid them first.");

            string restore = ExtractMethodBody(source, "private static void RestoreFrameIfHiddenByVS(IVsWindowFrame frame)");

            // A frame VS still reports as visible may be off screen because the user auto-hid it or left
            // it behind another tab — deliberate layout choices this feature must not undo.
            StringAssert.Contains(restore, "if (frame.IsVisible() == VSConstants.S_OK) return;",
                "An auto-hidden or tab-behind frame is visible to VS and must be left exactly where the user put it.");
            StringAssert.Contains(restore, "frame.ShowNoActivate();",
                "Restoring visibility must not steal focus from the editor the user is stepping through.");
        }

        [TestMethod]
        public void AClosedPanelIsNotConjuredUpByDebugging()
        {
            StringAssert.Contains(
                ExtractMethodBody(Source, "private async Task ShowExtensionAndCreatedTabsAsync()"),
                "FindToolWindow(typeof(ClaudeCodeToolWindow), 0, false)",
                "Creating the tool window here would reopen a panel the user closed every time they hit F5.");
        }

        /// <summary>
        /// Issue #142: the tool window frame can stay visible-per-VS through a debug-mode layout
        /// change while the embedded conhost/wt.exe HWND — a foreign window joined via SetParent,
        /// invisible to VS's own frame bookkeeping — gets silently orphaned underneath it, leaving
        /// the panel blank and unresponsive to clicks. The restore pass must repair that link too,
        /// not just re-show frames.
        /// </summary>
        [TestMethod]
        public void RestorePassAlsoRepairsAnOrphanedEmbeddedTerminal()
        {
            string source = Source;

            StringAssert.Contains(
                ExtractMethodBody(source, "private async Task ShowExtensionAndCreatedTabsAsync()"),
                "RepairEmbeddedTerminalIfOrphaned();",
                "Frame visibility says nothing about the embedded terminal HWND surviving the debug-layout change.");

            string repair = ExtractMethodBody(source, "private void RepairEmbeddedTerminalIfOrphaned()");

            StringAssert.Contains(repair, "GetParent(terminalHandle) != panel.Handle",
                "Must detect an orphaned terminal by checking whether it is still parented to the active panel.");
            StringAssert.Contains(repair, "SetParent(terminalHandle, panel.Handle);",
                "Must re-embed the terminal once an orphaned parent link is detected.");
            StringAssert.Contains(repair, "ResizeEmbeddedTerminal();",
                "Must reposition the terminal to the panel's current bounds after (or instead of) a re-embed.");
            StringAssert.Contains(repair, "RefreshEmbeddedTerminalWindow();",
                "Must force a repaint pass so a stale-painted surface doesn't linger even when the parent link was fine.");
        }

        /// <summary>
        /// Returns the text of a method's body, located by its signature and closed by brace matching.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Method not found; update this guard with the rename: {signature}");

            int open = source.IndexOf('{', start + signature.Length);
            Assert.IsTrue(open >= 0, $"No body found for: {signature}");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail($"Unbalanced braces while reading: {signature}");
            return string.Empty;
        }
    }
}
