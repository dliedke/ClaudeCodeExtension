/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Process-tree termination for native-mode agent sessions (ToolHelp32, no VS dependency)
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Kills an agent process together with everything it spawned.
    /// <para>
    /// Agent CLIs are launcher shims (<c>claude.cmd</c> → node, <c>wsl.exe</c> → distro process) that
    /// keep working children alive after the parent is killed, so killing only the process we started
    /// leaves orphans holding the working directory and the API session. Children are enumerated with
    /// a ToolHelp32 snapshot: a kernel call, sub-millisecond, and — unlike WMI — with no service
    /// dependency that can be disabled on locked-down machines.
    /// </para>
    /// <para>
    /// Deliberately standalone (its own P/Invokes, no <c>ClaudeCodeControl</c>) so the whole
    /// <c>Agents</c> namespace stays free of Visual Studio dependencies and unit-testable.
    /// <c>Controls/ClaudeCodeControl.Terminal.cs</c> keeps its own equivalent for the embedded
    /// terminal; the two are intentionally not shared while the terminal path stays frozen.
    /// </para>
    /// </summary>
    internal static class ProcessTree
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Returns the direct children of <paramref name="parentPid"/>.
        /// </summary>
        private static List<int> GetChildProcessIds(int parentPid)
        {
            var result = new List<int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32ParentProcessID == (uint)parentPid)
                        {
                            result.Add((int)entry.th32ProcessID);
                        }
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProcessTree.GetChildProcessIds({parentPid}) failed: {ex.Message}");
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return result;
        }

        /// <summary>
        /// Kills <paramref name="processId"/> and every descendant, children first so a parent cannot
        /// respawn them. Never throws.
        /// </summary>
        public static void Kill(int processId)
        {
            Kill(processId, new HashSet<int>());
        }

        private static void Kill(int processId, HashSet<int> visited)
        {
            // PID reuse plus a cyclic parent chain would otherwise recurse forever; guarding on
            // `visited` also makes repeated Dispose() calls harmless.
            if (processId <= 0 || processId == Process.GetCurrentProcess().Id || !visited.Add(processId))
            {
                return;
            }

            foreach (int childPid in GetChildProcessIds(processId))
            {
                Kill(childPid, visited);
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
            }
            catch (ArgumentException)
            {
                // Already exited between the snapshot and here — the normal race, not an error.
            }
            catch (InvalidOperationException)
            {
                // Same race, surfaced differently once the Process object is already associated.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProcessTree.Kill({processId}) failed: {ex.Message}");
            }
        }
    }
}
