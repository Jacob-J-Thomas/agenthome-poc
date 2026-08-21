using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.WorkspaceActions.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Leases fixed, crash-safe cleanup scan cursors without retaining target or evidence content.</summary>
internal sealed class WorkspaceActionCleanupCursorStore
{
    private const int ChecksumSize = 32;
    internal const int CursorFileSize = 4096;
    private const int LockMaximumAttempts = 200;
    internal const int SlotSize = sizeof(ulong) + sizeof(ulong) + ChecksumSize;
    private const string ArtifactsFileName = "artifacts.cursor";
    private const string ArtifactsLockFileName = ".artifacts-cleanup.lock";
    private const string PreparationsFileName = "preparations.cursor";
    private const string PreparationsLockFileName = ".preparations-cleanup.lock";
    private static readonly byte[] _artifactsDomain = Encoding.UTF8.GetBytes("workspace-action-cleanup-cursor-v1:artifacts");
    private static readonly string[] _cleanupSegments = [".agent", "loops", "execution", "workspace-actions", "cleanup-progress"];
    private static readonly TimeSpan _lockRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly byte[] _preparationsDomain = Encoding.UTF8.GetBytes("workspace-action-cleanup-cursor-v1:preparations");
    private readonly string _workspaceRoot;

    public WorkspaceActionCleanupCursorStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _workspaceRoot = paths.RootPath;
    }

    public Task<WorkspaceActionCleanupCursorLease> AcquirePreparationsAsync(int amount, CancellationToken cancellationToken = default)
        => AcquireAsync(PreparationsFileName, PreparationsLockFileName, _preparationsDomain, amount, cancellationToken);

    public Task<WorkspaceActionCleanupCursorLease> AcquireArtifactsAsync(int amount, CancellationToken cancellationToken = default)
        => AcquireAsync(ArtifactsFileName, ArtifactsLockFileName, _artifactsDomain, amount, cancellationToken);

    private async Task<WorkspaceActionCleanupCursorLease> AcquireAsync(
        string fileName,
        string lockFileName,
        byte[] checksumDomain,
        int amount,
        CancellationToken cancellationToken)
    {
        if (amount is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Workspace action cleanup cursor advancement is bounded to 1 through 64 entries.");
        }

        for (var attempt = 1; attempt <= LockMaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle? directory = null;
            FileStream? ownership = null;
            SafeFileHandle? firstCursor = null;
            SafeFileHandle? secondCursor = null;
            FileStream? firstCursorOwnership = null;
            FileStream? secondCursorOwnership = null;
            try
            {
                directory = OpenCleanupDirectory();
                var directoryIdentity = WorkspaceActionNativeFileSystem.GetIdentity(directory);
                SafeFileHandle? lockHandle = WorkspaceActionNativeFileSystem.OpenRelativeFileForUpdate(
                    directory,
                    lockFileName,
                    allowMissing: false,
                    create: true,
                    shareForLocking: true)!;
                try
                {
                    var lockIdentity = WorkspaceActionNativeFileSystem.GetIdentity(lockHandle);
                    RequireSameMount(directoryIdentity, lockIdentity);
                    WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(lockHandle);
                    if (RandomAccess.GetLength(lockHandle) != 0)
                    {
                        throw new FormatException("Workspace action cleanup ownership must remain value-free.");
                    }
                    ownership = new FileStream(lockHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
                    lockHandle = null;
                    if (!CustomLoopCrossProcessFileLock.TryAcquire(ownership))
                    {
                        ownership.Dispose();
                        ownership = null;
                        directory.Dispose();
                        directory = null;
                        if (attempt < LockMaximumAttempts)
                        {
                            await Task.Delay(_lockRetryDelay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        throw new InvalidOperationException("Workspace action cleanup progress remained owned by another process after bounded retries.");
                    }

                    var firstCursorName = $"{fileName}.0";
                    var secondCursorName = $"{fileName}.1";
                    firstCursor = OpenOrInitializeCursor(
                        directory,
                        directoryIdentity,
                        firstCursorName,
                        checksumDomain,
                        cancellationToken);
                    secondCursor = OpenOrInitializeCursor(
                        directory,
                        directoryIdentity,
                        secondCursorName,
                        checksumDomain,
                        cancellationToken);
                    var firstCursorIdentity = WorkspaceActionNativeFileSystem.GetIdentity(firstCursor);
                    var secondCursorIdentity = WorkspaceActionNativeFileSystem.GetIdentity(secondCursor);
                    RequireSameMount(directoryIdentity, firstCursorIdentity);
                    RequireSameMount(directoryIdentity, secondCursorIdentity);
                    if (firstCursorIdentity.SameEntry(secondCursorIdentity)
                        || firstCursorIdentity.SameEntry(lockIdentity)
                        || secondCursorIdentity.SameEntry(lockIdentity))
                    {
                        throw new IOException("Workspace action cleanup ownership and cursor roles must retain distinct physical files.");
                    }
                    firstCursorOwnership = new FileStream(firstCursor, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
                    firstCursor = null;
                    secondCursorOwnership = new FileStream(secondCursor, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
                    secondCursor = null;
                    if (!CustomLoopCrossProcessFileLock.TryAcquire(firstCursorOwnership)
                        || !CustomLoopCrossProcessFileLock.TryAcquire(secondCursorOwnership))
                    {
                        secondCursorOwnership.Dispose();
                        secondCursorOwnership = null;
                        firstCursorOwnership.Dispose();
                        firstCursorOwnership = null;
                        ownership.Dispose();
                        ownership = null;
                        directory.Dispose();
                        directory = null;
                        if (attempt < LockMaximumAttempts)
                        {
                            await Task.Delay(_lockRetryDelay, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        throw new InvalidOperationException("Workspace action cleanup cursor remained owned by another process after bounded retries.");
                    }

                    var first = ReadCursor(firstCursorOwnership.SafeFileHandle, checksumDomain, cancellationToken);
                    var second = ReadCursor(secondCursorOwnership.SafeFileHandle, checksumDomain, cancellationToken);
                    if (!first.IsValid && !second.IsValid)
                    {
                        throw new FormatException("Workspace action cleanup progress contains no authenticated cursor slot.");
                    }
                    if (first.IsValid && second.IsValid && first.Sequence == second.Sequence && first.Cursor != second.Cursor)
                    {
                        throw new FormatException("Workspace action cleanup progress contains conflicting cursor slots.");
                    }

                    var current = SelectCurrent(first, second);
                    var lease = new WorkspaceActionCleanupCursorLease(
                        directory,
                        ownership,
                        firstCursorOwnership,
                        secondCursorOwnership,
                        checksumDomain,
                        firstCursorName,
                        secondCursorName,
                        lockFileName,
                        amount,
                        current.Sequence,
                        current.Cursor,
                        lockIdentity,
                        firstCursorIdentity,
                        secondCursorIdentity,
                        _workspaceRoot,
                        directoryIdentity);
                    directory = null;
                    ownership = null;
                    firstCursorOwnership = null;
                    secondCursorOwnership = null;
                    return lease;
                }
                finally
                {
                    lockHandle?.Dispose();
                }
            }
            finally
            {
                secondCursor?.Dispose();
                firstCursor?.Dispose();
                secondCursorOwnership?.Dispose();
                firstCursorOwnership?.Dispose();
                ownership?.Dispose();
                directory?.Dispose();
            }
        }

        throw new UnreachableException();
    }

    private SafeFileHandle OpenCleanupDirectory()
        => OpenCleanupDirectory(_workspaceRoot, create: true);

    internal static SafeFileHandle OpenExistingCleanupDirectory(string workspaceRoot)
        => OpenCleanupDirectory(workspaceRoot, create: false);

    private static SafeFileHandle OpenCleanupDirectory(string workspaceRoot, bool create)
    {
        var current = WorkspaceActionNativeFileSystem.OpenAbsoluteDirectory(workspaceRoot);
        var rootIdentity = WorkspaceActionNativeFileSystem.GetIdentity(current);
        try
        {
            for (var index = 0; index < _cleanupSegments.Length; index++)
            {
                var next = create
                    ? WorkspaceActionNativeFileSystem.OpenOrCreateRelativeDirectory(
                        current,
                        _cleanupSegments[index],
                        privateSecurityAccess: index == _cleanupSegments.Length - 1)
                    : WorkspaceActionNativeFileSystem.OpenRelativeDirectory(current, _cleanupSegments[index]);
                WorkspaceActionNativeFileSystem.RequireExactOpenedName(next, _cleanupSegments[index]);
                if (!WorkspaceActionNativeFileSystem.GetIdentity(next).SameMount(rootIdentity))
                {
                    next.Dispose();
                    throw new IOException("Workspace action cleanup storage refused a mount or device crossing.");
                }
                current.Dispose();
                current = next;
            }
            if (create)
            {
                WorkspaceActionNativeFileSystem.RequirePrivateDirectoryPermissions(current);
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenOrInitializeCursor(
        SafeFileHandle directory,
        WorkspaceActionNativeFileStamp directoryIdentity,
        string fileName,
        byte[] checksumDomain,
        CancellationToken cancellationToken)
    {
        var initializerName = $".{fileName}.initializing";
        var cursor = WorkspaceActionNativeFileSystem.OpenRelativeFileForUpdate(
            directory,
            fileName,
            allowMissing: true,
            create: false,
            shareForLocking: true);
        var initializer = WorkspaceActionNativeFileSystem.OpenRelativeFileForUpdate(
            directory,
            initializerName,
            allowMissing: true,
            create: false);
        try
        {
            if (cursor is not null)
            {
                RequireSameMount(directoryIdentity, WorkspaceActionNativeFileSystem.GetIdentity(cursor));
            }
            if (initializer is not null)
            {
                RequireSameMount(directoryIdentity, WorkspaceActionNativeFileSystem.GetIdentity(initializer));
            }
            if (cursor is not null)
            {
                if (initializer is not null)
                {
                    DeleteExact(directory, initializerName, initializer);
                    initializer.Dispose();
                    initializer = null;
                }
                WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(cursor);
                var retained = cursor;
                cursor = null;
                return retained;
            }

            if (initializer is not null
                && !IsValidInitializer(initializer, checksumDomain, cancellationToken))
            {
                DeleteExact(directory, initializerName, initializer);
                initializer.Dispose();
                initializer = null;
            }
            if (initializer is null)
            {
                initializer = WorkspaceActionNativeFileSystem.CreateRelativeFile(
                    directory,
                    initializerName,
                    privateSecurityAccess: true);
                RequireSameMount(directoryIdentity, WorkspaceActionNativeFileSystem.GetIdentity(initializer));
                WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(initializer);
                var content = new byte[CursorFileSize];
                WriteSlot(content.AsSpan(0, SlotSize), checksumDomain, 1, 0);
                WriteCursor(initializer, content, cancellationToken);
            }

            WorkspaceActionNativeFileSystem.RenameRelative(
                initializer,
                directory,
                initializerName,
                directory,
                fileName,
                overwrite: false);
            WorkspaceActionNativeFileSystem.FlushDirectory(directory);
            var published = initializer;
            initializer = null;
            return published;
        }
        finally
        {
            cursor?.Dispose();
            initializer?.Dispose();
        }
    }

    private static bool IsValidInitializer(
        SafeFileHandle initializer,
        byte[] checksumDomain,
        CancellationToken cancellationToken)
    {
        WorkspaceActionNativeFileSystem.RequirePrivateFilePermissions(initializer);
        if (RandomAccess.GetLength(initializer) != CursorFileSize)
        {
            return false;
        }
        var slot = ReadCursor(initializer, checksumDomain, cancellationToken);
        return slot is { IsValid: true, Sequence: 1, Cursor: 0 };
    }

    private static CursorSlot ReadCursor(
        SafeFileHandle cursor,
        byte[] checksumDomain,
        CancellationToken cancellationToken)
    {
        WorkspaceActionNativeFileSystem.RequireRegularFile(cursor, "workspace action cleanup cursor");
        if (RandomAccess.GetLength(cursor) != CursorFileSize)
        {
            return default;
        }
        var content = new byte[CursorFileSize];
        var offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(cursor, content.AsSpan(offset), offset);
            if (read == 0)
            {
                return default;
            }
            offset += read;
        }
        WorkspaceActionNativeFileSystem.RequireRegularFile(cursor, "workspace action cleanup cursor");
        if (content.AsSpan(SlotSize).IndexOfAnyExcept((byte)0) >= 0)
        {
            return default;
        }
        return ReadSlot(content.AsSpan(0, SlotSize), checksumDomain);
    }

    private static void WriteCursor(SafeFileHandle cursor, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceActionNativeFileSystem.RequireRegularFile(cursor, "workspace action cleanup cursor initializer");
        RandomAccess.SetLength(cursor, content.Length);
        RandomAccess.Write(cursor, content, 0);
        WorkspaceActionNativeFileSystem.FlushFile(cursor);
        WorkspaceActionNativeFileSystem.RequireRegularFile(cursor, "workspace action cleanup cursor initializer");
    }

    private static void DeleteExact(SafeFileHandle directory, string fileName, SafeFileHandle file)
    {
        var identity = WorkspaceActionNativeFileSystem.GetIdentity(file);
        WorkspaceActionNativeFileSystem.DeleteExact(directory, fileName, file, identity);
        WorkspaceActionNativeFileSystem.FlushDirectory(directory);
    }

    private static void RequireSameMount(
        WorkspaceActionNativeFileStamp directory,
        WorkspaceActionNativeFileStamp file)
    {
        if (!file.SameMount(directory))
        {
            throw new IOException("Workspace action cleanup storage refused a mounted file outside its private directory.");
        }
    }

    private static CursorSlot ReadSlot(ReadOnlySpan<byte> slot, byte[] checksumDomain)
    {
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(slot);
        var cursor = BinaryPrimitives.ReadUInt64LittleEndian(slot[sizeof(ulong)..]);
        if (sequence == 0)
        {
            return default;
        }
        Span<byte> expected = stackalloc byte[ChecksumSize];
        ComputeChecksum(checksumDomain, sequence, cursor, expected);
        return CryptographicOperations.FixedTimeEquals(expected, slot[(sizeof(ulong) * 2)..])
            ? new CursorSlot(true, sequence, cursor)
            : default;
    }

    internal static void WriteSlot(Span<byte> slot, byte[] checksumDomain, ulong sequence, ulong cursor)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(slot, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[sizeof(ulong)..], cursor);
        ComputeChecksum(checksumDomain, sequence, cursor, slot[(sizeof(ulong) * 2)..]);
    }

    private static void ComputeChecksum(byte[] checksumDomain, ulong sequence, ulong cursor, Span<byte> destination)
    {
        var input = new byte[checksumDomain.Length + (sizeof(ulong) * 2)];
        checksumDomain.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(checksumDomain.Length), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(checksumDomain.Length + sizeof(ulong)), cursor);
        SHA256.HashData(input, destination);
    }

    private static CursorSlot SelectCurrent(CursorSlot first, CursorSlot second)
    {
        if (!first.IsValid)
        {
            return second;
        }
        if (!second.IsValid)
        {
            return first;
        }
        return first.Sequence >= second.Sequence ? first : second;
    }

    private readonly record struct CursorSlot(bool IsValid, ulong Sequence, ulong Cursor);
}
