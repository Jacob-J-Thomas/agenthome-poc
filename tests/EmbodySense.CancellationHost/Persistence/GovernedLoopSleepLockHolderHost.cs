using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.CancellationHost.Persistence;

internal static class GovernedLoopSleepLockHolderHost
{
    private const uint WindowsExclusiveLock = 0x00000002;
    private const uint WindowsFailImmediately = 0x00000001;
    private const int UnixExclusiveNonblockingLock = 2 | 4;

    internal static async Task<int> RunAsync(string lockPath, string releaseMarker, string readyMarker, string resultMarker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        await using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (!TryAcquire(stream))
        {
            throw new IOException("The governed-loop sleep lock holder could not acquire the native queue lock.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        await CrossProcessMarkerProtocol.WriteResultAsync(resultMarker, "released");
        return 0;
    }

    private static bool TryAcquire(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            var overlapped = new Overlapped();
            return LockFileEx(stream.SafeFileHandle, WindowsExclusiveLock | WindowsFailImmediately, 0, 1, 0, ref overlapped);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), UnixExclusiveNonblockingLock) == 0;
        }

        throw new PlatformNotSupportedException("Native cross-process file locking is not supported on this platform.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool LockFileEx(
        SafeFileHandle fileHandle,
        uint flags,
        uint reserved,
        uint numberOfBytesToLockLow,
        uint numberOfBytesToLockHigh,
        ref Overlapped overlapped);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}
