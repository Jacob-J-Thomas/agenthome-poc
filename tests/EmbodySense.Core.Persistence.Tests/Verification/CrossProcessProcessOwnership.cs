using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed class CrossProcessProcessOwnership : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly Process _process;
    private readonly SafeFileHandle? _job;

    private CrossProcessProcessOwnership(Process process, SafeFileHandle? job)
    {
        _process = process;
        _job = job;
    }

    internal static CrossProcessProcessOwnership Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return OperatingSystem.IsWindows()
            ? AttachWindows(process)
            : new CrossProcessProcessOwnership(process, null);
    }

    internal void TerminateProcessTree()
    {
        if (_job is not null && !_job.IsInvalid)
        {
            if (!TerminateJobObject(_job.DangerousGetHandle(), 1))
            {
                var error = (uint)Marshal.GetLastWin32Error();
                throw new Win32Exception((int)error, "The cross-process child job could not be terminated.");
            }

            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() => _job?.Dispose();

    private static CrossProcessProcessOwnership AttachWindows(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The cross-process child job could not be created.");
        }

        var job = new SafeFileHandle(handle, ownsHandle: true);
        try
        {
            var limits = new JobObjectExtendedLimitInformationData
            {
                BasicLimitInformation = new JobObjectBasicLimitInformationData
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(job.DangerousGetHandle(), JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The cross-process child job limits could not be configured.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (!AssignProcessToJobObject(job.DangerousGetHandle(), process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The cross-process child could not be assigned to its cleanup job.");
            }

            return new CrossProcessProcessOwnership(process, job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, uint jobObjectInformationClass, IntPtr jobObjectInformation, uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        internal JobObjectBasicLimitInformationData BasicLimitInformation;
        internal JobObjectIoCountersData IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationData
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectIoCountersData
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }
}
