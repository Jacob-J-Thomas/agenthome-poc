using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

internal sealed record MutationPlan(
    PlanStatus Status,
    bool CanPersist,
    string OperationId,
    GovernedLoopRevisionOperationKind Kind,
    GovernedLoopRevisionOperationOutcome Outcome,
    GovernedLoopRevisionOperationFailureCode FailureCode,
    GovernedLoopRevisionLifecycleHead? PreviousHead,
    GovernedLoopRevisionLifecycleHead? NextHead,
    GovernedLoopRevisionArtifact? ArtifactToAppend,
    GovernedLoopRevisionArtifact? PublicationArtifact)
{
    internal static MutationPlan Invalid { get; } = new(
        PlanStatus.InvalidStoreState,
        false,
        string.Empty,
        GovernedLoopRevisionOperationKind.Unknown,
        GovernedLoopRevisionOperationOutcome.Unknown,
        GovernedLoopRevisionOperationFailureCode.Unknown,
        null,
        null,
        null,
        null);

    internal static MutationPlan Success(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionLifecycleHead next,
        GovernedLoopRevisionArtifact? artifact,
        GovernedLoopRevisionArtifact? publicationArtifact)
        => new(
            PlanStatus.Ready,
            true,
            request.OperationId,
            request.Kind,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previous,
            next,
            artifact,
            publicationArtifact);

    internal static MutationPlan Failure(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead? previous,
        GovernedLoopRevisionOperationOutcome outcome,
        GovernedLoopRevisionOperationFailureCode failureCode)
        => new(
            PlanStatus.Ready,
            true,
            request.OperationId,
            request.Kind,
            outcome,
            failureCode,
            previous,
            null,
            null,
            null);

    internal static MutationPlan UnpersistableLimit(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead previous,
        GovernedLoopRevisionOperationFailureCode failureCode)
        => Failure(request, previous, GovernedLoopRevisionOperationOutcome.LimitExceeded, failureCode) with { CanPersist = false };
}
