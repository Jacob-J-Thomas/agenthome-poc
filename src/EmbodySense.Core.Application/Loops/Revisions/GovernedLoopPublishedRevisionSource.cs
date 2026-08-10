using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Resolves caller-supplied exact publication pins under the shared reentrant workspace authority fence.</summary>
public sealed class GovernedLoopPublishedRevisionSource : IGovernedLoopPublishedRevisionSource
{
    private readonly IGovernedLoopRevisionLifecycleStore _store;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates an exact publication source over one atomic lifecycle store.</summary>
    /// <param name="store">The atomic immutable revision lifecycle store.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="store"/> or <paramref name="authorityTransaction"/> is <see langword="null"/>.</exception>
    public GovernedLoopPublishedRevisionSource(
        IGovernedLoopRevisionLifecycleStore store,
        ICapabilityAuthorityTransaction authorityTransaction)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopPublishedRevisionResolution> ResolveAsync(
        GovernedLoopRevisionPublicationPin? pin,
        CancellationToken cancellationToken = default)
    {
        GovernedLoopPublishedRevisionResolution? completedResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completedResult = await ResolveUnderFenceAsync(pin, transactionToken).ConfigureAwait(false);
                    return completedResult;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completedResult is null)
        {
            throw;
        }
        catch (Exception)
        {
            if (HasExactResolvedProof(completedResult))
            {
                return completedResult!;
            }

            if (completedResult is not null)
            {
                return Result(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, SafePin(pin));
            }

            return Result(GovernedLoopPublishedRevisionResolutionStatus.Unavailable, SafePin(pin));
        }
    }

    private async Task<GovernedLoopPublishedRevisionResolution> ResolveUnderFenceAsync(
        GovernedLoopRevisionPublicationPin? pin,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopRevisionContractValidator.Validate(pin).IsValid)
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.Invalid, null);
        }

        GovernedLoopRevisionGraphReadResult read;
        try
        {
            read = await _store.ReadGraphAsync(pin!.Revision.GraphId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.Unavailable, pin);
        }

        if (read is null || read.StoreGeneration < 0)
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, pin);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.NotFound && read.Snapshot is null)
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.NotFound, pin);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.Unavailable && read.Snapshot is null)
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.Unavailable, pin);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.Ambiguous)
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, pin);
        }

        if (read.Status != GovernedLoopRevisionStoreReadStatus.Ready
            || !GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                read.Snapshot,
                pin.Revision.GraphId,
                read.StoreGeneration,
                out var snapshot))
        {
            return Result(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, pin);
        }

        var artifact = GovernedLoopRevisionStoreSnapshotGuard.FindArtifact(snapshot!.Artifacts, pin.Revision);
        var hasPublicationProof = GovernedLoopRevisionStoreSnapshotGuard.HasPublicationProof(snapshot.Operations, pin);
        if (artifact is null && hasPublicationProof)
        {
            return Observed(GovernedLoopPublishedRevisionResolutionStatus.Ambiguous, pin, null, snapshot.Head);
        }

        if (artifact is null || !hasPublicationProof)
        {
            return Observed(GovernedLoopPublishedRevisionResolutionStatus.NotFound, pin, null, snapshot.Head);
        }

        if (!Equals(snapshot.Head.PublishedRevision, pin))
        {
            return Observed(GovernedLoopPublishedRevisionResolutionStatus.Stale, pin, artifact, snapshot.Head);
        }

        var status = snapshot.Head.Status switch
        {
            GovernedLoopRevisionLifecycleStatus.Published => GovernedLoopPublishedRevisionResolutionStatus.Active,
            GovernedLoopRevisionLifecycleStatus.Disabled => GovernedLoopPublishedRevisionResolutionStatus.Disabled,
            GovernedLoopRevisionLifecycleStatus.Archived => GovernedLoopPublishedRevisionResolutionStatus.Archived,
            _ => GovernedLoopPublishedRevisionResolutionStatus.Ambiguous,
        };
        return Observed(status, pin, artifact, snapshot.Head);
    }

    private static GovernedLoopPublishedRevisionResolution Result(
        GovernedLoopPublishedRevisionResolutionStatus status,
        GovernedLoopRevisionPublicationPin? pin)
        => new(
            status,
            pin,
            null,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            string.Empty);

    private static GovernedLoopRevisionPublicationPin? SafePin(GovernedLoopRevisionPublicationPin? pin)
        => GovernedLoopRevisionContractValidator.Validate(pin).IsValid ? pin : null;

    private static bool HasExactResolvedProof(GovernedLoopPublishedRevisionResolution? result)
        => result is
        {
            Status: GovernedLoopPublishedRevisionResolutionStatus.Active
                or GovernedLoopPublishedRevisionResolutionStatus.Disabled
                or GovernedLoopPublishedRevisionResolutionStatus.Archived
                or GovernedLoopPublishedRevisionResolutionStatus.Stale,
            RequestedPin: { } pin,
            Artifact: { } artifact,
            ObservedLifecycleVersion: > 0,
            ObservedLifecycleHeadOperationId.Length: > 0,
        }
            && GovernedLoopRevisionContractValidator.Validate(pin).IsValid
            && GovernedLoopRevisionContractValidator.Validate(artifact).IsValid
            && GovernedLoopRevisionStoreSnapshotGuard.SameRevision(pin.Revision, artifact.Revision);

    private static GovernedLoopPublishedRevisionResolution Observed(
        GovernedLoopPublishedRevisionResolutionStatus status,
        GovernedLoopRevisionPublicationPin pin,
        GovernedLoopRevisionArtifact? artifact,
        GovernedLoopRevisionLifecycleHead head)
        => new(
            status,
            pin,
            artifact,
            head.Status,
            head.LifecycleVersion,
            head.LastOperationId);
}
