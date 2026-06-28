/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: AI provider detection, switching, and installation instructions
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Provider Fields

        /// <summary>
        /// Flag to show Claude installation notification only once per session
        /// </summary>
        private static bool _claudeNotificationShown = false;

        /// <summary>
        /// Flag to show Claude Code (WSL) installation notification only once per session
        /// </summary>
        private static bool _claudeCodeWSLNotificationShown = false;

        /// <summary>
        /// Flag to show Codex (WSL) installation notification only once per session
        /// </summary>
        private static bool _codexNotificationShown = false;

        /// <summary>
        /// Flag to show Codex (native) installation notification only once per session
        /// </summary>
        private static bool _codexNativeNotificationShown = false;

        /// <summary>
        /// Flag to show Cursor Agent (WSL) installation notification only once per session
        /// </summary>
        private static bool _cursorAgentNotificationShown = false;

        /// <summary>
        /// Flag to show Cursor (native) installation notification only once per session
        /// </summary>
        private static bool _cursorAgentNativeNotificationShown = false;

        /// <summary>
        /// Flag to show Open Code installation notification only once per session
        /// </summary>
        private static bool _openCodeNotificationShown = false;

        /// <summary>
        /// Flag to show Devin installation notification only once per session
        /// </summary>
        private static bool _devinNotificationShown = false;

        /// <summary>
        /// Flag to show PI installation notification only once per session
        /// </summary>
        private static bool _piNotificationShown = false;

        /// <summary>
        /// Flag to show Antigravity installation notification only once per session
        /// </summary>
        private static bool _antigravityNotificationShown = false;

        /// <summary>
        /// Flag to show Reasonix installation notification only once per session
        /// </summary>
        private static bool _reasonixNotificationShown = false;

        /// <summary>
        /// Flag to show Devin (native) installation notification only once per session
        /// </summary>
        private static bool _devinNativeNotificationShown = false;

        #endregion

        #region Provider Availability Cache

        /// <summary>
        /// Cache entry for provider availability with timestamp
        /// </summary>
        private class ProviderCacheEntry
        {
            public bool IsAvailable { get; set; }
            public DateTime CachedAt { get; set; }
        }

        /// <summary>
        /// Cache for provider availability results to avoid repeated slow checks
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<AiProvider, ProviderCacheEntry> _providerCache
            = new System.Collections.Generic.Dictionary<AiProvider, ProviderCacheEntry>();

        /// <summary>
        /// Cache for WSL installation status
        /// </summary>
        private static ProviderCacheEntry _wslCache = null;

        /// <summary>
        /// How long to cache provider availability results (5 minutes)
        /// </summary>
        private static readonly TimeSpan ProviderCacheExpiry = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Lock object for thread-safe cache access
        /// </summary>
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Checks if a cached provider result is still valid
        /// </summary>
        private static bool IsCacheValid(ProviderCacheEntry entry)
        {
            return entry != null && (DateTime.UtcNow - entry.CachedAt) < ProviderCacheExpiry;
        }

        /// <summary>
        /// Clears the provider availability cache (call when user explicitly checks or after install)
        /// </summary>
        public static void ClearProviderCache()
        {
            lock (_cacheLock)
            {
                _providerCache.Clear();
                _wslCache = null;
            }
        }

        #endregion

        #region Provider Detection

        /// <summary>
        /// Checks if Claude Code CLI is available on Windows.
        /// Prioritizes native installation at %USERPROFILE%\.local\bin\claude.exe,
        /// then falls back to any claude executable in PATH (claude.exe from winget,
        /// claude.cmd from NPM, etc.) via `where claude` which honors PATHEXT.
        /// Uses caching to avoid repeated slow checks.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if claude is available, false otherwise</returns>
        private async Task<bool> IsClaudeCmdAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.ClaudeCode, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.ClaudeCode, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                // First, check for native installation at %USERPROFILE%\.local\bin\claude.exe
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string nativeClaudePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");


                if (File.Exists(nativeClaudePath))
                {
                    CacheProviderResult(AiProvider.ClaudeCode, true);
                    return true;
                }


                cancellationToken.ThrowIfCancellationRequested();

                // Fall back to any claude executable in PATH — `where claude` (no extension)
                // lets cmd.exe search PATHEXT so it matches claude.exe (winget), claude.cmd (NPM), etc.
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where claude",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    // Use async wait with cancellation support
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.ClaudeCode, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();


                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.ClaudeCode, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Claude: {ex.Message}");
                CacheProviderResult(AiProvider.ClaudeCode, false);
                return false;
            }
        }

        /// <summary>
        /// Caches a provider availability result
        /// </summary>
        private static void CacheProviderResult(AiProvider provider, bool isAvailable)
        {
            lock (_cacheLock)
            {
                _providerCache[provider] = new ProviderCacheEntry
                {
                    IsAvailable = isAvailable,
                    CachedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Waits for a process to exit with timeout and cancellation support
        /// </summary>
        private static async Task<bool> WaitForProcessExitAsync(Process process, int timeoutMs, CancellationToken cancellationToken = default)
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(timeoutMs);
                    await Task.Run(() =>
                    {
                        while (!process.HasExited)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            Thread.Sleep(50);
                        }
                    }, cts.Token);
                    return true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred, not user cancellation
                return false;
            }
        }

        /// <summary>
        /// Checks if Claude Code CLI is available in WSL
        /// Uses retry logic with generous timeouts to handle WSL cold boot delays.
        /// Uses non-interactive login shell (-lc) for faster, cleaner detection.
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if claude is available in WSL, false otherwise</returns>
        private async Task<bool> IsClaudeCodeWSLAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.ClaudeCodeWSL, isWsl: true))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.ClaudeCodeWSL, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync(cancellationToken);
                if (!wslInstalled)
                {
                    CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Retry logic with timeouts to handle WSL cold boot
                int[] timeouts = { 8000, 20000 }; // 8s, 20s — generous for cold WSL boot
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Check if claude is available in WSL using 'which claude'
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -lc \"which claude\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        var completed = await WaitForProcessExitAsync(process, timeouts[attempt - 1], cancellationToken);

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }

                            // If not the last attempt, wait before retrying (reduced delay)
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();


                        // Check if output contains a path to claude
                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("claude");

                        if (isAvailable)
                        {
                            CacheProviderResult(AiProvider.ClaudeCodeWSL, true);
                            return true;
                        }

                        // If we got a definitive response (stdout has content, meaning WSL
                        // responded but claude was not found), no need to retry.
                        // Ignore stderr-only output (shell warnings from .bashrc, etc.)
                        if (!string.IsNullOrEmpty(output))
                        {
                            CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for claude in WSL: {ex.Message}");
                CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Codex CLI is available in WSL
        /// Uses retry logic to handle WSL initialization delays after boot
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if codex is available in WSL, false otherwise</returns>
        private async Task<bool> IsCodexCmdAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.Codex, isWsl: true))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.Codex, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync(cancellationToken);
                if (!wslInstalled)
                {
                    CacheProviderResult(AiProvider.Codex, false);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Retry logic with timeouts to handle WSL cold boot
                int[] timeouts = { 8000, 20000 }; // 8s, 20s — generous for cold WSL boot
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Check if codex is available in WSL using an interactive login shell.
                    // Codex is often installed through nvm, which may only be loaded for interactive shells.
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -lic \"which codex\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        var completed = await WaitForProcessExitAsync(process, timeouts[attempt - 1], cancellationToken);

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }

                            // If not the last attempt, wait before retrying (reduced delay)
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.Codex, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();


                        // Check if output contains a path to codex (like /home/user/.nvm/versions/node/v22.20.0/bin/codex)
                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("codex");

                        if (isAvailable)
                        {
                            CacheProviderResult(AiProvider.Codex, true);
                            return true;
                        }

                        // If we got a definitive response (stdout has content, meaning WSL
                        // responded but codex was not found), no need to retry.
                        // Ignore stderr-only output (shell warnings from .bashrc, etc.)
                        if (!string.IsNullOrEmpty(output))
                        {
                            CacheProviderResult(AiProvider.Codex, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                CacheProviderResult(AiProvider.Codex, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for codex in WSL: {ex.Message}");
                CacheProviderResult(AiProvider.Codex, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Codex CLI is available natively on Windows
        /// Uses 'where codex' to check if codex is in PATH
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if codex is available natively, false otherwise</returns>
        private async Task<bool> IsCodexNativeAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.CodexNative, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.CodexNative, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where codex",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.CodexNative, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.CodexNative, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Codex native: {ex.Message}");
                CacheProviderResult(AiProvider.CodexNative, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if WSL is installed on the system
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if WSL is installed, false otherwise</returns>
        private async Task<bool> IsWslInstalledAsync(CancellationToken cancellationToken = default)
        {
            // Check cache first
            lock (_cacheLock)
            {
                if (IsCacheValid(_wslCache))
                {
                    return _wslCache.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c wsl --status",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheWslResult(false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();


                    bool isInstalled = process.ExitCode == 0;

                    CacheWslResult(isInstalled);
                    return isInstalled;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for WSL: {ex.Message}");
                CacheWslResult(false);
                return false;
            }
        }

        /// <summary>
        /// Caches WSL installation result
        /// </summary>
        private static void CacheWslResult(bool isInstalled)
        {
            lock (_cacheLock)
            {
                _wslCache = new ProviderCacheEntry
                {
                    IsAvailable = isInstalled,
                    CachedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Checks if cursor-agent is installed inside WSL by checking for the symlink at ~/.local/bin/cursor-agent
        /// Uses retry logic to handle WSL initialization delays after boot
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if cursor-agent is available in WSL, false otherwise</returns>
        private async Task<bool> IsCursorAgentInstalledInWslAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.CursorAgent, isWsl: true))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.CursorAgent, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Retry logic with timeouts to handle WSL cold boot
                int[] timeouts = { 8000, 20000 }; // 8s, 20s — generous for cold WSL boot
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -lc \"test -L ~/.local/bin/cursor-agent && echo 'exists' || echo 'notfound'\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        var completed = await WaitForProcessExitAsync(process, timeouts[attempt - 1], cancellationToken);

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }

                            // If not the last attempt, wait before retrying (reduced delay)
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.CursorAgent, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();


                        bool isInstalled = output.Trim().Equals("exists", StringComparison.OrdinalIgnoreCase);

                        if (isInstalled)
                        {
                            CacheProviderResult(AiProvider.CursorAgent, true);
                            return true;
                        }

                        // If we got "notfound" response, agent is not installed, no need to retry
                        if (output.Trim().Equals("notfound", StringComparison.OrdinalIgnoreCase))
                        {
                            CacheProviderResult(AiProvider.CursorAgent, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                CacheProviderResult(AiProvider.CursorAgent, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for cursor-agent in WSL: {ex.Message}");
                CacheProviderResult(AiProvider.CursorAgent, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Devin is available inside WSL
        /// Uses 'which devin' command to verify installation
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if devin is available in WSL, false otherwise</returns>
        private async Task<bool> IsDevinAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.Devin, isWsl: true))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.Devin, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync(cancellationToken);
                if (!wslInstalled)
                {
                    CacheProviderResult(AiProvider.Devin, false);
                    return false;
                }

                // Retry logic with timeouts to handle WSL cold boot
                int[] timeouts = { 8000, 20000 }; // 8s, 20s — generous for cold WSL boot
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -lc \"which devin\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        var completed = await WaitForProcessExitAsync(process, timeouts[attempt - 1], cancellationToken);

                        if (!completed)
                        {
                            try { process.Kill(); } catch { }

                            if (attempt < maxRetries)
                            {
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.Devin, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();

                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("devin");

                        if (isAvailable)
                        {
                            CacheProviderResult(AiProvider.Devin, true);
                            return true;
                        }

                        // If stdout has content, WSL responded -- no need to retry
                        if (!string.IsNullOrEmpty(output))
                        {
                            CacheProviderResult(AiProvider.Devin, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                CacheProviderResult(AiProvider.Devin, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for devin in WSL: {ex.Message}");
                CacheProviderResult(AiProvider.Devin, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Cursor Agent is available natively on Windows
        /// Checks for agent.cmd at %LOCALAPPDATA%\cursor-agent\ first,
        /// then falls back to checking 'where agent' in PATH
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if cursor agent is available natively, false otherwise</returns>
        private async Task<bool> IsCursorAgentNativeAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.CursorAgentNative, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.CursorAgentNative, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                // First, check for installation at %LOCALAPPDATA%\cursor-agent\agent.cmd
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string nativeAgentPath = Path.Combine(localAppData, "cursor-agent", "agent.cmd");

                if (File.Exists(nativeAgentPath))
                {
                    CacheProviderResult(AiProvider.CursorAgentNative, true);
                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // If native exe not found, check for agent in PATH (agent.cmd etc.)
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where agent",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.CursorAgentNative, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.CursorAgentNative, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Cursor Agent native: {ex.Message}");
                CacheProviderResult(AiProvider.CursorAgentNative, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Open Code CLI is available (NPM installation)
        /// Uses 'where opencode' to check if opencode is in PATH
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if opencode is available, false otherwise</returns>
        private async Task<bool> IsOpenCodeAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.OpenCode, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.OpenCode, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where opencode",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.OpenCode, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();


                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.OpenCode, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Open Code: {ex.Message}");
                CacheProviderResult(AiProvider.OpenCode, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if PI CLI is available (NPM installation)
        /// Uses 'where pi' to check if pi is in PATH
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if pi is available, false otherwise</returns>
        private async Task<bool> IsPiAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.Pi, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.Pi, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where pi",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.Pi, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.Pi, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for PI: {ex.Message}");
                CacheProviderResult(AiProvider.Pi, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Antigravity CLI is available (native Windows installation under %LocalAppData%\agy)
        /// Uses 'where agy' to check if agy is in PATH
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if agy is available, false otherwise</returns>
        private async Task<bool> IsAntigravityAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.Antigravity, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.Antigravity, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where agy",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Refresh PATH from registry so a freshly installed agy is detected without VS restart
                string freshPath = GetFreshPathFromRegistry();
                if (!string.IsNullOrEmpty(freshPath))
                {
                    startInfo.EnvironmentVariables["PATH"] = freshPath;
                }

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.Antigravity, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.Antigravity, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Antigravity: {ex.Message}");
                CacheProviderResult(AiProvider.Antigravity, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Reasonix CLI is available (native Windows installation via 'npm i -g reasonix').
        /// Uses 'where reasonix' to check if reasonix is in PATH.
        /// Uses caching to avoid repeated slow checks.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if reasonix is available, false otherwise</returns>
        private async Task<bool> IsReasonixAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.Reasonix, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.Reasonix, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where reasonix",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Refresh PATH from registry so a freshly installed reasonix is detected without VS restart
                string freshPath = GetFreshPathFromRegistry();
                if (!string.IsNullOrEmpty(freshPath))
                {
                    startInfo.EnvironmentVariables["PATH"] = freshPath;
                }

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.Reasonix, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.Reasonix, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Reasonix: {ex.Message}");
                CacheProviderResult(AiProvider.Reasonix, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Devin CLI is available natively on Windows (installed via the
        /// 'irm https://static.devin.ai/cli/setup.ps1 | iex' setup script).
        /// Uses 'where devin' to check if devin is in PATH. Cached to avoid repeated slow checks.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if devin is available, false otherwise</returns>
        private async Task<bool> IsDevinNativeAvailableAsync(CancellationToken cancellationToken = default)
        {
            // A configured custom CLI path means the tool is usable even when it is not on PATH.
            if (CustomExecutableConfigured(AiProvider.DevinNative, isWsl: false))
            {
                return true;
            }

            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.DevinNative, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where devin",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Refresh PATH from registry so a freshly installed devin is detected without VS restart
                string freshPath = GetFreshPathFromRegistry();
                if (!string.IsNullOrEmpty(freshPath))
                {
                    startInfo.EnvironmentVariables["PATH"] = freshPath;
                }

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        CacheProviderResult(AiProvider.DevinNative, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.DevinNative, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Devin (native): {ex.Message}");
                CacheProviderResult(AiProvider.DevinNative, false);
                return false;
            }
        }

        /// <summary>
        /// Checks if Windows Terminal is installed and available
        /// Checks the PATH (refreshed from registry) for wt.exe executable
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if Windows Terminal is available, false otherwise</returns>
        public async Task<bool> IsWindowsTerminalAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where wt.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Use fresh PATH from registry to detect newly installed WT without VS restart
                string freshPath = GetFreshPathFromRegistry();
                if (!string.IsNullOrEmpty(freshPath))
                {
                    startInfo.EnvironmentVariables["PATH"] = freshPath;
                }

                using (var process = Process.Start(startInfo))
                {
                    var completed = await WaitForProcessExitAsync(process, 3000, cancellationToken);

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    bool found = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    if (found)
                    {
                        // Store the full resolved path so we can use it to launch wt.exe reliably
                        _wtExePath = output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    }

                    return found;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Windows Terminal: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Installation Instructions

        /// <summary>
        /// Shows installation instructions for Claude Code CLI
        /// </summary>
        private void ShowClaudeInstallationInstructions()
        {
            const string instructions = @"Claude Code is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

RECOMMENDED: Native Installation (Windows)

Open cmd as administrator and run:

curl -fsSL https://claude.ai/install.cmd -o install.cmd && install.cmd && del install.cmd

Then add claude.exe to the PATH environment variable:
C:\Users\%username%\.local\bin

ALTERNATIVE: NPM Installation

If you prefer using NPM, you can install it with:

npm install -g @anthropic-ai/claude-code

For more details, visit: https://docs.claude.com/en/docs/claude-code/setup";

            MessageBox.Show(instructions, "Claude Code Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Claude Code CLI in WSL
        /// </summary>
        private void ShowClaudeCodeWSLInstallationInstructions()
        {
            const string instructions = @"To use Claude Code (WSL), you need to install WSL and Claude Code inside WSL.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

# Start a shell inside of Windows Subsystem for Linux
wsl

# https://learn.microsoft.com/en-us/windows/dev-environment/javascript/nodejs-on-wsl
# Install Node.js in WSL
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/master/install.sh | bash

# In a new tab or after exiting and running `wsl` again to install Node.js
nvm install 22

# Install and run Claude Code in WSL
npm i -g @anthropic-ai/claude-code
claude";

            MessageBox.Show(instructions, "Claude Code (WSL) Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Codex CLI in WSL
        /// </summary>
        private void ShowCodexInstallationInstructions()
        {
            const string instructions = @"To use Codex, you need to install WSL and Codex inside WSL.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

# Start a shell inside of Windows Subsystem for Linux
wsl

# https://learn.microsoft.com/en-us/windows/dev-environment/javascript/nodejs-on-wsl
# Install Node.js in WSL
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/master/install.sh | bash

# In a new tab or after exiting and running `wsl` again to install Node.js
nvm install 22

# Install and run Codex in WSL
npm i -g @openai/codex
codex";

            MessageBox.Show(instructions, "Codex (WSL) Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Codex CLI (native Windows)
        /// </summary>
        private void ShowCodexNativeInstallationInstructions()
        {
            const string instructions = @"Codex is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

INSTALLATION: NPM Installation

Open cmd and run:

npm install -g @openai/codex

Requirements:
- Node.js installed
- Chat GPT Plus or better paid subscription

For more details, visit: https://github.com/openai/codex";

            MessageBox.Show(instructions, "Codex Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Cursor Agent (requires WSL)
        /// </summary>
        private void ShowCursorAgentInstallationInstructions()
        {
            const string instructions = @"To use Cursor Agent, you need to install WSL and cursor-agent.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

Install cursor agent inside WSL:

wsl 

curl https://cursor.com/install -fsS | bash

Copy and paste the 2 suggested commands to add cursor to path:

echo 'export PATH=""$HOME/.local/bin:$PATH""' >> ~/.bashrc
source ~/.bashrc

Start cursor-agent to login:

cursor-agent";

            MessageBox.Show(instructions, "Cursor Agent Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Cursor Agent (native Windows)
        /// </summary>
        private void ShowCursorAgentNativeInstallationInstructions()
        {
            const string instructions = @"Cursor Agent is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

INSTALLATION: Native Installation (Windows)

Open PowerShell and run:

irm 'https://cursor.com/install?win32=true' | iex

Then add agent.cmd to the PATH environment variable:
C:\Users\%username%\AppData\Local\cursor-agent

Also install ripgrep (required dependency):

winget install -e --id BurntSushi.ripgrep.MSVC

For more details, visit: https://cursor.com";

            MessageBox.Show(instructions, "Cursor Agent Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for Open Code CLI
        /// </summary>
        private void ShowOpenCodeInstallationInstructions()
        {
            const string instructions = @"Open Code is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

INSTALLATION: NPM Installation

Open cmd and run:

npm i -g opencode-ai

Requirements:
- Node.js installed

For more details, visit: https://opencode.ai";

            MessageBox.Show(instructions, "Open Code Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for the Devin CLI in WSL
        /// </summary>
        private void ShowDevinInstallationInstructions()
        {
            const string instructions = @"To use Devin, you need to install WSL and Devin inside WSL.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

Install Devin inside WSL:

wsl

curl -fsSL https://cli.devin.ai/install.sh | bash

Start devin to login:

devin";

            MessageBox.Show(instructions, "Devin Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation instructions for PI CLI
        /// </summary>
        private void ShowPiInstallationInstructions()
        {
            const string instructions = @"PI is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

INSTALLATION: NPM Installation

Open cmd and run:

npm install -g @earendil-works/pi-coding-agent

Requirements:
- Node.js installed
- Git for Windows (Git Bash) installed for bash support

For more details, visit: https://pi.dev";

            MessageBox.Show(instructions, "PI Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowAntigravityInstallationInstructions()
        {
            // Expand %LocalAppData%\agy to the actual path so the user can copy it
            // straight into the Environment Variables dialog without guessing.
            string agyPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agy");

            string instructions =
                "Antigravity is not installed. A regular CMD terminal will be used instead.\r\n\r\n" +
                "(you may click CTRL+C to copy full instructions)\r\n\r\n" +
                "INSTALLATION\r\n\r\n" +
                "Open PowerShell and run:\r\n\r\n" +
                "irm https://antigravity.google/cli/install.ps1 | iex\r\n\r\n" +
                "Then add the install folder to your PATH:\r\n\r\n" +
                agyPath + "\r\n\r\n" +
                "(Open a new terminal afterwards so the updated PATH takes effect.)\r\n\r\n" +
                "The agent is launched with the 'agy' command.\r\n\r\n" +
                "For more details, visit: https://antigravity.google/download";

            MessageBox.Show(instructions, "Antigravity Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation/configuration instructions for Reasonix when it is not detected on PATH.
        /// Reasonix is a DeepSeek-native coding agent installed via npm.
        /// </summary>
        private void ShowReasonixInstallationInstructions()
        {
            string instructions =
                "Reasonix is not installed. A regular CMD terminal will be used instead.\r\n\r\n" +
                "(you may click CTRL+C to copy full instructions)\r\n\r\n" +
                "INSTALLATION\r\n\r\n" +
                "Open a terminal and run:\r\n\r\n" +
                "npm i -g reasonix\r\n\r\n" +
                "(Open a new terminal afterwards so the updated PATH takes effect.)\r\n\r\n" +
                "CONFIGURATION\r\n\r\n" +
                "Set your DeepSeek API key before launching, e.g.:\r\n\r\n" +
                "setx DEEPSEEK_API_KEY sk-...\r\n\r\n" +
                "(or let the Reasonix setup wizard save it.)\r\n\r\n" +
                "The agent is launched with the 'reasonix' command.\r\n\r\n" +
                "For more details, visit: https://github.com/esengine/DeepSeek-Reasonix/releases";

            MessageBox.Show(instructions, "Reasonix Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Shows installation/configuration instructions for Devin (native) when it is not
        /// detected on PATH. Devin's native Windows CLI is installed via a PowerShell setup
        /// script that requires Windows Terminal.
        /// </summary>
        private void ShowDevinNativeInstallationInstructions()
        {
            string instructions =
                "Devin is not installed. A regular CMD terminal will be used instead.\r\n\r\n" +
                "(you may click CTRL+C to copy full instructions)\r\n\r\n" +
                "INSTALLATION\r\n\r\n" +
                "Open Windows Terminal and run:\r\n\r\n" +
                "irm https://static.devin.ai/cli/setup.ps1 | iex\r\n\r\n" +
                "(Installation must be done from Windows Terminal. Open a new terminal\r\n" +
                "afterwards so the updated PATH takes effect.)\r\n\r\n" +
                "Once installed, the 'devin' command works in both Windows Terminal and the\r\n" +
                "regular Command Prompt. The agent is launched with the 'devin' command.\r\n\r\n" +
                "For more details, visit: https://devin.ai";

            MessageBox.Show(instructions, "Devin Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Provider Switching

        /// <summary>
        /// Handles Open Code menu item click - switches to Open Code provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void OpenCodeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool openCodeAvailable = await IsOpenCodeAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.OpenCode;
            UpdateProviderSelection();
            SaveSettings();

            if (!openCodeAvailable)
            {
                ShowOpenCodeInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.OpenCode);
            }
        }

        /// <summary>
        /// Handles Devin (WSL) menu item click - switches to Devin provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void DevinMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool wslInstalled = await IsWslInstalledAsync();
            bool devinAvailable = false;

            if (wslInstalled)
            {
                devinAvailable = await IsDevinAvailableAsync();
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.Devin;
            UpdateProviderSelection();
            SaveSettings();

            if (!wslInstalled || !devinAvailable)
            {
                ShowDevinInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.Devin);
            }
        }

        /// <summary>
        /// Handles PI menu item click - switches to PI provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void PiMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool piAvailable = await IsPiAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.Pi;
            UpdateProviderSelection();
            SaveSettings();

            if (!piAvailable)
            {
                ShowPiInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.Pi);
            }
        }

        /// <summary>
        /// Handles Antigravity menu item click - switches to Antigravity provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void AntigravityMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool antigravityAvailable = await IsAntigravityAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.Antigravity;
            UpdateProviderSelection();
            SaveSettings();

            if (!antigravityAvailable)
            {
                ShowAntigravityInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.Antigravity);
            }
        }

        /// <summary>
        /// Handles Reasonix menu item click - switches to Reasonix provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void ReasonixMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool reasonixAvailable = await IsReasonixAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.Reasonix;
            UpdateProviderSelection();
            SaveSettings();

            if (!reasonixAvailable)
            {
                ShowReasonixInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.Reasonix);
            }
        }

        /// <summary>
        /// Handles Devin (native) menu item click - switches to the native Windows Devin provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void DevinNativeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool devinAvailable = await IsDevinNativeAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.DevinNative;
            UpdateProviderSelection();
            SaveSettings();

            if (!devinAvailable)
            {
                ShowDevinNativeInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.DevinNative);
            }
        }

        /// <summary>
        /// Handles Claude Code menu item click - switches to Claude Code provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void ClaudeCodeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool claudeAvailable = await IsClaudeCmdAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.ClaudeCode;
            UpdateProviderSelection();
            SaveSettings();

            if (!claudeAvailable)
            {
                ShowClaudeInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.ClaudeCode);
            }
        }

        /// <summary>
        /// Handles Claude Code (WSL) menu item click - switches to Claude Code (WSL) provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void ClaudeCodeWSLMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool claudeWSLAvailable = await IsClaudeCodeWSLAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.ClaudeCodeWSL;
            UpdateProviderSelection();
            SaveSettings();

            if (!claudeWSLAvailable)
            {
                ShowClaudeCodeWSLInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.ClaudeCodeWSL);
            }
        }

        /// <summary>
        /// Handles Codex (WSL) menu item click - switches to Codex (WSL) provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void CodexMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool codexAvailable = await IsCodexCmdAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.Codex;
            UpdateProviderSelection();
            SaveSettings();

            if (!codexAvailable)
            {
                ShowCodexInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.Codex);
            }
        }

        /// <summary>
        /// Handles Codex (native) menu item click - switches to Codex native provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void CodexNativeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool codexNativeAvailable = await IsCodexNativeAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.CodexNative;
            UpdateProviderSelection();
            SaveSettings();

            if (!codexNativeAvailable)
            {
                ShowCodexNativeInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.CodexNative);
            }
        }

        /// <summary>
        /// Handles Cursor Agent menu item click - switches to Cursor Agent provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void CursorAgentMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool wslInstalled = await IsWslInstalledAsync();
            bool cursorAgentInstalled = false;

            if (wslInstalled)
            {
                cursorAgentInstalled = await IsCursorAgentInstalledInWslAsync();
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.CursorAgent;
            UpdateProviderSelection();
            SaveSettings();

            if (!wslInstalled || !cursorAgentInstalled)
            {
                ShowCursorAgentInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.CursorAgent);
            }
        }

        /// <summary>
        /// Handles Cursor Agent (native) menu item click - switches to Cursor Agent native provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void CursorAgentNativeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool cursorAgentNativeAvailable = await IsCursorAgentNativeAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.CursorAgentNative;
            UpdateProviderSelection();
            SaveSettings();

            if (!cursorAgentNativeAvailable)
            {
                ShowCursorAgentNativeInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.CursorAgentNative);
            }
        }

        /// <summary>
        /// Updates UI to reflect the currently selected provider
        /// </summary>
        private void UpdateProviderSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            AiProvider? activeProvider = GetActiveOrSelectedProvider();

            // Update menu item checkmarks
            ClaudeCodeMenuItem.IsChecked = activeProvider == AiProvider.ClaudeCode;
            ClaudeCodeWSLMenuItem.IsChecked = activeProvider == AiProvider.ClaudeCodeWSL;
            CodexNativeMenuItem.IsChecked = activeProvider == AiProvider.CodexNative;
            CodexMenuItem.IsChecked = activeProvider == AiProvider.Codex;
            CursorAgentNativeMenuItem.IsChecked = activeProvider == AiProvider.CursorAgentNative;
            CursorAgentMenuItem.IsChecked = activeProvider == AiProvider.CursorAgent;
            OpenCodeMenuItem.IsChecked = activeProvider == AiProvider.OpenCode;
            DevinMenuItem.IsChecked = activeProvider == AiProvider.Devin;
            PiMenuItem.IsChecked = activeProvider == AiProvider.Pi;
            AntigravityMenuItem.IsChecked = activeProvider == AiProvider.Antigravity;
            ReasonixMenuItem.IsChecked = activeProvider == AiProvider.Reasonix;
            DevinNativeMenuItem.IsChecked = activeProvider == AiProvider.DevinNative;

            // Update GroupBox header to show the running provider when a terminal is active.
            // The header is hidden only when the terminal is on top (inverted horizontal
            // layout), where it is redundant with the tool window title. In a vertical
            // (side-by-side) split the terminal sits beside the prompt, so keep it visible.
            string providerName = GetProviderDisplayName(activeProvider);
            bool terminalOnTop = _settings?.InvertLayout == true
                && _settings?.SelectedLayoutOrientation == LayoutOrientation.Horizontal;
            if (terminalOnTop)
            {
                TerminalGroupBox.Header = null;
            }
            else
            {
                TerminalGroupBox.Header = new System.Windows.Controls.TextBlock { Text = providerName, Opacity = 0.93 };
            }

            // Show/hide model selection button based on provider
            bool isClaudeProvider = IsClaudeProvider(activeProvider);
            bool isDevinProvider = activeProvider == AiProvider.Devin || activeProvider == AiProvider.DevinNative;
            ModelDropdownButton.Visibility = (isClaudeProvider || isDevinProvider) ? Visibility.Visible : Visibility.Collapsed;

            UpdateInlineUsagePanelVisibility();

            // Swap the configurable features between dedicated toolbar buttons and the "⚙"
            // menu, applying provider constraints (Show Usage and Session History are
            // Claude/Devin-only).
            RefreshToolbarLayout();

            // Reflect the running provider/model immediately in the VS tool window title.
            UpdateToolWindowTitle(GetExtensionTitle(activeProvider));
        }

        /// <summary>
        /// Central swap between one-click toolbar buttons and "⚙" menu entries for the
        /// configurable features. For each feature: when its constraint holds AND the user
        /// promoted it (Settings → Toolbar), it shows as a toolbar button and is hidden from
        /// the menu; otherwise it stays in the menu (constraint permitting) and the button is
        /// hidden. Features whose constraint is not met are hidden from both places.
        /// See <see cref="ToolbarButton"/> and ClaudeCodeControl.SettingsDialog.cs.
        /// </summary>
        private void RefreshToolbarLayout()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            var promoted = _settings.VisibleToolbarButtons
                ?? new System.Collections.Generic.List<ToolbarButton>();

            // Apply the button/menu swap for one feature.
            void Apply(ToolbarButton id, bool constraintOk,
                System.Windows.Controls.Button button, System.Windows.Controls.MenuItem menuItem)
            {
                bool asButton = constraintOk && promoted.Contains(id);
                bool asMenu = constraintOk && !asButton;
                if (button != null) button.Visibility = asButton ? Visibility.Visible : Visibility.Collapsed;
                if (menuItem != null) menuItem.Visibility = asMenu ? Visibility.Visible : Visibility.Collapsed;
            }

            // Every configurable feature is always offered (button when promoted, otherwise menu
            // entry). Features that only apply to certain providers or workspaces — View Changes
            // (git repo only), Session History (Claude Code only), Show Usage (Claude/Devin only) —
            // are no longer hidden for the active agent; instead each shows a friendly explanation
            // at click time when it isn't applicable (issue #97).
            Apply(ToolbarButton.UpdateAgent, true, UpdateAgentToolbarButton, UpdateAgentMenuItem);
            Apply(ToolbarButton.DetachTerminal, true, DetachToolbarButton, DetachTerminalMenuItem);
            Apply(ToolbarButton.RestartAgent, true, RestartTerminalButton, RestartTerminalMenuItem);
            Apply(ToolbarButton.ViewChanges, true, ViewChangesToolbarButton, ViewChangesMenuItem);
            Apply(ToolbarButton.SessionHistory, true, SessionHistoryToolbarButton, SessionHistoryViewMenuItem);
            Apply(ToolbarButton.ShowUsage, true, ShowUsageToolbarButton, ShowUsageViewMenuItem);
            Apply(ToolbarButton.SetWorkingDirectory, true, SetWorkingDirectoryToolbarButton, SetWorkingDirectoryMenuItem);

            // The Tools dropdown button is shown only when at least one feature is parked in it
            // (not promoted to its own button, and its constraint allows it). When every feature is
            // a toolbar button the dropdown is empty, so it collapses instead of showing an empty menu.
            bool anyInDropdown =
                IsMenuItemVisible(UpdateAgentMenuItem) ||
                IsMenuItemVisible(RestartTerminalMenuItem) ||
                IsMenuItemVisible(DetachTerminalMenuItem) ||
                IsMenuItemVisible(ViewChangesMenuItem) ||
                IsMenuItemVisible(SessionHistoryViewMenuItem) ||
                IsMenuItemVisible(ShowUsageViewMenuItem) ||
                IsMenuItemVisible(SetWorkingDirectoryMenuItem);
            if (ToolsDropdownButton != null)
                ToolsDropdownButton.Visibility = anyInDropdown ? Visibility.Visible : Visibility.Collapsed;

            // Keep the detach control's icon/tooltip in sync with the detached state.
            UpdateDetachButtonIcon(_isTerminalDetached);
        }

        private static bool IsMenuItemVisible(System.Windows.Controls.MenuItem item)
            => item != null && item.Visibility == Visibility.Visible;

        /// <summary>Default left-to-right order of the configurable toolbar features.</summary>
        private static readonly ToolbarButton[] DefaultToolbarButtonOrder =
        {
            ToolbarButton.UpdateAgent, ToolbarButton.DetachTerminal, ToolbarButton.RestartAgent,
            ToolbarButton.ViewChanges, ToolbarButton.SessionHistory, ToolbarButton.ShowUsage,
            ToolbarButton.SetWorkingDirectory
        };

        /// <summary>
        /// Returns the configured feature order, completed with any features missing from the saved
        /// list (appended in default order) and stripped of unknowns/duplicates — so it always
        /// contains exactly the seven features in a valid order.
        /// </summary>
        private System.Collections.Generic.List<ToolbarButton> GetEffectiveToolbarOrder()
        {
            var order = new System.Collections.Generic.List<ToolbarButton>();
            var saved = _settings?.ToolbarButtonOrder;
            if (saved != null)
            {
                foreach (var b in saved)
                    if (System.Array.IndexOf(DefaultToolbarButtonOrder, b) >= 0 && !order.Contains(b))
                        order.Add(b);
            }
            foreach (var b in DefaultToolbarButtonOrder)
                if (!order.Contains(b)) order.Add(b);
            return order;
        }

        private System.Windows.Controls.Button GetToolbarButtonControl(ToolbarButton b)
        {
            switch (b)
            {
                case ToolbarButton.UpdateAgent: return UpdateAgentToolbarButton;
                case ToolbarButton.DetachTerminal: return DetachToolbarButton;
                case ToolbarButton.RestartAgent: return RestartTerminalButton;
                case ToolbarButton.ViewChanges: return ViewChangesToolbarButton;
                case ToolbarButton.SessionHistory: return SessionHistoryToolbarButton;
                case ToolbarButton.ShowUsage: return ShowUsageToolbarButton;
                case ToolbarButton.SetWorkingDirectory: return SetWorkingDirectoryToolbarButton;
                default: return null;
            }
        }

        private System.Windows.Controls.MenuItem GetToolbarMenuItemControl(ToolbarButton b)
        {
            switch (b)
            {
                case ToolbarButton.UpdateAgent: return UpdateAgentMenuItem;
                case ToolbarButton.DetachTerminal: return DetachTerminalMenuItem;
                case ToolbarButton.RestartAgent: return RestartTerminalMenuItem;
                case ToolbarButton.ViewChanges: return ViewChangesMenuItem;
                case ToolbarButton.SessionHistory: return SessionHistoryViewMenuItem;
                case ToolbarButton.ShowUsage: return ShowUsageViewMenuItem;
                case ToolbarButton.SetWorkingDirectory: return SetWorkingDirectoryMenuItem;
                default: return null;
            }
        }

        /// <summary>
        /// Applies the configured feature order to both the toolbar feature buttons (moved to the
        /// front of the right-hand button panel, ahead of the ☰/🤖/⚙ buttons) and the ☰ Tools
        /// dropdown items. Called at startup and after the Toolbar settings are applied — not on every
        /// RefreshToolbarLayout, to avoid reshuffling while a menu is open.
        /// </summary>
        private void ReorderToolbarControls()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var order = GetEffectiveToolbarOrder();

            if (RightButtonsPanel != null)
            {
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    var btn = GetToolbarButtonControl(order[i]);
                    if (btn == null || !RightButtonsPanel.Children.Contains(btn)) continue;
                    RightButtonsPanel.Children.Remove(btn);
                    RightButtonsPanel.Children.Insert(0, btn);
                }
            }

            if (ToolsContextMenu != null)
            {
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    var item = GetToolbarMenuItemControl(order[i]);
                    if (item == null || !ToolsContextMenu.Items.Contains(item)) continue;
                    ToolsContextMenu.Items.Remove(item);
                    ToolsContextMenu.Items.Insert(0, item);
                }
            }
        }

        private AiProvider? GetActiveOrSelectedProvider()
        {
            if (_currentRunningProvider.HasValue)
            {
                return _currentRunningProvider.Value;
            }

            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                return null;
            }

            return _settings?.SelectedProvider;
        }

        private static bool IsClaudeProvider(AiProvider? provider)
        {
            return provider == AiProvider.ClaudeCode || provider == AiProvider.ClaudeCodeWSL;
        }

        private static bool IsCodexProvider(AiProvider? provider)
        {
            return provider == AiProvider.Codex || provider == AiProvider.CodexNative;
        }

        private static bool IsCursorAgentProvider(AiProvider? provider)
        {
            return provider == AiProvider.CursorAgent || provider == AiProvider.CursorAgentNative;
        }

        /// <summary>
        /// Updates the tool window title to reflect the current provider/model
        /// </summary>
        /// <param name="title">Tool window title</param>
        private void UpdateToolWindowTitle(string title)
        {
            try
            {
#pragma warning disable VSSDK007, VSTHRD110 // Fire-and-forget to avoid blocking the caller
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _toolWindow?.UpdateTitle(title);
                    _detachedTerminalWindow?.UpdateCaption(title);
                });
#pragma warning restore VSSDK007, VSTHRD110
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating tool window title: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the provider name without model detail for compact labels inside the control.
        /// </summary>
        private string GetProviderDisplayName(AiProvider? provider)
        {
            switch (provider)
            {
                case AiProvider.CursorAgentNative:
                case AiProvider.CursorAgent:
                    return "Cursor Agent";
                case AiProvider.CodexNative:
                case AiProvider.Codex:
                    return "Codex";
                case AiProvider.ClaudeCodeWSL:
                case AiProvider.ClaudeCode:
                    return "Claude Code";
                case AiProvider.OpenCode:
                    return "Open Code";
                case AiProvider.Devin:
                    return "Devin";
                case AiProvider.DevinNative:
                    return "Devin";
                case AiProvider.Pi:
                    return "PI";
                case AiProvider.Antigravity:
                    return "Antigravity";
                case AiProvider.Reasonix:
                    return "Reasonix";
                default:
                    return "CMD";
            }
        }

        /// <summary>
        /// Gets the Visual Studio tool window title for the selected/running provider.
        /// Includes model detail for providers where the model can be changed.
        /// </summary>
        private string GetExtensionTitle(AiProvider? provider)
        {
            string providerName = GetProviderDisplayName(provider);

            switch (provider)
            {
                case AiProvider.ClaudeCode:
                case AiProvider.ClaudeCodeWSL:
                    return providerName + " - " + GetClaudeModelDisplayName();
                case AiProvider.Devin:
                case AiProvider.DevinNative:
                    string devinModel = GetDevinModelDisplayName();
                    return string.IsNullOrEmpty(devinModel) ? providerName : providerName + " - " + devinModel;
                default:
                    return providerName;
            }
        }

        private string GetClaudeModelDisplayName()
        {
            switch (_settings?.SelectedClaudeModel)
            {
                case ClaudeModel.Opus:
                    return "Opus";
                case ClaudeModel.Haiku:
                    return "Haiku";
                case ClaudeModel.Sonnet:
                default:
                    return "Sonnet";
            }
        }

        /// <summary>
        /// Returns the currently selected Devin model name, resolving to the first
        /// configured model when the saved selection is empty or no longer in the list.
        /// Returns an empty string when no models are configured.
        /// </summary>
        private string GetDevinModelDisplayName()
        {
            var models = _settings?.DevinModels;
            string selected = _settings?.SelectedDevinModel;

            if (models != null && models.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(selected) && models.Contains(selected))
                {
                    return selected;
                }
                return models[0];
            }

            return string.IsNullOrWhiteSpace(selected) ? string.Empty : selected;
        }

        #endregion

        #region Menu Handlers

        /// <summary>
        /// Handles set terminal type menu item click - shows dialog to select Command Prompt or Windows Terminal
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void SetTerminalTypeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            TerminalType currentType = _settings.SelectedTerminalType;

            // Resolve VS theme colors for the dialog
            System.Windows.Media.Brush themeBg;
            System.Windows.Media.Brush themeFg;
            try
            {
                themeBg = (System.Windows.Media.SolidColorBrush)FindResource(VsBrushes.WindowKey);
                themeFg = (System.Windows.Media.SolidColorBrush)FindResource(VsBrushes.WindowTextKey);
            }
            catch
            {
                themeBg = System.Windows.SystemColors.WindowBrush;
                themeFg = System.Windows.SystemColors.WindowTextBrush;
            }

            // Show a simple dialog with radio button options
            var window = new System.Windows.Window
            {
                Title = "Select Terminal Type",
                Width = 400,
                Height = 240,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };

            // Try to set owner to VS main window
            try
            {
                window.Owner = System.Windows.Application.Current?.MainWindow;
            }
            catch
            {
                // Ignore if owner cannot be set
            }

            var grid = new System.Windows.Controls.Grid();
            grid.Margin = new System.Windows.Thickness(15);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var stackPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            // Command Prompt option
            var cmdRadio = new System.Windows.Controls.RadioButton
            {
                Content = "Command Prompt (default)",
                Margin = new System.Windows.Thickness(0, 10, 0, 15),
                IsChecked = currentType == TerminalType.CommandPrompt,
                Foreground = themeFg
            };
            stackPanel.Children.Add(cmdRadio);

            // Windows Terminal option
            var wtRadio = new System.Windows.Controls.RadioButton
            {
                Content = "Windows Terminal (better emoji/unicode support)",
                Margin = new System.Windows.Thickness(0, 0, 0, 15),
                IsChecked = currentType == TerminalType.WindowsTerminal,
                Foreground = themeFg
            };
            stackPanel.Children.Add(wtRadio);

            // Note label
            var noteLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Note: Windows Terminal requires installation. If not found, it will show installation instructions.",
                FontSize = 11,
                Foreground = themeFg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 10, 0, 0),
                Opacity = 0.8
            };
            stackPanel.Children.Add(noteLabel);

            System.Windows.Controls.Grid.SetRow(stackPanel, 0);
            grid.Children.Add(stackPanel);

            // Button panel
            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            System.Windows.Controls.Grid.SetRow(buttonPanel, 1);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 75,
                Height = 25,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                IsDefault = true,
                Background = themeBg,
                Foreground = themeFg
            };
            okButton.Click += (s, okArgs) => window.DialogResult = true;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 75,
                Height = 25,
                IsCancel = true,
                Background = themeBg,
                Foreground = themeFg
            };
            cancelButton.Click += (s, cancelArgs) => window.DialogResult = false;
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(buttonPanel);
            window.Content = grid;

            if (window.ShowDialog() == true)
            {
                TerminalType selectedType = wtRadio.IsChecked == true ? TerminalType.WindowsTerminal : TerminalType.CommandPrompt;

                // If Windows Terminal selected, check if it's available
                if (selectedType == TerminalType.WindowsTerminal)
                {
                    bool wtAvailable = await IsWindowsTerminalAvailableAsync();
                    if (!wtAvailable)
                    {
                        MessageBox.Show(
                            "Windows Terminal (wt.exe) was not found in PATH.\n\n" +
                            "To install, open Command Prompt as Administrator and run:\n\n" +
                            "    winget install --id Microsoft.WindowsTerminal -e\n\n" +
                            "Or install from:\n" +
                            "• Microsoft Store: https://aka.ms/terminal\n" +
                            "• GitHub: https://github.com/microsoft/terminal/releases\n\n" +
                            "After installing, restart Visual Studio and try again.\n\n" +
                            "(Press Ctrl+C to copy this message)",
                            "Windows Terminal Not Found",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }
                }

                if (selectedType != currentType)
                {
                    _settings.SelectedTerminalType = selectedType;
                    SaveSettings();

                    // Restart terminal to apply new terminal type
                    try
                    {
                        await RestartTerminalWithSelectedProviderAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error restarting terminal after terminal type change: {ex.Message}");
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Handles set theme menu item click - shows dialog to select Automatic, Dark, or Light theme
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void SetThemeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            ThemePreference currentPref = _settings.SelectedThemePreference;

            // Resolve VS theme colors for the dialog
            System.Windows.Media.Brush themeBg;
            System.Windows.Media.Brush themeFg;
            try
            {
                themeBg = (System.Windows.Media.SolidColorBrush)FindResource(VsBrushes.WindowKey);
                themeFg = (System.Windows.Media.SolidColorBrush)FindResource(VsBrushes.WindowTextKey);
            }
            catch
            {
                themeBg = System.Windows.SystemColors.WindowBrush;
                themeFg = System.Windows.SystemColors.WindowTextBrush;
            }

            // Show a simple dialog with radio button options
            var window = new System.Windows.Window
            {
                Title = "Select Theme",
                Width = 400,
                Height = 240,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };

            // Try to set owner to VS main window
            try
            {
                window.Owner = System.Windows.Application.Current?.MainWindow;
            }
            catch
            {
                // Ignore if owner cannot be set
            }

            var grid = new System.Windows.Controls.Grid();
            grid.Margin = new System.Windows.Thickness(15);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var stackPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            // Automatic option
            var autoRadio = new System.Windows.Controls.RadioButton
            {
                Content = "Automatic (follow Visual Studio theme)",
                Margin = new System.Windows.Thickness(0, 10, 0, 15),
                IsChecked = currentPref == ThemePreference.Automatic,
                Foreground = themeFg
            };
            stackPanel.Children.Add(autoRadio);

            // Dark option
            var darkRadio = new System.Windows.Controls.RadioButton
            {
                Content = "Dark",
                Margin = new System.Windows.Thickness(0, 0, 0, 15),
                IsChecked = currentPref == ThemePreference.Dark,
                Foreground = themeFg
            };
            stackPanel.Children.Add(darkRadio);

            // Light option
            var lightRadio = new System.Windows.Controls.RadioButton
            {
                Content = "Light",
                Margin = new System.Windows.Thickness(0, 0, 0, 15),
                IsChecked = currentPref == ThemePreference.Light,
                Foreground = themeFg
            };
            stackPanel.Children.Add(lightRadio);

            System.Windows.Controls.Grid.SetRow(stackPanel, 0);
            grid.Children.Add(stackPanel);

            // Button panel
            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            System.Windows.Controls.Grid.SetRow(buttonPanel, 1);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 75,
                Height = 25,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                IsDefault = true,
                Background = themeBg,
                Foreground = themeFg
            };
            okButton.Click += (s, okArgs) => window.DialogResult = true;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 75,
                Height = 25,
                IsCancel = true,
                Background = themeBg,
                Foreground = themeFg
            };
            cancelButton.Click += (s, cancelArgs) => window.DialogResult = false;
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(buttonPanel);
            window.Content = grid;

            if (window.ShowDialog() == true)
            {
                ThemePreference selectedPref = ThemePreference.Automatic;
                if (darkRadio.IsChecked == true)
                    selectedPref = ThemePreference.Dark;
                else if (lightRadio.IsChecked == true)
                    selectedPref = ThemePreference.Light;

                if (selectedPref != currentPref)
                {
                    _settings.SelectedThemePreference = selectedPref;
                    SaveSettings();

                    // Update terminal theme immediately
                    UpdateTerminalTheme();
                    UpdateInlineUsageBarColors();

                    // Skip the restart prompt entirely when no terminal is
                    // running, or when the new panel color matches what the
                    // agent was launched with -- e.g. forcing Dark while VS
                    // is already on a dark theme that resolves to the same
                    // RGB. Same-color restarts are pure churn.
                    if (terminalHandle == IntPtr.Zero || !IsWindow(terminalHandle))
                        return;
                    if (terminalPanel != null &&
                        _terminalAgentColor != System.Drawing.Color.Empty &&
                        terminalPanel.BackColor == _terminalAgentColor)
                        return;

                    // Restart terminal to apply new theme colors
                    var result = MessageBox.Show(
                        "Theme preference changed. Restart the AI code agent to apply the new terminal colors?",
                        "Theme Changed",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            await RestartTerminalWithSelectedProviderAsync();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error restarting terminal after theme change: {ex.Message}");
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Handles About menu item click - displays extension information
        /// </summary>
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}";
            string aboutMessage = $"Claude Code Extension for Visual Studio\n\n" +
                                $"Version: {version}\n" +
                                $"Author: Daniel Carvalho Liedke\n" +
                                $"Copyright © Daniel Carvalho Liedke 2026\n\n" +
                                $"Provides seamless integration with Claude Code, Codex, Cursor Agent, Open Code, Devin, Devin, PI, Antigravity and Reasonix AI assistants directly within Visual Studio 2022/2026 IDE.";

            MessageBox.Show(aboutMessage, "About Claude Code Extension",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Handles dropdown button click - shows the provider selection menu
        /// </summary>
        private void MenuDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the context menu when the dropdown button is clicked
            var button = sender as System.Windows.Controls.Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Handles model dropdown button click - shows the model selection menu
        /// </summary>
        private void ModelDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the context menu when the model dropdown button is clicked
            var button = sender as System.Windows.Controls.Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Shows Claude-specific or Devin-specific model items based on the current provider
        /// </summary>
        private void ModelContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            AiProvider? activeProvider = GetActiveOrSelectedProvider();
            // Devin (WSL) and Devin (native) both run the `devin` CLI and share the model list.
            bool isDevin = activeProvider == AiProvider.Devin || activeProvider == AiProvider.DevinNative;
            bool isClaude = !isDevin;

            // Claude-specific items
            OpusMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            SonnetMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            HaikuMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortSeparator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortSliderMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;

            // Keep the slider in sync with the persisted effort level whenever the menu opens.
            if (isClaude)
            {
                UpdateEffortSelection();
            }
            ClaudeAccountSeparator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            ChangeAccountMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            SetLanguageMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            InstallCavemanMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            TuiFullscreenMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;

            // Devin-specific items: the model entries are rebuilt dynamically from the
            // user-configurable _settings.DevinModels list each time the menu opens.
            RebuildDevinModelMenuItems(isDevin);
            DevinModelsSeparator.Visibility = isDevin ? Visibility.Visible : Visibility.Collapsed;
            DevinConfigureModelsMenuItem.Visibility = isDevin ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Dynamically-inserted Devin model menu items, tracked so they can be removed and
        /// rebuilt each time the model menu opens (the list is user-configurable).
        /// </summary>
        private readonly System.Collections.Generic.List<System.Windows.Controls.MenuItem> _dynamicDevinModelItems
            = new System.Collections.Generic.List<System.Windows.Controls.MenuItem>();

        /// <summary>
        /// Rebuilds the Devin model menu items from <c>_settings.DevinModels</c>, inserting one
        /// checkable item per configured model at the top of the model context menu. Removes any
        /// previously-inserted dynamic items first. No-op (just clears) when Devin is not active.
        /// </summary>
        private void RebuildDevinModelMenuItems(bool show)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (var item in _dynamicDevinModelItems)
            {
                ModelContextMenu.Items.Remove(item);
            }
            _dynamicDevinModelItems.Clear();

            if (!show || _settings == null) return;

            EnsureDevinModelDefaults();

            string selected = GetDevinModelDisplayName();
            int insertIndex = 0;
            foreach (var model in _settings.DevinModels)
            {
                if (string.IsNullOrWhiteSpace(model)) continue;
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = model,
                    IsCheckable = true,
                    IsChecked = string.Equals(model, selected, StringComparison.Ordinal),
                    Tag = model
                };
                item.Click += DevinModelMenuItem_Click;
                ModelContextMenu.Items.Insert(insertIndex++, item);
                _dynamicDevinModelItems.Add(item);
            }
        }

        /// <summary>
        /// Ensures the Devin model list exists and the saved selection is one of its entries.
        /// </summary>
        private void EnsureDevinModelDefaults()
        {
            if (_settings == null) return;
            if (_settings.DevinModels == null)
            {
                _settings.DevinModels = new System.Collections.Generic.List<string>();
            }
            if (_settings.DevinModels.Count > 0 &&
                (string.IsNullOrWhiteSpace(_settings.SelectedDevinModel) ||
                 !_settings.DevinModels.Contains(_settings.SelectedDevinModel)))
            {
                _settings.SelectedDevinModel = _settings.DevinModels[0];
            }
        }

        /// <summary>
        /// Handles Opus menu item click - switches to Opus model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void OpusMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _settings.SelectedClaudeModel = ClaudeModel.Opus;
            UpdateModelSelection();
            SaveSettings();

            // Send /model command directly without restarting terminal
            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync("/model opus");
            }
        }

        /// <summary>
        /// Handles Sonnet menu item click - switches to Sonnet model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void SonnetMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _settings.SelectedClaudeModel = ClaudeModel.Sonnet;
            UpdateModelSelection();
            SaveSettings();

            // Send /model command directly without restarting terminal
            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync("/model sonnet");
            }
        }

        /// <summary>
        /// Handles Haiku menu item click - switches to Haiku model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void HaikuMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _settings.SelectedClaudeModel = ClaudeModel.Haiku;
            UpdateModelSelection();
            SaveSettings();

            // Send /model command directly without restarting terminal
            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync("/model haiku");
            }
        }

        /// <summary>
        /// Updates the model selection UI checkmarks
        /// </summary>
        private void UpdateModelSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            // Update Claude menu item checkmarks
            OpusMenuItem.IsChecked = _settings.SelectedClaudeModel == ClaudeModel.Opus;
            SonnetMenuItem.IsChecked = _settings.SelectedClaudeModel == ClaudeModel.Sonnet;
            HaikuMenuItem.IsChecked = _settings.SelectedClaudeModel == ClaudeModel.Haiku;

            // Update Devin dynamic model item checkmarks (items are rebuilt on menu open)
            string devinSelected = GetDevinModelDisplayName();
            foreach (var item in _dynamicDevinModelItems)
            {
                item.IsChecked = string.Equals(item.Tag as string, devinSelected, StringComparison.Ordinal);
            }

            // Update effort selection checkmarks
            UpdateEffortSelection();

            UpdateToolWindowTitle(GetExtensionTitle(GetActiveOrSelectedProvider()));
        }

        #endregion

        #region Devin Model Selection

        /// <summary>
        /// Handles a click on a dynamically-built Devin model menu item. Records the selection
        /// and, when a Devin terminal is running, switches the model live via
        /// <c>/model "&lt;name&gt;"</c> (quoted so names with spaces are passed as one argument).
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void DevinModelMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            var item = sender as System.Windows.Controls.MenuItem;
            string model = item?.Tag as string;
            if (string.IsNullOrWhiteSpace(model)) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _settings.SelectedDevinModel = model;
            UpdateModelSelection();
            SaveSettings();
            if (_currentRunningProvider == AiProvider.Devin || _currentRunningProvider == AiProvider.DevinNative)
            {
                await SendTextToTerminalAsync($"/model \"{model}\"");
            }
        }

        /// <summary>
        /// Handles the "Configure Models..." menu item click - opens the editor for the
        /// user-configurable Devin model list, then rebuilds the menu/title on close.
        /// </summary>
        private void DevinConfigureModelsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (_settings == null) _settings = new ClaudeCodeSettings();
                if (_settings.DevinModels == null) _settings.DevinModels = new System.Collections.Generic.List<string>();

                ShowDevinModelsDialog();

                EnsureDevinModelDefaults();
                SaveSettings();
                UpdateModelSelection();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error configuring Devin models: {ex.Message}");
                MessageBox.Show($"Error configuring Devin models: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles Devin Show Usage menu item click - opens the Devin usage page in the browser
        /// </summary>
        private void DevinShowUsageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://windsurf.com/subscription/usage?referrer=windsurf");
        }

        #endregion

        #region Effort Level Selection

        /// <summary>
        /// Handles effort level menu item click - sends /effort command to terminal
        /// </summary>
        private async Task SetEffortLevelAsync(EffortLevel level)
        {
            if (_settings == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _settings.SelectedEffortLevel = level;
            if (!IsSessionOnlyEffort(level))
            {
                _lastPersistableEffortLevel = level;
            }
            UpdateEffortSelection();
            SaveSettings();

            // Send /effort command to Claude Code terminal
            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync($"/effort {level.ToString().ToLower()}");
            }
        }

        // Effort slider state. The slider's left-to-right order is defined explicitly so it
        // is independent of the EffortLevel enum's integer values — that keeps persisted
        // settings stable as new levels are appended. Mirrors the VS Code effort slider:
        // Low, Medium, High, Extra High (xhigh), Max, Ultracode (xhigh + workflows).
        private static readonly EffortLevel[] _effortSliderOrder =
        {
            EffortLevel.Low, EffortLevel.Medium, EffortLevel.High,
            EffortLevel.XHigh, EffortLevel.Max, EffortLevel.Ultracode
        };
        private bool _suppressEffortSliderChange;
        private bool _effortSliderDragging;
        private EffortLevel _pendingEffortLevel = EffortLevel.High;

        // Max and Ultracode are session-only, mirroring the Claude Code CLI where
        // "/effort max" is applied for "this session only" and Ultracode (xhigh +
        // dynamic workflows) is likewise transient. They apply live to the running
        // session but are never persisted, so the next VS launch starts from the
        // last durable level (Low/Medium/High/Extra High) instead.
        private static bool IsSessionOnlyEffort(EffortLevel level)
            => level == EffortLevel.Max || level == EffortLevel.Ultracode;

        // The last durable (non-session-only) effort level the user selected. This is
        // what SaveSettings writes when the live level is a session-only one, so config
        // never carries Max/Ultracode across sessions.
        private EffortLevel _lastPersistableEffortLevel = EffortLevel.High;

        private static EffortLevel EffortFromSliderIndex(double value)
        {
            int idx = (int)Math.Round(value);
            if (idx < 0) idx = 0;
            if (idx >= _effortSliderOrder.Length) idx = _effortSliderOrder.Length - 1;
            return _effortSliderOrder[idx];
        }

        private static int EffortToSliderIndex(EffortLevel level)
        {
            int idx = Array.IndexOf(_effortSliderOrder, level);
            // Levels not on the slider (e.g. legacy Auto) fall back to High.
            if (idx < 0) idx = Array.IndexOf(_effortSliderOrder, EffortLevel.High);
            return idx < 0 ? 0 : idx;
        }

        /// <summary>
        /// Slider value changed. Updates the label live; commits the new level immediately
        /// for click/keyboard changes, but defers commits made while dragging until release
        /// (see <see cref="EffortSlider_DragCompleted"/>) so we don't spam /effort commands.
        /// </summary>
        private void EffortSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;

            EffortLevel level = EffortFromSliderIndex(e.NewValue);
            UpdateEffortLabel(level);

            if (_suppressEffortSliderChange) return;

            if (_effortSliderDragging)
            {
                _pendingEffortLevel = level;
                return;
            }

            _ = SetEffortLevelAsync(level);
        }

        private void EffortSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _effortSliderDragging = true;
            _pendingEffortLevel = EffortFromSliderIndex(EffortSlider.Value);
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void EffortSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _effortSliderDragging = false;
            await SetEffortLevelAsync(_pendingEffortLevel);
        }
#pragma warning restore VSTHRD100

        /// <summary>
        /// Updates the effort slider position and label to match the persisted selection.
        /// </summary>
        private void UpdateEffortSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null || EffortSlider == null) return;

            _suppressEffortSliderChange = true;
            try
            {
                EffortSlider.Value = EffortToSliderIndex(_settings.SelectedEffortLevel);
            }
            finally
            {
                _suppressEffortSliderChange = false;
            }

            UpdateEffortLabel(_settings.SelectedEffortLevel);
        }

        /// <summary>
        /// Updates the "Effort (Level)" caption above the slider.
        /// </summary>
        private void UpdateEffortLabel(EffortLevel level)
        {
            if (EffortSliderLabel == null) return;

            string name;
            switch (level)
            {
                case EffortLevel.XHigh: name = "Extra High"; break;
                case EffortLevel.Ultracode: name = "Ultracode - xhigh + workflows"; break;
                default: name = level.ToString(); break;
            }
            EffortSliderLabel.Text = $"Effort ({name})";
        }

        #endregion

        #region Config Menu Handlers

        /// <summary>
        /// Handles Show Usage menu item click - sends /usage command directly
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void ShowUsageMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync("/usage");
            }
        }

        /// <summary>
        /// Handles Change Account menu item click - sends /logout, prompts user, then resumes claude
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void ChangeAccountMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                // Send /logout command
                await SendTextToTerminalAsync("/logout");

                // Sign out the embedded usage WebView2 so the new account is picked up
                await SignOutUsageWindowIfActiveAsync();

                // Wait for logout to complete
                await Task.Delay(3000);

                // Prompt user to switch accounts in the browser
                MessageBox.Show(
                    "Please switch to the desired account in your browser, then click OK to resume Claude Code.",
                    "Change Account",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // Build resume command with --dangerously-skip-permissions if needed
                bool isWsl = _currentRunningProvider == AiProvider.ClaudeCodeWSL;
                string baseCmd = "claude --resume";
                if (_settings?.ClaudeDangerouslySkipPermissions == true)
                {
                    baseCmd += " --dangerously-skip-permissions";
                }

                string resumeCommand;
                if (isWsl)
                {
                    resumeCommand = $"wsl bash -lic \"{baseCmd}\"";
                }
                else
                {
                    string claudeCmd = GetClaudeCommand(isWsl: false).Replace(" --dangerously-skip-permissions", "");
                    resumeCommand = $"{claudeCmd} --resume";
                    if (_settings?.ClaudeDangerouslySkipPermissions == true)
                    {
                        resumeCommand += " --dangerously-skip-permissions";
                    }
                }

                await SendTextToTerminalAsync(resumeCommand);
            }
        }

        /// <summary>
        /// Handles Set Language menu item click - sends /config, types language, navigates and selects
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void SetLanguageMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync("/config");
                await Task.Delay(1500);

                bool isWindowsTerminal = _wtTabBarHeight > 0;

                // Type "language" to filter
                foreach (char c in "language")
                {
                    if (isWindowsTerminal)
                    {
                        // For Windows Terminal, use keybd_event (PostMessage WM_CHAR doesn't work)
                        short vk = (short)(c >= 'a' && c <= 'z' ? c - 32 : c); // to uppercase VK
                        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
                        await Task.Delay(30);
                        keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    else
                    {
                        PostMessage(terminalHandle, WM_CHAR, new IntPtr(c), IntPtr.Zero);
                    }
                    await Task.Delay(50);
                }
                await Task.Delay(500);

                // Press Down arrow to highlight
                if (isWindowsTerminal)
                {
                    keybd_event(VK_DOWN, 0, 0, UIntPtr.Zero);
                    await Task.Delay(30);
                    keybd_event(VK_DOWN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                else
                {
                    PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_DOWN), IntPtr.Zero);
                    PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_DOWN), IntPtr.Zero);
                }
                await Task.Delay(200);

                // Press Space to select
                if (isWindowsTerminal)
                {
                    keybd_event(VK_SPACE, 0, 0, UIntPtr.Zero);
                    await Task.Delay(30);
                    keybd_event(VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                else
                {
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_SPACE), IntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// Handles Install Caveman menu item click - installs the Caveman plugin (JuliusBrussee/caveman)
        /// inside the running Claude Code session via /plugin slash commands
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void InstallCavemanMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_currentRunningProvider != AiProvider.ClaudeCode &&
                _currentRunningProvider != AiProvider.ClaudeCodeWSL)
            {
                return;
            }

            var confirm = MessageBox.Show(
                "This will install the Caveman plugin (JuliusBrussee/caveman) into the current Claude Code session.\n\n" +
                "The following slash commands will be sent:\n" +
                "  /plugin marketplace add JuliusBrussee/caveman\n" +
                "  /plugin install caveman@caveman --scope user\n" +
                "  /reload-plugins\n" +
                "  /caveman\n" +
                "  hi\n\n" +
                "Claude Code may prompt you to confirm trust for the marketplace and plugin — please respond inside the terminal if asked.\n\n" +
                "Please be patient while the marketplace and plugin are downloaded and installed — do not type anything in the terminal until all commands have completed.\n\n" +
                "Continue?",
                "Install Caveman",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            await SendTextToTerminalAsync("/plugin marketplace add JuliusBrussee/caveman");
            await Task.Delay(7000);

            await SendTextToTerminalAsync("/plugin install caveman@caveman --scope user");
            await Task.Delay(4000);

            // Send Enter to confirm any prompt that Claude Code may show after the install command
            SendEnterKey();
            await Task.Delay(1500);

            await SendTextToTerminalAsync("/reload-plugins");
            await Task.Delay(3000);

            await SendTextToTerminalAsync("/caveman");
            await Task.Delay(2000);

            await SendTextToTerminalAsync("yes");
        }

        /// <summary>
        /// Handles Enable Fullscreen menu item click - switches Claude Code into fullscreen
        /// (flicker-free, alternate-screen) rendering via the /tui fullscreen slash command
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void TuiFullscreenEnableMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await SendTuiModeCommandAsync("fullscreen");
        }

        /// <summary>
        /// Handles Disable Fullscreen menu item click - switches Claude Code back to the classic
        /// renderer (native terminal scrollback) via the /tui default slash command
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void TuiFullscreenDisableMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await SendTuiModeCommandAsync("default");
        }

        /// <summary>
        /// Sets the Claude Code TUI rendering preference ("fullscreen" or "default"), persists it,
        /// and — after the user confirms — restarts the Code Agent so it relaunches with the matching
        /// environment variable (CLAUDE_CODE_NO_FLICKER / CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN).
        /// </summary>
        private async Task SendTuiModeCommandAsync(string mode)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_currentRunningProvider != AiProvider.ClaudeCode &&
                _currentRunningProvider != AiProvider.ClaudeCodeWSL)
            {
                return;
            }

            bool enableFullscreen = mode == "fullscreen";
            string modeLabel = enableFullscreen ? "fullscreen (flicker-free)" : "default (classic)";

            var confirm = MessageBox.Show(
                "This will switch Claude Code to " + modeLabel + " terminal rendering.\n\n" +
                "The Code Agent must be restarted to apply the new renderer. Clicking OK will restart it now — " +
                "the current session will be terminated. Your work in progress in the terminal will be lost.\n\n" +
                "Continue?",
                "TUI Fullscreen",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            if (_settings != null)
            {
                _settings.ClaudeTuiFullscreen = enableFullscreen;
                SaveSettings();
            }

            await RestartTerminalWithSelectedProviderAsync();
        }

        #endregion

        #region Provider Context Menu

        /// <summary>
        /// Handles the provider context menu opening - shows/hides git-specific options
        /// </summary>
        private void ProviderContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Show/hide menu options based on context
            AiProvider? activeProvider = GetActiveOrSelectedProvider();
            bool isClaudeProvider = IsClaudeProvider(activeProvider);
            bool isCodexProvider = IsCodexProvider(activeProvider);
            bool isCursorAgentProvider = IsCursorAgentProvider(activeProvider);
            bool isDevinProvider = activeProvider == AiProvider.Devin;
            bool isPiProvider = activeProvider == AiProvider.Pi;
            bool isAntigravityProvider = activeProvider == AiProvider.Antigravity;
            bool isReasonixProvider = activeProvider == AiProvider.Reasonix;
            bool isDevinNativeProvider = activeProvider == AiProvider.DevinNative;

            // Show/hide individual provider menu items based on VisibleProviders.
            // The currently selected provider is always shown so users keep access to it.
            ApplyProviderMenuVisibility();

            AutoOpenChangesSeparator.Visibility = (isClaudeProvider || isCodexProvider || isCursorAgentProvider || isDevinProvider || isPiProvider || isAntigravityProvider || isReasonixProvider || isDevinNativeProvider) ? Visibility.Visible : Visibility.Collapsed;
            ClaudeDangerouslySkipPermissionsMenuItem.Visibility = isClaudeProvider ? Visibility.Visible : Visibility.Collapsed;
            CodexFullAutoMenuItem.Visibility = isCodexProvider ? Visibility.Visible : Visibility.Collapsed;
            CursorAgentAutoRunMenuItem.Visibility = isCursorAgentProvider ? Visibility.Visible : Visibility.Collapsed;
            DevinDangerousModeMenuItem.Visibility = (isDevinProvider || isDevinNativeProvider) ? Visibility.Visible : Visibility.Collapsed;
            AntigravityDangerouslySkipPermissionsMenuItem.Visibility = isAntigravityProvider ? Visibility.Visible : Visibility.Collapsed;

            // Update checkbox state from settings
            if (_settings != null)
            {
                ClaudeDangerouslySkipPermissionsMenuItem.IsChecked = _settings.ClaudeDangerouslySkipPermissions;
                CodexFullAutoMenuItem.IsChecked = _settings.CodexFullAuto;
                CursorAgentAutoRunMenuItem.IsChecked = _settings.CursorAgentAutoRun;
                DevinDangerousModeMenuItem.IsChecked = _settings.DevinDangerousMode;
                AntigravityDangerouslySkipPermissionsMenuItem.IsChecked = _settings.AntigravityDangerouslySkipPermissions;
            }
        }

        /// <summary>
        /// Opens the Tools dropdown (the configurable features the user did not promote to their own
        /// toolbar button).
        /// </summary>
        private void ToolsDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (ToolsDropdownButton?.ContextMenu != null)
            {
                ToolsDropdownButton.ContextMenu.PlacementTarget = ToolsDropdownButton;
                ToolsDropdownButton.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Refreshes the Tools dropdown contents on open: usage state, the Set Working Directory
        /// header, and the button/menu swap so the parked feature entries reflect current context.
        /// </summary>
        private void ToolsContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SyncShowUsageMenuCheckState();
            UpdateSetWorkingDirectoryMenuHeader();
            RefreshToolbarLayout();
        }

        /// <summary>
        /// Updates the Set Working Directory menu item to show the current custom directory, with the
        /// path in red when it doesn't exist on disk. Shows the plain "Set Working Directory..." label
        /// when no custom directory is configured.
        /// </summary>
        private void UpdateSetWorkingDirectoryMenuHeader()
        {
            if (SetWorkingDirectoryMenuItem == null || _settings == null) return;

            if (!string.IsNullOrWhiteSpace(_settings.CustomWorkingDirectory))
            {
                string customDir = _settings.CustomWorkingDirectory.Trim();
                bool directoryExists = false;
                try
                {
                    if (Path.IsPathRooted(customDir))
                    {
                        directoryExists = Directory.Exists(customDir);
                    }
                    else
                    {
                        // Resolve relative path against cached workspace directory
                        // to avoid blocking the UI thread during menu open
                        string baseDir = _lastWorkspaceDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        string resolved = Path.GetFullPath(Path.Combine(baseDir, customDir));
                        directoryExists = Directory.Exists(resolved);
                    }
                }
                catch
                {
                    directoryExists = false;
                }

                var headerBlock = new System.Windows.Controls.TextBlock();
                headerBlock.Inlines.Add("📁  Set Working Directory (");
                var pathRun = new System.Windows.Documents.Run(customDir);
                if (!directoryExists)
                {
                    pathRun.Foreground = System.Windows.Media.Brushes.Red;
                }
                headerBlock.Inlines.Add(pathRun);
                headerBlock.Inlines.Add(")");
                SetWorkingDirectoryMenuItem.Header = headerBlock;
            }
            else
            {
                SetWorkingDirectoryMenuItem.Header = "📁  Set Working Directory...";
            }
        }

        /// <summary>
        /// Handles Claude dangerous skip permissions menu item click
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void ClaudeDangerouslySkipPermissionsMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            _settings.ClaudeDangerouslySkipPermissions = ClaudeDangerouslySkipPermissionsMenuItem.IsChecked;
            SaveSettings();

            // Reload Claude terminal immediately so the new startup flag is applied.
            if (_settings.SelectedProvider == AiProvider.ClaudeCode ||
                _settings.SelectedProvider == AiProvider.ClaudeCodeWSL)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reloading Claude Code after skip permissions change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to reload Claude Code: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Handles Codex full auto menu item click
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void CodexFullAutoMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            _settings.CodexFullAuto = CodexFullAutoMenuItem.IsChecked;
            SaveSettings();

            // Reload Codex terminal immediately so the new startup flag is applied.
            if (_settings.SelectedProvider == AiProvider.Codex ||
                _settings.SelectedProvider == AiProvider.CodexNative)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reloading Codex after full auto change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to reload Codex: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Handles Cursor Agent auto-run menu item click
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void CursorAgentAutoRunMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            _settings.CursorAgentAutoRun = CursorAgentAutoRunMenuItem.IsChecked;
            SaveSettings();

            // Reload Cursor Agent terminal immediately so the new startup flag is applied.
            if (_settings.SelectedProvider == AiProvider.CursorAgent ||
                _settings.SelectedProvider == AiProvider.CursorAgentNative)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reloading Cursor Agent after auto-run change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to reload Cursor Agent: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Handles Devin dangerous mode menu item click
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void DevinDangerousModeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            _settings.DevinDangerousMode = DevinDangerousModeMenuItem.IsChecked;
            SaveSettings();

            // Reload Devin terminal immediately so the new startup flag is applied.
            if (_settings.SelectedProvider == AiProvider.Devin)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reloading Devin after dangerous mode change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to reload Devin: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Handles Antigravity dangerous skip permissions menu item click
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void AntigravityDangerouslySkipPermissionsMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            _settings.AntigravityDangerouslySkipPermissions = AntigravityDangerouslySkipPermissionsMenuItem.IsChecked;
            SaveSettings();

            // Reload Antigravity terminal immediately so the new startup flag is applied.
            if (_settings.SelectedProvider == AiProvider.Antigravity)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reloading Antigravity after skip permissions change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to reload Antigravity: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Handles set working directory menu item click - prompts user for a custom working directory
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void SetWorkingDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            string currentValue = _settings.CustomWorkingDirectory ?? "";

            // Resolve base workspace directory for relative path validation in the dialog
            string baseDir = await GetBaseWorkspaceDirectoryAsync();

            // Show input dialog; returns null on Cancel, or the entered string on OK
            string input = ShowWorkingDirectoryInputDialog(currentValue, baseDir);
            if (input == null)
            {
                // User cancelled - no change
                return;
            }

            string trimmed = input.Trim();
            if (trimmed != currentValue)
            {
                _settings.CustomWorkingDirectory = trimmed;
                SaveSettings();

                // Restart terminal to apply the new working directory
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error restarting terminal after working directory change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Shows a WPF input dialog for the custom working directory setting.
        /// Validates the path in real-time, coloring the text red when the directory does not exist.
        /// </summary>
        /// <param name="currentValue">The current value to pre-populate</param>
        /// <param name="baseDir">The base workspace directory used to resolve relative paths</param>
        /// <returns>The entered string on OK, or null if the user cancelled</returns>
        private string ShowWorkingDirectoryInputDialog(string currentValue, string baseDir)
        {
            // Resolve VS theme colors for the dialog (same keys used in ClaudeCodeControl.Theme.cs)
            System.Windows.Media.Brush themeBg;
            System.Windows.Media.Brush themeFg;
            try
            {
                themeBg = (System.Windows.Media.SolidColorBrush)FindResource(VsBrushes.WindowKey);
                themeFg = (System.Windows.Media.SolidColorBrush)FindResource(VsBrushes.WindowTextKey);
            }
            catch
            {
                themeBg = System.Windows.SystemColors.WindowBrush;
                themeFg = System.Windows.SystemColors.WindowTextBrush;
            }

            // Build dialog window programmatically
            var dialog = new Window
            {
                Title = "Set Working Directory",
                Width = 500,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };

            // Try to set owner to VS main window
            try
            {
                dialog.Owner = Application.Current?.MainWindow;
            }
            catch
            {
                // Ignore if owner cannot be set
            }

            var grid = new System.Windows.Controls.Grid();
            grid.Margin = new Thickness(12);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            // Label
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Enter a custom working directory for the terminal:\n" +
                       "  - Absolute path (e.g. C:\\Projects\\MyRepo)\n" +
                       "  - Relative path to solution directory (e.g. ..\\OtherRepo)\n" +
                       "  - Leave empty to use the default solution directory",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Controls.Grid.SetRow(label, 0);
            grid.Children.Add(label);

            // Default foreground for restoring after validation
            var defaultForeground = themeFg;

            // Label theme
            label.Foreground = themeFg;

            // TextBox
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = currentValue,
                Margin = new Thickness(0, 0, 0, 12),
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg
            };
            textBox.SelectAll();
            System.Windows.Controls.Grid.SetRow(textBox, 1);
            grid.Children.Add(textBox);

            // Real-time path validation on text change
            textBox.TextChanged += (s, args) =>
            {
                string path = textBox.Text.Trim();
                if (string.IsNullOrEmpty(path))
                {
                    // Empty means default directory - always valid
                    textBox.Foreground = defaultForeground;
                    return;
                }

                bool exists = false;
                try
                {
                    if (Path.IsPathRooted(path))
                    {
                        exists = Directory.Exists(path);
                    }
                    else
                    {
                        string resolved = Path.GetFullPath(Path.Combine(baseDir, path));
                        exists = Directory.Exists(resolved);
                    }
                }
                catch
                {
                    exists = false;
                }

                textBox.Foreground = exists ? defaultForeground : System.Windows.Media.Brushes.Red;
            };

            // Button panel
            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            System.Windows.Controls.Grid.SetRow(buttonPanel, 3);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 75,
                Height = 25,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                Background = themeBg,
                Foreground = themeFg
            };
            okButton.Click += (s, args) => { dialog.DialogResult = true; };
            buttonPanel.Children.Add(okButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 75,
                Height = 25,
                IsCancel = true,
                Background = themeBg,
                Foreground = themeFg
            };
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(buttonPanel);
            dialog.Content = grid;

            // Focus the text box and trigger initial validation when loaded
            dialog.Loaded += (s, args) => { textBox.Focus(); };

            if (dialog.ShowDialog() == true)
            {
                return textBox.Text;
            }

            return null;
        }

        #endregion

        #region Visible Agents Configuration

        /// <summary>
        /// All provider menu items keyed by their <see cref="AiProvider"/> value.
        /// Built lazily on first use so menu-item field references are guaranteed
        /// to be initialized by the XAML parser.
        /// </summary>
        private System.Collections.Generic.Dictionary<AiProvider, System.Windows.Controls.MenuItem> _providerMenuItems;

        private System.Collections.Generic.Dictionary<AiProvider, System.Windows.Controls.MenuItem> GetProviderMenuItems()
        {
            if (_providerMenuItems == null)
            {
                _providerMenuItems = new System.Collections.Generic.Dictionary<AiProvider, System.Windows.Controls.MenuItem>
                {
                    { AiProvider.ClaudeCode,         ClaudeCodeMenuItem },
                    { AiProvider.ClaudeCodeWSL,      ClaudeCodeWSLMenuItem },
                    { AiProvider.CodexNative,        CodexNativeMenuItem },
                    { AiProvider.Codex,              CodexMenuItem },
                    { AiProvider.CursorAgentNative,  CursorAgentNativeMenuItem },
                    { AiProvider.CursorAgent,        CursorAgentMenuItem },
                    { AiProvider.OpenCode,           OpenCodeMenuItem },
                    { AiProvider.DevinNative,        DevinNativeMenuItem },
                    { AiProvider.Devin,           DevinMenuItem },
                    { AiProvider.Pi,                 PiMenuItem },
                    { AiProvider.Antigravity,        AntigravityMenuItem },
                    { AiProvider.Reasonix,           ReasonixMenuItem },
                };
            }
            return _providerMenuItems;
        }

        /// <summary>
        /// Friendly display name for a provider, used in the configure-visible-agents dialog.
        /// </summary>
        private static string GetProviderConfigLabel(AiProvider provider)
        {
            switch (provider)
            {
                case AiProvider.ClaudeCode:        return "Claude Code";
                case AiProvider.ClaudeCodeWSL:     return "Claude Code (WSL)";
                case AiProvider.CodexNative:       return "Codex";
                case AiProvider.Codex:             return "Codex (WSL)";
                case AiProvider.CursorAgentNative: return "Cursor Agent";
                case AiProvider.CursorAgent:       return "Cursor Agent (WSL)";
                case AiProvider.OpenCode:          return "Open Code";
                case AiProvider.Devin:          return "Devin (WSL)";
                case AiProvider.Pi:                return "PI";
                case AiProvider.Antigravity:       return "Antigravity";
                case AiProvider.Reasonix:          return "Reasonix";
                case AiProvider.DevinNative:       return "Devin";
                default:                           return provider.ToString();
            }
        }

        /// <summary>
        /// Applies the user's VisibleProviders filter to the provider items in the
        /// agent menu. The currently selected provider is always visible so a user
        /// who already had a non-default agent picked before upgrading still sees it.
        /// </summary>
        private void ApplyProviderMenuVisibility()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            var visible = _settings.VisibleProviders ?? new System.Collections.Generic.List<AiProvider>();
            var selected = GetActiveOrSelectedProvider();
            var items = GetProviderMenuItems();

            foreach (var pair in items)
            {
                if (pair.Value == null) continue;
                bool show = visible.Contains(pair.Key) || pair.Key == selected;
                pair.Value.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Handles the "Configure Visible Code Agents..." menu item click.
        /// Opens a checkbox dialog letting the user pick which agents appear
        /// in the provider menu.
        /// </summary>
        private void ConfigureVisibleAgentsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_settings == null) _settings = new ClaudeCodeSettings();
                if (_settings.VisibleProviders == null)
                {
                    _settings.VisibleProviders = new System.Collections.Generic.List<AiProvider> { AiProvider.ClaudeCode };
                }

                if (ShowVisibleAgentsDialog())
                {
                    SaveSettings();
                    ApplyProviderMenuVisibility();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error configuring visible agents: {ex.Message}");
                MessageBox.Show($"Error configuring visible agents: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Shows the dialog with one checkbox per provider. Returns true when the
        /// user clicked OK and the selection was saved into <c>_settings.VisibleProviders</c>.
        /// The currently selected provider's checkbox is force-checked and disabled
        /// because hiding the active agent would leave the menu inconsistent with
        /// the live terminal title.
        /// </summary>
        private bool ShowVisibleAgentsDialog()
        {
            GetThemeBrushes(out System.Windows.Media.Brush themeBg, out System.Windows.Media.Brush themeFg);

            var dialog = new System.Windows.Window
            {
                Title = "Configure Visible Code Agents",
                Width = 420,
                Height = 460,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };

            try { dialog.Owner = System.Windows.Application.Current?.MainWindow; } catch { }

            var grid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(12) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Select which code agents appear in the agent menu. " +
                       "The currently active agent is always shown.",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = themeFg,
                Margin = new System.Windows.Thickness(0, 0, 0, 10)
            };
            System.Windows.Controls.Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = stack
            };
            System.Windows.Controls.Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var visible = _settings.VisibleProviders ?? new System.Collections.Generic.List<AiProvider>();
            var selected = GetActiveOrSelectedProvider();

            // Preserve menu order from the XAML provider list
            var providersInOrder = new[]
            {
                AiProvider.ClaudeCode,
                AiProvider.ClaudeCodeWSL,
                AiProvider.CodexNative,
                AiProvider.Codex,
                AiProvider.CursorAgentNative,
                AiProvider.CursorAgent,
                AiProvider.OpenCode,
                AiProvider.DevinNative,
                AiProvider.Devin,
                AiProvider.Pi,
                AiProvider.Antigravity,
                AiProvider.Reasonix,
            };

            var checkboxes = new System.Collections.Generic.Dictionary<AiProvider, System.Windows.Controls.CheckBox>();
            foreach (var provider in providersInOrder)
            {
                bool isActive = provider == selected;
                var cb = new System.Windows.Controls.CheckBox
                {
                    Content = isActive
                        ? GetProviderConfigLabel(provider) + "  (active)"
                        : GetProviderConfigLabel(provider),
                    IsChecked = isActive || visible.Contains(provider),
                    IsEnabled = !isActive,
                    Foreground = themeFg,
                    Margin = new System.Windows.Thickness(0, 4, 0, 4)
                };
                stack.Children.Add(cb);
                checkboxes[provider] = cb;
            }

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new System.Windows.Thickness(0, 10, 0, 0)
            };
            System.Windows.Controls.Grid.SetRow(buttonPanel, 2);

            System.Windows.Style buttonStyle = GetDialogButtonStyle();

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                Width = 80,
                Height = 26,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 26,
                IsCancel = true
            };
            if (buttonStyle != null)
            {
                okButton.Style = buttonStyle;
                cancelButton.Style = buttonStyle;
            }
            else
            {
                okButton.Background = themeBg; okButton.Foreground = themeFg; okButton.BorderBrush = themeFg;
                cancelButton.Background = themeBg; cancelButton.Foreground = themeFg; cancelButton.BorderBrush = themeFg;
            }

            bool confirmed = false;
            okButton.Click += (s, args) =>
            {
                var newList = new System.Collections.Generic.List<AiProvider>();
                foreach (var provider in providersInOrder)
                {
                    if (checkboxes[provider].IsChecked == true)
                    {
                        newList.Add(provider);
                    }
                }
                _settings.VisibleProviders = newList;
                confirmed = true;
                dialog.DialogResult = true;
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            dialog.ShowDialog();
            return confirmed;
        }

        #endregion
    }
}
