/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Terminal input/output communication - sending text and keyboard events
 *
 * *******************************************************************************************************************/

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Terminal Communication

        /// <summary>
        /// Timeout for clipboard operations in milliseconds
        /// </summary>
        private const int ClipboardTimeoutMs = 2000;

        /// <summary>
        /// Maximum number of retry attempts for clipboard operations.
        /// Combined with ClipboardRetryDelayMs gives a ~6s ceiling (30 * 200ms).
        /// Tuned for cases where another process (clipboard manager, conhost mark-mode) holds the clipboard.
        /// </summary>
        private const int ClipboardMaxRetries = 30;

        /// <summary>
        /// Delay between clipboard retry attempts in milliseconds
        /// </summary>
        private const int ClipboardRetryDelayMs = 200;

        /// <summary>
        /// Maximum number of SetText+verify attempts before giving up. If verify keeps
        /// failing a clipboard manager is persistently overwriting our content; sending
        /// would silently truncate, so we abort with a visible error instead.
        /// </summary>
        private const int ClipboardVerifyRetries = 3;

        /// <summary>
        /// Upper bound on the additional post-paste wait per chunk. Each chunk is bounded
        /// by PasteChunkSize so a 5-second cap is far more than needed at realistic paste
        /// rates. See issue #48.
        /// </summary>
        private const int MaxExtraPasteDelayMs = 5000;

        /// <summary>
        /// Estimated paste-streaming rate, expressed as milliseconds-per-character.
        /// conhost realistically streams a paste at ~5 KB/s once you factor in TUI render
        /// time, so 1 ms per 5 chars (= 5000 chars/s) is conservative-but-not-excessive.
        /// </summary>
        private const int PasteMsPerCharDivisor = 5;

        /// <summary>
        /// Maximum chunk size for a single paste operation. Texts longer than this are split
        /// into multiple sequential pastes. A single paste larger than this hits the Claude
        /// Code CLI input-buffer limit and produces front-truncation (the head of the prompt
        /// gets dropped). At 24 KB each chunk is small enough to fit comfortably in the CLI
        /// input buffer while keeping the number of [Pasted text #N] blocks low for big
        /// prompts (e.g. a 65 KB file → 3 blocks instead of 20+ with the previous 4 KB
        /// chunking). See issue #48.
        /// </summary>
        private const int PasteChunkSize = 24576;

        /// <summary>
        /// Sends text to the embedded terminal by copying to clipboard and simulating paste
        /// Preserves the original clipboard content and restores it after sending
        /// This is the synchronous wrapper for backward compatibility
        /// </summary>
        /// <param name="text">The text to send to the terminal</param>
        private void SendTextToTerminal(string text)
        {
            // Fire and forget with error handling - the async version handles the actual work
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await SendTextToTerminalAsync(text);
            });
        }

        /// <summary>
        /// Sends text to the embedded terminal asynchronously
        /// Preserves the original clipboard content and restores it after sending
        /// </summary>
        /// <param name="text">The text to send to the terminal</param>
        private async Task SendTextToTerminalAsync(string text)
        {
            // Mouse-input-mode fallback (issue #76): when a TUI has switched the embedded conhost
            // into mouse-input mode (QuickEdit disabled), conhost's right-click paste is intercepted
            // by the running app, so the clipboard paste below silently does nothing. Detect that
            // state and deliver the text through focus-independent WM_CHAR keystrokes instead. Done
            // before any clipboard work so the user's clipboard is left untouched. Only for plain
            // conhost providers — Open Code / PI / Antigravity have their own paste handling and
            // Windows Terminal isn't conhost. The probe is best-effort: any failure returns false
            // and falls through to the normal clipboard paste, so the common case is unaffected.
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle)
                && _wtTabBarHeight == 0
                && _currentRunningProvider != AiProvider.OpenCode
                && _currentRunningProvider != AiProvider.Pi
                && _currentRunningProvider != AiProvider.Antigravity)
            {
                bool mouseInputMode = await Task.Run(() => IsTerminalInMouseInputMode());
                if (mouseInputMode)
                {
                    await SendTextViaKeystrokesAsync(text);
                    return;
                }
            }

            // Dictionary to store all original clipboard formats and their data
            System.Collections.Generic.Dictionary<string, object> originalClipboardData = null;

            try
            {
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    // Make sure we're on the UI thread for clipboard operations
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Save the current clipboard content before modifying it.
                    // Non-fatal: if another process holds the clipboard, skip preservation and continue —
                    // losing the user's prior clipboard is preferable to aborting the send.
                    try
                    {
                        originalClipboardData = await ClipboardRetryAsync(() => SaveClipboardContent());
                    }
                    catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                    {
                        LogClipboardLockOwner("SaveClipboardContent");
                        originalClipboardData = null;
                    }

                    // Clear clipboard immediately so the deselect right-click below won't paste old content.
                    // Non-fatal — if it fails, the right-click below may paste stale content but the send still proceeds.
                    try
                    {
                        await ClipboardRetryAsync(() => Clipboard.Clear());
                    }
                    catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                    {
                        LogClipboardLockOwner("Clipboard.Clear (pre-deselect)");
                    }
                    await Task.Delay(50);

                    // If terminal is detached, ensure the detached window tab is visible
                    // (auto-open changes may have activated the diff viewer tab instead)
                    if (_isTerminalDetached && _detachedTerminalWindow?.Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame detachedFrame)
                    {
                        detachedFrame.Show();
                        await Task.Delay(200);
                    }

                    // Set focus to terminal window
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);

                    await Task.Delay(500); // Reduced from 700ms

                    // TUI-based Node.js providers need extra time to initialize
                    // their interface after receiving focus, otherwise the paste may arrive
                    // before the TUI is ready to accept input.
                    if (_currentRunningProvider == AiProvider.Pi)
                    {
                        await Task.Delay(400);
                    }

                    // For Command Prompt (conhost): right-click first to cancel any active text selection.
                    // If text is selected, right-click copies it to clipboard and deselects.
                    // If no text is selected, right-click pastes from clipboard (which is empty, so harmless).
                    // Antigravity is excluded because it disables QuickEdit at startup, so a plain
                    // right-click would open the conhost context menu instead — the menu navigation
                    // for Antigravity happens in the dedicated paste branch below.
                    bool isCommandPrompt = _wtTabBarHeight == 0
                                           && _currentRunningProvider != AiProvider.OpenCode
                                           && _currentRunningProvider != AiProvider.Pi
                                           && _currentRunningProvider != AiProvider.Antigravity;
                    if (isCommandPrompt)
                    {
                        await RightClickTerminalCenterAsync();
                        await Task.Delay(300);
                    }

                    // Now set the clipboard to the prompt text (after deselect right-click which may overwrite clipboard)
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try
                    {
                        await ClipboardRetryAsync(() => Clipboard.Clear());
                    }
                    catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                    {
                        LogClipboardLockOwner("Clipboard.Clear (pre-SetText)");
                    }
                    await Task.Delay(50);

                    // Put the prompt on the clipboard, verify the clipboard actually holds that
                    // exact content (a clipboard manager can race in and replace it between
                    // SetText and paste), then paste it. If the prompt is larger than the
                    // Claude-Code-CLI input-buffer fits, we split it into chunks so each paste
                    // arrives whole — sending the whole thing as one paste produces front-
                    // truncation upstream. If verification keeps failing we abort with a visible
                    // error rather than silently sending a partial prompt. See issue #48.
                    int totalLen = text?.Length ?? 0;
                    int chunkCount = totalLen <= PasteChunkSize
                        ? 1
                        : (totalLen + PasteChunkSize - 1) / PasteChunkSize;

                    for (int chunkIdx = 0; chunkIdx < chunkCount; chunkIdx++)
                    {
                        int start = chunkIdx * PasteChunkSize;
                        int len = Math.Min(PasteChunkSize, totalLen - start);
                        string chunk = chunkCount == 1 ? text : text.Substring(start, len);

                        bool clipboardOk;
                        try
                        {
                            clipboardOk = await SetClipboardAndVerifyAsync(chunk);
                        }
                        catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                        {
                            clipboardOk = false;
                        }

                        if (!clipboardOk)
                        {
                            string owner = LogClipboardLockOwner("Clipboard.SetText");
                            string scope = chunkCount == 1
                                ? "the prompt"
                                : $"chunk {chunkIdx + 1} of {chunkCount}";

                            // Log and proceed. The paste might still succeed — the verification read
                            // can fail intermittently even when SetText actually put the right content
                            // on the clipboard (race with clipboard listeners that briefly open the
                            // clipboard for notification, RDP redirection delays, etc.). Users who keep
                            // hitting clipboard contention should enable "Disable clipboard" in Settings
                            // to bypass the clipboard entirely. See issues #59 and #61.
                            System.Diagnostics.Debug.WriteLine(
                                $"[Clipboard] Verification failed for {scope} (owner = {owner}); proceeding with paste anyway.");
                        }

                        await TriggerPasteAndWaitAsync(len);
                    }

                    SendEnterKey();
                }
                else
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show("Terminal is not available. Please restart the terminal.",
                                  "Terminal Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Error sending text to terminal: {ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Restore the original clipboard content on UI thread
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await Task.Delay(100); // Small delay to ensure paste completed
                    await ClipboardRetryAsync(() => RestoreClipboardContent(originalClipboardData));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error restoring clipboard: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Sends text to the embedded terminal WITHOUT touching the clipboard, by injecting each
        /// character as an OS-level Unicode keystroke via SendInput (KEYEVENTF_UNICODE). Used by the
        /// "Disable clipboard" send mode for users whose clipboard is held by another application so
        /// the normal paste path fails. The payload is expected to be short (a file reference), so
        /// per-character SendInput is fast enough. Focus/detach setup mirrors the paste path; the
        /// provider-specific Enter is reused via SendEnterKey. See issue #61.
        /// </summary>
        /// <param name="text">The text to type into the terminal (no clipboard is used)</param>
        private async Task SendTextViaKeystrokesAsync(string text)
        {
            try
            {
                if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show("Terminal is not available. Please restart the terminal.",
                                  "Terminal Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // If terminal is detached, ensure the detached window tab is visible
                // (auto-open changes may have activated the diff viewer tab instead)
                if (_isTerminalDetached && _detachedTerminalWindow?.Frame is Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame detachedFrame)
                {
                    detachedFrame.Show();
                    await Task.Delay(200);
                }

                // Properly focus the embedded terminal before typing. SetForegroundWindow on the
                // terminal child window silently fails (it's a child of the VS panel), so SendInput
                // would otherwise type into whatever VS control currently has keyboard focus. Mirror
                // the zoom-replay focus sequence: activate the hosting tool window, focus the WinForms
                // panel, then SetFocus the child terminal window. See issue #61.
                await ActivateTerminalToolWindowAsync();
                await Task.Delay(60);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var panel = ActiveTerminalPanel;
                if (panel == null)
                {
                    MessageBox.Show("Terminal is not available. Please restart the terminal.",
                                  "Terminal Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                FocusTerminalPanel(panel);
                await Task.Delay(120);

                // TUI-based Node.js providers need extra time to initialize their interface
                // after receiving focus, otherwise the first keystrokes may arrive too early.
                if (_currentRunningProvider == AiProvider.Pi)
                {
                    await Task.Delay(400);
                }

                // Re-assert focus in case VS restored it to another control during the delay.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetFocus(terminalHandle);
                await Task.Delay(50);

                TypeUnicodeText(text);

                // Brief settle so the typed text is fully rendered before Enter is sent.
                await Task.Delay(150);
                SendEnterKey();
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Error sending text to terminal: {ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Injects a string into the terminal one UTF-16 code unit at a time by posting WM_CHAR
        /// directly to the terminal window. This is the same focus-independent, cross-process
        /// primitive SendEnterKey uses to deliver Enter to Claude Code — unlike SendInput, it does
        /// not depend on the terminal owning the foreground keyboard focus (the embedded terminal
        /// is a child of the VS panel in a separate process, so SendInput's WM_CHAR lands on the
        /// panel instead). Surrogate pairs are posted as their two constituent code units. Carriage
        /// returns are skipped; the caller sends the provider-specific Enter separately. See issue #61.
        /// </summary>
        private void TypeUnicodeText(string text)
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle)) return;
            if (string.IsNullOrEmpty(text)) return;

            foreach (char c in text)
            {
                // Skip CR — line submission is handled by SendEnterKey, not by typing a newline.
                if (c == '\r') continue;

                PostMessage(terminalHandle, WM_CHAR, new IntPtr(c), IntPtr.Zero);

                // Pacing delay so the TUI's input loop doesn't drop characters. At a few ms the
                // readline occasionally coalesced/dropped keystrokes (notably spaces); 25 ms is
                // reliable and, for the short file-reference payload (~90 chars), still imperceptible
                // (~2 s total).
                System.Threading.Thread.Sleep(25);
            }
        }

        /// <summary>
        /// Executes a clipboard operation with automatic retry logic
        /// Handles CLIPBRD_E_CANT_OPEN (0x800401D0) errors that occur when another application holds the clipboard
        /// </summary>
        /// <param name="action">The clipboard action to execute</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        private async Task ClipboardRetryAsync(Action action, int maxRetries = ClipboardMaxRetries, int retryDelayMs = ClipboardRetryDelayMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                {
                    if (attempt == maxRetries)
                        throw;
                    await Task.Delay(retryDelayMs);
                }
            }
        }

        /// <summary>
        /// Executes a clipboard operation with automatic retry logic, returning a value
        /// Handles CLIPBRD_E_CANT_OPEN (0x800401D0) errors that occur when another application holds the clipboard
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="func">The clipboard function to execute</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        /// <returns>The result of the clipboard operation</returns>
        private async Task<T> ClipboardRetryAsync<T>(Func<T> func, int maxRetries = ClipboardMaxRetries, int retryDelayMs = ClipboardRetryDelayMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return func();
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                {
                    if (attempt == maxRetries)
                        throw;
                    await Task.Delay(retryDelayMs);
                }
            }
            return default; // Should never reach here
        }

        /// <summary>
        /// Sets the clipboard to <paramref name="text"/> and verifies the content is actually
        /// what we put there before returning true. A clipboard manager (Win+V history, Ditto,
        /// Office clipboard) can overwrite our content between SetText and the paste trigger;
        /// without this verification that would silently truncate the prompt. See issue #48.
        /// </summary>
        /// <returns>true if the clipboard reliably contains <paramref name="text"/>; false otherwise.</returns>
        private async Task<bool> SetClipboardAndVerifyAsync(string text)
        {
            for (int attempt = 0; attempt < ClipboardVerifyRetries; attempt++)
            {
                try
                {
                    await ClipboardRetryAsync(() => Clipboard.SetText(text));
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    return false;
                }

                // Brief settle to let other clipboard listeners react before we verify.
                await Task.Delay(30);

                string current = null;
                try
                {
                    current = await ClipboardRetryAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    current = null;
                }

                if (ClipboardTextMatches(current, text))
                {
                    return true;
                }

                // Brief backoff before the next SetText attempt — gives transient
                // clipboard holders (notification listeners, RDP redirection,
                // history apps) time to release the clipboard. Without it, the
                // three retries can finish in under 100 ms and hit the same hold.
                await Task.Delay(150);
            }
            return false;
        }

        /// <summary>
        /// Compares a clipboard read-back against the original text. Tolerates the
        /// well-known cases where the round-tripped string differs harmlessly from
        /// the source: line endings normalized to <c>\r\n</c>, a trailing newline
        /// appended, or a stray <c>\0</c> terminator left from CF_UNICODETEXT.
        /// Without this leniency the verify step rejects content that would have
        /// pasted correctly. See issue #59.
        /// </summary>
        private static bool ClipboardTextMatches(string actual, string expected)
        {
            if (actual == null || expected == null) return false;
            if (string.Equals(actual, expected, StringComparison.Ordinal)) return true;

            // Trim a trailing NUL (CF_UNICODETEXT terminator sometimes leaks through).
            string normalizedActual = actual.TrimEnd('\0');

            // Normalize line endings on both sides: convert all \r\n and lone \r to \n.
            normalizedActual = normalizedActual.Replace("\r\n", "\n").Replace("\r", "\n");
            string normalizedExpected = expected.Replace("\r\n", "\n").Replace("\r", "\n");

            if (string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal))
                return true;

            // Some clipboard pipelines append a trailing newline to text content.
            if (string.Equals(normalizedActual, normalizedExpected + "\n", StringComparison.Ordinal))
                return true;

            return false;
        }

        /// <summary>
        /// Triggers the provider-specific paste action for the content currently on the clipboard
        /// and waits long enough for conhost/ConPTY to stream it into the CLI's stdin. Re-focuses
        /// the terminal first in case focus drifted. See issue #48.
        /// </summary>
        /// <param name="textLength">Length of the text currently on the clipboard, used to scale the post-paste wait.</param>
        private async Task TriggerPasteAndWaitAsync(int textLength)
        {
            SetForegroundWindow(terminalHandle);
            SetFocus(terminalHandle);
            await Task.Delay(50);

            // Scale the post-paste wait with text length, capped at MaxExtraPasteDelayMs.
            // The cap is the safety ceiling for very large prompts; in practice paste rates
            // are faster than 5 KB/s, so the wait is usually shorter than the cap.
            int extraDelayMs = Math.Min(textLength / PasteMsPerCharDivisor, MaxExtraPasteDelayMs);

            if (_wtTabBarHeight > 0)
            {
                await PasteViaCtrlShiftVAsync();
                await Task.Delay(500 + extraDelayMs);
            }
            else if (_currentRunningProvider == AiProvider.OpenCode
                     || _currentRunningProvider == AiProvider.Pi)
            {
                await ShiftRightClickTerminalCenterAsync();
                await Task.Delay(800 + extraDelayMs);
            }
            else if (_currentRunningProvider == AiProvider.Antigravity)
            {
                // Antigravity disables conhost QuickEdit mode at startup so its TUI can
                // capture mouse events. As a result, right-click opens the conhost context
                // menu instead of pasting. Navigate the menu with the keyboard:
                //   Down -> highlights "Mark" (first item)
                //   Down -> highlights "Copy" (disabled, but Windows menus still stop on it)
                //   Down -> highlights "Paste"
                //   Enter -> invokes Paste
                // Menu order is locale-agnostic; only the labels change. The delays are
                // intentionally generous because rushing the keystrokes drops them.
                await RightClickTerminalCenterAsync();
                await Task.Delay(500); // let conhost render the context menu
                SendKeyDownUp(VK_DOWN);
                await Task.Delay(150);
                SendKeyDownUp(VK_DOWN);
                await Task.Delay(150);
                SendKeyDownUp(VK_DOWN);
                await Task.Delay(150);
                SendKeyDownUp(VK_RETURN);
                await Task.Delay(800 + extraDelayMs);
            }
            else
            {
                await RightClickTerminalCenterAsync();
                await Task.Delay(800 + extraDelayMs);
            }
        }

        /// <summary>
        /// Executes a clipboard operation with synchronous retry logic
        /// Handles CLIPBRD_E_CANT_OPEN (0x800401D0) errors that occur when another application holds the clipboard
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="func">The clipboard function to execute</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        /// <returns>The result of the clipboard operation</returns>
        private T ClipboardRetrySync<T>(Func<T> func, int maxRetries = ClipboardMaxRetries, int retryDelayMs = ClipboardRetryDelayMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return func();
                }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.ErrorCode == unchecked((int)0x800401D0))
                {
                    if (attempt == maxRetries)
                        throw;
                    Thread.Sleep(retryDelayMs);
                }
            }
            return default; // Should never reach here
        }

        /// <summary>
        /// Logs which process currently holds the Win32 clipboard (when a CLIPBRD_E_CANT_OPEN occurs)
        /// and returns a human-readable description for use in dialogs / debug output.
        /// </summary>
        /// <param name="context">Where the lock was hit, used as log prefix</param>
        /// <returns>"ProcessName (PID 1234)" or "unknown" if owner cannot be determined</returns>
        private string LogClipboardLockOwner(string context)
        {
            string description = "unknown";
            try
            {
                IntPtr ownerWindow = GetOpenClipboardWindow();
                if (ownerWindow != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(ownerWindow, out uint pid);
                    if (pid != 0)
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                            description = $"{proc.ProcessName} (PID {pid})";
                        }
                        catch
                        {
                            description = $"PID {pid}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LogClipboardLockOwner: failed to query owner: {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine($"[Clipboard lock] {context}: owner = {description}");
            return description;
        }

        /// <summary>
        /// Saves all clipboard content formats for later restoration
        /// Preserves all formats including Office-specific formats (Excel, Word, etc.)
        /// </summary>
        /// <returns>Dictionary of format names to data objects, or null if clipboard is empty</returns>
        private System.Collections.Generic.Dictionary<string, object> SaveClipboardContent()
        {
            try
            {
                IDataObject dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                    return null;

                string[] formats = dataObject.GetFormats();
                if (formats == null || formats.Length == 0)
                    return null;

                var savedData = new System.Collections.Generic.Dictionary<string, object>();

                foreach (string format in formats)
                {
                    try
                    {
                        // Skip formats that can cause issues
                        if (format == "EnhancedMetafile" ||
                            format == "MetaFilePict" ||
                            format == "DeviceIndependentBitmap" ||
                            format == "System.Drawing.Bitmap" ||
                            format.StartsWith("Object Descriptor") ||
                            format.StartsWith("Link Source") ||
                            format.StartsWith("Ole Private Data"))
                            continue;

                        object data = dataObject.GetData(format);
                        if (data != null)
                        {
                            // For MemoryStream, we need to copy it as the original may be disposed
                            if (data is System.IO.MemoryStream ms)
                            {
                                try
                                {
                                    if (ms.CanRead && ms.CanSeek)
                                    {
                                        var copy = new System.IO.MemoryStream();
                                        ms.Position = 0;
                                        ms.CopyTo(copy);
                                        copy.Position = 0;
                                        savedData[format] = copy;
                                    }
                                }
                                catch
                                {
                                    // Skip streams that can't be copied
                                }
                            }
                            // For other Stream types, skip them as they can cause issues
                            else if (data is System.IO.Stream)
                            {
                                continue;
                            }
                            // Save primitive types and strings directly
                            else if (data is string || data is string[] || data.GetType().IsPrimitive)
                            {
                                savedData[format] = data;
                            }
                            // For byte arrays, make a copy
                            else if (data is byte[] bytes)
                            {
                                var copy = new byte[bytes.Length];
                                Array.Copy(bytes, copy, bytes.Length);
                                savedData[format] = copy;
                            }
                        }
                    }
                    catch
                    {
                        // Skip formats that can't be read
                    }
                }

                return savedData.Count > 0 ? savedData : null;
            }
            catch
            {
                // Silently fail if we can't access clipboard
                return null;
            }
        }

        /// <summary>
        /// Restores previously saved clipboard content with all formats
        /// This preserves Office application data (Excel cells, Word content, etc.)
        /// </summary>
        /// <param name="savedData">Dictionary of format names to data objects</param>
        private void RestoreClipboardContent(System.Collections.Generic.Dictionary<string, object> savedData)
        {
            try
            {
                if (savedData == null || savedData.Count == 0)
                {
                    // Original clipboard was empty, clear it
                    Clipboard.Clear();
                    return;
                }

                // Create a new DataObject and add all saved formats
                DataObject newDataObject = new DataObject();

                foreach (var kvp in savedData)
                {
                    try
                    {
                        // Reset stream position if it's a stream
                        if (kvp.Value is System.IO.MemoryStream ms)
                        {
                            ms.Position = 0;
                        }
                        newDataObject.SetData(kvp.Key, kvp.Value);
                    }
                    catch
                    {
                        // Skip formats that can't be set
                    }
                }

                Clipboard.SetDataObject(newDataObject, true);
            }
            catch
            {
                // Silently fail if we can't restore clipboard
            }
        }

        /// <summary>
        /// Pastes text using Ctrl+Shift+V keyboard shortcut (for Windows Terminal).
        /// Windows Terminal right-click opens a context menu instead of pasting directly,
        /// so we use the keyboard shortcut which always pastes reliably.
        /// </summary>
        private async Task PasteViaCtrlShiftVAsync()
        {
            if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle)) return;

            SetForegroundWindow(terminalHandle);
            SetFocus(terminalHandle);
            await Task.Delay(100);

            // Ctrl+Shift+V
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            await Task.Delay(30);
            keybd_event(0x56, 0, 0, UIntPtr.Zero); // VK_V = 0x56
            await Task.Delay(30);
            keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>
        /// Simulates a right-click at the specified screen coordinates (async version)
        /// </summary>
        /// <param name="x">Screen X coordinate</param>
        /// <param name="y">Screen Y coordinate</param>
        private async Task SendRightClickAsync(int x, int y)
        {
            SetCursorPos(x, y);
            await Task.Delay(30); // Reduced from 50ms
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(30); // Reduced from 50ms
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        /// <summary>
        /// Simulates a right-click at the specified screen coordinates (sync version for backward compat)
        /// </summary>
        private void SendRightClick(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        /// <summary>
        /// Right-clicks on the center of the terminal window (async version)
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private async Task RightClickTerminalCenterAsync()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                // The window is positioned at Y = -_wtTabBarHeight, so add it back to get visible center
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                await SendRightClickAsync(centerX, centerY);
            }
        }

        /// <summary>
        /// Right-clicks on the center of the terminal window (sync version for backward compat)
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private void RightClickTerminalCenter()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                SendRightClick(centerX, centerY);
            }
        }

        /// <summary>
        /// Performs SHIFT+Right-click on the center of the terminal window (async version)
        /// Required for Open Code to paste text properly
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private async Task ShiftRightClickTerminalCenterAsync()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                // Move cursor to center
                SetCursorPos(centerX, centerY);
                await Task.Delay(30); // Reduced from 50ms

                // Hold SHIFT key down
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                await Task.Delay(30); // Reduced from 50ms

                // Perform right-click
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(30); // Reduced from 50ms
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(30); // Reduced from 50ms

                // Release SHIFT key
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        /// <summary>
        /// Performs SHIFT+Right-click on the center of the terminal window (sync version)
        /// Required for Open Code to paste text properly
        /// For Windows Terminal, adjusts Y coordinate to account for hidden tab bar
        /// </summary>
        private void ShiftRightClickTerminalCenter()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                GetWindowRect(terminalHandle, out RECT rect);
                int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                // For Windows Terminal with hidden tab bar, adjust Y coordinate
                if (_wtTabBarHeight > 0)
                {
                    centerY += _wtTabBarHeight;
                }

                // Move cursor to center
                SetCursorPos(centerX, centerY);
                System.Threading.Thread.Sleep(30);

                // Hold SHIFT key down
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);

                // Perform right-click
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(30);

                // Release SHIFT key
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        /// <summary>
        /// Sends a single virtual-key down+up using keybd_event. Used for keyboard navigation
        /// of system context menus (e.g. the conhost right-click menu shown by Antigravity).
        /// </summary>
        private void SendKeyDownUp(int virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>
        /// Sends the Enter key to the terminal window
        /// Uses different methods depending on the provider (WSL-based vs Windows-based)
        /// </summary>
        private void SendEnterKey()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // Check CURRENTLY RUNNING provider (not the next one being set)
                bool isClaudeCodeWSL = _currentRunningProvider == AiProvider.ClaudeCodeWSL;

                // Check if we're using other WSL-based providers (Codex WSL, CursorAgent, Windsurf)
                bool isOtherWSLProvider = _currentRunningProvider == AiProvider.Codex ||
                                         _currentRunningProvider == AiProvider.CursorAgent ||
                                         _currentRunningProvider == AiProvider.Windsurf;

                bool isCodexNative = _currentRunningProvider == AiProvider.CodexNative;

                bool isOpenCode = _currentRunningProvider == AiProvider.OpenCode;
                bool isPi = _currentRunningProvider == AiProvider.Pi;

                // Check if Windows Terminal is active (tab bar height > 0)
                bool isWindowsTerminal = _wtTabBarHeight > 0;

                if (isWindowsTerminal)
                {
                    // For Windows Terminal, use KEYDOWN/KEYUP approach (works better with embedded window)
                    SendEnterKeyDownUp();
                }
                else if (isClaudeCodeWSL)
                {
                    // For Claude Code (WSL), send Enter using WM_CHAR
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
                else if (isCodexNative)
                {
                    // For Codex (Windows native), use KEYDOWN/KEYUP approach (Codex requires double Enter)
                    SendEnterKeyDownUp();
                }
                else if (isOtherWSLProvider)
                {
                    // For other WSL-based providers (Codex, CursorAgent), use KEYDOWN/KEYUP approach
                    SendEnterKeyDownUp();
                }
                else if (isOpenCode || isPi)
                {
                    // For Open Code and PI, use single WM_CHAR (TUI-based Node.js apps)
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
                else
                {
                    // For Windows-based providers (Claude Code), use single WM_CHAR
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// Sends Enter key using KEYDOWN/KEYUP messages (required for Codex and Windows Terminal)
        /// For Windows Terminal, uses keybd_event for better compatibility with embedded windows
        /// Sends the key twice to ensure submission
        /// </summary>
        private void SendEnterKeyDownUp()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // For Windows Terminal, use keybd_event (works better with embedded windows)
                if (_wtTabBarHeight > 0)
                {
                    // First Enter attempt
                    keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(100);

                    // Second Enter attempt to ensure submission
                    keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                else
                {
                    // For other providers (Codex, etc), use PostMessage
                    // First Enter attempt
                    PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
                    System.Threading.Thread.Sleep(100);

                    // Second Enter attempt to ensure submission
                    PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                    System.Threading.Thread.Sleep(50);
                    PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
            }
        }

        #endregion
    }
}