/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native mode chat tab — hosts the transcript in a document tab with its own composer and selectors
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Chat Tab Fields

        private NativeChatToolWindow _nativeChatWindow;

        /// <summary>True while the transcript is parented into the document tab instead of the panel.</summary>
        private bool _chatIsInTab;

        /// <summary>Composer events are wired once for the lifetime of the control.</summary>
        private bool _composerWired;

        /// <summary>Guards against a second switch being started while a relaunch is in flight.</summary>
        private bool _nativeSwitchInProgress;

        #endregion

        #region Chat Tab Hosting

        /// <summary>
        /// Moves the transcript into its own document tab — the default home for native mode, so the
        /// conversation gets the full editor width and can be driven without the panel.
        /// <para>
        /// The transcript instance is never rebuilt: it is re-parented, so the messages already in it
        /// (and the session streaming into it) survive the move in both directions.
        /// </para>
        /// </summary>
        private async Task ShowNativeChatTabAsync(bool focusComposer)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (ChatTranscript == null)
            {
                return;
            }

            try
            {
                var package = await GetPackageAsync();
                if (package == null)
                {
                    Debug.WriteLine("ShowNativeChatTabAsync: could not get the package; the chat stays in the panel.");
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_nativeChatWindow == null)
                {
                    _nativeChatWindow = package.FindToolWindow(typeof(NativeChatToolWindow), 0, true) as NativeChatToolWindow;
                    if (_nativeChatWindow == null)
                    {
                        Debug.WriteLine("ShowNativeChatTabAsync: could not create the chat tab; the chat stays in the panel.");
                        return;
                    }

                    _nativeChatWindow.Closed += OnNativeChatWindowClosed;
                }

                WireChatComposer();

                if (!_chatIsInTab)
                {
                    // A WPF element lives in exactly one visual tree, so it has to leave the panel grid
                    // before the pane can take it.
                    if (TerminalSlotGrid != null && TerminalSlotGrid.Children.Contains(ChatTranscript))
                    {
                        TerminalSlotGrid.Children.Remove(ChatTranscript);
                    }

                    _nativeChatWindow.SetChatContent(ChatTranscript);
                    _chatIsInTab = true;
                }

                ChatTranscript.Visibility = Visibility.Visible;
                ChatTranscript.ShowComposer(true);
                UpdateChatComposerState();
                UpdateChatTabCaption();
                SetPanelTerminalAreaVisible(false);

                var frame = _nativeChatWindow.Frame as IVsWindowFrame;
                frame?.Show();

                UpdateDetachButtonIcon(true);

                if (focusComposer)
                {
                    ChatTranscript.FocusComposer();
                }

                ChatTranscript.ScrollToEndIfFollowing();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening the chat tab: {ex.Message}");
            }
        }

        /// <summary>
        /// Brings the transcript back into the panel slot. Called when the user closes the tab and when
        /// native mode ends — never loses the conversation, only where it is drawn.
        /// </summary>
        private void ReturnNativeChatToPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_chatIsInTab || ChatTranscript == null)
            {
                return;
            }

            try
            {
                _nativeChatWindow?.SetChatContent(null);

                if (TerminalSlotGrid != null && !TerminalSlotGrid.Children.Contains(ChatTranscript))
                {
                    TerminalSlotGrid.Children.Add(ChatTranscript);
                }

                _chatIsInTab = false;

                // Inside the panel the prompt box sits directly above the transcript, so the composer
                // would only be a second input box saying the same thing.
                ChatTranscript.ShowComposer(false);
                ChatTranscript.Visibility = IsNativeModeActive ? Visibility.Visible : Visibility.Collapsed;

                SetPanelTerminalAreaVisible(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error returning the chat to the panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows or hides the panel's terminal group box. While the chat is in its own tab that slot has
        /// nothing to draw, so it is collapsed and its minimum size released — the same treatment a
        /// detached terminal gets, and the prompt box expands into the freed space.
        /// </summary>
        private void SetPanelTerminalAreaVisible(bool visible)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (TerminalGroupBox == null || MainGrid == null)
            {
                return;
            }

            // A detached terminal already collapsed this area for its own reasons; putting it back here
            // would show an empty box.
            if (visible && _isTerminalDetached)
            {
                return;
            }

            TerminalGroupBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            int terminalSlot = (_settings?.InvertLayout == true) ? 0 : 2;

            if (visible)
            {
                RestoreTerminalSlotMinimumSize();
            }
            else if (LayoutGridIsVertical)
            {
                MainGrid.ColumnDefinitions[terminalSlot].MinWidth = 0;
            }
            else
            {
                MainGrid.RowDefinitions[terminalSlot].MinHeight = 0;
            }

            MainGrid.UpdateLayout();
        }

        private void OnNativeChatWindowClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ReturnNativeChatToPanel();

            // The pane is transient: once closed it is gone, so the next open has to build a new one.
            if (_nativeChatWindow != null)
            {
                _nativeChatWindow.Closed -= OnNativeChatWindowClosed;
                _nativeChatWindow = null;
            }

            // The Detach control is the way back to the tab, so it has to flip to "detach" again.
            UpdateDetachButtonIcon(false);
        }

        /// <summary>
        /// Closes the chat tab, if it is open. Used when native mode ends: leaving an empty tab behind
        /// would look like the conversation was lost.
        /// </summary>
        private void CloseNativeChatTab()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Take the reference before closing: CloseFrame fires the Closed handler, which clears the
            // field on its way out.
            NativeChatToolWindow window = _nativeChatWindow;

            ReturnNativeChatToPanel();

            if (window == null)
            {
                return;
            }

            try
            {
                if (window.Frame is IVsWindowFrame frame)
                {
                    frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error closing the chat tab: {ex.Message}");
            }

            window.Closed -= OnNativeChatWindowClosed;
            _nativeChatWindow = null;
        }

        /// <summary>
        /// Moves the chat between its tab and the panel. This is what the Detach control does while
        /// native mode is running, and the way back after the user closes the tab.
        /// </summary>
        private async Task ToggleChatTabAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_chatIsInTab)
            {
                CloseNativeChatTab();
                UpdateDetachButtonIcon(false);
                return;
            }

            await ShowNativeChatTabAsync(focusComposer: true);
        }

        private void UpdateChatTabCaption()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_nativeChatWindow == null)
            {
                return;
            }

            string provider = GetProviderDisplayName(GetActiveOrSelectedProvider());
            _nativeChatWindow.UpdateCaption(string.IsNullOrEmpty(provider) ? "Chat" : provider + " Chat");
        }

        #endregion

        #region Composer Wiring

        private void WireChatComposer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_composerWired || ChatTranscript == null)
            {
                return;
            }

            ChatTranscript.SendRequested += OnComposerSendRequested;
            ChatTranscript.AttachRequested += OnComposerAttachRequested;
            ChatTranscript.FilesDropped += OnComposerFilesDropped;
            ChatTranscript.SelectorClicked += OnComposerSelectorClicked;
            ChatTranscript.EffortChanged += OnComposerEffortChanged;
            ChatTranscript.NewChatRequested += OnComposerNewChatRequested;
            ChatTranscript.ZoomChanged += OnComposerZoomChanged;
            ChatTranscript.ComposerHeightChanged += OnComposerHeightChanged;
            ChatTranscript.PasteRequested += OnComposerPasteRequested;
            ChatTranscript.HistoryPreviousRequested += OnComposerHistoryPreviousRequested;
            ChatTranscript.HistoryNextRequested += OnComposerHistoryNextRequested;
            _composerWired = true;
        }

        /// <summary>
        /// Applies the persisted look of the chat: font, Ctrl+Scroll zoom and composer height. Called
        /// wherever the transcript becomes visible — in the tab and in the panel — so the conversation
        /// always comes back the size the user left it, and again when the settings dialog changes it.
        /// </summary>
        private void ApplyChatAppearance()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || _settings == null)
            {
                return;
            }

            ChatTranscript.SetChatFont(_settings.NativeChatFontFaceName, _settings.NativeChatFontSizePt);
            ChatTranscript.Zoom = _settings.NativeChatZoom;
            ChatTranscript.ComposerHeight = _settings.NativeChatComposerHeight;
        }

        /// <summary>
        /// Ctrl+V in the composer: an image on the clipboard becomes an attachment, exactly as it does
        /// in the panel's prompt box. Anything else falls through to the text box's own paste.
        /// </summary>
        private void OnComposerPasteRequested(object sender, ChatPasteEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (TryPasteImage())
            {
                e.Handled = true;
            }
        }

        private void OnComposerHistoryPreviousRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigateHistoryUp();
        }

        private void OnComposerHistoryNextRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            NavigateHistoryDown();
        }

        /// <summary>Ctrl+Scroll zoom and the composer height are per-user layout, so both are persisted.</summary>
        private void OnComposerZoomChanged(object sender, double zoom)
        {
            if (_settings == null) return;

            _settings.NativeChatZoom = zoom;
            SaveSettings();
        }

        private void OnComposerHeightChanged(object sender, double height)
        {
            if (_settings == null) return;

            _settings.NativeChatComposerHeight = height;
            SaveSettings();
        }

        /// <summary>
        /// Starts a fresh conversation: the agent is relaunched without a resume id, so neither side
        /// keeps the previous history.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerNewChatRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_agentSession == null)
                {
                    return;
                }

                if (MessageBox.Show(
                        "Start a new chat? The current conversation is cleared.",
                        "New Chat",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }

                ChatTranscript.Clear();

                // No resume id is staged, so the relaunch below starts a brand new session.
                await RelaunchNativeSessionAsync("🤖 New chat", forceNewSession: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat new-conversation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes everything the composer shows about the running agent: the four selector captions,
        /// which of them apply, and the send-key preference.
        /// </summary>
        private void UpdateChatComposerState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || !_chatIsInTab)
            {
                return;
            }

            AiProvider? provider = GetActiveOrSelectedProvider();
            bool isClaude = IsClaudeProvider(provider);
            bool isDevin = provider == AiProvider.Devin || provider == AiProvider.DevinNative;

            ChatTranscript.SendWithEnter = _settings?.SendWithEnter != false;
            ChatTranscript.SendWithCtrlEnter = _settings?.SendWithCtrlEnter == true;

            ChatTranscript.SetSelectorLabels(
                GetChatProviderDisplayName(provider),
                GetChatModelLabel(provider),
                GetChatEffortLabel(),
                GetChatPermissionLabel(provider));

            // Model and effort are only meaningful where the extension owns the choice: every other
            // agent picks its model inside its own UI, which native mode has no access to.
            ChatTranscript.SetSelectorAvailability(
                model: isClaude || isDevin,
                effort: isClaude,
                permission: GetChatPermissionLabel(provider) != null);

            ChatTranscript.SetEffortSlider(
                EffortToSliderIndex(_settings != null ? _settings.SelectedEffortLevel : EffortLevel.High),
                GetChatEffortLabel());

            ApplyChatAppearance();
            UpdateComposerAttachmentChips();
        }

        #endregion

        #region Welcome Card

        /// <summary>
        /// CLI versions already measured, keyed by provider. Static so the probe runs once per Visual
        /// Studio session rather than once per new chat; an empty value records "asked and got nothing",
        /// which stops a missing or slow CLI from being probed again on every conversation.
        /// </summary>
        private static readonly ConcurrentDictionary<AiProvider, string> _cliVersions =
            new ConcurrentDictionary<AiProvider, string>();

        /// <summary>
        /// How long a version probe is given before it is killed. It is decoration on a card that is
        /// already on screen, so it may never hold anything up.
        /// </summary>
        private const int CliVersionTimeoutMs = 5000;

        /// <summary>
        /// Shows the card that opens a fresh conversation: which agent is running, with which model,
        /// effort and permissions, against which folder, and what the chat can do. It stands in for the
        /// banner the CLI prints on startup, which native mode never sees — the agent is running
        /// headless and its greeting is not part of the event stream.
        /// </summary>
        /// <param name="workspace">
        /// Folder the agent was started in. Callers that have just resolved it pass it; the last known
        /// one is used otherwise.
        /// </param>
        private void ShowChatWelcome(string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null)
            {
                return;
            }

            AiProvider? provider = GetActiveOrSelectedProvider();
            string directory = string.IsNullOrWhiteSpace(workspace) ? _lastWorkspaceDirectory : workspace;

            ChatTranscript.ShowWelcome(
                BuildWelcomeTitle(provider),
                BuildWelcomeFacts(provider, directory),
                BuildWelcomeTips());

            if (provider.HasValue)
            {
                BeginCliVersionLookup(provider.Value, directory);
            }
        }

        /// <summary>Caption on the card's frame: the agent, and its CLI version once that is known.</summary>
        private string BuildWelcomeTitle(AiProvider? provider)
        {
            string name = GetChatProviderDisplayName(provider);

            string version;
            if (provider.HasValue &&
                _cliVersions.TryGetValue(provider.Value, out version) &&
                !string.IsNullOrEmpty(version))
            {
                return name + " v" + version;
            }

            return name;
        }

        /// <summary>
        /// The lines under the mascot. Only what the extension actually controls is claimed: model and
        /// effort are omitted for the agents that pick those inside their own UI, exactly as the
        /// composer hides those selectors for them.
        /// </summary>
        private List<string> BuildWelcomeFacts(AiProvider? provider, string workspace)
        {
            var facts = new List<string>();

            bool isClaude = IsClaudeProvider(provider);
            bool isDevin = provider == AiProvider.Devin || provider == AiProvider.DevinNative;

            if (isClaude)
            {
                facts.Add(GetChatModelLabel(provider) + " with " + GetChatEffortLabel().ToLowerInvariant() + " effort");
            }
            else if (isDevin)
            {
                facts.Add(GetChatModelLabel(provider));
            }

            string permission = GetChatPermissionLabel(provider);
            if (!string.IsNullOrEmpty(permission))
            {
                facts.Add(permission);
            }

            if (!string.IsNullOrWhiteSpace(workspace))
            {
                facts.Add(workspace);
            }

            return facts;
        }

        /// <summary>
        /// The right-hand column. Deliberately only things the chat tab can actually do — a tip that
        /// does not work is worse than no tip, and the composer has no "@" picker or slash commands.
        /// </summary>
        private static List<string> BuildWelcomeTips()
        {
            return new List<string>
            {
                "Press ↑ in the prompt box to bring back earlier prompts.",
                "Ctrl+V pastes an image from the clipboard; 📎 attaches files.",
                "Ctrl+Scroll zooms the conversation, and the top edge of the prompt box can be dragged.",
                "The buttons below switch agent, model, effort and permissions mid-conversation.",
                "✚ starts a new chat."
            };
        }

        /// <summary>
        /// Asks the CLI for its version in the background and re-renders the card when it answers. Not
        /// awaited by anything: the card is complete without it, and a CLI that is slow to start must
        /// not delay the conversation.
        /// </summary>
        private void BeginCliVersionLookup(AiProvider provider, string workspace)
        {
            if (_cliVersions.ContainsKey(provider))
            {
                // Already measured — BuildWelcomeTitle has it, or it is known to be unavailable.
                return;
            }

            string fileName;
            string arguments;
            string pathOverride;

            if (!TryBuildVersionCommand(provider, out fileName, out arguments, out pathOverride))
            {
                _cliVersions[provider] = string.Empty;
                return;
            }

#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                string version = await Task.Run(() => ReadCliVersion(fileName, arguments, pathOverride))
                    .ConfigureAwait(false);

                _cliVersions[provider] = version ?? string.Empty;

                if (string.IsNullOrEmpty(version))
                {
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Only worth redrawing while the card this probe belongs to is still the one on screen:
                // the user may already have sent a prompt, or switched to another agent.
                if (ChatTranscript != null &&
                    ChatTranscript.IsWelcomeVisible &&
                    GetActiveOrSelectedProvider() == provider)
                {
                    ShowChatWelcome(workspace);
                }
            }).FileAndForget("claudecode/nativemode/cliversion");
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Builds the "--version" command for a provider, resolving the executable exactly the way the
        /// session launch does so the version reported is the one that is actually running.
        /// </summary>
        private bool TryBuildVersionCommand(AiProvider provider, out string fileName, out string arguments,
            out string pathOverride)
        {
            fileName = string.Empty;
            arguments = string.Empty;
            pathOverride = GetFreshPathFromRegistry();

            bool isWsl = provider == AiProvider.ClaudeCodeWSL
                || provider == AiProvider.Codex
                || provider == AiProvider.CursorAgent
                || provider == AiProvider.Devin;

            string executable;

            switch (provider)
            {
                case AiProvider.ClaudeCode:
                    executable = ResolveNativeClaudeExecutable();
                    break;

                case AiProvider.ClaudeCodeWSL:
                    executable = "claude";
                    break;

                case AiProvider.Codex:
                case AiProvider.CodexNative:
                    executable = ResolveNativeProviderExecutable(provider, "codex");
                    break;

                case AiProvider.CursorAgent:
                case AiProvider.CursorAgentNative:
                    executable = ResolveNativeCursorExecutable(provider, isWsl, pathOverride);
                    break;

                case AiProvider.OpenCode:
                case AiProvider.Devin:
                case AiProvider.DevinNative:
                case AiProvider.Reasonix:
                    executable = ResolveNativeProviderExecutable(provider, GetAcpDefaultCommand(provider));
                    break;

                case AiProvider.Pi:
                    executable = ResolveNativeProviderExecutable(provider, "pi");
                    break;

                case AiProvider.Antigravity:
                    executable = ResolveNativeProviderExecutable(provider, "agy");
                    break;

                default:
                    return false;
            }

            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            if (isWsl)
            {
                // A login shell for the same reason the session launch uses one: the CLI is usually
                // installed by a version manager that only puts it on PATH from the profile scripts.
                fileName = "wsl.exe";
                arguments = "bash -lic \"" + executable + " --version\"";
                pathOverride = string.Empty;
                return true;
            }

            fileName = ResolveExecutableOnPath(executable, pathOverride);
            arguments = "--version";

            return !string.IsNullOrWhiteSpace(fileName);
        }

        /// <summary>
        /// Runs the probe and pulls a version number out of whatever it prints. Reads both pipes
        /// asynchronously: a CLI that fills one of them while nobody drains it blocks forever, and this
        /// runs on a pool thread that would then never come back.
        /// </summary>
        private static string ReadCliVersion(string fileName, string arguments, string pathOverride)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrWhiteSpace(pathOverride))
                {
                    startInfo.EnvironmentVariables["PATH"] = pathOverride;
                }

                var output = new StringBuilder();

                using (var process = new Process { StartInfo = startInfo })
                {
                    DataReceivedEventHandler collect = delegate (object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;

                        lock (output)
                        {
                            output.AppendLine(e.Data);
                        }
                    };

                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;

                    if (!process.Start())
                    {
                        return string.Empty;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(CliVersionTimeoutMs))
                    {
                        try
                        {
                            ProcessTree.Kill(process.Id);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Version probe could not be stopped: {ex.Message}");
                        }

                        return string.Empty;
                    }

                    lock (output)
                    {
                        return ExtractVersion(output.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Version probe for '{fileName}' failed: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// First version-shaped token in the output. The CLIs disagree on the format — bare
        /// "2.1.220", "codex-cli 0.5.0", "cursor-agent version 1.2.3" — so the number is picked out
        /// rather than the line being trusted whole.
        /// </summary>
        private static string ExtractVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            Match match = Regex.Match(output, @"\d+\.\d+(\.\d+)*([-+][0-9A-Za-z.\-]+)?");

            return match.Success ? match.Value : string.Empty;
        }

        #endregion

        #region Composer Selectors

        /// <summary>
        /// Provider name for the composer's agent selector, qualified by platform. The compact name is
        /// ambiguous here: the Windows and WSL builds of the same agent are separate entries in the
        /// list, and without the suffix the menu shows the same caption twice.
        /// </summary>
        private string GetChatProviderDisplayName(AiProvider? provider)
        {
            string name = GetProviderDisplayName(provider);

            switch (provider)
            {
                case AiProvider.ClaudeCodeWSL:
                case AiProvider.Codex:
                case AiProvider.CursorAgent:
                case AiProvider.Devin:
                    return name + " (WSL)";

                case AiProvider.CodexNative:
                case AiProvider.CursorAgentNative:
                case AiProvider.DevinNative:
                    return name + " (native)";

                default:
                    return name;
            }
        }

        private string GetChatModelLabel(AiProvider? provider)
        {
            if (provider == AiProvider.Devin || provider == AiProvider.DevinNative)
            {
                return string.IsNullOrWhiteSpace(_settings?.SelectedDevinModel) ? "Model" : _settings.SelectedDevinModel;
            }

            switch (_settings?.SelectedClaudeModel)
            {
                case ClaudeModel.Opus: return "Opus";
                case ClaudeModel.Sonnet: return "Sonnet";
                case ClaudeModel.Haiku: return "Haiku";
                case ClaudeModel.OpusPlan: return "Opus Plan";
                default: return "Best";
            }
        }

        private string GetChatEffortLabel()
        {
            EffortLevel level = _settings != null ? _settings.SelectedEffortLevel : EffortLevel.High;

            switch (level)
            {
                case EffortLevel.XHigh: return "Extra High";
                case EffortLevel.Ultracode: return "Ultracode";
                default: return level.ToString();
            }
        }

        /// <summary>
        /// Caption for the permission selector, or null for agents whose permission handling this
        /// extension does not drive (the button is hidden for those).
        /// </summary>
        private string GetChatPermissionLabel(AiProvider? provider)
        {
            bool? skipping = GetChatPermissionSkipFlag(provider);
            if (!skipping.HasValue)
            {
                return null;
            }

            if (IsClaudeProvider(provider) && _settings?.ClaudePlanMode == true)
            {
                return "Plan mode";
            }

            return skipping.Value ? "Skip permissions" : "Ask permission";
        }

        private bool? GetChatPermissionSkipFlag(AiProvider? provider)
        {
            if (_settings == null)
            {
                return null;
            }

            if (IsClaudeProvider(provider)) return _settings.ClaudeDangerouslySkipPermissions;
            if (IsCodexProvider(provider)) return _settings.CodexFullAuto;
            if (IsCursorAgentProvider(provider)) return _settings.CursorAgentAutoRun;
            if (provider == AiProvider.Antigravity) return _settings.AntigravityDangerouslySkipPermissions;
            if (provider == AiProvider.Devin || provider == AiProvider.DevinNative) return _settings.DevinDangerousMode;

            return null;
        }

        private void SetChatPermissionSkipFlag(AiProvider? provider, bool skip)
        {
            if (_settings == null) return;

            if (IsClaudeProvider(provider)) _settings.ClaudeDangerouslySkipPermissions = skip;
            else if (IsCodexProvider(provider)) _settings.CodexFullAuto = skip;
            else if (IsCursorAgentProvider(provider)) _settings.CursorAgentAutoRun = skip;
            else if (provider == AiProvider.Antigravity) _settings.AntigravityDangerouslySkipPermissions = skip;
            else if (provider == AiProvider.Devin || provider == AiProvider.DevinNative) _settings.DevinDangerousMode = skip;
        }

        #endregion

        #region Composer Send and Attachments

        /// <summary>
        /// Sends what the user typed in the tab. The text is handed to the panel's prompt box and the
        /// existing send path runs untouched, so attachments, prompt history, diff tracking and the
        /// "On Agent Finish" bookkeeping behave exactly as they do from the panel.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnComposerSendRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                string text = ChatTranscript.ComposerText;
                if (string.IsNullOrWhiteSpace(text) && attachedImagePaths.Count == 0)
                {
                    return;
                }

                PromptTextBox.Text = text;
                ChatTranscript.ComposerText = string.Empty;

                SendButton_Click(this, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat composer send failed: {ex.Message}");
            }
        }

        private void OnComposerAttachRequested(object sender, EventArgs e)
        {
            ImageDropBorder_Click(sender, null);
        }

        private void OnComposerFilesDropped(object sender, string[] files)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool any = false;
            foreach (string path in files)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (Directory.Exists(path)) continue;
                if (!File.Exists(path)) continue;
                if (attachedImagePaths.Contains(path)) continue;

                attachedImagePaths.Add(path);
                any = true;
            }

            if (any)
            {
                UpdateImageDropDisplay();
            }
        }

        /// <summary>
        /// Mirrors the panel's attachment chips into the composer. They are separate elements because a
        /// WPF element cannot appear in two visual trees, but both read the same list.
        /// </summary>
        private void UpdateComposerAttachmentChips()
        {
            if (ChatTranscript == null || !_chatIsInTab)
            {
                return;
            }

            Panel host = ChatTranscript.ComposerAttachmentsPanel;
            host.Children.Clear();

            foreach (string path in attachedImagePaths)
            {
                host.Children.Add(CreateAttachmentChip(path));
            }
        }

        #endregion

        #region Composer Selectors

        private void OnComposerSelectorClicked(object sender, ChatSelector selector)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var anchor = sender as Button;
            if (anchor == null)
            {
                return;
            }

            ContextMenu menu;

            // Effort is not here: it opens the pill slider inside the transcript view and reports back
            // through EffortChanged.
            switch (selector)
            {
                case ChatSelector.Provider: menu = BuildChatProviderMenu(); break;
                case ChatSelector.Model: menu = BuildChatModelMenu(); break;
                default: menu = BuildChatPermissionMenu(); break;
            }

            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Builds a themed dropdown for the composer. The menus are rebuilt on every click because
        /// their contents depend on the running agent, which can change between two clicks.
        /// </summary>
        private ContextMenu CreateComposerMenu()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var menu = new ContextMenu
            {
                Background = themeBg,
                Foreground = themeFg
            };

            // Rounded, borderless chrome with no icon gutter: the default menu draws a checkbox column
            // and a square white frame, which look nothing like the rest of the composer.
            var style = ChatTranscript?.TryFindResource("ComposerMenuStyle") as Style;
            if (style != null)
            {
                menu.Style = style;
            }

            return menu;
        }

        private MenuItem AddComposerMenuItem(ContextMenu menu, string header, bool isChecked, RoutedEventHandler onClick)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = isChecked,
                StaysOpenOnClick = false,
                Background = themeBg,
                Foreground = themeFg
            };

            var style = ChatTranscript?.TryFindResource("ComposerMenuItemStyle") as Style;
            if (style != null)
            {
                item.Style = style;
            }

            item.Click += onClick;
            menu.Items.Add(item);

            return item;
        }

        private ContextMenu BuildChatProviderMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            List<AiProvider> visible = _settings?.VisibleProviders;
            if (visible == null || visible.Count == 0)
            {
                visible = new List<AiProvider> { AiProvider.ClaudeCode };
            }

            foreach (AiProvider provider in visible)
            {
                // An agent with no structured channel would drop the user back into the terminal the
                // moment it started, which is not what clicking an entry in the chat tab should do.
                if (!SupportsNativeMode(provider)) continue;

                AiProvider current = provider;
                AddComposerMenuItem(menu, GetChatProviderDisplayName(provider), provider == active,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderSelected(current); });
            }

            return menu;
        }

        private ContextMenu BuildChatModelMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            if (active == AiProvider.Devin || active == AiProvider.DevinNative)
            {
                EnsureDevinModelDefaults();

                if (_settings?.DevinModels != null)
                {
                    foreach (string model in _settings.DevinModels)
                    {
                        if (string.IsNullOrWhiteSpace(model)) continue;

                        string current = model;
                        AddComposerMenuItem(menu, model, model == _settings.SelectedDevinModel,
                            delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatDevinModelSelected(current); });
                    }
                }

                return menu;
            }

            ClaudeModel selected = _settings != null ? _settings.SelectedClaudeModel : ClaudeModel.Best;

            AddComposerMenuItem(menu, "Best", selected == ClaudeModel.Best,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Best); });
            AddComposerMenuItem(menu, "Opus", selected == ClaudeModel.Opus,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Opus); });
            AddComposerMenuItem(menu, "Sonnet", selected == ClaudeModel.Sonnet,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Sonnet); });
            AddComposerMenuItem(menu, "Haiku", selected == ClaudeModel.Haiku,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.Haiku); });
            AddComposerMenuItem(menu, "Opus Plan", selected == ClaudeModel.OpusPlan,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatClaudeModelSelected(ClaudeModel.OpusPlan); });

            return menu;
        }

        private ContextMenu BuildChatPermissionMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            AiProvider? active = GetActiveOrSelectedProvider();

            bool? skipping = GetChatPermissionSkipFlag(active);
            if (!skipping.HasValue)
            {
                return menu;
            }

            // Plan mode is a Claude flag: the agent researches and writes a plan, then asks for approval
            // before touching anything. The other agents have no equivalent launch mode.
            if (IsClaudeProvider(active))
            {
                bool planning = _settings?.ClaudePlanMode == true;

                AddComposerMenuItem(menu, "Plan mode", planning,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPlanModeSelected(true); });
                AddComposerMenuItem(menu, "Ask permission", !planning && !skipping.Value,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPlanModeSelected(false); });
                AddComposerMenuItem(menu, "Skip permissions", !planning && skipping.Value,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(true); });

                return menu;
            }

            AddComposerMenuItem(menu, "Ask permission", !skipping.Value,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(false); });
            AddComposerMenuItem(menu, "Skip permissions", skipping.Value,
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatPermissionSelected(true); });

            return menu;
        }

        #endregion

        #region Live Switching

        /// <summary>
        /// Switching agent restarts the conversation: no two agents share a transcript, so this goes
        /// through the ordinary provider-restart path rather than pretending the history carries over.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatProviderSelected(AiProvider provider)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.SelectedProvider == provider)
                {
                    return;
                }

                _settings.SelectedProvider = provider;
                UpdateProviderSelection();
                SaveSettings();

                await RestartTerminalWithSelectedProviderAsync();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                UpdateChatComposerState();
                UpdateChatTabCaption();
                AddNativeMessage(ChatMessageKind.Notice,
                    $"🤖 Switched to {GetChatProviderDisplayName(provider)} — this starts a new conversation.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat provider switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Switches the Claude model without losing the conversation: the agent is relaunched with the
        /// new model flag and the previous session id, which is the same resume mechanism the adapter
        /// already uses after an interrupt.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatClaudeModelSelected(ClaudeModel model)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.SelectedClaudeModel == model)
                {
                    return;
                }

                _settings.SelectedClaudeModel = model;
                UpdateModelSelection();
                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync($"🤖 Switched to {GetChatModelLabel(GetActiveOrSelectedProvider())}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat model switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Devin's model is chosen inside its own session, and its protocol has no resume, so the new
        /// model is recorded and takes effect the next time the agent starts.
        /// </summary>
        private void OnChatDevinModelSelected(string model)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null || string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            _settings.SelectedDevinModel = model;
            UpdateModelSelection();
            SaveSettings();
            UpdateChatComposerState();

            AddNativeMessage(ChatMessageKind.Notice, $"🤖 Model set to {model} — it applies the next time the agent starts.");
        }

        /// <summary>
        /// The composer's effort pill settled on a new stop. Same slider order as the panel, so both
        /// controls always agree on what "High" means.
        /// </summary>
        private void OnComposerEffortChanged(object sender, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OnChatEffortSelected(EffortFromSliderIndex(index));
        }

        /// <summary>
        /// Switches the effort level. Like the model, it is a launch flag (<c>--effort</c>), so the
        /// session is relaunched and resumed rather than being sent the <c>/effort</c> command.
        /// <para>
        /// The command was the original approach and had two defects: it only holds "for this session
        /// only", so the level was silently lost on every other relaunch while the composer still showed
        /// it, and writing to a live session while the CLI was shutting down after an interrupt made the
        /// expected exit look like a crash.
        /// </para>
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatEffortSelected(EffortLevel level)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.SelectedEffortLevel == level)
                {
                    return;
                }

                _settings.SelectedEffortLevel = level;
                if (!IsSessionOnlyEffort(level))
                {
                    _lastPersistableEffortLevel = level;
                }

                UpdateEffortSelection();
                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync($"🤖 Effort switched to {GetChatEffortLabel()}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat effort switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Permission handling is a launch flag for every agent that has one, so the switch relaunches
        /// the session — resuming it where the protocol allows, so the conversation is not lost.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPermissionSelected(bool skip)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                AiProvider? provider = GetActiveOrSelectedProvider();
                bool? current = GetChatPermissionSkipFlag(provider);
                if (!current.HasValue || current.Value == skip)
                {
                    return;
                }

                SetChatPermissionSkipFlag(provider, skip);

                // Skipping every prompt and planning before acting are opposites; picking one drops
                // the other rather than launching with a contradictory pair of flags.
                if (skip && _settings != null)
                {
                    _settings.ClaudePlanMode = false;
                }

                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync(skip
                    ? "🤖 Switched to skipping permission prompts"
                    : "🤖 Switched to asking for permission");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat permission switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Turns plan mode on or off. It is a launch flag, so the session is relaunched — resumed, so
        /// the conversation so far still counts as the plan's input.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatPlanModeSelected(bool planning)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (_settings == null || _settings.ClaudePlanMode == planning)
                {
                    return;
                }

                _settings.ClaudePlanMode = planning;

                if (planning)
                {
                    // Plan mode is the CLI asking before it acts, which "skip permissions" would
                    // bypass entirely.
                    _settings.ClaudeDangerouslySkipPermissions = false;
                }

                SaveSettings();
                UpdateChatComposerState();

                await RelaunchNativeSessionAsync(planning
                    ? "🤖 Plan mode on — the agent will propose a plan before making changes"
                    : "🤖 Plan mode off");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat plan-mode switch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restarts the agent process with the current settings and drops an inline notice into the
        /// transcript, which is deliberately not cleared: the conversation reads as one continuous
        /// thread with a divider where the switch happened.
        /// <para>
        /// Claude is relaunched with <c>--resume &lt;session id&gt;</c>, so the agent keeps the history
        /// too. The other adapters have no equivalent, so there the notice says so.
        /// </para>
        /// </summary>
        private async Task RelaunchNativeSessionAsync(string notice, bool forceNewSession = false)
        {
            if (_nativeSwitchInProgress)
            {
                return;
            }

            IAgentSession previous = _agentSession;
            if (previous == null)
            {
                // Native mode is not running (the panel is on the terminal) — nothing to relaunch.
                return;
            }

            _nativeSwitchInProgress = true;

            try
            {
                AiProvider provider = _currentRunningProvider ?? _settings.SelectedProvider;
                // Not SessionId: after a relaunch that never ran a turn, that is still the throwaway
                // id the adapter *asked* for and the CLI never created a transcript for. Resuming it
                // fails the launch, which is what made two consecutive dropdown changes kill the agent.
                string resumeId = previous.ResumableSessionId;
                bool providerCanResume = IsClaudeProvider(provider);
                bool canResume = !forceNewSession && providerCanResume && !string.IsNullOrEmpty(resumeId);

                string workspace = await GetWorkspaceDirectoryAsync();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                ChatTranscript.SetBusy(false);
                ChatTranscript.SetStatus("Restarting the agent...");

                DisposeNativeSession();

                if (canResume)
                {
                    // CreateClaudeSession consumes this exactly like a resume request coming from the
                    // Session History window.
                    Interlocked.Exchange(ref _pendingResumeSessionId, resumeId);
                }

                IAgentSession session = CreateAgentSession(provider, workspace);
                if (session == null)
                {
                    AddNativeMessage(ChatMessageKind.Error, "The agent could not be restarted.");
                    ChatTranscript.SetStatus(string.Empty);
                    return;
                }

                _nativeSessionCts = new CancellationTokenSource();
                _agentSession = session;
                session.Received += OnAgentEventReceived;

                _pendingToolCalls.Clear();
                _streamingAssistantMessage = null;
                _streamingThinkingMessage = null;
                _nativeTurnFinishConfig = null;
                _nativeTurnInFlight = false;

                await session.StartAsync(workspace, _nativeSessionCts.Token);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _currentRunningProvider = provider;
                ChatTranscript.SetStatus("Ready.");

                // No caveat when the provider can resume but there is simply nothing to resume yet
                // (no turn has run), because in that case no history is being lost.
                AddNativeMessage(ChatMessageKind.Notice, canResume || forceNewSession || providerCanResume
                    ? notice
                    : notice + " — this agent cannot resume, so the conversation starts over.");

                UpdateChatComposerState();
                UpdateChatTabCaption();

                // Only for "New chat". A model or permission switch keeps the conversation, and opening
                // a fresh-start card in the middle of one would read as if it had been thrown away.
                if (forceNewSession)
                {
                    ShowChatWelcome(workspace);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: relaunch failed: {ex}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessage(ChatMessageKind.Error, $"The agent could not be restarted: {ex.Message}");
                ChatTranscript.SetStatus(string.Empty);
            }
            finally
            {
                _nativeSwitchInProgress = false;
            }
        }

        #endregion
    }
}
