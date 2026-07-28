/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Shared plumbing for native-mode adapters: a child process talked to in newline-delimited JSON over stdio
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Describes the child process an adapter wants to run.
    /// </summary>
    public class JsonLineProcessOptions
    {
        /// <summary>Full path (or PATH-resolvable name) of the executable.</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>Already-quoted argument string.</summary>
        public string Arguments { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Environment variables to set on the child, overriding inherited ones. Used for the
        /// registry-refreshed PATH (a CLI installed after Visual Studio started is invisible to the
        /// inherited PATH) and for provider-specific switches.
        /// </summary>
        public IDictionary<string, string> EnvironmentOverrides { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs an agent CLI in headless mode and exchanges newline-delimited JSON with it over stdio.
    /// <para>
    /// Every adapter (<c>ClaudeStreamJsonSession</c>, <c>AcpSession</c>, <c>OneShotResumeSession</c>)
    /// sits on top of this; it owns process lifetime, the reader loops and write serialization, and
    /// knows nothing about any particular wire format — it hands raw lines up and takes raw lines down.
    /// </para>
    /// <para>Not thread-safe for <see cref="StartAsync"/>/<see cref="Dispose"/>; <see cref="WriteLineAsync"/> is.</para>
    /// </summary>
    public class JsonLineProcessHost : IDisposable
    {
        private readonly JsonLineProcessOptions _options;

        // Writes are serialized: a turn and an interrupt can be issued from different threads, and two
        // interleaved WriteLineAsync calls would splice one JSON line into the middle of another.
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private Process _process;
        private StreamWriter _stdin;
        private Task _stdoutTask;
        private Task _stderrTask;
        private CancellationTokenSource _lifetimeCts;
        private int _disposed;

        public JsonLineProcessHost(JsonLineProcessOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>Raised for each line the child writes to stdout. Fires on a background thread.</summary>
        public event EventHandler<string> LineReceived;

        /// <summary>
        /// Raised for each line the child writes to stderr. Fires on a background thread. Most CLIs use
        /// stderr for progress noise, so this is diagnostics — not necessarily failure.
        /// </summary>
        public event EventHandler<string> ErrorLineReceived;

        /// <summary>
        /// Raised once the child exits, with its exit code. A non-zero code is not automatically an
        /// error: Claude exits with 1 after an interrupt, which the adapter handles by resuming.
        /// </summary>
        public event EventHandler<int> Exited;

        public bool IsRunning
        {
            get
            {
                Process process = _process;
                try
                {
                    return process != null && !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        /// <summary>Process id of the child, or 0 when not running.</summary>
        public int ProcessId { get; private set; }

        /// <summary>
        /// Starts the child with stdio redirected and no console window. Returns as soon as the process
        /// is up; protocol readiness is the adapter's business.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_process != null)
            {
                throw new InvalidOperationException("Process already started.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo
            {
                FileName = _options.FileName,
                Arguments = _options.Arguments,
                WorkingDirectory = _options.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Without a BOM: the CLIs parse raw UTF-8 and a leading BOM breaks the first JSON line.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            foreach (KeyValuePair<string, string> pair in _options.EnvironmentOverrides)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;

            _lifetimeCts = new CancellationTokenSource();

            if (!_process.Start())
            {
                throw new InvalidOperationException($"Failed to start '{_options.FileName}'.");
            }

            ProcessId = _process.Id;

            // stdin is written through our own UTF-8 writer, never through Process.StandardInput.
            // .NET Framework has no StandardInputEncoding (it arrived in .NET Core 2.1), and the writer
            // it builds encodes in Console.InputEncoding — the machine's ANSI/OEM code page when Visual
            // Studio has no console. Every one of these CLIs reads stdin as UTF-8, so a single accented
            // character in a prompt went out as one code-page byte and was rejected: Codex answered
            // "input is not valid UTF-8" and exited 1 before the turn started.
            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false));

            // stdin is deliberately left open for the life of the session: closing the pipe is how these
            // CLIs are told the conversation is over, and doing it after the first turn ends the session.
            // Both streams are drained concurrently — reading only stdout fills the stderr pipe buffer
            // and deadlocks the child once it exceeds ~4 KB of diagnostics.
            _stdoutTask = Task.Run(() => PumpAsync(_process.StandardOutput, LineReceived, _lifetimeCts.Token));
            _stderrTask = Task.Run(() => PumpAsync(_process.StandardError, ErrorLineReceived, _lifetimeCts.Token));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Writes one JSON line to the child's stdin. Serialized against concurrent callers.
        /// </summary>
        public async Task WriteLineAsync(string json, CancellationToken cancellationToken)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (!IsRunning)
            {
                throw new InvalidOperationException("Agent process is not running.");
            }

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StreamWriter stdin = _stdin;
                if (stdin == null)
                {
                    throw new InvalidOperationException("Agent process is not running.");
                }

                // "\n", never WriteLine: on Windows that would emit CRLF, and the CCs' line-based
                // parsers hand the trailing CR to the JSON reader.
                await stdin.WriteAsync(json).ConfigureAwait(false);
                await stdin.WriteAsync("\n").ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // The child died between the IsRunning check and the write — a normal race after an
                // interrupt, surfaced to the adapter as a plain failure to send.
                throw new InvalidOperationException("Agent process closed its input pipe.", ex);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Closes the child's stdin.
        /// <para>
        /// The persistent adapters must never call this — for them an open pipe is what keeps the
        /// conversation alive. The one-shot adapters must: those CLIs read the prompt from stdin and
        /// only start working once they see EOF.
        /// </para>
        /// </summary>
        public void CloseInput()
        {
            StreamWriter stdin = _stdin;
            if (stdin == null)
            {
                return;
            }

            try
            {
                // Closing our writer flushes it and closes the underlying pipe, which is the EOF the
                // one-shot CLIs wait for.
                stdin.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: closing stdin failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads <paramref name="reader"/> line by line until EOF, raising <paramref name="handler"/>
        /// for each. <see cref="StreamReader.ReadLineAsync"/> grows its own buffer, so the 100 KB+ lines
        /// that carry extended-thinking blocks with their signatures are handled without a fixed buffer.
        /// </summary>
        private async Task PumpAsync(StreamReader reader, EventHandler<string> handler, CancellationToken cancellationToken)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        handler?.Invoke(this, line);
                    }
                    catch (Exception ex)
                    {
                        // A throwing subscriber must not kill the pump — that would silently freeze the
                        // whole session with the process still alive.
                        Debug.WriteLine($"JsonLineProcessHost: handler threw: {ex}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected during teardown: the stream is disposed out from under the pending read.
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: pump ended: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: pump failed: {ex}");
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            int exitCode;
            try
            {
                exitCode = _process.ExitCode;
            }
            catch (Exception)
            {
                exitCode = -1;
            }

            try
            {
                Exited?.Invoke(this, exitCode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: Exited handler threw: {ex}");
            }
        }

        /// <summary>
        /// Waits — briefly — for the pumps to drain after the child exits, so the adapter still sees the
        /// final <c>result</c> line instead of losing it to teardown.
        /// </summary>
        public async Task WaitForOutputDrainAsync(int timeoutMs)
        {
            Task stdout = _stdoutTask;
            Task stderr = _stderrTask;
            if (stdout == null)
            {
                return;
            }

            try
            {
                await Task.WhenAny(Task.WhenAll(stdout, stderr ?? Task.CompletedTask), Task.Delay(timeoutMs))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: drain wait failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _lifetimeCts?.Cancel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: cancel failed: {ex.Message}");
            }

            Process process = _process;
            if (process != null)
            {
                process.Exited -= OnProcessExited;

                // Closing stdin is the graceful "we're done" signal; give the CLI a moment to shut down
                // on its own before killing the tree, so it can flush its transcript to disk.
                try
                {
                    if (!process.HasExited)
                    {
                        CloseInput();
                        process.WaitForExit(1500);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JsonLineProcessHost: graceful close failed: {ex.Message}");
                }

                try
                {
                    if (!process.HasExited)
                    {
                        ProcessTree.Kill(ProcessId);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JsonLineProcessHost: kill failed: {ex.Message}");
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JsonLineProcessHost: dispose failed: {ex.Message}");
                }
            }

            _process = null;

            try
            {
                _stdin?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: stdin dispose failed: {ex.Message}");
            }

            _stdin = null;

            try
            {
                _lifetimeCts?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"JsonLineProcessHost: cts dispose failed: {ex.Message}");
            }

            _writeLock.Dispose();
        }
    }
}
