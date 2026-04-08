/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
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
        /// Flag to show Qwen Code installation notification only once per session
        /// </summary>
        private static bool _qwenCodeNotificationShown = false;

        /// <summary>
        /// Flag to show Open Code installation notification only once per session
        /// </summary>
        private static bool _openCodeNotificationShown = false;

        /// <summary>
        /// Flag to show Windsurf installation notification only once per session
        /// </summary>
        private static bool _windsurfNotificationShown = false;

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
        /// Checks if Claude Code CLI is available (native or NPM installation)
        /// Prioritizes native installation at %USERPROFILE%\.local\bin\claude.exe
        /// Falls back to NPM installation (claude.cmd in PATH)
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if claude is available, false otherwise</returns>
        private async Task<bool> IsClaudeCmdAvailableAsync(CancellationToken cancellationToken = default)
        {
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

                // If native not found, check for NPM installation (claude.cmd in PATH)
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where claude.cmd",
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

                    // Check if codex is available in WSL using 'which codex' with interactive shell
                    // We need -i flag because codex is installed via nvm which requires interactive shell
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -lc \"which codex\"",
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
        /// Checks if Windsurf (devin) is available inside WSL
        /// Uses 'which devin' command to verify installation
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if devin is available in WSL, false otherwise</returns>
        private async Task<bool> IsWindsurfAvailableAsync(CancellationToken cancellationToken = default)
        {
            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.Windsurf, out var cached) && IsCacheValid(cached))
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
                    CacheProviderResult(AiProvider.Windsurf, false);
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
                            CacheProviderResult(AiProvider.Windsurf, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();

                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("devin");

                        if (isAvailable)
                        {
                            CacheProviderResult(AiProvider.Windsurf, true);
                            return true;
                        }

                        // If stdout has content, WSL responded -- no need to retry
                        if (!string.IsNullOrEmpty(output))
                        {
                            CacheProviderResult(AiProvider.Windsurf, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                CacheProviderResult(AiProvider.Windsurf, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for devin in WSL: {ex.Message}");
                CacheProviderResult(AiProvider.Windsurf, false);
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
        /// Checks if Qwen Code CLI is available (native or NPM installation)
        /// Prioritizes NPM installation (qwen in PATH) but also checks for other possible installations
        /// Uses caching to avoid repeated slow checks
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if qwen is available, false otherwise</returns>
        private async Task<bool> IsQwenCodeAvailableAsync(CancellationToken cancellationToken = default)
        {
            // Check cache first
            lock (_cacheLock)
            {
                if (_providerCache.TryGetValue(AiProvider.QwenCode, out var cached) && IsCacheValid(cached))
                {
                    return cached.IsAvailable;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where qwen",
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
                        CacheProviderResult(AiProvider.QwenCode, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();


                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);

                    CacheProviderResult(AiProvider.QwenCode, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Qwen Code: {ex.Message}");
                CacheProviderResult(AiProvider.QwenCode, false);
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
        /// Shows installation instructions for Qwen Code CLI
        /// </summary>
        private void ShowQwenCodeInstallationInstructions()
        {
            const string instructions = @"Qwen Code is not installed. A regular CMD terminal will be used instead.

(you may click CTRL+C to copy full instructions)

INSTALLATION: NPM Installation (Recommended)

Open cmd and run:

npm install -g @qwen-code/qwen-code@latest

Alternatively, you can install from source:

git clone https://github.com/QwenLM/qwen-code.git
cd qwen-code
npm install
npm install -g .

Requirements:
- Node.js version 20 or higher

For more details, visit: https://github.com/QwenLM/qwen-code";

            MessageBox.Show(instructions, "Qwen Code Installation",
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
        /// Shows installation instructions for Windsurf (devin CLI) in WSL
        /// </summary>
        private void ShowWindsurfInstallationInstructions()
        {
            const string instructions = @"To use Windsurf, you need to install WSL and Windsurf (devin) inside WSL.

(you may click CTRL+C to copy full instructions)

Make sure virtualization is enabled in BIOS.

Open PowerShell as Administrator and run:

dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart

dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

wsl --install

Install Windsurf (devin) inside WSL:

wsl

curl -fsSL https://cli.devin.ai/install.sh | bash

Start devin to login:

devin";

            MessageBox.Show(instructions, "Windsurf Installation",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Provider Switching

        /// <summary>
        /// Handles Qwen Code menu item click - switches to Qwen Code provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void QwenCodeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool qwenCodeAvailable = await IsQwenCodeAvailableAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.QwenCode;
            UpdateProviderSelection();
            SaveSettings();

            if (!qwenCodeAvailable)
            {
                ShowQwenCodeInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.QwenCode);
            }
        }

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
        /// Handles Windsurf (WSL) menu item click - switches to Windsurf provider
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void WindsurfMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;

            bool wslInstalled = await IsWslInstalledAsync();
            bool windsurfAvailable = false;

            if (wslInstalled)
            {
                windsurfAvailable = await IsWindsurfAvailableAsync();
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Always update the selection regardless of availability
            _settings.SelectedProvider = AiProvider.Windsurf;
            UpdateProviderSelection();
            SaveSettings();

            if (!wslInstalled || !windsurfAvailable)
            {
                ShowWindsurfInstallationInstructions();
                await StartEmbeddedTerminalAsync(null); // Regular CMD
            }
            else
            {
                await StartEmbeddedTerminalAsync(AiProvider.Windsurf);
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

            // Update menu item checkmarks
            ClaudeCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.ClaudeCode;
            ClaudeCodeWSLMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.ClaudeCodeWSL;
            CodexNativeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.CodexNative;
            CodexMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.Codex;
            CursorAgentNativeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.CursorAgentNative;
            CursorAgentMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.CursorAgent;
            QwenCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.QwenCode;
            OpenCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.OpenCode;
            WindsurfMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.Windsurf;

            // Update GroupBox header to show selected provider (not necessarily running yet)
            string providerName = _settings.SelectedProvider == AiProvider.ClaudeCode ? "Claude Code" :
                                  _settings.SelectedProvider == AiProvider.ClaudeCodeWSL ? "Claude Code" :
                                  _settings.SelectedProvider == AiProvider.CodexNative ? "Codex" :
                                  _settings.SelectedProvider == AiProvider.Codex ? "Codex" :
                                  _settings.SelectedProvider == AiProvider.CursorAgentNative ? "Cursor Agent" :
                                  _settings.SelectedProvider == AiProvider.CursorAgent ? "Cursor Agent" :
                                  _settings.SelectedProvider == AiProvider.QwenCode ? "Qwen Code" :
                                  _settings.SelectedProvider == AiProvider.OpenCode ? "Open Code" :
                                  _settings.SelectedProvider == AiProvider.Windsurf ? "Windsurf" :
                                  "Claude Code";
            TerminalGroupBox.Header = new System.Windows.Controls.TextBlock { Text = providerName, Opacity = 0.93 };

            // Show/hide model selection button based on provider
            bool isClaudeProvider = _settings.SelectedProvider == AiProvider.ClaudeCode ||
                                   _settings.SelectedProvider == AiProvider.ClaudeCodeWSL;
            bool isWindsurfProvider = _settings.SelectedProvider == AiProvider.Windsurf;
            ModelDropdownButton.Visibility = (isClaudeProvider || isWindsurfProvider) ? Visibility.Visible : Visibility.Collapsed;

            // Note: Tool window title will be updated after the terminal actually starts
            // in StartEmbeddedTerminalAsync to reflect what's actually running
        }

        /// <summary>
        /// Updates the tool window title to reflect the current provider
        /// </summary>
        /// <param name="providerName">Name of the current provider</param>
        private void UpdateToolWindowTitle(string providerName)
        {
            try
            {
#pragma warning disable VSSDK007, VSTHRD110 // Fire-and-forget to avoid blocking the caller
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _toolWindow?.UpdateTitle(providerName);
                    _detachedTerminalWindow?.UpdateCaption(providerName);
                });
#pragma warning restore VSSDK007, VSTHRD110
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating tool window title: {ex.Message}");
            }
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
                                $"Provides seamless integration with Claude Code, Codex, Cursor Agent, Qwen Code and Open Code AI assistants directly within Visual Studio 2022/2026 IDE.";

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
        /// Shows Claude-specific or Windsurf-specific model items based on the current provider
        /// </summary>
        private void ModelContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool isWindsurf = _settings?.SelectedProvider == AiProvider.Windsurf;
            bool isClaude = !isWindsurf;

            // Claude-specific items
            ShowUsageMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            ClaudeModelSeparator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            OpusMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            SonnetMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            HaikuMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortSeparator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortLabelMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortAutoMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortLowMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortMediumMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortHighMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            EffortMaxMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            ClaudeAccountSeparator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            ChangeAccountMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;
            SetLanguageMenuItem.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;

            // Windsurf-specific items
            WindsurfShowUsageMenuItem.Visibility = isWindsurf ? Visibility.Visible : Visibility.Collapsed;
            WindsurfModelSeparator.Visibility = isWindsurf ? Visibility.Visible : Visibility.Collapsed;
            WindsurfClaudeOpusMenuItem.Visibility = isWindsurf ? Visibility.Visible : Visibility.Collapsed;
            WindsurfClaudeSonnetMenuItem.Visibility = isWindsurf ? Visibility.Visible : Visibility.Collapsed;
            WindsurfCodexMenuItem.Visibility = isWindsurf ? Visibility.Visible : Visibility.Collapsed;
            WindsurfGeminiProMenuItem.Visibility = isWindsurf ? Visibility.Visible : Visibility.Collapsed;
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

            // Update Windsurf menu item checkmarks
            WindsurfClaudeOpusMenuItem.IsChecked = _settings.SelectedWindsurfModel == WindsurfModel.ClaudeOpus;
            WindsurfClaudeSonnetMenuItem.IsChecked = _settings.SelectedWindsurfModel == WindsurfModel.ClaudeSonnet;
            WindsurfCodexMenuItem.IsChecked = _settings.SelectedWindsurfModel == WindsurfModel.Codex;
            WindsurfGeminiProMenuItem.IsChecked = _settings.SelectedWindsurfModel == WindsurfModel.GeminiPro;

            // Update effort selection checkmarks
            UpdateEffortSelection();
        }

        #endregion

        #region Windsurf Model Selection

        /// <summary>
        /// Handles Windsurf Claude Opus menu item click - switches to Claude Opus model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void WindsurfClaudeOpusMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _settings.SelectedWindsurfModel = WindsurfModel.ClaudeOpus;
            UpdateModelSelection();
            SaveSettings();
            if (_currentRunningProvider == AiProvider.Windsurf)
            {
                await SendTextToTerminalAsync("/model opus");
            }
        }

        /// <summary>
        /// Handles Windsurf Claude Sonnet menu item click - switches to Claude Sonnet model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void WindsurfClaudeSonnetMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _settings.SelectedWindsurfModel = WindsurfModel.ClaudeSonnet;
            UpdateModelSelection();
            SaveSettings();
            if (_currentRunningProvider == AiProvider.Windsurf)
            {
                await SendTextToTerminalAsync("/model sonnet");
            }
        }

        /// <summary>
        /// Handles Windsurf Codex menu item click - switches to Codex model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void WindsurfCodexMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _settings.SelectedWindsurfModel = WindsurfModel.Codex;
            UpdateModelSelection();
            SaveSettings();
            if (_currentRunningProvider == AiProvider.Windsurf)
            {
                await SendTextToTerminalAsync("/model codex");
            }
        }

        /// <summary>
        /// Handles Windsurf Gemini Pro menu item click - switches to Gemini Pro model
        /// </summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void WindsurfGeminiProMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_settings == null) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _settings.SelectedWindsurfModel = WindsurfModel.GeminiPro;
            UpdateModelSelection();
            SaveSettings();
            if (_currentRunningProvider == AiProvider.Windsurf)
            {
                await SendTextToTerminalAsync("/model gemini pro");
            }
        }

        /// <summary>
        /// Handles Windsurf Show Usage menu item click - opens the Windsurf usage page in the browser
        /// </summary>
        private void WindsurfShowUsageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://windsurf.com/subscription/usage");
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
            UpdateEffortSelection();
            SaveSettings();

            // Send /effort command to Claude Code terminal
            if (_currentRunningProvider == AiProvider.ClaudeCode ||
                _currentRunningProvider == AiProvider.ClaudeCodeWSL)
            {
                await SendTextToTerminalAsync($"/effort {level.ToString().ToLower()}");
            }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void EffortAutoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetEffortLevelAsync(EffortLevel.Auto);
        }

        private async void EffortLowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetEffortLevelAsync(EffortLevel.Low);
        }

        private async void EffortMediumMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetEffortLevelAsync(EffortLevel.Medium);
        }

        private async void EffortHighMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetEffortLevelAsync(EffortLevel.High);
        }

        private async void EffortMaxMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetEffortLevelAsync(EffortLevel.Max);
        }
#pragma warning restore VSTHRD100

        /// <summary>
        /// Updates the effort selection UI checkmarks
        /// </summary>
        private void UpdateEffortSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            EffortAutoMenuItem.IsChecked = _settings.SelectedEffortLevel == EffortLevel.Auto;
            EffortLowMenuItem.IsChecked = _settings.SelectedEffortLevel == EffortLevel.Low;
            EffortMediumMenuItem.IsChecked = _settings.SelectedEffortLevel == EffortLevel.Medium;
            EffortHighMenuItem.IsChecked = _settings.SelectedEffortLevel == EffortLevel.High;
            EffortMaxMenuItem.IsChecked = _settings.SelectedEffortLevel == EffortLevel.Max;
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
                    resumeCommand = $"wsl bash -ic \"{baseCmd}\"";
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

        #endregion

        #region Provider Context Menu

        /// <summary>
        /// Handles the provider context menu opening - shows/hides git-specific options
        /// </summary>
        private void ProviderContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Check if project is in a git repository using cached workspace directory
            // to avoid blocking the UI thread with async calls during menu open
            bool isInGitRepo = false;
            try
            {
                string workspaceDir = _lastWorkspaceDirectory;
                if (!string.IsNullOrEmpty(workspaceDir))
                {
                    isInGitRepo = !string.IsNullOrEmpty(FindGitRepositoryRoot(workspaceDir));
                }
            }
            catch
            {
                isInGitRepo = false;
            }

            // Show/hide menu options based on context
            bool isClaudeProvider = _settings != null &&
                                   (_settings.SelectedProvider == AiProvider.ClaudeCode ||
                                    _settings.SelectedProvider == AiProvider.ClaudeCodeWSL);
            bool isCodexProvider = _settings != null &&
                                   (_settings.SelectedProvider == AiProvider.Codex ||
                                    _settings.SelectedProvider == AiProvider.CodexNative);
            bool isWindsurfProvider = _settings != null &&
                                     _settings.SelectedProvider == AiProvider.Windsurf;

            AutoOpenChangesSeparator.Visibility = (isInGitRepo || isClaudeProvider || isWindsurfProvider) ? Visibility.Visible : Visibility.Collapsed;
            AutoOpenChangesMenuItem.Visibility = isInGitRepo ? Visibility.Visible : Visibility.Collapsed;
            ClaudeDangerouslySkipPermissionsMenuItem.Visibility = isClaudeProvider ? Visibility.Visible : Visibility.Collapsed;
            CodexFullAutoMenuItem.Visibility = isCodexProvider ? Visibility.Visible : Visibility.Collapsed;
            WindsurfDangerousModeMenuItem.Visibility = isWindsurfProvider ? Visibility.Visible : Visibility.Collapsed;

            // Update checkbox state from settings
            if (_settings != null)
            {
                AutoOpenChangesMenuItem.IsChecked = _settings.AutoOpenChangesOnPrompt;
                ClaudeDangerouslySkipPermissionsMenuItem.IsChecked = _settings.ClaudeDangerouslySkipPermissions;
                CodexFullAutoMenuItem.IsChecked = _settings.CodexFullAuto;
                WindsurfDangerousModeMenuItem.IsChecked = _settings.WindsurfDangerousMode;

                // Update working directory menu item to show current value, with red text if path doesn't exist
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
                    headerBlock.Inlines.Add("Set Working Directory (");
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
                    SetWorkingDirectoryMenuItem.Header = "Set Working Directory...";
                }

                // Update terminal type menu item to show current selection
                string terminalTypeName = _settings.SelectedTerminalType == TerminalType.WindowsTerminal ? "Windows Terminal" : "Command Prompt";
                SetTerminalTypeMenuItem.Header = $"Set Terminal Type... ({terminalTypeName})";
            }
        }

        /// <summary>
        /// Handles auto-open changes menu item click
        /// </summary>
        private void AutoOpenChangesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            _settings.AutoOpenChangesOnPrompt = AutoOpenChangesMenuItem.IsChecked;
            SaveSettings();
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
        /// Handles Windsurf dangerous mode menu item click
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void WindsurfDangerousModeMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) return;

            _settings.WindsurfDangerousMode = WindsurfDangerousModeMenuItem.IsChecked;
            SaveSettings();

            // Reload Windsurf terminal immediately so the new startup flag is applied.
            if (_settings.SelectedProvider == AiProvider.Windsurf)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reloading Windsurf after dangerous mode change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to reload Windsurf: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
    }
}
