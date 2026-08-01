using System.Runtime.InteropServices;
using System.Text;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal static class WindowsPathAliases
{
    public static string? TryGetShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var length = GetShortPathName(path, null, 0);
        if (length == 0)
        {
            return null;
        }

        var value = new StringBuilder(checked((int)length + 1));
        return GetShortPathName(path, value, value.Capacity) == 0 ? null : value.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder? shortPath, int shortPathBufferLength);
}
