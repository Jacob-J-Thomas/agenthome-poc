using System.Runtime.InteropServices;
using System.Text;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal static class WindowsPathAliases
{
    public static string? TryGetVolumeGuidPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        var mountPoint = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(mountPoint))
        {
            return null;
        }

        var volumeName = new StringBuilder(50);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, volumeName, volumeName.Capacity))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(mountPoint, fullPath);
        return string.Equals(relativePath, ".", StringComparison.Ordinal) ? volumeName.ToString() : Path.Combine(volumeName.ToString(), relativePath);
    }

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

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int volumeNameLength);
}
