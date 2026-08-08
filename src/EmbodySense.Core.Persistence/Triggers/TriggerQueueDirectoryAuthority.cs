using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using EmbodySense.Core.Persistence.Triggers.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Retains exact native authority over one queue directory for a mutation lease.</summary>
internal sealed class TriggerQueueDirectoryAuthority : IDisposable
{
    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private const int UnixReadWrite = 2;
    private const int UnixOwnerReadWriteMode = 0x180;
    private readonly string _queueRoot;
    private readonly TriggerQueueFileIdentity _queueIdentity;
    private readonly IReadOnlyList<SafeFileHandle> _windowsDirectoryHandles;
    private SafeFileHandle? _unixDirectoryHandle;

    private TriggerQueueDirectoryAuthority(string queueRoot, TriggerQueueFileIdentity queueIdentity, IReadOnlyList<SafeFileHandle> windowsDirectoryHandles, SafeFileHandle? unixDirectoryHandle)
    {
        _queueRoot = queueRoot;
        _queueIdentity = queueIdentity;
        _windowsDirectoryHandles = windowsDirectoryHandles;
        _unixDirectoryHandle = unixDirectoryHandle;
    }

    /// <summary>Captures native no-replacement authority matching an exact governed directory chain.</summary>
    public static TriggerQueueDirectoryAuthority Capture(IReadOnlyList<TriggerQueueDirectorySnapshot> rootSnapshot)
    {
        ArgumentNullException.ThrowIfNull(rootSnapshot);
        if (rootSnapshot.Count == 0)
        {
            throw new ArgumentException("Trigger queue directory authority requires a non-empty root snapshot.", nameof(rootSnapshot));
        }

        return OperatingSystem.IsWindows() ? CaptureWindows(rootSnapshot) : CaptureUnix(rootSnapshot[^1]);
    }

    /// <summary>Creates a new direct-child file without following links under retained directory authority.</summary>
    public FileStream CreateNew(string fileName, FileAccess access, FileShare share, int bufferSize, FileOptions options)
    {
        ValidateFileName(fileName);
        if (OperatingSystem.IsWindows())
        {
            return CreateNewWindows(fileName, access, share, bufferSize, options);
        }

        var descriptor = OpenAtCreate(UnixDescriptor, fileName, AccessFlags(access) | CreateFlag | ExclusiveFlag | NoFollowFlag | CloseOnExecFlag, UnixOwnerReadWriteMode);
        if (descriptor < 0)
        {
            throw new IOException("Trigger queue direct-child create-new failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        SetOwnerOnlyMode(descriptor, "Trigger queue direct-child create-new");

        return new FileStream(new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true), access, bufferSize, isAsync: false);
    }

    /// <summary>Opens an existing direct-child regular file or creates it, without following links.</summary>
    public FileStream OpenOrCreate(string fileName)
    {
        ValidateFileName(fileName);
        if (OperatingSystem.IsWindows())
        {
            return OpenOrCreateWindows(fileName);
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var descriptor = OpenAt(UnixDescriptor, fileName, UnixReadOnly | NoFollowFlag | CloseOnExecFlag);
            if (descriptor >= 0)
            {
                return new FileStream(new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true), FileAccess.Read, 1, isAsync: false);
            }

            const int NoSuchFileOrDirectory = 2;
            if (Marshal.GetLastWin32Error() != NoSuchFileOrDirectory)
            {
                throw new IOException("Trigger queue mutation lock could not be opened through pinned directory authority.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            descriptor = OpenAtCreate(UnixDescriptor, fileName, UnixReadOnly | CreateFlag | ExclusiveFlag | NoFollowFlag | CloseOnExecFlag, UnixOwnerReadWriteMode);
            if (descriptor >= 0)
            {
                SetOwnerOnlyMode(descriptor, "Trigger queue mutation lock creation");
                return new FileStream(new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true), FileAccess.Read, 1, isAsync: false);
            }

            const int AlreadyExists = 17;
            if (Marshal.GetLastWin32Error() != AlreadyExists)
            {
                throw new IOException("Trigger queue mutation lock could not be created through pinned directory authority.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        throw new IOException("Trigger queue mutation lock path could not be opened after bounded create/open races.");
    }

    /// <summary>Opens an existing direct-child file without following links under retained directory authority.</summary>
    public FileStream OpenExisting(string fileName, FileAccess access, FileShare share, int bufferSize)
    {
        ValidateFileName(fileName);
        if (OperatingSystem.IsWindows())
        {
            return OpenExistingWindows(fileName, access, share, bufferSize);
        }

        var descriptor = OpenAt(UnixDescriptor, fileName, AccessFlags(access) | NoFollowFlag | CloseOnExecFlag | NonBlockingFlag);
        if (descriptor < 0)
        {
            throw new IOException("Trigger queue direct-child open failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new FileStream(new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true), access, bufferSize, isAsync: false);
    }

    /// <summary>Atomically renames one direct child to another without replacement under retained directory authority.</summary>
    public void MoveNoReplace(string sourceName, string destinationName)
    {
        ValidateFileName(sourceName);
        ValidateFileName(destinationName);
        if (OperatingSystem.IsWindows())
        {
            MoveNoReplaceWindows(sourceName, destinationName);
            return;
        }

        var result = OperatingSystem.IsMacOS()
            ? RenameAtExclusiveMac(UnixDescriptor, sourceName, UnixDescriptor, destinationName, 0x00000004 | 0x00000010)
            : RenameAtNoReplaceLinux(UnixDescriptor, sourceName, UnixDescriptor, destinationName, 0x00000001);
        if (result != 0)
        {
            throw new IOException("Trigger queue atomic no-replace rename failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        Flush();
    }

    /// <summary>Flushes the retained queue directory where the host supports directory durability.</summary>
    public void Flush()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (Fsync(UnixDescriptor) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue directory durability flush failed.");
        }
    }

    /// <summary>Validates that the canonical queue path still resolves to the retained authority.</summary>
    public void ValidateCurrentPath()
    {
        var current = TriggerQueueNativeFileInspector.InspectDirectoryPath(_queueRoot);
        if (current.Device != _queueIdentity.Device || current.File != _queueIdentity.File)
        {
            throw new InvalidOperationException("Trigger queue path no longer resolves to the retained mutation directory authority.");
        }
    }

    /// <summary>Releases all retained native directory authority.</summary>
    public void Dispose()
    {
        var unixHandle = Interlocked.Exchange(ref _unixDirectoryHandle, null);
        unixHandle?.Dispose();
        foreach (var handle in _windowsDirectoryHandles)
        {
            handle.Dispose();
        }
    }

    private int UnixDescriptor => _unixDirectoryHandle?.DangerousGetHandle().ToInt32() ?? throw new ObjectDisposedException(nameof(TriggerQueueDirectoryAuthority));

    [ExcludeFromCodeCoverage(Justification = "This Windows no-delete-sharing directory pin is covered by public trigger-queue tests on Windows and is unreachable in Unix coverage runs.")]
    private static TriggerQueueDirectoryAuthority CaptureWindows(IReadOnlyList<TriggerQueueDirectorySnapshot> rootSnapshot)
    {
        var handles = new List<SafeFileHandle>(rootSnapshot.Count);
        try
        {
            foreach (var snapshot in rootSnapshot)
            {
                const uint FileShareRead = 0x00000001;
                const uint FileShareWrite = 0x00000002;
                const uint OpenExisting = 3;
                const uint FileFlagBackupSemantics = 0x02000000;
                const uint FileFlagOpenReparsePoint = 0x00200000;
                var handle = CreateFile(snapshot.Path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue governed directory could not be pinned against replacement.");
                }

                handles.Add(handle);
                var identity = TriggerQueueNativeFileInspector.InspectDirectoryHandle(handle, snapshot.Path);
                if (identity.Device != snapshot.Identity.Device || identity.File != snapshot.Identity.File)
                {
                    throw new InvalidOperationException("Trigger queue governed directory handle did not match captured authority.");
                }
            }

            return new TriggerQueueDirectoryAuthority(rootSnapshot[^1].Path, rootSnapshot[^1].Identity, handles, null);
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static TriggerQueueDirectoryAuthority CaptureUnix(TriggerQueueDirectorySnapshot queueRoot)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Pinned trigger queue directory authority is unavailable on this platform.");
        }

        var descriptor = Open(queueRoot.Path, DirectoryFlag | NoFollowFlag | CloseOnExecFlag);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Trigger queue directory could not be pinned for mutation authority.");
        }

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            var identity = TriggerQueueNativeFileInspector.InspectDirectoryHandle(handle, queueRoot.Path);
            if (identity.Device != queueRoot.Identity.Device || identity.File != queueRoot.Identity.File)
            {
                throw new InvalidOperationException("Trigger queue directory handle did not match captured mutation authority.");
            }

            return new TriggerQueueDirectoryAuthority(queueRoot.Path, queueRoot.Identity, [], handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "This Windows no-follow lock open is covered by public trigger-queue tests on Windows and is unreachable in Unix coverage runs.")]
    private FileStream OpenOrCreateWindows(string fileName)
    {
        var path = Path.Combine(_queueRoot, fileName);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ValidateCurrentPath();
            const uint GenericRead = 0x80000000;
            const uint GenericWrite = 0x40000000;
            const uint FileShareRead = 0x00000001;
            const uint FileShareWrite = 0x00000002;
            const uint OpenAlways = 4;
            const uint FileFlagOpenReparsePoint = 0x00200000;
            const uint FileFlagWriteThrough = 0x80000000;
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenAlways, FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
            }

            handle.Dispose();
            if (attempt == 7)
            {
                throw new IOException("Trigger queue mutation lock path could not be opened after bounded create/open races.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        throw new IOException("Trigger queue mutation lock path could not be opened after bounded create/open races.");
    }

    [ExcludeFromCodeCoverage(Justification = "This Windows pinned-path create-new operation is covered by public trigger-queue tests on Windows and is unreachable in Unix coverage runs.")]
    private FileStream CreateNewWindows(string fileName, FileAccess access, FileShare share, int bufferSize, FileOptions options)
    {
        ValidateCurrentPath();
        return new FileStream(Path.Combine(_queueRoot, fileName), FileMode.CreateNew, access, share, bufferSize, options);
    }

    [ExcludeFromCodeCoverage(Justification = "This Windows no-follow direct-child open is covered by public trigger-queue tests on Windows and is unreachable in Unix coverage runs.")]
    private FileStream OpenExistingWindows(string fileName, FileAccess access, FileShare share, int bufferSize)
    {
        ValidateCurrentPath();
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint OpenExistingDisposition = 3;
        const uint FileFlagOpenReparsePoint = 0x00200000;
        var desiredAccess = access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        var handle = CreateFile(Path.Combine(_queueRoot, fileName), desiredAccess, (uint)share, IntPtr.Zero, OpenExistingDisposition, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Trigger queue direct-child open failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new FileStream(handle, access, bufferSize, isAsync: false);
    }

    [ExcludeFromCodeCoverage(Justification = "This Windows write-through no-replace rename is covered by public trigger-queue tests on Windows and is unreachable in Unix coverage runs.")]
    private void MoveNoReplaceWindows(string sourceName, string destinationName)
    {
        ValidateCurrentPath();
        const uint MoveFileWriteThrough = 0x00000008;
        if (!MoveFileEx(Path.Combine(_queueRoot, sourceName), Path.Combine(_queueRoot, destinationName), MoveFileWriteThrough))
        {
            throw new IOException("Trigger queue durable no-replace rename failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static int AccessFlags(FileAccess access)
    {
        return access switch
        {
            FileAccess.Read => UnixReadOnly,
            FileAccess.Write => UnixWriteOnly,
            FileAccess.ReadWrite => UnixReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
    }

    private static void SetOwnerOnlyMode(int descriptor, string operation)
    {
        if (Fchmod(descriptor, Convert.ToUInt32("600", 8)) != 0)
        {
            var error = Marshal.GetLastWin32Error();
            Close(descriptor);
            throw new IOException($"{operation} could not establish owner-only permissions.", new Win32Exception(error));
        }
    }

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) || fileName is "." or "..")
        {
            throw new InvalidOperationException("Trigger queue native authority accepts direct-child file names only.");
        }
    }

    private static int DirectoryFlag => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;

    private static int CloseOnExecFlag => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    private static int NoFollowFlag => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;

    private static int NonBlockingFlag => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    private static int CreateFlag => OperatingSystem.IsMacOS() ? 0x200 : 0x40;

    private static int ExclusiveFlag => OperatingSystem.IsMacOS() ? 0x800 : 0x80;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtCreate(int directoryDescriptor, string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int descriptor, uint mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtExclusiveMac(int sourceDirectory, string sourceName, int destinationDirectory, string destinationName, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAtNoReplaceLinux(int sourceDirectory, string sourceName, int destinationDirectory, string destinationName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
