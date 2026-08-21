using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Posture;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Projects bounded queue posture directly from the canonical trigger queue store.</summary>
public sealed class TriggerQueueOperationalPostureAdapter : IGovernedLoopQueueOperationalPosturePort
{
    private readonly TriggerQueueStore _store;
    private readonly string _workspaceId;

    /// <summary>Creates an adapter bound to one trusted workspace and its canonical queue store.</summary>
    public TriggerQueueOperationalPostureAdapter(TriggerQueueStore store, string workspaceId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!CustomLoopArtifactIdentifier.IsValid(workspaceId, GovernedLoopOperationalPostureLimits.MaxWorkspaceIdCharacters))
        {
            throw new ArgumentException("Operational queue posture requires a bounded trusted workspace identity.", nameof(workspaceId));
        }
        _workspaceId = workspaceId;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopQueueEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!IsValid(request) || !GovernedLoopOperationalContract.IsUtc(observedAtUtc))
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
        }

        try
        {
            var snapshot = await _store.PeekSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!TriggerQueueSnapshotEvidenceContract.IsValid(snapshot)
                || snapshot.Entries.Any(item => !string.Equals(item.WorkspaceId, _workspaceId, StringComparison.Ordinal)))
            {
                return Result(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
            }
            var start = 0;
            if (request.AfterId is not null)
            {
                if (!TriggerQueueOperationalCursor.TryParse(request.AfterId, out var generation, out start, out var previousDeliveryId)
                    || generation != snapshot.Generation
                    || start >= snapshot.Entries.Count
                    || !snapshot.Entries[start - 1].DeliveryId.Equals(previousDeliveryId))
                {
                    return Result(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
                }
            }
            var ordered = snapshot.Entries.Skip(start).Take(request.MaximumCount + 1).ToArray();
            var hasMore = ordered.Length > request.MaximumCount;
            var page = Array.AsReadOnly(ordered.Take(request.MaximumCount).Select(Copy).ToArray());
            return new GovernedLoopQueueEvidenceReadResult(
                page.Count == 0 ? GovernedLoopOperationalEvidenceReadStatus.Empty : GovernedLoopOperationalEvidenceReadStatus.Found,
                snapshot.Generation,
                snapshot.QueuedEntries,
                snapshot.QueuedReservationBytes,
                snapshot.RetainedEntries,
                snapshot.RetainedReservationBytes,
                snapshot.PersistenceBackpressured,
                hasMore,
                hasMore ? TriggerQueueOperationalCursor.Create(snapshot.Generation, checked(start + page.Count), page[^1].DeliveryId) : null,
                page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or OverflowException or ArgumentException)
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
        }
        catch (TriggerQueuePersistenceBackpressureException)
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Backpressured);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException)
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Unavailable);
        }
    }

    private static TriggerQueueEntry Copy(TriggerQueueEntry item)
        => item with
        {
            WorkerLease = item.WorkerLease is null ? null : item.WorkerLease with { },
            Dispatch = item.Dispatch is null ? null : item.Dispatch with
            {
                GovernedInvocation = item.Dispatch.GovernedInvocation is null ? null : item.Dispatch.GovernedInvocation with { }
            }
        };

    private static bool IsValid(GovernedLoopOperationalEvidencePageRequest? request)
        => request is not null
            && request.MaximumCount is > 0 and <= GovernedLoopOperationalPostureLimits.MaxPageItems
            && (request.AfterId is null || GovernedLoopOperationalContract.IsQueueCursor(request.AfterId));

    private static GovernedLoopQueueEvidenceReadResult Result(GovernedLoopOperationalEvidenceReadStatus status)
        => new(status, 0, 0, 0, 0, 0, false, false, null, Array.AsReadOnly(Array.Empty<TriggerQueueEntry>()));
}
