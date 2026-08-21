using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

internal static class GovernedLoopSleepContractTestFixture
{
    internal static readonly DateTimeOffset PublishedAtUtc = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);

    internal static string Hash(char value) => new(value, GovernedLoopSleepContractLimits.Sha256HexCharacters);

    internal static GovernedLoopSleepBinding Binding(
        string runId = "run-1",
        long executionGeneration = 1,
        long frontierVersion = 7,
        string? frontierHash = null,
        int activationOrdinal = 3,
        string? cycleId = null,
        int? cycleIteration = null,
        string nodeId = "wait-node",
        int nodeVisitOrdinal = 1,
        int waitAttempt = 1,
        string waitOperationId = "wait-operation-1",
        string publicationOperationId = "publication-operation-1",
        string? publicationEvidenceHash = null)
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('a'));
        var execution = GovernedLoopExecutionBinding.Create(1, runId, revision, executionGeneration);
        var publication = new GovernedLoopRevisionPublicationPin(
            1,
            revision,
            publicationOperationId,
            publicationEvidenceHash ?? Hash('b'));
        return new GovernedLoopSleepBinding(
            execution,
            publication,
            frontierVersion,
            frontierHash ?? Hash('c'),
            activationOrdinal,
            cycleId,
            cycleIteration,
            nodeId,
            nodeVisitOrdinal,
            waitAttempt,
            waitOperationId);
    }

    internal static GovernedLoopSleepCheckpoint TimestampCheckpoint(
        GovernedLoopSleepBinding? binding = null,
        DateTimeOffset? deadlineUtc = null,
        DateTimeOffset? publishedAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            1,
            string.Empty,
            binding ?? Binding(),
            GovernedLoopWakeMode.Timestamp,
            deadlineUtc ?? PublishedAtUtc.AddHours(1),
            null,
            publishedAtUtc ?? PublishedAtUtc,
            string.Empty));

    internal static GovernedLoopSleepCheckpoint EventCheckpoint(
        string eventReference = "authenticated-event-1",
        GovernedLoopSleepBinding? binding = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            1,
            string.Empty,
            binding ?? Binding(),
            GovernedLoopWakeMode.AuthenticatedEvent,
            null,
            eventReference,
            PublishedAtUtc,
            string.Empty));

    internal static GovernedLoopWakeIdentity WakeIdentity(
        GovernedLoopSleepCheckpoint checkpoint,
        string? authenticationEvidenceHash = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            1,
            string.Empty,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            checkpoint.WakeMode,
            checkpoint.AuthenticatedEventReference,
            checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent
                ? authenticationEvidenceHash ?? Hash('d')
                : null,
            string.Empty));

    internal static GovernedLoopWakeEvidence WakeEvidence(
        GovernedLoopWakeDisposition disposition = GovernedLoopWakeDisposition.Prepared,
        long evidenceVersion = 1,
        GovernedLoopWakeIdentity? identity = null,
        string? continuationOperationId = null,
        string? continuationEvidenceHash = null,
        string? dispositionEvidenceReference = null,
        DateTimeOffset? recordedAtUtc = null)
    {
        var shape = disposition switch
        {
            GovernedLoopWakeDisposition.Prepared => (continuationOperationId ?? "continuation-operation-1", (string?)null, (string?)null),
            GovernedLoopWakeDisposition.Committed => (continuationOperationId ?? "continuation-operation-1", continuationEvidenceHash ?? Hash('e'), (string?)null),
            GovernedLoopWakeDisposition.AmbiguousAttempt => (continuationOperationId ?? "continuation-operation-1", (string?)null, dispositionEvidenceReference ?? "ambiguity-evidence-1"),
            GovernedLoopWakeDisposition.Failed => (continuationOperationId, (string?)null, dispositionEvidenceReference ?? "failure-evidence-1"),
            _ => ((string?)null, (string?)null, dispositionEvidenceReference ?? "disposition-evidence-1")
        };
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            1,
            evidenceVersion,
            identity ?? WakeIdentity(TimestampCheckpoint()),
            disposition,
            shape.Item1,
            shape.Item2,
            shape.Item3,
            recordedAtUtc ?? PublishedAtUtc.AddHours(1),
            string.Empty));
    }

    internal static GovernedLoopCoordinatorOwnership Ownership(
        string coordinatorId = "background-coordinator",
        string ownerId = "process-owner-1",
        long ownershipEpoch = 1,
        DateTimeOffset? acquiredAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            1,
            coordinatorId,
            ownerId,
            ownershipEpoch,
            acquiredAtUtc ?? PublishedAtUtc,
            string.Empty));

    internal static GovernedLoopCoordinatorLifecycle Lifecycle(
        GovernedLoopCoordinatorStatus status,
        long version = 1,
        GovernedLoopCoordinatorOwnership? ownership = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        var updated = updatedAtUtc ?? PublishedAtUtc.AddMinutes(version);
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
            1,
            version,
            ownership ?? Ownership(),
            status,
            updated,
            status is GovernedLoopCoordinatorStatus.Stopped or GovernedLoopCoordinatorStatus.Failed ? updated : null,
            string.Empty));
    }

    internal static GovernedLoopCoordinatorHeartbeat Heartbeat(
        long sequence = 1,
        GovernedLoopCoordinatorOwnership? ownership = null,
        DateTimeOffset? recordedAtUtc = null,
        DateTimeOffset? leaseExpiresAtUtc = null)
    {
        var recorded = recordedAtUtc ?? PublishedAtUtc.AddMinutes(sequence);
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
            1,
            sequence,
            ownership ?? Ownership(),
            recorded,
            leaseExpiresAtUtc ?? recorded.AddMinutes(1),
            string.Empty));
    }

    internal static GovernedLoopCoordinatorFailure Failure(
        long sequence = 1,
        GovernedLoopCoordinatorOwnership? ownership = null,
        GovernedLoopCoordinatorFailureKind kind = GovernedLoopCoordinatorFailureKind.StoreUnavailable,
        string? detailEvidenceReference = "coordinator-failure-1",
        DateTimeOffset? occurredAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorFailure(
            1,
            sequence,
            ownership ?? Ownership(),
            kind,
            detailEvidenceReference,
            occurredAtUtc ?? PublishedAtUtc.AddMinutes(sequence),
            string.Empty));
}
