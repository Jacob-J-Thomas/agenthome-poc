using System.Security.Cryptography;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.HumanInput.Policies.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

/// <summary>Publishes and retires exact policy-store files through a retained, revalidated no-follow parent.</summary>
/// <remarks>POSIX publications include a parent-directory durability barrier. Windows flushes the reopened target but has no portable directory barrier, so callers must retain a recoverable protocol rather than infer cross-rename ordering.</remarks>
internal sealed class HumanInputPolicyFileStoreCanonicalPublisher
{
    private readonly Func<HumanInputPolicyFileStorePublicationPart, HumanInputPolicyFileStorePhysicalPersistenceBoundary, CancellationToken, ValueTask>? _physicalBoundaryObserver;

    public HumanInputPolicyFileStoreCanonicalPublisher(Func<HumanInputPolicyFileStorePublicationPart, HumanInputPolicyFileStorePhysicalPersistenceBoundary, CancellationToken, ValueTask>? physicalBoundaryObserver)
    {
        _physicalBoundaryObserver = physicalBoundaryObserver;
    }

    public async Task PublishAsync(CapabilityCatalogPathSession session, string directory, string destinationName, byte[] content, bool overwrite, HumanInputPolicyFileStorePublicationPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);
        ArgumentNullException.ThrowIfNull(content);

        var stagingName = ".tmp-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        SafeFileHandle? parent = null;
        SafeFileHandle? staging = null;
        var renamed = false;
        try
        {
            parent = session.RequireBoundDirectory(directory);
            staging = CustomLoopRunNativeFileSystem.CreateStagingFile(parent, stagingName);
            await RandomAccess.WriteAsync(staging, content, 0, cancellationToken).ConfigureAwait(false);
            CustomLoopRunNativeFileSystem.FlushStagingFile(staging);
            await ObserveAsync(part, HumanInputPolicyFileStorePhysicalPersistenceBoundary.StagedFileFlushed, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var stagingIdentity = CustomLoopRunNativeFileSystem.GetRegularFileIdentity(staging);
            CustomLoopRunNativeFileSystem.RenameWithinParent(staging, parent, stagingName, destinationName, overwrite);
            renamed = true;
            staging.Dispose();
            staging = null;
            await ObserveAsync(part, HumanInputPolicyFileStorePhysicalPersistenceBoundary.CanonicalRenamed, CancellationToken.None).ConfigureAwait(false);
            CustomLoopRunNativeFileSystem.FlushAfterRename(parent, destinationName);
            await ObserveAsync(part, HumanInputPolicyFileStorePhysicalPersistenceBoundary.ParentDirectoryFlushed, CancellationToken.None).ConfigureAwait(false);
            await ProveTargetAsync(parent, destinationName, stagingIdentity, content).ConfigureAwait(false);
            await ObserveAsync(part, HumanInputPolicyFileStorePhysicalPersistenceBoundary.TargetProven, CancellationToken.None).ConfigureAwait(false);
            await ProveTargetAsync(parent, destinationName, stagingIdentity, content).ConfigureAwait(false);
            session.RevalidateBoundDirectory(directory);
        }
        finally
        {
            try
            {
                if (!renamed && parent is not null && staging is not null)
                {
                    CustomLoopRunNativeFileSystem.DeleteUnpublishedStagingFile(parent, stagingName, staging);
                    CustomLoopRunNativeFileSystem.FlushDirectory(parent);
                }
            }
            finally
            {
                staging?.Dispose();
            }
        }
    }

    public async Task RetireAsync(CapabilityCatalogPathSession session, string directory, string name, HumanInputPolicyFileStorePublicationPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = session.RequireBoundDirectory(directory);
        var target = CustomLoopRunNativeFileSystem.OpenRegularFile(parent, name);
        try
        {
            CustomLoopRunNativeFileSystem.DeleteUnpublishedStagingFile(parent, name, target);
        }
        finally
        {
            target.Dispose();
        }
        await ObserveAsync(part, HumanInputPolicyFileStorePhysicalPersistenceBoundary.Deleted, CancellationToken.None).ConfigureAwait(false);
        CustomLoopRunNativeFileSystem.FlushDirectory(parent);
        await ObserveAsync(part, HumanInputPolicyFileStorePhysicalPersistenceBoundary.Retired, CancellationToken.None).ConfigureAwait(false);
        session.RevalidateBoundDirectory(directory);
    }

    private async Task ProveTargetAsync(SafeFileHandle parent, string destinationName, CustomLoopRunNativeIdentity expectedIdentity, byte[] expectedContent)
    {
        using var target = CustomLoopRunNativeFileSystem.OpenRegularFile(parent, destinationName);
        if (CustomLoopRunNativeFileSystem.GetRegularFileIdentity(target) != expectedIdentity) throw new IOException("The Human Input policy publication target identity could not be proved after publication.");

        await using var stream = new FileStream(target, FileAccess.Read, 64 * 1024, isAsync: false);
        if (stream.Length != expectedContent.LongLength) throw new IOException("The Human Input policy publication target content could not be proved after publication.");
        var observed = GC.AllocateUninitializedArray<byte>(expectedContent.Length);
        await stream.ReadExactlyAsync(observed).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(observed, expectedContent)) throw new IOException("The Human Input policy publication target content could not be proved after publication.");
    }

    private async ValueTask ObserveAsync(HumanInputPolicyFileStorePublicationPart part, HumanInputPolicyFileStorePhysicalPersistenceBoundary boundary, CancellationToken cancellationToken)
    {
        if (_physicalBoundaryObserver is not null) await _physicalBoundaryObserver(part, boundary, cancellationToken).ConfigureAwait(false);
    }
}
