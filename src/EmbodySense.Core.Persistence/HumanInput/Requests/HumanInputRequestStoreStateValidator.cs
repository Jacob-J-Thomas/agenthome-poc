using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests;

internal static class HumanInputRequestStoreStateValidator
{
    public static bool Validate(
        HumanInputRequestStoreDocument? document,
        string workspaceIdentity,
        HumanInputRequestStoreOptions options)
    {
        if (document is null
            || document.SchemaVersion != HumanInputRequestStoreDocument.CurrentSchemaVersion
            || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || document.Generation is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || document.RequestVersions is null
            || document.Heads is null
            || document.Operations is null
            || document.Generation != document.Operations.Count
            || document.RequestVersions.Count > options.MaxRequestVersions
            || document.Heads.Count > options.MaxRequests
            || document.Operations.Count > options.MaxOperations
            || !document.Heads.Select(head => head.RequestId).SequenceEqual(
                document.Heads.Select(head => head.RequestId).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            return false;
        }

        var requestsByReference = new Dictionary<string, HumanInputRequest>(StringComparer.Ordinal);
        var requestVersionIdentities = new HashSet<string>(StringComparer.Ordinal);
        var requestHashesByVersionIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        var versionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var request in document.RequestVersions.Take(options.MaxRequestVersions + 1))
        {
            if (!HumanInputRequestSnapshot.TryCapture(request, out var snapshot, out _)
                || snapshot is null
                || !requestVersionIdentities.Add(VersionIdentityKey(snapshot))
                || !TryRetainVersionIdentity(requestHashesByVersionIdentity, snapshot.RequestId, snapshot.RequestVersionId, snapshot.RequestHash)
                || !requestsByReference.TryAdd(ReferenceKey(snapshot), snapshot))
            {
                return false;
            }

            var count = versionCounts.GetValueOrDefault(snapshot.RequestId) + 1;
            if (count > HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest)
            {
                return false;
            }

            versionCounts[snapshot.RequestId] = count;
        }

        var retainedHeads = new Dictionary<string, HumanInputRequestLifecycleHead>(StringComparer.Ordinal);
        foreach (var head in document.Heads.Take(options.MaxRequests + 1))
        {
            if (!HumanInputRequestLifecycleValidator.ValidateHead(head).IsValid
                || !retainedHeads.TryAdd(head.RequestId, head)
                || !requestsByReference.ContainsKey(ReferenceKey(head.CurrentRequest)))
            {
                return false;
            }
        }

        var currentHeads = new Dictionary<string, HumanInputRequestLifecycleHead>(StringComparer.Ordinal);
        var operationsById = new HashSet<string>(StringComparer.Ordinal);
        var operationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var claimedVersions = new List<HumanInputRequest>();
        var claimedVersionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in document.Operations.Take(options.MaxOperations + 1))
        {
            if (!HumanInputRequestLifecycleValidator.ValidateEvidence(operation).IsValid
                || !operationsById.Add(operation.OperationId)
                || !IncrementOperationCount(operation.TargetRequestId, operationCounts)
                || operation.CandidateRequest is { } candidate
                    && !TryRetainVersionIdentity(requestHashesByVersionIdentity, candidate.RequestId, candidate.RequestVersionId, candidate.RequestHash))
            {
                return false;
            }

            currentHeads.TryGetValue(operation.TargetRequestId, out var currentTarget);
            if (!Equals(operation.PreviousHead, currentTarget)
                || currentTarget is not null && operation.RecordedAtUtc < currentTarget.UpdatedAtUtc)
            {
                return false;
            }

            HumanInputRequestLifecycleHead? currentRelated = null;
            if (operation.RelatedRequestId is { } relatedRequestId)
            {
                if (!IncrementOperationCount(relatedRequestId, operationCounts))
                {
                    return false;
                }

                currentHeads.TryGetValue(relatedRequestId, out currentRelated);
                if (!Equals(operation.RelatedPreviousHead, currentRelated)
                    || currentRelated is not null && operation.RecordedAtUtc < currentRelated.UpdatedAtUtc)
                {
                    return false;
                }
            }

            if (operation.Outcome != HumanInputRequestLifecycleOperationOutcome.Committed)
            {
                if (!Equals(operation.ResultHead, currentTarget)
                    || !Equals(operation.RelatedResultHead, currentRelated))
                {
                    return false;
                }

                continue;
            }

            var previousRequest = currentTarget is null
                ? null
                : FindRequest(requestsByReference, currentTarget.CurrentRequest);
            HumanInputRequest? candidateRequest = null;
            if (operation.CandidateRequest is { } candidateReference)
            {
                candidateRequest = FindRequest(requestsByReference, candidateReference);
                if (candidateRequest is null || !claimedVersionKeys.Add(ReferenceKey(candidateReference)))
                {
                    return false;
                }

                claimedVersions.Add(candidateRequest);
            }

            if (!HumanInputRequestLifecycleValidator.ValidateCommittedTransition(operation, previousRequest, candidateRequest).IsValid
                || operation.ResultHead is null)
            {
                return false;
            }

            currentHeads[operation.TargetRequestId] = operation.ResultHead;
            if (operation.RelatedRequestId is { } related && operation.RelatedResultHead is { } relatedResult)
            {
                currentHeads[related] = relatedResult;
            }
        }

        if (claimedVersions.Count != document.RequestVersions.Count
            || !claimedVersions.Select(ReferenceKey).SequenceEqual(document.RequestVersions.Select(ReferenceKey), StringComparer.Ordinal)
            || currentHeads.Count != retainedHeads.Count
            || currentHeads.Any(pair => !retainedHeads.TryGetValue(pair.Key, out var head) || !Equals(pair.Value, head)))
        {
            return false;
        }

        foreach (var head in retainedHeads.Values)
        {
            if (head.SupersededByRequestId is { } successor
                && (!retainedHeads.TryGetValue(successor, out var successorHead)
                    || !string.Equals(successorHead.SupersedesRequestId, head.RequestId, StringComparison.Ordinal))
                || head.SupersedesRequestId is { } predecessor
                    && (!retainedHeads.TryGetValue(predecessor, out var predecessorHead)
                        || !string.Equals(predecessorHead.SupersededByRequestId, head.RequestId, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsDirectSuccessor(
        HumanInputRequestStoreDocument current,
        HumanInputRequestStoreDocument candidate)
    {
        if (current.Generation is < 0 or >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || candidate.Generation is < 1 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || candidate.Generation != current.Generation + 1
            || !string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            || candidate.Operations.Count != current.Operations.Count + 1
            || !candidate.Operations.Take(current.Operations.Count).SequenceEqual(current.Operations)
            || candidate.RequestVersions.Count < current.RequestVersions.Count
            || candidate.RequestVersions.Count > current.RequestVersions.Count + 1
            || !candidate.RequestVersions.Take(current.RequestVersions.Count).Select(ReferenceKey)
                .SequenceEqual(current.RequestVersions.Select(ReferenceKey), StringComparer.Ordinal))
        {
            return false;
        }

        var appended = candidate.Operations[^1];
        var appendsRequest = appended.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
            && appended.Kind is HumanInputRequestLifecycleOperationKind.Create
                or HumanInputRequestLifecycleOperationKind.Reroute
                or HumanInputRequestLifecycleOperationKind.Amend
                or HumanInputRequestLifecycleOperationKind.Supersede;
        if (appendsRequest != (candidate.RequestVersions.Count == current.RequestVersions.Count + 1)
            || appendsRequest && (appended.CandidateRequest is null
                || !appended.CandidateRequest.Matches(candidate.RequestVersions[^1])))
        {
            return false;
        }

        var currentHeads = current.Heads.ToDictionary(head => head.RequestId, StringComparer.Ordinal);
        var candidateHeads = candidate.Heads.ToDictionary(head => head.RequestId, StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        if (appended.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed)
        {
            affected.Add(appended.TargetRequestId);
            if (appended.RelatedRequestId is { } related)
            {
                affected.Add(related);
            }

            if (appended.ResultHead is null
                || !candidateHeads.TryGetValue(appended.TargetRequestId, out var target)
                || !Equals(target, appended.ResultHead)
                || appended.RelatedRequestId is { } relatedRequestId
                    && (appended.RelatedResultHead is null
                        || !candidateHeads.TryGetValue(relatedRequestId, out var relatedHead)
                        || !Equals(relatedHead, appended.RelatedResultHead)))
            {
                return false;
            }
        }

        return currentHeads.Where(pair => !affected.Contains(pair.Key)).All(
                pair => candidateHeads.TryGetValue(pair.Key, out var unchanged) && Equals(pair.Value, unchanged))
            && candidateHeads.Where(pair => !affected.Contains(pair.Key)).All(
                pair => currentHeads.TryGetValue(pair.Key, out var unchanged) && Equals(pair.Value, unchanged));
    }

    private static bool IncrementOperationCount(string requestId, IDictionary<string, int> counts)
    {
        _ = counts.TryGetValue(requestId, out var previous);
        var count = previous + 1;
        if (count > HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest)
        {
            return false;
        }

        counts[requestId] = count;
        return true;
    }

    private static HumanInputRequest? FindRequest(
        IReadOnlyDictionary<string, HumanInputRequest> requests,
        HumanInputRequestReference reference)
        => requests.TryGetValue(ReferenceKey(reference), out var request) && reference.Matches(request)
            ? request
            : null;

    private static string ReferenceKey(HumanInputRequest request)
        => request.RequestId + "\n" + request.RequestVersionId + "\n" + request.RequestHash;

    private static string VersionIdentityKey(HumanInputRequest request)
        => request.RequestId + "\n" + request.RequestVersionId;

    private static bool TryRetainVersionIdentity(
        IDictionary<string, string> hashesByIdentity,
        string requestId,
        string requestVersionId,
        string requestHash)
    {
        var identity = requestId + "\n" + requestVersionId;
        if (hashesByIdentity.TryGetValue(identity, out var retainedHash))
        {
            return string.Equals(retainedHash, requestHash, StringComparison.Ordinal);
        }

        hashesByIdentity.Add(identity, requestHash);
        return true;
    }

    private static string ReferenceKey(HumanInputRequestReference reference)
        => reference.RequestId + "\n" + reference.RequestVersionId + "\n" + reference.RequestHash;
}
