using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using EmbodySense.Core.Persistence.Triggers.Models;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Rejects non-regular, multiply linked, or substituted queue files using operating-system file identities.</summary>
internal static class TriggerQueueNativeFileInspector
{
    private const uint WindowsReparsePoint = 0x400;

    /// <summary>Inspects an existing path without following a Unix symbolic link.</summary>
    public static TriggerQueueFileIdentity InspectPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw Unsafe(path);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            return InspectHandle(stream.SafeFileHandle, path);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return InspectUnix(path, -1, followPath: false, expectDirectory: false);
        }

        throw new PlatformNotSupportedException("Durable trigger queue file identity checks are not available on this platform.");
    }

    /// <summary>Inspects an open handle and proves it is a single-link regular file.</summary>
    public static TriggerQueueFileIdentity InspectHandle(SafeFileHandle handle, string path)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if ((information.FileAttributes & WindowsReparsePoint) != 0 || information.NumberOfLinks != 1)
            {
                throw Unsafe(path);
            }

            var volume = information.VolumeSerialNumber;
            var file = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            return new TriggerQueueFileIdentity(volume, file, information.NumberOfLinks);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return InspectUnix(path, handle.DangerousGetHandle().ToInt32(), followPath: true, expectDirectory: false);
        }

        throw new PlatformNotSupportedException("Durable trigger queue file identity checks are not available on this platform.");
    }

    /// <summary>Inspects one directory path without accepting reparse points or a non-directory replacement.</summary>
    public static TriggerQueueFileIdentity InspectDirectoryPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint FileShareRead = 0x00000001;
            const uint FileShareWrite = 0x00000002;
            const uint FileShareDelete = 0x00000004;
            const uint OpenExisting = 3;
            const uint FileFlagBackupSemantics = 0x02000000;
            const uint FileFlagOpenReparsePoint = 0x00200000;
            using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if ((information.FileAttributes & ((uint)FileAttributes.Directory | WindowsReparsePoint)) != (uint)FileAttributes.Directory)
            {
                throw Unsafe(path);
            }

            var file = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            return new TriggerQueueFileIdentity(information.VolumeSerialNumber, file, information.NumberOfLinks);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return InspectUnix(path, -1, followPath: false, expectDirectory: true);
        }

        throw new PlatformNotSupportedException("Durable trigger queue directory identity checks are not available on this platform.");
    }

    /// <summary>Inspects an open directory handle used to bind native artifact mutation to lease authority.</summary>
    public static TriggerQueueFileIdentity InspectDirectoryHandle(SafeFileHandle handle, string path)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if ((information.FileAttributes & ((uint)FileAttributes.Directory | WindowsReparsePoint)) != (uint)FileAttributes.Directory)
            {
                throw Unsafe(path);
            }

            var file = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            return new TriggerQueueFileIdentity(information.VolumeSerialNumber, file, information.NumberOfLinks);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return InspectUnix(path, handle.DangerousGetHandle().ToInt32(), followPath: true, expectDirectory: true);
        }

        throw new PlatformNotSupportedException("Descriptor-relative trigger queue directory checks are not available on this platform.");
    }

    private static TriggerQueueFileIdentity InspectUnix(string path, int descriptor, bool followPath, bool expectDirectory)
    {
        if (OperatingSystem.IsLinux())
        {
            return InspectLinux(path, descriptor, followPath, expectDirectory);
        }

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[256], 0, buffer, 256);
            var result = followPath ? Fstat(descriptor, buffer) : Lstat(path, buffer);
            if (result != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            var links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
            var file = unchecked((ulong)Marshal.ReadInt64(buffer, 8));

            ValidateUnixType(path, mode, links, expectDirectory);
            return new TriggerQueueFileIdentity(device, file, links);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TriggerQueueFileIdentity InspectLinux(string path, int descriptor, bool followPath, bool expectDirectory)
    {
        const int AtCurrentWorkingDirectory = -100;
        const int AtNoAutomount = 0x800;
        const int AtEmptyPath = 0x1000;
        const int AtSymbolicLinkNoFollow = 0x100;
        const uint StatxBasicStats = 0x7ff;
        const uint RequiredMask = 0x105;
        var directoryDescriptor = followPath ? descriptor : AtCurrentWorkingDirectory;
        var inspectedPath = followPath ? string.Empty : path;
        var flags = AtNoAutomount | (followPath ? AtEmptyPath : AtSymbolicLinkNoFollow);
        if (Statx(directoryDescriptor, inspectedPath, flags, StatxBasicStats, out var information) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if ((information.Mask & RequiredMask) != RequiredMask)
        {
            throw new InvalidOperationException($"Linux did not return the file identity fields required for trigger queue persistence: `{path}`.");
        }

        ValidateUnixType(path, information.Mode, information.LinkCount, expectDirectory);
        var device = ((ulong)information.DeviceMajor << 32) | information.DeviceMinor;
        return new TriggerQueueFileIdentity(device, information.Inode, information.LinkCount);
    }

    private static void ValidateUnixType(string path, uint mode, ulong links, bool expectDirectory)
    {
        const uint FileTypeMask = 0xF000;
        const uint Directory = 0x4000;
        const uint RegularFile = 0x8000;
        var expectedType = expectDirectory ? Directory : RegularFile;
        if ((mode & FileTypeMask) != expectedType || !expectDirectory && links != 1)
        {
            throw Unsafe(path);
        }
    }

    private static InvalidOperationException Unsafe(string path) => new($"Trigger queue persistence refuses a non-regular, linked, or substituted file: `{path}`.");

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int Lstat(string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out TriggerQueueLinuxStatx information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out TriggerQueueByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
