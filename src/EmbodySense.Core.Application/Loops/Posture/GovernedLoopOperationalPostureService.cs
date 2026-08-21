using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Builds one bounded read-only posture from authoritative local-background evidence ports.</summary>
/// <remarks>Projection never selects work, changes lifecycle, or treats caller state as authority. Any malformed family fails the aggregate read closed.</remarks>
public sealed class GovernedLoopOperationalPostureService : IGovernedLoopOperationalPostureReader
{
    private readonly IGovernedLoopOperationalControlAuthorityPort _authority;
    private readonly string _coordinatorId;
    private readonly IGovernedLoopCoordinatorEvidencePort _coordinator;
    private readonly IGovernedLoopQueueOperationalPosturePort _queue;
    private readonly IGovernedLoopRunOperationalPosturePort _runs;
    private readonly IScheduleOperationalPosturePort _schedules;
    private readonly TimeProvider _timeProvider;
    private readonly string _triggerWorkspaceId;
    private readonly IGovernedLoopWakeOperationalPosturePort _wakes;
    private readonly string _workspaceId;

    /// <summary>Creates one projection service over closed authoritative ports and exact runtime coordinates.</summary>
    public GovernedLoopOperationalPostureService(
        string workspaceId,
        string triggerWorkspaceId,
        string coordinatorId,
        IGovernedLoopQueueOperationalPosturePort queue,
        IScheduleOperationalPosturePort schedules,
        IGovernedLoopWakeOperationalPosturePort wakes,
        IGovernedLoopRunOperationalPosturePort runs,
        IGovernedLoopCoordinatorEvidencePort coordinator,
        IGovernedLoopOperationalControlAuthorityPort authority,
        TimeProvider? timeProvider = null)
    {
        if (!GovernedLoopOperationalContract.IsWorkspaceId(workspaceId))
        {
            throw new ArgumentException("Operational posture requires a bounded trusted workspace identity.", nameof(workspaceId));
        }
        if (!CustomLoopArtifactIdentifier.IsValid(triggerWorkspaceId, GovernedLoopOperationalPostureLimits.MaxWorkspaceIdCharacters))
        {
            throw new ArgumentException("Operational posture requires the exact trigger-plane workspace identity.", nameof(triggerWorkspaceId));
        }
        if (!GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId(coordinatorId))
        {
            throw new ArgumentException("Operational posture requires the stable canonical coordinator identity.", nameof(coordinatorId));
        }
        _workspaceId = workspaceId;
        _triggerWorkspaceId = triggerWorkspaceId;
        _coordinatorId = coordinatorId;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _wakes = wakes ?? throw new ArgumentNullException(nameof(wakes));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Reads independent finite pages from every family at one trusted UTC observation instant.</summary>
    public async Task<GovernedLoopOperationalPostureResult> ReadAsync(
        GovernedLoopOperationalPostureQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopOperationalContract.IsValid(query))
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Invalid, "operational-posture-request-invalid");
        }

        DateTimeOffset readStartedAtUtc;
        try
        {
            readStartedAtUtc = _timeProvider.GetUtcNow();
        }
        catch (Exception)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Unavailable, "operational-posture-clock-unavailable");
        }
        if (!GovernedLoopOperationalContract.IsUtc(readStartedAtUtc))
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Corrupt, "operational-posture-clock-corrupt");
        }

        GovernedLoopQueueEvidenceReadResult? queue;
        GovernedLoopScheduleEvidenceReadResult? schedules;
        GovernedLoopWakeCatalogEvidenceReadResult? wakes;
        GovernedLoopRunEvidenceReadResult? runs;
        GovernedLoopCoordinatorReadResult? coordinator;
        GovernedLoopOperationalControlAuthority? authority;
        try
        {
            var queueTask = _queue.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(query.MaximumQueueEntries, query.QueueCursor), readStartedAtUtc, cancellationToken);
            var scheduleTask = _schedules.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(query.MaximumSchedules, query.AfterScheduleId), cancellationToken);
            var wakeTask = _wakes.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(query.MaximumWakes, query.AfterCheckpointId), cancellationToken);
            var runTask = _runs.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(query.MaximumRuns, query.AfterRunId), cancellationToken);
            var coordinatorTask = _coordinator.ReadAsync(_coordinatorId, cancellationToken);
            var authorityTask = _authority.ReadCurrentAsync(cancellationToken);
            await Task.WhenAll(queueTask, scheduleTask, wakeTask, runTask, coordinatorTask, authorityTask).ConfigureAwait(false);
            queue = Detach(await queueTask.ConfigureAwait(false));
            schedules = GovernedLoopOperationalEvidenceDetacher.Schedules(await scheduleTask.ConfigureAwait(false));
            wakes = GovernedLoopOperationalEvidenceDetacher.Wakes(await wakeTask.ConfigureAwait(false));
            runs = Detach(await runTask.ConfigureAwait(false));
            coordinator = await coordinatorTask.ConfigureAwait(false);
            authority = await authorityTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Unavailable, "operational-posture-source-unavailable");
        }

        DateTimeOffset observedAtUtc;
        try
        {
            observedAtUtc = _timeProvider.GetUtcNow();
        }
        catch (Exception)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Unavailable, "operational-posture-clock-unavailable");
        }
        if (!GovernedLoopOperationalContract.IsUtc(observedAtUtc) || observedAtUtc < readStartedAtUtc)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Corrupt, "operational-posture-clock-corrupt");
        }

        if (!IsValid(queue, observedAtUtc)
            || !IsValid(schedules)
            || !IsValid(wakes)
            || !IsValid(runs, observedAtUtc)
            || !IsValid(coordinator, observedAtUtc)
            || !GovernedLoopOperationalContract.IsValid(authority)
            || !string.Equals(authority!.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || authority.ObservedAtUtc < readStartedAtUtc
            || authority.ObservedAtUtc > observedAtUtc)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Corrupt, "operational-posture-evidence-corrupt");
        }

        var statuses = new[] { queue!.Status, schedules!.Status, wakes!.Status, runs!.Status };
        if (statuses.Contains(GovernedLoopOperationalEvidenceReadStatus.Corrupt)
            || coordinator!.Status == GovernedLoopCoordinatorReadStatus.Corrupt)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Corrupt, "operational-posture-evidence-corrupt");
        }
        if (statuses.Contains(GovernedLoopOperationalEvidenceReadStatus.Unavailable)
            || coordinator.Status == GovernedLoopCoordinatorReadStatus.Unavailable)
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Unavailable, "operational-posture-source-unavailable");
        }
        if (statuses.Contains(GovernedLoopOperationalEvidenceReadStatus.Backpressured))
        {
            return Result(GovernedLoopOperationalPostureReadStatus.Backpressured, "operational-posture-source-backpressured");
        }

        var snapshot = new GovernedLoopOperationalPostureSnapshot(
            GovernedLoopOperationalPostureSnapshot.CurrentSchemaVersion,
            _workspaceId,
            observedAtUtc,
            authority.EvidenceHash,
            ProjectQueue(queue, observedAtUtc),
            ProjectSchedules(schedules, observedAtUtc),
            ProjectWakes(wakes, observedAtUtc),
            ProjectRuns(runs, observedAtUtc),
            ProjectCoordinator(coordinator, observedAtUtc));
        var backpressured = queue.PersistenceBackpressured;
        return new GovernedLoopOperationalPostureResult(
            backpressured ? GovernedLoopOperationalPostureReadStatus.Backpressured : GovernedLoopOperationalPostureReadStatus.Available,
            snapshot,
            backpressured ? "trigger-queue-persistence-backpressured" : "operational-posture-available");
    }

    private GovernedLoopQueuePosture ProjectQueue(GovernedLoopQueueEvidenceReadResult source, DateTimeOffset observedAtUtc)
    {
        var projected = new List<GovernedLoopQueueItemPosture>(source.Items.Count);
        foreach (var item in source.Items)
        {
            var leaseExpired = item.WorkerLease is { ReleasedAtUtc: null } lease && observedAtUtc >= lease.ExpiresAtUtc;
            var state = QueueState(item, leaseExpired);
            var controls = IsNonterminal(item.State)
                ? Controls(new GovernedLoopControlEligibility(GovernedLoopOperationalControlKind.CancelDelivery, item.Revision, item.CanonicalEnvelopeHash))
                : Controls();
            projected.Add(new GovernedLoopQueueItemPosture(
                _workspaceId,
                GovernedLoopOperationalSource.Queue,
                QueueSeverity(item, leaseExpired),
                observedAtUtc,
                item.CanonicalEnvelopeHash,
                item.DeliveryId.Value,
                item.LoopId,
                item.TargetGraphId,
                item.TargetRevisionId,
                state,
                QueueReason(item, leaseExpired, observedAtUtc),
                null,
                item.OrderKey.EligibleAtUtc,
                item.Revision,
                item.WorkerLease?.WorkerId,
                item.WorkerLease?.Generation,
                item.WorkerLease?.ExpiresAtUtc,
                leaseExpired,
                controls));
        }
        var catalogHash = GovernedLoopOperationalHash.QueueCatalog(
            source.Generation,
            source.QueuedEntries,
            source.QueuedReservationBytes,
            source.RetainedEntries,
            source.RetainedReservationBytes,
            source.PersistenceBackpressured);
        return new GovernedLoopQueuePosture(
            source.Generation,
            catalogHash,
            source.QueuedEntries,
            source.QueuedReservationBytes,
            source.RetainedEntries,
            source.RetainedReservationBytes,
            source.PersistenceBackpressured,
            source.HasMore,
            source.ContinuationCursor,
            Array.AsReadOnly(projected.ToArray()),
            source.QueuedEntries > 0
                ? Controls(new GovernedLoopControlEligibility(GovernedLoopOperationalControlKind.CancelPendingDeliveries, source.Generation, catalogHash))
                : Controls());
    }

    private GovernedLoopScheduleCatalogPosture ProjectSchedules(GovernedLoopScheduleEvidenceReadResult source, DateTimeOffset observedAtUtc)
    {
        var items = source.Items.Select(item => ProjectSchedule(item, observedAtUtc));
        return new GovernedLoopScheduleCatalogPosture(source.Generation, source.HasMore, source.ContinuationCursor, Order(items, item => item.Severity, item => item.ScheduleId));
    }

    private GovernedLoopSchedulePosture ProjectSchedule(GovernedLoopScheduleEvidenceSnapshot item, DateTimeOffset observedAtUtc)
    {
        var definition = item.Definition;
        var state = item.State;
        var pending = state.PendingDelivery;
        var next = pending?.Occurrence.ScheduledAtUtc ?? state.DeferredOccurrence?.Occurrence.ScheduledAtUtc ?? state.NextOccurrence?.ScheduledAtUtc;
        var (posture, reason) = ScheduleState(definition, state, observedAtUtc);
        var hash = StateHash(state);
        var controls = !definition.Enabled
            ? Controls()
            : Controls(new GovernedLoopControlEligibility(
                state.Enabled ? GovernedLoopOperationalControlKind.DisableSchedule : GovernedLoopOperationalControlKind.EnableSchedule,
                state.StateRevision,
                hash));
        var revision = definition.Target.GovernedPublication!.Revision;
        return new GovernedLoopSchedulePosture(
            _workspaceId,
            GovernedLoopOperationalSource.Schedule,
            ScheduleSeverity(posture),
            observedAtUtc,
            hash,
            definition.ScheduleId.Value,
            revision.GraphId,
            revision.RevisionId,
            definition.Revision,
            state.StateRevision,
            state.Enabled,
            posture,
            reason,
            next,
            pending?.Identity.DeliveryId.Value,
            pending is null ? null : PendingPhase(pending.Phase),
            controls);
    }

    private GovernedLoopWakeCatalogPosture ProjectWakes(GovernedLoopWakeCatalogEvidenceReadResult source, DateTimeOffset observedAtUtc)
    {
        var items = source.Items.Select(item => ProjectWake(item, observedAtUtc));
        return new GovernedLoopWakeCatalogPosture(source.Generation, source.HasMore, source.ContinuationCursor, Order(items, item => item.Severity, item => item.CheckpointId));
    }

    private GovernedLoopWakePosture ProjectWake(GovernedLoopWakeEvidenceSnapshot item, DateTimeOffset observedAtUtc)
    {
        var checkpoint = item.Checkpoint;
        var wake = item.Wake;
        var (state, reason) = wake is null
            ? checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp && checkpoint.WakeDeadlineUtc <= observedAtUtc
                ? ("due", "wake-timestamp-due")
                : checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp
                    ? ("sleeping", "wake-timestamp-pending")
                    : ("waiting", "wake-event-pending")
            : WakeState(wake.Disposition);
        var revision = checkpoint.Binding.Execution.Revision;
        return new GovernedLoopWakePosture(
            _workspaceId,
            GovernedLoopOperationalSource.Wake,
            WakeSeverity(state),
            observedAtUtc,
            wake?.ContentHash ?? checkpoint.ContentHash,
            checkpoint.CheckpointId,
            checkpoint.Binding.Execution.RunId,
            checkpoint.Binding.NodeId,
            revision.GraphId,
            revision.RevisionId,
            state,
            reason,
            checkpoint.WakeDeadlineUtc,
            wake?.EvidenceVersion,
            wake?.Identity.WakeId,
            Controls());
    }

    private GovernedLoopRunCatalogPosture ProjectRuns(GovernedLoopRunEvidenceReadResult source, DateTimeOffset observedAtUtc)
    {
        var items = source.Items.Select(item =>
        {
            var summary = item.Summary;
            var state = summary.IsDeleted ? "deleted" : Token(summary.Status);
            var eligible = (summary.IsDeleted
                    ? Enumerable.Empty<GovernedLoopOperationalControlKind>()
                    : CustomLoopLifecycleControlEligibility.GetEligible(summary.Status))
                .Select(kind => new GovernedLoopControlEligibility(kind, summary.LifecycleVersion, item.EvidenceHash));
            return new GovernedLoopRunPosture(
                _workspaceId,
                GovernedLoopOperationalSource.Run,
                RunSeverity(summary.Status),
                observedAtUtc,
                item.EvidenceHash,
                summary.Id,
                summary.LoopId,
                item.GraphId,
                item.RevisionId,
                summary.LifecycleVersion,
                state,
                summary.IsDeleted ? "run-deleted" : "run-" + state,
                summary.UpdatedAtUtc,
                Controls(eligible.ToArray()));
        });
        return new GovernedLoopRunCatalogPosture(source.HasMore, source.ContinuationCursor, Order(items, item => item.Severity, item => item.RunId));
    }

    private GovernedLoopCoordinatorPosture ProjectCoordinator(GovernedLoopCoordinatorReadResult source, DateTimeOffset observedAtUtc)
    {
        if (source.Status == GovernedLoopCoordinatorReadStatus.NotFound)
        {
            return new GovernedLoopCoordinatorPosture(
                _workspaceId,
                GovernedLoopOperationalSource.Coordinator,
                GovernedLoopPostureSeverity.Information,
                observedAtUtc,
                null,
                "stopped",
                "coordinator-not-started",
                _coordinatorId,
                null,
                null,
                null,
                null,
                false,
                0,
                null,
                Controls());
        }

        var snapshot = source.Snapshot!;
        var expired = observedAtUtc >= snapshot.LatestHeartbeat.LeaseExpiresAtUtc;
        var lifecycle = snapshot.LatestLifecycle.Status;
        var (state, reason) = expired && lifecycle is GovernedLoopCoordinatorStatus.Starting or GovernedLoopCoordinatorStatus.Running or GovernedLoopCoordinatorStatus.Stopping
            ? ("blocked", "coordinator-lease-expired")
            : lifecycle switch
            {
                GovernedLoopCoordinatorStatus.Starting => ("starting", "coordinator-starting"),
                GovernedLoopCoordinatorStatus.Running => ("running", snapshot.LatestFailureSequence > 0 ? "coordinator-running-with-failure-evidence" : "coordinator-running"),
                GovernedLoopCoordinatorStatus.Stopping => ("stopping", "coordinator-stopping"),
                GovernedLoopCoordinatorStatus.Stopped => ("stopped", "coordinator-stopped"),
                _ => ("failed", "coordinator-failed")
            };
        var hashes = snapshot.LatestFailureHash is null
            ? new[] { snapshot.Ownership.ContentHash, snapshot.LatestLifecycle.ContentHash, snapshot.LatestHeartbeat.ContentHash }
            : new[] { snapshot.Ownership.ContentHash, snapshot.LatestLifecycle.ContentHash, snapshot.LatestHeartbeat.ContentHash, snapshot.LatestFailureHash };
        return new GovernedLoopCoordinatorPosture(
            _workspaceId,
            GovernedLoopOperationalSource.Coordinator,
            state is "failed" ? GovernedLoopPostureSeverity.Critical : expired ? GovernedLoopPostureSeverity.Warning : GovernedLoopPostureSeverity.Information,
            observedAtUtc,
            GovernedLoopOperationalHash.Evidence(hashes),
            state,
            reason,
            snapshot.Ownership.CoordinatorId,
            snapshot.Ownership.OwnerId,
            snapshot.Ownership.OwnershipEpoch,
            snapshot.LatestHeartbeat.RecordedAtUtc,
            snapshot.LatestHeartbeat.LeaseExpiresAtUtc,
            expired,
            snapshot.LatestFailureSequence,
            snapshot.LatestFailureHash,
            Controls());
    }

    private static GovernedLoopQueueEvidenceReadResult? Detach(GovernedLoopQueueEvidenceReadResult? source)
        => source is null
            ? null
            : source with
            {
                Items = source.Items is null
                    ? null!
                    : Array.AsReadOnly(source.Items.Select(item => item is null ? null! : item with
                    {
                        WorkerLease = item.WorkerLease is null ? null : item.WorkerLease with { },
                        Dispatch = item.Dispatch is null ? null : item.Dispatch with
                        {
                            GovernedInvocation = item.Dispatch.GovernedInvocation is null ? null : item.Dispatch.GovernedInvocation with { }
                        }
                    }).ToArray())
            };

    private static GovernedLoopRunEvidenceReadResult? Detach(GovernedLoopRunEvidenceReadResult? source)
        => source is null
            ? null
            : source with
            {
                Items = source.Items is null
                    ? null!
                    : Array.AsReadOnly(source.Items.Select(item => item is null ? null! : item with { Summary = item.Summary with { } }).ToArray())
            };

    private bool IsValid(GovernedLoopQueueEvidenceReadResult? source, DateTimeOffset observedAtUtc)
        => source is not null
            && IsValidShape(source.Status, source.Items, source.HasMore, source.ContinuationCursor, value => value is null || GovernedLoopOperationalContract.IsQueueCursor(value))
            && source.Generation >= 0
            && source.QueuedEntries >= 0
            && source.QueuedReservationBytes >= 0
            && source.RetainedEntries >= source.QueuedEntries
            && source.RetainedReservationBytes >= source.QueuedReservationBytes
            && source.Items.Count <= GovernedLoopOperationalPostureLimits.MaxPageItems
            && source.Items.All(item => TriggerQueueSnapshotEvidenceContract.IsValid(item)
                && string.Equals(item.WorkspaceId, _triggerWorkspaceId, StringComparison.Ordinal)
                && IsValidOptionalRevision(item.TargetGraphId, item.TargetRevisionId)
                && IsObservedQueueEvidence(item, observedAtUtc))
            && source.Items.Select(item => item.DeliveryId.Value).Distinct(StringComparer.Ordinal).Count() == source.Items.Count
            && IsCanonicallyQueueOrdered(source.Items);

    private bool IsValid(GovernedLoopScheduleEvidenceReadResult? source)
        => source is not null
            && IsValidShape(source.Status, source.Items, source.HasMore, source.ContinuationCursor, GovernedLoopOperationalContract.IsOptionalArtifactCursor)
            && source.Generation >= 0
            && source.Items.Count <= GovernedLoopOperationalPostureLimits.MaxPageItems
            && source.Items.All(item => item is not null
                && ScheduleContractValidator.ValidateDefinitionStateComposition(item.Definition, item.State).IsValid
                && item.Definition.Target.GovernedPublication is not null
                && string.Equals(item.Definition.WorkspaceId, _triggerWorkspaceId, StringComparison.Ordinal)
                && ScheduleContractHash.TryComputeState(item.State, out _, out _))
            && IsStrictlyOrdered(source.Items.Select(item => item.Definition.ScheduleId.Value))
            && (!source.HasMore || string.Equals(source.ContinuationCursor, source.Items[^1].Definition.ScheduleId.Value, StringComparison.Ordinal));

    private static bool IsValid(GovernedLoopWakeCatalogEvidenceReadResult? source)
        => source is not null
            && IsValidShape(source.Status, source.Items, source.HasMore, source.ContinuationCursor, GovernedLoopOperationalContract.IsOptionalArtifactCursor)
            && source.Generation >= 0
            && source.Items.Count <= GovernedLoopOperationalPostureLimits.MaxPageItems
            && source.Items.All(item => item is not null
                && GovernedLoopSleepContractValidator.Validate(item.Checkpoint).IsValid
                && (item.Wake is null || GovernedLoopSleepContractValidator.ValidateComposition(item.Checkpoint, item.Wake).IsValid))
            && IsStrictlyOrdered(source.Items.Select(item => item.Checkpoint.CheckpointId))
            && (!source.HasMore || string.Equals(source.ContinuationCursor, source.Items[^1].Checkpoint.CheckpointId, StringComparison.Ordinal));

    private static bool IsValid(GovernedLoopRunEvidenceReadResult? source, DateTimeOffset observedAtUtc)
        => source is not null
            && IsValidShape(source.Status, source.Items, source.HasMore, source.ContinuationCursor, GovernedLoopOperationalContract.IsOptionalRunCursor)
            && source.Items.Count <= GovernedLoopOperationalPostureLimits.MaxPageItems
            && source.Items.All(item => item is not null
                && item.Summary is not null
                && CustomLoopArtifactIdentifier.IsValid(item.Summary.Id)
                && CustomLoopArtifactIdentifier.IsValid(item.Summary.LoopId)
                && (item.Summary.IsDeleted
                    ? item.Summary.LifecycleVersion == 0
                    : item.Summary.LifecycleVersion > 0)
                && Enum.IsDefined(item.Summary.Status)
                && GovernedLoopOperationalContract.IsUtc(item.Summary.UpdatedAtUtc)
                && item.Summary.UpdatedAtUtc <= observedAtUtc
                && IsValidOptionalRevision(item.GraphId, item.RevisionId)
                && GovernedLoopOperationalContract.IsHash(item.EvidenceHash));

    private static bool IsValidOptionalRevision(string? graphId, string? revisionId)
        => graphId is null && revisionId is null
            || CustomLoopArtifactIdentifier.IsValid(graphId, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters)
                && CustomLoopArtifactIdentifier.IsValid(revisionId, GovernedLoopOperationalPostureLimits.MaxTargetIdCharacters);

    private static bool IsObservedQueueEvidence(TriggerQueueEntry item, DateTimeOffset observedAtUtc)
        => item.RecordedAtUtc <= observedAtUtc
            && (item.TerminalAtUtc is null || item.TerminalAtUtc <= observedAtUtc)
            && (item.WorkerLease is null
                || item.WorkerLease.AcquiredAtUtc <= observedAtUtc
                    && (item.WorkerLease.ReleasedAtUtc is null || item.WorkerLease.ReleasedAtUtc <= observedAtUtc))
            && (item.Dispatch is null
                || item.Dispatch.IntentRecordedAtUtc <= observedAtUtc
                    && (item.Dispatch.OutcomeRecordedAtUtc is null || item.Dispatch.OutcomeRecordedAtUtc <= observedAtUtc));

    private bool IsValid(GovernedLoopCoordinatorReadResult? source, DateTimeOffset observedAtUtc)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(source))
        {
            return false;
        }
        if (source!.Status != GovernedLoopCoordinatorReadStatus.Found)
        {
            return source.Snapshot is null;
        }
        var snapshot = source.Snapshot!;
        return string.Equals(snapshot.Ownership.CoordinatorId, _coordinatorId, StringComparison.Ordinal)
            && snapshot.Ownership.AcquiredAtUtc <= observedAtUtc
            && snapshot.LatestLifecycle.UpdatedAtUtc <= observedAtUtc
            && snapshot.LatestHeartbeat.RecordedAtUtc <= observedAtUtc;
    }

    private static bool IsValidShape<T>(
        GovernedLoopOperationalEvidenceReadStatus status,
        IReadOnlyList<T>? items,
        bool hasMore,
        string? continuationCursor,
        Func<string?, bool> cursorValidator)
        => items is not null
            && Enum.IsDefined(status)
            && hasMore == (continuationCursor is not null)
            && cursorValidator(continuationCursor)
            && status switch
            {
                GovernedLoopOperationalEvidenceReadStatus.Found => items.Count > 0,
                GovernedLoopOperationalEvidenceReadStatus.Empty => items.Count == 0 && !hasMore,
                GovernedLoopOperationalEvidenceReadStatus.Backpressured
                    or GovernedLoopOperationalEvidenceReadStatus.Corrupt
                    or GovernedLoopOperationalEvidenceReadStatus.Unavailable => items.Count == 0 && !hasMore,
                _ => false
            };

    private static bool IsStrictlyOrdered(IEnumerable<string> values)
    {
        string? previous = null;
        foreach (var value in values)
        {
            if (previous is not null && string.Compare(previous, value, StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            previous = value;
        }
        return true;
    }

    private static bool IsCanonicallyQueueOrdered(IReadOnlyList<TriggerQueueEntry> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            if (TriggerQueueOrdering.Compare(items[index - 1].OrderKey, items[index].OrderKey) > 0)
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<T> Order<T>(IEnumerable<T> items, Func<T, GovernedLoopPostureSeverity> severity, Func<T, string> identity)
        => Array.AsReadOnly(items.OrderByDescending(severity).ThenBy(identity, StringComparer.Ordinal).ToArray());

    private static IReadOnlyList<GovernedLoopControlEligibility> Controls(params GovernedLoopControlEligibility[] controls)
        => Array.AsReadOnly(controls.Select(control => control with { }).ToArray());

    private static bool IsNonterminal(TriggerQueueEntryState state)
        => state is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching;

    private static string QueueState(TriggerQueueEntry entry, bool leaseExpired)
        => leaseExpired ? "blocked" : entry.State switch
        {
            TriggerQueueEntryState.Queued => "queued",
            TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching => "running",
            TriggerQueueEntryState.Dispatched => "completed",
            TriggerQueueEntryState.Cancelled => "cancelled",
            TriggerQueueEntryState.Expired => "expired",
            TriggerQueueEntryState.NeedsReview => "needs-review",
            TriggerQueueEntryState.Backpressured => "backpressured",
            _ => "failed"
        };

    private static string QueueReason(TriggerQueueEntry entry, bool leaseExpired, DateTimeOffset observedAtUtc)
        => leaseExpired ? "trigger-worker-lease-expired" : entry.State switch
        {
            TriggerQueueEntryState.Queued => entry.OrderKey.EligibleAtUtc > observedAtUtc ? "trigger-not-yet-eligible" : "trigger-queued",
            TriggerQueueEntryState.WorkerOwned => "trigger-worker-owned",
            TriggerQueueEntryState.Dispatching => "trigger-dispatching",
            TriggerQueueEntryState.Dispatched => "trigger-dispatched",
            TriggerQueueEntryState.Cancelled => "trigger-cancelled",
            TriggerQueueEntryState.Expired => "trigger-expired",
            TriggerQueueEntryState.NeedsReview => "trigger-needs-review",
            TriggerQueueEntryState.Backpressured => "trigger-backpressured",
            _ => "trigger-failed"
        };

    private static GovernedLoopPostureSeverity QueueSeverity(TriggerQueueEntry entry, bool leaseExpired)
        => entry.State == TriggerQueueEntryState.NeedsReview ? GovernedLoopPostureSeverity.Critical
            : leaseExpired || entry.State == TriggerQueueEntryState.Backpressured ? GovernedLoopPostureSeverity.Warning
            : entry.State is TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching ? GovernedLoopPostureSeverity.Attention
            : GovernedLoopPostureSeverity.Information;

    private static (string State, string Reason) ScheduleState(ScheduleDefinition definition, ScheduleState state, DateTimeOffset observedAtUtc)
    {
        if (state.PendingDelivery is
            {
                Phase: SchedulePendingDeliveryPhase.ResultObserved,
                Result.Kind: ScheduleDeliveryResultKind.Ambiguous
            })
        {
            return ("needs-review", "schedule-delivery-outcome-ambiguous");
        }
        if (!definition.Enabled || !state.Enabled)
        {
            return ("disabled", "schedule-disabled");
        }
        if (state.LastClockObservedAtUtc is { } lastClock && observedAtUtc < lastClock)
        {
            return ("blocked", "schedule-clock-rollback");
        }
        if (state.PendingDelivery is { } pending)
        {
            return ("running", pending.Phase switch
            {
                SchedulePendingDeliveryPhase.Claimed => "schedule-occurrence-claimed",
                SchedulePendingDeliveryPhase.Prepared => "schedule-delivery-prepared",
                SchedulePendingDeliveryPhase.ResultObserved => "schedule-delivery-result-observed",
                _ => "schedule-pending"
            });
        }
        if (state.DeferredOccurrence is not null)
        {
            return ("waiting", "schedule-overlap-deferred");
        }
        if (state.NextOccurrence is null)
        {
            return ("completed", "schedule-exhausted");
        }
        return state.NextOccurrence.ScheduledAtUtc <= observedAtUtc ? ("due", "schedule-due") : ("waiting", "schedule-not-due");
    }

    private static GovernedLoopPostureSeverity ScheduleSeverity(string state)
        => state == "needs-review" ? GovernedLoopPostureSeverity.Critical
            : state == "blocked" ? GovernedLoopPostureSeverity.Warning
            : state is "due" or "running" ? GovernedLoopPostureSeverity.Attention
            : GovernedLoopPostureSeverity.Information;

    private static string StateHash(ScheduleState state)
    {
        if (!ScheduleContractHash.TryComputeState(state, out var hash, out _))
        {
            throw new InvalidOperationException("Validated schedule evidence became unhashable during projection.");
        }
        return hash!;
    }

    private static string PendingPhase(SchedulePendingDeliveryPhase phase) => phase switch
    {
        SchedulePendingDeliveryPhase.Claimed => "claimed",
        SchedulePendingDeliveryPhase.Prepared => "prepared",
        SchedulePendingDeliveryPhase.ResultObserved => "result-observed",
        _ => "unknown"
    };

    private static (string State, string Reason) WakeState(GovernedLoopWakeDisposition disposition) => disposition switch
    {
        GovernedLoopWakeDisposition.Prepared => ("running", "wake-continuation-prepared"),
        GovernedLoopWakeDisposition.Committed => ("completed", "wake-continued"),
        GovernedLoopWakeDisposition.Duplicate => ("completed", "wake-duplicate"),
        GovernedLoopWakeDisposition.Late => ("failed", "wake-late"),
        GovernedLoopWakeDisposition.Stale => ("failed", "wake-stale"),
        GovernedLoopWakeDisposition.Conflict => ("failed", "wake-conflict"),
        GovernedLoopWakeDisposition.Expired => ("expired", "wake-expired"),
        GovernedLoopWakeDisposition.Cancelled => ("cancelled", "wake-cancelled"),
        GovernedLoopWakeDisposition.ReviewBlocked => ("needs-review", "wake-review-blocked"),
        GovernedLoopWakeDisposition.AmbiguousAttempt => ("needs-review", "wake-ambiguous-attempt"),
        GovernedLoopWakeDisposition.Paused => ("waiting", "wake-paused"),
        GovernedLoopWakeDisposition.Failed => ("failed", "wake-failed"),
        _ => ("failed", "wake-unknown")
    };

    private static GovernedLoopPostureSeverity WakeSeverity(string state)
        => state is "needs-review" or "failed" ? GovernedLoopPostureSeverity.Critical
            : state is "due" or "running" ? GovernedLoopPostureSeverity.Attention
            : GovernedLoopPostureSeverity.Information;

    private static GovernedLoopPostureSeverity RunSeverity(CustomLoopRunStatus status)
        => status == CustomLoopRunStatus.NeedsReview ? GovernedLoopPostureSeverity.Critical
            : status == CustomLoopRunStatus.Failed ? GovernedLoopPostureSeverity.Warning
            : status is CustomLoopRunStatus.Running or CustomLoopRunStatus.Waiting or CustomLoopRunStatus.PauseRequested or CustomLoopRunStatus.CancelRequested
                ? GovernedLoopPostureSeverity.Attention
                : GovernedLoopPostureSeverity.Information;

    private static string Token<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        var token = new System.Text.StringBuilder(text.Length + 4);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsUpper(character) && index > 0)
            {
                token.Append('-');
            }
            token.Append(char.ToLowerInvariant(character));
        }
        return token.ToString();
    }

    private static GovernedLoopOperationalPostureResult Result(GovernedLoopOperationalPostureReadStatus status, string reasonCode)
        => new(status, null, reasonCode);
}
