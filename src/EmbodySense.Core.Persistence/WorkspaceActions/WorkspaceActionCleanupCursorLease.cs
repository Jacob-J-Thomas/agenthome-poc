using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Persistence.WorkspaceActions.Models;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Retains exclusive cleanup-window ownership and advances it only after examination completes.</summary>
internal sealed class WorkspaceActionCleanupCursorLease : IAsyncDisposable
{
    private readonly byte[] _checksumDomain;
    private readonly FileStream _firstCursorOwnership;
    private readonly WorkspaceActionNativeFileStamp _firstCursorIdentity;
    private readonly string _firstCursorName;
    private readonly SafeFileHandle _directory;
    private readonly WorkspaceActionNativeFileStamp _directoryIdentity;
    private readonly int _increment;
    private readonly string _lockFileName;
    private readonly FileStream _ownership;
    private readonly WorkspaceActionNativeFileStamp _ownershipIdentity;
    private readonly ulong _sequence;
    private readonly FileStream _secondCursorOwnership;
    private readonly WorkspaceActionNativeFileStamp _secondCursorIdentity;
    private readonly string _secondCursorName;
    private readonly string _workspaceRoot;
    private bool _completed;
    private bool _disposed;

    public WorkspaceActionCleanupCursorLease(
        SafeFileHandle directory,
        FileStream ownership,
        FileStream firstCursorOwnership,
        FileStream secondCursorOwnership,
        byte[] checksumDomain,
        string firstCursorName,
        string secondCursorName,
        string lockFileName,
        int increment,
        ulong sequence,
        ulong value,
        WorkspaceActionNativeFileStamp ownershipIdentity,
        WorkspaceActionNativeFileStamp firstCursorIdentity,
        WorkspaceActionNativeFileStamp secondCursorIdentity,
        string workspaceRoot,
        WorkspaceActionNativeFileStamp directoryIdentity)
    {
        _directory = directory;
        _ownership = ownership;
        _firstCursorOwnership = firstCursorOwnership;
        _secondCursorOwnership = secondCursorOwnership;
        _checksumDomain = checksumDomain;
        _firstCursorName = firstCursorName;
        _secondCursorName = secondCursorName;
        _lockFileName = lockFileName;
        _increment = increment;
        _sequence = sequence;
        _ownershipIdentity = ownershipIdentity;
        _firstCursorIdentity = firstCursorIdentity;
        _secondCursorIdentity = secondCursorIdentity;
        _workspaceRoot = workspaceRoot;
        _directoryIdentity = directoryIdentity;
        Value = value;
    }

    public ulong Value { get; }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return Task.CompletedTask;
        }
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNamedBindings();
        var nextSequence = checked(_sequence + 1);
        var cursor = nextSequence % 2 == 1
            ? _firstCursorOwnership.SafeFileHandle
            : _secondCursorOwnership.SafeFileHandle;
        WorkspaceActionNativeFileSystem.RequireRegularFile(cursor, "workspace action cleanup cursor");
        if (RandomAccess.GetLength(cursor) != WorkspaceActionCleanupCursorStore.CursorFileSize)
        {
            throw new FormatException("Workspace action cleanup progress changed size while its scan was leased.");
        }
        var nextCursor = unchecked(Value + (ulong)_increment);
        var next = new byte[WorkspaceActionCleanupCursorStore.CursorFileSize];
        WorkspaceActionCleanupCursorStore.WriteSlot(
            next.AsSpan(0, WorkspaceActionCleanupCursorStore.SlotSize),
            _checksumDomain,
            nextSequence,
            nextCursor);
        RandomAccess.Write(cursor, next, 0);
        WorkspaceActionNativeFileSystem.FlushFile(cursor);
        WorkspaceActionNativeFileSystem.RequireRegularFile(cursor, "workspace action cleanup cursor");
        WorkspaceActionNativeFileSystem.FlushDirectory(_directory);
        ValidateNamedBindings();
        _completed = true;
        return Task.CompletedTask;
    }

    private void ValidateNamedBindings()
    {
        using var currentDirectory = WorkspaceActionCleanupCursorStore.OpenExistingCleanupDirectory(_workspaceRoot);
        if (!WorkspaceActionNativeFileSystem.GetIdentity(currentDirectory).SameEntry(_directoryIdentity))
        {
            throw new IOException("Workspace action cleanup directory lineage was substituted while its scan was leased.");
        }
        ValidateNamedBinding(_lockFileName, _ownershipIdentity);
        ValidateNamedBinding(_firstCursorName, _firstCursorIdentity);
        ValidateNamedBinding(_secondCursorName, _secondCursorIdentity);
    }

    private void ValidateNamedBinding(string name, WorkspaceActionNativeFileStamp expected)
    {
        using var named = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            _directory,
            name,
            allowMissing: false,
            write: false)!;
        WorkspaceActionNativeFileSystem.RequireExactOpenedName(named, name);
        if (!WorkspaceActionNativeFileSystem.GetIdentity(named).SameEntry(expected))
        {
            throw new IOException("Workspace action cleanup ownership was substituted while its scan was leased.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _secondCursorOwnership.Dispose();
            _firstCursorOwnership.Dispose();
            _ownership.Dispose();
            _directory.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
