using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

internal static class HumanInputRequestLifecycleStoreSnapshotGuard
{
    internal static bool TryCapture(
        HumanInputRequestLifecycleStoreSnapshot? source,
        string expectedRequestId,
        out HumanInputRequestLifecycleStoreSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            if (source is not
                {
                    Head: { } head,
                    RequestVersions: { } requestVersions,
                    Operations: { } sourceOperationList,
                }
                || !string.Equals(head.RequestId, expectedRequestId, StringComparison.Ordinal)
                || !HumanInputRequestLifecycleValidator.ValidateHead(head).IsValid)
            {
                return false;
            }

            var sourceRequests = requestVersions.Take(HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest + 1).ToArray();
            var sourceOperations = sourceOperationList.Take(HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest + 1).ToArray();
            if (sourceRequests.Length is < 1 or > HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest
                || sourceOperations.Length is < 1 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest)
            {
                return false;
            }

            var requests = new List<HumanInputRequest>(sourceRequests.Length);
            var requestsByKey = new Dictionary<string, HumanInputRequest>(StringComparer.Ordinal);
            var requestVersionIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in sourceRequests)
            {
                if (!HumanInputRequestSnapshot.TryCapture(request, out var captured, out _)
                    || captured is null
                    || !string.Equals(captured.RequestId, expectedRequestId, StringComparison.Ordinal)
                    || !requestVersionIdentities.Add(VersionIdentity(captured))
                    || !requestsByKey.TryAdd(Key(captured), captured))
                {
                    return false;
                }

                requests.Add(captured);
            }

            if (!requestsByKey.TryGetValue(Key(head.CurrentRequest), out var currentArtifact)
                || !head.CurrentRequest.Matches(currentArtifact))
            {
                return false;
            }

            var operations = new List<HumanInputRequestLifecycleOperationEvidence>(sourceOperations.Length);
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            var claimedRequests = new List<string>();
            HumanInputRequestLifecycleHead? current = null;
            foreach (var operation in sourceOperations)
            {
                if (operation is null
                    || !HumanInputRequestLifecycleValidator.ValidateEvidence(operation).IsValid
                    || !operationIds.Add(operation.OperationId))
                {
                    return false;
                }

                var isPrimary = string.Equals(operation.TargetRequestId, expectedRequestId, StringComparison.Ordinal);
                var isRelated = string.Equals(operation.RelatedRequestId, expectedRequestId, StringComparison.Ordinal);
                if (isPrimary == isRelated)
                {
                    return false;
                }

                var previous = isPrimary ? operation.PreviousHead : operation.RelatedPreviousHead;
                var result = isPrimary ? operation.ResultHead : operation.RelatedResultHead;
                if (!Equals(previous, current))
                {
                    return false;
                }

                if (operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed)
                {
                    if (result is null)
                    {
                        return false;
                    }

                    current = result;
                    HumanInputRequest? candidateArtifact = null;
                    if (operation.CandidateRequest is { } candidate
                        && string.Equals(candidate.RequestId, expectedRequestId, StringComparison.Ordinal))
                    {
                        if (!requestsByKey.TryGetValue(Key(candidate), out candidateArtifact) || !candidate.Matches(candidateArtifact))
                        {
                            return false;
                        }

                        claimedRequests.Add(Key(candidate));
                    }

                    var previousArtifact = previous is null
                        ? null
                        : requestsByKey.GetValueOrDefault(Key(previous.CurrentRequest));
                    if (operation.Kind == HumanInputRequestLifecycleOperationKind.Supersede)
                    {
                        if (!ValidateCommittedSupersedeHalf(operation, isPrimary, previousArtifact, candidateArtifact))
                        {
                            return false;
                        }
                    }
                    else if (!HumanInputRequestLifecycleValidator.ValidateCommittedTransition(operation, previousArtifact, candidateArtifact).IsValid)
                    {
                        return false;
                    }
                }
                else if (!Equals(result, current))
                {
                    return false;
                }

                operations.Add(operation);
            }

            if (!Equals(current, head)
                && !TryValidateAnswerOperation(source.AnswerOperation, current, head, currentArtifact, operationIds)
                || Equals(current, head) && source.AnswerOperation is not null
                || claimedRequests.Count != requests.Count
                || !claimedRequests.SequenceEqual(requests.Select(Key), StringComparer.Ordinal))
            {
                return false;
            }

            snapshot = new HumanInputRequestLifecycleStoreSnapshot(
                head,
                Array.AsReadOnly(requests.ToArray()),
                Array.AsReadOnly(operations.ToArray()),
                source.AnswerOperation);
            return true;
        }
        catch (Exception)
        {
            snapshot = null;
            return false;
        }
    }

    internal static bool ValidatePairedSupersede(
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related,
        HumanInputRequestLifecycleOperationEvidence? evidence)
    {
        if (primary is null
            || related is null
            || evidence is null
            || evidence.Kind != HumanInputRequestLifecycleOperationKind.Supersede
            || evidence.Outcome != HumanInputRequestLifecycleOperationOutcome.Committed
            || !string.Equals(primary.Head.RequestId, evidence.TargetRequestId, StringComparison.Ordinal)
            || !string.Equals(related.Head.RequestId, evidence.RelatedRequestId, StringComparison.Ordinal)
            || !ContainsOperation(primary, evidence)
            || !ContainsOperation(related, evidence))
        {
            return false;
        }

        var previous = FindRequest(primary, evidence.PreviousHead?.CurrentRequest);
        var candidate = FindRequest(related, evidence.CandidateRequest);
        return HumanInputRequestLifecycleValidator.ValidateCommittedTransition(evidence, previous, candidate).IsValid;
    }

    internal static IReadOnlyList<string> RequiredSupersedeRequestIds(HumanInputRequestLifecycleStoreSnapshot snapshot)
    {
        try
        {
            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidence in snapshot.Operations)
            {
                if (evidence.Kind != HumanInputRequestLifecycleOperationKind.Supersede
                    || evidence.RelatedRequestId is not { } relatedRequestId)
                {
                    continue;
                }

                if (evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                    || evidence.PreviousHead is not null
                    || evidence.ResultHead is not null)
                {
                    requestIds.Add(evidence.TargetRequestId);
                }
                if (evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                    || evidence.RelatedPreviousHead is not null
                    || evidence.RelatedResultHead is not null)
                {
                    requestIds.Add(relatedRequestId);
                }
            }

            return Array.AsReadOnly(requestIds.ToArray());
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    internal static bool ValidateCommittedSupersedeGraph(
        IReadOnlyDictionary<string, HumanInputRequestLifecycleStoreSnapshot> snapshots)
    {
        try
        {
            foreach (var snapshot in snapshots.Values)
            {
                foreach (var evidence in snapshot.Operations)
                {
                    if (evidence.Kind != HumanInputRequestLifecycleOperationKind.Supersede
                        || evidence.Outcome != HumanInputRequestLifecycleOperationOutcome.Committed)
                    {
                        continue;
                    }

                    if (!snapshots.TryGetValue(evidence.TargetRequestId, out var primary)
                        || evidence.RelatedRequestId is not { } relatedRequestId
                        || !snapshots.TryGetValue(relatedRequestId, out var related)
                        || !ValidatePairedSupersede(primary, related, evidence))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool ValidateOperationOccurrences(
        IReadOnlyDictionary<string, HumanInputRequestLifecycleStoreSnapshot> snapshots,
        long storeGeneration)
    {
        try
        {
            if (storeGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore)
            {
                return false;
            }

            var evidenceById = new Dictionary<string, HumanInputRequestLifecycleOperationEvidence>(StringComparer.Ordinal);
            var answerIds = new HashSet<string>(StringComparer.Ordinal);
            var occurrencesById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var pair in snapshots)
            {
                foreach (var evidence in pair.Value.Operations)
                {
                    if (answerIds.Contains(evidence.OperationId))
                    {
                        return false;
                    }

                    if (evidenceById.TryGetValue(evidence.OperationId, out var retained))
                    {
                        if (evidence.Kind != HumanInputRequestLifecycleOperationKind.Supersede
                            || !Equals(retained, evidence))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        evidenceById.Add(evidence.OperationId, evidence);
                        occurrencesById.Add(evidence.OperationId, new HashSet<string>(StringComparer.Ordinal));
                    }

                    if (!occurrencesById[evidence.OperationId].Add(pair.Key))
                    {
                        return false;
                    }
                }

                if (pair.Value.AnswerOperation is { } answer
                    && (!answerIds.Add(answer.OperationId) || evidenceById.ContainsKey(answer.OperationId)))
                {
                    return false;
                }
            }

            if (evidenceById.Count + answerIds.Count > storeGeneration)
            {
                return false;
            }

            foreach (var pair in evidenceById)
            {
                var evidence = pair.Value;
                var occurrences = occurrencesById[pair.Key];
                if (evidence.Kind != HumanInputRequestLifecycleOperationKind.Supersede
                    && occurrences.Count != 1)
                {
                    return false;
                }

                snapshots.TryGetValue(evidence.TargetRequestId, out var primary);
                HumanInputRequestLifecycleStoreSnapshot? related = null;
                if (evidence.RelatedRequestId is { } relatedRequestId)
                {
                    snapshots.TryGetValue(relatedRequestId, out related);
                }

                if (!EvidenceMatchesSnapshots(evidence, primary, related)
                    || !ExpectedBindingMatchesSnapshots(evidence, primary, related))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryValidateAnswerOperation(
        HumanInputResponseOperationEvidence? answer,
        HumanInputRequestLifecycleHead? previous,
        HumanInputRequestLifecycleHead head,
        HumanInputRequest request,
        ISet<string> requestOperationIds)
    {
        return previous is not null
            && previous.Status == HumanInputRequestLifecycleStatus.Pending
            && head.Status == HumanInputRequestLifecycleStatus.Answered
            && answer is not null
            && !requestOperationIds.Contains(answer.OperationId)
            && HumanInputResponseContractValidator.ValidateEvidence(answer).IsValid
            && answer.Kind is HumanInputResponseOperationKind.Submit or HumanInputResponseOperationKind.Select
            && answer.Outcome == HumanInputResponseOperationOutcome.Committed
            && answer.FailureCode == HumanInputResponseOperationFailureCode.None
            && answer.Selection is not null
            && Equals(answer.PreviousHead, previous)
            && Equals(answer.ResultHead, head)
            && Equals(answer.Request, previous.CurrentRequest)
            && answer.Request.Matches(request)
            && Equals(answer.Binding, request.Binding)
            && answer.ExpectedLifecycleVersion == previous.LifecycleVersion
            && answer.ExpectedLifecycleStatus == previous.Status
            && Equals(head.AnswerSelection, answer.Selection)
            && string.Equals(head.LastOperationId, answer.OperationId, StringComparison.Ordinal)
            && head.UpdatedAtUtc == answer.RecordedAtUtc;
    }

    internal static bool ValidateRequestVersionIdentities(
        IReadOnlyDictionary<string, HumanInputRequestLifecycleStoreSnapshot> snapshots)
    {
        try
        {
            var hashesByIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots.Values)
            {
                foreach (var request in snapshot.RequestVersions)
                {
                    if (!TryRetainVersionIdentity(hashesByIdentity, request.RequestId, request.RequestVersionId, request.RequestHash))
                    {
                        return false;
                    }
                }

                foreach (var evidence in snapshot.Operations)
                {
                    if (evidence.CandidateRequest is { } candidate
                        && !TryRetainVersionIdentity(hashesByIdentity, candidate.RequestId, candidate.RequestVersionId, candidate.RequestHash))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool EvidenceMatchesSnapshots(
        HumanInputRequestLifecycleOperationEvidence evidence,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related)
    {
        try
        {
            var requiresPrimary = evidence.PreviousHead is not null || evidence.ResultHead is not null;
            var requiresRelated = evidence.RelatedPreviousHead is not null || evidence.RelatedResultHead is not null;
            return (!requiresPrimary || primary is not null)
                && (!requiresRelated || related is not null)
                && (primary is null
                    || string.Equals(primary.Head.RequestId, evidence.TargetRequestId, StringComparison.Ordinal)
                    && ContainsOperation(primary, evidence))
                && (related is null
                    || evidence.RelatedRequestId is { } relatedRequestId
                    && string.Equals(related.Head.RequestId, relatedRequestId, StringComparison.Ordinal)
                    && ContainsOperation(related, evidence));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ExpectedBindingMatchesSnapshots(
        HumanInputRequestLifecycleOperationEvidence evidence,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related)
    {
        if (evidence.Kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            return evidence.ExpectedBinding is null;
        }

        if (evidence.ExpectedBinding is not { } expected)
        {
            return false;
        }

        if (evidence.PreviousHead is { } previous)
        {
            var previousRequest = FindRequest(primary, previous.CurrentRequest);
            if (previousRequest is null || !Equals(previousRequest.Binding, expected))
            {
                return false;
            }
        }

        if (evidence.RelatedPreviousHead is { } relatedPrevious)
        {
            var relatedRequest = FindRequest(related, relatedPrevious.CurrentRequest);
            if (relatedRequest is null || !Equals(relatedRequest.Binding, expected))
            {
                return false;
            }
        }

        return true;
    }

    internal static HumanInputRequest? FindRequest(
        HumanInputRequestLifecycleStoreSnapshot? snapshot,
        HumanInputRequestReference? reference)
    {
        if (snapshot is null || reference is null)
        {
            return null;
        }

        try
        {
            return snapshot.RequestVersions.SingleOrDefault(candidate => reference.Matches(candidate));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool ContainsOperation(
        HumanInputRequestLifecycleStoreSnapshot? snapshot,
        HumanInputRequestLifecycleOperationEvidence evidence)
    {
        try
        {
            return snapshot is not null && snapshot.Operations.Any(candidate => Equals(candidate, evidence));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static HumanInputRequestLifecycleProjection Project(HumanInputRequestLifecycleHead head)
        => new(
            head.SchemaVersion,
            head.RequestId,
            head.LifecycleVersion,
            head.Status,
            head.CurrentRequest,
            head.ReminderCount,
            head.SupersedesRequestId,
            head.SupersededByRequestId,
            head.UpdatedAtUtc);

    private static bool ValidateCommittedSupersedeHalf(
        HumanInputRequestLifecycleOperationEvidence evidence,
        bool isPrimary,
        HumanInputRequest? previousArtifact,
        HumanInputRequest? candidateArtifact)
    {
        if (evidence is not
            {
                PreviousHead: { } previous,
                ResultHead: { } result,
                RelatedRequestId: { } relatedRequestId,
                RelatedPreviousHead: null,
                RelatedResultHead: { } relatedResult,
                CandidateRequest: { } candidate,
            }
            || previous.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion
            || previous.Status != HumanInputRequestLifecycleStatus.Pending
            || !string.Equals(previous.RequestId, evidence.TargetRequestId, StringComparison.Ordinal)
            || !string.Equals(candidate.RequestId, relatedRequestId, StringComparison.Ordinal)
            || string.Equals(previous.RequestId, relatedRequestId, StringComparison.Ordinal)
            || result.LifecycleVersion != previous.LifecycleVersion + 1
            || result.Status != HumanInputRequestLifecycleStatus.Superseded
            || !string.Equals(result.RequestId, previous.RequestId, StringComparison.Ordinal)
            || !Equals(result.CurrentRequest, previous.CurrentRequest)
            || result.ReminderCount != previous.ReminderCount
            || !string.Equals(result.SupersedesRequestId, previous.SupersedesRequestId, StringComparison.Ordinal)
            || !string.Equals(result.SupersededByRequestId, relatedRequestId, StringComparison.Ordinal)
            || !string.Equals(result.LastOperationId, evidence.OperationId, StringComparison.Ordinal)
            || result.UpdatedAtUtc != evidence.RecordedAtUtc
            || evidence.RecordedAtUtc < previous.UpdatedAtUtc
            || relatedResult.SchemaVersion != HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion
            || !string.Equals(relatedResult.RequestId, relatedRequestId, StringComparison.Ordinal)
            || relatedResult.LifecycleVersion != 1
            || relatedResult.Status != HumanInputRequestLifecycleStatus.Pending
            || !Equals(relatedResult.CurrentRequest, candidate)
            || relatedResult.ReminderCount != 0
            || !string.Equals(relatedResult.SupersedesRequestId, evidence.TargetRequestId, StringComparison.Ordinal)
            || relatedResult.SupersededByRequestId is not null
            || !string.Equals(relatedResult.LastOperationId, evidence.OperationId, StringComparison.Ordinal)
            || relatedResult.UpdatedAtUtc != evidence.RecordedAtUtc)
        {
            return false;
        }

        return isPrimary
            ? previousArtifact is not null
                && previous.CurrentRequest.Matches(previousArtifact)
                && evidence.RecordedAtUtc <= previousArtifact.Timing.ExpiresAtUtc
            : candidateArtifact is not null
                && candidate.Matches(candidateArtifact)
                && evidence.RecordedAtUtc >= candidateArtifact.Timing.RequestedAtUtc
                && evidence.RecordedAtUtc <= candidateArtifact.Timing.ExpiresAtUtc;
    }

    private static string Key(HumanInputRequest request)
        => request.RequestId + "\n" + request.RequestVersionId + "\n" + request.RequestHash;

    private static string Key(HumanInputRequestReference reference)
        => reference.RequestId + "\n" + reference.RequestVersionId + "\n" + reference.RequestHash;

    private static string VersionIdentity(HumanInputRequest request)
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
}
