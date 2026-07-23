/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Auto-send runtime errors to agent" feature (issue #110). Companion to the
 *          "auto-send build errors" feature: when the opt-in setting is enabled, this
 *          subscribes to the Visual Studio debugger's break events and, whenever execution
 *          breaks on an UNHANDLED runtime exception, collects the exception (type, message,
 *          and stack trace) and sends a formatted "please fix this" prompt to the active code
 *          agent's terminal automatically.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Fields

        /// <summary>
        /// VS debugger-event sink. Held in a field so the COM event subscription isn't garbage
        /// collected for the lifetime of the control (DTE event objects are otherwise collectible).
        /// </summary>
        private EnvDTE.DebuggerEvents _debuggerEvents;

        /// <summary>Guards against overlapping auto-sends while one is in flight.</summary>
        private bool _autoSendRuntimeErrorsInProgress;

        /// <summary>
        /// Signature of the last runtime error that was auto-sent, so an identical exception
        /// breaking again back-to-back isn't resent (which would spam the terminal).
        /// </summary>
        private string _lastAutoSentRuntimeErrorSignature;

        #endregion

        #region Subscribe / Unsubscribe

        /// <summary>
        /// Subscribes to Visual Studio debugger break events (once). The subscription is always
        /// established regardless of the setting; the setting is checked at fire time, so the user
        /// can toggle it live without re-subscribing.
        /// </summary>
        private void InitializeRuntimeErrorAutoSend()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_debuggerEvents != null) return;

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Events == null) return;

                _debuggerEvents = dte.Events.DebuggerEvents;
                _debuggerEvents.OnEnterBreakMode += OnDebuggerEnterBreakMode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeRuntimeErrorAutoSend error: {ex.Message}");
            }
        }

        /// <summary>Unsubscribes from debugger events. Called during control cleanup.</summary>
        private void DisposeRuntimeErrorAutoSend()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_debuggerEvents != null)
                {
                    _debuggerEvents.OnEnterBreakMode -= OnDebuggerEnterBreakMode;
                    _debuggerEvents = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DisposeRuntimeErrorAutoSend error: {ex.Message}");
            }
        }

        #endregion

        #region Break-mode handling

        /// <summary>
        /// Fires (on the UI thread) whenever the debugger enters break mode. Only acts when the
        /// break is caused by an UNHANDLED exception — ordinary breakpoints, steps, and
        /// first-chance (handled) exceptions are ignored so this doesn't fire on every caught
        /// exception. The exception is read while still in break mode (the only time
        /// <c>$exception</c> is available), then the send is fired asynchronously.
        /// </summary>
        private void OnDebuggerEnterBreakMode(EnvDTE.dbgEventReason reason,
            ref EnvDTE.dbgExecutionAction executionAction)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_settings?.AutoSendRuntimeErrorsToAgent != true) return;

                // Only unhandled exceptions — the ones that actually crash the app. First-chance
                // (dbgEventReasonExceptionThrown) breaks are far too noisy to forward.
                if (reason != EnvDTE.dbgEventReason.dbgEventReasonExceptionNotHandled) return;
                if (_autoSendRuntimeErrorsInProgress) return;

                // Collect synchronously — $exception and the call stack are only valid while the
                // debugger is paused in break mode, which it is right now inside this handler.
                string details = TryCollectRuntimeException();
                if (string.IsNullOrWhiteSpace(details)) return;

                // Skip an identical exception sent back-to-back (e.g. re-running and hitting the
                // same crash) so the terminal isn't spammed.
                if (string.Equals(details, _lastAutoSentRuntimeErrorSignature, StringComparison.Ordinal))
                {
                    return;
                }

                // Nothing to send to if no agent terminal is running.
                if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    Debug.WriteLine("Auto-send runtime errors: no running agent terminal; skipping.");
                    return;
                }

                _lastAutoSentRuntimeErrorSignature = details;
                _autoSendRuntimeErrorsInProgress = true;

                string prompt = FormatRuntimeErrorPrompt(details);

#pragma warning disable VSSDK007 // fire-and-forget; the send switches threads itself
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await SendRuntimeErrorPromptAsync(prompt); }
                    finally { _autoSendRuntimeErrorsInProgress = false; }
                });
#pragma warning restore VSSDK007
            }
            catch (Exception ex)
            {
                _autoSendRuntimeErrorsInProgress = false;
                Debug.WriteLine($"OnDebuggerEnterBreakMode error: {ex.Message}");
            }
        }

        #endregion

        #region Collection + formatting

        /// <summary>
        /// Reads the current unhandled exception from the paused debugger. Prefers
        /// <c>$exception.ToString()</c> (which for a managed exception already includes the type,
        /// message, and real stack trace); falls back to the type + message plus the debugger's
        /// own call stack when the full string isn't available. Returns null when nothing useful
        /// could be read (e.g. native debugging, no <c>$exception</c> in scope).
        /// </summary>
        private string TryCollectRuntimeException()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var dbg = dte?.Debugger;
                if (dbg == null) return null;

                string full = EvalDebuggerExpression(dbg, "$exception.ToString()");
                string type = EvalDebuggerExpression(dbg, "$exception.GetType().FullName");
                string message = EvalDebuggerExpression(dbg, "$exception.Message");

                var sb = new StringBuilder();

                if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(message))
                {
                    sb.Append(string.IsNullOrWhiteSpace(type) ? "Exception" : type);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        sb.Append(": ").Append(message);
                    }
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(full))
                {
                    // Avoid duplicating the "type: message" header when ToString() already leads with it.
                    if (sb.Length == 0 || full.IndexOf(type ?? "\0", StringComparison.Ordinal) != 0)
                    {
                        sb.AppendLine();
                    }
                    sb.AppendLine("Details:");
                    sb.AppendLine(full);
                }
                else
                {
                    // No full ToString() — reconstruct a stack from the debugger's own frames.
                    string stack = TryCollectCallStack(dbg);
                    if (!string.IsNullOrWhiteSpace(stack))
                    {
                        sb.AppendLine();
                        sb.AppendLine("Call stack:");
                        sb.Append(stack);
                    }
                }

                string result = sb.ToString().Trim();
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryCollectRuntimeException error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Evaluates a debugger expression in the current stack frame and returns its value as a
        /// plain string, unwrapping the surrounding quotes/escapes the debugger adds for string
        /// results. Returns null when the expression can't be evaluated.
        /// </summary>
        private static string EvalDebuggerExpression(EnvDTE.Debugger dbg, string expression)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var expr = dbg.GetExpression(expression, false, 2000);
                if (expr != null && expr.IsValidValue)
                {
                    return UnwrapDebuggerString(expr.Value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EvalDebuggerExpression('{expression}') error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// The debugger returns string values wrapped in double quotes with C#-style escapes
        /// (e.g. <c>"line one\r\nline two"</c>). This unwraps them back to readable multi-line text.
        /// Non-string values (numbers, "null") are returned unchanged.
        /// </summary>
        private static string UnwrapDebuggerString(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                string inner = value.Substring(1, value.Length - 2);
                return inner
                    .Replace("\\r\\n", "\n")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\n")
                    .Replace("\\t", "\t")
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
            }

            return value;
        }

        /// <summary>
        /// Builds a compact call stack from the paused thread's frames (function names only),
        /// capped so a very deep stack doesn't flood the prompt. Used only as a fallback when
        /// <c>$exception.ToString()</c> didn't yield a stack trace.
        /// </summary>
        private static string TryCollectCallStack(EnvDTE.Debugger dbg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var thread = dbg.CurrentThread;
                var frames = thread?.StackFrames;
                if (frames == null) return null;

                var sb = new StringBuilder();
                int shown = 0;
                foreach (EnvDTE.StackFrame frame in frames)
                {
                    if (frame == null) continue;
                    string name;
                    try { name = frame.FunctionName; }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    sb.Append("   at ").AppendLine(name);
                    if (++shown >= 25)
                    {
                        sb.AppendLine("   ...");
                        break;
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryCollectCallStack error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds the prompt sent to the agent for a runtime exception.
        /// </summary>
        internal static string FormatRuntimeErrorPrompt(string details)
        {
            var sb = new StringBuilder();
            sb.AppendLine("While running under the Visual Studio debugger, the application hit an " +
                          "unhandled runtime exception. Please investigate the cause and fix it:");
            sb.AppendLine();
            sb.AppendLine(details);
            sb.AppendLine();
            sb.Append("After making changes, run the application again to confirm the exception no longer occurs.");
            return sb.ToString();
        }

        #endregion

        #region Send

        /// <summary>
        /// Delivers a runtime-error prompt to the terminal, mirroring the build-error send path:
        /// generated text over the large-prompt threshold is written to a temp file and sent as a
        /// short "Read and follow: &lt;path&gt;" reference (so a long stack trace doesn't jam the
        /// CLI's bracketed-paste state), and the "On Agent Finish" watcher is armed so the
        /// configured completion action still fires.
        /// </summary>
        private async Task SendRuntimeErrorPromptAsync(string prompt)
        {
            string textToSend = prompt;

            if (prompt.Length > LargeBuildErrorPromptThresholdChars)
            {
                try
                {
                    string sessionDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(sessionDir);
                    string promptFile = Path.Combine(sessionDir, $"runtime-error-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                    File.WriteAllText(promptFile, prompt, new UTF8Encoding(false));

                    bool isWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                         _currentRunningProvider == AiProvider.ClaudeCodeWSL ||
                                         _currentRunningProvider == AiProvider.CursorAgent ||
                                         _currentRunningProvider == AiProvider.Devin;
                    string displayPath = isWSLProvider ? ConvertToWslPath(promptFile) : promptFile;
                    textToSend = $"Read and follow: {displayPath}";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to save runtime-error prompt to file, falling back to inline send: {ex.Message}");
                }
            }

            await SendTextToTerminalAsync(textToSend);

            // Arm the "On Agent Finish" watcher, mirroring the typed-prompt and build-error paths.
            _ = ArmAgentCompletionWatcherAsync();
        }

        #endregion
    }
}
