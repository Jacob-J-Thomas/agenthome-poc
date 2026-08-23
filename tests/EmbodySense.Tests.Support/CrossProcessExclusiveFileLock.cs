using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Tests.Support;

public sealed class CrossProcessExclusiveFileLock : IDisposable
{
    private const uint WindowsExclusiveLock = 0x00000002;
    private const uint WindowsFailImmediately = 0x00000001;
    private const int UnixExclusiveNonblockingLock = 2 | 4;
    private FileStream? _stream;

    private CrossProcessExclusiveFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static CrossProcessExclusiveFileLock Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (!TryAcquire(stream))
            {
                throw new IOException("The test fixture could not acquire the cross-process file lock.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return new CrossProcessExclusiveFileLock(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
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

        throw new PlatformNotSupportedException("Cross-process file locking is not supported on this platform.");
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
