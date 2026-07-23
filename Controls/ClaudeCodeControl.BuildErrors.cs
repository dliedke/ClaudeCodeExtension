/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Auto-send build errors to agent" feature. When the opt-in setting is enabled,
 *          this subscribes to Visual Studio build-completion events and, whenever a build
 *          finishes with one or more errors, collects the errors (and warnings, for context)
 *          from the Error List and sends a formatted "please fix these" prompt to the active
 *          code agent's terminal automatically.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Fields

        /// <summary>
        /// VS build-event sink. Held in a field so the COM event subscription isn't garbage
        /// collected for the lifetime of the control (DTE event objects are otherwise collectible).
        /// </summary>
        private EnvDTE.BuildEvents _buildEvents;

        /// <summary>Guards against overlapping auto-sends while one is in flight.</summary>
        private bool _autoSendBuildErrorsInProgress;

        /// <summary>
        /// Signature of the last error set that was auto-sent. Used to skip resending an
        /// identical set back-to-back (e.g. an "On Agent Finish → Build" loop where the agent
        /// made no progress), which would otherwise spam the terminal. Cleared after any build
        /// that produces no errors, so a later identical failure is sent again.
        /// </summary>
        private string _lastAutoSentBuildErrorSignature;

        /// <summary>Maximum number of error lines included in the auto-send prompt.</summary>
        private const int MaxAutoSendErrors = 40;

        /// <summary>Maximum number of warning lines included in the auto-send prompt.</summary>
        private const int MaxAutoSendWarnings = 20;

        #endregion

        #region Subscribe / Unsubscribe

        /// <summary>
        /// Subscribes to Visual Studio build-completion events (once). The subscription is
        /// always established regardless of the setting; the setting is checked at fire time,
        /// so the user can toggle it live without re-subscribing.
        /// </summary>
        private void InitializeBuildErrorAutoSend()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_buildEvents != null) return;

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Events == null) return;

                _buildEvents = dte.Events.BuildEvents;
                _buildEvents.OnBuildDone += OnSolutionBuildDone;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeBuildErrorAutoSend error: {ex.Message}");
            }
        }

        /// <summary>Unsubscribes from build events. Called during control cleanup.</summary>
        private void DisposeBuildErrorAutoSend()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_buildEvents != null)
                {
                    _buildEvents.OnBuildDone -= OnSolutionBuildDone;
                    _buildEvents = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DisposeBuildErrorAutoSend error: {ex.Message}");
            }
        }

        #endregion

        #region Build-done handling

        /// <summary>
        /// Fires (on the UI thread) after any solution/project build finishes. Only acts on a
        /// Build or Rebuild action when the opt-in setting is enabled — Clean/Deploy are ignored.
        /// </summary>
        private void OnSolutionBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            try
            {
                if (_settings?.AutoSendBuildErrorsToAgent != true) return;
                if (action != EnvDTE.vsBuildAction.vsBuildActionBuild &&
                    action != EnvDTE.vsBuildAction.vsBuildActionRebuildAll)
                {
                    return;
                }
                if (_autoSendBuildErrorsInProgress) return;

#pragma warning disable VSSDK007 // fire-and-forget; the handler switches to the UI thread itself
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(HandleBuildDoneAndMaybeSendAsync);
#pragma warning restore VSSDK007
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnSolutionBuildDone error: {ex.Message}");
            }
        }

        /// <summary>
        /// Collects the build errors (and warnings) and, when there is at least one error and an
        /// agent terminal is running, sends a formatted fix request to the agent.
        /// </summary>
        private async Task HandleBuildDoneAndMaybeSendAsync()
        {
            if (_autoSendBuildErrorsInProgress) return;
            _autoSendBuildErrorsInProgress = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Re-check the setting (it may have been toggled off since the build started).
                if (_settings?.AutoSendBuildErrorsToAgent != true) return;

                // Give the Error List a brief moment to finish populating after OnBuildDone.
                await Task.Delay(250);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var errors = new List<string>();
                var warnings = new List<string>();
                TryCollectBuildErrors(errors, warnings);

                // Only auto-send when the build actually failed with errors. A successful build
                // (or warnings-only) clears the dedupe signature so a later identical failure is
                // resent.
                if (errors.Count == 0)
                {
                    _lastAutoSentBuildErrorSignature = null;
                    return;
                }

                // Nothing to send to if no agent terminal is running.
                if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    Debug.WriteLine("Auto-send build errors: no running agent terminal; skipping.");
                    return;
                }

                // Skip an identical error set sent back-to-back (breaks no-progress build loops).
                string signature = string.Join("\n", errors);
                if (string.Equals(signature, _lastAutoSentBuildErrorSignature, StringComparison.Ordinal))
                {
                    Debug.WriteLine("Auto-send build errors: identical error set already sent; skipping.");
                    return;
                }
                _lastAutoSentBuildErrorSignature = signature;

                string prompt = FormatBuildErrorPrompt(errors, warnings);
                await SendBuildErrorPromptAsync(prompt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleBuildDoneAndMaybeSendAsync error: {ex.Message}");
            }
            finally
            {
                _autoSendBuildErrorsInProgress = false;
            }
        }

        #endregion

        #region Manual send (toolbar button)

        /// <summary>
        /// Toolbar/menu handler for the "Send Build Errors to Agent" one-click feature. Collects
        /// the current Error List contents and sends them to the running agent, regardless of the
        /// "Auto-send build errors to agent" setting — this is an explicit, on-demand action.
        /// Plain <c>async void</c> (like <c>CustomCommandMenuItem_Click</c>) rather than a blocking
        /// <c>JoinableTaskFactory.Run</c> wrapper: Windows Terminal's paste is delivered via a
        /// foreground-focus-stealing Ctrl+Shift+V <c>keybd_event</c> (see
        /// <c>PasteViaCtrlShiftVAsync</c>), and running that inside a nested, reentrant message pump
        /// was found to disrupt the focus/activation timing it depends on — Command Prompt's
        /// window-handle-targeted <c>PostMessage</c> paste isn't affected, which is why the button
        /// worked there but silently did nothing under Windows Terminal.
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void SendBuildErrorsButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var errors = new List<string>();
                var warnings = new List<string>();
                TryCollectBuildErrors(errors, warnings);

                if (errors.Count == 0)
                {
                    MessageBox.Show(
                        "There are no build errors in the Error List to send.",
                        "Send Build Errors to Agent",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    MessageBox.Show(
                        "No agent terminal is running. Start an agent session before sending build errors.",
                        "Send Build Errors to Agent",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string signature = string.Join("\n", errors);
                _lastAutoSentBuildErrorSignature = signature;

                string prompt = FormatBuildErrorPrompt(errors, warnings);
                await SendBuildErrorPromptAsync(prompt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SendBuildErrorsButton_Click error: {ex.Message}");
            }
        }

        /// <summary>Same threshold the prompt box uses for its "Send large prompts as file" opt-in (see UserInput.cs).</summary>
        private const int LargeBuildErrorPromptThresholdChars = 1024;

        /// <summary>
        /// Delivers a build-error prompt to the terminal. Unlike a typed prompt, this text is
        /// generated, not authored, and a handful of errors with full relative paths easily clears a
        /// couple KB — pasting that much in one raw clipboard paste risks leaving the CLI's
        /// bracketed-paste state stuck mid-transfer (the terminal shows "Pasting..." and never
        /// receives the rest). There's no downside to a file reference for generated text, so this
        /// path always writes prompts over the threshold to a temp file and sends a short
        /// "Read and follow: &lt;path&gt;" reference instead — the same mechanism the prompt box uses when
        /// the user opts into "Send large prompts as file", but unconditional here since the user
        /// never sees or edits this text directly. Also arms the "On Agent Finish" completion
        /// watcher (same as a normal typed prompt via SendButton_Click) so the notify/action
        /// configured there fires once the agent finishes fixing these errors — this send bypasses
        /// SendButton_Click entirely, so without this call the watcher would never arm.
        /// </summary>
        private async Task SendBuildErrorPromptAsync(string prompt)
        {
            string textToSend = prompt;

            if (prompt.Length > LargeBuildErrorPromptThresholdChars)
            {
                try
                {
                    string sessionDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(sessionDir);
                    string promptFile = Path.Combine(sessionDir, $"build-errors-{DateTime.Now:yyyyMMdd-HHmmss}.md");
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
                    Debug.WriteLine($"Failed to save build-error prompt to file, falling back to inline send: {ex.Message}");
                }
            }

            await SendTextToTerminalAsync(textToSend);

            // Arm the "On Agent Finish" watcher, mirroring the typed-prompt send path
            // (SendButton_Click). Without this the feature never fires for a build-error send.
            _ = ArmAgentCompletionWatcherAsync();
        }

        #endregion

        #region Collection + formatting

        /// <summary>
        /// Reads the Error List and fills <paramref name="errors"/> and <paramref name="warnings"/>
        /// with formatted, solution-relative lines. Errors are <c>vsBuildErrorLevelHigh</c>,
        /// warnings are <c>vsBuildErrorLevelMedium</c>; lower-level messages are ignored.
        /// </summary>
        private void TryCollectBuildErrors(List<string> errors, List<string> warnings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte2 = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                var errorList = dte2?.ToolWindows?.ErrorList;
                var items = errorList?.ErrorItems;
                if (items == null) return;

                int count = items.Count;
                for (int i = 1; i <= count; i++)
                {
                    EnvDTE80.ErrorItem item;
                    try { item = items.Item(i); }
                    catch { continue; }
                    if (item == null) continue;

                    string line = FormatErrorItem(item);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    switch (item.ErrorLevel)
                    {
                        case EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh:
                            errors.Add(line);
                            break;
                        case EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelMedium:
                            warnings.Add(line);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryCollectBuildErrors error: {ex.Message}");
            }
        }

        /// <summary>
        /// Formats a single Error List item as "path(line,col): description" with a
        /// solution-relative, forward-slash path. Falls back to just the description when the
        /// item has no file.
        /// </summary>
        private string FormatErrorItem(EnvDTE80.ErrorItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string description;
            string file;
            int lineNum;
            int col;
            try
            {
                description = (item.Description ?? string.Empty).Trim();
                file = item.FileName ?? string.Empty;
                lineNum = item.Line;
                col = item.Column;
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrEmpty(file))
            {
                return description.Length > 0 ? description : null;
            }

            string rel = MakeSolutionRelativePath(file);
            return $"{rel}({lineNum},{col}): {description}";
        }

        /// <summary>
        /// Returns <paramref name="fullPath"/> relative to the solution directory using forward
        /// slashes (portable across native and WSL agents). Falls back to the file name, then the
        /// original path, when a relative form can't be computed.
        /// </summary>
        private string MakeSolutionRelativePath(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                string solutionFile = dte?.Solution?.FullName;
                string baseDir = !string.IsNullOrEmpty(solutionFile)
                    ? Path.GetDirectoryName(solutionFile)
                    : null;

                if (!string.IsNullOrEmpty(baseDir) && Path.IsPathRooted(fullPath))
                {
                    string baseFull = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + "\\";
                    string target = Path.GetFullPath(fullPath);
                    if (target.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return target.Substring(baseFull.Length).Replace('\\', '/');
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MakeSolutionRelativePath error: {ex.Message}");
            }

            try { return Path.GetFileName(fullPath); }
            catch { return fullPath; }
        }

        /// <summary>
        /// Builds the prompt sent to the agent: a summary line, a numbered errors list, and
        /// (when present) a numbered warnings list, each capped so a build with hundreds of
        /// issues doesn't flood the terminal.
        /// </summary>
        internal static string FormatBuildErrorPrompt(List<string> errors, List<string> warnings)
        {
            var sb = new StringBuilder();

            string errPart = errors.Count == 1 ? "1 error" : $"{errors.Count} errors";
            string warnPart = warnings.Count == 0
                ? string.Empty
                : (warnings.Count == 1 ? " and 1 warning" : $" and {warnings.Count} warnings");

            sb.Append("The Visual Studio build finished with ").Append(errPart).Append(warnPart)
              .AppendLine(". Please investigate and fix the errors:");
            sb.AppendLine();

            sb.AppendLine("Errors:");
            AppendCappedList(sb, errors, MaxAutoSendErrors, "error");

            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                AppendCappedList(sb, warnings, MaxAutoSendWarnings, "warning");
            }

            sb.AppendLine();
            sb.Append("After making changes, rebuild the solution to confirm the errors are resolved.");
            return sb.ToString();
        }

        /// <summary>Appends a numbered, capped list; notes how many were omitted past the cap.</summary>
        private static void AppendCappedList(StringBuilder sb, List<string> lines, int cap, string noun)
        {
            int shown = Math.Min(lines.Count, cap);
            for (int i = 0; i < shown; i++)
            {
                sb.Append(i + 1).Append(". ").AppendLine(lines[i]);
            }
            int remaining = lines.Count - shown;
            if (remaining > 0)
            {
                sb.Append("... and ").Append(remaining).Append(' ')
                  .Append(remaining == 1 ? noun : noun + "s").AppendLine(" more.");
            }
        }

        #endregion
    }
}
