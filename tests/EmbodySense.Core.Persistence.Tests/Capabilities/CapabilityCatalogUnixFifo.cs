using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal static class CapabilityCatalogUnixFifo
{
    private const int PermissionUserReadWrite = 0x180;

    public static bool TryCreate(string path)
    {
        return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && mkfifo(path, PermissionUserReadWrite) == 0;
    }

    public static bool TryCreateHardLink(string existingPath, string linkPath)
    {
        return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && link(existingPath, linkPath) == 0;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkfifo(string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string existingPath, string linkPath);
}
