using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClaudeWatch;

/// <summary>
/// Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: every process assigned to it
/// (including children like the real app exe under `dotnet run`) dies when the job is
/// terminated or the watcher process exits for any reason. No-ops on non-Windows.
/// </summary>
public sealed class JobObject : IDisposable
{
    private nint _handle;

    public JobObject()
    {
        if (!OperatingSystem.IsWindows()) return;

        _handle = CreateJobObject(0, null);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
        };
        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Assign(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows() || _handle == 0) return;
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    /// <summary>Kill every process in the job immediately.</summary>
    public void Terminate()
    {
        if (!OperatingSystem.IsWindows() || _handle == 0) return;
        TerminateJobObject(_handle, 1);
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle); // KILL_ON_JOB_CLOSE fires here if anything is still alive
            _handle = 0;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass, nint lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(nint hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
