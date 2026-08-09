using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EmbodySense.Core.Persistence.Capabilities.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Provides the minimal no-follow native filesystem surface required by catalog persistence.</summary>
internal static class CapabilityCatalogNativeFileSystem
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint NtFileOpen = 1;
    private const uint NtFileCreate = 2;
    private const uint NtFileOpenIf = 3;
    private const uint NtFileDirectory = 0x00000001;
    private const uint NtFileWriteThrough = 0x00000002;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileNonDirectory = 0x00000040;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint OpenExisting = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorMoreData = 234;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorNoMoreFiles = 18;
    private const int FileRenameInformation = 10;
    private const int InitialWindowsDirectoryBufferBytes = 16 * 1_024;
    private const int MaximumWindowsDirectoryBufferBytes = 60 * 1_024;
    private const int AtEmptyPath = 0x1000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int PermissionUserReadWrite = 0x180;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFile = 0x8000;
    private const uint StatxMode = 0x2;
    private const uint StatxLinkCount = 0x4;
    private const uint AttributeVolumeCapabilities = 0x00020000;
    private const uint AttributeVolumeInfo = 0x80000000;

    public static SafeFileHandle? OpenDirectory(string fullPath, SafeFileHandle? parent, string? name, bool create, ICapabilityCatalogDurabilityBarrier durabilityBarrier, out bool created)
    {
        return OperatingSystem.IsWindows() ? OpenWindowsDirectory(fullPath, parent, name, create, durabilityBarrier, out created) : OpenUnixDirectory(parent, name, create, out created);
    }

    public static SafeFileHandle? OpenRegularFile(string fullPath, SafeFileHandle parent, string name, FileMode mode, FileAccess access, FileShare share, bool writeThrough)
    {
        return OperatingSystem.IsWindows() ? OpenWindowsFile(parent, name, mode, access, share, writeThrough) : OpenUnixFile(parent, name, mode, access);
    }

    public static FileStream? TryAcquireExclusiveLock(string fullPath, SafeFileHandle parent, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = OpenWindowsHandle(parent, name, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, writeThrough: true, allowDirectory: false, returnNullWhenMissing: false, returnNullWhenContended: true);
            return handle is null ? null : new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
        }

        var unixHandle = OpenUnixFile(parent, name, FileMode.OpenOrCreate, FileAccess.ReadWrite) ?? throw new IOException("The capability catalog lock parent is unavailable.");
        if (flock(unixHandle, LockExclusive | LockNonBlocking) == 0)
        {
            return new FileStream(unixHandle, FileAccess.ReadWrite, 1, isAsync: false);
        }

        var error = Marshal.GetLastPInvokeError();
        unixHandle.Dispose();
        if (error is 11 or 35)
        {
            return null;
        }

        throw NativeIOException("The capability catalog lock could not be acquired", error);
    }

    public static void RequireSingleLink(SafeFileHandle handle, string name)
    {
        uint linkCount;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw NativeIOException($"Capability catalog file link metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            linkCount = information.NumberOfLinks;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxLinkCount, out var information) != 0 || (information.Mask & StatxLinkCount) == 0)
            {
                throw NativeIOException($"Capability catalog file link metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            linkCount = information.LinkCount;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (fstat(handle, out CapabilityCatalogMacStat information) != 0)
            {
                throw NativeIOException($"Capability catalog file link metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            linkCount = information.LinkCount;
        }
        else
        {
            throw new PlatformNotSupportedException("Capability catalog hard-link validation supports Windows, Linux, and macOS.");
        }

        if (linkCount != 1)
        {
            throw new IOException($"Capability catalog file `{name}` is hard-linked and cannot be trusted as immutable evidence.");
        }
    }

    public static void MoveFile(string sourceFullPath, string destinationFullPath, SafeFileHandle parent, string sourceName, string destinationName)
    {
        if (OperatingSystem.IsWindows())
        {
            using var source = OpenWindowsRelative(parent, sourceName, DeleteAccess | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileNonDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | NtFileWriteThrough, returnNullWhenMissing: false, returnNullWhenContended: false) ?? throw new FileNotFoundException("The capability catalog staging artifact disappeared before its retained-handle move.", sourceFullPath);
            ValidateWindowsHandle(source, sourceName, requireDirectory: false);
            RenameWindowsByHandle(source, parent, destinationName, replaceExisting: true, destinationFullPath);
            return;
        }

        if (renameat(parent, sourceName, parent, destinationName) != 0)
        {
            throw NativeIOException("The capability catalog artifact could not be moved atomically", Marshal.GetLastPInvokeError());
        }
    }

    public static void DeleteFileIfPresent(string fullPath, SafeFileHandle parent, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            using var file = OpenWindowsRelative(parent, name, DeleteAccess | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileNonDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint, returnNullWhenMissing: true, returnNullWhenContended: false);
            if (file is not null)
            {
                ValidateWindowsHandle(file, name, requireDirectory: false);
                if (!SetFileInformationByHandle(file, FileInfoByHandleClass.FileDispositionInfo, [1], 1))
                {
                    throw NativeIOException($"Capability catalog temporary artifact `{fullPath}` could not be removed by retained handle", Marshal.GetLastPInvokeError());
                }
            }
            return;
        }

        if (unlinkat(parent, name, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 2)
            {
                throw NativeIOException("The capability catalog temporary artifact could not be removed", error);
            }
        }
    }

    public static void FlushToDisk(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            stream.Flush(flushToDisk: true);
        }
        else if (fsync(stream.SafeFileHandle) != 0)
        {
            throw NativeIOException("The capability catalog artifact could not be flushed durably", Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushAfterRename(string destinationFullPath, SafeFileHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            using var destination = OpenWindowsHandle(parent, Path.GetFileName(destinationFullPath), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, writeThrough: true, allowDirectory: false, returnNullWhenMissing: false, returnNullWhenContended: false) ?? throw new IOException("The renamed capability catalog artifact is unavailable for its durability barrier.");
            if (!FlushFileBuffers(destination))
            {
                throw NativeIOException("The renamed capability catalog artifact could not be flushed durably", Marshal.GetLastPInvokeError());
            }
            return;
        }

        if (fsync(parent) != 0)
        {
            throw NativeIOException("The capability catalog parent-directory rename metadata could not be flushed durably", Marshal.GetLastPInvokeError());
        }
    }

    public static void FlushAfterDirectoryCreate(SafeFileHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows directory creation already used a retained-handle rename followed by FlushFileBuffers.
            return;
        }

        if (fsync(parent) != 0)
        {
            throw NativeIOException("The capability catalog parent-directory creation metadata could not be flushed durably", Marshal.GetLastPInvokeError());
        }
    }

    public static void SetUserOnlyPermissions(SafeFileHandle file)
    {
        if (!OperatingSystem.IsWindows() && fchmod(file, PermissionUserReadWrite) != 0)
        {
            throw NativeIOException("The capability catalog authentication key permissions could not be restricted", Marshal.GetLastPInvokeError());
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool TryGetExistingWindowsDirectoryIdentity(string fullPath, out string identity, out string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        identity = string.Empty;
        finalPath = string.Empty;
        var handle = CreateFile(fullPath, 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return false;
            }

            throw NativeIOException("The capability catalog root topology could not inspect an existing directory", error);
        }

        using (handle)
        {
            RequireWindowsTopologyQuery(GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, out CapabilityCatalogFileAttributeTagInfo attributes, (uint)Marshal.SizeOf<CapabilityCatalogFileAttributeTagInfo>()), "The capability catalog root topology could not inspect an existing directory");

            if ((attributes.FileAttributes & FileAttributeDirectory) == 0)
            {
                return false;
            }

            RequireWindowsTopologyQuery(GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, out CapabilityCatalogFileIdInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileIdInfo>()), "The capability catalog root topology could not inspect an existing directory");

            identity = $"{information.VolumeSerialNumber:x16}:{information.FileId:N}";
            var finalPathBuffer = new StringBuilder(32_768);
            var finalPathLength = GetFinalPathNameByHandle(handle, finalPathBuffer, finalPathBuffer.Capacity, 0);
            RequireWindowsTopologyQuery(finalPathLength != 0, "The capability catalog root topology could not resolve an existing directory");

            if (finalPathLength >= finalPathBuffer.Capacity)
            {
                throw new IOException("The capability catalog root topology resolved an existing directory path beyond its safety bound.");
            }

            finalPath = finalPathBuffer.ToString();
            return true;
        }
    }

    public static string GetPhysicalIdentityMaterial(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(directory, FileInfoByHandleClass.FileIdInfo, out CapabilityCatalogFileIdInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileIdInfo>()))
            {
                throw NativeIOException("The capability catalog workspace physical identity could not be read", Marshal.GetLastPInvokeError());
            }

            return $"windows:{information.VolumeSerialNumber:x16}:{Convert.ToHexString(information.FileId.ToByteArray()).ToLowerInvariant()}";
        }

        if (OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(CapabilityCatalogWorkspaceIdentity.LinuxUnsupportedMessage);
        }

        if (OperatingSystem.IsMacOS())
        {
            CapabilityCatalogWorkspaceIdentity.RequireNativePhysicalIdentityRead(fstat(directory, out CapabilityCatalogMacStat information), Marshal.GetLastPInvokeError());
            return CapabilityCatalogWorkspaceIdentity.CreateUnixPhysicalIdentityMaterial("macos", information.Device, 0, information.Inode, information.Generation, information.BirthTime.Seconds, information.BirthTime.Nanoseconds, information.Generation == 0 && MacVolumeUsesNonRecycledObjectIds(directory));
        }

        throw new PlatformNotSupportedException("Capability catalog physical workspace identity supports Windows and macOS.");
    }

    private static bool MacVolumeUsesNonRecycledObjectIds(SafeFileHandle directory)
    {
        var attributes = new CapabilityCatalogMacAttributeList { BitmapCount = 5, VolumeAttributes = AttributeVolumeInfo | AttributeVolumeCapabilities };
        return CapabilityCatalogWorkspaceIdentity.MacVolumeCapabilitiesProveNonRecycledObjectIdentity(fgetattrlist(directory, ref attributes, out CapabilityCatalogMacVolumeCapabilitiesBuffer capabilities, (nuint)Marshal.SizeOf<CapabilityCatalogMacVolumeCapabilitiesBuffer>(), 0), Marshal.GetLastPInvokeError(), capabilities.Length, (uint)Marshal.SizeOf<CapabilityCatalogMacVolumeCapabilitiesBuffer>(), capabilities.ValidFormatCapabilities, capabilities.FormatCapabilities);
    }

    public static string GetDirectoryEnumerationPath(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows directory enumeration uses the leased canonical path.");
        }

        var descriptor = directory.DangerousGetHandle().ToInt64();
        var procPath = $"/proc/self/fd/{descriptor}";
        if (Directory.Exists(procPath))
        {
            return procPath;
        }

        var devicePath = $"/dev/fd/{descriptor}";
        return Directory.Exists(devicePath) ? devicePath : throw new IOException("No handle-relative directory enumeration surface is available on this platform.");
    }

    public static IReadOnlyList<CapabilityCatalogDirectoryEntry> EnumerateWindowsDirectory(SafeFileHandle directory, int maximumEntries)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows directory enumeration is available only on Windows.");
        }
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        const int FileNameLengthOffset = 60;
        const int FileAttributesOffset = 56;
        const int FileNameOffset = 68;
        var entries = new List<CapabilityCatalogDirectoryEntry>();
        var buffer = new byte[InitialWindowsDirectoryBufferBytes];
        var informationClass = FileInfoByHandleClass.FileFullDirectoryRestartInfo;
        while (true)
        {
            if (!GetFileInformationByHandleEx(directory, informationClass, buffer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNoMoreFiles)
                {
                    return entries;
                }
                if (error == ErrorMoreData && entries.Count == 0 && buffer.Length < MaximumWindowsDirectoryBufferBytes)
                {
                    Array.Resize(ref buffer, Math.Min(checked(buffer.Length * 2), MaximumWindowsDirectoryBufferBytes));
                    continue;
                }
                throw NativeIOException("The capability catalog directory handle could not be enumerated within its native buffer bound", error);
            }
            informationClass = FileInfoByHandleClass.FileFullDirectoryInfo;

            var offset = 0;
            while (true)
            {
                if (offset < 0 || offset > buffer.Length - FileNameOffset)
                {
                    throw new IOException("Windows returned malformed capability catalog directory enumeration data.");
                }
                var nextOffset = BitConverter.ToUInt32(buffer, offset);
                var attributes = BitConverter.ToUInt32(buffer, offset + FileAttributesOffset);
                var fileNameLength = BitConverter.ToUInt32(buffer, offset + FileNameLengthOffset);
                if (fileNameLength == 0 || (fileNameLength & 1) != 0 || fileNameLength > buffer.Length - offset - FileNameOffset)
                {
                    throw new IOException("Windows returned a malformed capability catalog directory entry.");
                }
                var name = Encoding.Unicode.GetString(buffer, offset + FileNameOffset, checked((int)fileNameLength));
                if (name is not "." and not "..")
                {
                    if (entries.Count >= maximumEntries)
                    {
                        return entries;
                    }
                    var kind = (attributes & FileAttributeReparsePoint) != 0 ? CapabilityCatalogDirectoryEntryKind.Unsafe : (attributes & FileAttributeDirectory) != 0 ? CapabilityCatalogDirectoryEntryKind.Directory : CapabilityCatalogDirectoryEntryKind.RegularFile;
                    entries.Add(new CapabilityCatalogDirectoryEntry(name, kind));
                }
                if (nextOffset == 0)
                {
                    break;
                }
                var minimumNextOffset = checked((uint)FileNameOffset + fileNameLength);
                if ((nextOffset & 7) != 0
                    || nextOffset < minimumNextOffset
                    || nextOffset > buffer.Length - offset)
                {
                    throw new IOException("Windows returned an invalid capability catalog directory continuation offset.");
                }
                offset = checked(offset + (int)nextOffset);
            }
        }
    }

    public static IReadOnlyList<CapabilityCatalogDirectoryEntry> EnumerateMacDirectory(SafeFileHandle directory, int maximumEntries)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Native directory enumeration is required only on macOS.");
        }
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
        var duplicate = dup(directory.DangerousGetHandle().ToInt32());
        if (duplicate < 0)
        {
            throw NativeIOException("The capability catalog directory handle could not be duplicated", Marshal.GetLastPInvokeError());
        }
        var stream = fdopendir(duplicate);
        if (stream == IntPtr.Zero)
        {
            _ = close(duplicate);
            throw NativeIOException("The capability catalog directory stream could not be opened", Marshal.GetLastPInvokeError());
        }
        try
        {
            var entries = new List<CapabilityCatalogDirectoryEntry>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var pointer = readdir(stream);
                if (pointer == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error == 0 ? entries : throw NativeIOException("The capability catalog directory stream could not be read", error);
                }
                var entry = Marshal.PtrToStructure<CapabilityCatalogMacDirent>(pointer);
                if (entry.NameLength is 0 or > 1_024 || entry.Name is null || entry.NameLength > entry.Name.Length)
                {
                    throw new IOException("The capability catalog directory stream returned a malformed entry.");
                }
                var name = Encoding.UTF8.GetString(entry.Name, 0, entry.NameLength);
                if (name is "." or "..")
                {
                    continue;
                }
                if (entries.Count >= maximumEntries)
                {
                    return entries;
                }
                var kind = entry.Type switch { 4 => CapabilityCatalogDirectoryEntryKind.Directory, 8 => CapabilityCatalogDirectoryEntryKind.RegularFile, 0 => CapabilityCatalogDirectoryEntryKind.Unknown, _ => CapabilityCatalogDirectoryEntryKind.Unsafe };
                entries.Add(new CapabilityCatalogDirectoryEntry(name, kind));
            }
        }
        finally
        {
            _ = closedir(stream);
        }
    }

    private static SafeFileHandle? OpenWindowsDirectory(string fullPath, SafeFileHandle? parent, string? name, bool create, ICapabilityCatalogDurabilityBarrier durabilityBarrier, out bool created)
    {
        created = false;
        if (parent is null)
        {
            var root = CreateFile(fullPath, FileListDirectory | FileTraverse | FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (root.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                root.Dispose();
                return !create && error is ErrorFileNotFound or ErrorPathNotFound ? null : throw NativeIOException($"Capability catalog filesystem root `{fullPath}` could not be opened safely", error);
            }
            ValidateWindowsHandle(root, fullPath, requireDirectory: true);
            return root;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var handle = OpenWindowsRelative(parent, name, FileListDirectory | FileTraverse | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint, returnNullWhenMissing: true, returnNullWhenContended: false);
        if (handle is null)
        {
            if (!create)
            {
                return null;
            }
            handle = CreateWindowsDirectoryDurably(fullPath, parent, name, durabilityBarrier);
            created = true;
        }
        ValidateWindowsHandle(handle, name, requireDirectory: true);
        return handle;
    }

    private static SafeFileHandle CreateWindowsDirectoryDurably(string fullPath, SafeFileHandle parent, string name, ICapabilityCatalogDurabilityBarrier durabilityBarrier)
    {
        var temporaryName = $".{name}.{Guid.NewGuid():N}.mkdir";
        var temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, temporaryName);
        SafeFileHandle? staging = null;
        SafeFileHandle? movedIdentity = null;
        var renamed = false;
        try
        {
            staging = OpenWindowsRelative(parent, temporaryName, GenericRead | GenericWrite | DeleteAccess | SynchronizeAccess, FileShareRead | FileShareWrite, NtFileCreate, NtFileDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | NtFileWriteThrough, returnNullWhenMissing: false, returnNullWhenContended: false) ?? throw new IOException("The capability catalog staging directory could not be created relative to retained authority.");
            ValidateWindowsHandle(staging, temporaryName, requireDirectory: true);
            var expectedIdentity = GetWindowsFileIdentity(staging, temporaryPath);
            durabilityBarrier.BeforeDirectoryMove(temporaryPath, fullPath);
            RenameWindowsByHandle(staging, parent, name, replaceExisting: false, fullPath);
            renamed = true;
            if (!FlushFileBuffers(staging))
            {
                throw NativeIOException($"Capability catalog directory `{fullPath}` could not be flushed after its handle-based move", Marshal.GetLastPInvokeError());
            }

            movedIdentity = OpenWindowsRelative(parent, name, FileListDirectory | FileTraverse | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint, returnNullWhenMissing: false, returnNullWhenContended: false) ?? throw new IOException("The new capability catalog directory could not be identity-checked relative to retained authority.");
            ValidateWindowsHandle(movedIdentity, name, requireDirectory: true);
            RequireSameWindowsFileIdentity(expectedIdentity, GetWindowsFileIdentity(movedIdentity, fullPath), fullPath);

            staging.Dispose();
            staging = null;
            durabilityBarrier.AfterDirectoryMove(temporaryPath, fullPath);

            var retained = OpenWindowsRelative(parent, name, FileListDirectory | FileTraverse | FileReadAttributes | SynchronizeAccess, FileShareRead | FileShareWrite | FileShareDelete, NtFileOpen, NtFileDirectory | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint, returnNullWhenMissing: false, returnNullWhenContended: false) ?? throw new IOException("The new capability catalog directory could not be retained relative to parent authority.");
            try
            {
                ValidateWindowsHandle(retained, name, requireDirectory: true);
                RequireSameWindowsFileIdentity(expectedIdentity, GetWindowsFileIdentity(retained, fullPath), fullPath);
                return retained;
            }
            catch
            {
                retained.Dispose();
                throw;
            }
        }
        finally
        {
            try
            {
                if (!renamed && staging is not null && !staging.IsInvalid && !staging.IsClosed)
                {
                    MarkWindowsDirectoryForDeletion(staging);
                }
            }
            finally
            {
                staging?.Dispose();
                movedIdentity?.Dispose();
            }
        }
    }

    private static void RenameWindowsByHandle(SafeFileHandle source, SafeFileHandle parent, string destinationName, bool replaceExisting, string destinationPath)
    {
        var fileName = Encoding.Unicode.GetBytes(destinationName);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var unalignedBufferSize = checked(fileNameOffset + fileName.Length + sizeof(char));
        var bufferSize = checked((unalignedBufferSize + IntPtr.Size - 1) & -IntPtr.Size);
        var information = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, information, bufferSize);
            Marshal.WriteByte(information, replaceExisting ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(information, rootDirectoryOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(information, fileNameLengthOffset, fileName.Length);
            Marshal.Copy(fileName, 0, IntPtr.Add(information, fileNameOffset), fileName.Length);
            var status = NtSetInformationFile(source, out _, information, (uint)bufferSize, FileRenameInformation);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                throw NativeIOException($"Capability catalog entry could not be moved by retained handle to `{destinationPath}`", unchecked((int)RtlNtStatusToDosError(status)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }

    private static void MarkWindowsDirectoryForDeletion(SafeFileHandle directory)
    {
        if (!SetFileInformationByHandle(directory, FileInfoByHandleClass.FileDispositionInfo, [1], 1))
        {
            throw NativeIOException("Capability catalog staging directory could not be marked for exact cleanup", Marshal.GetLastPInvokeError());
        }
    }

    private static CapabilityCatalogFileIdInfo GetWindowsFileIdentity(SafeFileHandle directory, string path)
    {
        if (!GetFileInformationByHandleEx(directory, FileInfoByHandleClass.FileIdInfo, out CapabilityCatalogFileIdInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileIdInfo>()))
        {
            throw NativeIOException($"Capability catalog directory identity for `{path}` could not be read", Marshal.GetLastPInvokeError());
        }
        return information;
    }

    private static void RequireSameWindowsFileIdentity(CapabilityCatalogFileIdInfo expected, CapabilityCatalogFileIdInfo actual, string path)
    {
        if (expected.VolumeSerialNumber != actual.VolumeSerialNumber || expected.FileId != actual.FileId)
        {
            throw new IOException($"Capability catalog directory `{path}` was substituted during durable creation.");
        }
    }

    private static SafeFileHandle? OpenWindowsFile(SafeFileHandle parent, string name, FileMode mode, FileAccess access, FileShare share, bool writeThrough)
    {
        return OpenWindowsHandle(parent, name, mode, access, share, writeThrough, allowDirectory: false, returnNullWhenMissing: mode == FileMode.Open, returnNullWhenContended: false);
    }

    private static SafeFileHandle? OpenWindowsHandle(SafeFileHandle parent, string name, FileMode mode, FileAccess access, FileShare share, bool writeThrough, bool allowDirectory, bool returnNullWhenMissing, bool returnNullWhenContended)
    {
        var desiredAccess = access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        var shareMode = 0U;
        if (share.HasFlag(FileShare.Read))
        {
            shareMode |= FileShareRead;
        }
        if (share.HasFlag(FileShare.Write))
        {
            shareMode |= FileShareWrite;
        }
        if (share.HasFlag(FileShare.Delete))
        {
            shareMode |= FileShareDelete;
        }

        var disposition = mode switch
        {
            FileMode.CreateNew => NtFileCreate,
            FileMode.Open => NtFileOpen,
            FileMode.OpenOrCreate => NtFileOpenIf,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), "Capability catalog native opens support only create-new, open, and open-or-create modes.")
        };
        var options = (allowDirectory ? NtFileDirectory : NtFileNonDirectory) | NtFileSynchronousIoNonAlert | NtFileOpenReparsePoint | (writeThrough ? NtFileWriteThrough : 0);
        // Metadata validation below requires FILE_READ_ATTRIBUTES even when the
        // caller is opening a write-only stream. GENERIC_WRITE does not grant
        // that right on Windows.
        var handle = OpenWindowsRelative(parent, name, desiredAccess | FileReadAttributes | SynchronizeAccess, shareMode, disposition, options, returnNullWhenMissing, returnNullWhenContended);
        if (handle is null)
        {
            return null;
        }
        ValidateWindowsHandle(handle, name, requireDirectory: allowDirectory);
        return handle;
    }

    private static SafeFileHandle? OpenWindowsRelative(SafeFileHandle parent, string name, uint desiredAccess, uint shareMode, uint disposition, uint options, bool returnNullWhenMissing, bool returnNullWhenContended)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new IOException("Capability catalog relative opens accept exactly one canonical child name.");
        }

        var nameBytes = checked(name.Length * sizeof(char));
        if (nameBytes > ushort.MaxValue)
        {
            throw new PathTooLongException("Capability catalog relative child name exceeds the native bound.");
        }
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = IntPtr.Zero;
        try
        {
            var unicode = new CapabilityCatalogWindowsUnicodeString { Length = (ushort)nameBytes, MaximumLength = (ushort)(nameBytes + sizeof(char)), Buffer = nameBuffer };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<CapabilityCatalogWindowsUnicodeString>());
            Marshal.StructureToPtr(unicode, unicodeBuffer, fDeleteOld: false);
            var attributes = new CapabilityCatalogWindowsObjectAttributes { Length = Marshal.SizeOf<CapabilityCatalogWindowsObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodeBuffer, Attributes = ObjectCaseInsensitive };
            var status = NtCreateFile(out var rawHandle, desiredAccess, ref attributes, out _, IntPtr.Zero, FileAttributeNormal, shareMode, disposition, options, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status >= 0)
            {
                return new SafeFileHandle(rawHandle, ownsHandle: true);
            }

            var error = unchecked((int)RtlNtStatusToDosError(status));
            if ((returnNullWhenMissing && error is ErrorFileNotFound or ErrorPathNotFound) || (returnNullWhenContended && error is ErrorSharingViolation or ErrorLockViolation))
            {
                return null;
            }
            throw NativeIOException($"Capability catalog child `{name}` could not be opened relative to retained directory authority", error);
        }
        finally
        {
            if (unicodeBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeBuffer);
            }
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static SafeFileHandle? OpenUnixDirectory(SafeFileHandle? parent, string? name, bool create, out bool created)
    {
        created = false;
        var flags = UnixOpenReadOnly | UnixOpenDirectory | UnixOpenNoFollow | UnixOpenCloseOnExec;
        if (parent is null)
        {
            var root = open("/", flags, 0);
            return root >= 0 ? new SafeFileHandle(new IntPtr(root), ownsHandle: true) : throw NativeIOException("The filesystem root could not be opened safely", Marshal.GetLastPInvokeError());
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var descriptor = openat(parent, name, flags, 0);
        if (descriptor < 0 && create && Marshal.GetLastPInvokeError() == 2)
        {
            if (mkdirat(parent, name, 0x1C0) == 0)
            {
                created = true;
            }
            else if (Marshal.GetLastPInvokeError() != 17)
            {
                throw NativeIOException($"Capability catalog directory `{name}` could not be created safely", Marshal.GetLastPInvokeError());
            }
            descriptor = openat(parent, name, flags, 0);
        }

        if (descriptor >= 0)
        {
            return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (!create && error == 2)
        {
            return null;
        }

        throw NativeIOException($"Capability catalog directory `{name}` could not be opened without following links", error);
    }

    private static SafeFileHandle? OpenUnixFile(SafeFileHandle parent, string name, FileMode mode, FileAccess access)
    {
        var flags = access switch
        {
            FileAccess.Read => UnixOpenReadOnly,
            FileAccess.Write => UnixOpenWriteOnly,
            FileAccess.ReadWrite => UnixOpenReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        flags |= UnixOpenNoFollow | UnixOpenCloseOnExec | UnixOpenNonBlocking;
        flags |= mode switch
        {
            FileMode.CreateNew => UnixOpenCreate | UnixOpenExclusive,
            FileMode.Open => 0,
            FileMode.OpenOrCreate => UnixOpenCreate,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var descriptor = openat(parent, name, flags, PermissionUserReadWrite);
        if (descriptor >= 0)
        {
            var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            try
            {
                if (mode is FileMode.CreateNew or FileMode.OpenOrCreate)
                {
                    SetUserOnlyPermissions(handle);
                }
                RequireUnixRegularFile(handle, name);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var error = Marshal.GetLastPInvokeError();
        if (mode == FileMode.Open && error == 2)
        {
            return null;
        }

        throw NativeIOException($"Capability catalog file `{name}` could not be opened without following links", error);
    }

    private static void RequireUnixRegularFile(SafeFileHandle handle, string name)
    {
        ushort mode;
        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, string.Empty, AtEmptyPath, StatxMode, out var information) != 0)
            {
                throw NativeIOException($"Capability catalog file metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            if ((information.Mask & StatxMode) == 0)
            {
                throw new IOException($"Capability catalog file metadata for `{name}` omitted its filesystem entry type.");
            }
            mode = information.Mode;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (fstat(handle, out CapabilityCatalogMacStat information) != 0)
            {
                throw NativeIOException($"Capability catalog file metadata for `{name}` could not be read", Marshal.GetLastPInvokeError());
            }
            mode = information.Mode;
        }
        else
        {
            throw new PlatformNotSupportedException("Capability catalog regular-file validation supports Windows, Linux, and macOS.");
        }

        if ((mode & UnixFileTypeMask) != UnixRegularFile)
        {
            throw new IOException($"Capability catalog file `{name}` is not a regular file.");
        }
    }

    private static void ValidateWindowsHandle(SafeFileHandle handle, string path, bool requireDirectory)
    {
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo, out CapabilityCatalogFileAttributeTagInfo information, (uint)Marshal.SizeOf<CapabilityCatalogFileAttributeTagInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeIOException($"Capability catalog filesystem metadata for `{path}` could not be read", error);
        }

        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0 || requireDirectory != isDirectory)
        {
            handle.Dispose();
            throw new IOException($"Capability catalog persistence refuses reparse points or mismatched filesystem entry types: `{path}`.");
        }
    }

    private static void RequireWindowsTopologyQuery(bool succeeded, string failureMessage)
    {
        if (!succeeded)
        {
            throw NativeIOException(failureMessage, Marshal.GetLastPInvokeError());
        }
    }

    internal static IOException NativeIOException(string message, int error)
    {
        return new IOException($"{message}: {new Win32Exception(error).Message}", unchecked((int)(0x80070000U | (uint)error)));
    }

    private static int UnixOpenReadOnly => 0;
    private static int UnixOpenWriteOnly => 1;
    private static int UnixOpenReadWrite => 2;
    private static int UnixOpenCreate => OperatingSystem.IsMacOS() ? 0x200 : 0x40;
    private static int UnixOpenExclusive => OperatingSystem.IsMacOS() ? 0x800 : 0x80;
    private static int UnixOpenNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int UnixOpenDirectory => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
    private static int UnixOpenCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int UnixOpenNonBlocking => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    private enum FileInfoByHandleClass
    {
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
        FileAttributeTagInfo = 9,
        FileFullDirectoryInfo = 14,
        FileFullDirectoryRestartInfo = 15,
        FileIdInfo = 18
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, byte[] fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out CapabilityCatalogFileAttributeTagInfo fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, out CapabilityCatalogFileIdInfo fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass fileInformationClass, [Out] byte[] fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out CapabilityCatalogWindowsFileInformation fileInformation);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref CapabilityCatalogWindowsObjectAttributes objectAttributes, out CapabilityCatalogWindowsIoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out CapabilityCatalogWindowsIoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int openat(SafeFileHandle directory, string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkdirat(SafeFileHandle directory, string path, int mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat(SafeFileHandle oldDirectory, string oldPath, SafeFileHandle newDirectory, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlinkat(SafeFileHandle directory, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle file, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle file);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle file, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr fdopendir(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr readdir(IntPtr directory);

    [DllImport("libc", SetLastError = true)]
    private static extern int closedir(IntPtr directory);

    [DllImport("libc", SetLastError = true)]
    private static extern int fgetattrlist(SafeFileHandle file, ref CapabilityCatalogMacAttributeList attributeList, out CapabilityCatalogMacVolumeCapabilitiesBuffer attributeBuffer, nuint attributeBufferSize, uint options);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out CapabilityCatalogLinuxStatx information);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(SafeFileHandle file, out CapabilityCatalogMacStat information);
}
