using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Validates one complete lifecycle-store mutation at every persistence boundary.</summary>
public static class GovernedLoopRevisionStoreMutationGuard
{
    /// <summary>Returns whether the mutation is a complete, internally consistent schema-1 persistence command.</summary>
    public static bool IsValid(GovernedLoopRevisionStoreMutation? mutation)
    {
        if (mutation is null
            || !CustomLoopArtifactIdentifier.IsValid(
                mutation.GraphId,
                GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            || mutation.ExpectedStoreGeneration < 0
            || !GovernedLoopRevisionContractValidator.Validate(mutation.Operation).IsValid
            || mutation.Operation.Outcome is GovernedLoopRevisionOperationOutcome.Unknown
                or GovernedLoopRevisionOperationOutcome.OutcomeUnknown
            || !EvidenceTargetsGraph(mutation.Operation, mutation.GraphId))
        {
            return false;
        }

        var committed = mutation.Operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed;
        if (committed != (mutation.HeadToWrite is not null)
            || mutation.HeadToWrite is not null
            && (!GovernedLoopRevisionContractValidator.Validate(mutation.HeadToWrite).IsValid
                || !string.Equals(mutation.HeadToWrite.GraphId, mutation.GraphId, StringComparison.Ordinal)
                || !Equals(mutation.HeadToWrite, mutation.Operation.ResultHead)))
        {
            return false;
        }

        var requiresArtifact = committed
            && mutation.Operation.Kind is (
                GovernedLoopRevisionOperationKind.CreateDraft
                or GovernedLoopRevisionOperationKind.ReplaceDraft
                or GovernedLoopRevisionOperationKind.Rollback);
        if (requiresArtifact != (mutation.ArtifactToAppend is not null))
        {
            return false;
        }

        return mutation.ArtifactToAppend is null
            || ArtifactMatchesOperation(mutation.ArtifactToAppend, mutation.Operation);
    }

    private static bool ArtifactMatchesOperation(
        GovernedLoopRevisionArtifact artifact,
        GovernedLoopRevisionOperationEvidence operation)
    {
        var expectedPredecessor = operation.Kind == GovernedLoopRevisionOperationKind.CreateDraft
            ? null
            : operation.PreviousHead?.DraftRevision ?? operation.PreviousHead?.PublishedRevision?.Revision;
        return GovernedLoopRevisionContractValidator.Validate(artifact).IsValid
            && string.Equals(artifact.CreationOperationId, operation.OperationId, StringComparison.Ordinal)
            && string.Equals(artifact.CreatedByActorId, operation.ActorId, StringComparison.Ordinal)
            && artifact.CreatedAtUtc == operation.RecordedAtUtc
            && Equals(artifact.Revision, operation.CandidateRevision)
            && Equals(artifact.PredecessorRevision, expectedPredecessor)
            && Equals(artifact.RollbackSourcePublication, operation.RollbackSourcePublication)
            && (operation.Kind == GovernedLoopRevisionOperationKind.Rollback)
                == (artifact.RollbackSourcePublication is not null);
    }

    private static bool EvidenceTargetsGraph(
        GovernedLoopRevisionOperationEvidence evidence,
        string graphId)
    {
        var references = new[]
        {
            evidence.PreviousHead?.GraphId,
            evidence.ResultHead?.GraphId,
            evidence.CandidateRevision?.GraphId,
            evidence.TargetRevision?.GraphId,
            evidence.RollbackSourcePublication?.Revision.GraphId,
        };
        return references.Any(value => value is not null)
            && references
                .Where(value => value is not null)
                .All(value => string.Equals(value, graphId, StringComparison.Ordinal));
    }
}
