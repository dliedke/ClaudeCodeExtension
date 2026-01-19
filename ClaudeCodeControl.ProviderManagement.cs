/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
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
        /// Flag to show Codex installation notification only once per session
        /// </summary>
        private static bool _codexNotificationShown = false;

        /// <summary>
        /// Flag to show Cursor Agent installation notification only once per session
        /// </summary>
        private static bool _cursorAgentNotificationShown = false;

        /// <summary>
        /// Flag to show Qwen Code installation notification only once per session
        /// </summary>
        private static bool _qwenCodeNotificationShown = false;

        /// <summary>
        /// Flag to show Open Code installation notification only once per session
        /// </summary>
        private static bool _openCodeNotificationShown = false;

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
                Debug.WriteLine("Provider availability cache cleared");
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
                    Debug.WriteLine($"Using cached Claude Code availability: {cached.IsAvailable}");
                    return cached.IsAvailable;
                }
            }

            try
            {
                // First, check for native installation at %USERPROFILE%\.local\bin\claude.exe
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string nativeClaudePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");

                Debug.WriteLine($"Checking for native Claude installation at: {nativeClaudePath}");

                if (File.Exists(nativeClaudePath))
                {
                    Debug.WriteLine("Native Claude installation found");
                    CacheProviderResult(AiProvider.ClaudeCode, true);
                    return true;
                }

                Debug.WriteLine("Native Claude installation not found, checking NPM installation...");

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
                        Debug.WriteLine("Claude NPM check timed out");
                        CacheProviderResult(AiProvider.ClaudeCode, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Claude NPM check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Claude NPM check - Output: {output}");
                    Debug.WriteLine($"Claude NPM check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Claude NPM availability result: {isAvailable}");

                    CacheProviderResult(AiProvider.ClaudeCode, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Claude Code check was cancelled");
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
                Debug.WriteLine($"Cached {provider} availability: {isAvailable}");
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
        /// Uses retry logic to handle WSL initialization delays after boot
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
                    Debug.WriteLine($"Using cached Claude Code WSL availability: {cached.IsAvailable}");
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync(cancellationToken);
                if (!wslInstalled)
                {
                    Debug.WriteLine("WSL is not installed, Claude Code in WSL not available");
                    CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Reduced retry logic - only retry once with shorter timeouts
                int[] timeouts = { 3000, 5000 }; // Reduced timeouts: 3s, 5s
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Debug.WriteLine($"Claude Code WSL check attempt {attempt}/{maxRetries}");

                    // Check if claude is available in WSL using 'which claude'
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -ic \"which claude\"",
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
                            Debug.WriteLine($"Claude Code WSL check timed out on attempt {attempt}");

                            // If not the last attempt, wait before retrying (reduced delay)
                            if (attempt < maxRetries)
                            {
                                Debug.WriteLine($"Waiting 1 second before retry (WSL may be initializing)...");
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();

                        Debug.WriteLine($"Claude Code WSL check - Exit code: {process.ExitCode}");
                        Debug.WriteLine($"Claude Code WSL check - Output: {output}");
                        Debug.WriteLine($"Claude Code WSL check - Error: {error}");

                        // Check if output contains a path to claude
                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("claude");

                        if (isAvailable)
                        {
                            Debug.WriteLine($"Claude Code in WSL found on attempt {attempt}");
                            CacheProviderResult(AiProvider.ClaudeCodeWSL, true);
                            return true;
                        }

                        // If we got a response but agent not found, no need to retry
                        if (process.ExitCode == 0 || !string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(error))
                        {
                            Debug.WriteLine($"Claude Code in WSL not found (WSL responded, agent not installed)");
                            CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            Debug.WriteLine($"WSL didn't respond properly, waiting 1 second before retry...");
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                Debug.WriteLine($"Claude Code in WSL not available after {maxRetries} attempts");
                CacheProviderResult(AiProvider.ClaudeCodeWSL, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Claude Code WSL check was cancelled");
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
                    Debug.WriteLine($"Using cached Codex availability: {cached.IsAvailable}");
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Check if WSL is installed first
                bool wslInstalled = await IsWslInstalledAsync(cancellationToken);
                if (!wslInstalled)
                {
                    Debug.WriteLine("WSL is not installed, Codex in WSL not available");
                    CacheProviderResult(AiProvider.Codex, false);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Reduced retry logic - only retry once with shorter timeouts
                int[] timeouts = { 3000, 5000 }; // Reduced timeouts: 3s, 5s
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Debug.WriteLine($"Codex WSL check attempt {attempt}/{maxRetries}");

                    // Check if codex is available in WSL using 'which codex' with interactive shell
                    // We need -i flag because codex is installed via nvm which requires interactive shell
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -ic \"which codex\"",
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
                            Debug.WriteLine($"Codex check in WSL timed out on attempt {attempt}");

                            // If not the last attempt, wait before retrying (reduced delay)
                            if (attempt < maxRetries)
                            {
                                Debug.WriteLine($"Waiting 1 second before retry (WSL may be initializing)...");
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.Codex, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();

                        Debug.WriteLine($"Codex WSL check - Exit code: {process.ExitCode}");
                        Debug.WriteLine($"Codex WSL check - Output: {output}");
                        Debug.WriteLine($"Codex WSL check - Error: {error}");

                        // Check if output contains a path to codex (like /home/user/.nvm/versions/node/v22.20.0/bin/codex)
                        bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) && output.Contains("codex");

                        if (isAvailable)
                        {
                            Debug.WriteLine($"Codex in WSL found on attempt {attempt}");
                            CacheProviderResult(AiProvider.Codex, true);
                            return true;
                        }

                        // If we got a response but agent not found, no need to retry
                        if (process.ExitCode == 0 || !string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(error))
                        {
                            Debug.WriteLine($"Codex in WSL not found (WSL responded, agent not installed)");
                            CacheProviderResult(AiProvider.Codex, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            Debug.WriteLine($"WSL didn't respond properly, waiting 1 second before retry...");
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                Debug.WriteLine($"Codex in WSL not available after {maxRetries} attempts");
                CacheProviderResult(AiProvider.Codex, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Codex check was cancelled");
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
                    Debug.WriteLine($"Using cached WSL availability: {_wslCache.IsAvailable}");
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
                        Debug.WriteLine("WSL check timed out");
                        CacheWslResult(false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"WSL check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"WSL check - Output: {output}");
                    Debug.WriteLine($"WSL check - Error: {error}");

                    bool isInstalled = process.ExitCode == 0;
                    Debug.WriteLine($"WSL installed: {isInstalled}");

                    CacheWslResult(isInstalled);
                    return isInstalled;
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("WSL check was cancelled");
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
                Debug.WriteLine($"Cached WSL availability: {isInstalled}");
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
                    Debug.WriteLine($"Using cached Cursor Agent availability: {cached.IsAvailable}");
                    return cached.IsAvailable;
                }
            }

            try
            {
                // Reduced retry logic - only retry once with shorter timeouts
                int[] timeouts = { 3000, 5000 }; // Reduced timeouts: 3s, 5s
                int maxRetries = 2;

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Debug.WriteLine($"Cursor Agent WSL check attempt {attempt}/{maxRetries}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c wsl bash -c \"test -L ~/.local/bin/cursor-agent && echo 'exists' || echo 'notfound'\"",
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
                            Debug.WriteLine($"Cursor agent check in WSL timed out on attempt {attempt}");

                            // If not the last attempt, wait before retrying (reduced delay)
                            if (attempt < maxRetries)
                            {
                                Debug.WriteLine($"Waiting 1 second before retry (WSL may be initializing)...");
                                await Task.Delay(1000, cancellationToken);
                                continue;
                            }
                            CacheProviderResult(AiProvider.CursorAgent, false);
                            return false;
                        }

                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();

                        Debug.WriteLine($"Cursor agent WSL check - Exit code: {process.ExitCode}");
                        Debug.WriteLine($"Cursor agent WSL check - Output: {output}");
                        Debug.WriteLine($"Cursor agent WSL check - Error: {error}");

                        bool isInstalled = output.Trim().Equals("exists", StringComparison.OrdinalIgnoreCase);

                        if (isInstalled)
                        {
                            Debug.WriteLine($"Cursor agent found on attempt {attempt}");
                            CacheProviderResult(AiProvider.CursorAgent, true);
                            return true;
                        }

                        // If we got "notfound" response, agent is not installed, no need to retry
                        if (output.Trim().Equals("notfound", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.WriteLine($"Cursor agent not found (WSL responded, agent not installed)");
                            CacheProviderResult(AiProvider.CursorAgent, false);
                            return false;
                        }

                        // WSL didn't respond properly, retry if we have attempts left
                        if (attempt < maxRetries)
                        {
                            Debug.WriteLine($"WSL didn't respond properly, waiting 1 second before retry...");
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }

                Debug.WriteLine($"Cursor agent not available after {maxRetries} attempts");
                CacheProviderResult(AiProvider.CursorAgent, false);
                return false;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Cursor Agent check was cancelled");
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
                    Debug.WriteLine($"Using cached Qwen Code availability: {cached.IsAvailable}");
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
                        Debug.WriteLine("Qwen Code check timed out");
                        CacheProviderResult(AiProvider.QwenCode, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Qwen Code check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Qwen Code check - Output: {output}");
                    Debug.WriteLine($"Qwen Code check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Qwen Code availability result: {isAvailable}");

                    CacheProviderResult(AiProvider.QwenCode, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Qwen Code check was cancelled");
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
                    Debug.WriteLine($"Using cached Open Code availability: {cached.IsAvailable}");
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
                        Debug.WriteLine("Open Code check timed out");
                        CacheProviderResult(AiProvider.OpenCode, false);
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Open Code check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Open Code check - Output: {output}");
                    Debug.WriteLine($"Open Code check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Open Code availability result: {isAvailable}");

                    CacheProviderResult(AiProvider.OpenCode, isAvailable);
                    return isAvailable;
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Open Code check was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for Open Code: {ex.Message}");
                CacheProviderResult(AiProvider.OpenCode, false);
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

        #endregion

        #region Provider Switching

        /// <summary>
        /// Handles Qwen Code menu item click - switches to Qwen Code provider
        /// </summary>
        private void QwenCodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Open Code menu item click - switches to Open Code provider
        /// </summary>
        private void OpenCodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Claude Code menu item click - switches to Claude Code provider
        /// </summary>
        private void ClaudeCodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Claude Code (WSL) menu item click - switches to Claude Code (WSL) provider
        /// </summary>
        private void ClaudeCodeWSLMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Codex menu item click - switches to Codex provider
        /// </summary>
        private void CodexMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Cursor Agent menu item click - switches to Cursor Agent provider
        /// </summary>
        private void CursorAgentMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
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
            CodexMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.Codex;
            CursorAgentMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.CursorAgent;
            QwenCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.QwenCode;
            OpenCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.OpenCode;

            // Update GroupBox header to show selected provider (not necessarily running yet)
            string providerName = _settings.SelectedProvider == AiProvider.ClaudeCode ? "Claude Code" :
                                  _settings.SelectedProvider == AiProvider.ClaudeCodeWSL ? "Claude Code" :
                                  _settings.SelectedProvider == AiProvider.Codex ? "Codex" :
                                  _settings.SelectedProvider == AiProvider.QwenCode ? "Qwen Code" :
                                  _settings.SelectedProvider == AiProvider.OpenCode ? "Open Code" :
                                  "Cursor Agent";
            TerminalGroupBox.Header = providerName;

            // Show/hide model selection button based on provider
            bool isClaudeProvider = _settings.SelectedProvider == AiProvider.ClaudeCode ||
                                   _settings.SelectedProvider == AiProvider.ClaudeCodeWSL;
            ModelDropdownButton.Visibility = isClaudeProvider ? Visibility.Visible : Visibility.Collapsed;

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
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _toolWindow?.UpdateTitle(providerName);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating tool window title: {ex.Message}");
            }
        }

        #endregion

        #region Menu Handlers

        /// <summary>
        /// Handles About menu item click - displays extension information
        /// </summary>
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}";
            string aboutMessage = $"Claude Code Extension for Visual Studio\n\n" +
                                $"Version: {version}\n" +
                                $"Author: Daniel Liedke\n" +
                                $"Copyright © Daniel Liedke 2025\n\n" +
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
        /// Handles model dropdown button click - shows the Claude model selection menu
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
        /// Handles Opus menu item click - switches to Opus model
        /// </summary>
        private void OpusMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Sonnet menu item click - switches to Sonnet model
        /// </summary>
        private void SonnetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Handles Haiku menu item click - switches to Haiku model
        /// </summary>
        private void HaikuMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
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
            });
        }

        /// <summary>
        /// Updates the model selection UI checkmarks
        /// </summary>
        private void UpdateModelSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            // Update menu item checkmarks
            OpusMenuItem.IsChecked = _settings.SelectedClaudeModel == ClaudeModel.Opus;
            SonnetMenuItem.IsChecked = _settings.SelectedClaudeModel == ClaudeModel.Sonnet;
            HaikuMenuItem.IsChecked = _settings.SelectedClaudeModel == ClaudeModel.Haiku;
        }

        #endregion
    }
}