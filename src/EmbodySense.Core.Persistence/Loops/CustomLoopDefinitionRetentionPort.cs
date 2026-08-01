using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Projects one class-specific receipt-retention port over a definition store that owns the shared authoring lineage.
/// </summary>
public sealed class CustomLoopDefinitionRetentionPort : ICustomLoopReceiptRetentionPort
{
    private readonly CustomLoopDefinitionStore _store;

    /// <summary>
    /// Initializes one definition-mutation or tombstone retention projection.
    /// </summary>
    /// <param name="store">The integrated definition store.</param>
    /// <param name="artifactClass">The authoring artifact class.</param>
    public CustomLoopDefinitionRetentionPort(CustomLoopDefinitionStore store, CustomLoopReceiptArtifactClass artifactClass)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (artifactClass is not CustomLoopReceiptArtifactClass.DefinitionMutationReceipt and not CustomLoopReceiptArtifactClass.DefinitionTombstone)
        {
            throw new ArgumentOutOfRangeException(nameof(artifactClass), artifactClass, "The definition store owns only authoring receipts and tombstones.");
        }

        ArtifactClass = artifactClass;
    }

    /// <inheritdoc />
    public CustomLoopReceiptArtifactClass ArtifactClass { get; }

    /// <inheritdoc />
    public Task<CustomLoopReceiptClassPosture> InspectAsync(CancellationToken cancellationToken = default) => _store.InspectReceiptRetentionAsync(ArtifactClass, cancellationToken);

    /// <inheritdoc />
    public Task<CustomLoopReceiptOperationLookupResult> LookupOperationAsync(string operationId, CancellationToken cancellationToken = default) => _store.LookupReceiptOperationAsync(ArtifactClass, operationId, cancellationToken);

    /// <inheritdoc />
    public Task<CustomLoopReceiptCleanupResult> CleanupAsync(CustomLoopReceiptCleanupCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ArtifactClass != ArtifactClass)
        {
            throw new ArgumentException("Cleanup command artifact class does not match this retention port.", nameof(command));
        }

        return _store.CleanupReceiptRetentionAsync(command, cancellationToken);
    }
}
