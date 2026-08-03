/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Native mode — drives an IAgentSession and renders it in the chat transcript instead of the terminal
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Native Sessions Management (Parallel Sessions Infrastructure)

        // Dictionary to hold multiple concurrent sessions (future: currently single-session mode)
        private Dictionary<string, NativeChatSessionState> _nativeSessions =
            new Dictionary<string, NativeChatSessionState>(StringComparer.Ordinal);
        private string _activeSessionId;
        private readonly object _sessionLock = new object();
        private int _sessionIdCounter = 0;

        // Helper: get currently active session (or null if none)
        private NativeChatSessionState GetActiveSession()
        {
            if (string.IsNullOrEmpty(_activeSessionId))
                return null;
            lock (_sessionLock)
            {
                _nativeSessions.TryGetValue(_activeSessionId, out var session);
                return session;
            }
        }

        // Helper: get session by ID (internal access for NativeChat.cs)
        internal NativeChatSessionState GetSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;
            lock (_sessionLock)
            {
                _nativeSessions.TryGetValue(sessionId, out var session);
                return session;
            }
        }

        // Helper: remove session (internal access for NativeChat.cs)
        internal void RemoveSession(string sessionId)
        {
            lock (_sessionLock)
            {
                if (_nativeSessions.TryGetValue(sessionId, out var state))
                {
                    state.Dispose();
                    _nativeSessions.Remove(sessionId);
                }

                if (_activeSessionId == sessionId)
                    _activeSessionId = null;
            }
        }

        #endregion

        #region Native Mode Fields

        private IAgentSession _agentSession;

        /// <summary>
        /// The row currently receiving streamed assistant text, so chunks append instead of creating a
        /// new bubble per token. Null between turns.
        /// </summary>
        private ChatMessageViewModel _streamingAssistantMessage;

        /// <summary>Same idea for the extended-thinking block of the turn in flight.</summary>
        private ChatMessageViewModel _streamingThinkingMessage;

        /// <summary>Tool rows awaiting their result, keyed by the CLI's tool-use id.</summary>
        private readonly Dictionary<string, ChatMessageViewModel> _pendingToolCalls =
            new Dictionary<string, ChatMessageViewModel>(StringComparer.Ordinal);

        private CancellationTokenSource _nativeSessionCts;

        /// <summary>When the turn in flight was submitted, so the finish notification can report its length.</summary>
        private DateTime _nativeTurnStartedUtc;

        /// <summary>
        /// The "On Agent Finish" configuration captured when the turn was submitted. Resolving it up
        /// front (instead of at completion time) matches the terminal path, where the watcher is armed
        /// at send time, and keeps a mid-turn settings change from retargeting the action.
        /// </summary>
        private AgentFinishConfig _nativeTurnFinishConfig;

        /// <summary>Last throttling notice shown, so the repeated rate-limit events don't stack up.</summary>
        private string _lastNativeRateLimitNotice;

        /// <summary>
        /// The launch options of the running Claude session, kept so a side question (<c>/btw</c>) can
        /// be asked with the same executable, distro, folder and model. Null for every other agent and
        /// between sessions.
        /// </summary>
        private ClaudeSessionOptions _nativeClaudeOptions;

        /// <summary>
        /// Running totals for the turn in flight, fed by the mid-turn usage events so the status line
        /// counts up instead of staying at zero until the final result arrives.
        /// <para>
        /// Output tokens accumulate across the turn's requests; the input count is that of the most
        /// recent request, because each one re-sends the whole context and adding them up would report
        /// a number several times larger than anything that was actually billed.
        /// </para>
        /// </summary>
        private int _nativeTurnOutputTokens;
        private int _nativeTurnInputTokens;

        /// <summary>True between the prompt being sent and the turn's end, so a stray end-of-turn event
        /// (the one-shot adapters emit one on relaunch) cannot post a second summary row.</summary>
        private bool _nativeTurnInFlight;

        /// <summary>
        /// Follow-up prompts accepted while Codex is running in native chat mode. Both its Windows and
        /// WSL headless protocols launch one process per turn, so messages cannot be injected into that
        /// process; they are resumed on the same thread, in order, as soon as it has fully exited.
        /// </summary>
        private readonly Queue<string> _codexNativePromptQueue = new Queue<string>();

        /// <summary>The session whose send loop currently owns <see cref="_codexNativePromptQueue"/>.</summary>
        private IAgentSession _codexNativeQueueOwner;

        /// <summary>
        /// Completed by the UI event bridge after the current Codex turn is fully rendered. Waiting for
        /// this prevents the next turn's activity indicator from being cleared by the previous turn's
        /// asynchronously marshalled completion event.
        /// </summary>
        private TaskCompletionSource<bool> _codexNativeTurnRendered;

        /// <summary>Queued prompts discarded by Stop, reported beside the interrupted-turn notice.</summary>
        private int _cancelledCodexNativePromptCount;

        #endregion

        #region Native Mode State

        /// <summary>True when the chat transcript — not the embedded terminal — is driving the panel.</summary>
        private bool IsNativeModeActive
        {
            get { return _agentSession != null; }
        }

        /// <summary>
        /// True when there is an agent to hand work to at all: an embedded console window, or a native
        /// session. Anything that sends the agent something unprompted — build errors, runtime errors —
        /// must ask this instead of testing <c>terminalHandle</c> directly, because native mode has no
        /// window and a raw handle test reads as "no agent is running" for the entire mode.
        /// </summary>
        private bool IsAgentAvailable
        {
            get { return IsNativeModeActive || (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle)); }
        }

        /// <summary>
        /// Whether this provider uses the Codex native-chat queue. Kept separate from the Windows
        /// provider name: Codex (WSL) uses the same one-shot native adapter and must behave identically.
        /// </summary>
        internal static bool SupportsQueuedCodexNativeChat(AiProvider? provider)
        {
            return provider == AiProvider.Codex || provider == AiProvider.CodexNative;
        }

        /// <summary>
        /// Whether a provider has a structured channel this build can drive. Providers that do not are
        /// silently launched in the embedded terminal, so turning the setting on can never leave a user
        /// with a panel that does nothing.
        /// </summary>
        private static bool SupportsNativeMode(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.ClaudeCode:
                case AiProvider.ClaudeCodeWSL:
                // These three speak ACP, so one adapter drives all of them. Reasonix is deliberately
                // **not** here: its ACP adapter works (it handshakes and answers), but it is kept on
                // the embedded terminal by product decision, so nothing in this table should be read
                // as a statement about which agents *could* run natively.
                case AiProvider.OpenCode:
                case AiProvider.Devin:
                case AiProvider.DevinNative:
                // These four stream JSON but end the process with each turn, so the adapter relaunches
                // them with a resume flag. The conversation survives; the prompt cache does not.
                case AiProvider.Codex:
                case AiProvider.CodexNative:
                case AiProvider.CursorAgent:
                case AiProvider.CursorAgentNative:
                // PI speaks a protocol of its own over a persistent process.
                case AiProvider.Pi:
                // Antigravity has no event stream at all — the answer arrives in one piece.
                case AiProvider.Antigravity:
                    return true;

                default:
                    return false;
            }
        }

        #endregion

        #region Native Mode Lifecycle

        /// <summary>
        /// Starts native mode if the setting is on and the selected provider supports it.
        /// </summary>
        /// <returns>
        /// True when the chat session took over, meaning the caller must not launch the terminal.
        /// False means "carry on as usual" — the setting is off, or this provider has no native channel.
        /// </returns>
        private async Task<bool> TryStartNativeModeAsync()
        {
            if (_settings == null || !_settings.UseNativeMode)
            {
                return false;
            }

            AiProvider provider = _settings.SelectedProvider;
            LogTerminalLaunch($"Native mode: attempting start for provider={provider}");
            if (!SupportsNativeMode(provider))
            {
                Debug.WriteLine($"Native mode: {provider} always uses the embedded terminal.");
                LogTerminalLaunch($"Native mode: {provider} always uses the embedded terminal.");

                // Reasonix gets its own wording: it *does* have a working ACP channel, so telling the
                // user it has none would be untrue — it is kept on the terminal on purpose.
                await ShowNativeFallbackNoticeAsync(provider == AiProvider.Reasonix
                    ? $"{GetProviderDisplayName(provider)} always runs in the embedded terminal."
                    : $"{GetProviderDisplayName(provider)} has no native chat channel — the embedded terminal was used instead.");
                return false;
            }

            try
            {
                string workspace = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
                {
                    Debug.WriteLine("Native mode: no usable workspace directory; using the embedded terminal.");
                    LogTerminalLaunch($"Native mode: no usable workspace directory (workspace='{workspace}'); using the embedded terminal.");
                    await ShowNativeFallbackNoticeAsync(
                        "Native mode needs an open folder or solution — the embedded terminal was used instead.");
                    return false;
                }

                // Read before the session consumes it. Only a resume the *user* asked for — from the
                // Session History window — replays a transcript into the chat; the resumes a relaunch
                // does for itself (model, effort, permission, plan) go through the same field and would
                // otherwise re-render the whole conversation on every dropdown change.
                string resumeRequest = Volatile.Read(ref _pendingResumeSessionId);

                IAgentSession session = CreateAgentSession(provider, workspace);
                if (session == null)
                {
                    return false;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Turning native mode on while a console session is live must not leave that agent
                // running invisibly behind the chat: stop the console and its idle watcher first.
                ResetAgentCompletionWatcher();
                await StopExistingTerminalAsync();
                EnsureNoConsoleAttached();

                // The chat lives in the terminal's grid slot, so a detached terminal hides it completely.
                // Bring the slot back into the panel, keeping the saved preference so the detached tab
                // returns if native mode is turned off again.
                if (_isTerminalDetached)
                {
                    await AttachTerminalAsync(preserveDetachPreference: true);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Publish the session before the panel swaps: ShowNativeTranscript refreshes the toolbar,
                // which asks IsNativeModeActive whether the detach control still applies.
                _nativeSessionCts = new CancellationTokenSource();
                _agentSession = session;
                session.Received += OnAgentEventReceived;

                ShowNativeTranscript(true);
                ChatTranscript.StopRequested -= OnChatStopRequested;
                ChatTranscript.StopRequested += OnChatStopRequested;
                ChatTranscript.InteractionResolved -= OnChatInteractionResolved;
                ChatTranscript.InteractionResolved += OnChatInteractionResolved;
                ChatTranscript.Clear();
                _pendingToolCalls.Clear();
                _streamingAssistantMessage = null;
                _streamingThinkingMessage = null;
                _lastNativeRateLimitNotice = null;
                _nativeTurnFinishConfig = null;
                _nativeTurnInFlight = false;
                ChatTranscript.SetStatus("Starting the agent...");

                await session.StartAsync(workspace, _nativeSessionCts.Token);

                _currentRunningProvider = provider;
                ChatTranscript.SetStatus("Ready.");

                // A resumed conversation is replayed from its transcript, because the CLI restores the
                // agent's memory without re-emitting a single message of it. Otherwise: an empty
                // transcript says nothing about what is running — the CLI's own startup banner never
                // reaches native mode, so the chat prints its own.
                if (!await TryReplayResumedTranscriptAsync(provider, workspace, resumeRequest))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    ShowChatWelcome(workspace);
                }

                // Native mode always lives in its own document tab: the conversation gets the full
                // editor width and its own composer, instead of the narrow panel strip.
                await ShowNativeChatTabAsync(focusComposer: false);

                LogTerminalLaunch($"Native mode: started successfully for provider={provider}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode failed to start: {ex}");
                LogTerminalLaunch($"Native mode: failed to start for provider={provider}: {ex}");

                // Never strand the user on a dead panel: tear the half-started session down and let the
                // caller fall back to the embedded terminal.
                await ShutdownNativeModeAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowNativeTranscript(false);
                await ShowNativeFallbackNoticeAsync(
                    $"Native mode could not start ({ex.Message}) — the embedded terminal was used instead.");

                return false;
            }
        }

        /// <summary>
        /// Tells the user, once and dismissibly, that the panel they are looking at is the terminal and
        /// not the chat they asked for. Silence here is the worst outcome: the setting is on, so the
        /// terminal appearing looks like the setting simply did nothing.
        /// </summary>
        private async Task ShowNativeFallbackNoticeAsync(string text)
        {
            try
            {
                await ShowAgentFinishNotificationAsync(text, null, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: could not show the fallback notice: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the adapter for a provider. Returns null when the provider has no native channel.
        /// </summary>
        private IAgentSession CreateAgentSession(AiProvider provider, string workspace, string modelOverride = null)
        {
            // Cleared for every launch, so the Claude-only state cannot outlive a switch to another
            // agent and have a side question re-run the previous CLI.
            _nativeClaudeOptions = null;

            switch (provider)
            {
                case AiProvider.ClaudeCode:
                case AiProvider.ClaudeCodeWSL:
                    return CreateClaudeSession(provider, workspace, modelOverride);

                case AiProvider.OpenCode:
                case AiProvider.Devin:
                case AiProvider.DevinNative:
                case AiProvider.Reasonix:
                    return CreateAcpSession(provider, workspace);

                case AiProvider.Codex:
                case AiProvider.CodexNative:
                case AiProvider.CursorAgent:
                case AiProvider.CursorAgentNative:
                    return CreateOneShotSession(provider, workspace);

                case AiProvider.Pi:
                    return CreatePiSession();

                case AiProvider.Antigravity:
                    return CreatePrintModeSession();

                default:
                    return null;
            }
        }

        private IAgentSession CreateClaudeSession(AiProvider provider, string workspace, string modelOverride = null)
        {
            bool isWsl = provider == AiProvider.ClaudeCodeWSL;

            bool planMode = _settings?.ClaudePlanMode == true;

            var options = new ClaudeSessionOptions
            {
                UseWsl = isWsl,
                ExecutablePath = isWsl ? "claude" : ResolveNativeClaudeExecutable(),
                WslWorkingDirectory = isWsl ? ConvertToWslPath(workspace) : string.Empty,
                SessionId = Guid.NewGuid().ToString(),
                Model = modelOverride ?? GetNativeModelArgument(),
                Effort = GetNativeEffortArgument(),

                // Plan mode is the CLI asking before it acts, so it cannot coexist with skipping
                // every prompt; plan wins when both are somehow set.
                DangerouslySkipPermissions = !planMode && _settings?.ClaudeDangerouslySkipPermissions == true,
                PermissionMode = planMode ? "plan" : "acceptEdits"
            };

            // A CLI installed after Visual Studio started is missing from the PATH we inherited; the
            // terminal path solves this the same way.
            string freshPath = GetFreshPathFromRegistry();
            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            // Consume a pending resume request from the Session History window, exactly as the terminal
            // launch does — "--continue" has no stream-json equivalent, so it is ignored there.
            string resumeArg = Interlocked.Exchange(ref _pendingResumeSessionId, null);
            if (!string.IsNullOrEmpty(resumeArg) && resumeArg != "-c")
            {
                options.ResumeSessionId = resumeArg;
            }

            // Remembered so a side question can re-run the same CLI — same executable, distro, folder
            // and model as the conversation it is a side question to.
            _nativeClaudeOptions = options;

            return new ClaudeStreamJsonSession(options);
        }

        /// <summary>
        /// Builds the ACP adapter. OpenCode, Devin (both flavours) and Reasonix expose the same
        /// <c>acp</c> subcommand and the same protocol, so only the executable and the session mode
        /// differ between them.
        /// </summary>
        private IAgentSession CreateAcpSession(AiProvider provider, string workspace)
        {
            bool isWsl = provider == AiProvider.Devin;
            string freshPath = GetFreshPathFromRegistry();

            string executable = ResolveNativeProviderExecutable(provider, GetAcpDefaultCommand(provider));
            if (!isWsl)
            {
                executable = ResolveExecutableOnPath(executable, freshPath);
            }

            var options = new AcpSessionOptions
            {
                UseWsl = isWsl,
                ExecutablePath = executable,
                WslWorkingDirectory = isWsl ? ConvertToWslPath(workspace) : string.Empty,
                ModeId = GetAcpModeId(provider),
                ModelName = GetAcpModelName(provider),
                ModelLaunchArgument = GetAcpModelLaunchArgument(provider),
                DisplayName = GetProviderDisplayName(provider),
                AnswerFirstRunPromptWithNo = provider == AiProvider.Reasonix,
                // Routed into the same terminal-launch log the console path writes to: on a Release
                // build Debug.WriteLine is gone, so this is the only way to see why an agent that
                // dies during the handshake died.
                DiagnosticLog = LogTerminalLaunch
            };

            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            return new AcpSession(options);
        }

        /// <summary>
        /// Builds the adapter for the CLIs that stream JSON but exit after every turn. Codex and Cursor
        /// Agent differ only in their wire format, which is what the protocol object supplies.
        /// </summary>
        private IAgentSession CreateOneShotSession(AiProvider provider, string workspace)
        {
            bool isWsl = provider == AiProvider.Codex || provider == AiProvider.CursorAgent;
            bool isCursor = provider == AiProvider.CursorAgent || provider == AiProvider.CursorAgentNative;
            string freshPath = GetFreshPathFromRegistry();

            string executable = isCursor
                ? ResolveNativeCursorExecutable(provider, isWsl, freshPath)
                : ResolveNativeProviderExecutable(provider, "codex");

            if (!isWsl && !isCursor)
            {
                executable = ResolveExecutableOnPath(executable, freshPath);
            }

            var options = new OneShotSessionOptions
            {
                UseWsl = isWsl,
                ExecutablePath = executable,
                WslWorkingDirectory = isWsl ? ConvertToWslPath(workspace) : string.Empty,
                // With nothing chosen, Cursor is asked for "auto" rather than left to its own default:
                // measured, a free plan refuses every named model, and "auto" is accepted on all plans.
                Model = ResolveOneShotModel(provider, isCursor),
                ReasoningEffort = isCursor ? string.Empty : GetCodexReasoningArgument(),
                SkipApprovals = isCursor
                    ? _settings?.CursorAgentAutoRun == true
                    : _settings?.CodexFullAuto == true,
                DisplayName = GetProviderDisplayName(provider)
            };

            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            IOneShotTurnProtocol protocol = isCursor
                ? (IOneShotTurnProtocol)new CursorAgentProtocol()
                : new CodexExecProtocol();

            return new OneShotResumeSession(options, protocol);
        }

        private string ResolveOneShotModel(AiProvider provider, bool isCursor)
        {
            string selected = GetSelectedProviderModelId(provider);
            if (!string.IsNullOrWhiteSpace(selected)) return selected;

            return isCursor ? "auto" : string.Empty;
        }

        /// <summary>
        /// Builds the PI adapter. PI keeps one process alive like the ACP agents, but over a protocol
        /// of its own.
        /// </summary>
        private IAgentSession CreatePiSession()
        {
            string freshPath = GetFreshPathFromRegistry();

            var options = new PiSessionOptions
            {
                ExecutablePath = ResolveExecutableOnPath(
                    ResolveNativeProviderExecutable(AiProvider.Pi, "pi"), freshPath),
                Model = GetSelectedProviderModelId(AiProvider.Pi),
                DisplayName = GetProviderDisplayName(AiProvider.Pi)
            };

            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            return new PiRpcSession(options);
        }

        /// <summary>
        /// Builds the print-mode adapter for Antigravity, whose headless surface has no event stream.
        /// </summary>
        private IAgentSession CreatePrintModeSession()
        {
            string freshPath = GetFreshPathFromRegistry();

            var options = new PrintModeSessionOptions
            {
                ExecutablePath = ResolveExecutableOnPath(
                    ResolveNativeProviderExecutable(AiProvider.Antigravity, "agy"), freshPath),
                Model = GetSelectedProviderModelId(AiProvider.Antigravity),
                SkipApprovals = _settings?.AntigravityDangerouslySkipPermissions == true,
                DisplayName = GetProviderDisplayName(AiProvider.Antigravity)
            };

            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                options.EnvironmentOverrides["PATH"] = freshPath;
            }

            return new PrintModeSession(options);
        }

        /// <summary>
        /// Resolves the Cursor Agent executable as an unquoted path, mirroring the terminal's order:
        /// a user-configured path, then the native install, then PATH.
        /// </summary>
        private string ResolveNativeCursorExecutable(AiProvider provider, bool isWsl, string freshPath)
        {
            string custom = GetCustomExecutablePath(provider);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return TrimMatchingQuotes(custom.Trim());
            }

            if (isWsl)
            {
                return "cursor-agent";
            }

            string nativePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cursor-agent", "agent.cmd");

            return File.Exists(nativePath) ? nativePath : ResolveExecutableOnPath("agent", freshPath);
        }

        private static string GetAcpDefaultCommand(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.OpenCode: return "opencode";
                case AiProvider.Reasonix: return "reasonix";
                default: return "devin";
            }
        }

        /// <summary>
        /// Session mode to request after the handshake. Devin governs permissions through modes rather
        /// than through the protocol's approval channel, so its "dangerous mode" setting maps here;
        /// the other agents keep whatever default they ship with.
        /// </summary>
        private string GetAcpModeId(AiProvider provider)
        {
            bool isDevin = provider == AiProvider.Devin || provider == AiProvider.DevinNative;

            if (isDevin && _settings?.DevinDangerousMode == true)
            {
                return "bypass";
            }

            return string.Empty;
        }

        /// <summary>
        /// Model to select after the handshake, for the agents that publish a model picker there
        /// (Devin and Open Code). Reasonix publishes none and takes its model at launch instead.
        /// </summary>
        private string GetAcpModelName(AiProvider provider)
        {
            if (provider == AiProvider.Reasonix) return string.Empty;

            return GetSelectedProviderModelId(provider);
        }

        /// <summary>
        /// Model passed on the launch command line, for Reasonix alone — it exposes no model picker
        /// over the protocol, so the only way to choose one is <c>reasonix acp -model &lt;id&gt;</c>.
        /// The flag itself is built by <see cref="AcpCommandBuilder.BuildReasonixModelArgument"/>, which
        /// is where the reason it must be spelled <c>-model</c> and not <c>-m</c> is documented — and
        /// where the test that keeps it that way lives.
        /// </summary>
        private string GetAcpModelLaunchArgument(AiProvider provider)
        {
            if (provider != AiProvider.Reasonix) return string.Empty;

            return AcpCommandBuilder.BuildReasonixModelArgument(GetSelectedProviderModelId(provider));
        }

        /// <summary>
        /// Resolves a provider's executable as an unquoted path for <see cref="ProcessStartInfo"/>.
        /// <see cref="ResolveProviderExecutable"/> cannot be reused directly: it quotes for a command
        /// line, and those quotes would become part of the file name here.
        /// </summary>
        private string ResolveNativeProviderExecutable(AiProvider provider, string defaultCommand)
        {
            string custom = GetCustomExecutablePath(provider);

            return string.IsNullOrWhiteSpace(custom) ? defaultCommand : TrimMatchingQuotes(custom.Trim());
        }

        /// <summary>
        /// Expands a bare command name into a full path with its extension.
        /// <para>
        /// The terminal path can pass a bare name because a shell applies PATHEXT to it; starting a
        /// process directly cannot. "opencode" would resolve to the extensionless npm shim — a shell
        /// script, not an image — and fail to launch, so the extension has to be found here.
        /// </para>
        /// </summary>
        private static string ResolveExecutableOnPath(string command, string pathValue)
        {
            if (string.IsNullOrWhiteSpace(command) ||
                command.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                command.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                !string.IsNullOrEmpty(Path.GetExtension(command)))
            {
                return command;
            }

            string searchPath = string.IsNullOrWhiteSpace(pathValue)
                ? Environment.GetEnvironmentVariable("PATH")
                : pathValue;

            if (string.IsNullOrWhiteSpace(searchPath))
            {
                return command;
            }

            // Same precedence as PATHEXT: a real binary wins over an npm .cmd shim.
            string[] extensions = { ".exe", ".cmd", ".bat" };

            foreach (string directory in searchPath.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                foreach (string extension in extensions)
                {
                    try
                    {
                        string candidate = Path.Combine(directory.Trim(), command + extension);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // A malformed PATH entry (illegal characters) — skip it, do not fail the launch.
                    }
                }
            }

            return command;
        }

        /// <summary>
        /// Resolves the Windows claude executable as an unquoted path suitable for
        /// <see cref="ProcessStartInfo"/>. Mirrors the terminal's resolution order — user-configured
        /// path, native install, then PATH.
        /// </summary>
        private string ResolveNativeClaudeExecutable()
        {
            string custom = GetCustomExecutablePath(AiProvider.ClaudeCode);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return TrimMatchingQuotes(custom.Trim());
            }

            string nativePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");

            return File.Exists(nativePath)
                ? nativePath
                : ResolveExecutableOnPath("claude", GetFreshPathFromRegistry());
        }

        /// <summary>
        /// Maps the selected model to the CLI alias. "Best" and "OpusPlan" are interactive-only
        /// selections with no headless equivalent, so they fall back to the CLI default.
        /// </summary>
        private string GetNativeModelArgument()
        {
            if (_settings == null)
            {
                return string.Empty;
            }

            switch (_settings.SelectedClaudeModel)
            {
                case ClaudeModel.Opus: return "opus";
                case ClaudeModel.Sonnet: return "sonnet";
                case ClaudeModel.Haiku: return "haiku";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Maps the selected effort to the CLI's <c>--effort</c> value. Every level the slider offers is
        /// accepted, including the undocumented "ultracode"; "Auto" is the extension's own "say nothing"
        /// and is the one value the CLI rejects, so it maps to the empty string.
        /// </summary>
        private string GetNativeEffortArgument()
        {
            if (_settings == null || _settings.SelectedEffortLevel == EffortLevel.Auto)
            {
                return string.Empty;
            }

            return _settings.SelectedEffortLevel.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Maps the Codex reasoning selection to <c>model_reasoning_effort</c>. Default is the
        /// extension's "do not override the model" choice and therefore maps to an empty string.
        /// </summary>
        private string GetCodexReasoningArgument()
        {
            if (_settings == null ||
                _settings.SelectedCodexReasoningLevel == CodexReasoningLevel.Default)
            {
                return string.Empty;
            }

            return _settings.SelectedCodexReasoningLevel.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Ends the session and releases the child process. Safe to call when native mode is not active.
        /// </summary>
        private async Task ShutdownNativeModeAsync()
        {
            DisposeNativeSession();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _pendingToolCalls.Clear();
            _streamingAssistantMessage = null;
            _streamingThinkingMessage = null;

            if (ChatTranscript != null)
            {
                ChatTranscript.StopRequested -= OnChatStopRequested;
                ChatTranscript.InteractionResolved -= OnChatInteractionResolved;

                // The session already denied the underlying requests on its way out; this just stops
                // the cards claiming they are still waiting for an answer.
                ChatTranscript.AbandonPendingInteractions();

                ChatTranscript.SetBusy(false);
                ChatTranscript.SetQueuedMessageCount(0);
                ChatTranscript.SetStatus(string.Empty);
            }

            // Leaving an empty chat tab behind would read as "the conversation was lost".
            CloseNativeChatTab();
        }

        /// <summary>
        /// Kills the agent process without touching the UI, so control teardown can call it directly.
        /// </summary>
        private void DisposeNativeSession()
        {
            IAgentSession session = _agentSession;
            _agentSession = null;

            _codexNativePromptQueue.Clear();
            _cancelledCodexNativePromptCount = 0;
            _codexNativeQueueOwner = null;
            _codexNativeTurnRendered?.TrySetResult(true);
            _codexNativeTurnRendered = null;

            if (session != null)
            {
                session.Received -= OnAgentEventReceived;

                try
                {
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Native mode: session dispose failed: {ex.Message}");
                }
            }

            try
            {
                if (_nativeSessionCts != null)
                {
                    _nativeSessionCts.Cancel();
                    _nativeSessionCts.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: cancellation cleanup failed: {ex.Message}");
            }
            _nativeSessionCts = null;
        }

        /// <summary>
        /// Swaps the panel between the chat transcript and the embedded terminal. Both live in the same
        /// grid cell and only one is ever visible.
        /// </summary>
        private void ShowNativeTranscript(bool show)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || TerminalHost == null)
            {
                return;
            }

            // While the chat is in its own tab the panel slot is empty and collapsed, so leaving native
            // mode has to close that tab and take the transcript back before the terminal can have the
            // cell again.
            if (!show && _chatIsInTab)
            {
                CloseNativeChatTab();
            }

            ChatTranscript.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TerminalHost.Visibility = (show || _chatIsInTab) ? Visibility.Collapsed : Visibility.Visible;

            // Font, zoom and composer height are applied here too, not only when the chat moves into
            // its tab: while the transcript is hosted in the panel nothing else would restore them.
            if (show)
            {
                ApplyChatAppearance();
            }

            // The chat shares the terminal's grid cell, so it inherits whatever the detach code did to
            // that cell: a session detached earlier leaves the group box collapsed and the slot at zero
            // minimum size, and the chat renders into nothing at all. Undo it whenever the chat takes
            // over the panel — this is the whole reason turning native mode on could show an empty
            // panel. The tab case is the opposite and stays collapsed on purpose.
            if (show && !_chatIsInTab && TerminalGroupBox != null)
            {
                TerminalGroupBox.Visibility = Visibility.Visible;
                RestoreTerminalSlotMinimumSize();
            }

            // Detach re-parents a Win32 console window into another tool window; with no console there
            // is nothing to detach, so the control is hidden. RefreshToolbarLayout owns the
            // button-versus-menu split, so it decides where the control reappears when the terminal
            // comes back — setting both to Visible here would show it twice.
            RefreshToolbarLayout();
        }

        #endregion

        #region Native Mode Send / Interrupt

        /// <summary>
        /// The single bifurcation point for every "hand this text to the agent" caller that isn't the
        /// prompt box: build errors, runtime errors, custom commands and the "On Agent Finish"
        /// follow-up. Native mode delivers it over the structured channel; otherwise it goes through
        /// the terminal exactly as before.
        /// <para>
        /// Terminal-only traffic — slash commands, CLI self-updates, the Caveman install — keeps
        /// calling <see cref="SendTextToTerminalAsync"/> directly, since none of it means anything to
        /// a structured session.
        /// </para>
        /// </summary>
        private async Task SendTextToAgentAsync(string text)
        {
            if (IsNativeModeActive)
            {
                await SendPromptToNativeAgentAsync(text);
                return;
            }

            await SendTextToTerminalAsync(text);
        }

        /// <summary>
        /// Delivers a prompt over the agent's structured channel and echoes it in the transcript.
        /// </summary>
        /// <summary>Sends prompt to a specific session's agent (multi-session support).</summary>
        private async Task SendPromptToNativeAgentAsync(string text, string sessionId)
        {
            var sessionState = GetSession(sessionId);
            if (sessionState?.AgentSession == null || string.IsNullOrWhiteSpace(text))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Update active session
            _activeSessionId = sessionId;

            if (SupportsQueuedCodexNativeChat(sessionState.SelectedProvider))
            {
                await SendPromptToCodexNativeAsync(sessionState.AgentSession, text);
                return;
            }

            await SendSinglePromptToNativeAgentAsync(sessionState, text);
        }

        private async Task SendPromptToNativeAgentAsync(string text)
        {
            IAgentSession session = _agentSession;
            if (session == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (SupportsQueuedCodexNativeChat(_currentRunningProvider))
            {
                await SendPromptToCodexNativeAsync(session, text);
                return;
            }

            await SendSinglePromptToNativeAgentAsync(session, text);
        }

        /// <summary>
        /// Fast path used by the UI send handler while a Codex turn is already running. It accepts the
        /// follow-up synchronously, before generic prompt preparation or its re-entrancy guard can
        /// reject the click. Attachments keep using the full preparation path so they are copied to
        /// their stable temporary locations before being queued.
        /// </summary>
        private bool TryQueueActiveCodexNativeFollowUp(string text, bool hasAttachments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (hasAttachments ||
                string.IsNullOrWhiteSpace(text) ||
                !SupportsQueuedCodexNativeChat(_currentRunningProvider) ||
                !_nativeTurnInFlight ||
                !ReferenceEquals(_codexNativeQueueOwner, _agentSession))
            {
                return false;
            }

            _codexNativePromptQueue.Enqueue(text);
            ChatTranscript.SetQueuedMessageCount(_codexNativePromptQueue.Count);
            return true;
        }

        /// <summary>
        /// Accepts Codex Native follow-ups while a turn is active and drains them sequentially. A
        /// queued call returns immediately; the call that started the loop owns it until the queue is
        /// empty or the session changes.
        /// </summary>
        private async Task SendPromptToCodexNativeAsync(IAgentSession session, string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!ReferenceEquals(session, _agentSession))
            {
                return;
            }

            if (_codexNativeQueueOwner != null)
            {
                _codexNativePromptQueue.Enqueue(text);
                ChatTranscript.SetQueuedMessageCount(_codexNativePromptQueue.Count);
                return;
            }

            _codexNativeQueueOwner = session;

            try
            {
                string nextPrompt = text;

                while (ReferenceEquals(session, _agentSession) &&
                       ReferenceEquals(session, _codexNativeQueueOwner))
                {
                    var turnRendered = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _codexNativeTurnRendered = turnRendered;

                    await SendSinglePromptToNativeAgentAsync(session, nextPrompt);
                    await turnRendered.Task;
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (!ReferenceEquals(session, _agentSession) ||
                        !ReferenceEquals(session, _codexNativeQueueOwner) ||
                        _codexNativePromptQueue.Count == 0)
                    {
                        break;
                    }

                    nextPrompt = _codexNativePromptQueue.Dequeue();
                    ChatTranscript.SetQueuedMessageCount(_codexNativePromptQueue.Count);
                }
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (ReferenceEquals(session, _codexNativeQueueOwner))
                {
                    _codexNativeQueueOwner = null;
                    _codexNativeTurnRendered = null;
                }
            }
        }

        /// <summary>Runs one native turn and echoes its prompt in the transcript.</summary>
        /// <summary>Sends a prompt to a session-specific agent (multi-session support).</summary>
        private async Task SendSinglePromptToNativeAgentAsync(NativeChatSessionState sessionState, string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (sessionState?.ChatTranscript == null || sessionState.AgentSession == null)
                return;

            var userMessage = new ChatMessageViewModel(ChatMessageKind.User) { Text = text.TrimEnd() };
            userMessage.Complete();
            sessionState.ChatTranscript.Messages.Add(userMessage);

            sessionState.StreamingAssistantMessage = null;
            sessionState.StreamingThinkingMessage = null;

            sessionState.TurnOutputTokens = 0;
            sessionState.TurnInputTokens = 0;
            sessionState.TurnInFlight = true;

            sessionState.ChatTranscript.BeginActivity();
            sessionState.ChatTranscript.SetBusy(sessionState.AgentSession.SupportsInterrupt);

            sessionState.TurnStartedUtc = DateTime.UtcNow;
            sessionState.TurnFinishConfig = GetEffectiveAgentFinish();

            try
            {
                await sessionState.AgentSession.SendAsync(text, sessionState.SessionCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: send failed: {ex}");
                AddNativeMessageToSession(sessionState, ChatMessageKind.Error, $"The prompt could not be delivered: {ex.Message}");
                sessionState.ChatTranscript.SetStatus(string.Empty);
                sessionState.ChatTranscript.SetBusy(false);
            }
        }

        private async Task SendSinglePromptToNativeAgentAsync(IAgentSession session, string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!ReferenceEquals(session, _agentSession))
            {
                return;
            }

            var userMessage = new ChatMessageViewModel(ChatMessageKind.User) { Text = text.TrimEnd() };
            userMessage.Complete();
            ChatTranscript.Messages.Add(userMessage);

            _streamingAssistantMessage = null;
            _streamingThinkingMessage = null;

            _nativeTurnOutputTokens = 0;
            _nativeTurnInputTokens = 0;
            _nativeTurnInFlight = true;

            // A spinner and a running clock rather than a motionless "Working...": on a twenty-minute
            // turn there is otherwise no way to tell progress from a hang.
            ChatTranscript.BeginActivity();
            ChatTranscript.SetBusy(session.SupportsInterrupt);

            // "On Agent Finish" replacement for the console-idle watcher: capture the same config the
            // watcher would have been armed with, and let the protocol's end-of-turn event fire it.
            _nativeTurnStartedUtc = DateTime.UtcNow;
            _nativeTurnFinishConfig = GetEffectiveAgentFinish();

            try
            {
                await session.SendAsync(text, _nativeSessionCts != null
                    ? _nativeSessionCts.Token
                    : CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: send failed: {ex}");
                AddNativeMessage(ChatMessageKind.Error, $"The prompt could not be delivered: {ex.Message}");
                ChatTranscript.SetStatus(string.Empty);
                ChatTranscript.SetBusy(false);
            }
        }

        /// <summary>Longest a side question is given before its process is killed.</summary>
        private const int SideQuestionTimeoutMs = 180000;

        /// <summary>
        /// Answers a <c>/btw</c> side question out of band. A second, short-lived CLI is launched with
        /// <c>--resume &lt;id&gt; --fork-session</c>: it reads the whole conversation for context but
        /// writes to a forked session id, so the live session's transcript is untouched and the turn in
        /// flight — if there is one — carries on unaware. That is what the command means in the TUI, and
        /// what the headless CLI refuses to do itself ("/btw isn't available in this environment").
        /// <para>
        /// Plain <c>--print</c>, so the answer is just text on stdout. None of the turn bookkeeping runs:
        /// no busy state, no activity clock, no "On Agent Finish" — a side question is not a turn.
        /// </para>
        /// </summary>
        private async Task AskSideQuestionAsync(string question)
        {
            ClaudeSessionOptions options = _nativeClaudeOptions;
            IAgentSession session = _agentSession;

            if (options == null || session == null || string.IsNullOrWhiteSpace(question))
            {
                return;
            }

            string workspace = await GetWorkspaceDirectoryAsync();

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = ClaudeCommandBuilder.GetFileName(options),
                Arguments = ClaudeCommandBuilder.GetSideQuestionArguments(options, session.ResumableSessionId),
                WorkingDirectory = workspace
            };

            foreach (KeyValuePair<string, string> pair in options.EnvironmentOverrides)
            {
                hostOptions.EnvironmentOverrides[pair.Key] = pair.Value;
            }

            var answer = new StringBuilder();
            var stderr = new StringBuilder();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var host = new JsonLineProcessHost(hostOptions);

            EventHandler<string> onLine = delegate (object s, string line) { lock (answer) { answer.AppendLine(line); } };
            EventHandler<string> onError = delegate (object s, string line) { lock (stderr) { stderr.AppendLine(line); } };
            EventHandler<int> onExited = delegate (object s, int code) { exited.TrySetResult(code); };

            host.LineReceived += onLine;
            host.ErrorLineReceived += onError;
            host.Exited += onExited;

            try
            {
                await host.StartAsync(CancellationToken.None);

                // Over stdin, like the one-shot adapters: nothing to escape onto a command line and no
                // length limit. EOF is what tells the CLI the prompt is complete.
                await host.WriteLineAsync(question, CancellationToken.None);
                host.CloseInput();

                Task completed = await Task.WhenAny(exited.Task, Task.Delay(SideQuestionTimeoutMs));
                if (completed != exited.Task)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AddNativeMessage(ChatMessageKind.Error, "The side question timed out.");
                    return;
                }

                await host.WaitForOutputDrainAsync(2000);

                string text;
                lock (answer) { text = answer.ToString().Trim(); }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (text.Length > 0)
                {
                    AddNativeMessage(ChatMessageKind.Assistant, text);
                }
                else
                {
                    string detail;
                    lock (stderr) { detail = stderr.ToString().Trim(); }

                    AddNativeMessage(ChatMessageKind.Error, string.IsNullOrEmpty(detail)
                        ? "The side question returned nothing."
                        : "The side question failed: " + detail);
                }

                ChatTranscript.ScrollToEndIfFollowing();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Side question failed: {ex}");

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNativeMessage(ChatMessageKind.Error, "The side question failed: " + ex.Message);
            }
            finally
            {
                host.LineReceived -= onLine;
                host.ErrorLineReceived -= onError;
                host.Exited -= onExited;

                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Side question: host dispose failed: {ex.Message}");
                }
            }
        }

#pragma warning disable VSTHRD100 // Async void is required by the UI event signature
        private async void OnChatStopRequested(object sender, EventArgs e)
#pragma warning restore VSTHRD100
        {
            // The transcript that raised it identifies the session, so Esc/Stop in a tab interrupts
            // that tab's agent instead of the panel's.
            var transcript = sender as ChatTranscriptView;
            string owningSessionId = null;

            if (transcript != null)
            {
                lock (_sessionLock)
                {
                    foreach (KeyValuePair<string, NativeChatSessionState> pair in _nativeSessions)
                    {
                        if (ReferenceEquals(pair.Value.ChatTranscript, transcript))
                        {
                            owningSessionId = pair.Key;
                            break;
                        }
                    }
                }
            }

            if (owningSessionId != null)
            {
                await InterruptNativeAgentAsync(owningSessionId);
                return;
            }

            await InterruptNativeAgentAsync();
        }

        /// <summary>Aborts the turn in flight for a specific session.</summary>
        private async Task InterruptNativeAgentAsync(string sessionId)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var sessionState = GetSession(sessionId);
            if (sessionState?.AgentSession == null || !sessionState.AgentSession.SupportsInterrupt || !sessionState.AgentSession.IsBusy)
                return;

            try
            {
                sessionState.ChatTranscript.SetBusy(false);
                sessionState.ChatTranscript.SetActivityLabel("Stopping...");

                await sessionState.AgentSession.InterruptAsync(sessionState.SessionCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: interrupt failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Aborts the turn in flight. The Claude adapter relaunches itself transparently afterwards.
        /// </summary>
        private async Task InterruptNativeAgentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            IAgentSession session = _agentSession;
            if (session == null || !session.SupportsInterrupt || !session.IsBusy)
            {
                return;
            }

            try
            {
                if (SupportsQueuedCodexNativeChat(_currentRunningProvider))
                {
                    _cancelledCodexNativePromptCount += ClearQueuedCodexNativePrompts();
                }

                ChatTranscript.SetBusy(false);
                ChatTranscript.SetActivityLabel("Stopping...");

                await session.InterruptAsync(_nativeSessionCts != null
                    ? _nativeSessionCts.Token
                    : CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: interrupt failed: {ex.Message}");
            }
        }

        /// <summary>Removes follow-ups that have not started and returns how many were discarded.</summary>
        private int ClearQueuedCodexNativePrompts()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            int count = _codexNativePromptQueue.Count;
            _codexNativePromptQueue.Clear();
            ChatTranscript?.SetQueuedMessageCount(0);
            return count;
        }

        #endregion

        #region Native Mode Event Bridge

        /// <summary>
        /// Receives adapter events on a background thread and hands them to the UI thread.
        /// </summary>
        private void OnAgentEventReceived(object sender, AgentEvent agentEvent)
        {
            if (agentEvent == null || !ReferenceEquals(sender, _agentSession))
            {
                return;
            }

#pragma warning disable VSSDK007, VSTHRD110 // Intentionally fire-and-forget; events arrive on the reader thread
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                try
                {
                    ApplyAgentEvent(agentEvent);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Native mode: failed to render {agentEvent.Kind}: {ex}");
                }
            }).FileAndForget("claudecode/nativemode/event");
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>Processes an event for a specific session (multi-session event routing).</summary>
        private void ApplyAgentEventToSession(string sessionId, NativeChatSessionState session, AgentEvent agentEvent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (session == null)
                return;

            // Process based on event kind
            switch (agentEvent.Kind)
            {
                case AgentEventKind.SessionStarted:
                    // Every tab refreshes its own caption/label: the saved title only becomes known once
                    // the agent reports its session id, and each session has a different one.
                    UpdateSessionTabCaption(session);
                    break;

                case AgentEventKind.AssistantText:
                    {
                        var msg = session.StreamingAssistantMessage;
                        AppendStreamingTextForSession(session, ref msg, ChatMessageKind.Assistant, agentEvent.Text, null);
                        session.StreamingAssistantMessage = msg;
                    }
                    break;

                case AgentEventKind.Thinking:
                    {
                        var msg = session.StreamingThinkingMessage;
                        AppendStreamingTextForSession(session, ref msg, ChatMessageKind.Thinking, agentEvent.Text, "Thinking");
                        session.StreamingThinkingMessage = msg;
                    }
                    break;

                case AgentEventKind.ToolCallStarted:
                    AddToolCallMessageToSession(session, agentEvent);
                    break;

                case AgentEventKind.ToolCallCompleted:
                    CompleteToolCallMessageForSession(session, agentEvent);
                    break;

                case AgentEventKind.PermissionRequested:
                    ShowNativePermissionDialog(agentEvent.PermissionRequest);
                    break;

                case AgentEventKind.InteractionRequested:
                    ShowNativeInteraction(agentEvent.Interaction);
                    break;

                case AgentEventKind.UsageUpdated:
                    if (sessionId == _activeSessionId)
                        ApplyNativeLiveUsage(agentEvent.Usage);
                    break;

                case AgentEventKind.RateLimitUpdated:
                    ApplyNativeRateLimitForSession(session, agentEvent.RateLimit);
                    break;

                case AgentEventKind.SessionError:
                    AddNativeMessageToSession(session, ChatMessageKind.Error, agentEvent.Text);
                    break;

                case AgentEventKind.TurnCompleted:
                    try
                    {
                        CompleteNativeTurnForSession(session, agentEvent);
                    }
                    finally
                    {
                        session.CodexTurnRendered?.TrySetResult(true);
                    }
                    break;
            }
        }

        private void ApplyAgentEvent(AgentEvent agentEvent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            switch (agentEvent.Kind)
            {
                case AgentEventKind.SessionStarted:
                    // Re-announced at the start of every turn — never treat it as a new conversation.
                    // Refreshed here (cheap, idempotent) so a resumed session's saved title shows up in
                    // the header above the transcript as soon as its id is known, not only after an
                    // explicit rename.
                    UpdateChatTabCaption();
                    break;

                case AgentEventKind.AssistantText:
                    AppendStreamingText(ref _streamingAssistantMessage, ChatMessageKind.Assistant, agentEvent.Text, null);
                    break;

                case AgentEventKind.Thinking:
                    AppendStreamingText(ref _streamingThinkingMessage, ChatMessageKind.Thinking, agentEvent.Text, "Thinking");
                    break;

                case AgentEventKind.ToolCallStarted:
                    AddToolCallMessage(agentEvent);
                    break;

                case AgentEventKind.ToolCallCompleted:
                    CompleteToolCallMessage(agentEvent);
                    break;

                case AgentEventKind.PermissionRequested:
                    ShowNativePermissionDialog(agentEvent.PermissionRequest);
                    break;

                case AgentEventKind.InteractionRequested:
                    ShowNativeInteraction(agentEvent.Interaction);
                    break;

                case AgentEventKind.UsageUpdated:
                    ApplyNativeLiveUsage(agentEvent.Usage);
                    break;

                case AgentEventKind.RateLimitUpdated:
                    ApplyNativeRateLimit(agentEvent.RateLimit);
                    break;

                case AgentEventKind.SessionError:
                    AddNativeMessage(ChatMessageKind.Error, agentEvent.Text);
                    break;

                case AgentEventKind.TurnCompleted:
                    try
                    {
                        CompleteNativeTurn(agentEvent);
                    }
                    finally
                    {
                        _codexNativeTurnRendered?.TrySetResult(true);
                    }
                    break;
            }
        }

        /// <summary>
        /// Appends a streamed chunk, opening a row on the first one. Passing the field by reference
        /// keeps the "current row" bookkeeping in one place for both text and thinking.
        /// </summary>
        private void AppendStreamingText(ref ChatMessageViewModel target, ChatMessageKind kind, string text, string header)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (target == null)
            {
                target = new ChatMessageViewModel(kind) { IsStreaming = true, Header = header ?? string.Empty };
                ChatTranscript.Messages.Add(target);
            }

            target.Append(text);
        }

        /// <summary>
        /// Characters of a tool result kept in the transcript. A whole-file Read or a verbose test run
        /// otherwise puts hundreds of kilobytes into a text box and stalls the layout for seconds.
        /// </summary>
        private const int MaxToolResultLength = 20000;

        private void AddToolCallMessage(AgentEvent agentEvent)
        {
            // A new tool call means the assistant's current sentence is finished; close it so the tool
            // row does not end up above still-growing text.
            FinishStreamingMessages();

            // Collapsed, the row is one line — so that line has to say what the tool did and to what,
            // not just "Edit". The presenter turns the raw payload into that line plus, for the editing
            // tools, the diff shown when the row is opened.
            ChatToolPresentation presentation = ChatToolPresenter.Describe(agentEvent.ToolName, agentEvent.ToolInputJson);

            var message = new ChatMessageViewModel(ChatMessageKind.ToolCall)
            {
                ToolCallId = agentEvent.ToolCallId,
                ToolName = agentEvent.ToolName,
                ToolInputJson = FormatToolInput(agentEvent.ToolInputJson),
                Header = presentation.Title,
                ToolIcon = presentation.Icon,
                ToolTarget = presentation.Subtitle,
                ToolBadge = presentation.Badge,
                ToolAccent = ChatToolAccents.For(presentation.Category),
                IsRunning = true
            };

            message.SetDiff(presentation.Diff);

            ChatTranscript.Messages.Add(message);

            if (!string.IsNullOrEmpty(agentEvent.ToolCallId))
            {
                _pendingToolCalls[agentEvent.ToolCallId] = message;
            }
        }

        private void CompleteToolCallMessage(AgentEvent agentEvent)
        {
            ChatMessageViewModel message;
            if (string.IsNullOrEmpty(agentEvent.ToolCallId) ||
                !_pendingToolCalls.TryGetValue(agentEvent.ToolCallId, out message))
            {
                return;
            }

            _pendingToolCalls.Remove(agentEvent.ToolCallId);

            message.ToolResult = TruncateToolResult(agentEvent.ToolResult);
            message.IsError = agentEvent.IsError;
            message.IsRunning = false;

            // Failures open themselves: a collapsed row would hide the reason the agent gave up.
            if (agentEvent.IsError)
            {
                message.IsExpanded = true;
            }
        }

        private static string TruncateToolResult(string result)
        {
            if (string.IsNullOrEmpty(result) || result.Length <= MaxToolResultLength)
            {
                return result;
            }

            return result.Substring(0, MaxToolResultLength) +
                   Environment.NewLine + Environment.NewLine +
                   $"… {result.Length - MaxToolResultLength:N0} more characters not shown.";
        }

        /// <summary>
        /// Puts a question / plan / permission card in the transcript. The agent is stopped on its side
        /// until the card is answered, so the status line says so rather than leaving "Working..." up.
        /// </summary>
        private void ShowNativeInteraction(AgentInteractionRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (request == null || ChatTranscript == null)
            {
                return;
            }

            if (ShouldPlayNativeQuestionSound(
                _currentRunningProvider,
                _nativeTurnFinishConfig,
                request))
            {
                // Unlike the terminal watcher, the structured channel emits each waiting interaction
                // once, so there is no polling episode to debounce.
                PlayQuestionSound();
            }

            // Close whatever text was streaming: the card belongs below the sentence that introduced it.
            FinishStreamingMessages();

            var interaction = new ChatInteractionViewModel(request);
            ChatTranscript.AddInteraction(interaction);

            // The label changes but the clock keeps running: the turn is still open, and the time spent
            // waiting for the user is part of how long it took.
            ChatTranscript.SetActivityLabel(interaction.IsPlanReview
                ? "Waiting for you to review the plan..."
                : "Waiting for your answer...");
        }

        /// <summary>
        /// Whether a structured Claude interaction should play the distinct "waiting for you" sound.
        /// Questions, plan reviews and tool approvals all stop the turn for an answer, matching the
        /// terminal detector's selection/confirmation semantics.
        /// </summary>
        internal static bool ShouldPlayNativeQuestionSound(
            AiProvider? provider,
            AgentFinishConfig config,
            AgentInteractionRequest request)
        {
            return IsClaudeProvider(provider) &&
                   config != null &&
                   config.Enabled &&
                   config.PlayQuestionSound &&
                   request != null;
        }

        private void OnChatInteractionResolved(object sender, ChatInteractionViewModel interaction)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChatTranscript == null || interaction == null)
            {
                return;
            }

            // Approving the plan is how a session leaves plan mode. Leaving the setting on would put the
            // agent straight back into planning the next time it is relaunched, right after the user
            // told it to go ahead.
            if (interaction.IsPlanReview && interaction.WasAccepted && _settings?.ClaudePlanMode == true)
            {
                _settings.ClaudePlanMode = false;
                SaveSettings();
                UpdateChatComposerState();
            }

            // Empty hands the line back to the rotating verbs: the agent is working again.
            ChatTranscript.SetActivityLabel(string.Empty);
        }

        /// <summary>
        /// Folds a mid-turn usage snapshot into the running totals and refreshes the status line.
        /// </summary>
        private void ApplyNativeLiveUsage(AgentUsage usage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (usage == null || ChatTranscript == null)
            {
                return;
            }

            _nativeTurnOutputTokens += usage.OutputTokens;

            // Not accumulated: every request re-sends the whole conversation, so the latest one is the
            // context size, and summing them would report a number several times too large.
            if (usage.InputTokens > 0) _nativeTurnInputTokens = usage.InputTokens;

            ChatTranscript.SetActivityDetail(
                ChatFormatting.Tokens(_nativeTurnInputTokens, _nativeTurnOutputTokens));
        }

        private void CompleteNativeTurn(AgentEvent agentEvent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            FinishStreamingMessages();
            ChatTranscript.SetBusy(false);

            // One turn, one firing: clearing it here means a stray end-of-turn event (the one-shot
            // adapters can emit one on relaunch) can't run the action a second time.
            AgentFinishConfig finishConfig = _nativeTurnFinishConfig;
            _nativeTurnFinishConfig = null;

            if (agentEvent.WasInterrupted)
            {
                AddNativeMessage(ChatMessageKind.Notice, "Turn interrupted.");

                if (_cancelledCodexNativePromptCount > 0)
                {
                    AddNativeMessage(
                        ChatMessageKind.Notice,
                        _cancelledCodexNativePromptCount == 1
                            ? "1 queued message was cancelled."
                            : $"{_cancelledCodexNativePromptCount} queued messages were cancelled.");
                    _cancelledCodexNativePromptCount = 0;
                }
            }

            if (agentEvent.PermissionDenials != null && agentEvent.PermissionDenials.Count > 0)
            {
                // The stream-json protocol has no interactive approval, so a blocked tool is only
                // reported here. Without this line the agent looks like it silently ignored the request.
                var names = new List<string>();
                foreach (AgentPermissionDenial denial in agentEvent.PermissionDenials)
                {
                    names.Add(string.IsNullOrEmpty(denial.ToolName) ? "tool" : denial.ToolName);
                }

                AddNativeMessage(ChatMessageKind.Notice,
                    $"Blocked for lack of permission: {string.Join(", ", names)}. " +
                    "Enable \"Skip permissions\" in the agent menu to allow these tools.");
            }

            // The CLI's own duration is the honest one; the wall clock covers the adapters that report
            // none. Read before EndActivity, which stops the clock.
            TimeSpan elapsed = ResolveTurnDuration(agentEvent.Usage);
            bool wasInFlight = _nativeTurnInFlight;
            _nativeTurnInFlight = false;

            // The status line goes away rather than repeating the footer that is about to land right
            // above it — the two sat adjacent and said the same thing twice.
            ChatTranscript.EndActivity(string.Empty);

            // A permanent footer for the turn: "how long did that take" is exactly the question asked
            // after scrolling back, and the status line is transient.
            if (wasInFlight)
            {
                AddNativeMessage(ChatMessageKind.Notice,
                    FormatTurnFooter(
                        agentEvent.Usage,
                        elapsed,
                        agentEvent.WasInterrupted,
                        SupportsQueuedCodexNativeChat(_currentRunningProvider)));
            }

            FireNativeAgentFinish(finishConfig, agentEvent);
        }

        /// <summary>
        /// How long the turn took: the CLI's own measurement when it reports one, the wall clock
        /// otherwise. The live status clock is the fallback's source, so the footer and the ticking
        /// line it replaces never disagree.
        /// </summary>
        private TimeSpan ResolveTurnDuration(AgentUsage usage)
        {
            if (usage != null && usage.DurationMs > 0)
            {
                return TimeSpan.FromMilliseconds(usage.DurationMs);
            }

            TimeSpan onScreen = ChatTranscript != null ? ChatTranscript.ActivityElapsed : TimeSpan.Zero;

            return onScreen > TimeSpan.Zero ? onScreen : DateTime.UtcNow - _nativeTurnStartedUtc;
        }

        /// <summary>
        /// Runs the "On Agent Finish" notify/action for a turn that ended on a protocol event. This is
        /// the whole point of native mode for this feature: no AttachConsole, no screen hashing, no
        /// idle heuristic — the agent says when it is done.
        /// </summary>
        private void FireNativeAgentFinish(AgentFinishConfig cfg, AgentEvent agentEvent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // An interrupted turn is the user cancelling, not the agent finishing; building or running
            // on top of a half-applied edit would be actively harmful.
            if (cfg == null || !cfg.Enabled || agentEvent.WasInterrupted)
            {
                return;
            }

            AgentUsage usage = agentEvent.Usage;

            // The CLI's own duration is the honest one; the wall clock is only a fallback for adapters
            // that don't report it.
            TimeSpan duration = usage != null && usage.DurationMs > 0
                ? TimeSpan.FromMilliseconds(usage.DurationMs)
                : DateTime.UtcNow - _nativeTurnStartedUtc;

            int tokenDelta = usage != null ? usage.InputTokens + usage.OutputTokens : 0;
            string detailedTokenSummary =
                SupportsQueuedCodexNativeChat(_currentRunningProvider) &&
                usage != null &&
                (usage.InputTokens > 0 || usage.OutputTokens > 0)
                    ? ChatFormatting.CodexTokens(
                        usage.InputTokens,
                        usage.OutputTokens,
                        usage.CacheReadTokens)
                    : null;

#pragma warning disable VSSDK007, VSTHRD110 // Intentionally fire-and-forget; the turn is already over
            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await OnAgentTurnCompletedAsync(
                    cfg,
                    duration,
                    tokenDelta,
                    detailedTokenSummary);
            }).FileAndForget("claudecode/nativemode/agentfinish");
#pragma warning restore VSSDK007, VSTHRD110
        }

        private void FinishStreamingMessages()
        {
            if (_streamingAssistantMessage != null)
            {
                _streamingAssistantMessage.Complete();
                _streamingAssistantMessage = null;
            }

            if (_streamingThinkingMessage != null)
            {
                _streamingThinkingMessage.Complete();
                _streamingThinkingMessage = null;
            }
        }

        private void AddNativeMessage(ChatMessageKind kind, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || ChatTranscript == null)
            {
                return;
            }

            var message = new ChatMessageViewModel(kind) { Text = text };
            message.Complete();
            ChatTranscript.Messages.Add(message);
        }

        /// <summary>
        /// The one-line summary left in the transcript when a turn ends, mirroring what the CLI prints
        /// after a run. Codex keeps its cache and output breakdown because its reported input includes
        /// cached context; collapsing that to one unlabeled number makes short replies look implausibly
        /// expensive. Other providers retain the compact total.
        /// </summary>
        private static string FormatTurnFooter(
            AgentUsage usage,
            TimeSpan elapsed,
            bool wasInterrupted,
            bool showCodexBreakdown)
        {
            string footer = wasInterrupted
                ? "✳ Stopped after " + ChatFormatting.Duration(elapsed)
                : "✳ Done in " + ChatFormatting.Duration(elapsed);

            if (usage != null && (usage.InputTokens > 0 || usage.OutputTokens > 0))
            {
                footer += " · " + (showCodexBreakdown
                    ? ChatFormatting.CodexTokens(
                        usage.InputTokens,
                        usage.OutputTokens,
                        usage.CacheReadTokens)
                    : ChatFormatting.Tokens(usage.InputTokens, usage.OutputTokens));
            }

            return footer;
        }

        /// <summary>
        /// Pretty-prints the tool input so a one-line JSON blob is readable in the collapsed card.
        /// </summary>
        private static string FormatToolInput(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                object parsed = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                return Newtonsoft.Json.JsonConvert.SerializeObject(parsed, Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// Asks the user to approve a tool call. Only the ACP agents can reach this: their protocol has
        /// a real approval channel, unlike Claude's stream-json, which auto-denies and only reports the
        /// denial afterwards.
        /// <para>
        /// The agent is blocked while the dialog is up, so every exit path answers — closing the window
        /// counts as a refusal rather than leaving the CLI waiting forever.
        /// </para>
        /// </summary>
        private void ShowNativePermissionDialog(AgentPermissionRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (request == null || request.Options == null || request.Options.Count == 0)
            {
                request?.Cancel();
                return;
            }

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            var dialog = new Window
            {
                Title = "Permission required",
                SizeToContent = SizeToContent.Height,
                Width = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = Application.Current?.MainWindow; } catch (Exception) { }

            var layout = new StackPanel { Margin = new Thickness(16) };

            layout.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(request.Description) ? "The agent is asking for permission." : request.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = themeFg,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            if (!string.IsNullOrEmpty(request.ToolName) && request.ToolName != request.Description)
            {
                layout.Children.Add(new TextBlock
                {
                    Text = request.ToolName,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = themeFg,
                    Opacity = 0.8,
                    Margin = new Thickness(0, 0, 0, 12)
                });
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            string chosenOptionId = null;
            Button refuseButton = null;

            foreach (AgentPermissionOption option in request.Options)
            {
                AgentPermissionOption current = option;

                var button = new Button
                {
                    Content = string.IsNullOrEmpty(current.Name) ? current.OptionId : current.Name,
                    MinWidth = 96,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = themeBg,
                    Foreground = themeFg,
                    BorderBrush = themeFg
                };

                button.Click += delegate
                {
                    chosenOptionId = current.OptionId;
                    dialog.Close();
                };

                buttons.Children.Add(button);

                if (refuseButton == null && current.Kind != null && current.Kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase))
                {
                    refuseButton = button;
                }
            }

            layout.Children.Add(buttons);
            dialog.Content = layout;

            // Refusing is the safe default, so that is what Enter and Escape do.
            if (refuseButton != null)
            {
                refuseButton.IsDefault = true;
                refuseButton.IsCancel = true;
            }

            dialog.Closed += delegate
            {
                if (string.IsNullOrEmpty(chosenOptionId))
                {
                    request.Cancel();
                }
                else
                {
                    request.Respond(chosenOptionId);
                }
            };

            dialog.ShowDialog();
        }

        /// <summary>
        /// Feeds the inline usage bars from the agent's own stream instead of the usage page.
        /// <para>
        /// The stream says which window is under pressure and when it resets, but never a percentage —
        /// the fill of each bar still comes from the scraped snapshot. So this refreshes the reset text
        /// of the matching bar rather than inventing a number, and surfaces a throttling warning in the
        /// transcript, which the bars cannot express on their own.
        /// </para>
        /// </summary>
        private void ApplyNativeRateLimit(AgentRateLimit rateLimit)
        {
            if (rateLimit == null)
            {
                return;
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            Debug.WriteLine($"Native mode rate limit: {rateLimit.Status}/{rateLimit.LimitType} resets={rateLimit.ResetsAtUnix}");

            try
            {
                bool weekly = !string.IsNullOrEmpty(rateLimit.LimitType) &&
                              rateLimit.LimitType.IndexOf("week", StringComparison.OrdinalIgnoreCase) >= 0;
                string resets = FormatRateLimitReset(rateLimit.ResetsAtUnix);

                if (!string.IsNullOrEmpty(resets))
                {
                    if (weekly)
                    {
                        if (InlineWeeklyReset != null) InlineWeeklyReset.Text = resets;
                    }
                    else
                    {
                        if (InlineSessionReset != null) InlineSessionReset.Text = resets;
                    }
                }

                string status = rateLimit.Status ?? string.Empty;
                if (status.Length == 0 || status.Equals("allowed", StringComparison.OrdinalIgnoreCase))
                {
                    _lastNativeRateLimitNotice = null;
                    return;
                }

                string window = weekly ? "weekly" : "session";
                string notice = status.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0
                    ? $"The {window} usage limit was reached."
                    : $"Approaching the {window} usage limit.";

                if (!string.IsNullOrEmpty(resets)) notice += " " + resets + ".";
                if (rateLimit.IsUsingOverage) notice += " Extra usage is being billed.";

                // The CLI repeats the event on every turn while the window stays hot; repeating the
                // notice would bury the conversation under it.
                if (string.Equals(_lastNativeRateLimitNotice, notice, StringComparison.Ordinal))
                {
                    return;
                }

                _lastNativeRateLimitNotice = notice;
                AddNativeMessage(ChatMessageKind.Notice, notice);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Native mode: failed to apply rate limit: {ex.Message}");
            }
        }

        /// <summary>Turns the stream's Unix reset stamp into the same "Resets ..." wording the bars use.</summary>
        private static string FormatRateLimitReset(long resetsAtUnix)
        {
            if (resetsAtUnix <= 0)
            {
                return string.Empty;
            }

            try
            {
                DateTime local = DateTimeOffset.FromUnixTimeSeconds(resetsAtUnix).ToLocalTime().DateTime;
                return local.Date == DateTime.Now.Date
                    ? "Resets " + local.ToString("HH:mm")
                    : "Resets " + local.ToString("ddd, HH:mm");
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>Appends streaming text to a specific session's transcript.</summary>
        private void AppendStreamingTextForSession(NativeChatSessionState session, ref ChatMessageViewModel target, ChatMessageKind kind, string text, string header)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (target == null)
            {
                target = new ChatMessageViewModel(kind) { IsStreaming = true, Header = header ?? string.Empty };
                session.ChatTranscript.Messages.Add(target);
            }

            // Append() feeds the internal buffer that Complete() renders from; assigning Text directly
            // leaves that buffer empty and the finished row renders blank.
            target.Append(text);
        }

        /// <summary>Adds a tool call message to a specific session's transcript.</summary>
        private void AddToolCallMessageToSession(NativeChatSessionState session, AgentEvent agentEvent)
        {
            FinishStreamingMessages(session);

            ChatToolPresentation presentation = ChatToolPresenter.Describe(agentEvent.ToolName, agentEvent.ToolInputJson);

            var message = new ChatMessageViewModel(ChatMessageKind.ToolCall)
            {
                ToolCallId = agentEvent.ToolCallId,
                ToolName = agentEvent.ToolName,
                ToolInputJson = FormatToolInput(agentEvent.ToolInputJson),
                Header = presentation.Title,
                ToolIcon = presentation.Icon,
                ToolTarget = presentation.Subtitle,
                ToolBadge = presentation.Badge,
                ToolAccent = ChatToolAccents.For(presentation.Category),
                IsRunning = true
            };

            message.SetDiff(presentation.Diff);
            session.ChatTranscript.Messages.Add(message);

            if (!string.IsNullOrEmpty(agentEvent.ToolCallId))
            {
                session.PendingToolCalls[agentEvent.ToolCallId] = message;
            }
        }

        /// <summary>Completes a tool call message in a specific session's transcript.</summary>
        private void CompleteToolCallMessageForSession(NativeChatSessionState session, AgentEvent agentEvent)
        {
            ChatMessageViewModel message;
            if (string.IsNullOrEmpty(agentEvent.ToolCallId) ||
                !session.PendingToolCalls.TryGetValue(agentEvent.ToolCallId, out message))
            {
                return;
            }

            session.PendingToolCalls.Remove(agentEvent.ToolCallId);

            message.ToolResult = TruncateToolResult(agentEvent.ToolResult);
            message.IsError = agentEvent.IsError;
            message.IsRunning = false;

            if (agentEvent.IsError)
            {
                message.IsExpanded = true;
            }
        }

        /// <summary>Adds a status message to a specific session's transcript.</summary>
        private void AddNativeMessageToSession(NativeChatSessionState session, ChatMessageKind kind, string text)
        {
            var msg = new ChatMessageViewModel(kind) { Text = text };
            session.ChatTranscript.Messages.Add(msg);
        }

        /// <summary>Applies rate limit to a specific session.</summary>
        private void ApplyNativeRateLimitForSession(NativeChatSessionState session, AgentRateLimit rateLimit)
        {
            if (rateLimit == null)
                return;

            Debug.WriteLine($"Native mode rate limit: {rateLimit.Status}/{rateLimit.LimitType} resets={rateLimit.ResetsAtUnix}");

            bool weekly = !string.IsNullOrEmpty(rateLimit.LimitType) &&
                          rateLimit.LimitType.IndexOf("week", StringComparison.OrdinalIgnoreCase) >= 0;
            string resets = FormatRateLimitReset(rateLimit.ResetsAtUnix);

            // "allowed" means the request was NOT limited — the CLI emits it on every turn. Announcing
            // it produced the bogus "Rate limited: allowed" line.
            string status = rateLimit.Status ?? string.Empty;
            if (status.Length == 0 || status.Equals("allowed", StringComparison.OrdinalIgnoreCase))
            {
                session.LastRateLimitNotice = null;
                return;
            }

            string window = weekly ? "weekly" : "session";
            string message = status.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0
                ? $"The {window} usage limit was reached."
                : $"Approaching the {window} usage limit.";

            if (!string.IsNullOrEmpty(resets)) message += " " + resets + ".";
            if (rateLimit.IsUsingOverage) message += " Extra usage is being billed.";

            // Repeated on every turn while the window stays hot; say it once.
            if (string.Equals(session.LastRateLimitNotice, message, StringComparison.Ordinal))
            {
                return;
            }

            session.LastRateLimitNotice = message;
            AddNativeMessageToSession(session, ChatMessageKind.Notice, message);
        }

        /// <summary>Completes a turn for a specific session.</summary>
        private void CompleteNativeTurnForSession(NativeChatSessionState session, AgentEvent agentEvent)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            FinishStreamingMessages(session);
            session.ChatTranscript.SetBusy(false);

            // One turn, one firing: cleared here so a stray end-of-turn event can't run it twice.
            AgentFinishConfig finishConfig = session.TurnFinishConfig;
            session.TurnFinishConfig = null;

            if (agentEvent.Usage != null)
            {
                session.TurnOutputTokens = agentEvent.Usage.OutputTokens;
                session.TurnInputTokens = agentEvent.Usage.InputTokens;
            }

            if (agentEvent.WasInterrupted)
            {
                AddNativeMessageToSession(session, ChatMessageKind.Notice, "Turn interrupted.");
            }

            if (agentEvent.PermissionDenials != null && agentEvent.PermissionDenials.Count > 0)
            {
                var names = new List<string>();
                foreach (AgentPermissionDenial denial in agentEvent.PermissionDenials)
                {
                    names.Add(string.IsNullOrEmpty(denial.ToolName) ? "tool" : denial.ToolName);
                }

                AddNativeMessageToSession(session, ChatMessageKind.Notice,
                    $"Blocked for lack of permission: {string.Join(", ", names)}. " +
                    "Enable \"Skip permissions\" in the agent menu to allow these tools.");
            }

            // Read before EndActivity, which stops the clock this falls back to.
            TimeSpan elapsed = ResolveTurnDurationForSession(session, agentEvent.Usage);
            bool wasInFlight = session.TurnInFlight;
            session.TurnInFlight = false;

            // Stops the spinner and the running clock. Without this the tab shows "Puzzling... 51s"
            // forever even though the turn already ended.
            session.ChatTranscript.EndActivity(string.Empty);

            if (wasInFlight)
            {
                AddNativeMessageToSession(session, ChatMessageKind.Notice,
                    FormatTurnFooter(
                        agentEvent.Usage,
                        elapsed,
                        agentEvent.WasInterrupted,
                        SupportsQueuedCodexNativeChat(session.SelectedProvider)));
            }

            FireNativeAgentFinish(finishConfig, agentEvent);
        }

        /// <summary>Turn duration scoped to one session: the CLI's own figure, else that tab's clock.</summary>
        private TimeSpan ResolveTurnDurationForSession(NativeChatSessionState session, AgentUsage usage)
        {
            if (usage != null && usage.DurationMs > 0)
            {
                return TimeSpan.FromMilliseconds(usage.DurationMs);
            }

            TimeSpan onScreen = session.ChatTranscript != null
                ? session.ChatTranscript.ActivityElapsed
                : TimeSpan.Zero;

            return onScreen > TimeSpan.Zero ? onScreen : DateTime.UtcNow - session.TurnStartedUtc;
        }

        /// <summary>Finishes streaming messages in a specific session.</summary>
        private void FinishStreamingMessages(NativeChatSessionState session)
        {
            // Complete() — not just IsStreaming = false: it is what hands the buffered text to the
            // markdown view, so a row closed without it stays blank.
            if (session.StreamingAssistantMessage != null)
            {
                session.StreamingAssistantMessage.Complete();
                session.StreamingAssistantMessage = null;
            }

            if (session.StreamingThinkingMessage != null)
            {
                session.StreamingThinkingMessage.Complete();
                session.StreamingThinkingMessage = null;
            }
        }

        #region Session Management Helpers

        /// <summary>Creates a new session and registers it (future: for parallel session support).</summary>
        internal NativeChatSessionState CreateAndRegisterSession(AiProvider provider, string workspace)
        {
            _sessionIdCounter++;
            string sessionId = $"session_{_sessionIdCounter}";
            int windowId = _sessionIdCounter;  // Use session counter as window ID (0 is first, 1 is second, etc.)

            // Determine model for this session
            string sessionModel = GetSelectedProviderModelId(provider);

            // Create agent session with the correct model
            var agentSession = CreateAgentSession(provider, workspace, sessionModel);
            if (agentSession == null)
                return null;

            // Create new transcript UI
            var transcript = new ChatTranscriptView();

            // Bundle into session state with window ID
            var state = new NativeChatSessionState(sessionId, agentSession, transcript, windowId);
            state.SelectedProvider = provider;
            state.SelectedModel = sessionModel;
            state.SelectedEffortLevel = _settings.SelectedEffortLevel;

            // Register event handler (closure captures sessionId and state)
            EventHandler<AgentEvent> handler = (sender, agentEvent) =>
            {
                if (!ReferenceEquals(sender, agentSession))
                    return;

#pragma warning disable VSSDK007, VSTHRD110 // Intentionally fire-and-forget; events arrive on the reader thread
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try
                    {
                        ApplyAgentEventToSession(sessionId, state, agentEvent);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Native mode: failed to apply event to session {sessionId}: {ex}");
                    }
                }).FileAndForget("claudecode/nativemode/event");
#pragma warning restore VSSDK007, VSTHRD110
            };

            state.EventHandler = handler;
            agentSession.Received += handler;

            // Register in sessions dict
            lock (_sessionLock)
            {
                _nativeSessions[sessionId] = state;
                _activeSessionId = sessionId;
            }

            return state;
        }

        #endregion

        #endregion
    }
}
