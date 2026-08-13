using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

internal static class GovernedLoopSleepContractCopy
{
    internal static GovernedLoopExecutionBinding Copy(GovernedLoopExecutionBinding? value)
        => value is null
            ? null!
            : GovernedLoopExecutionBinding.Create(
                value.SchemaVersion,
                value.RunId,
                GovernedLoopRevisionReference.Create(
                    value.Revision.SchemaVersion,
                    value.Revision.GraphId,
                    value.Revision.RevisionId,
                    value.Revision.ExecutableHash),
                value.ExecutionGeneration);

    internal static GovernedLoopRevisionPublicationPin Copy(GovernedLoopRevisionPublicationPin? value)
        => value is null
            ? null!
            : new GovernedLoopRevisionPublicationPin(
                value.SchemaVersion,
                value.Revision is null
                    ? null!
                    : GovernedLoopRevisionReference.Create(
                        value.Revision.SchemaVersion,
                        value.Revision.GraphId,
                        value.Revision.RevisionId,
                        value.Revision.ExecutableHash),
                value.PublicationOperationId,
                value.ValidationEvidenceHash);

    internal static GovernedLoopSleepBinding Copy(GovernedLoopSleepBinding? value)
        => value is null
            ? null!
            : new GovernedLoopSleepBinding(
                value.Execution,
                value.Publication,
                value.FrontierVersion,
                value.FrontierHash,
                value.ActivationOrdinal,
                value.CycleId,
                value.CycleIteration,
                value.NodeId,
                value.NodeVisitOrdinal,
                value.WaitAttempt,
                value.WaitOperationId);

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

    internal static GovernedLoopCoordinatorOwnership Copy(GovernedLoopCoordinatorOwnership? value)
        => value is null
            ? null!
            : new GovernedLoopCoordinatorOwnership(
                value.SchemaVersion,
                value.CoordinatorId,
                value.OwnerId,
                value.OwnershipEpoch,
                value.AcquiredAtUtc,
                value.ContentHash);
}
