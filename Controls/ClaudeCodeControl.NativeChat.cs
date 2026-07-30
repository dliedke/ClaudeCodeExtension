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
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
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
            ChatTranscript.ComposerPreviewKeyDown += ComposerInput_AtMentionPreviewKeyDown;
            ChatTranscript.ComposerInputBox.TextChanged += ComposerInput_AtMentionTextChanged;
            ChatTranscript.LinkClicked += OnChatLinkClicked;
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

            // Effort stays Claude-only; the model selector now covers every agent, listing either
            // the models its CLI reports or a list configured in the settings.
            ChatTranscript.SetSelectorAvailability(
                model: isClaude || ProviderHasModelCatalog(provider),
                effort: isClaude,
                permission: GetChatPermissionLabel(provider) != null);

            ChatTranscript.SetEffortStopLabels(GetChatEffortStopLabels());
            ChatTranscript.SetEffortSlider(
                EffortToSliderIndex(_settings != null ? _settings.SelectedEffortLevel : EffortLevel.High),
                GetChatEffortLabel());

            ApplyChatAppearance();
            UpdateComposerAttachmentChips();
        }

        #endregion

        #region Markdown Link Navigation

        /// <summary>Splits a trailing <c>:line</c> or <c>:line-line2</c> off a file reference, e.g. one an agent cites for its own diff.</summary>
        private static readonly Regex ChatFileLineRefPattern = new Regex(@"^(?<path>.+):(?<line1>\d+)(?:-(?<line2>\d+))?$", RegexOptions.Compiled);

        /// <summary>
        /// A link clicked in the rendered transcript: a web address opens in the default browser,
        /// anything else is treated as a file reference (optionally with a trailing <c>:line</c>) and
        /// opened in the editor at that line.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatLinkClicked(object sender, string url)
#pragma warning restore VSTHRD100
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                if (Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return;
                }

                await OpenChatFileLinkAsync(url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Chat link click failed for '{url}': {ex.Message}");
            }
        }

        private async Task OpenChatFileLinkAsync(string reference)
        {
            string path = reference.Trim();
            int line = 0;

            Match match = ChatFileLineRefPattern.Match(path);
            if (match.Success)
            {
                path = match.Groups["path"].Value;
                int.TryParse(match.Groups["line1"].Value, out line);
            }

            string resolved = await ResolveChatFileLinkAsync(path);
            if (resolved == null)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte == null)
            {
                return;
            }

            dte.ItemOperations.OpenFile(resolved);

            if (line > 0 && dte.ActiveDocument != null)
            {
                var selection = dte.ActiveDocument.Selection as EnvDTE.TextSelection;
                selection?.GotoLine(line, false);
            }
        }

        /// <summary>
        /// Resolves a path an agent cited to an actual file: as typed, relative to the workspace root,
        /// or — since agents often abbreviate to a bare filename or a repo-relative form that does not
        /// match where the extension resolved the workspace — by falling back to the same index the "@"
        /// mention picker builds and matching on the trailing path segments.
        /// </summary>
        private async Task<string> ResolveChatFileLinkAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string candidate = path.Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }

            string workspace = await GetWorkspaceDirectoryAsync();
            if (string.IsNullOrEmpty(workspace))
            {
                return null;
            }

            string direct = Path.Combine(workspace, candidate);
            if (File.Exists(direct))
            {
                return direct;
            }

            await EnsureAtEntriesAsync();
            if (_atEntries == null)
            {
                return null;
            }

            string normalizedTarget = path.Replace('\\', '/').TrimStart('/');
            string entry = _atEntries.FirstOrDefault(e =>
                !e.EndsWith("/", StringComparison.Ordinal) &&
                (string.Equals(e, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                 e.EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileName(e), normalizedTarget, StringComparison.OrdinalIgnoreCase)));

            return entry != null ? Path.Combine(workspace, entry.Replace('/', Path.DirectorySeparatorChar)) : null;
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
                BuildWelcomeTips(provider));

            if (provider.HasValue)
            {
                BeginCliVersionLookup(provider.Value, directory);
            }
        }

        /// <summary>
        /// Most rows a replayed transcript may add. A long conversation would otherwise take seconds to
        /// render and bury the composer under thousands of bubbles; the newest ones are the ones worth
        /// showing, so the cap keeps the tail and says how much it dropped.
        /// </summary>
        private const int ResumedTranscriptMaxRows = 200;

        /// <summary>
        /// Replays a resumed conversation into the chat. The CLI is the reason this exists: measured,
        /// <c>--resume</c> in print mode restores the agent's memory in full but emits nothing of the
        /// history — only <c>system/init</c> and then the new turn. So the agent remembered everything
        /// while the chat opened blank, which is what "session history doesn't load" looked like.
        /// The transcript on disk is the same JSONL the Session History dialog already reads.
        /// </summary>
        /// <param name="sessionId">
        /// The id the user asked to resume, or null/"-c" for anything else. "-c" is the Session History
        /// window's "continue last session", which native mode has no equivalent for and ignores.
        /// </param>
        /// <returns>False when there is nothing to replay, and the caller shows the welcome card.</returns>
        private async Task<bool> TryReplayResumedTranscriptAsync(AiProvider provider, string workspace, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId == "-c" || ChatTranscript == null)
            {
                return false;
            }

            try
            {
                string directory = await ResolveSessionDirectoryAsync(provider, workspace);
                if (string.IsNullOrEmpty(directory))
                {
                    return false;
                }

                string path = Path.Combine(directory, sessionId + ".jsonl");
                if (!File.Exists(path))
                {
                    Debug.WriteLine($"Native mode: no transcript to replay at {path}");
                    return false;
                }

                // Parsed off the UI thread — a long session is megabytes of JSONL — and returned as
                // plain data, because a WPF view model may only be built on the thread that owns it.
                int total = 0;
                List<KeyValuePair<ChatMessageKind, string>> rows =
                    await Task.Run(() => ReadResumedTranscript(path, out total));

                if (rows == null || rows.Count == 0)
                {
                    return false;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                int dropped = total - rows.Count;
                AddNativeMessage(ChatMessageKind.Notice, dropped > 0
                    ? $"🕘 Resumed this conversation — showing the last {rows.Count} of {total} messages."
                    : $"🕘 Resumed this conversation — {rows.Count} earlier messages restored.");

                foreach (KeyValuePair<ChatMessageKind, string> row in rows)
                {
                    AddNativeMessage(row.Key, row.Value);
                }

                ChatTranscript.ScrollToEndIfFollowing();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: replaying the resumed transcript failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads a session's JSONL into chat rows, newest-last, capped at
        /// <see cref="ResumedTranscriptMaxRows"/>. <paramref name="total"/> reports how many rows the
        /// file held before the cap, so the caller can say what it left out. Shares the extraction
        /// helpers with the Session History dialog, so both read a transcript the same way — including
        /// skipping the tool-result rows the CLI re-injects as "user" messages.
        /// </summary>
        private static List<KeyValuePair<ChatMessageKind, string>> ReadResumedTranscript(string path, out int total)
        {
            var rows = new List<KeyValuePair<ChatMessageKind, string>>();
            total = 0;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JObject obj;
                    try { obj = JObject.Parse(line); }
                    catch { continue; }

                    string type = (string)obj["type"];
                    if (type != "user" && type != "assistant") continue;

                    JToken message = obj["message"];
                    if (message == null) continue;

                    bool isUser = type == "user";
                    string text = isUser
                        ? ExtractUserText(message["content"])
                        : ExtractAssistantText(message["content"]);

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    total++;
                    rows.Add(new KeyValuePair<ChatMessageKind, string>(
                        isUser ? ChatMessageKind.User : ChatMessageKind.Assistant, text.TrimEnd()));

                    // Trimming as we go keeps a very long transcript from being held in full.
                    if (rows.Count > ResumedTranscriptMaxRows)
                    {
                        rows.RemoveAt(0);
                    }
                }
            }

            return rows;
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
        /// Reads the signed-in Claude account from the CLI's own state file (<c>~/.claude.json</c>,
        /// key <c>oauthAccount</c>) — the same place the CLI itself keeps it. Native mode has no other
        /// source for this: the headless <c>system/init</c> event carries session/model info only, no
        /// account or email field. Best-effort — the file's shape is undocumented and CLI-owned, so any
        /// read/parse failure (including a concurrent CLI write) just means no label is shown.
        /// </summary>
        private static string GetSignedInClaudeAccountLabel()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }

                JToken account = JObject.Parse(json)["oauthAccount"];
                if (account == null)
                {
                    return null;
                }

                string email = (string)account["emailAddress"];
                string displayName = (string)account["displayName"];

                if (string.IsNullOrWhiteSpace(email))
                {
                    return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
                }

                return string.IsNullOrWhiteSpace(displayName) ? email : $"{displayName} ({email})";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSignedInClaudeAccountLabel failed: {ex.Message}");
                return null;
            }
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

                string account = GetSignedInClaudeAccountLabel();
                if (!string.IsNullOrEmpty(account))
                {
                    facts.Add("Signed in as " + account);
                }
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
        /// does not work is worse than no tip, so the composer's "@" picker is not mentioned (there
        /// isn't one) and the slash-command tip appears only where those commands are handled.
        /// </summary>
        private static List<string> BuildWelcomeTips(AiProvider? provider)
        {
            var tips = new List<string>
            {
                "Press Ctrl+Up/Down in the prompt box for prompt history.",
                "Ctrl+V pastes an image from the clipboard; 📎 attaches files.",
                "Ctrl+Scroll zooms the conversation, and the top edge of the prompt box can be dragged.",
                "The buttons below switch agent, model, effort and permissions mid-conversation."
            };

            if (IsClaudeProvider(provider))
            {
                tips.Add("Type /plan, /model or /effort to switch without leaving the prompt box.");
            }

            tips.Add("✚ starts a new chat.");

            return tips;
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
            if (!IsClaudeProvider(provider) && ProviderHasModelCatalog(provider))
            {
                string label = GetSelectedProviderModelLabel(provider);

                // Nothing chosen: the agent runs on its own default, which the extension cannot name.
                return string.IsNullOrWhiteSpace(label) ? "Model" : label;
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
            return GetChatEffortLabel(_settings != null ? _settings.SelectedEffortLevel : EffortLevel.High);
        }

        private static string GetChatEffortLabel(EffortLevel level)
        {
            switch (level)
            {
                case EffortLevel.XHigh: return "Extra High";
                case EffortLevel.Ultracode: return "Ultracode";
                default: return level.ToString();
            }
        }

        /// <summary>
        /// Captions of the effort slider stops, in slider order, so the popup can name the level under
        /// the thumb before it is applied.
        /// </summary>
        private static string[] GetChatEffortStopLabels()
        {
            return Array.ConvertAll(_effortSliderOrder, GetChatEffortLabel);
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
                ChatTranscript.ComposerAttachmentsPanel.Children.Clear();

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

        /// <summary>
        /// Adds one entry to a composer dropdown. The parent is an <see cref="ItemsControl"/> rather
        /// than the menu itself so the same themed entry can go into a submenu (see
        /// <see cref="AddComposerSubmenu"/>), which is how a long model list is broken up.
        /// </summary>
        private MenuItem AddComposerMenuItem(ItemsControl menu, string header, bool isChecked, RoutedEventHandler onClick)
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

        /// <summary>
        /// Adds a submenu header to a composer dropdown and returns it, so entries can be added to it
        /// with <see cref="AddComposerMenuItem"/>.
        /// </summary>
        private MenuItem AddComposerSubmenu(ItemsControl menu, string header)
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = false,
                Background = themeBg,
                Foreground = themeFg
            };

            var style = ChatTranscript?.TryFindResource("ComposerSubmenuStyle") as Style;
            if (style != null)
            {
                item.Style = style;
            }

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

            if (active != null && !IsClaudeProvider(active) && ProviderHasModelCatalog(active))
            {
                FillChatModelMenu(menu, active.Value);

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

        /// <summary>
        /// Fills the composer's model dropdown with the agent's own models, plus an entry that leaves
        /// the model to the agent. A stale or missing list is re-read from the CLI in the background
        /// and dropped into the same menu when it arrives, so the dropdown opens instantly either way.
        /// </summary>
        private void FillChatModelMenu(ContextMenu menu, AiProvider provider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string selected = GetSelectedProviderModelId(provider);
            List<Agents.ModelGroup> groups =
                Agents.ModelCatalogGrouping.Group(GetCachedProviderModels(provider));

            // Repeated at the top when the list is grouped: inside a submenu the checked entry is
            // invisible until the right one is opened.
            if (groups.Exists(g => g.IsSubmenu) && !string.IsNullOrWhiteSpace(selected))
            {
                AddChatModelMenuItem(menu, new Agents.ModelOption
                {
                    Id = selected,
                    Name = GetSelectedProviderModelLabel(provider)
                }, selected);
            }

            foreach (Agents.ModelGroup group in groups)
            {
                ItemsControl parent = group.IsSubmenu ? AddComposerSubmenu(menu, group.Name) : (ItemsControl)menu;

                foreach (Agents.ModelOption model in group.Models)
                {
                    AddChatModelMenuItem(parent, model, selected);
                }
            }

            AddComposerMenuItem(menu, "Agent default", string.IsNullOrWhiteSpace(selected),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelected(string.Empty); });

            if (!ShouldRefreshProviderModels(provider)) return;

            MenuItem loading = AddComposerMenuItem(menu, "Loading models…", false, delegate { });
            loading.IsEnabled = false;

            _ = RefreshProviderModelsAsync(provider).ContinueWith(
                delegate
                {
                    ThreadHelper.ThrowIfNotOnUIThread();

                    // Only worth doing while the dropdown is still on screen; otherwise the next
                    // click rebuilds it from the cache that was just filled.
                    if (!menu.IsOpen || GetActiveOrSelectedProvider() != provider) return;

                    menu.Items.Clear();
                    FillChatModelMenu(menu, provider);
                },
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void AddChatModelMenuItem(ItemsControl parent, Agents.ModelOption model, string selected)
        {
            string current = model.Id;

            AddComposerMenuItem(parent, model.DisplayName,
                string.Equals(current, selected, StringComparison.OrdinalIgnoreCase),
                delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatProviderModelSelected(current); });
        }

        /// <summary>
        /// The effort levels as a dropdown, for the typed <c>/effort</c> command. The button opens the
        /// pill slider instead, which is anchored to that button — with the transcript back in the
        /// panel there is no button and nothing to anchor to, and a menu reads the same from either
        /// host.
        /// </summary>
        private ContextMenu BuildChatEffortMenu()
        {
            ContextMenu menu = CreateComposerMenu();
            EffortLevel selected = _settings != null ? _settings.SelectedEffortLevel : EffortLevel.High;

            foreach (EffortLevel level in _effortSliderOrder)
            {
                EffortLevel current = level;
                AddComposerMenuItem(menu, GetChatEffortLabel(level), level == selected,
                    delegate { ThreadHelper.ThrowIfNotOnUIThread(); OnChatEffortSelected(current); });
            }

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

        #region Typed Selector Commands

        /// <summary>
        /// Handles the commands a user can type instead of clicking: <c>/plan</c> turns plan mode on,
        /// <c>/model</c> and <c>/effort</c> open the pickers the composer buttons open, and
        /// <c>/btw</c> asks a side question.
        /// <para>
        /// They are the extension's own commands, not the CLI's: native mode runs the agent headless,
        /// where these settings are launch flags rather than anything the agent can be told mid-turn.
        /// Sending them on as a prompt is what happened before, and the agent simply answered as if
        /// asked about the word.
        /// </para>
        /// </summary>
        /// <returns>True when the text was a command and was handled, so nothing is sent.</returns>
        private bool TryHandleNativeSelectorCommand(string prompt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return false;
            }

            // Claude-only, exactly like the composer's own model, effort and plan-mode selectors: the
            // other agents pick their model inside a TUI native mode cannot reach, and none of them
            // has an effort level or a plan mode.
            if (!IsClaudeProvider(GetActiveOrSelectedProvider()))
            {
                return false;
            }

            string trimmed = prompt.Trim();

            // "/btw <question>" is the one that takes an argument, so it is matched by prefix. The bare
            // "/btw" falls through to the usage line rather than asking an empty question.
            if (trimmed.StartsWith("/btw", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 4 || char.IsWhiteSpace(trimmed[4])))
            {
                string question = trimmed.Substring(4).Trim();
                if (question.Length == 0)
                {
                    AddNativeMessage(ChatMessageKind.Notice, "Usage: /btw <your question>");
                    return true;
                }

                AddNativeMessage(ChatMessageKind.User, trimmed);
                AddNativeMessage(ChatMessageKind.Notice,
                    "💬 Side question — asked in a forked copy of this conversation, so the work in progress is untouched.");

                _ = AskSideQuestionAsync(question);
                return true;
            }

            // Only the bare command. "/plan the migration" is a prompt about planning, and taking it
            // as a command would silently swallow it.
            switch (trimmed.ToLowerInvariant())
            {
                case "/plan":
                    if (_settings?.ClaudePlanMode == true)
                    {
                        AddNativeMessage(ChatMessageKind.Notice, "🤖 Already in plan mode.");
                    }
                    else
                    {
                        OnChatPlanModeSelected(true);
                    }
                    return true;

                case "/model":
                    ShowChatSelectorMenu(BuildChatModelMenu(), ChatSelector.Model);
                    return true;

                case "/effort":
                    ShowChatSelectorMenu(BuildChatEffortMenu(), ChatSelector.Effort);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Opens a composer menu for a typed command. It hangs off the matching composer button when the
        /// chat is in its tab; with the transcript back in the panel there is no composer, so it falls
        /// back to the panel's send button — next to the prompt box the command was typed into.
        /// </summary>
        private void ShowChatSelectorMenu(ContextMenu menu, ChatSelector selector)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            UIElement anchor = ChatTranscript?.GetSelectorAnchor(selector)
                ?? (UIElement)SendPromptButton
                ?? ChatTranscript;

            if (anchor == null)
            {
                return;
            }

            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
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

                // Refresh again now that the new session is actually running: the call above ran
                // before the restart, so it still read the outgoing provider off
                // _currentRunningProvider and left the tool window caption on the old name.
                UpdateProviderSelection();
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
        /// A model picked for any non-Claude agent. An ACP session that publishes a model picker
        /// (Devin, Open Code) takes the change over the protocol without losing the conversation;
        /// every other agent reads its model at launch, so the user is offered a restart.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatProviderModelSelected(string model)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            AiProvider? provider = GetActiveOrSelectedProvider();
            if (_settings == null || provider == null)
            {
                return;
            }

            SetSelectedProviderModelId(provider.Value, model);
            UpdateModelSelection();
            SaveSettings();
            UpdateChatComposerState();

            string label = string.IsNullOrWhiteSpace(model)
                ? "the agent's default"
                : GetSelectedProviderModelLabel(provider);

            // "Agent default" cannot be requested over the protocol — there is no such option to
            // select — so it always goes through the relaunch below.
            if (!string.IsNullOrWhiteSpace(model) && await TrySwitchAcpModelAsync(model))
            {
                AddNativeMessage(ChatMessageKind.Notice, $"🤖 Model switched to {label}.");
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Switch the model to {label} and restart the chat so the change takes effect now?",
                $"Switch {GetChatProviderDisplayName(provider)} model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await RelaunchNativeSessionAsync($"🤖 Model switched to {label}", forceNewSession: true);
            }
            else
            {
                AddNativeMessage(ChatMessageKind.Notice, $"🤖 Model set to {label} — it applies the next time the agent starts.");
            }
        }

        /// <summary>
        /// Asks a live ACP session to change its model. False when there is no such session running or
        /// the agent would not take the model, and the caller then offers the restart instead.
        /// </summary>
        private async Task<bool> TrySwitchAcpModelAsync(string model)
        {
            var session = _agentSession as AcpSession;
            if (session == null)
            {
                return false;
            }

            try
            {
                return await session.SetModelAsync(model, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native chat: switching the model on the live session failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The composer's effort popup closed on a stop other than the one it opened on. Same slider
        /// order as the panel, so both controls always agree on what "High" means.
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
        /// <param name="appendAccountLabel">
        /// When true, the signed-in account (<see cref="GetSignedInClaudeAccountLabel"/>) is appended to
        /// <paramref name="notice"/> once the relaunched process is up — read at that point, not before,
        /// so an account switch has the best chance of the CLI's state file already reflecting it.
        /// </param>
        private async Task RelaunchNativeSessionAsync(string notice, bool forceNewSession = false, bool appendAccountLabel = false)
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

                if (appendAccountLabel)
                {
                    string account = GetSignedInClaudeAccountLabel();
                    if (!string.IsNullOrEmpty(account))
                    {
                        notice += " — now signed in as " + account;
                    }
                }

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

        /// <summary>
        /// Handles the ⚙ menu's "Change Account" item in native mode. There is no console here to run
        /// <c>/logout</c> against — the terminal-mode equivalent (<c>ChangeAccountMenuItem_Click</c>)
        /// scripts that as keystrokes into the embedded window — so this signs the usage WebView2 out,
        /// opens claude.ai in the default browser for the user to switch accounts, and once they
        /// confirm, relaunches the agent so the new turn picks up the refreshed credentials.
        /// </summary>
#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void ChangeAccountNativeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!IsNativeModeActive || !IsClaudeProvider(GetActiveOrSelectedProvider()))
                {
                    return;
                }

                // Sign out the embedded usage WebView2 so the new account is picked up there too.
                await SignOutUsageWindowIfActiveAsync();

                try
                {
                    Process.Start(new ProcessStartInfo("https://claude.ai") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ChangeAccountNativeMenuItem_Click: could not open browser: {ex.Message}");
                }

                MessageBox.Show(
                    "Please switch to the desired account in your browser, then click OK to resume Claude Code.",
                    "Change Account",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await RelaunchNativeSessionAsync("🔑 Switched account — resuming", appendAccountLabel: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Change account (native) failed: {ex.Message}");
            }
        }

        #endregion
    }
}
