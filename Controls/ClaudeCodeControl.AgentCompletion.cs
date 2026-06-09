/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "On Agent Finish" feature — detects when the embedded agent stops working by
 *          watching the conhost screen buffer for quiescence (the visible text + cursor
 *          position stop changing), then optionally plays a sound, shows a Visual Studio
 *          info bar (duration, and token count when the provider is Claude), and runs an
 *          action (build / run / tests / a script / a command sent back to the agent).
 *
 *          Console-output quiescence is provider-agnostic: it works for any agent running
 *          in the Command Prompt (conhost) terminal. TUIs that animate a spinner / elapsed
 *          timer while busy read as "changing" and a settled input prompt reads as "idle".
 *          Windows Terminal hosts its buffer in a separate process the console API can't
 *          read, so detection is limited to the Command Prompt terminal type.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Agent Completion Fields

        private const int AgentCompletionPollIntervalMs = 1000;
        private const int AgentCompletionMaxWatchMinutes = 30;

        // While the user is actively typing in the terminal we skip the console read (the brief
        // AttachConsole can disturb keystrokes). "Actively typing" means a key was pressed in the
        // terminal within this window — merely holding focus (e.g. right after a paste) does not
        // count, so detection still fires while the terminal is focused but idle.
        private const int TerminalTypingGuardMs = 1500;
        private DateTime _lastTerminalKeyUtc = DateTime.MinValue;

        private DispatcherTimer _agentCompletionTimer;
        private bool _completionWatchActive;
        private bool _completionTickBusy;
        private bool _consoleSawActivity;
        private int _watchedConsolePid;
        private string _lastConsoleHash;
        private DateTime _promptSentUtc;
        private DateTime _watchStartedUtc;
        private DateTime _lastConsoleChangeUtc;

        // Optional token enrichment — only meaningful for Claude Code transcripts.
        private bool _tokenEnrichmentClaude;
        private string _watchedSessionDir;
        private int _baselineTokenCount;

        // The effective config captured when the watcher armed, so a mid-turn
        // solution switch can't swap the per-project config out from under it.
        private AgentFinishConfig _watchedAgentFinish;

        private readonly object _consoleSnapshotLock = new object();

        // The currently-shown agent-finish info bar, so a newer one can replace it.
        private IVsInfoBarUIElement _activeAgentFinishInfoBar;

        #endregion

        #region Effective Config Resolution

        /// <summary>
        /// Returns the "On Agent Finish" config that applies to the currently open
        /// solution: the per-solution override when one exists for the solution
        /// name, otherwise the global default. Never returns null.
        /// </summary>
        private AgentFinishConfig GetEffectiveAgentFinish()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) _settings = new ClaudeCodeSettings();
            if (_settings.AgentFinish == null) _settings.AgentFinish = new AgentFinishConfig();

            string name = GetCurrentSolutionName();
            if (!string.IsNullOrEmpty(name)
                && _settings.ProjectAgentFinish != null
                && _settings.ProjectAgentFinish.TryGetValue(name, out var projectCfg)
                && projectCfg != null)
            {
                return projectCfg;
            }

            return _settings.AgentFinish;
        }

        /// <summary>
        /// Returns the open solution's name (the .sln file name without extension),
        /// or an empty string when no solution is loaded. Used as the per-project
        /// key for "On Agent Finish" overrides.
        /// </summary>
        private string GetCurrentSolutionName()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string full = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(full))
                {
                    return Path.GetFileNameWithoutExtension(full);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetCurrentSolutionName error: {ex.Message}");
            }
            return string.Empty;
        }

        #endregion

        #region Arm / Disarm

        /// <summary>
        /// Arms the completion watcher after a prompt is sent. No-op unless the feature is
        /// enabled, the Command Prompt terminal is in use, and a console process is running.
        /// Captures the console PID and an initial screen snapshot, then starts the poll timer.
        /// When the running provider is Claude Code, also records a token baseline so the
        /// notification can show how many tokens the turn used. Re-arming resets everything.
        /// </summary>
        private async Task ArmAgentCompletionWatcherAsync()
        {
            try
            {
                if (_settings == null) return;

                // The console screen buffer can only be read for the conhost (Command Prompt).
                if (_settings.SelectedTerminalType != TerminalType.CommandPrompt) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Resolve the effective config (per-solution override or global default)
                // on the UI thread, since the solution name comes from DTE.
                var cfg = GetEffectiveAgentFinish();
                if (cfg == null || !cfg.Enabled) return;

                int pid = 0;
                try { if (cmdProcess != null && !cmdProcess.HasExited) pid = cmdProcess.Id; }
                catch { pid = 0; }
                if (pid == 0) return;

                // Best-effort token baseline (Claude transcripts only).
                bool claude = IsClaudeCodeSessionHistoryProvider(_currentRunningProvider);
                string dir = null;
                int baseTokens = 0;
                if (claude)
                {
                    string workspace = await GetWorkspaceDirectoryAsync();
                    dir = await ResolveSessionDirectoryAsync(_currentRunningProvider.Value, workspace);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string newest = await Task.Run(() => GetNewestJsonl(dir));
                        baseTokens = newest != null ? await Task.Run(() => CountTranscriptTokens(newest)) : 0;
                    }
                }

                string initialHash = await Task.Run(() => TryCaptureConsoleHash(pid));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StopAgentCompletionTimer();

                _watchedConsolePid = pid;
                _lastConsoleHash = initialHash;
                _consoleSawActivity = false;
                _promptSentUtc = DateTime.UtcNow;
                _watchStartedUtc = DateTime.UtcNow;
                _lastConsoleChangeUtc = DateTime.UtcNow;
                _tokenEnrichmentClaude = claude;
                _watchedSessionDir = dir;
                _baselineTokenCount = baseTokens;
                _watchedAgentFinish = cfg;
                _completionWatchActive = true;

                EnsureAgentCompletionTimer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ArmAgentCompletionWatcherAsync error: {ex.Message}");
            }
        }

        private void EnsureAgentCompletionTimer()
        {
            if (_agentCompletionTimer != null) return;

            _agentCompletionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(AgentCompletionPollIntervalMs)
            };
            _agentCompletionTimer.Tick += OnAgentCompletionTimerTick;
            _agentCompletionTimer.Start();
        }

        private void StopAgentCompletionTimer()
        {
            _completionWatchActive = false;
            if (_agentCompletionTimer == null) return;

            _agentCompletionTimer.Stop();
            _agentCompletionTimer.Tick -= OnAgentCompletionTimerTick;
            _agentCompletionTimer = null;
        }

        /// <summary>
        /// Resets the completion watcher and clears any pending notification. Runs on solution
        /// change and before every terminal start (restart button, provider/model switch, theme
        /// restart, session resume). Stopping the watcher before the terminal restarts is
        /// important: otherwise its 1-second console-attach tick can overlap the new terminal
        /// launch and leave Visual Studio attached to the old console, which makes the new
        /// conhost fail to create its window and renders the embedded terminal blank (issue #73).
        /// Also dismisses the agent-finish info bar so a stale "finished" notification from the
        /// previous session doesn't linger.
        /// </summary>
        internal void ResetAgentCompletionWatcher()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StopAgentCompletionTimer();
            DismissAgentFinishNotification();
        }

        /// <summary>
        /// Detaches Visual Studio's own process from any console it may still be attached to.
        /// The completion watcher briefly AttachConsole()s VS to the agent's console to read its
        /// screen buffer; if a FreeConsole() is ever missed (the console torn down mid-read, or a
        /// later AttachConsole skipped because VS was already attached), VS stays attached. A
        /// lingering attachment makes the next conhost.exe we launch fail to create its own window,
        /// leaving the embedded terminal blank. Calling this before each terminal launch clears that
        /// state; it is a harmless no-op when VS has no console. Serialized with the watcher via the
        /// snapshot lock so it can't race an in-flight screen read.
        /// </summary>
        internal void EnsureNoConsoleAttached()
        {
            // Bounded acquire: this runs on the UI thread (terminal launch, Run action). A blocking
            // lock here would freeze Visual Studio whenever a background console capture is mid-read
            // and slow to release — the threading hang users hit. If the lock isn't free, a capture
            // is in flight and will FreeConsole() itself in its own finally, so skipping is safe.
            bool taken = false;
            try
            {
                System.Threading.Monitor.TryEnter(_consoleSnapshotLock, 250, ref taken);
                if (taken)
                {
                    try { FreeConsole(); }
                    catch { }
                }
            }
            finally
            {
                if (taken) System.Threading.Monitor.Exit(_consoleSnapshotLock);
            }
        }

        /// <summary>
        /// Closes the currently-shown agent-finish info bar, if any.
        /// </summary>
        private void DismissAgentFinishNotification()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var bar = _activeAgentFinishInfoBar;
            _activeAgentFinishInfoBar = null;
            if (bar != null)
            {
                try { bar.Close(); }
                catch { }
            }
        }

        #endregion

        #region Detection (console-output quiescence)

        private void OnAgentCompletionTimerTick(object sender, EventArgs e)
        {
            if (_completionTickBusy || !_completionWatchActive) return;
            _completionTickBusy = true;

#pragma warning disable VSSDK007, VSTHRD110 // Intentionally fire-and-forget; reentrancy guarded by _completionTickBusy
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // Hard timeout so a never-settling watch can't poll forever.
                    if ((DateTime.UtcNow - _watchStartedUtc).TotalMinutes >= AgentCompletionMaxWatchMinutes)
                    {
                        Debug.WriteLine("Agent completion watch timed out.");
                        StopAgentCompletionTimer();
                        return;
                    }

                    int pid = _watchedConsolePid;
                    if (pid == 0) { StopAgentCompletionTimer(); return; }

                    // Don't read the console while the user is actively typing in the terminal.
                    // The capture briefly AttachConsole()s VS to the terminal's console, which can
                    // disturb the conhost's keyboard focus/input mid-keystroke (e.g. while the user
                    // answers an agent prompt). Skipping these ticks — and pushing the idle window
                    // forward so detection effectively pauses — keeps typing uninterrupted. The gate
                    // is recent keystrokes, not mere focus: pasting a prompt leaves the terminal
                    // focused but not being typed into, so a focus-only check would wrongly pause the
                    // whole turn and only fire once the user clicked away (the reported ~15s lag).
                    // (We're on the UI thread here, before the Task.Run, which is required for
                    // GetGUIThreadInfo to be meaningful.)
                    if ((DateTime.UtcNow - _lastTerminalKeyUtc).TotalMilliseconds < TerminalTypingGuardMs
                        && IsTerminalFocused())
                    {
                        _lastConsoleChangeUtc = DateTime.UtcNow;
                        return;
                    }

                    string text = await Task.Run(() => TryCaptureConsoleText(pid));
                    string hash = text != null ? ComputeStableHash(text) : null;
                    if (hash == null)
                    {
                        // Read failed — if the console process is gone, the terminal was
                        // closed or restarted, so disarm. Otherwise just skip this tick.
                        if (!IsProcessAlive(pid)) StopAgentCompletionTimer();
                        return;
                    }

                    if (_lastConsoleHash == null || !string.Equals(hash, _lastConsoleHash, StringComparison.Ordinal))
                    {
                        // Screen changed → agent is still working.
                        _lastConsoleHash = hash;
                        _lastConsoleChangeUtc = DateTime.UtcNow;
                        _consoleSawActivity = true;
                        return;
                    }

                    // Don't fire until the agent actually produced output this turn.
                    if (!_consoleSawActivity) return;

                    int idle = Math.Max(2, Math.Min(120, _watchedAgentFinish?.IdleSeconds ?? 3));
                    if ((DateTime.UtcNow - _lastConsoleChangeUtc).TotalSeconds < idle) return;

                    // Settled — but a static y/n or selection prompt also reads as idle. If the
                    // screen looks like the agent is waiting for input, this is not a completion:
                    // don't fire and keep watching, so the notification lands on the real finish
                    // after the user answers (the screen will change → activity → re-evaluate).
                    if (LooksLikeAgentInputPrompt(text)) return;

                    // Settled — the turn is done.
                    var cfg = _watchedAgentFinish;
                    if (cfg == null) { StopAgentCompletionTimer(); return; }

                    int delta = 0;
                    if (_tokenEnrichmentClaude && !string.IsNullOrEmpty(_watchedSessionDir))
                    {
                        string newest = await Task.Run(() => GetNewestJsonl(_watchedSessionDir));
                        if (newest != null)
                        {
                            int final = await Task.Run(() => CountTranscriptTokens(newest));
                            delta = Math.Max(0, final - _baselineTokenCount);
                        }
                    }

                    TimeSpan dur = DateTime.UtcNow - _promptSentUtc;
                    StopAgentCompletionTimer();
                    await OnAgentTurnCompletedAsync(cfg, dur, delta);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OnAgentCompletionTimerTick error: {ex.Message}");
                }
                finally
                {
                    _completionTickBusy = false;
                }
            }).FileAndForget("claudecode/agentfinish/tick");
#pragma warning restore VSSDK007, VSTHRD110
        }

        /// <summary>
        /// Convenience wrapper: captures the console text and returns its stable hash
        /// (or null on any failure). Used by the arm path which only needs a baseline hash.
        /// </summary>
        private string TryCaptureConsoleHash(int conhostPid)
        {
            string text = TryCaptureConsoleText(conhostPid);
            return text != null ? ComputeStableHash(text) : null;
        }

        /// <summary>
        /// Attaches to the target process's console, reads the visible screen-buffer text and
        /// cursor position, and returns the raw captured string (or null on any failure). The
        /// attach/read/detach is serialized and tightly scoped so it doesn't linger attached
        /// to another process's console. A moving spinner / elapsed timer changes the text and
        /// a moving cursor changes the position, so "busy" always differs from a settled prompt.
        /// The trailing "|cursorX,cursorY" marker lets cursor movement count as activity.
        /// </summary>
        private string TryCaptureConsoleText(int conhostPid)
        {
            if (conhostPid <= 0) return null;

            // The terminal is launched as conhost.exe; AttachConsole needs a console *client*
            // (the cmd.exe running inside it), not the conhost host PID. Resolve it fresh each
            // sample so a terminal restart can't leave us pinned to a dead PID.
            int clientPid = ResolveConsoleClientPid(conhostPid);
            if (clientPid <= 0) return null;

            lock (_consoleSnapshotLock)
            {
                IntPtr handle = IntPtr.Zero;
                bool attached = false;
                bool ctrlGuarded = false;
                try
                {
                    // Shield VS from console signals: while attached, a Ctrl+C the user sends to
                    // interrupt the agent would otherwise be delivered to VS too (default handler
                    // = terminate). Ignoring it for the brief attach window prevents that.
                    SetConsoleCtrlHandler(IntPtr.Zero, true);
                    ctrlGuarded = true;

                    // Do NOT FreeConsole defensively first — that could detach another part of
                    // the extension (e.g. an in-flight paste) from its console. If attach fails
                    // because something else holds one, we simply skip this sample.
                    if (!AttachConsole((uint)clientPid))
                    {
                        Debug.WriteLine($"AttachConsole({clientPid}) failed, Win32={Marshal.GetLastWin32Error()}");
                        return null;
                    }
                    attached = true;

                    handle = CreateFile("CONOUT$",
                        GENERIC_READ_CONSOLE | GENERIC_WRITE_CONSOLE,
                        FILE_SHARE_READ_CONSOLE | FILE_SHARE_WRITE_CONSOLE,
                        IntPtr.Zero, OPEN_EXISTING_CONSOLE, 0, IntPtr.Zero);
                    if (handle.ToInt64() == -1 || handle == IntPtr.Zero) return null;

                    if (!GetConsoleScreenBufferInfo(handle, out CONSOLE_SCREEN_BUFFER_INFO csbi)) return null;

                    int left = csbi.srWindow.Left;
                    int top = csbi.srWindow.Top;
                    int right = csbi.srWindow.Right;
                    int bottom = csbi.srWindow.Bottom;
                    int width = right - left + 1;
                    int height = bottom - top + 1;
                    if (width <= 0 || height <= 0 || width > 1000 || height > 1000) return null;

                    var sb = new StringBuilder(width * height + 16);
                    var row = new char[width];
                    for (short y = (short)top; y <= bottom; y++)
                    {
                        var coord = new COORD { X = (short)left, Y = y };
                        if (ReadConsoleOutputCharacter(handle, row, (uint)width, coord, out uint read))
                        {
                            sb.Append(row, 0, (int)read);
                            sb.Append('\n');
                        }
                    }
                    // Fold in the cursor position so cursor movement counts as activity.
                    sb.Append('|').Append(csbi.dwCursorPosition.X).Append(',').Append(csbi.dwCursorPosition.Y);

                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"TryCaptureConsoleText error: {ex.Message}");
                    return null;
                }
                finally
                {
                    if (handle != IntPtr.Zero && handle.ToInt64() != -1) CloseHandle(handle);
                    if (attached) FreeConsole();
                    if (ctrlGuarded) SetConsoleCtrlHandler(IntPtr.Zero, false);
                }
            }
        }

        // High-precision markers that the agent is waiting on a yes/no or "press key" prompt.
        // Kept deliberately strong to avoid mis-classifying a genuine completion (which would
        // silently skip its notification). Compared case-insensitively against trimmed tail lines.
        private static readonly string[] AgentPromptKeywords =
        {
            "(y/n)", "[y/n]", "y/n]", "(yes/no)", "[yes/no]",
            "do you want to", "do you trust", "allow this", "allow command",
            "press enter to continue", "press any key",
        };

        /// <summary>
        /// Heuristically decides whether the settled console screen shows the agent waiting for
        /// input (a y/n confirmation or a numbered selection menu, e.g. Claude Code's permission
        /// box) rather than a finished turn. Only the bottom of the screen is examined, since
        /// prompts render there. Intentionally conservative: it would rather miss an unusual
        /// prompt than suppress a real completion. Provider-agnostic but tuned for Claude Code.
        /// </summary>
        private static bool LooksLikeAgentInputPrompt(string screenText)
        {
            if (string.IsNullOrEmpty(screenText)) return false;

            // Drop the trailing "|cursorX,cursorY" marker the capture appends.
            int bar = screenText.LastIndexOf('|');
            string body = bar >= 0 ? screenText.Substring(0, bar) : screenText;

            string[] lines = body.Split('\n');
            int start = Math.Max(0, lines.Length - 18); // prompts sit at the bottom

            bool sawArrow = false;
            int menuItems = 0;

            for (int i = start; i < lines.Length; i++)
            {
                string raw = lines[i];
                string trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;

                string lower = trimmed.ToLowerInvariant();
                foreach (string kw in AgentPromptKeywords)
                {
                    if (lower.IndexOf(kw, StringComparison.Ordinal) >= 0) return true;
                }

                // Selection cursor used by Claude Code's permission / choice prompts.
                if (raw.IndexOf('❯') >= 0 || raw.IndexOf('›') >= 0 || raw.IndexOf('▶') >= 0)
                    sawArrow = true;

                // Numbered option line: "1. Yes", "❯ 2. No", "3) ...".
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[❯›▶\s]*\d+[\.\)]\s+\S"))
                    menuItems++;
            }

            // An arrow-marked menu, or two-plus numbered options together, is an interactive choice.
            if (sawArrow && menuItems >= 1) return true;
            if (menuItems >= 2) return true;

            return false;
        }

        /// <summary>
        /// Resolves a console *client* PID (the cmd.exe running inside the conhost) suitable for
        /// AttachConsole. The embedded terminal window's PID is the console client (cmd.exe), not
        /// conhost, due to Windows back-compat; falls back to the conhost's first child process.
        /// </summary>
        private int ResolveConsoleClientPid(int conhostPid)
        {
            try
            {
                IntPtr h = terminalHandle;
                if (h != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(h, out uint wpid);
                    if (wpid != 0 && wpid != (uint)conhostPid) return (int)wpid;
                }
                foreach (uint child in GetChildProcessIds((uint)conhostPid))
                {
                    return (int)child;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResolveConsoleClientPid error: {ex.Message}");
            }
            return 0;
        }

        /// <summary>FNV-1a 64-bit — process-independent and collision-resistant enough for snapshot comparison.</summary>
        private static string ComputeStableHash(string s)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x");
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) { return !p.HasExited; }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns the newest <c>*.jsonl</c> transcript in the session directory, or null (Claude token enrichment only).</summary>
        private static string GetNewestJsonl(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir)
                    .GetFiles("*.jsonl")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Sums input + output tokens across every assistant entry (Claude token enrichment only).</summary>
        private static int CountTranscriptTokens(string file)
        {
            int total = 0;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        JObject o;
                        try { o = JObject.Parse(line); }
                        catch { continue; }
                        if ((string)o["type"] != "assistant") continue;
                        var usage = o["message"]?["usage"];
                        if (usage != null)
                        {
                            total += ((int?)usage["input_tokens"] ?? 0) + ((int?)usage["output_tokens"] ?? 0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CountTranscriptTokens error: {ex.Message}");
            }
            return total;
        }

        #endregion

        #region Completion Handling (notify + action)

        private async Task OnAgentTurnCompletedAsync(AgentFinishConfig cfg, TimeSpan duration, int tokenDelta)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (cfg.PlaySound) PlayFinishSound();

                string summary = "Agent finished · " + FormatDuration(duration)
                    + (tokenDelta > 0 ? $" · +{tokenDelta:N0} tokens" : string.Empty);

                bool hasAction = cfg.Action != AgentFinishActionType.None;
                if (!hasAction)
                {
                    if (cfg.ShowToast) await ShowAgentFinishNotificationAsync(summary, null, null);
                    return;
                }

                // Optionally gate the action on the agent having changed files.
                if (cfg.RequireFileChanges)
                {
                    bool changed = await HasWorkingTreeChangesAsync();
                    if (!changed)
                    {
                        if (cfg.ShowToast)
                            await ShowAgentFinishNotificationAsync(summary + " · no file changes — action skipped", null, null);
                        return;
                    }
                }

                string actionLabel = DescribeAction(cfg);
                if (cfg.Confirm)
                {
                    // Confirmation needed → always surface the button (regardless of ShowToast).
                    await ShowAgentFinishNotificationAsync(summary, actionLabel, () => ExecuteAgentFinishActionAsync(cfg));
                }
                else
                {
                    if (cfg.ShowToast)
                        await ShowAgentFinishNotificationAsync($"{summary} · {actionLabel}", null, null);
                    await ExecuteAgentFinishActionAsync(cfg);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnAgentTurnCompletedAsync error: {ex.Message}");
            }
        }

        private async Task ExecuteAgentFinishActionAsync(AgentFinishConfig cfg)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Belt-and-suspenders: guarantee VS isn't still attached to the agent's console before
            // launching/building. A leaked attachment makes the debuggee's own console allocation
            // conflict and can hang VS when the action is Run.
            EnsureNoConsoleAttached();

            try
            {
                switch (cfg.Action)
                {
                    case AgentFinishActionType.BuildSolution:       ExecuteDteCommand("Build.BuildSolution"); break;
                    case AgentFinishActionType.RebuildSolution:     ExecuteDteCommand("Build.RebuildSolution"); break;
                    case AgentFinishActionType.Run:                 ExecuteDteCommand("Debug.Start"); break;
                    case AgentFinishActionType.RunWithoutDebugging: ExecuteDteCommand("Debug.StartWithoutDebugging"); break;
                    case AgentFinishActionType.RunTests:            ExecuteDteCommand("TestExplorer.RunAllTests"); break;
                    case AgentFinishActionType.RunScript:           await RunFinishScriptAsync(cfg.ScriptOrCommand); break;
                    case AgentFinishActionType.SendToAgent:
                        if (!string.IsNullOrWhiteSpace(cfg.ScriptOrCommand))
                            await SendTextToTerminalAsync(cfg.ScriptOrCommand);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExecuteAgentFinishActionAsync error: {ex.Message}");
            }
        }

        private static void ExecuteDteCommand(string command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ExecuteCommand(command);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DTE command '{command}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Launches a script (deploy.cmd etc.) in the workspace directory. Relative paths
        /// are resolved against the workspace. UseShellExecute lets a .cmd/.bat open its own
        /// console so the user can watch its output.
        /// </summary>
        private async Task RunFinishScriptAsync(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return;

            string workspace = await GetWorkspaceDirectoryAsync();
            try
            {
                string path = script.Trim().Trim('"');
                if (!Path.IsPathRooted(path))
                {
                    string combined = Path.Combine(workspace ?? string.Empty, path);
                    if (File.Exists(combined)) path = combined;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = workspace,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RunFinishScriptAsync error: {ex.Message}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to run the On-Agent-Finish script:\n\n{script}\n\n{ex.Message}",
                    "On Agent Finish", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// True when the workspace git working tree has uncommitted changes. When the
        /// workspace is not a git repo (or git is unavailable) returns true so the
        /// "only if files changed" gate never blocks a non-git project.
        /// </summary>
        private async Task<bool> HasWorkingTreeChangesAsync()
        {
            try
            {
                string root = _gitRepositoryRoot;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return true;

                return await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "git",
                            Arguments = "status --porcelain",
                            WorkingDirectory = root,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        using (var p = Process.Start(psi))
                        {
                            string outp = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(5000);
                            return !string.IsNullOrWhiteSpace(outp);
                        }
                    }
                    catch
                    {
                        return true;
                    }
                });
            }
            catch
            {
                return true;
            }
        }

        private static string DescribeAction(AgentFinishConfig cfg)
        {
            switch (cfg.Action)
            {
                case AgentFinishActionType.BuildSolution: return "Build solution";
                case AgentFinishActionType.RebuildSolution: return "Rebuild solution";
                case AgentFinishActionType.Run: return "Run (F5)";
                case AgentFinishActionType.RunWithoutDebugging: return "Run without debugging";
                case AgentFinishActionType.RunTests: return "Run all tests";
                case AgentFinishActionType.RunScript:
                    string s = cfg.ScriptOrCommand?.Trim().Trim('"');
                    return string.IsNullOrEmpty(s) ? "Run script" : $"Run {Path.GetFileName(s)}";
                case AgentFinishActionType.SendToAgent:
                    string c = cfg.ScriptOrCommand?.Trim();
                    return string.IsNullOrEmpty(c)
                        ? "Send command"
                        : $"Send {(c.Length > 24 ? c.Substring(0, 24) + "…" : c)}";
                default: return string.Empty;
            }
        }

        private static string FormatDuration(TimeSpan d)
        {
            if (d.TotalSeconds < 60) return $"{Math.Max(1, (int)d.TotalSeconds)}s";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
            return $"{(int)d.TotalHours}h {d.Minutes}m";
        }

        /// <summary>
        /// Plays a short two-tone chime via Win32 Beep on a background thread (Beep blocks for the
        /// tone duration). Beep is used instead of SystemSounds because it is independent of the
        /// Windows sound scheme — SystemSounds is silent when the scheme event is set to "None".
        /// </summary>
        private static void PlayFinishSound()
        {
#pragma warning disable VSTHRD110 // Intentional fire-and-forget; the chime must not block the caller
            _ = Task.Run(() =>
            {
                try
                {
                    Beep(784, 140);   // G5
                    Beep(1047, 180);  // C6
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlayFinishSound error: {ex.Message}");
                }
            });
#pragma warning restore VSTHRD110
        }

        #endregion

        #region Notification (VS info bar)

        /// <summary>
        /// Shows a Visual Studio info bar on the main window. When <paramref name="actionLabel"/>
        /// is non-null it renders as a hyperlink that runs <paramref name="onAction"/> on click.
        /// Shows even when our tool window is hidden, which is the point for long tasks.
        /// </summary>
        private async Task ShowAgentFinishNotificationAsync(string text, string actionLabel, Func<Task> onAction)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var shell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
                var factory = Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
                if (shell == null || factory == null) return;

                shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj);
                var host = hostObj as IVsInfoBarHost;
                if (host == null)
                {
                    Debug.WriteLine("Main window info bar host unavailable; skipping toast.");
                    return;
                }

                var spans = new[] { new InfoBarTextSpan(text) };
                var actionItems = string.IsNullOrEmpty(actionLabel)
                    ? new IVsInfoBarActionItem[0]
                    : new IVsInfoBarActionItem[] { new InfoBarHyperlink(actionLabel) };

                var model = new InfoBarModel(spans, actionItems, KnownMonikers.StatusInformation, isCloseButtonVisible: true);

                IVsInfoBarUIElement element = factory.CreateInfoBar(model);
                var events = new AgentFinishInfoBarEvents(onAction, () =>
                {
                    if (ReferenceEquals(_activeAgentFinishInfoBar, element)) _activeAgentFinishInfoBar = null;
                });
                element.Advise(events, out uint cookie);
                events.Cookie = cookie;

                // Show the new bar, then close the previous one so only the latest is visible.
                // (Order matters: set the field first so the previous bar's OnClosed callback,
                // which checks reference-equality, won't clear the new one.)
                var previous = _activeAgentFinishInfoBar;
                host.AddInfoBar(element);
                _activeAgentFinishInfoBar = element;
                if (previous != null)
                {
                    try { previous.Close(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowAgentFinishNotificationAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles info bar lifetime: unadvises on close, runs the (optional) action on click.
        /// </summary>
        private sealed class AgentFinishInfoBarEvents : IVsInfoBarUIEvents
        {
            private readonly Func<Task> _onAction;
            private readonly Action _onClosed;
            public uint Cookie;

            public AgentFinishInfoBarEvents(Func<Task> onAction, Action onClosed)
            {
                _onAction = onAction;
                _onClosed = onClosed;
            }

            public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { infoBarUIElement.Unadvise(Cookie); }
                catch { }
                try { _onClosed?.Invoke(); }
                catch { }
            }

            public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_onAction != null)
                {
#pragma warning disable VSSDK007, VSTHRD110 // Intentional fire-and-forget from a UI event
                    ThreadHelper.JoinableTaskFactory.RunAsync(async () => await _onAction())
                        .FileAndForget("claudecode/agentfinish/action");
#pragma warning restore VSSDK007, VSTHRD110
                }
                try { infoBarUIElement.Close(); }
                catch { }
            }
        }

        #endregion
    }
}
