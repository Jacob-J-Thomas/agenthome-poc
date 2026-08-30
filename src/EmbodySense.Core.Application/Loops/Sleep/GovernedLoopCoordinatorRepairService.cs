using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Owns current-operator authorization, exact failed-evidence binding, and idempotent coordinator-repair admission.</summary>
/// <remarks>
/// This service only appends an immutable repair disposition. Startup owns the separately fenced acquisition and runtime
/// lifetime; neither preview nor submit can change a failed lifecycle or execute any work-family action.
/// </remarks>
public sealed class GovernedLoopCoordinatorRepairService : IGovernedLoopCoordinatorRepairService
{
    private readonly IGovernedLoopOperationalControlAuthorityPort _authority;
    private readonly IGovernedLoopCoordinatorRepairDependencyPort _dependencies;
    private readonly IGovernedLoopCoordinatorEvidencePort _evidence;
    private readonly IGovernedLoopCoordinatorRepairPort _repairs;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    /// <summary>Creates a repair admission service over current trusted authority, coordinator evidence, and non-actuating dependency probes.</summary>
    public GovernedLoopCoordinatorRepairService(
        string workspaceId,
        IGovernedLoopOperationalControlAuthorityPort authority,
        IGovernedLoopCoordinatorEvidencePort evidence,
        IGovernedLoopCoordinatorRepairPort repairs,
        IGovernedLoopCoordinatorRepairDependencyPort dependencies,
        TimeProvider? timeProvider = null)
    {
        if (!IsWorkspaceId(workspaceId))
        {
            throw new ArgumentException("Coordinator repair requires a bounded trusted workspace identity.", nameof(workspaceId));
        }

        _workspaceId = workspaceId;
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopCoordinatorRepairPreview> PreviewAsync(
        GovernedLoopCoordinatorRepairPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValid(request))
        {
            return Preview(GovernedLoopCoordinatorRepairPreviewStatus.Invalid, request?.OperationId ?? string.Empty, null, "coordinator-repair-preview-invalid");
        }

        var authority = await ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
        if (authority.Status is not GovernedLoopCoordinatorRepairPreviewStatus.Ready)
        {
            return Preview(authority.Status, request!.OperationId, null, authority.ReasonCode);
        }

        var evidence = await ReadEvidenceAsync(request!.CoordinatorId, cancellationToken).ConfigureAwait(false);
        if (evidence.Status is not GovernedLoopCoordinatorRepairPreviewStatus.Ready)
        {
            return Preview(evidence.Status, request.OperationId, null, evidence.ReasonCode);
        }

        var dependencies = await ReadDependenciesAsync(request.CoordinatorId, cancellationToken).ConfigureAwait(false);
        if (dependencies.Status is not GovernedLoopCoordinatorRepairPreviewStatus.Ready)
        {
            return Preview(dependencies.Status, request.OperationId, null, dependencies.ReasonCode);
        }

        if (!TryGetUtcNow(out var recordedAtUtc))
        {
            return Preview(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, request.OperationId, null, "coordinator-repair-clock-unavailable");
        }
        if (recordedAtUtc < dependencies.Value!.EvaluatedAtUtc)
        {
            recordedAtUtc = dependencies.Value.EvaluatedAtUtc;
        }

        var snapshot = evidence.Value!;
        var disposition = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            GovernedLoopCoordinatorRepairDisposition.CurrentSchemaVersion,
            _workspaceId,
            request.CoordinatorId,
            request.OperationId,
            authority.Value!.ActorId,
            snapshot.Ownership,
            snapshot.LatestLifecycle.ContentHash,
            snapshot.LatestHeartbeat.ContentHash,
            snapshot.LatestFailureHash!,
            dependencies.Value,
            recordedAtUtc,
            string.Empty));
        return !GovernedLoopSleepContractValidator.Validate(disposition).IsValid
            ? Preview(GovernedLoopCoordinatorRepairPreviewStatus.Corrupt, request.OperationId, null, "coordinator-repair-preview-corrupt")
            : Preview(GovernedLoopCoordinatorRepairPreviewStatus.Ready, request.OperationId, disposition, "coordinator-repair-preview-ready");
    }

    /// <inheritdoc />
    public async Task<GovernedLoopCoordinatorRepairSubmitResult> SubmitAsync(
        GovernedLoopCoordinatorRepairSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !GovernedLoopSleepContractValidator.Validate(request.Disposition).IsValid)
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Invalid, request?.Disposition.OperationId ?? string.Empty, null, "coordinator-repair-submit-invalid");
        }

        var disposition = request.Disposition;
        if (!string.Equals(disposition.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, disposition.OperationId, null, "coordinator-repair-workspace-conflict");
        }

        var authority = await ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
        if (authority.Status != GovernedLoopCoordinatorRepairPreviewStatus.Ready)
        {
            return Submit(Map(authority.Status), disposition.OperationId, null, authority.ReasonCode);
        }
        if (!string.Equals(authority.Value!.ActorId, disposition.ActorId, StringComparison.Ordinal))
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Unauthorized, disposition.OperationId, null, "coordinator-repair-actor-changed");
        }

        var retained = await ReadRetainedRepairAsync(disposition, cancellationToken).ConfigureAwait(false);
        if (retained is not null)
        {
            return retained;
        }

        var evidence = await ReadEvidenceAsync(disposition.CoordinatorId, cancellationToken).ConfigureAwait(false);
        if (evidence.Status != GovernedLoopCoordinatorRepairPreviewStatus.Ready)
        {
            return Submit(Map(evidence.Status), disposition.OperationId, null, evidence.ReasonCode);
        }
        if (!Matches(disposition, evidence.Value!))
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, disposition.OperationId, null, "coordinator-repair-evidence-stale");
        }

        var dependencies = await ReadDependenciesAsync(disposition.CoordinatorId, cancellationToken).ConfigureAwait(false);
        if (dependencies.Status != GovernedLoopCoordinatorRepairPreviewStatus.Ready)
        {
            return Submit(Map(dependencies.Status), disposition.OperationId, null, dependencies.ReasonCode);
        }

        GovernedLoopCoordinatorRepairMutationResult? persisted;
        try
        {
            persisted = await _repairs.AppendAsync(disposition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, disposition.OperationId, null, "coordinator-repair-ledger-unavailable");
        }

        if (!IsValid(persisted))
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, disposition.OperationId, null, "coordinator-repair-ledger-corrupt");
        }
        return persisted!.Status switch
        {
            GovernedLoopCoordinatorRepairMutationStatus.Appended => Submit(GovernedLoopCoordinatorRepairSubmitStatus.Accepted, disposition.OperationId, persisted.Disposition, "coordinator-repair-accepted"),
            GovernedLoopCoordinatorRepairMutationStatus.Duplicate => Submit(GovernedLoopCoordinatorRepairSubmitStatus.Replayed, disposition.OperationId, persisted.Disposition, "coordinator-repair-replayed"),
            GovernedLoopCoordinatorRepairMutationStatus.Conflict => Submit(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, disposition.OperationId, null, "coordinator-repair-ledger-conflict"),
            GovernedLoopCoordinatorRepairMutationStatus.Corrupt => Submit(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, disposition.OperationId, null, "coordinator-repair-ledger-corrupt"),
            _ => Submit(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, disposition.OperationId, null, "coordinator-repair-ledger-unavailable")
        };
    }

    private async Task<GovernedLoopCoordinatorRepairSubmitResult?> ReadRetainedRepairAsync(GovernedLoopCoordinatorRepairDisposition disposition, CancellationToken cancellationToken)
    {
        GovernedLoopCoordinatorRepairReadResult? read;
        try
        {
            read = await _repairs.ReadAsync(disposition.CoordinatorId, disposition.FailedOwnership.ContentHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, disposition.OperationId, null, "coordinator-repair-ledger-unavailable");
        }

        if (read is null || !Enum.IsDefined(read.Status))
        {
            return Submit(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, disposition.OperationId, null, "coordinator-repair-ledger-corrupt");
        }
        if (read.Status == GovernedLoopCoordinatorRepairReadStatus.NotFound && read.Disposition is null)
        {
            return null;
        }
        if (read.Status == GovernedLoopCoordinatorRepairReadStatus.Found
            && GovernedLoopSleepContractValidator.Validate(read.Disposition).IsValid)
        {
            return read.Disposition == disposition
                ? Submit(GovernedLoopCoordinatorRepairSubmitStatus.Replayed, disposition.OperationId, read.Disposition, "coordinator-repair-replayed")
                : Submit(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, disposition.OperationId, null, "coordinator-repair-ledger-conflict");
        }

        return read.Status == GovernedLoopCoordinatorRepairReadStatus.Unavailable && read.Disposition is null
            ? Submit(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, disposition.OperationId, null, "coordinator-repair-ledger-unavailable")
            : Submit(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, disposition.OperationId, null, "coordinator-repair-ledger-corrupt");
    }

    private async Task<ReadResult<GovernedLoopOperationalControlAuthority>> ReadAuthorityAsync(CancellationToken cancellationToken)
    {
        GovernedLoopOperationalControlAuthority? authority;
        try
        {
            authority = await _authority.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, null, "coordinator-repair-authority-unavailable");
        }

        if (!GovernedLoopOperationalContract.IsValid(authority))
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Corrupt, null, "coordinator-repair-authority-corrupt");
        }
        if (!authority!.Permitted || !string.Equals(authority.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Unauthorized, null, "coordinator-repair-authority-denied");
        }

        return new(GovernedLoopCoordinatorRepairPreviewStatus.Ready, authority, "coordinator-repair-authority-ready");
    }

    private async Task<ReadResult<GovernedLoopCoordinatorSnapshot>> ReadEvidenceAsync(string coordinatorId, CancellationToken cancellationToken)
    {
        GovernedLoopCoordinatorReadResult? read;
        try
        {
            read = await _evidence.ReadAsync(coordinatorId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, null, "coordinator-repair-evidence-unavailable");
        }

        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(read))
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Corrupt, null, "coordinator-repair-evidence-corrupt");
        }
        if (read!.Status == GovernedLoopCoordinatorReadStatus.NotFound)
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.NotFound, null, "coordinator-repair-not-found");
        }
        if (read.Status != GovernedLoopCoordinatorReadStatus.Found || read.Snapshot is null)
        {
            return new(read.Status == GovernedLoopCoordinatorReadStatus.Unavailable
                ? GovernedLoopCoordinatorRepairPreviewStatus.Unavailable
                : GovernedLoopCoordinatorRepairPreviewStatus.Corrupt, null, "coordinator-repair-evidence-unavailable");
        }
        if (!TryGetUtcNow(out var now))
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, null, "coordinator-repair-clock-unavailable");
        }

        var snapshot = read.Snapshot;
        return snapshot.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed
            && snapshot.LatestLifecycle.TerminalAtUtc is not null
            && snapshot.LatestFailureSequence > 0
            && snapshot.LatestFailureHash is not null
            && now >= snapshot.LatestHeartbeat.LeaseExpiresAtUtc
            ? new(GovernedLoopCoordinatorRepairPreviewStatus.Ready, snapshot, "coordinator-repair-evidence-ready")
            : new(GovernedLoopCoordinatorRepairPreviewStatus.Conflict, null, "coordinator-repair-live-or-not-failed");
    }

    private async Task<ReadResult<GovernedLoopCoordinatorRepairReadiness>> ReadDependenciesAsync(string coordinatorId, CancellationToken cancellationToken)
    {
        GovernedLoopCoordinatorRepairReadiness? readiness;
        try
        {
            readiness = await _dependencies.ReadAsync(_workspaceId, coordinatorId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, null, "coordinator-repair-dependencies-unavailable");
        }

        if (!GovernedLoopSleepContractValidator.Validate(readiness).IsValid)
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Corrupt, null, "coordinator-repair-dependencies-corrupt");
        }
        if (!string.Equals(readiness!.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(readiness.CoordinatorId, coordinatorId, StringComparison.Ordinal)
            || !GovernedLoopCoordinatorRepairReadinessContract.IsReady(readiness))
        {
            return new(GovernedLoopCoordinatorRepairPreviewStatus.Conflict, null, "coordinator-repair-dependencies-not-ready");
        }

        return new(GovernedLoopCoordinatorRepairPreviewStatus.Ready, readiness, "coordinator-repair-dependencies-ready");
    }

    private static bool Matches(GovernedLoopCoordinatorRepairDisposition disposition, GovernedLoopCoordinatorSnapshot snapshot)
        => snapshot.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed
            && snapshot.Ownership == disposition.FailedOwnership
            && string.Equals(snapshot.LatestLifecycle.ContentHash, disposition.TerminalLifecycleHash, StringComparison.Ordinal)
            && string.Equals(snapshot.LatestHeartbeat.ContentHash, disposition.LatestHeartbeatHash, StringComparison.Ordinal)
            && string.Equals(snapshot.LatestFailureHash, disposition.LatestFailureHash, StringComparison.Ordinal);

    private bool TryGetUtcNow(out DateTimeOffset value)
    {
        try
        {
            value = _timeProvider.GetUtcNow();
            return value != default && value.Offset == TimeSpan.Zero;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private static bool IsWorkspaceId(string? value)
        => ContextualRoleWorkspaceId.IsValid(value);

    private static bool IsValid(GovernedLoopCoordinatorRepairPreviewRequest? request)
        => request is not null
            && GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId(request.CoordinatorId)
            && CustomLoopArtifactIdentifier.IsValid(request.OperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters);

    private static bool IsValid(GovernedLoopCoordinatorRepairMutationResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && (result.Status is GovernedLoopCoordinatorRepairMutationStatus.Appended or GovernedLoopCoordinatorRepairMutationStatus.Duplicate
                ? GovernedLoopSleepContractValidator.Validate(result.Disposition).IsValid
                : result.Disposition is null);

    private static GovernedLoopCoordinatorRepairPreview Preview(
        GovernedLoopCoordinatorRepairPreviewStatus status,
        string operationId,
        GovernedLoopCoordinatorRepairDisposition? disposition,
        string reasonCode)
        => new(status, operationId, disposition, reasonCode);

    private static GovernedLoopCoordinatorRepairSubmitResult Submit(
        GovernedLoopCoordinatorRepairSubmitStatus status,
        string operationId,
        GovernedLoopCoordinatorRepairDisposition? disposition,
        string reasonCode)
        => new(status, operationId, disposition, reasonCode);

    private static GovernedLoopCoordinatorRepairSubmitStatus Map(GovernedLoopCoordinatorRepairPreviewStatus status)
        => status switch
        {
            GovernedLoopCoordinatorRepairPreviewStatus.Unauthorized => GovernedLoopCoordinatorRepairSubmitStatus.Unauthorized,
            GovernedLoopCoordinatorRepairPreviewStatus.Conflict or GovernedLoopCoordinatorRepairPreviewStatus.NotFound => GovernedLoopCoordinatorRepairSubmitStatus.Conflict,
            GovernedLoopCoordinatorRepairPreviewStatus.Corrupt => GovernedLoopCoordinatorRepairSubmitStatus.Corrupt,
            GovernedLoopCoordinatorRepairPreviewStatus.Invalid => GovernedLoopCoordinatorRepairSubmitStatus.Invalid,
            _ => GovernedLoopCoordinatorRepairSubmitStatus.Unavailable
        };

    private sealed record ReadResult<T>(GovernedLoopCoordinatorRepairPreviewStatus Status, T? Value, string ReasonCode) where T : class;
}
