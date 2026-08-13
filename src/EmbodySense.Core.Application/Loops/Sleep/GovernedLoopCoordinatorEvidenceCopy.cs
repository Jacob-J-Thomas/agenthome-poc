using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

internal static class GovernedLoopCoordinatorEvidenceCopy
{
    internal static GovernedLoopCoordinatorOwnership Ownership(GovernedLoopCoordinatorOwnership? value)
        => value is null ? null! : value with { };

    internal static GovernedLoopCoordinatorLifecycle Lifecycle(GovernedLoopCoordinatorLifecycle? value)
        => value is null
            ? null!
            : new(
            value.SchemaVersion,
            value.LifecycleVersion,
            Ownership(value.Ownership),
            value.Status,
            value.UpdatedAtUtc,
            value.TerminalAtUtc,
            value.ContentHash);

    internal static GovernedLoopCoordinatorHeartbeat Heartbeat(GovernedLoopCoordinatorHeartbeat? value)
        => value is null
            ? null!
            : new(
            value.SchemaVersion,
            value.HeartbeatSequence,
            Ownership(value.Ownership),
            value.RecordedAtUtc,
            value.LeaseExpiresAtUtc,
            value.ContentHash);

    internal static GovernedLoopCoordinatorFailure Failure(GovernedLoopCoordinatorFailure? value)
        => value is null
            ? null!
            : new(
            value.SchemaVersion,
            value.FailureSequence,
            Ownership(value.Ownership),
            value.Kind,
            value.DetailEvidenceReference,
            value.OccurredAtUtc,
            value.ContentHash);

    internal static GovernedLoopCoordinatorSnapshot? Snapshot(GovernedLoopCoordinatorSnapshot? value)
        => value is null
            ? null
            : new GovernedLoopCoordinatorSnapshot(
                value.Ownership,
                value.LatestLifecycle,
                value.LatestHeartbeat,
                value.LatestFailureSequence,
                value.LatestFailureHash);
}
