using System;
using System.Runtime.InteropServices;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// Frees Windows' standby file cache (the "standby list") via
    /// NtSetSystemInformation(SystemMemoryListInformation). This is the one
    /// honest memory operation: it drops cached file data that Windows would
    /// discard on its own the moment RAM is actually needed. App working sets
    /// are untouched, so nothing stutters reloading. Requires admin, which the
    /// app already has.
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

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public UIntPtr Size;
            public MemoryListCommand Command;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(
            int SystemInformationClass,
            ref SYSTEM_MEMORY_LIST_INFORMATION SystemInformation,
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
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

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
                var info = new SYSTEM_MEMORY_LIST_INFORMATION
                {
                    Size = (UIntPtr)Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>(),
                    Command = MemoryListCommand.MemoryPurgeStandbyList
                };
                int status = NtSetSystemInformation(SystemMemoryListInformation,
                    ref info, Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>());
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
