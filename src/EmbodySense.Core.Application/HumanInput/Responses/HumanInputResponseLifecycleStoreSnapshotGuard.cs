using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

internal static class HumanInputResponseLifecycleStoreSnapshotGuard
{
    internal static bool TryCapture(
        HumanInputResponseLifecycleStoreSnapshot? source,
        string expectedRequestId,
        out HumanInputResponseLifecycleStoreSnapshot? snapshot)
        => TryCapture(source, expectedRequestId, null, out snapshot);

    internal static bool TryCapture(
        HumanInputResponseLifecycleStoreSnapshot? source,
        HumanInputRequestReference expectedResponseRequest,
        out HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        if (expectedResponseRequest is null)
        {
            snapshot = null;
            return false;
        }
        return TryCapture(source, expectedResponseRequest.RequestId, expectedResponseRequest, out snapshot);
    }

    private static bool TryCapture(
        HumanInputResponseLifecycleStoreSnapshot? source,
        string expectedRequestId,
        HumanInputRequestReference? expectedResponseRequest,
        out HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            if (source is null
                || source.Request is not { } requestSource
                || source.ResponseRequest is null
                || source.Responses is not { } responseSource
                || source.Operations is not { } operationSource)
            {
                return false;
            }
            if (!HumanInput.Lifecycle.HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(requestSource, expectedRequestId, out var request)
                || request is null
                || !HumanInputRequestLifecycleValidator.ValidateReference(source.ResponseRequest).IsValid
                || expectedResponseRequest is not null && !Equals(source.ResponseRequest, expectedResponseRequest)
                || !TryFindResponseRequest(request, source.ResponseRequest, out var responseRequest)
                || responseRequest is null)
            {
                return false;
            }

            var rawResponses = responseSource.Take(HumanInputResponseContractLimits.MaxResponsesPerRequest + 1).ToArray();
            var rawOperations = operationSource.Take(HumanInputResponseContractLimits.MaxOperationsPerRequest + 1).ToArray();
            if (rawResponses.Length > HumanInputResponseContractLimits.MaxResponsesPerRequest
                || rawOperations.Length > HumanInputResponseContractLimits.MaxOperationsPerRequest)
            {
                return false;
            }

            var responses = new List<HumanInputResponseArtifact>(rawResponses.Length);
            var responsesById = new Dictionary<string, HumanInputResponseArtifact>(StringComparer.Ordinal);
            foreach (var artifact in rawResponses)
            {
                if (!HumanInputResponseArtifactSnapshot.TryCapture(responseRequest, artifact, out var captured, out _)
                    || captured is null
                    || !responsesById.TryAdd(captured.ResponseId, captured))
                {
                    return false;
                }
                responses.Add(captured);
            }

            var operations = new HumanInputResponseOperationEvidence[rawOperations.Length];
            for (var index = 0; index < rawOperations.Length; index++)
            {
                if (!HumanInputResponseOperationEvidenceSnapshot.TryCapture(rawOperations[index], out var captured, out _)
                    || captured is null)
                {
                    return false;
                }
                operations[index] = captured;
            }
            if (!TryReplay(responseRequest, request, responsesById, operations, out var active)
                || active is null)
            {
                return false;
            }

            HumanInputResponseSelection? selection = null;
            if (source.Selection is not null
                && (!HumanInputResponseSelectionSnapshot.TryCapture(responseRequest, source.Selection, active, out selection, out _)
                    || selection is null))
            {
                return false;
            }
            if (!SelectionMatchesRequestProof(request, source.ResponseRequest, operations, selection))
            {
                return false;
            }

            snapshot = new HumanInputResponseLifecycleStoreSnapshot(
                request,
                source.ResponseRequest,
                Array.AsReadOnly(responses.ToArray()),
                Array.AsReadOnly(operations.ToArray()),
                selection);
            return true;
        }
        catch (Exception)
        {
            snapshot = null;
            return false;
        }
    }

    internal static bool TryGetActiveResponses(
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        out IReadOnlyList<HumanInputResponseArtifact>? active)
    {
        active = null;
        try
        {
            if (!TryFindResponseRequest(snapshot.Request, snapshot.ResponseRequest, out var request) || request is null)
            {
                return false;
            }
            var byId = snapshot.Responses.ToDictionary(response => response.ResponseId, StringComparer.Ordinal);
            return TryReplay(request, snapshot.Request, byId, snapshot.Operations, out active);
        }
        catch (Exception)
        {
            active = null;
            return false;
        }
    }

    internal static HumanInputResponseLifecycleProjection? Project(HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        if (snapshot is null
            || !Equals(snapshot.ResponseRequest, snapshot.Request.Head.CurrentRequest)
            || !TryFindResponseRequest(snapshot.Request, snapshot.ResponseRequest, out var request)
            || request is null
            || !TryGetActiveResponses(snapshot, out var active)
            || active is null)
        {
            return null;
        }
        var withdrawals = snapshot.Operations.Count(operation => operation.Kind == HumanInputResponseOperationKind.Withdraw && operation.Outcome == HumanInputResponseOperationOutcome.Committed);
        return new HumanInputResponseLifecycleProjection(
            HumanInputResponseContractLimits.CurrentSchemaVersion,
            request.RequestId,
            request.RequestVersionId,
            snapshot.Request.Head.LifecycleVersion,
            snapshot.Request.Head.Status,
            snapshot.Responses.Count,
            active.Count,
            withdrawals,
            snapshot.Request.Head.AnswerSelection,
            snapshot.Request.Head.UpdatedAtUtc);
    }

    internal static HumanInputResponseArtifact? FindResponse(
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        HumanInputResponseReference reference)
    {
        try
        {
            return snapshot.Responses.SingleOrDefault(candidate => reference.Matches(FindResponseRequest(snapshot.Request, snapshot.ResponseRequest)!, candidate));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool EvidenceMatchesSnapshot(
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleStoreSnapshot snapshot)
    {
        if (!snapshot.Operations.Any(candidate => Equals(candidate, evidence)))
        {
            return false;
        }
        return evidence.SubmittedResponse is null
            || snapshot.Responses.Any(response => evidence.SubmittedResponse.Matches(FindResponseRequest(snapshot.Request, snapshot.ResponseRequest)!, response));
    }

    internal static bool EvidenceMatchesAbsentExpectedLifecycle(
        HumanInputResponseOperationEvidence evidence,
        HumanInputRequestLifecycleStoreSnapshot lifecycle)
    {
        try
        {
            return FindResponseRequest(lifecycle, evidence.Request) is null
                && evidence.FailureCode is HumanInputResponseOperationFailureCode.StaleResponse
                    or HumanInputResponseOperationFailureCode.RequestTerminal
                && evidence.ActorRoleId is null
                && evidence.PreviousHead is not null
                && ObservedHeadsForRequest(lifecycle).Any(head => Equals(head, evidence.PreviousHead))
                && FindResponseRequest(lifecycle, evidence.PreviousHead.CurrentRequest) is { } observedRequest
                && Equals(evidence.ObservedBinding, observedRequest.Binding);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReplay(
        HumanInputRequest request,
        HumanInputRequestLifecycleStoreSnapshot requestSnapshot,
        IReadOnlyDictionary<string, HumanInputResponseArtifact> responsesById,
        IReadOnlyList<HumanInputResponseOperationEvidence> operations,
        out IReadOnlyList<HumanInputResponseArtifact>? active)
    {
        active = null;
        var activeById = new Dictionary<string, HumanInputResponseArtifact>(StringComparer.Ordinal);
        var activeOrder = new List<string>();
        var claimedResponses = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previousTime = null;
        var selectionSeen = false;
        var observedHeads = ObservedHeadsForRequest(requestSnapshot);
        if (observedHeads.Count == 0)
        {
            return false;
        }
        foreach (var operation in operations)
        {
            if (operation is null
                || !operationIds.Add(operation.OperationId)
                || !Equals(operation.Request, RequestReference(request))
                || !BindingsMatchLifecycleProof(operation, requestSnapshot, request)
                || !RoleMatchesProjectedRequest(operation, request)
                || previousTime is { } retainedTime && operation.RecordedAtUtc < retainedTime
                || selectionSeen && operation.FailureCode != HumanInputResponseOperationFailureCode.RequestTerminal)
            {
                return false;
            }
            previousTime = operation.RecordedAtUtc;

            if (operation.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound)
            {
                return false;
            }
            if (operation.Selection is not null)
            {
                if (!observedHeads.Any(head => Equals(head, operation.PreviousHead)) || !Equals(operation.ResultHead, requestSnapshot.Head))
                {
                    return false;
                }
            }
            else if (!Equals(operation.PreviousHead, operation.ResultHead)
                || !observedHeads.Any(head => Equals(head, operation.PreviousHead)))
            {
                return false;
            }

            if (operation.Outcome == HumanInputResponseOperationOutcome.Committed
                && operation.Kind == HumanInputResponseOperationKind.Submit)
            {
                var reference = operation.SubmittedResponse!;
                if (!responsesById.TryGetValue(reference.ResponseId, out var artifact)
                    || !reference.Matches(request, artifact)
                    || !claimedResponses.Add(reference.ResponseId)
                    || activeById.Values.Any(existing => existing.ActorId.Equals(artifact.ActorId)))
                {
                    return false;
                }
                activeById.Add(reference.ResponseId, artifact);
                activeOrder.Add(reference.ResponseId);
                var activeAfterAppend = activeOrder.Select(id => activeById[id]).ToArray();
                if (!HumanInputResponseAutomaticPolicyDecision.TryEvaluate(
                        request,
                        operation.OperationId,
                        operation.RecordedAtUtc,
                        activeAfterAppend,
                        out var requiredSelection)
                    || (requiredSelection is null) != (operation.Selection is null)
                    || requiredSelection is not null && !operation.Selection!.Matches(requiredSelection))
                {
                    return false;
                }
            }
            else if (operation.Outcome == HumanInputResponseOperationOutcome.Committed
                && operation.Kind == HumanInputResponseOperationKind.Withdraw)
            {
                var target = operation.TargetResponses[0];
                if (!activeById.Remove(target.ResponseId, out var removed) || !target.Matches(request, removed))
                {
                    return false;
                }
                activeOrder.Remove(target.ResponseId);
            }

            if (operation.Selection is not null)
            {
                selectionSeen = true;
                if (!Equals(requestSnapshot.AnswerOperation, operation))
                {
                    return false;
                }
            }
        }

        if (claimedResponses.Count != responsesById.Count)
        {
            return false;
        }
        active = Array.AsReadOnly(activeOrder.Select(id => activeById[id]).ToArray());
        return true;
    }

    private static bool BindingsMatchLifecycleProof(
        HumanInputResponseOperationEvidence operation,
        HumanInputRequestLifecycleStoreSnapshot lifecycle,
        HumanInputRequest projectedRequest)
    {
        if (operation.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound
            || operation.PreviousHead is null)
        {
            return false;
        }
        var observedRequest = FindResponseRequest(lifecycle, operation.PreviousHead.CurrentRequest);
        if (observedRequest is null || !Equals(operation.ObservedBinding, observedRequest.Binding))
        {
            return false;
        }
        if (operation.FailureCode == HumanInputResponseOperationFailureCode.RequestTerminal)
        {
            return true;
        }
        if (operation.FailureCode != HumanInputResponseOperationFailureCode.StaleResponse)
        {
            return Equals(operation.ExpectedBinding, projectedRequest.Binding)
                && Equals(operation.ObservedBinding, projectedRequest.Binding);
        }
        return Equals(operation.PreviousHead.CurrentRequest, operation.Request)
            ? !Equals(operation.ExpectedBinding, projectedRequest.Binding)
                && Equals(operation.ObservedBinding, projectedRequest.Binding)
            : Equals(operation.ExpectedBinding, projectedRequest.Binding);
    }

    private static bool RoleMatchesProjectedRequest(
        HumanInputResponseOperationEvidence operation,
        HumanInputRequest projectedRequest)
        => operation.ActorRoleId is null
            || projectedRequest.EligibleRespondents.Any(respondent => respondent is not null
                && string.Equals(respondent.RespondentId, operation.ActorId.Value, StringComparison.Ordinal)
                && string.Equals(respondent.RespondentRoleId, operation.ActorRoleId, StringComparison.Ordinal));

    private static IReadOnlyList<HumanInputRequestLifecycleHead> ObservedHeadsForRequest(
        HumanInputRequestLifecycleStoreSnapshot snapshot)
    {
        var heads = new List<HumanInputRequestLifecycleHead>();
        foreach (var operation in snapshot.Operations)
        {
            RetainPending(operation.PreviousHead);
            RetainPending(operation.ResultHead);
            RetainPending(operation.RelatedPreviousHead);
            RetainPending(operation.RelatedResultHead);
        }
        RetainPending(snapshot.Head);
        RetainPending(snapshot.AnswerOperation?.PreviousHead);
        return Array.AsReadOnly(heads.ToArray());

        void RetainPending(HumanInputRequestLifecycleHead? head)
        {
            if (head is not null
                && string.Equals(head.RequestId, snapshot.Head.RequestId, StringComparison.Ordinal)
                && FindResponseRequest(snapshot, head.CurrentRequest) is not null
                && !heads.Any(candidate => Equals(candidate, head)))
            {
                heads.Add(head);
            }
        }
    }

    private static bool SelectionMatchesRequestProof(
        HumanInputRequestLifecycleStoreSnapshot request,
        HumanInputRequestReference responseRequest,
        IReadOnlyList<HumanInputResponseOperationEvidence> operations,
        HumanInputResponseSelection? selection)
    {
        if (Equals(request.Head.CurrentRequest, responseRequest)
            && request.Head.Status == HumanInputRequestLifecycleStatus.Answered)
        {
            return selection is not null
                && request.AnswerOperation is { } answer
                && operations.Any(operation => Equals(operation, answer))
                && answer.Selection is not null
                && answer.Selection.Matches(selection)
                && Equals(request.Head.AnswerSelection, answer.Selection);
        }
        if (Equals(request.Head.CurrentRequest, responseRequest))
        {
            return selection is null
                && request.Head.Status != HumanInputRequestLifecycleStatus.Answered
                && request.AnswerOperation is null
                && request.Head.AnswerSelection is null;
        }
        return selection is null;
    }

    private static bool TryFindResponseRequest(
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        HumanInputRequestReference reference,
        out HumanInputRequest? request)
    {
        request = FindResponseRequest(snapshot, reference);
        return request is not null;
    }

    private static HumanInputRequest? FindResponseRequest(
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        HumanInputRequestReference reference)
    {
        try
        {
            return snapshot.RequestVersions.SingleOrDefault(reference.Matches);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HumanInputRequestReference RequestReference(HumanInputRequest request)
        => new(HumanInputResponseContractLimits.CurrentSchemaVersion, request.RequestId, request.RequestVersionId, request.RequestHash);
}
