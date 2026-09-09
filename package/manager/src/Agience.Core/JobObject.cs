using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Agience.Core;

/// <summary>
/// A Windows job object that kills everything in it when this process goes away.
/// </summary>
/// <remarks>
/// <para>
/// Without this, "stop" is a claim rather than an outcome. Terminating a process on Windows does
/// not terminate its children — and a uvicorn worker, a pip resolver and an ember serve loop all
/// have them. The visible failure is the one that costs an afternoon: the tray reports the service
/// stopped, the icon goes grey, and the port stays held by an orphan process the tray does not
/// track, so the next start fails to bind against a process nothing will admit to owning.
/// </para>
/// <para>
/// It also covers the case no stop path can: if the tray is killed — Task Manager, a crash, a
/// forced logoff — every handle it holds closes, this job closes with them, and the services die
/// with it. That is the difference between a supervisor and a process that merely started things.
/// </para>
/// <para>
/// One job for the whole installation, not one per service. A per-service job would be tidier and
/// buys nothing: the kill-on-close guarantee is about this process exiting, and per-service
/// stopping is done by terminating the service's own process tree, which
/// <see cref="Process.Kill(bool)"/> already does.
/// </para>
/// </remarks>
public sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint JobObjectLimitBreakawayOk = 0x0800;

    private IntPtr _handle;

    /// <summary>Is a job in force? False on the systems where one could not be created.</summary>
    public bool Active => _handle != IntPtr.Zero;

    /// <summary>
    /// Why there is no job, when there is none.
    /// </summary>
    /// <remarks>
    /// Failing to make a job is not a reason to refuse to run. Nested jobs are the usual cause —
    /// a CI agent or a terminal that already put this process in one that forbids breakaway — and
    /// the services still start, stop and report correctly without it. What is lost is only the
    /// cleanup after an abnormal exit, so the failure is recorded and named rather than thrown.
    /// </remarks>
    public string? Unavailable { get; private set; }

    public JobObject()
    {
        try
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                Unavailable = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return;
            }

            var info = new JobObjectExtendedLimitInformationStruct
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    // BREAKAWAY_OK alongside KILL_ON_JOB_CLOSE: a child that deliberately breaks
                    // away is then allowed to, rather than failing to start at all. Nothing here
                    // asks to break away; this only keeps a third-party helper that does from
                    // turning into a launch failure.
                    LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk,
                },
            };

            var length = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    Unavailable = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Close();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exc)
        {
            Unavailable = exc.Message;
            Close();
        }
    }

    /// <summary>Put a process in the job. Returns false — never throws — if it could not be done.</summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception)
        {
            // The process exited between Start() and here, which is a real and ordinary race: a
            // service that fails on its first import is gone in well under a second.
            return false;
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void Close()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    ~JobObject() => Close();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
