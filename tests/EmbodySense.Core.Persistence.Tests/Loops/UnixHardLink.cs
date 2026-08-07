using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal static class UnixHardLink
{
    public static void Create(string linkPath, string targetPath)
    {
        if (link(targetPath, linkPath) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The test hard link could not be created.");
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int link(string targetPath, string linkPath);
}
