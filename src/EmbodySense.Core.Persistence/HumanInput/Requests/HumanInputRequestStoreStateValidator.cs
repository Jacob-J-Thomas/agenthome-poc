using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests;

internal static class HumanInputRequestStoreStateValidator
{
    public static bool Validate(
        HumanInputRequestStoreDocument? document,
        string workspaceIdentity,
        HumanInputRequestStoreOptions options)
    {
        try
        {
            return ValidateCore(document, workspaceIdentity, options);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ValidateCore(
        HumanInputRequestStoreDocument? document,
        string workspaceIdentity,
        HumanInputRequestStoreOptions options)
    {
        if (!HasValidDocumentShape(document, workspaceIdentity, options))
        {
            return false;
        }

        var requestsByReference = new Dictionary<string, HumanInputRequest>(StringComparer.Ordinal);
        var requestVersionIdentities = new HashSet<string>(StringComparer.Ordinal);
        var requestHashesByVersionIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        var versionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var request in document!.RequestVersions.Take(options.MaxRequestVersions + 1))
        {
            if (!HumanInputRequestSnapshot.TryCapture(request, out var snapshot, out _)
                || snapshot is null
                || !requestVersionIdentities.Add(VersionIdentityKey(snapshot))
                || !TryRetainVersionIdentity(requestHashesByVersionIdentity, snapshot.RequestId, snapshot.RequestVersionId, snapshot.RequestHash)
                || !requestsByReference.TryAdd(RequestReferenceKey(snapshot), snapshot))
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

        var responsesByReference = new Dictionary<string, HumanInputResponseArtifact>(StringComparer.Ordinal);
        var responseIdentities = new HashSet<string>(StringComparer.Ordinal);
        var responseCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var response in document.ResponseArtifacts.Take(options.MaxResponseArtifacts + 1))
        {
            var request = FindRequest(requestsByReference, response?.Request);
            if (request is null
                || !HumanInputResponseArtifactSnapshot.TryCapture(request, response, out var snapshot, out _)
                || snapshot is null
                || !responseIdentities.Add(ResponseIdentityKey(snapshot))
                || !responsesByReference.TryAdd(ResponseReferenceKey(snapshot), snapshot))
            {
                return false;
            }

            var responseRequestKey = RequestReferenceKey(snapshot.Request);
            var count = responseCounts.GetValueOrDefault(responseRequestKey) + 1;
            if (count > HumanInputResponseContractLimits.MaxResponsesPerRequest)
            {
                return false;
            }
            responseCounts[responseRequestKey] = count;
        }

        var selectionsByReference = new Dictionary<string, HumanInputResponseSelection>(StringComparer.Ordinal);
        var selectionIds = new HashSet<string>(StringComparer.Ordinal);
        var selectionRequestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in document.Selections.Take(options.MaxSelections + 1))
        {
            if (selection is null
                || !HumanInputResponseContractValidator.ValidateSelectionReference(HumanInputResponseSelectionReference.Create(selection)).IsValid
                || !HumanInputResponseSelectionHash.Matches(selection)
                || !selectionIds.Add(selection.SelectionId)
                || !selectionRequestIds.Add(selection.Request.RequestId)
                || !selectionsByReference.TryAdd(SelectionReferenceKey(selection), selection)
                || FindRequest(requestsByReference, selection.Request) is null)
            {
                return false;
            }
        }

        var retainedHeads = new Dictionary<string, HumanInputRequestLifecycleHead>(StringComparer.Ordinal);
        foreach (var head in document.Heads.Take(options.MaxRequests + 1))
        {
            if (!HumanInputRequestLifecycleValidator.ValidateHead(head).IsValid
                || !retainedHeads.TryAdd(head.RequestId, head)
                || !requestsByReference.ContainsKey(RequestReferenceKey(head.CurrentRequest)))
            {
                return false;
            }
        }

        var currentHeads = new Dictionary<string, HumanInputRequestLifecycleHead>(StringComparer.Ordinal);
        var operationsById = new HashSet<string>(StringComparer.Ordinal);
        var lifecycleOperationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var responseOperationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var responseOperationTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var claimedVersions = new List<HumanInputRequest>();
        var claimedVersionKeys = new HashSet<string>(StringComparer.Ordinal);
        var claimedResponses = new List<HumanInputResponseArtifact>();
        var claimedResponseKeys = new HashSet<string>(StringComparer.Ordinal);
        var claimedSelections = new List<HumanInputResponseSelection>();
        var claimedSelectionKeys = new HashSet<string>(StringComparer.Ordinal);
        var activeResponses = new Dictionary<string, List<HumanInputResponseArtifact>>(StringComparer.Ordinal);

        foreach (var envelope in document.Operations.Take(options.MaxOperations + 1))
        {
            if (!ValidateEnvelope(envelope)
                || !operationsById.Add(envelope.OperationId))
            {
                return false;
            }

            if (envelope.RequestLifecycle is { } lifecycle)
            {
                if (!ReplayLifecycleOperation(
                        lifecycle,
                        requestsByReference,
                        requestHashesByVersionIdentity,
                        currentHeads,
                        lifecycleOperationCounts,
                        options.MaxLifecycleOperationsPerRequest,
                        claimedVersions,
                        claimedVersionKeys))
                {
                    return false;
                }
            }
            else if (!ReplayResponseOperation(
                         envelope.ResponseLifecycle!,
                         requestsByReference,
                         claimedVersionKeys,
                         requestHashesByVersionIdentity,
                         responsesByReference,
                         selectionsByReference,
                         currentHeads,
                         responseOperationCounts,
                         options.MaxResponseOperationsPerRequest,
                         responseOperationTimes,
                         activeResponses,
                         claimedResponses,
                         claimedResponseKeys,
                         claimedSelections,
                         claimedSelectionKeys))
            {
                return false;
            }
        }

        if (claimedVersions.Count != document.RequestVersions.Count
            || !claimedVersions.Select(RequestReferenceKey).SequenceEqual(document.RequestVersions.Select(RequestReferenceKey), StringComparer.Ordinal)
            || claimedResponses.Count != document.ResponseArtifacts.Count
            || !claimedResponses.Select(ResponseReferenceKey).SequenceEqual(document.ResponseArtifacts.Select(ResponseReferenceKey), StringComparer.Ordinal)
            || claimedSelections.Count != document.Selections.Count
            || !claimedSelections.Select(SelectionReferenceKey).SequenceEqual(document.Selections.Select(SelectionReferenceKey), StringComparer.Ordinal)
            || currentHeads.Count != retainedHeads.Count
            || currentHeads.Any(pair => !retainedHeads.TryGetValue(pair.Key, out var head) || !Equals(pair.Value, head)))
        {
            return false;
        }

        if (!ValidateResponseCausality(document))
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

            var retainedSelection = document.Selections.SingleOrDefault(selection => string.Equals(selection.Request.RequestId, head.RequestId, StringComparison.Ordinal));
            if (head.Status == HumanInputRequestLifecycleStatus.Answered
                ? retainedSelection is null || head.AnswerSelection is null || !head.AnswerSelection.Matches(retainedSelection)
                : retainedSelection is not null || head.AnswerSelection is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateResponseCausality(HumanInputRequestStoreDocument document)
    {
        var exactSnapshots = new Dictionary<string, HumanInputResponseLifecycleStoreSnapshot?>(StringComparer.Ordinal);
        var currentSnapshots = new Dictionary<string, HumanInputResponseLifecycleStoreSnapshot?>(StringComparer.Ordinal);
        var observations = new List<HumanInputResponseOperationCausalityObservation>(document.Operations.Count);
        foreach (var envelope in document.Operations)
        {
            if (envelope.ResponseLifecycle is not { } operation)
            {
                continue;
            }

            HumanInputResponseLifecycleStoreSnapshot? snapshot = null;
            if (operation.FailureCode != HumanInputResponseOperationFailureCode.RequestNotFound)
            {
                var requestKey = RequestReferenceKey(operation.Request);
                if (!exactSnapshots.TryGetValue(requestKey, out snapshot))
                {
                    snapshot = HumanInputRequestStore.ResponseSnapshot(document, operation.Request);
                    exactSnapshots.Add(requestKey, snapshot);
                }
            }
            if (snapshot is null
                && operation.FailureCode is HumanInputResponseOperationFailureCode.StaleResponse
                    or HumanInputResponseOperationFailureCode.RequestTerminal)
            {
                if (!currentSnapshots.TryGetValue(operation.Request.RequestId, out snapshot))
                {
                    snapshot = HumanInputRequestStore.ResponseSnapshot(document, operation.Request.RequestId);
                    currentSnapshots.Add(operation.Request.RequestId, snapshot);
                }
            }

            observations.Add(new HumanInputResponseOperationCausalityObservation(operation, snapshot));
        }
        return HumanInputResponseOperationCausality.MatchesChronology(observations);
    }

    public static bool IsDirectSuccessor(
        HumanInputRequestStoreDocument current,
        HumanInputRequestStoreDocument candidate)
    {
        if (current.Generation is < 0 or >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || candidate.Generation is < 1 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || candidate.Generation != current.Generation + 1
            || candidate.SchemaVersion != current.SchemaVersion
            || !string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            || candidate.Operations.Count != current.Operations.Count + 1
            || !OperationPrefixesEqual(current.Operations, candidate.Operations)
            || !RequestPrefixEqual(current.RequestVersions, candidate.RequestVersions)
            || !ResponsePrefixEqual(current.ResponseArtifacts, candidate.ResponseArtifacts)
            || !SelectionPrefixEqual(current.Selections, candidate.Selections))
        {
            return false;
        }

        var appended = candidate.Operations[^1];
        if (appended.RequestLifecycle is { } lifecycle)
        {
            return IsLifecycleSuccessor(current, candidate, lifecycle);
        }
        return appended.ResponseLifecycle is { } response
            && IsResponseSuccessor(current, candidate, response);
    }

    private static bool HasValidDocumentShape(
        HumanInputRequestStoreDocument? document,
        string workspaceIdentity,
        HumanInputRequestStoreOptions options)
        => document is not null
            && document.SchemaVersion == HumanInputRequestStoreDocument.CurrentSchemaVersion
            && string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            && document.Generation is >= 0 and <= HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            && document.RequestVersions is not null
            && document.Heads is not null
            && document.ResponseArtifacts is not null
            && document.Selections is not null
            && document.Operations is not null
            && document.Generation == document.Operations.Count
            && document.RequestVersions.Count <= options.MaxRequestVersions
            && document.Heads.Count <= options.MaxRequests
            && document.ResponseArtifacts.Count <= options.MaxResponseArtifacts
            && document.Selections.Count <= options.MaxSelections
            && document.Operations.Count <= options.MaxOperations
            && document.Heads.Select(head => head.RequestId).SequenceEqual(
                document.Heads.Select(head => head.RequestId).Order(StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static bool ValidateEnvelope(HumanInputRequestStoreOperationDocument? envelope)
    {
        if (envelope is null
            || envelope.SchemaVersion != HumanInputRequestStoreOperationDocument.CurrentSchemaVersion
            || !HumanInputIdentifier.IsValid(envelope.OperationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters))
        {
            return false;
        }

        return envelope.Family switch
        {
            HumanInputRequestStoreOperationFamily.RequestLifecycle => envelope.RequestLifecycle is { } lifecycle
                && envelope.ResponseLifecycle is null
                && string.Equals(envelope.OperationId, lifecycle.OperationId, StringComparison.Ordinal)
                && HumanInputRequestLifecycleValidator.ValidateEvidence(lifecycle).IsValid,
            HumanInputRequestStoreOperationFamily.ResponseLifecycle => envelope.RequestLifecycle is null
                && envelope.ResponseLifecycle is { } response
                && string.Equals(envelope.OperationId, response.OperationId, StringComparison.Ordinal)
                && HumanInputResponseContractValidator.ValidateEvidence(response).IsValid
                && HumanInputResponseEligibilityEvidenceHash.Matches(response),
            _ => false,
        };
    }

    private static bool ReplayLifecycleOperation(
        HumanInputRequestLifecycleOperationEvidence operation,
        IReadOnlyDictionary<string, HumanInputRequest> requestsByReference,
        IDictionary<string, string> requestHashesByVersionIdentity,
        IDictionary<string, HumanInputRequestLifecycleHead> currentHeads,
        IDictionary<string, int> operationCounts,
        int maxOperationCount,
        ICollection<HumanInputRequest> claimedVersions,
        ISet<string> claimedVersionKeys)
    {
        if (!IncrementOperationCount(operation.TargetRequestId, operationCounts, maxOperationCount)
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
            if (!IncrementOperationCount(relatedRequestId, operationCounts, maxOperationCount))
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
            return Equals(operation.ResultHead, currentTarget)
                && Equals(operation.RelatedResultHead, currentRelated);
        }

        var previousRequest = currentTarget is null ? null : FindRequest(requestsByReference, currentTarget.CurrentRequest);
        HumanInputRequest? candidateRequest = null;
        if (operation.CandidateRequest is { } candidateReference)
        {
            candidateRequest = FindRequest(requestsByReference, candidateReference);
            var key = RequestReferenceKey(candidateReference);
            if (candidateRequest is null || !claimedVersionKeys.Add(key))
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
        return true;
    }

    private static bool ReplayResponseOperation(
        HumanInputResponseOperationEvidence operation,
        IReadOnlyDictionary<string, HumanInputRequest> requestsByReference,
        ISet<string> claimedVersionKeys,
        IDictionary<string, string> requestHashesByVersionIdentity,
        IReadOnlyDictionary<string, HumanInputResponseArtifact> responsesByReference,
        IReadOnlyDictionary<string, HumanInputResponseSelection> selectionsByReference,
        IDictionary<string, HumanInputRequestLifecycleHead> currentHeads,
        IDictionary<string, int> operationCounts,
        int maxOperationCount,
        IDictionary<string, DateTimeOffset> operationTimes,
        IDictionary<string, List<HumanInputResponseArtifact>> activeResponses,
        ICollection<HumanInputResponseArtifact> claimedResponses,
        ISet<string> claimedResponseKeys,
        ICollection<HumanInputResponseSelection> claimedSelections,
        ISet<string> claimedSelectionKeys)
    {
        var requestId = operation.Request.RequestId;
        var responseStateKey = RequestReferenceKey(operation.Request);
        var requestWasClaimed = claimedVersionKeys.Contains(responseStateKey);
        if (requestWasClaimed
            && (!IncrementOperationCount(responseStateKey, operationCounts, maxOperationCount)
                || operationTimes.TryGetValue(responseStateKey, out var previousOperationTime)
                    && operation.RecordedAtUtc < previousOperationTime
                || !TryRetainVersionIdentity(
                    requestHashesByVersionIdentity,
                    operation.Request.RequestId,
                    operation.Request.RequestVersionId,
                    operation.Request.RequestHash)))
        {
            return false;
        }
        if (requestWasClaimed)
        {
            operationTimes[responseStateKey] = operation.RecordedAtUtc;
        }

        currentHeads.TryGetValue(requestId, out var currentHead);
        if (!Equals(operation.PreviousHead, currentHead)
            || currentHead is not null && operation.RecordedAtUtc < currentHead.UpdatedAtUtc)
        {
            return false;
        }

        var observedRequest = currentHead is null
            ? null
            : FindRequest(requestsByReference, currentHead.CurrentRequest);
        if (currentHead is null
            ? operation.ObservedBinding is not null
            : observedRequest is null || !Equals(operation.ObservedBinding, observedRequest.Binding))
        {
            return false;
        }

        var request = requestWasClaimed
            ? FindRequest(requestsByReference, operation.Request)
            : null;
        if (operation.ActorRoleId is { } actorRoleId
            && (request is null
                || !request.EligibleRespondents.Any(respondent =>
                    string.Equals(respondent.RespondentId, operation.ActorId.Value, StringComparison.Ordinal)
                    && string.Equals(respondent.RespondentRoleId, actorRoleId, StringComparison.Ordinal))))
        {
            return false;
        }

        if (request is null)
        {
            var requestNotFound = operation.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound
                && operation.Outcome == HumanInputResponseOperationOutcome.NotFound
                && currentHead is null
                && operation.ResultHead is null;
            var staleReference = operation.FailureCode == HumanInputResponseOperationFailureCode.StaleResponse
                && operation.Outcome == HumanInputResponseOperationOutcome.Conflict
                && currentHead is not null
                && Equals(operation.ResultHead, currentHead);
            var terminalReference = operation.FailureCode == HumanInputResponseOperationFailureCode.RequestTerminal
                && operation.Outcome == HumanInputResponseOperationOutcome.Rejected
                && currentHead is not null
                && currentHead.Status != HumanInputRequestLifecycleStatus.Pending
                && Equals(operation.ResultHead, currentHead);
            return (requestNotFound || staleReference || terminalReference)
                && operation.SubmittedResponse is null
                && operation.Selection is null;
        }

        if (operation.AttemptedResponse is { } attemptedResponse
            && attemptedResponse.PrivacyClass != request.PrivacyClass)
        {
            return false;
        }

        if (operation.Outcome != HumanInputResponseOperationOutcome.Committed)
        {
            if (operation.FailureCode == HumanInputResponseOperationFailureCode.IneligibleRespondent
                && !ProvesIneligibleRespondent(request, operation, responsesByReference))
            {
                return false;
            }

            if (operation.FailureCode == HumanInputResponseOperationFailureCode.IneligibleSelector)
            {
                var eligibleRespondent = request.EligibleRespondents.SingleOrDefault(respondent =>
                    string.Equals(respondent.RespondentId, operation.ActorId.Value, StringComparison.Ordinal));
                if (eligibleRespondent is not null
                    && request.ResponsePolicy.Kind == HumanInputResponsePolicyKind.ManualSelection
                    && request.ResponsePolicy.OrderedRoleIds is { } selectorRoles
                    && selectorRoles.Contains(eligibleRespondent.RespondentRoleId, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return Equals(operation.ResultHead, currentHead)
                && operation.SubmittedResponse is null
                && operation.Selection is null;
        }

        if (currentHead is null || !Equals(currentHead.CurrentRequest, operation.Request))
        {
            return false;
        }

        if (!activeResponses.TryGetValue(responseStateKey, out var active))
        {
            active = [];
            activeResponses.Add(responseStateKey, active);
        }

        HumanInputResponseSelection? requiredAutomaticSelection = null;
        if (operation.Kind == HumanInputResponseOperationKind.Submit)
        {
            if (operation.SubmittedResponse is not { } submitted
                || !responsesByReference.TryGetValue(ResponseReferenceKey(submitted), out var artifact)
                || !submitted.Matches(request, artifact)
                || artifact.SubmittedAtUtc != operation.RecordedAtUtc
                || !artifact.ActorId.Equals(operation.ActorId)
                || !string.Equals(artifact.RespondentRoleId, operation.ActorRoleId, StringComparison.Ordinal)
                || active.Any(existing => existing.ActorId.Equals(artifact.ActorId)
                    || string.Equals(existing.ResponseId, artifact.ResponseId, StringComparison.Ordinal))
                || !claimedResponseKeys.Add(ResponseReferenceKey(artifact)))
            {
                return false;
            }
            active.Add(artifact);
            claimedResponses.Add(artifact);
            if (!HumanInputResponseAutomaticPolicyDecision.TryEvaluate(
                    request,
                    operation.OperationId,
                    operation.RecordedAtUtc,
                    active,
                    out requiredAutomaticSelection))
            {
                return false;
            }
        }
        else if (operation.Kind == HumanInputResponseOperationKind.Withdraw)
        {
            var target = operation.TargetResponses[0];
            var index = active.FindIndex(artifact => target.Matches(request, artifact));
            if (index < 0
                || !active[index].ActorId.Equals(operation.ActorId)
                || !string.Equals(active[index].RespondentRoleId, operation.ActorRoleId, StringComparison.Ordinal))
            {
                return false;
            }
            active.RemoveAt(index);
        }

        if (operation.Selection is null)
        {
            return requiredAutomaticSelection is null
                && Equals(operation.ResultHead, currentHead);
        }

        if (!selectionsByReference.TryGetValue(SelectionReferenceKey(operation.Selection), out var selection)
            || !operation.Selection.Matches(selection)
            || selection.SelectedAtUtc != operation.RecordedAtUtc
            || !HumanInputResponseSelectionSnapshot.TryCapture(request, selection, active, out var capturedSelection, out _)
            || capturedSelection is null
            || !string.Equals(selection.SelectionId, operation.OperationId, StringComparison.Ordinal)
            || !claimedSelectionKeys.Add(SelectionReferenceKey(selection))
            || operation.Kind == HumanInputResponseOperationKind.Submit
                && (requiredAutomaticSelection is null
                    || !ResponseSelectionEquals(selection, requiredAutomaticSelection))
            || operation.Kind == HumanInputResponseOperationKind.Select
                && !operation.TargetResponses.SequenceEqual(selection.Responses)
            || operation.Kind == HumanInputResponseOperationKind.Select
                && (!Equals(selection.SelectorActorId, operation.ActorId)
                    || !string.Equals(selection.SelectorRoleId, operation.ActorRoleId, StringComparison.Ordinal))
            || operation.ResultHead is null)
        {
            return false;
        }

        claimedSelections.Add(selection);
        currentHeads[requestId] = operation.ResultHead;
        return true;
    }

    private static bool ProvesIneligibleRespondent(
        HumanInputRequest request,
        HumanInputResponseOperationEvidence operation,
        IReadOnlyDictionary<string, HumanInputResponseArtifact> responsesByReference)
    {
        var actorIsEligible = request.EligibleRespondents.Any(respondent =>
            string.Equals(respondent.RespondentId, operation.ActorId.Value, StringComparison.Ordinal));
        if (!actorIsEligible)
        {
            return true;
        }

        if (operation.Kind != HumanInputResponseOperationKind.Withdraw
            || operation.TargetResponses.Length != 1
            || !responsesByReference.TryGetValue(ResponseReferenceKey(operation.TargetResponses[0]), out var retainedResponse))
        {
            return false;
        }

        return operation.TargetResponses[0].Matches(request, retainedResponse)
            && !retainedResponse.ActorId.Equals(operation.ActorId);
    }

    private static bool IsLifecycleSuccessor(
        HumanInputRequestStoreDocument current,
        HumanInputRequestStoreDocument candidate,
        HumanInputRequestLifecycleOperationEvidence appended)
    {
        if (candidate.ResponseArtifacts.Count != current.ResponseArtifacts.Count
            || candidate.Selections.Count != current.Selections.Count)
        {
            return false;
        }

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

        var affected = new HashSet<string>(StringComparer.Ordinal);
        if (appended.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed)
        {
            affected.Add(appended.TargetRequestId);
            if (appended.RelatedRequestId is { } related)
            {
                affected.Add(related);
            }
            if (!HeadMatches(candidate, appended.TargetRequestId, appended.ResultHead)
                || appended.RelatedRequestId is { } relatedRequestId
                    && !HeadMatches(candidate, relatedRequestId, appended.RelatedResultHead))
            {
                return false;
            }
        }
        return UnaffectedHeadsEqual(current, candidate, affected);
    }

    private static bool IsResponseSuccessor(
        HumanInputRequestStoreDocument current,
        HumanInputRequestStoreDocument candidate,
        HumanInputResponseOperationEvidence appended)
    {
        if (candidate.RequestVersions.Count != current.RequestVersions.Count)
        {
            return false;
        }

        var appendsResponse = appended.Outcome == HumanInputResponseOperationOutcome.Committed
            && appended.Kind == HumanInputResponseOperationKind.Submit;
        var appendsSelection = appended.Outcome == HumanInputResponseOperationOutcome.Committed
            && appended.Selection is not null;
        if (appendsResponse != (candidate.ResponseArtifacts.Count == current.ResponseArtifacts.Count + 1)
            || appendsSelection != (candidate.Selections.Count == current.Selections.Count + 1)
            || appendsResponse && (appended.SubmittedResponse is null
                || !string.Equals(appended.SubmittedResponse.ResponseHash, candidate.ResponseArtifacts[^1].ResponseHash, StringComparison.Ordinal))
            || appendsSelection && (appended.Selection is null || !appended.Selection.Matches(candidate.Selections[^1])))
        {
            return false;
        }

        var affected = new HashSet<string>(StringComparer.Ordinal);
        if (appendsSelection)
        {
            affected.Add(appended.Request.RequestId);
            if (!HeadMatches(candidate, appended.Request.RequestId, appended.ResultHead))
            {
                return false;
            }
        }
        return UnaffectedHeadsEqual(current, candidate, affected);
    }

    private static bool OperationPrefixesEqual(
        IReadOnlyList<HumanInputRequestStoreOperationDocument> current,
        IReadOnlyList<HumanInputRequestStoreOperationDocument> candidate)
    {
        for (var index = 0; index < current.Count; index++)
        {
            if (!EnvelopeEquals(current[index], candidate[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool EnvelopeEquals(HumanInputRequestStoreOperationDocument left, HumanInputRequestStoreOperationDocument right)
        => left.SchemaVersion == right.SchemaVersion
            && left.Family == right.Family
            && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && Equals(left.RequestLifecycle, right.RequestLifecycle)
            && ResponseEvidenceEquals(left.ResponseLifecycle, right.ResponseLifecycle);

    private static bool ResponseEvidenceEquals(HumanInputResponseOperationEvidence? left, HumanInputResponseOperationEvidence? right)
        => left is null || right is null
            ? left is null && right is null
            : Equals(
                    left with { AttemptedResponse = null, TargetResponses = default },
                    right with { AttemptedResponse = null, TargetResponses = default })
                && ResponseArtifactEquals(left.AttemptedResponse, right.AttemptedResponse)
                && !left.TargetResponses.IsDefault
                && !right.TargetResponses.IsDefault
                && left.TargetResponses.SequenceEqual(right.TargetResponses);

    private static bool ResponseArtifactEquals(
        HumanInputResponseArtifact? left,
        HumanInputResponseArtifact? right)
        => left is null || right is null
            ? left is null && right is null
            : left.SchemaVersion == right.SchemaVersion
                && string.Equals(left.ResponseId, right.ResponseId, StringComparison.Ordinal)
                && Equals(left.Request, right.Request)
                && Equals(left.Binding, right.Binding)
                && left.ActorId.Equals(right.ActorId)
                && string.Equals(left.RespondentRoleId, right.RespondentRoleId, StringComparison.Ordinal)
                && left.SubmittedAtUtc == right.SubmittedAtUtc
                && left.PrivacyClass == right.PrivacyClass
                && ResponseValueEquals(left.Value, right.Value)
                && string.Equals(left.Explanation, right.Explanation, StringComparison.Ordinal)
                && string.Equals(left.ValueHash, right.ValueHash, StringComparison.Ordinal)
                && string.Equals(left.ResponseHash, right.ResponseHash, StringComparison.Ordinal);

    private static bool ResponseValueEquals(HumanInputResponseValue left, HumanInputResponseValue right)
        => left.Kind == right.Kind
            && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
            && string.Equals(left.ChoiceId, right.ChoiceId, StringComparison.Ordinal)
            && left.Confirmation == right.Confirmation
            && NullableSequenceEqual(left.StructuredFields, right.StructuredFields)
            && Equals(left.Reference, right.Reference);

    private static bool NullableSequenceEqual<T>(
        System.Collections.Immutable.ImmutableArray<T>? left,
        System.Collections.Immutable.ImmutableArray<T>? right)
        => left.HasValue == right.HasValue
            && (!left.HasValue
                || !left.Value.IsDefault
                    && !right!.Value.IsDefault
                    && left.Value.SequenceEqual(right.Value));

    private static bool ResponseSelectionEquals(HumanInputResponseSelection left, HumanInputResponseSelection right)
        => Equals(left with { Responses = default }, right with { Responses = default })
            && !left.Responses.IsDefault
            && !right.Responses.IsDefault
            && left.Responses.SequenceEqual(right.Responses);

    private static bool RequestPrefixEqual(IReadOnlyList<HumanInputRequest> current, IReadOnlyList<HumanInputRequest> candidate)
        => candidate.Count >= current.Count
            && candidate.Count <= current.Count + 1
            && candidate.Take(current.Count).Select(RequestReferenceKey)
                .SequenceEqual(current.Select(RequestReferenceKey), StringComparer.Ordinal);

    private static bool ResponsePrefixEqual(IReadOnlyList<HumanInputResponseArtifact> current, IReadOnlyList<HumanInputResponseArtifact> candidate)
        => candidate.Count >= current.Count
            && candidate.Count <= current.Count + 1
            && candidate.Take(current.Count).Select(ResponseReferenceKey)
                .SequenceEqual(current.Select(ResponseReferenceKey), StringComparer.Ordinal);

    private static bool SelectionPrefixEqual(IReadOnlyList<HumanInputResponseSelection> current, IReadOnlyList<HumanInputResponseSelection> candidate)
        => candidate.Count >= current.Count
            && candidate.Count <= current.Count + 1
            && candidate.Take(current.Count).Select(SelectionReferenceKey)
                .SequenceEqual(current.Select(SelectionReferenceKey), StringComparer.Ordinal);

    private static bool HeadMatches(HumanInputRequestStoreDocument document, string requestId, HumanInputRequestLifecycleHead? expected)
        => expected is not null
            && document.Heads.SingleOrDefault(head => string.Equals(head.RequestId, requestId, StringComparison.Ordinal)) is { } actual
            && Equals(actual, expected);

    private static bool UnaffectedHeadsEqual(
        HumanInputRequestStoreDocument current,
        HumanInputRequestStoreDocument candidate,
        ISet<string> affected)
    {
        var currentHeads = current.Heads.ToDictionary(head => head.RequestId, StringComparer.Ordinal);
        var candidateHeads = candidate.Heads.ToDictionary(head => head.RequestId, StringComparer.Ordinal);
        return currentHeads.Where(pair => !affected.Contains(pair.Key)).All(
                pair => candidateHeads.TryGetValue(pair.Key, out var unchanged) && Equals(pair.Value, unchanged))
            && candidateHeads.Where(pair => !affected.Contains(pair.Key)).All(
                pair => currentHeads.TryGetValue(pair.Key, out var unchanged) && Equals(pair.Value, unchanged));
    }

    private static bool IncrementOperationCount(string requestId, IDictionary<string, int> counts, int maximum)
    {
        _ = counts.TryGetValue(requestId, out var previous);
        var count = previous + 1;
        if (count > maximum)
        {
            return false;
        }
        counts[requestId] = count;
        return true;
    }

    private static HumanInputRequest? FindRequest(
        IReadOnlyDictionary<string, HumanInputRequest> requests,
        HumanInputRequestReference? reference)
        => reference is not null
            && requests.TryGetValue(RequestReferenceKey(reference), out var request)
            && reference.Matches(request)
                ? request
                : null;

    private static string RequestReferenceKey(HumanInputRequest request)
        => request.RequestId + "\n" + request.RequestVersionId + "\n" + request.RequestHash;

    private static string RequestReferenceKey(HumanInputRequestReference reference)
        => reference.RequestId + "\n" + reference.RequestVersionId + "\n" + reference.RequestHash;

    private static string VersionIdentityKey(HumanInputRequest request)
        => request.RequestId + "\n" + request.RequestVersionId;

    private static string ResponseIdentityKey(HumanInputResponseArtifact response)
        => RequestReferenceKey(response.Request) + "\n" + response.ResponseId;

    private static string ResponseReferenceKey(HumanInputResponseArtifact response)
        => ResponseIdentityKey(response) + "\n" + response.ValueHash + "\n" + response.ResponseHash;

    private static string ResponseReferenceKey(HumanInputResponseReference response)
        => RequestReferenceKey(response.Request) + "\n" + response.ResponseId + "\n" + response.ValueHash + "\n" + response.ResponseHash;

    private static string SelectionReferenceKey(HumanInputResponseSelection selection)
        => RequestReferenceKey(selection.Request) + "\n" + selection.SelectionId + "\n" + selection.SelectionHash;

    private static string SelectionReferenceKey(HumanInputResponseSelectionReference selection)
        => RequestReferenceKey(selection.Request) + "\n" + selection.SelectionId + "\n" + selection.SelectionHash;

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
