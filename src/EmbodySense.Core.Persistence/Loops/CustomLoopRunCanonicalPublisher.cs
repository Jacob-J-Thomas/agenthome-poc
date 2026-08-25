using System.Security.Cryptography;
using EmbodySense.Core.Application.Loops.Diagnostics;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Persistence.Loops.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Publishes one canonical run artifact through a retained parent directory and proves the exact target afterwards.</summary>
internal sealed class CustomLoopRunCanonicalPublisher
{
    private readonly Func<CustomLoopRunPublicationBoundary, CancellationToken, ValueTask>? _boundaryObserver;

    public CustomLoopRunCanonicalPublisher(Func<CustomLoopRunPublicationBoundary, CancellationToken, ValueTask>? boundaryObserver = null)
    {
        _boundaryObserver = boundaryObserver;
    }

    public async Task<CustomLoopRunCanonicalPublicationResult> PublishAsync(
        string directory,
        string destinationName,
        byte[] content,
        bool overwrite,
        TimeSpan contentionTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);
        ArgumentNullException.ThrowIfNull(content);

        var stagingName = $".{destinationName}.{Guid.NewGuid():N}.tmp";
        SafeFileHandle? parent = null;
        SafeFileHandle? staged = null;
        var renamed = false;
        try
        {
            parent = CustomLoopRunNativeFileSystem.OpenParentDirectory(directory);
            var parentIdentity = CustomLoopRunNativeFileSystem.GetDirectoryIdentity(parent);
            staged = CustomLoopRunNativeFileSystem.CreateStagingFile(parent, stagingName);
            await RandomAccess.WriteAsync(staged, content, 0, cancellationToken).ConfigureAwait(false);
            CustomLoopRunNativeFileSystem.FlushStagingFile(staged);
            await ObserveAsync(CustomLoopRunPublicationBoundary.StagedFileFlushed, cancellationToken).ConfigureAwait(false);
            var stagedIdentity = CustomLoopRunNativeFileSystem.GetRegularFileIdentity(staged);
            await MoveWithRetryAsync(staged, parent, stagingName, destinationName, overwrite, contentionTimeout, retryDelay, cancellationToken).ConfigureAwait(false);
            renamed = true;

            try
            {
                staged.Dispose();
                staged = null;
                await ObserveAsync(CustomLoopRunPublicationBoundary.CanonicalRenamed, CancellationToken.None).ConfigureAwait(false);
                CustomLoopRunNativeFileSystem.FlushAfterRename(parent, destinationName);
                await ObserveAsync(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed, CancellationToken.None).ConfigureAwait(false);
                await ProveTargetAsync(parent, destinationName, stagedIdentity, content).ConfigureAwait(false);
                await ObserveAsync(CustomLoopRunPublicationBoundary.TargetProven, CancellationToken.None).ConfigureAwait(false);
                CustomLoopRunNativeFileSystem.RevalidateCanonicalParentDirectory(directory, parentIdentity);
                return new CustomLoopRunCanonicalPublicationResult(true, null);
            }
            catch (Exception exception)
            {
                _ = await TryProveTargetAsync(parent, destinationName, stagedIdentity, content).ConfigureAwait(false);
                return new CustomLoopRunCanonicalPublicationResult(false, CreateDiagnostic(exception), exception);
            }
        }
        finally
        {
            try
            {
                if (!renamed && parent is not null && staged is not null)
                {
                    CustomLoopRunNativeFileSystem.DeleteUnpublishedStagingFile(parent, stagingName, staged);
                }
            }
            finally
            {
                staged?.Dispose();
                parent?.Dispose();
            }
        }
    }

    private async Task MoveWithRetryAsync(
        SafeFileHandle staged,
        SafeFileHandle parent,
        string stagingName,
        string destinationName,
        bool overwrite,
        TimeSpan contentionTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(contentionTimeout);
        Exception? lastTransient = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CustomLoopRunNativeFileSystem.RenameWithinParent(staged, parent, stagingName, destinationName, overwrite);
                return;
            }
            catch (Exception exception) when (CustomLoopRunNativeFileSystem.IsTransientWindowsContention(exception))
            {
                lastTransient = exception;
                try
                {
                    await Task.Delay(retryDelay, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw lastTransient;
                }
            }
        }
    }

    private static async Task ProveTargetAsync(SafeFileHandle parent, string destinationName, CustomLoopRunNativeIdentity expectedIdentity, byte[] expectedContent)
    {
        using var target = CustomLoopRunNativeFileSystem.OpenRegularFile(parent, destinationName);
        if (CustomLoopRunNativeFileSystem.GetRegularFileIdentity(target) != expectedIdentity)
        {
            throw new IOException("Canonical run target identity could not be proved after publication.");
        }

        await using var stream = new FileStream(target, FileAccess.Read, 64 * 1024, isAsync: false);
        if (stream.Length != expectedContent.LongLength)
        {
            throw new IOException("Canonical run target content could not be proved after publication.");
        }

        var observed = GC.AllocateUninitializedArray<byte>(expectedContent.Length);
        await stream.ReadExactlyAsync(observed).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(observed, expectedContent))
        {
            throw new IOException("Canonical run target content could not be proved after publication.");
        }
    }

    private static async Task<bool> TryProveTargetAsync(SafeFileHandle parent, string destinationName, CustomLoopRunNativeIdentity expectedIdentity, byte[] expectedContent)
    {
        try
        {
            await ProveTargetAsync(parent, destinationName, expectedIdentity, expectedContent).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask ObserveAsync(CustomLoopRunPublicationBoundary boundary, CancellationToken cancellationToken)
    {
        if (_boundaryObserver is not null)
        {
            await _boundaryObserver(boundary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CustomLoopRunPersistenceDiagnostic CreateDiagnostic(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is CustomLoopRunNativeIOException native)
            {
                return new CustomLoopRunPersistenceDiagnostic(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, native.ErrorKind, native.ErrorCode);
            }

            current = current.InnerException;
        }

        return new CustomLoopRunPersistenceDiagnostic(CustomLoopRunPersistenceDiagnosticStage.CanonicalDirectoryBarrier, CustomLoopRunPersistenceNativeErrorKind.None, null);
    }
}
