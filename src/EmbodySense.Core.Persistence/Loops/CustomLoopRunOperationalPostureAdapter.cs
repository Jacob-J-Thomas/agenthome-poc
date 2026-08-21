using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture;
using System.Security.Cryptography;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>Projects bounded run posture directly from canonical run artifacts and their discovery index.</summary>
public sealed class CustomLoopRunOperationalPostureAdapter : IGovernedLoopRunOperationalPosturePort
{
    private const int MaximumCoherentSnapshotAttempts = 3;
    private readonly CustomLoopRunStore _store;

    /// <summary>Creates an adapter over the exact run store owned by the canonical runtime.</summary>
    public CustomLoopRunOperationalPostureAdapter(CustomLoopRunStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopRunEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null
            || request.MaximumCount is < 1 or > GovernedLoopOperationalPostureLimits.MaxPageItems
            || request.AfterId is not null
                && !GovernedLoopOperationalContract.IsRunCursor(request.AfterId))
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
        }

        try
        {
            // The cursor page is not allowed to hide corruption in an unselected run,
            // tombstone, or deletion receipt. This canonical bounded quota scan validates
            // the complete retained store before any posture item is projected.
            await _store.GetTraceQuotaAsync(cancellationToken).ConfigureAwait(false);
            var page = await _store.ListPageAsync(
                new CustomLoopRunPageRequest(request.MaximumCount, Cursor: request.AfterId),
                cancellationToken).ConfigureAwait(false);
            var items = new List<GovernedLoopRunEvidenceSnapshot>(page.Items.Count);
            foreach (var summary in page.Items)
            {
                var coherent = await ReadCoherentSnapshotAsync(summary, cancellationToken).ConfigureAwait(false);
                if (coherent.Status != GovernedLoopOperationalEvidenceReadStatus.Found)
                {
                    return Result(coherent.Status);
                }

                items.Add(coherent.Snapshot!);
            }

            return new GovernedLoopRunEvidenceReadResult(
                items.Count == 0 ? GovernedLoopOperationalEvidenceReadStatus.Empty : GovernedLoopOperationalEvidenceReadStatus.Found,
                page.ContinuationCursor is not null,
                page.ContinuationCursor,
                Array.AsReadOnly(items.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or OverflowException or ArgumentException)
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Corrupt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or NotSupportedException)
        {
            return Result(GovernedLoopOperationalEvidenceReadStatus.Unavailable);
        }
    }

    private static GovernedLoopRunEvidenceReadResult Result(GovernedLoopOperationalEvidenceReadStatus status)
        => new(status, false, null, Array.AsReadOnly(Array.Empty<GovernedLoopRunEvidenceSnapshot>()));

    private async Task<(GovernedLoopOperationalEvidenceReadStatus Status, GovernedLoopRunEvidenceSnapshot? Snapshot)> ReadCoherentSnapshotAsync(
        CustomLoopRunSummary selectedSummary,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumCoherentSnapshotAttempts; attempt++)
        {
            var before = await _store.GetMonitorAsync(selectedSummary.Id, cancellationToken).ConfigureAwait(false);
            var run = await _store.GetAsync(selectedSummary.Id, cancellationToken).ConfigureAwait(false);
            var after = await _store.GetMonitorAsync(selectedSummary.Id, cancellationToken).ConfigureAwait(false);
            if (before is null || after is null || !Equals(before, after))
            {
                continue;
            }

            var currentSummary = before.Summary;
            if (!HasSameRetainedIdentity(selectedSummary, currentSummary)
                || !GovernedLoopOperationalContract.IsHash(before.ArtifactHash))
            {
                return (GovernedLoopOperationalEvidenceReadStatus.Corrupt, null);
            }

            if (currentSummary.IsDeleted)
            {
                if (run is not null)
                {
                    continue;
                }

                return (GovernedLoopOperationalEvidenceReadStatus.Found, new GovernedLoopRunEvidenceSnapshot(
                    currentSummary with { },
                    null,
                    null,
                    before.ArtifactHash));
            }

            if (run is null || !CustomLoopRunValidator.Validate(run).IsValid)
            {
                continue;
            }

            var artifact = CustomLoopRunArtifactSerializer.Serialize(run);
            var artifactHash = Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant();
            if (!Equals(ToSummary(run), currentSummary)
                || !string.Equals(artifactHash, before.ArtifactHash, StringComparison.Ordinal))
            {
                continue;
            }

            var revision = run.SequentialAdapterBinding?.ExecutionBinding.Revision;
            return (GovernedLoopOperationalEvidenceReadStatus.Found, new GovernedLoopRunEvidenceSnapshot(
                currentSummary with { },
                revision?.GraphId,
                revision?.RevisionId,
                before.ArtifactHash));
        }

        // A valid lifecycle mutation can land between the discovery, monitor, and
        // canonical-artifact reads. Exhausting this finite retry budget is temporary
        // read contention, not evidence that either durable artifact is corrupt.
        return (GovernedLoopOperationalEvidenceReadStatus.Backpressured, null);
    }

    private static bool HasSameRetainedIdentity(CustomLoopRunSummary selected, CustomLoopRunSummary current)
        => string.Equals(selected.Id, current.Id, StringComparison.Ordinal)
            && string.Equals(selected.LoopId, current.LoopId, StringComparison.Ordinal)
            && string.Equals(selected.AdmissionOperationId, current.AdmissionOperationId, StringComparison.Ordinal)
            && selected.DefinitionVersion == current.DefinitionVersion
            && selected.CreatedAtUtc == current.CreatedAtUtc;

    private static CustomLoopRunSummary ToSummary(CustomLoopRunRecord run)
        => new(
            run.Id,
            run.LoopId,
            run.AdmissionOperationId,
            run.AdmittedDefinition.DefinitionVersion,
            run.LifecycleVersion,
            run.Status,
            run.CreatedAtUtc,
            run.UpdatedAtUtc,
            run.CompletedAtUtc,
            run.Checkpoint.Iteration,
            run.Checkpoint.NextStepIndex,
            run.FailureCode,
            IsDeleted: false);

}
