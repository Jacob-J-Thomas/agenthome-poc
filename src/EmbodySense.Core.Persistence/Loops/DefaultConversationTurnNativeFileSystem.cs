using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using EmbodySense.Core.Persistence.Loops.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Provides no-follow regular-file opens for default-conversation turn persistence.</summary>
internal static class DefaultConversationTurnNativeFileSystem
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileShareDelete = 0x4;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int UnixPermissionDenied = 13;
    private const int MaximumLeaseInitializationOpenAttempts = 8;
    private const int AtEmptyPath = 0x1000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int PermissionUserReadWrite = 0x180;
    private const ushort UnixPostureMask = 0x0FFF;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFile = 0x8000;
    private const uint StatxMode = 0x2;
    private const uint StatxLinkCount = 0x4;
    private const uint StatxUserId = 0x8;
    private const uint StatxInode = 0x100;

    public static async Task<FileStream?> TryAcquireExclusiveLeaseAsync(
        string path,
        Func<DefaultConversationTurnLeasePhase, CancellationToken, Task>? observeUnixLeasePhase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRegularFile(path, readWrite: true, exclusive: true, create: true, returnNullWhenContended: true);
            return handle is null ? null : new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Default-conversation active-set leases support Windows, Linux, and macOS.");
        }

        return await TryAcquireUnixExclusiveLeaseAsync(path, observeUnixLeasePhase, cancellationToken);
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public cross-process store behavior; its alternate platform is unreachable in any one coverage run.")]
    private static async Task<FileStream?> TryAcquireUnixExclusiveLeaseAsync(
        string path,
        Func<DefaultConversationTurnLeasePhase, CancellationToken, Task>? observeLeasePhase,
        CancellationToken cancellationToken)
    {
        FileStream unixStream;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                unixStream = OpenUnixRegularFile(path, readWrite: true, create: true);
                break;
            }
            catch (IOException exception) when (
                (IsNativeError(exception, UnixPermissionDenied)
                    || exception is UnixLeasePostureException)
                && attempt < MaximumLeaseInitializationOpenAttempts)
            {
                // A peer creating this lease under a restrictive umask can publish the pathname
                // immediately before it restores exact owner-only mode on its own new handle.
                // Retry that one bounded native failure without ever changing a pre-existing inode.
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
            catch (UnixLeasePostureException exception)
            {
                throw new IOException(exception.Message, exception);
            }
        }

        var ownsStream = true;
        try
        {
            if (observeLeasePhase is not null)
            {
                await observeLeasePhase(DefaultConversationTurnLeasePhase.AfterValidatedOpenBeforeExclusiveLock, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RequireUnixRegularFile(unixStream.SafeFileHandle, path, requireLeasePosture: true);
            RequireUnixLeasePathMatchesHandle(unixStream, path);
            if (flock(unixStream.SafeFileHandle, LockExclusive | LockNonBlocking) == 0)
            {
                if (observeLeasePhase is not null)
                {
                    await observeLeasePhase(DefaultConversationTurnLeasePhase.AfterExclusiveLockBeforeFinalValidation, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                RequireUnixRegularFile(unixStream.SafeFileHandle, path, requireLeasePosture: true);
                RequireUnixLeasePathMatchesHandle(unixStream, path);
                ownsStream = false;
                return unixStream;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is 11 or 35)
            {
                return null;
            }

            throw NativeIOException("The default-conversation active-set lease could not be acquired", error);
        }
        finally
        {
            if (ownsStream)
            {
                unixStream.Dispose();
            }
        }
    }

    public static FileStream OpenRegularRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRegularFile(path, readWrite: false, exclusive: false, create: false, returnNullWhenContended: false);
            return new FileStream(handle ?? throw new FileNotFoundException("The default-conversation turn artifact was not found.", path), FileAccess.Read, 4_096, isAsync: false);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Default-conversation persistence reads support Windows, Linux, and macOS.");
        }

        return OpenUnixRegularFile(path, readWrite: false, create: false);
    }

    public static FileStream OpenRegularReadForRetirement(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsRegularFile(path, readWrite: false, exclusive: false, create: false, returnNullWhenContended: false, shareDelete: true);
            return new FileStream(handle ?? throw new FileNotFoundException("The default-conversation turn artifact was not found.", path), FileAccess.Read, 4_096, isAsync: false);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Default-conversation persistence retirement supports Windows, Linux, and macOS.");
        }

        return OpenUnixRegularFile(path, readWrite: false, create: false);
    }

    public static DefaultConversationTurnFileIdentity GetIdentity(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsIdentity(stream.SafeFileHandle);
        }

        return GetUnixIdentity(stream.SafeFileHandle);
    }

    public static void RequireSingleLinkRegularFile(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsWindows())
        {
            RequireWindowsSingleLinkRegularFile(stream.SafeFileHandle);
            return;
        }

        RequireUnixRegularFile(stream.SafeFileHandle, "retirement evidence", requireSingleLink: true);
    }

    public static bool RegularPathMatchesIdentity(string path, DefaultConversationTurnFileIdentity expectedIdentity)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenWindowsRegularFileMetadata(path);
            return handle is not null && GetWindowsIdentity(handle) == expectedIdentity;
        }

        try
        {
            using var stream = OpenUnixRegularFile(path, readWrite: false, create: false);
            RequireUnixRegularFile(stream.SafeFileHandle, path, requireSingleLink: true);
            return GetUnixIdentity(stream.SafeFileHandle) == expectedIdentity;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Windows; Windows is unreachable in Unix coverage runs.")]
    private static DefaultConversationTurnFileIdentity GetWindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeIOException("Default-conversation persistence file identity could not be read", Marshal.GetLastPInvokeError());
        }

        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new DefaultConversationTurnFileIdentity(information.VolumeSerialNumber, fileId);
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Windows; Windows is unreachable in Unix coverage runs.")]
    private static void RequireWindowsSingleLinkRegularFile(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeIOException("Default-conversation persistence file metadata could not be read", Marshal.GetLastPInvokeError());
        }

        if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 || information.NumberOfLinks != 1)
        {
            throw new IOException("Default-conversation retirement evidence is not a single-link regular file.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Unix; Unix is unreachable in Windows coverage runs.")]
    private static DefaultConversationTurnFileIdentity GetUnixIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxMode | StatxInode, out var information) != 0)
            {
                throw NativeIOException("Default-conversation persistence file identity could not be read", Marshal.GetLastPInvokeError());
            }

            if ((information.Mask & StatxInode) == 0)
            {
                throw new IOException("Default-conversation persistence file metadata omitted its filesystem identity.");
            }

            var deviceId = ((ulong)information.DeviceIdMajor << 32) | information.DeviceIdMinor;
            return new DefaultConversationTurnFileIdentity(deviceId, information.Inode);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (fstat(handle, out var information) != 0)
            {
                throw NativeIOException("Default-conversation persistence file identity could not be read", Marshal.GetLastPInvokeError());
            }

            return new DefaultConversationTurnFileIdentity(information.Device, information.Inode);
        }

        throw new PlatformNotSupportedException("Default-conversation file identity supports Windows, Linux, and macOS.");
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Windows; Windows is unreachable in Unix coverage runs.")]
    private static SafeFileHandle? OpenWindowsRegularFile(string path, bool readWrite, bool exclusive, bool create, bool returnNullWhenContended, bool shareDelete = false)
    {
        var desiredAccess = readWrite ? GenericRead | GenericWrite : GenericRead;
        var shareMode = exclusive ? 0U : FileShareRead | (shareDelete ? FileShareDelete : 0U);
        var disposition = create ? OpenAlways : OpenExisting;
        var handle = CreateFile(path, desiredAccess, shareMode, IntPtr.Zero, disposition, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (!create && error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            if (returnNullWhenContended && error is ErrorSharingViolation or ErrorLockViolation)
            {
                return null;
            }

            throw NativeIOException($"Default-conversation persistence file `{path}` could not be opened safely", error);
        }

        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, out WindowsFileAttributeTagInfo information, (uint)Marshal.SizeOf<WindowsFileAttributeTagInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeIOException($"Default-conversation persistence file metadata for `{path}` could not be read", error);
        }

        if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            handle.Dispose();
            throw new IOException($"Default-conversation persistence file `{path}` is not a regular file.");
        }

        return handle;
    }

    [ExcludeFromCodeCoverage(Justification = "This metadata-only Windows pathname revalidation is covered through public source-proof publication behavior on Windows; Windows is unreachable in Unix coverage runs.")]
    private static SafeFileHandle? OpenWindowsRegularFileMetadata(string path)
    {
        var handle = CreateFile(path, 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw NativeIOException($"Default-conversation persistence metadata for `{path}` could not be opened safely", error);
        }

        try
        {
            RequireWindowsSingleLinkRegularFile(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public store behavior on Unix; Unix is unreachable in Windows coverage runs.")]
    private static FileStream OpenUnixRegularFile(string path, bool readWrite, bool create)
    {
        var flags = readWrite ? UnixOpenReadWrite : UnixOpenReadOnly;
        flags |= UnixOpenNoFollow | UnixOpenCloseOnExec | UnixOpenNonBlocking;
        if (create)
        {
            FileStream? createdStream = null;
            try
            {
#pragma warning disable CA1416 // This method is reached only through the explicit Linux/macOS guards above.
                createdStream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.WriteThrough,
                    BufferSize = 1,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
#pragma warning restore CA1416
            }
            catch (IOException)
            {
                // A concurrent or pre-existing entry is reopened below through the no-follow path.
            }

            if (createdStream is not null)
            {
                try
                {
                    if (fchmod(createdStream.SafeFileHandle, PermissionUserReadWrite) != 0)
                    {
                        throw NativeIOException($"Default-conversation persistence file permissions for `{path}` could not be restricted", Marshal.GetLastPInvokeError());
                    }

                    RequireUnixRegularFile(createdStream.SafeFileHandle, path, requireLeasePosture: true);
                    return createdStream;
                }
                catch
                {
                    createdStream.Dispose();
                    throw;
                }
            }
        }

        var descriptor = open(path, flags);

        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (!create && error == ErrorFileNotFound)
            {
                throw new FileNotFoundException("The default-conversation turn artifact was not found.", path);
            }

            throw NativeIOException($"Default-conversation persistence file `{path}` could not be opened without following links", error);
        }

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            RequireUnixRegularFile(
                handle,
                path,
                requireLeasePosture: create,
                identifyLeaseInitializationPosture: create);
            return new FileStream(handle, readWrite ? FileAccess.ReadWrite : FileAccess.Read, readWrite ? 1 : 4_096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public FIFO and symbolic-link store behavior; its alternate Unix ABI is unreachable in any one coverage run.")]
    private static void RequireUnixRegularFile(
        SafeFileHandle handle,
        string path,
        bool requireLeasePosture = false,
        bool requireSingleLink = false,
        bool identifyLeaseInitializationPosture = false)
    {
        ushort mode;
        ulong linkCount = 0;
        uint userId = 0;
        if (OperatingSystem.IsLinux())
        {
            var mask = requireLeasePosture ? StatxMode | StatxLinkCount | StatxUserId : requireSingleLink ? StatxMode | StatxLinkCount : StatxMode;
            if (statx(handle, string.Empty, AtEmptyPath, mask, out LinuxStatx information) != 0)
            {
                throw NativeIOException($"Default-conversation persistence file metadata for `{path}` could not be read", Marshal.GetLastPInvokeError());
            }

            if ((information.Mask & mask) != mask)
            {
                throw new IOException($"Default-conversation persistence file metadata for `{path}` omitted required file posture.");
            }

            mode = information.Mode;
            linkCount = information.LinkCount;
            userId = information.UserId;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (fstat(handle, out MacStat information) != 0)
            {
                throw NativeIOException($"Default-conversation persistence file metadata for `{path}` could not be read", Marshal.GetLastPInvokeError());
            }

            mode = information.Mode;
            linkCount = information.LinkCount;
            userId = information.UserId;
        }
        else
        {
            throw new PlatformNotSupportedException("Default-conversation regular-file validation supports Windows, Linux, and macOS.");
        }

        if ((mode & UnixFileTypeMask) != UnixRegularFile)
        {
            throw new IOException($"Default-conversation persistence file `{path}` is not a regular file.");
        }

        if (requireSingleLink && linkCount != 1)
        {
            throw new IOException($"Default-conversation retirement evidence `{path}` is not a single-link regular file.");
        }

        if (requireLeasePosture
            && (linkCount != 1
                || (mode & UnixPostureMask) != PermissionUserReadWrite
                || userId != geteuid()))
        {
            var message = $"Default-conversation active-set lease `{path}` does not have exclusive owner-only file posture.";
            if (identifyLeaseInitializationPosture)
            {
                throw new UnixLeasePostureException(message);
            }

            throw new IOException(message);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This OS-native shim is covered through public lease pathname-replacement behavior; its alternate Unix ABI is unreachable in any one coverage run.")]
    private static void RequireUnixLeasePathMatchesHandle(FileStream leaseStream, string path)
    {
        using var pathStream = OpenUnixRegularFile(path, readWrite: true, create: false);
        RequireUnixRegularFile(pathStream.SafeFileHandle, path, requireLeasePosture: true);
        if (GetUnixIdentity(leaseStream.SafeFileHandle) != GetUnixIdentity(pathStream.SafeFileHandle))
        {
            throw new IOException($"Default-conversation active-set lease `{path}` no longer names the validated file.");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Native error translation is exercised only through the host-specific shims excluded above.")]
    private static IOException NativeIOException(string message, int error)
    {
        return new IOException($"{message}: {new Win32Exception(error).Message}", unchecked((int)(0x80070000U | (uint)error)));
    }

    private static bool IsNativeError(IOException exception, int error)
    {
        return (exception.HResult & 0xFFFF) == error;
    }

    private static int UnixOpenReadOnly => 0;
    private static int UnixOpenReadWrite => 2;
    private static int UnixOpenNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int UnixOpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int UnixOpenNonBlocking => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    private struct WindowsFileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    private struct WindowsByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint DeviceIdMajor;
        public uint DeviceIdMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Spare1;
        public ulong Spare2;
        public ulong Spare3;
        public ulong Spare4;
        public ulong Spare5;
        public ulong Spare6;
        public ulong Spare7;
        public ulong Spare8;
        public ulong Spare9;
        public ulong Spare10;
        public ulong Spare11;
        public ulong Spare12;
    }

    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private struct MacStat
    {
        public uint Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public uint RawDevice;
        public MacTimespec AccessTime;
        public MacTimespec ModificationTime;
        public MacTimespec ChangeTime;
        public MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Reserved1;
        public long Reserved2;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out WindowsFileAttributeTagInfo fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsByHandleFileInformation fileInformation);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle file, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle file, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(SafeFileHandle file, out MacStat information);

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
