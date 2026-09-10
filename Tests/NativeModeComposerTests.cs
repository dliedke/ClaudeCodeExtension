/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Covers the pure decision helpers behind issue #151 (native mode's selectors/prompt box)
 *          plus source-level guards for the surrounding wiring that cannot run without a VS shell.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// <see cref="ClaudeCodeControl.ResolveComposerMode"/> and
    /// <see cref="ClaudeCodeControl.ShouldHidePromptBox"/> decide what issue #151 was actually about —
    /// whether the chat composer's selectors have an anchor, and whether the panel's prompt box shows —
    /// and both are pure enough to test directly, unlike the rest of the native-mode plumbing (which
    /// needs a live WPF tree and is guarded at the source level instead, in the style of
    /// <see cref="NativeSessionLifecycleTests"/>).
    /// </summary>
    [TestClass]
    public class NativeModeComposerTests
    {
        [TestMethod]
        public void ResolveComposerMode_OutsideNativeMode_IsHiddenRegardlessOfTabState()
        {
            Assert.AreEqual(ChatTranscriptView.ComposerMode.Hidden, ClaudeCodeControl.ResolveComposerMode(false, false));
            Assert.AreEqual(ChatTranscriptView.ComposerMode.Hidden, ClaudeCodeControl.ResolveComposerMode(false, true));
        }

        [TestMethod]
        public void ResolveComposerMode_NativeAndDockedInPanel_IsActionsOnly()
        {
            // This is the exact issue #151 scenario: native mode on, chat not detached to its own tab.
            // ActionsOnly is what keeps the Agent/Model/Effort/Permissions selectors reachable here.
            Assert.AreEqual(ChatTranscriptView.ComposerMode.ActionsOnly, ClaudeCodeControl.ResolveComposerMode(true, false));
        }

        [TestMethod]
        public void ResolveComposerMode_NativeAndInItsOwnTab_IsFull()
        {
            Assert.AreEqual(ChatTranscriptView.ComposerMode.Full, ClaudeCodeControl.ResolveComposerMode(true, true));
        }

        [TestMethod]
        public void ShouldHidePromptBox_ExplicitSettingAlwaysHidesIt()
        {
            // hidePromptPanel=true must win no matter what native mode/auto-hide say.
            Assert.IsTrue(ClaudeCodeControl.ShouldHidePromptBox(true, false, false, false));
            Assert.IsTrue(ClaudeCodeControl.ShouldHidePromptBox(true, true, true, false));
            Assert.IsTrue(ClaudeCodeControl.ShouldHidePromptBox(true, true, false, true));
        }

        [TestMethod]
        public void ShouldHidePromptBox_NotNativeMode_NeverAutoHides()
        {
            Assert.IsFalse(ClaudeCodeControl.ShouldHidePromptBox(false, false, true, true));
            Assert.IsFalse(ClaudeCodeControl.ShouldHidePromptBox(false, false, false, true));
        }

        [TestMethod]
        public void ShouldHidePromptBox_NativeButChatStillDockedInPanel_KeepsTheBoxVisible()
        {
            // The chat's own composer only shows its action row while docked (ComposerMode.ActionsOnly);
            // the panel's prompt box is still the only place to type, so it must not auto-hide here.
            Assert.IsFalse(ClaudeCodeControl.ShouldHidePromptBox(false, true, false, true));
        }

        [TestMethod]
        public void ShouldHidePromptBox_NativeAndChatInTab_AutoHidesOnlyWhenTheSettingIsOn()
        {
            Assert.IsTrue(ClaudeCodeControl.ShouldHidePromptBox(false, true, true, true));
            Assert.IsFalse(ClaudeCodeControl.ShouldHidePromptBox(false, true, true, false));
        }

        private static string NativeChatSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeChat.cs");
        private static string SettingsSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Settings.cs");
        private static string ProviderManagementSource => RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ProviderManagement.cs");
        private static string ChatTranscriptXaml => RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml");
        private static string ChatTranscriptCode => RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");
        private static string PanelXaml => RepositoryLayout.ReadText("UI", "ClaudeCodeControl.xaml");

        [TestMethod]
        public void ReturnNativeChatToPanel_NoLongerFullyHidesTheComposer()
        {
            // The old ShowComposer(false) call is what killed GetSelectorAnchor and caused issue #151 —
            // it must be gone from this method, replaced by SetComposerMode(ResolveComposerMode(...)).
            string body = ExtractMethodBody(NativeChatSource, "private void ReturnNativeChatToPanel()");

            StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex(@"ShowComposer\s*\(\s*false\s*\)"),
                "ReturnNativeChatToPanel must not fully hide the composer — that also hides the selectors (issue #151).");
            StringAssert.Contains(body, "SetComposerMode(ResolveComposerMode(",
                "The composer's visibility must be driven by ResolveComposerMode so it agrees with ShowNativeChatTabAsync.");
        }

        [TestMethod]
        public void ApplyPromptPanelHiddenState_ComputesHiddenThroughTheSharedHelper()
        {
            string body = ExtractMethodBody(SettingsSource, "private void ApplyPromptPanelHiddenState()");

            StringAssert.Contains(body, "ShouldHidePromptBox(",
                "The hide decision must go through the shared, testable helper rather than an inline expression.");
        }

        /// <summary>
        /// Issue #151 round 4: a lone floating ⧉ in an otherwise-empty panel (everything else
        /// collapses once the chat leaves for its own tab) read as confusing clutter rather than a
        /// useful control, so it was dropped from native mode's own toolbar entirely — the panel
        /// already re-shows the chat tab automatically whenever a native session (re)starts.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_NeverShowsTheDetachButtonInNativeMode()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "Apply(ToolbarButton.DetachTerminal, !IsNativeModeActive, DetachToolbarButton, DetachTerminalMenuItem);",
                "⧉ must never show while native mode is active, whether the chat is docked or in its own tab.");
        }

        /// <summary>
        /// The chat tab already has a restart-agent button (↻ ComposerClearButton) — the mirrored
        /// toolbar feed must not add a second one, and DetachTerminal (the panel's own ⧉) makes no
        /// sense mirrored into the tab it opens.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_PromotedButtonMirrorSkipsDetachAndRestart()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "id == ToolbarButton.DetachTerminal || id == ToolbarButton.RestartAgent",
                "The composer's mirrored button feed must skip both DetachTerminal and RestartAgent.");
            StringAssert.Contains(body, "ChatTranscript.SetPromotedButtons(",
                "RefreshToolbarLayout must push the mirrored set into the chat tab's composer.");
        }

        [TestMethod]
        public void ChatTranscriptXaml_DeclaresTheMirroredConfigButtons()
        {
            string xaml = ChatTranscriptXaml;

            StringAssert.Contains(xaml, "x:Name=\"ComposerSettingsButton\"");
            StringAssert.Contains(xaml, "x:Name=\"ComposerToolsButton\"");
            StringAssert.Contains(xaml, "x:Name=\"ComposerCustomCommandsButton\"");
            StringAssert.Contains(xaml, "x:Name=\"ComposerPromotedButtons\"");
        }

        /// <summary>
        /// The reporter's follow-up screenshot showed a Send button next to a composer whose Enter
        /// key already sends (<c>ComposerInput_PreviewKeyDown</c>) — it was pure clutter. It must be
        /// gone from the XAML, its click handler, and the mode-visibility wiring in the code-behind.
        /// </summary>
        [TestMethod]
        public void ChatTranscriptView_NoLongerHasASendButton()
        {
            string xaml = ChatTranscriptXaml;
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");

            StringAssert.DoesNotMatch(xaml, new System.Text.RegularExpressions.Regex("ComposerSendButton"),
                "The Send button must be removed from the composer bar (issue #151 follow-up).");
            StringAssert.DoesNotMatch(cs, new System.Text.RegularExpressions.Regex("ComposerSendButton"),
                "No leftover reference to the removed Send button should remain in the code-behind.");
        }

        /// <summary>
        /// Reinstated after issue #151 round 3 removed it: a file picker is still the only way to
        /// browse to a file outside the editor (drag-and-drop and Ctrl+V paste don't cover that), and
        /// its absence was reported directly. The button, its click handler, its raised event and the
        /// panel's subscriptions to it must all be present, and reuse the same file-picking helper the
        /// terminal panel's own attach button uses so both paths share one implementation.
        /// </summary>
        [TestMethod]
        public void ChatTranscriptView_HasAnAttachButton()
        {
            string xaml = ChatTranscriptXaml;
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");
            string nativeChatCs = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeChat.cs");

            StringAssert.Matches(xaml, new System.Text.RegularExpressions.Regex("ComposerAttachButton"),
                "The attach button must be present in the composer bar.");
            StringAssert.Contains(cs, "public event EventHandler AttachRequested;");
            StringAssert.Contains(cs, "ComposerAttachButton_Click");
            StringAssert.Contains(nativeChatCs, "ChatTranscript.AttachRequested += OnComposerAttachRequested;");
            StringAssert.Contains(nativeChatCs, "transcript.AttachRequested += OnComposerAttachRequested;");
            StringAssert.Contains(nativeChatCs, "PickAttachmentFiles()");

            // Drag-and-drop and Ctrl+V paste remain independent of the button and must still work.
            StringAssert.Contains(cs, "public event EventHandler<string[]> FilesDropped;");
        }

        /// <summary>
        /// Issue #151 round 3: a customized toolbar plus every selector and mirrored config button can
        /// be wider than the tab. The action row must be reachable by horizontal scroll rather than
        /// clipping silently, and the mouse wheel (vertical by default) must be remapped to it since
        /// there is nothing vertical in this row for the wheel to scroll.
        /// </summary>
        [TestMethod]
        public void ComposerActionRow_ScrollsHorizontallyWhenButtonsDontFit()
        {
            string xaml = ChatTranscriptXaml;
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");

            StringAssert.Contains(xaml, "x:Name=\"ComposerActionsScroller\"");
            StringAssert.Contains(xaml, "PreviewMouseWheel=\"ComposerActionsScroller_PreviewMouseWheel\"");
            StringAssert.Contains(cs, "private void ComposerActionsScroller_PreviewMouseWheel(");
            StringAssert.Contains(cs, "ComposerActionsScroller.ScrollToHorizontalOffset(");
        }

        /// <summary>
        /// Round 4: a full-width scrollbar track read as "ugly" under a row this thin, so it's
        /// replaced with small ◀/▶ buttons that only appear once there's actually something to
        /// scroll to in that direction — never a permanently-visible pair of arrows.
        /// </summary>
        [TestMethod]
        public void ComposerActionRow_UsesSmallArrowButtonsInsteadOfAScrollbar()
        {
            string xaml = ChatTranscriptXaml;
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");

            StringAssert.Contains(xaml, "HorizontalScrollBarVisibility=\"Hidden\"",
                "The native scrollbar track must be hidden — the ◀/▶ buttons are the only scroll affordance shown.");
            StringAssert.Contains(xaml, "x:Name=\"ComposerActionsScrollLeftButton\"");
            StringAssert.Contains(xaml, "x:Name=\"ComposerActionsScrollRightButton\"");
            StringAssert.Contains(xaml, "ScrollChanged=\"ComposerActionsScroller_ScrollChanged\"");

            StringAssert.Contains(cs, "private void ComposerActionsScroller_ScrollChanged(");
            StringAssert.Contains(cs, "ComposerActionsScrollLeftButton.Visibility = canScroll && ComposerActionsScroller.HorizontalOffset > 0.5");
            StringAssert.Contains(cs, "ComposerActionsScrollRightButton.Visibility = canScroll && ComposerActionsScroller.HorizontalOffset < ComposerActionsScroller.ScrollableWidth - 0.5");
        }

        /// <summary>
        /// Round 5 originally toggled the arrows with <c>Visibility.Hidden</c> instead of
        /// <c>Collapsed</c>, reasoning that a collapsed "Auto" Grid column would widen the
        /// scroller column and shift the toolbar's footprint whenever an arrow appeared or
        /// disappeared. Round 11: a screenshot showed that reserved space rendering as a visible
        /// dead gap next to the scroller whenever an arrow wasn't needed — worse than the
        /// footprint concern, since Column 2 (the scroller) is this row's only Star column and
        /// already bounded by <c>ComposerBar</c>, so collapsing an arrow's column only hands its
        /// few pixels to the scroller rather than resizing the row itself. Switched both the XAML
        /// default and the ScrollChanged handler to <c>Visibility.Collapsed</c> so unused arrow
        /// space is never reserved.
        /// </summary>
        [TestMethod]
        public void ComposerActionRow_ArrowButtonsCollapseInsteadOfReservingSpace()
        {
            string xaml = ChatTranscriptXaml;
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");

            StringAssert.Contains(xaml, "x:Name=\"ComposerActionsScrollLeftButton\"");
            StringAssert.Contains(xaml, "x:Name=\"ComposerActionsScrollRightButton\"");

            int leftIndex = xaml.IndexOf("x:Name=\"ComposerActionsScrollLeftButton\"", System.StringComparison.Ordinal);
            int rightIndex = xaml.IndexOf("x:Name=\"ComposerActionsScrollRightButton\"", System.StringComparison.Ordinal);
            Assert.IsTrue(leftIndex >= 0 && xaml.IndexOf("Visibility=\"Collapsed\"", leftIndex, System.StringComparison.Ordinal) - leftIndex < 300,
                "ComposerActionsScrollLeftButton must default to Visibility=\"Collapsed\".");
            Assert.IsTrue(rightIndex >= 0 && xaml.IndexOf("Visibility=\"Collapsed\"", rightIndex, System.StringComparison.Ordinal) - rightIndex < 300,
                "ComposerActionsScrollRightButton must default to Visibility=\"Collapsed\".");

            StringAssert.Matches(cs, new System.Text.RegularExpressions.Regex(
                "ComposerActionsScrollLeftButton\\.Visibility = canScroll && ComposerActionsScroller\\.HorizontalOffset > 0\\.5\\s*\\?\\s*Visibility\\.Visible\\s*:\\s*Visibility\\.Collapsed;"),
                "ComposerActionsScrollLeftButton must toggle to Collapsed, not Hidden.");
            StringAssert.Matches(cs, new System.Text.RegularExpressions.Regex(
                "ComposerActionsScrollRightButton\\.Visibility = canScroll && ComposerActionsScroller\\.HorizontalOffset < ComposerActionsScroller\\.ScrollableWidth - 0\\.5\\s*\\?\\s*Visibility\\.Visible\\s*:\\s*Visibility\\.Collapsed;"),
                "ComposerActionsScrollRightButton must toggle to Collapsed, not Hidden.");
        }

        /// <summary>
        /// Round 6: a screenshot showed the session actions and the Agent/Model/Effort/Permissions
        /// selectors scrolling out of view along with everything else. Only the mirrored config
        /// buttons (⚙/☰/⚡ and the promoted-toolbar mirrors) are meant to give way when space runs
        /// out — the session actions and selectors are what actually drive the agent.
        ///
        /// v174.0 keeps that guarantee but moves where it comes from: the whole row now scrolls (so a
        /// tab too narrow even for the shortest captions can still reach every control), and the
        /// essentials are protected by coming *first* in scroll order plus the density tiers that
        /// shrink them before anything is clipped. So the invariant this test enforces is the
        /// ordering: essentials in ComposerEssentialGroup ahead of the mirrors in ComposerMirrorGroup,
        /// both inside the scroller, with the arrows bookending the row.
        /// </summary>
        [TestMethod]
        public void ComposerActionRow_EssentialsScrollAheadOfTheMirroredConfigButtons()
        {
            string xaml = ChatTranscriptXaml;

            int scrollerOpenIdx = xaml.IndexOf("<ScrollViewer x:Name=\"ComposerActionsScroller\"", System.StringComparison.Ordinal);
            int scrollerCloseIdx = xaml.IndexOf("</ScrollViewer>", scrollerOpenIdx, System.StringComparison.Ordinal);
            Assert.IsTrue(scrollerOpenIdx >= 0 && scrollerCloseIdx > scrollerOpenIdx,
                "ComposerActionsScroller must exist as a ScrollViewer with a matching close tag.");

            int essentialIdx = xaml.IndexOf("x:Name=\"ComposerEssentialGroup\"", System.StringComparison.Ordinal);
            int mirrorIdx = xaml.IndexOf("x:Name=\"ComposerMirrorGroup\"", System.StringComparison.Ordinal);
            Assert.IsTrue(essentialIdx > scrollerOpenIdx && essentialIdx < scrollerCloseIdx,
                "ComposerEssentialGroup must sit inside ComposerActionsScroller.");
            Assert.IsTrue(mirrorIdx > essentialIdx && mirrorIdx < scrollerCloseIdx,
                "ComposerMirrorGroup must follow ComposerEssentialGroup inside the scroller, so the mirrors are what runs off the right edge first.");

            // The controls that drive the agent lead the scrolled content.
            foreach (string essential in new[]
            {
                "ComposerOverflowButton", "ComposerClearButton", "ComposerNewChatButton",
                "ComposerRenameSessionButton", "ComposerColorButton", "ComposerProviderButton",
                "ComposerModelButton", "ComposerEffortButton", "ComposerPermissionButton"
            })
            {
                int idx = xaml.IndexOf($"x:Name=\"{essential}\"", System.StringComparison.Ordinal);
                Assert.IsTrue(idx >= 0, $"{essential} not found.");
                Assert.IsTrue(idx > essentialIdx && idx < mirrorIdx,
                    $"{essential} must sit in ComposerEssentialGroup, ahead of the mirrored config buttons.");
            }

            // The mirrors trail behind — they are duplicates of controls the panel already has.
            foreach (string mirror in new[]
            {
                "ComposerPromotedButtons", "ComposerCustomCommandsButton", "ComposerToolsButton", "ComposerSettingsButton"
            })
            {
                int idx = xaml.IndexOf($"x:Name=\"{mirror}\"", System.StringComparison.Ordinal);
                Assert.IsTrue(idx >= 0, $"{mirror} not found.");
                Assert.IsTrue(idx > mirrorIdx && idx < scrollerCloseIdx,
                    $"{mirror} must sit in ComposerMirrorGroup so it scrolls out of view before the selectors do.");
            }

            int leftArrowIdx = xaml.IndexOf("x:Name=\"ComposerActionsScrollLeftButton\"", System.StringComparison.Ordinal);
            int rightArrowIdx = xaml.IndexOf("x:Name=\"ComposerActionsScrollRightButton\"", System.StringComparison.Ordinal);

            Assert.IsTrue(leftArrowIdx >= 0 && leftArrowIdx < scrollerOpenIdx,
                "The ◀ arrow must precede the scrollable section.");
            Assert.IsTrue(rightArrowIdx > scrollerCloseIdx,
                "The ▶ arrow must follow the scrollable section.");
        }

        /// <summary>
        /// v174.0: a narrow chat tab used to clip the row — "Sonnet ▾" was cut in half and the effort,
        /// permission and mirrored buttons after it were unreachable. The density pass is what prevents
        /// that, so it must measure the essential group (never the mirrors, which are meant to scroll)
        /// and must walk its tiers from the widest one every time, or a tab that is widened again would
        /// stay stuck on the short captions.
        /// </summary>
        [TestMethod]
        public void ComposerDensity_MeasuresTheEssentialGroupAndStartsFromTheWidestTier()
        {
            string cs = ChatTranscriptCode;

            StringAssert.Contains(cs, "ComposerEssentialGroup.Measure(",
                "RefreshComposerDensity must measure ComposerEssentialGroup to choose the tier.");
            Assert.IsFalse(cs.Contains("ComposerMirrorGroup.Measure("),
                "The mirrored config buttons must not drive the density tier — they are meant to scroll out of view instead.");

            int orderIdx = cs.IndexOf("_composerDensityOrder", System.StringComparison.Ordinal);
            Assert.IsTrue(orderIdx >= 0, "The density tiers must be walked from a declared order.");

            int fullIdx = cs.IndexOf("ComposerDensity.Full", orderIdx, System.StringComparison.Ordinal);
            int compactIdx = cs.IndexOf("ComposerDensity.Compact", orderIdx, System.StringComparison.Ordinal);
            int tightIdx = cs.IndexOf("ComposerDensity.Tight", orderIdx, System.StringComparison.Ordinal);
            Assert.IsTrue(fullIdx >= 0 && fullIdx < compactIdx && compactIdx < tightIdx,
                "The tier order must run widest first (Full, Compact, Tight) so widening the tab restores the full captions.");

            // The ⋯ menu is the only place the folded session actions remain reachable, so it has to
            // invoke the same handlers rather than a second copy of the logic.
            string xaml = ChatTranscriptXaml;
            int overflowIdx = xaml.IndexOf("x:Name=\"ComposerOverflowButton\"", System.StringComparison.Ordinal);
            int overflowEndIdx = xaml.IndexOf("</Button>", overflowIdx, System.StringComparison.Ordinal);
            string overflow = xaml.Substring(overflowIdx, overflowEndIdx - overflowIdx);

            foreach (string handler in new[]
            {
                "ComposerClearButton_Click", "ComposerNewChatButton_Click",
                "ComposerRenameSessionButton_Click", "ComposerColorButton_Click"
            })
            {
                StringAssert.Contains(overflow, handler,
                    $"The ⋯ menu must reuse {handler} instead of duplicating the action.");
            }
        }

        /// <summary>
        /// Issue #151 follow-up: the composer's mirrored config buttons (⚙/☰/⚡ and the promoted-
        /// toolbar mirrors) are redundant while the chat is still docked in the panel — the panel's
        /// own copies are right there in the same window — so they must only appear once the chat has
        /// its own tab (<c>ComposerMode.Full</c>), not in <c>ActionsOnly</c>.
        /// </summary>
        [TestMethod]
        public void SetComposerMode_RestrictsMirroredConfigButtonsToFullMode()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs"),
                "public void SetComposerMode(ComposerMode mode)");

            StringAssert.Contains(body, "ComposerSettingsButton.Visibility = fullOnly",
                "⚙ must only show once the chat is in its own tab.");
            StringAssert.Contains(body, "UpdateCustomCommandsButtonVisibility()",
                "⚡ visibility must be recomputed on every mode change, not fixed to fullOnly alone " +
                "(issue #151 round 7: it must also stay hidden when no custom commands are configured).");
            StringAssert.Contains(body, "ComposerPromotedButtons.Visibility = fullOnly",
                "The promoted-toolbar mirrors must only show once the chat is in its own tab.");
        }

        /// <summary>
        /// Issue #151 round 7: "the ⚡ menu should show only if we have actions configured" — the
        /// composer's ⚡ mirror was gated only on composer mode (fullOnly), unlike the panel's own ⚡
        /// (CustomCommandsButton), which already collapses when <c>_settings.CustomCommands</c> is
        /// empty (ClaudeCodeControl.CustomCommands.cs's RefreshCustomCommandsButton). The mirror must
        /// track the same "has any commands" signal, not just the composer mode.
        /// </summary>
        [TestMethod]
        public void SetCustomCommandsMenuHasItems_FeedsTheSameGateAsTheFullOnlyVisibility()
        {
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");

            StringAssert.Contains(cs, "public void SetCustomCommandsMenuHasItems(bool hasItems)",
                "The chat view needs a way to hear whether any custom commands are configured.");
            StringAssert.Contains(cs, "_composerMode == ComposerMode.Full && _customCommandsHasItems",
                "⚡ must be gated by both Full mode and having at least one configured command, not either alone.");
        }

        [TestMethod]
        public void RefreshCustomCommandsButton_FeedsHasAnyIntoTheComposerMirror()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.CustomCommands.cs"),
                "private void RefreshCustomCommandsButton()");

            StringAssert.Contains(body, "ChatTranscript?.SetCustomCommandsMenuHasItems(hasAny)",
                "The composer's ⚡ mirror must collapse under the same condition as the panel's own ⚡.");
        }

        /// <summary>
        /// "The tools menu should not appear in case there are no items" — the composer's ☰ mirror
        /// must track the same <c>anyInDropdown</c> computation the panel's own ☰ uses, not just the
        /// composer mode.
        /// </summary>
        [TestMethod]
        public void SetToolsMenuHasItems_FeedsTheSameGateAsTheFullOnlyVisibility()
        {
            string cs = RepositoryLayout.ReadText("UI", "ChatTranscriptView.xaml.cs");

            StringAssert.Contains(cs, "public void SetToolsMenuHasItems(bool hasItems)",
                "The chat view needs a way to hear whether the Tools menu would be empty.");
            StringAssert.Contains(cs, "_composerMode == ComposerMode.Full && _toolsMenuHasItems",
                "☰ must be gated by both Full mode and having at least one item, not either alone.");
        }

        [TestMethod]
        public void RefreshToolbarLayout_FeedsAnyInDropdownIntoTheComposerMirror()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "ChatTranscript?.SetToolsMenuHasItems(anyInDropdown)",
                "The composer's ☰ mirror must collapse under the same condition as the panel's own ☰.");
        }

        /// <summary>
        /// The core ask: "duplicated buttons in the native window and in the claude code panel tab,
        /// it should be only in the native window." Once the chat has actually left for its own tab,
        /// every one of the panel's own promoted buttons/⚙/☰/⚡/🤖/⧉ must collapse — round 4 dropped
        /// ⧉ from native mode's toolbar entirely (see <see cref="RefreshToolbarLayout_NeverShowsTheDetachButtonInNativeMode"/>),
        /// so nothing needs to be spared from this loop anymore.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_CollapsesThePanelsOwnControlsOnceTheChatIsInItsOwnTab()
        {
            string body = ExtractMethodBody(ProviderManagementSource, "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "if (IsChatDetachedToOwnTab)",
                "The collapse must be gated on the chat having actually left the panel, not just native mode being on — ActionsOnly still needs the panel's own toolbar.");
            StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex("if \\(id == ToolbarButton\\.DetachTerminal\\) continue;"),
                "DetachTerminal no longer needs sparing from this loop — it's unconditionally collapsed in native mode before this point.");
            StringAssert.Contains(body, "if (ModelDropdownButton != null) ModelDropdownButton.Visibility = Visibility.Collapsed;");
            StringAssert.Contains(body, "if (ToolsDropdownButton != null) ToolsDropdownButton.Visibility = Visibility.Collapsed;");
            StringAssert.Contains(body, "if (CustomCommandsButton != null) CustomCommandsButton.Visibility = Visibility.Collapsed;");
            StringAssert.Contains(body, "if (MenuDropdownButton != null) MenuDropdownButton.Visibility = Visibility.Collapsed;");
            StringAssert.Contains(body, "if (AttachDropdownButton != null) AttachDropdownButton.Visibility = Visibility.Collapsed;",
                "AttachDropdownButton lives in ControlsRow, not inside the auto-hidden PromptGroupBox, so it needs its own collapse.");
            StringAssert.Contains(body, "if (SendPromptButton != null) SendPromptButton.Visibility = Visibility.Collapsed;",
                "SendPromptButton lives in ControlsRow, not inside the auto-hidden PromptGroupBox, so it needs its own collapse.");
        }

        /// <summary>
        /// The v170.0 collapse logic above only ever ran inside RefreshToolbarLayout, but nothing
        /// called that method when the chat actually moved in or out of its own tab — so the panel
        /// kept showing its full toolbar until some unrelated event happened to refresh it (the exact
        /// duplication the reporter's second screenshot caught). Both transition points must trigger it.
        /// </summary>
        [TestMethod]
        public void ChatTabTransitions_RefreshTheToolbarLayoutSoThePanelActuallyCollapses()
        {
            string showBody = ExtractMethodBody(NativeChatSource, "private async Task ShowNativeChatTabAsync(bool focusComposer)");
            string returnBody = ExtractMethodBody(NativeChatSource, "private void ReturnNativeChatToPanel()");

            StringAssert.Contains(showBody, "RefreshToolbarLayout();",
                "Entering the chat tab must refresh the panel's toolbar so its own controls collapse.");
            StringAssert.Contains(returnBody, "RefreshToolbarLayout();",
                "Returning the chat to the panel must refresh the toolbar so the panel's controls reappear.");
        }

        /// <summary>
        /// Two other call sites unconditionally reset <c>SendPromptButton.Visibility</c> from the
        /// <c>SendWithEnter</c> setting (initial load and the Settings dialog's Apply). Both must defer
        /// to <c>IsChatDetachedToOwnTab</c> or a settings change while the chat is in its own tab would
        /// bring the Send button back next to a prompt box that is no longer even shown.
        /// </summary>
        [TestMethod]
        public void SendPromptButtonVisibility_DefersToChatDetachedState()
        {
            string settingsCs = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.Settings.cs");
            string settingsDialogCs = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.SettingsDialog.cs");

            StringAssert.Contains(settingsCs, "_settings != null && !IsChatDetachedToOwnTab");
            StringAssert.Contains(settingsDialogCs, "if (!IsChatDetachedToOwnTab)");
        }

        /// <summary>
        /// The action row (session actions, selectors, mirrored config buttons) must sit above the
        /// prompt text box, not below it — the second screenshot showed it crammed at the very bottom
        /// of the window, underneath the box it controls.
        /// </summary>
        [TestMethod]
        public void ComposerBar_ActionRowSitsAboveTheInputBoxNotBelowIt()
        {
            string xaml = ChatTranscriptXaml;

            // Grid.Row assignments decide visual order regardless of document order (WPF doesn't
            // require rows to be declared in order), so read the row number each element actually
            // declares rather than comparing where they appear in the file.
            int resizeGripRow = GridRowOfNamedElement(xaml, "ComposerResizeGrip");
            int attachmentsRow = GridRowOfNamedElement(xaml, "ComposerAttachments");
            int inputBorderRow = GridRowOfNamedElement(xaml, "ComposerInputBorder");
            // The action row is a Grid now (arrow buttons flanking the scrollable button strip), which
            // is what carries the Grid.Row — not the DockPanel or ScrollViewer nested inside it.
            int actionRowRow = GridRowOfNamedElement(xaml, "ComposerActionRow");

            Assert.IsTrue(resizeGripRow >= 0 && attachmentsRow >= 0 && inputBorderRow >= 0 && actionRowRow >= 0,
                "One of the composer's rows could not be found — update this guard with the rename.");
            Assert.IsTrue(resizeGripRow < actionRowRow && actionRowRow < attachmentsRow && attachmentsRow < inputBorderRow,
                "Expected row order: resize grip, then the action row, then attachment chips, then the text box.");
        }

        /// <summary>
        /// Issue #151 round 7: "In terminal, the settings button is gone." Root cause — ControlsRow's
        /// right-hand column held the whole RightButtonsPanel as "Auto" width, which never shrinks no
        /// matter how narrow the tool window gets; the panel just rendered past the visible edge with
        /// no scrollbar and no indication anything was hidden, and ⚙ (declared last, so pushed
        /// furthest toward the edge) was the first casualty. The column must be Star so the row
        /// actually has a bounded width to scroll within.
        /// </summary>
        [TestMethod]
        public void ControlsRow_RightColumnIsStar_SoTheRightButtonsRowHasABoundedWidthToScrollWithin()
        {
            string xaml = PanelXaml;
            int gridIdx = xaml.IndexOf("x:Name=\"ControlsRow\"", System.StringComparison.Ordinal);
            Assert.IsTrue(gridIdx >= 0, "ControlsRow not found — update this guard with the rename.");

            int colDefsIdx = xaml.IndexOf("<Grid.ColumnDefinitions>", gridIdx, System.StringComparison.Ordinal);
            int colDefsEnd = xaml.IndexOf("</Grid.ColumnDefinitions>", colDefsIdx, System.StringComparison.Ordinal);
            string colDefs = xaml.Substring(colDefsIdx, colDefsEnd - colDefsIdx);

            int starIdx = colDefs.IndexOf("Width=\"*\"", System.StringComparison.Ordinal);
            int lastAutoIdx = colDefs.LastIndexOf("Width=\"Auto\"", System.StringComparison.Ordinal);
            Assert.IsTrue(starIdx >= 0 && starIdx > lastAutoIdx,
                "The last (rightmost) column, which holds the right-buttons row, must be Star — an " +
                "Auto column always grows to fit its content regardless of the tool window's width.");
        }

        /// <summary>
        /// The fixed group (☰ Tools, ⚡ Custom Commands, 🤖 Model, ⚙ Settings/Agent) must never scroll
        /// out of reach — none of them have a fallback elsewhere if they silently disappear. Only the
        /// customizable feature buttons (which already fall back to the ☰ menu when not promoted) are
        /// allowed to scroll, mirroring the split already used for the native-mode composer's action
        /// row (issue #151 round 6).
        /// </summary>
        [TestMethod]
        public void RightButtonsPanel_FeatureButtonsScroll_FixedGroupNeverDoes()
        {
            string xaml = PanelXaml;

            int leftArrowIdx = xaml.IndexOf("x:Name=\"RightButtonsScrollLeftButton\"", System.StringComparison.Ordinal);
            int scrollerOpen = xaml.IndexOf("x:Name=\"RightButtonsScroller\"", System.StringComparison.Ordinal);
            int scrollerClose = xaml.IndexOf("</ScrollViewer>", scrollerOpen, System.StringComparison.Ordinal);
            int rightArrowIdx = xaml.IndexOf("x:Name=\"RightButtonsScrollRightButton\"", System.StringComparison.Ordinal);
            int fixedGroupIdx = xaml.IndexOf("x:Name=\"FixedRightButtonsPanel\"", System.StringComparison.Ordinal);

            Assert.IsTrue(leftArrowIdx >= 0 && scrollerOpen >= 0 && scrollerClose > scrollerOpen
                && rightArrowIdx >= 0 && fixedGroupIdx >= 0,
                "One of the right-buttons row's elements could not be found — update this guard with the rename.");

            foreach (string essential in new[] { "MenuDropdownButton", "ModelDropdownButton", "ToolsDropdownButton", "CustomCommandsButton" })
            {
                int idx = xaml.IndexOf($"x:Name=\"{essential}\"", System.StringComparison.Ordinal);
                Assert.IsTrue(idx >= 0 && idx > fixedGroupIdx,
                    $"{essential} must live in the fixed group (FixedRightButtonsPanel) — it has no fallback " +
                    "if it scrolls out of reach and silently disappears (issue #151 round 7).");
                Assert.IsFalse(idx > scrollerOpen && idx < scrollerClose,
                    $"{essential} must not be inside RightButtonsScroller.");
            }

            Assert.IsTrue(leftArrowIdx < fixedGroupIdx && fixedGroupIdx < rightArrowIdx && rightArrowIdx < scrollerOpen,
                "Expected document order: ◀ arrow (docked left), then the fixed group and ▶ arrow (both docked " +
                "right, fixed group first so it ends up rightmost), then the scroller last as the fill child " +
                "(issue #151 round 9).");
        }

        /// <summary>
        /// Issue #151 round 9: the right-buttons row must be a DockPanel, not a Grid. A Grid splits its
        /// width between Star columns proportionally regardless of what the content actually needs, which
        /// arranged ⚙ past the tool window's visible right edge and handed the ScrollViewer a viewport
        /// wider than the panel — so it never had anything to scroll and the ◀/▶ arrows never appeared.
        /// A DockPanel measures each docked child against the space genuinely left over and gives the fill
        /// child exactly the remainder, so the fixed group is always on screen and the scroller's viewport
        /// is always the real available width.
        /// </summary>
        [TestMethod]
        public void RightButtonsRow_IsADockPanel_SoTheFixedGroupIsNeverArrangedOffScreen()
        {
            string xaml = PanelXaml;

            int rowIdx = xaml.IndexOf("x:Name=\"RightButtonsRow\"", System.StringComparison.Ordinal);
            Assert.IsTrue(rowIdx >= 0, "RightButtonsRow not found.");

            int openIdx = xaml.LastIndexOf("<", rowIdx, System.StringComparison.Ordinal);
            int closeIdx = xaml.IndexOf(">", rowIdx, System.StringComparison.Ordinal);
            string openTag = xaml.Substring(openIdx, closeIdx - openIdx);

            StringAssert.StartsWith(openTag, "<DockPanel",
                "RightButtonsRow must be a DockPanel — a Grid's Star columns consume their full proportional " +
                "share whether the content needs it or not, which pushed ⚙ off screen (issue #151 round 9).");
            StringAssert.Contains(openTag, "LastChildFill=\"True\"",
                "The ScrollViewer is the last child and needs the leftover width; without LastChildFill it " +
                "would only get its own desired size and would never scroll.");

            int leftArrow = xaml.IndexOf("x:Name=\"RightButtonsScrollLeftButton\"", System.StringComparison.Ordinal);
            Assert.IsTrue(leftArrow >= 0, "RightButtonsScrollLeftButton not found.");
            StringAssert.Contains(xaml.Substring(leftArrow, 200), "DockPanel.Dock=\"Left\"",
                "The ◀ arrow must be docked left.");

            foreach (string dockedRight in new[] { "FixedRightButtonsPanel", "RightButtonsScrollRightButton" })
            {
                int idx = xaml.IndexOf($"x:Name=\"{dockedRight}\"", System.StringComparison.Ordinal);
                Assert.IsTrue(idx >= 0, $"{dockedRight} not found.");
                StringAssert.Contains(xaml.Substring(idx, 200), "DockPanel.Dock=\"Right\"",
                    $"{dockedRight} must be docked right so it is laid out before the fill child and can " +
                    "never be pushed outside the panel.");
            }

            // The scroller must be the panel's last child, otherwise LastChildFill hands the leftover
            // width to something else and the scroller falls back to its (unbounded) desired size.
            int scrollerIdx = xaml.IndexOf("x:Name=\"RightButtonsScroller\"", System.StringComparison.Ordinal);
            int scrollerEnd = xaml.IndexOf("</ScrollViewer>", scrollerIdx, System.StringComparison.Ordinal);
            int rowEnd = xaml.IndexOf("</DockPanel>", scrollerEnd, System.StringComparison.Ordinal);
            Assert.IsTrue(rowEnd > scrollerEnd, "RightButtonsRow's closing </DockPanel> not found.");
            string afterScroller = xaml.Substring(scrollerEnd + "</ScrollViewer>".Length, rowEnd - scrollerEnd - "</ScrollViewer>".Length);
            Assert.IsFalse(afterScroller.Contains("x:Name="),
                "RightButtonsScroller must be the DockPanel's last child so LastChildFill gives it the " +
                "remaining width (issue #151 round 9).");
        }

        [TestMethod]
        public void RightButtonsScroller_HasArrowScrollHandlersMirroringTheComposer()
        {
            string cs = RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ProviderManagement.cs");

            StringAssert.Contains(cs, "private void RightButtonsScrollLeftButton_Click(object sender, RoutedEventArgs e)");
            StringAssert.Contains(cs, "private void RightButtonsScrollRightButton_Click(object sender, RoutedEventArgs e)");
            StringAssert.Contains(cs, "private void RightButtonsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)");

            string scrollChangedBody = ExtractMethodBody(cs,
                "private void RightButtonsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)");
            StringAssert.Contains(scrollChangedBody, "Visibility.Hidden",
                "Arrow buttons must toggle Hidden, not Collapsed, so the row's own footprint never changes " +
                "size when an arrow appears or disappears (same reasoning as the composer's action row, round 6).");
        }

        /// <summary>
        /// Issue #151 round 8: a Grid Star column, when given a genuinely bounded (not infinite)
        /// available width by its parent, always consumes its full proportional share for both
        /// measure and arrange — even when its content is narrower than that share. Left at the
        /// default Stretch alignment, RightButtonsScroller therefore left extra blank space between
        /// the last promoted button and the always-visible ▶ arrow / fixed group (⚙/🤖/☰/⚡),
        /// visually splitting one toolbar into two. Right-aligning the scroller moves any unused
        /// slack to its own left edge (next to the ◀ arrow) instead, so it always sits flush against
        /// the fixed group with no gap when everything fits.
        /// </summary>
        [TestMethod]
        public void RightButtonsScroller_IsRightAligned_SoItHugsTheFixedGroupWithNoGap()
        {
            string xaml = PanelXaml;

            int scrollerIdx = xaml.IndexOf("x:Name=\"RightButtonsScroller\"", System.StringComparison.Ordinal);
            int scrollerTagEnd = xaml.IndexOf(">", scrollerIdx, System.StringComparison.Ordinal);
            Assert.IsTrue(scrollerIdx >= 0 && scrollerTagEnd > scrollerIdx, "RightButtonsScroller not found.");

            string openTag = xaml.Substring(scrollerIdx, scrollerTagEnd - scrollerIdx);
            StringAssert.Contains(openTag, "HorizontalAlignment=\"Right\"",
                "Without this, the Star column's unused slack renders as a visible gap between the " +
                "scrollable feature buttons and the always-visible fixed group instead of at the row's own start.");
        }

        /// <summary>
        /// Issue #151 round 8: RefreshCustomCommandsButton() runs on its own (from
        /// ApplyLoadedSettings and after the configure-commands dialog closes) and used to set
        /// CustomCommandsButton.Visibility unconditionally from "hasAny" alone. If it ran while the
        /// chat was already detached to its own tab, it silently undid RefreshToolbarLayout()'s
        /// IsChatDetachedToOwnTab collapse, popping ⚡ back onto the panel toolbar on its own — with
        /// nothing else in that row visible, it rendered as a single stray button floating near the
        /// panel's title bar.
        /// </summary>
        [TestMethod]
        public void RefreshCustomCommandsButton_RespectsChatDetachedToOwnTab()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.CustomCommands.cs"),
                "private void RefreshCustomCommandsButton()");

            StringAssert.Contains(body, "hasAny && !IsChatDetachedToOwnTab",
                "The panel's own ⚡ must stay collapsed while the chat is detached, even when this method " +
                "runs independently of RefreshToolbarLayout — the composer's mirror is what should show instead.");
        }

        /// <summary>
        /// Issue #151 round 9: collapsing every individual control inside ControlsRow and CheckboxRow was
        /// not enough — the ◀/▶ arrows are Hidden (not Collapsed) by design, and the empty file-chips row
        /// still contributes its margins, so both rows kept reserving height. With the prompt box also
        /// auto-hidden in this state, that showed as a dead strip between the panel's title bar and the
        /// usage bars, which are the only thing the panel still owns once the chat has its own tab.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_CollapsesTheWholeControlsAndCheckboxRows_WhenChatIsDetached()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ProviderManagement.cs"),
                "private void RefreshToolbarLayout()");

            StringAssert.Contains(body, "ControlsRow.Visibility = panelRows",
                "ControlsRow itself must collapse when the chat owns its tab — its Hidden scroll arrows " +
                "keep reserving height otherwise (issue #151 round 9).");
            StringAssert.Contains(body, "CheckboxRow.Visibility = panelRows",
                "CheckboxRow itself must collapse when the chat owns its tab — an empty file-chips row " +
                "still contributes its own margins otherwise (issue #151 round 9).");
            StringAssert.Contains(body, "IsChatDetachedToOwnTab ? Visibility.Collapsed : Visibility.Visible",
                "Both rows must come back the moment the chat is docked back in the panel.");
        }

        /// <summary>
        /// Issue #151 round 9: hiding whichever arrow had nowhere left to go meant the row usually showed
        /// a single lone arrow, which reads as decoration rather than as "there is more over here" — the
        /// reporter didn't recognize the row as scrollable at all. Both arrows now appear together as soon
        /// as the row can scroll, with the dead direction greyed out instead of vanishing.
        /// </summary>
        [TestMethod]
        public void RightButtonsScroller_ShowsBothArrowsTogether_AndGreysOutTheDeadDirection()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ProviderManagement.cs"),
                "private void RightButtonsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)");

            StringAssert.Contains(body, "RightButtonsScrollLeftButton.Visibility = arrows",
                "Both arrows must share one visibility decision so neither ever appears alone.");
            StringAssert.Contains(body, "RightButtonsScrollRightButton.Visibility = arrows");
            StringAssert.Contains(body, "RightButtonsScrollLeftButton.IsEnabled",
                "The direction with nowhere to go must be disabled rather than hidden.");
            StringAssert.Contains(body, "RightButtonsScrollRightButton.IsEnabled");
        }

        /// <summary>
        /// Issue #151 round 10: the IsChatDetachedToOwnTab block collapses ⚙, ⚡, 📎 and ▶ Send, but
        /// unlike everything else it touches, nothing earlier in RefreshToolbarLayout recomputes those
        /// four from scratch — so the collapse was one-way and they never came back. Switching native
        /// mode off left the classic terminal panel permanently without its ⚙ Settings/Agent button,
        /// with no other route to it. Every control collapsed in that branch must have a restoring
        /// counterpart in the else-branch.
        /// </summary>
        [TestMethod]
        public void RefreshToolbarLayout_RestoresTheDetachOnlyCollapses_WhenTheChatIsNotDetached()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.ProviderManagement.cs"),
                "private void RefreshToolbarLayout()");

            int elseIdx = body.IndexOf("MenuDropdownButton.Visibility = Visibility.Visible", System.StringComparison.Ordinal);
            Assert.IsTrue(elseIdx > 0,
                "⚙ MenuDropdownButton has no other Visibility authority anywhere in the codebase — if " +
                "RefreshToolbarLayout collapses it for the detached chat, it must also restore it here, " +
                "or the button is gone for good (issue #151 round 10).");

            StringAssert.Contains(body, "AttachDropdownButton.Visibility = Visibility.Visible",
                "📎 Attach is collapsed with the prompt box in the detached state and has no other " +
                "Visibility authority — it must be restored explicitly.");
            StringAssert.Contains(body, "RefreshCustomCommandsButton()",
                "⚡ must be re-evaluated (it hides when no custom commands exist), not forced Visible.");
            StringAssert.Contains(body, "_settings.SendWithEnter",
                "▶ Send must be restored per its own send-with-Enter rule, not forced Visible.");
        }

        /// <summary>
        /// Issue #151 round 11: an agent that will not run on the selected model is the one start
        /// failure native mode cannot recover from on its own — the agent is healthy, so retrying lands
        /// in the same place, and the fix (the 🤖 model menu) is hidden while native mode is active.
        /// Both entry points must therefore hand the user back to the embedded terminal.
        /// </summary>
        [TestMethod]
        public void NativeStart_RollsBackToTheTerminal_WhenTheAgentRefusesTheSelectedModel()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs"),
                "private async Task<NativeStartOutcome> StartNativeModeCoreAsync()");

            int modelCatch = body.IndexOf("catch (AgentModelUnavailableException", System.StringComparison.Ordinal);
            int generalCatch = body.IndexOf("catch (Exception", System.StringComparison.Ordinal);
            Assert.IsTrue(modelCatch >= 0, "AgentModelUnavailableException must get its own handler.");
            Assert.IsTrue(generalCatch < 0 || modelCatch < generalCatch,
                "The model handler has to come before catch (Exception), which would otherwise swallow it.");

            string tail = body.Substring(modelCatch);
            StringAssert.Contains(tail, "HandOffToEmbeddedTerminalAsync",
                "Silence here reads as native mode simply not working — the hand-off is what shows the " +
                "notice telling the user which model to change, and what puts ⚙ back within reach so " +
                "they can (issue #151 round 12).");
            StringAssert.Contains(tail, "NativeStartOutcome.Declined",
                "Declined is what makes the caller launch the embedded terminal instead.");
        }

        [TestMethod]
        public void NativeRelaunch_RollsBackToTheTerminalOutsideTheLifecycleLock_OnAModelRefusal()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeChat.cs"),
                "private async Task RelaunchNativeSessionAsync(string notice, bool forceNewSession = false, bool appendAccountLabel = false, Func<Task> midRelaunchAsync = null)");

            StringAssert.Contains(body, "catch (AgentModelUnavailableException",
                "A model switch to something the agent does not list must not leave the chat sitting on " +
                "a dead session — the panel's 🤖 menu is hidden in native mode, so there would be no way back.");

            int release = body.IndexOf("_nativeLifecycleSemaphore.Release()", System.StringComparison.Ordinal);
            int restart = body.IndexOf("RestartTerminalWithSelectedProviderAsync()", System.StringComparison.Ordinal);
            Assert.IsTrue(release >= 0 && restart > release,
                "The rollback starts a new session and so takes _nativeLifecycleSemaphore itself — it " +
                "must run after this method has released it, or it deadlocks (issue #151 round 11).");
        }

        [TestMethod]
        public void NativeStart_SendsEveryTerminalFallbackThroughTheOneRestorePath()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs"),
                "private async Task<NativeStartOutcome> StartNativeModeCoreAsync()");

            Assert.IsFalse(body.Contains("ShowNativeFallbackNoticeAsync"),
                "Issue #151 round 12: a fallback that only prints a notice leaves the panel wearing " +
                "native mode's collapsed toolbar. Every decline has to go through " +
                "HandOffToEmbeddedTerminalAsync, which shows the notice *and* restores the controls.");
            Assert.IsFalse(body.Contains("ShutdownNativeModeAsync"),
                "The teardown belongs to HandOffToEmbeddedTerminalAsync, where a throw in it can no " +
                "longer skip the restore that follows.");

            int modelCatch = body.IndexOf("catch (AgentModelUnavailableException", System.StringComparison.Ordinal);
            int generalCatch = body.IndexOf("catch (Exception", System.StringComparison.Ordinal);
            Assert.IsTrue(modelCatch > 0 && generalCatch > modelCatch,
                "Both catch blocks must still be there, model first (see the round 11 guard).");

            StringAssert.Contains(body.Substring(modelCatch, generalCatch - modelCatch),
                "HandOffToEmbeddedTerminalAsync",
                "The model rollback is the one the reporter hit: it has to land on a panel with ⚙ on it.");
            StringAssert.Contains(body.Substring(generalCatch), "HandOffToEmbeddedTerminalAsync",
                "Any other start failure ends up on the terminal the same way, so it needs the same restore.");
        }

        [TestMethod]
        public void TerminalHandOff_RestoresTheSettingsButton_EvenWhenTheTeardownThrows()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs"),
                "private async Task HandOffToEmbeddedTerminalAsync(string notice)");

            int shutdown = body.IndexOf("await ShutdownNativeModeAsync();", System.StringComparison.Ordinal);
            int guard = body.IndexOf("catch (Exception", System.StringComparison.Ordinal);
            Assert.IsTrue(shutdown >= 0 && guard > shutdown,
                "The teardown must be inside a try: a throw there used to escape the whole start " +
                "method, so the panel never got its toolbar back and the caller never launched the " +
                "terminal either (issue #151 round 12).");

            int restore = body.IndexOf("EnsureSettingsButtonReachable()", System.StringComparison.Ordinal);
            int notice = body.IndexOf("ShowNativeFallbackNoticeAsync", System.StringComparison.Ordinal);
            Assert.IsTrue(restore > body.IndexOf("ShowNativeTranscript(false)", System.StringComparison.Ordinal),
                "The last-resort restore runs after the normal one, so it wins whatever that produced.");
            Assert.IsTrue(notice > restore,
                "The notice tells the user to fix the model from the panel — the button it points at " +
                "has to be there before it says so.");
        }

        [TestMethod]
        public void EnsureSettingsButtonReachable_ShowsTheMenuButtonAndBothRowsThatCarryIt()
        {
            string body = ExtractMethodBody(
                RepositoryLayout.ReadText("Controls", "ClaudeCodeControl.NativeMode.cs"),
                "private void EnsureSettingsButtonReachable()");

            StringAssert.Contains(body, "MenuDropdownButton.Visibility = Visibility.Visible",
                "⚙ is the whole point: it is where the model, the provider and the native-mode " +
                "setting itself are changed after a failed start.");
            StringAssert.Contains(body, "ControlsRow.Visibility = Visibility.Visible",
                "⚙ is inside ControlsRow, which round 9 collapses while the chat owns its own tab — " +
                "showing the button inside a collapsed row shows nothing.");
            StringAssert.Contains(body, "RightButtonsRow.Visibility = Visibility.Visible",
                "Same again one level down: RightButtonsRow is the DockPanel that holds the fixed " +
                "button group ⚙ sits in.");
        }

        /// <summary>Reads the Grid.Row value declared on the tag where the named element starts.</summary>
        private static int GridRowOfNamedElement(string xaml, string name)
        {
            int nameIdx = xaml.IndexOf($"x:Name=\"{name}\"", System.StringComparison.Ordinal);
            if (nameIdx < 0) return -1;

            var match = System.Text.RegularExpressions.Regex.Match(
                xaml.Substring(nameIdx, 200), "Grid\\.Row=\"(\\d+)\"");
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }

        /// <summary>
        /// Returns the text of a method's body, located by its signature and closed by brace matching.
        /// Mirrors <c>DebugVisibilityTests.ExtractMethodBody</c>.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, System.StringComparison.Ordinal);
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
