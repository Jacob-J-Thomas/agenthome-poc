using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

internal static class GovernedLoopWaitContractCopy
{
    internal static GovernedLoopWaitCondition Copy(GovernedLoopWaitCondition? value)
        => value is null
            ? null!
            : new GovernedLoopWaitCondition(
                value.SchemaVersion,
                value.Descriptor is null ? null! : value.Descriptor with { },
                value.ParameterKind,
                value.WakeDeadlineUtc,
                value.AuthenticatedEventReference,
                value.ContentHash);

    internal static GovernedLoopSleepCheckpoint Copy(GovernedLoopSleepCheckpoint? value)
        => value is null
            ? null!
            : new GovernedLoopSleepCheckpoint(
                value.SchemaVersion,
                value.CheckpointId,
                value.Binding,
                value.WakeMode,
                value.WakeDeadlineUtc,
                value.AuthenticatedEventReference,
                value.PublishedAtUtc,
                value.ContentHash);

    internal static GovernedLoopWakeIdentity Copy(GovernedLoopWakeIdentity? value)
        => value is null
            ? null!
            : new GovernedLoopWakeIdentity(
                value.SchemaVersion,
                value.WakeId,
                value.CheckpointId,
                value.CheckpointHash,
                value.WakeMode,
                value.AuthenticatedEventReference,
                value.AuthenticationEvidenceHash,
                value.ContentHash);

    internal static GovernedLoopWakeEvidence Copy(GovernedLoopWakeEvidence? value)
        => value is null
            ? null!
            : new GovernedLoopWakeEvidence(
                value.SchemaVersion,
                value.EvidenceVersion,
                Copy(value.Identity),
                value.Disposition,
                value.ContinuationOperationId,
                value.ContinuationEvidenceHash,
                value.DispositionEvidenceReference,
                value.RecordedAtUtc,
                value.ContentHash);

    internal static GovernedLoopWaitParkEvidence? Copy(GovernedLoopWaitParkEvidence? value)
        => value is null
            ? null
            : new GovernedLoopWaitParkEvidence(
                value.SchemaVersion,
                Copy(value.Condition),
                Copy(value.Checkpoint),
                value.ParkedAtUtc,
                value.ContentHash);

    internal static GovernedLoopWaitContinuationEvidence? Copy(GovernedLoopWaitContinuationEvidence? value)
        => value is null
            ? null
            : new GovernedLoopWaitContinuationEvidence(
                value.SchemaVersion,
                value.ParkEvidenceHash,
                Copy(value.PreparedWakeEvidence),
                value.PreResumeFrontierVersion,
                value.PreResumeFrontierHash,
                value.ResumedFrontierVersion,
                value.ResumedFrontierHash,
                value.ResumedAtUtc,
                value.ContentHash);

    internal static GovernedLoopWaitExecutionEvidence Copy(GovernedLoopWaitExecutionEvidence? value)
        => value is null
            ? null!
            : new GovernedLoopWaitExecutionEvidence(
                value.SchemaVersion,
                value.ActivationOrdinal,
                value.NodeId,
                value.NodeVisitOrdinal,
                value.CycleId,
                value.CycleIteration,
                value.WaitAttempt,
                value.WaitOperationId,
                Copy(value.Condition),
                value.ParkedAtUtc,
                value.ParkedFrontierVersion,
                value.ParkedFrontierHash,
                Copy(value.ParkEvidence),
                Copy(value.ContinuationEvidence),
                value.ContentHash);
}
