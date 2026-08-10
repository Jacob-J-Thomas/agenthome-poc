using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

internal static class GovernedLoopRevisionStoreSnapshotGuard
{
    internal static bool TryCaptureAtGeneration(
        GovernedLoopRevisionStoreSnapshot? candidate,
        string expectedGraphId,
        long storeGeneration,
        out GovernedLoopRevisionStoreSnapshot? snapshot)
    {
        snapshot = null;
        if (storeGeneration <= 0
            || !TryCapture(candidate, expectedGraphId, out var captured)
            || captured!.Operations.Count > storeGeneration)
        {
            return false;
        }

        snapshot = captured;
        return true;
    }

    internal static bool TryCapture(
        GovernedLoopRevisionStoreSnapshot? candidate,
        string expectedGraphId,
        out GovernedLoopRevisionStoreSnapshot? snapshot)
    {
        snapshot = null;
        if (candidate is null
            || !CustomLoopArtifactIdentifier.IsValid(expectedGraphId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            || !GovernedLoopRevisionContractValidator.Validate(candidate.Head).IsValid
            || !string.Equals(candidate.Head.GraphId, expectedGraphId, StringComparison.Ordinal)
            || !TrySnapshot(candidate.Artifacts, GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph, out var artifacts)
            || !TrySnapshot(candidate.Operations, GovernedLoopRevisionContractLimits.MaxOperationsPerGraph, out var operations))
        {
            return false;
        }

        if (artifacts.Count == 0 || operations.Count == 0)
        {
            return false;
        }

        var artifactsByRevisionId = new Dictionary<string, GovernedLoopRevisionArtifact>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!GovernedLoopRevisionContractValidator.Validate(artifact).IsValid
                || !string.Equals(artifact.Revision.GraphId, expectedGraphId, StringComparison.Ordinal)
                || !artifactsByRevisionId.TryAdd(artifact.Revision.RevisionId, artifact))
            {
                return false;
            }
        }

        var operationsById = new Dictionary<string, GovernedLoopRevisionOperationEvidence>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (!GovernedLoopRevisionContractValidator.Validate(operation).IsValid
                || !EvidenceBelongsToGraph(operation, expectedGraphId)
                || !operationsById.TryAdd(operation.OperationId, operation))
            {
                return false;
            }
        }

        if (!ValidateArtifactCreationEvidence(artifacts, operationsById)
            || !ValidateAppendOrder(artifacts, operations, candidate.Head)
            || !ValidateLifecycleHeads(candidate.Head, artifactsByRevisionId, operationsById)
            || !ValidateRollbackPublicationProofs(artifacts, operations))
        {
            return false;
        }

        snapshot = new GovernedLoopRevisionStoreSnapshot(
            candidate.Head,
            Array.AsReadOnly(artifacts.ToArray()),
            Array.AsReadOnly(operations.ToArray()));
        return true;
    }

    internal static bool HasPublicationProof(
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations,
        GovernedLoopRevisionPublicationPin publication)
        => HasPublicationProofBefore(operations, publication, operations.Count);

    private static bool HasPublicationProofBefore(
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations,
        GovernedLoopRevisionPublicationPin publication,
        int exclusiveEndIndex)
    {
        for (var index = 0; index < exclusiveEndIndex; index++)
        {
            var operation = operations[index];
            if (operation.Outcome != GovernedLoopRevisionOperationOutcome.Committed
                || operation.Kind is not (GovernedLoopRevisionOperationKind.Publish or GovernedLoopRevisionOperationKind.Rollback)
                || !string.Equals(operation.OperationId, publication.PublicationOperationId, StringComparison.Ordinal)
                || !string.Equals(operation.PublicationValidationEvidenceHash, publication.ValidationEvidenceHash, StringComparison.Ordinal)
                || !Equals(operation.ResultHead?.PublishedRevision, publication))
            {
                continue;
            }

            var publishedRevision = operation.Kind == GovernedLoopRevisionOperationKind.Publish
                ? operation.TargetRevision
                : operation.CandidateRevision;
            if (SameRevision(publishedRevision, publication.Revision))
            {
                return true;
            }
        }

        return false;
    }

    internal static GovernedLoopRevisionArtifact? FindArtifact(
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        GovernedLoopRevisionReference revision)
        => artifacts.FirstOrDefault(artifact => SameRevision(artifact.Revision, revision));

    internal static bool SameRevision(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
        => left is not null && right is not null
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);

    private static bool ValidateArtifactCreationEvidence(
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyDictionary<string, GovernedLoopRevisionOperationEvidence> operations)
    {
        foreach (var artifact in artifacts)
        {
            if (!operations.TryGetValue(artifact.CreationOperationId, out var operation)
                || operation.Outcome != GovernedLoopRevisionOperationOutcome.Committed
                || operation.Kind is not (GovernedLoopRevisionOperationKind.CreateDraft
                    or GovernedLoopRevisionOperationKind.ReplaceDraft
                    or GovernedLoopRevisionOperationKind.Rollback)
                || !SameRevision(operation.CandidateRevision, artifact.Revision)
                || !string.Equals(operation.ActorId, artifact.CreatedByActorId, StringComparison.Ordinal)
                || operation.RecordedAtUtc != artifact.CreatedAtUtc
                || !Equals(operation.RollbackSourcePublication, artifact.RollbackSourcePublication)
                || operation.Kind == GovernedLoopRevisionOperationKind.CreateDraft && (artifact.PredecessorRevision is not null || operation.TargetRevision is not null)
                || operation.Kind is GovernedLoopRevisionOperationKind.ReplaceDraft or GovernedLoopRevisionOperationKind.Rollback
                    && !SameRevision(artifact.PredecessorRevision, operation.TargetRevision))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateAppendOrder(
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations,
        GovernedLoopRevisionLifecycleHead expectedHead)
    {
        GovernedLoopRevisionLifecycleHead? projection = null;
        var artifactIndex = 0;
        foreach (var operation in operations)
        {
            if (operation.Outcome == GovernedLoopRevisionOperationOutcome.OutcomeUnknown)
            {
                return false;
            }

            if (!Equals(operation.PreviousHead, projection))
            {
                return false;
            }

            if (operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed)
            {
                projection = operation.ResultHead;
                if (operation.Kind is GovernedLoopRevisionOperationKind.CreateDraft
                    or GovernedLoopRevisionOperationKind.ReplaceDraft
                    or GovernedLoopRevisionOperationKind.Rollback)
                {
                    if (artifactIndex >= artifacts.Count
                        || !string.Equals(artifacts[artifactIndex].CreationOperationId, operation.OperationId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    artifactIndex++;
                }
            }
            else if (!Equals(operation.ResultHead, projection))
            {
                return false;
            }
        }

        return artifactIndex == artifacts.Count && Equals(projection, expectedHead);
    }

    private static bool ValidateLifecycleHeads(
        GovernedLoopRevisionLifecycleHead head,
        IReadOnlyDictionary<string, GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyDictionary<string, GovernedLoopRevisionOperationEvidence> operations)
    {
        if (head.DraftRevision is not null
            && (!artifacts.TryGetValue(head.DraftRevision.RevisionId, out var draft)
                || !SameRevision(draft.Revision, head.DraftRevision)))
        {
            return false;
        }

        if (head.PublishedRevision is { } publication
            && (!artifacts.TryGetValue(publication.Revision.RevisionId, out var published)
                || !SameRevision(published.Revision, publication.Revision)
                || !HasPublicationProof(operations.Values.ToArray(), publication)))
        {
            return false;
        }

        return operations.TryGetValue(head.LastOperationId, out var headOperation)
            && headOperation.Outcome == GovernedLoopRevisionOperationOutcome.Committed
            && Equals(headOperation.ResultHead, head);
    }

    private static bool ValidateRollbackPublicationProofs(
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations)
    {
        var operationIndexes = operations
            .Select((operation, index) => (operation.OperationId, Index: index))
            .ToDictionary(item => item.OperationId, item => item.Index, StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (artifact.RollbackSourcePublication is { } source
                && (!operationIndexes.TryGetValue(artifact.CreationOperationId, out var creationIndex)
                    || !HasPublicationProofBefore(operations, source, creationIndex)))
            {
                return false;
            }
        }

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Kind == GovernedLoopRevisionOperationKind.Rollback
                && operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed
                && operation.RollbackSourcePublication is { } source
                && !HasPublicationProofBefore(operations, source, index))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool EvidenceBelongsToGraph(GovernedLoopRevisionOperationEvidence evidence, string graphId)
    {
        var observedGraphId = evidence.PreviousHead?.GraphId
            ?? evidence.ResultHead?.GraphId
            ?? evidence.CandidateRevision?.GraphId
            ?? evidence.TargetRevision?.GraphId
            ?? evidence.RollbackSourcePublication?.Revision.GraphId;
        return string.Equals(observedGraphId, graphId, StringComparison.Ordinal);
    }

    private static bool TrySnapshot<T>(IEnumerable<T>? source, int limit, out IReadOnlyList<T> snapshot)
    {
        snapshot = Array.Empty<T>();
        if (source is null)
        {
            return false;
        }

        try
        {
            var captured = new List<T>();
            foreach (var item in source.Take(limit + 1))
            {
                if (item is null || captured.Count == limit)
                {
                    return false;
                }

                captured.Add(item);
            }

            snapshot = Array.AsReadOnly(captured.ToArray());
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }
}
