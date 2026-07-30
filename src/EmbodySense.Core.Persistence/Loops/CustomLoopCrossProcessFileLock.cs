using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

internal static class CustomLoopCrossProcessFileLock
{
    public static bool TryAcquire(FileStream ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (OperatingSystem.IsWindows())
        {
            var overlapped = new Overlapped();
            return LockFileEx(ownership.SafeFileHandle, LockFileExclusive | LockFileFailImmediately, 0, 1, 0, ref overlapped);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            const int ExclusiveNonblocking = 2 | 4;
            var descriptor = ownership.SafeFileHandle.DangerousGetHandle().ToInt32();
            return Flock(descriptor, ExclusiveNonblocking) == 0;
        }

        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool LockFileEx(SafeFileHandle fileHandle, uint flags, uint reserved, uint numberOfBytesToLockLow, uint numberOfBytesToLockHigh, ref Overlapped overlapped);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    private const uint LockFileExclusive = 0x00000002;
    private const uint LockFileFailImmediately = 0x00000001;
}
