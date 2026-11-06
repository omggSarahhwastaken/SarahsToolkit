using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// Frees memory two ways. The safe way drops only Windows' standby file
    /// cache (the "standby list") via NtSetSystemInformation — cached file
    /// data Windows would discard on its own when RAM is actually needed,
    /// so apps keep their memory and nothing stutters reloading. The deep
    /// way also trims running apps' working sets first (then flushes the
    /// modified list and purges standby), which frees far more but makes
    /// trimmed apps reload slowly when you switch back to them. Requires
    /// admin, which the app already has.
    /// </summary>
    public class MemoryService
    {
        private const int SystemMemoryListInformation = 80;

        private enum MemoryListCommand : int
        {
            MemoryCaptureAccessedBits = 0,
            MemoryCaptureAndResetAccessedBits = 1,
            MemoryEmptyWorkingSets = 2,
            MemoryFlushModifiedList = 3,
            MemoryPurgeStandbyList = 4,
            MemoryPurgeLowPriorityStandbyList = 5,
        }

        // NOTE: despite the SYSTEM_MEMORY_LIST_INFORMATION struct in some
        // headers, the kernel expects just the command DWORD here — passing
        // the struct (with its Size field) gets STATUS_INVALID_PARAMETER.
        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(
            int SystemInformationClass,
            ref int SystemInformation,
            int SystemInformationLength);

        [DllImport("ntdll.dll")]
        private static extern int RtlAdjustPrivilege(
            int Privilege,
            [MarshalAs(UnmanagedType.Bool)] bool Enable,
            [MarshalAs(UnmanagedType.Bool)] bool CurrentThread,
            out bool Enabled);

        private const int SeProfileSingleProcessPrivilege = 13;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static int RunMemoryListCommand(int command)
        {
            return NtSetSystemInformation(SystemMemoryListInformation,
                ref command, sizeof(int));
        }

        // Processes whose working sets must never be trimmed: the kernel,
        // session / login / security plumbing, and the window manager.
        // Trimming one of these can freeze or crash the session.
        private static readonly HashSet<string> ProtectedProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system", "idle", "registry", "memory compression",
                "smss", "csrss", "wininit", "winlogon", "lsass",
                "services", "svchost", "dwm", "fontdrvhost",
            };

        /// <summary>
        /// Deep free-up, in the order every "free memory" tool uses:
        /// trim each (non-critical) process's working set, flush the
        /// modified list, then purge the standby list. Frees far more than
        /// a standby-only purge, because it also moves live app memory to
        /// the pagefile / standby — so trimmed apps reload slowly when you
        /// switch back to them. Best used before launching a game, not
        /// while one is running. Returns true if any step freed memory.
        /// </summary>
        public static bool TryFreeMemoryDeep(out string error, out int trimmedCount)
        {
            error = "";
            trimmedCount = 0;
            bool anySucceeded = false;
            try
            {
                RtlAdjustPrivilege(SeProfileSingleProcessPrivilege, true, false, out _);
                int ownId = Process.GetCurrentProcess().Id;

                foreach (Process proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.Id == ownId || proc.Id <= 4 || proc.HasExited)
                            continue;
                        string name = "";
                        try { name = proc.ProcessName; } catch { continue; }
                        if (ProtectedProcessNames.Contains(name))
                            continue;
                        if (EmptyWorkingSet(proc.Handle))
                        {
                            trimmedCount++;
                            anySucceeded = true;
                        }
                    }
                    catch { /* per-process failure: skip it, keep going */ }
                    finally { try { proc.Dispose(); } catch { } }
                }

                int flushStatus = RunMemoryListCommand((int)MemoryListCommand.MemoryFlushModifiedList);
                if (flushStatus == 0)
                    anySucceeded = true;

                int purgeStatus = RunMemoryListCommand((int)MemoryListCommand.MemoryPurgeStandbyList);
                if (purgeStatus == 0)
                    anySucceeded = true;
                else if (!anySucceeded)
                    error = "Windows refused the memory clean (status 0x" + purgeStatus.ToString("X8") + ").";

                return anySucceeded;
            }
            catch (Exception ex)
            {
                if (!anySucceeded)
                    error = ex.Message;
                return anySucceeded;
            }
        }

        public static ulong GetAvailableBytes()
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref st) ? st.ullAvailPhys : 0;
        }

        public static string FormatGb(ulong bytes)
        {
            return (bytes / 1073741824.0).ToString("0.0") + " GB";
        }

        /// <summary>Drops the standby list. Returns false with an error message on failure.</summary>
        public static bool TryPurgeStandbyList(out string error)
        {
            error = "";
            try
            {
                // SystemMemoryListInformation requires SeProfileSingleProcessPrivilege.
                // Admin tokens carry it but disabled by default; without enabling it
                // the purge is refused.
                RtlAdjustPrivilege(SeProfileSingleProcessPrivilege, true, false, out _);
                int command = (int)MemoryListCommand.MemoryPurgeStandbyList;
                int status = NtSetSystemInformation(SystemMemoryListInformation,
                    ref command, sizeof(int));
                if (status != 0)
                {
                    error = "Windows refused the purge (status 0x" + status.ToString("X8") + ").";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
