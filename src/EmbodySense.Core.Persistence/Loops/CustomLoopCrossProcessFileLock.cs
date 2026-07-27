using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

internal static class CustomLoopCrossProcessFileLock
{
    public static bool TryAcquire(FileStream ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const int exclusiveNonblocking = 2 | 4;
        var descriptor = ownership.SafeFileHandle.DangerousGetHandle().ToInt32();
        return Flock(descriptor, exclusiveNonblocking) == 0;
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}
