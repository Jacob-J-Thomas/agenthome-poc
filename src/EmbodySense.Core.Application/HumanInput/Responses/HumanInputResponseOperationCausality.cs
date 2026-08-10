using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Re-evaluates exact response intent against chronological durable state before retained evidence is trusted.</summary>
public static class HumanInputResponseOperationCausality
{
    /// <summary>Proves that exact retained evidence is the canonical outcome of the supplied command and durable snapshot.</summary>
    /// <param name="command">The exact validated command witness.</param>
    /// <param name="evidence">The retained terminal operation evidence.</param>
    /// <param name="snapshot">The exact response snapshot, or null only for a request-not-found result.</param>
    /// <returns><see langword="true"/> only when re-evaluation produces the exact retained outcome and artifacts.</returns>
    public static bool Matches(
        HumanInputResponseLifecycleCommand? command,
        HumanInputResponseOperationEvidence? evidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        try
        {
            if (!TryCaptureCommand(command, out var capturedCommand)
                || capturedCommand is null
                || HumanInputResponseLifecycleCommandValidator.Validate(capturedCommand).Count > 0
                || !TryCaptureEvidence(evidence, out var capturedEvidence)
                || capturedEvidence is null
                || !OperationMatchesCommand(capturedEvidence, capturedCommand))
            {
                return false;
            }
            return MatchesCore(capturedCommand, capturedEvidence, snapshot);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Proves that retained evidence is the canonical outcome of the exact durable snapshot when no caller command remains available.</summary>
    /// <param name="evidence">The retained terminal operation evidence.</param>
    /// <param name="snapshot">The exact response snapshot, or null only for a request-not-found result.</param>
    /// <returns><see langword="true"/> only when retained evidence and durable artifacts provide a sufficient causal witness.</returns>
    /// <remarks>For a submit disposition reached before response-content inspection, only the fields relevant to the winning precedence rule are reconstructed; no value is inferred from its one-way command digest.</remarks>
    public static bool Matches(
        HumanInputResponseOperationEvidence? evidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        try
        {
            if (!TryCaptureEvidence(evidence, out var capturedEvidence)
                || capturedEvidence is null
                || capturedEvidence.FailureCode is HumanInputResponseOperationFailureCode.OperationIntentConflict
                    or HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded)
            {
                return false;
            }

            HumanInputResponseLifecycleStoreSnapshot? capturedSnapshot = null;
            if (snapshot is not null
                && (!HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(
                        snapshot,
                        capturedEvidence.Request.RequestId,
                        out capturedSnapshot)
                    || capturedSnapshot is null))
            {
                return false;
            }
            if (!TryReconstructCommand(capturedEvidence, capturedSnapshot, out var command)
                || command is null)
            {
                return false;
            }
            return MatchesCore(command, capturedEvidence, capturedSnapshot);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool MatchesCore(
        HumanInputResponseLifecycleCommand capturedCommand,
        HumanInputResponseOperationEvidence capturedEvidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        try
        {
            if (snapshot is null)
            {
                var missing = Evaluate(
                    capturedCommand,
                    null,
                    null,
                    null,
                    capturedEvidence.ActorId,
                    capturedEvidence.RecordedAtUtc,
                    [],
                    [],
                    0,
                    null);
                return PlanMatchesEvidence(missing, capturedCommand, capturedEvidence, null);
            }
            if (!HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(snapshot, capturedEvidence.Request.RequestId, out var exactSnapshot)
                || exactSnapshot is null)
            {
                return false;
            }

            var operationIndex = Array.FindIndex(
                exactSnapshot.Operations.ToArray(),
                operation => HumanInputResponseOperationEvidenceComparer.ExactEquals(operation, capturedEvidence));
            var expectedRequest = operationIndex < 0
                ? null
                : FindRequest(exactSnapshot, capturedCommand.ExpectedRequest);
            var previousHead = capturedEvidence.PreviousHead;
            var observedRequest = previousHead is null ? null : FindRequest(exactSnapshot, previousHead.CurrentRequest);
            if (expectedRequest is not null && operationIndex < 0
                || expectedRequest is null
                    && capturedEvidence.FailureCode is not HumanInputResponseOperationFailureCode.StaleResponse
                        and not HumanInputResponseOperationFailureCode.RequestTerminal)
            {
                return false;
            }

            var retained = new List<HumanInputResponseArtifact>();
            var active = new List<HumanInputResponseArtifact>();
            if (operationIndex >= 0
                && !TryReconstructPriorResponses(exactSnapshot, operationIndex, retained, active))
            {
                return false;
            }
            var evaluated = Evaluate(
                capturedCommand,
                observedRequest,
                expectedRequest,
                previousHead,
                capturedEvidence.ActorId,
                capturedEvidence.RecordedAtUtc,
                retained,
                active,
                Math.Max(operationIndex, 0),
                operationIndex > 0 ? exactSnapshot.Operations[operationIndex - 1].RecordedAtUtc : null);
            return PlanMatchesEvidence(evaluated, capturedCommand, capturedEvidence, exactSnapshot);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static (HumanInputResponseLifecycleMutationPlan Plan, HumanInputRequest? Request, string? ActorRoleId) Evaluate(
        HumanInputResponseLifecycleCommand command,
        HumanInputRequest? observedRequest,
        HumanInputRequest? expectedRequest,
        HumanInputRequestLifecycleHead? head,
        AuthorityActorId actorId,
        DateTimeOffset recordedAtUtc,
        IReadOnlyList<HumanInputResponseArtifact> retainedResponses,
        IReadOnlyList<HumanInputResponseArtifact> activeResponses,
        int priorOperationCount,
        DateTimeOffset? previousOperationAtUtc)
    {
        if (head is null || observedRequest is null)
        {
            return (
                HumanInputResponsePolicyEvaluator.Failure(
                    null,
                    command.TargetResponses,
                    HumanInputResponseLifecycleMutationStatus.NotFound,
                    HumanInputResponseOperationOutcome.NotFound,
                    HumanInputResponseOperationFailureCode.RequestNotFound),
                null,
                null);
        }

        var actorRoleId = expectedRequest is null ? null : EligibleRole(expectedRequest, actorId);
        var chronologyAllowsPersistence = recordedAtUtc >= head.UpdatedAtUtc
            && recordedAtUtc >= observedRequest.Timing.RequestedAtUtc
            && (previousOperationAtUtc is null || recordedAtUtc >= previousOperationAtUtc.Value);
        if (head.Status != HumanInputRequestLifecycleStatus.Pending)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Conflict,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.RequestTerminal,
                canPersist: chronologyAllowsPersistence), observedRequest, actorRoleId);
        }
        if (!Equals(head.CurrentRequest, command.ExpectedRequest)
            || !Equals(observedRequest.Binding, command.ExpectedBinding))
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Conflict,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.StaleResponse,
                canPersist: chronologyAllowsPersistence), observedRequest, actorRoleId);
        }
        if (head.LifecycleVersion != command.ExpectedLifecycleVersion)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Conflict,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: chronologyAllowsPersistence), observedRequest, actorRoleId);
        }
        var selectorIsEligible = command.Kind != HumanInputResponseOperationKind.Select
            || observedRequest.ResponsePolicy.Kind == HumanInputResponsePolicyKind.ManualSelection
                && observedRequest.ResponsePolicy.OrderedRoleIds is { } selectorRoles
                && actorRoleId is not null
                && selectorRoles.Contains(actorRoleId, StringComparer.Ordinal);
        if (actorRoleId is null || !selectorIsEligible)
        {
            var selector = command.Kind == HumanInputResponseOperationKind.Select;
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Ineligible,
                HumanInputResponseOperationOutcome.Rejected,
                selector
                    ? HumanInputResponseOperationFailureCode.IneligibleSelector
                    : HumanInputResponseOperationFailureCode.IneligibleRespondent,
                canPersist: chronologyAllowsPersistence), observedRequest, null);
        }
        if (!chronologyAllowsPersistence)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Unavailable,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: false), observedRequest, actorRoleId);
        }
        if (priorOperationCount >= HumanInputResponseContractLimits.MaxOperationsPerRequest)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.LimitExceeded,
                HumanInputResponseOperationOutcome.LimitExceeded,
                HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded,
                canPersist: false), observedRequest, actorRoleId);
        }
        if (command.Kind is HumanInputResponseOperationKind.Submit or HumanInputResponseOperationKind.Select
            && recordedAtUtc > observedRequest.Timing.ExpiresAtUtc)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Late,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.LateResponse), observedRequest, actorRoleId);
        }
        if (command.Kind == HumanInputResponseOperationKind.Submit
            && !SubmittedValueIsValid(observedRequest, command, recordedAtUtc))
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                [],
                HumanInputResponseLifecycleMutationStatus.Invalid,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.MalformedResponse), observedRequest, actorRoleId);
        }

        var plan = HumanInputResponsePolicyEvaluator.Evaluate(
            observedRequest,
            head,
            command,
            actorId,
            actorRoleId,
            recordedAtUtc,
            retainedResponses,
            activeResponses);
        if (plan.ResponseToAppend is not null
            && !HumanInputResponseContractValidator.ValidateArtifact(observedRequest, plan.ResponseToAppend).IsValid
            || plan.SelectionToAppend is not null
                && !HumanInputResponseContractValidator.ValidateSelection(
                    observedRequest,
                    plan.SelectionToAppend,
                    activeResponses.Append(plan.ResponseToAppend).Where(response => response is not null).Select(response => response!).ToArray()).IsValid)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Ambiguous,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: false), observedRequest, actorRoleId);
        }
        return (plan, observedRequest, actorRoleId);
    }

    private static bool TryReconstructPriorResponses(
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        int operationIndex,
        List<HumanInputResponseArtifact> retained,
        List<HumanInputResponseArtifact> active)
    {
        var artifacts = snapshot.Responses.ToDictionary(response => response.ResponseId, StringComparer.Ordinal);
        foreach (var operation in snapshot.Operations.Take(operationIndex))
        {
            if (operation.Outcome != HumanInputResponseOperationOutcome.Committed)
            {
                continue;
            }
            if (operation.Kind == HumanInputResponseOperationKind.Submit)
            {
                if (operation.SubmittedResponse is not { } reference
                    || !artifacts.TryGetValue(reference.ResponseId, out var artifact)
                    || !reference.Matches(FindRequest(snapshot, operation.Request)!, artifact))
                {
                    return false;
                }
                retained.Add(artifact);
                active.Add(artifact);
            }
            else if (operation.Kind == HumanInputResponseOperationKind.Withdraw)
            {
                var target = operation.TargetResponses[0];
                var index = active.FindIndex(response => target.Matches(FindRequest(snapshot, operation.Request)!, response));
                if (index < 0)
                {
                    return false;
                }
                active.RemoveAt(index);
            }
        }
        return true;
    }

    private static bool PlanMatchesEvidence(
        (HumanInputResponseLifecycleMutationPlan Plan, HumanInputRequest? Request, string? ActorRoleId) evaluated,
        HumanInputResponseLifecycleCommand command,
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        var (plan, request, actorRoleId) = evaluated;
        if (!plan.CanPersist
            || plan.Outcome != evidence.Outcome
            || plan.FailureCode != evidence.FailureCode
            || !Equals(plan.PreviousHead, evidence.PreviousHead)
            || !Equals(plan.ResultHead, evidence.ResultHead)
            || !plan.TargetResponses.SequenceEqual(evidence.TargetResponses))
        {
            return false;
        }
        var attributedRole = plan.FailureCode is HumanInputResponseOperationFailureCode.RequestNotFound
            or HumanInputResponseOperationFailureCode.IneligibleRespondent
            or HumanInputResponseOperationFailureCode.IneligibleSelector
            ? null
            : actorRoleId;
        if (!string.Equals(attributedRole, evidence.ActorRoleId, StringComparison.Ordinal))
        {
            return false;
        }
        if (RequiresAttemptedResponse(evidence))
        {
            if (request is null
                || attributedRole is null
                || !HumanInputResponsePolicyEvaluator.TryCreateAttempt(
                    request,
                    command,
                    evidence.ActorId,
                    attributedRole,
                    evidence.RecordedAtUtc,
                    out var expectedAttempt)
                || expectedAttempt is null
                || !HumanInputResponseOperationEvidenceComparer.ArtifactEquals(expectedAttempt, evidence.AttemptedResponse))
            {
                return false;
            }
        }
        else if (evidence.AttemptedResponse is not null)
        {
            return false;
        }
        if (plan.ResponseToAppend is null)
        {
            if (evidence.SubmittedResponse is not null)
            {
                return false;
            }
        }
        else if (request is null
            || evidence.SubmittedResponse is not { } submitted
            || !submitted.Matches(request, plan.ResponseToAppend)
            || snapshot is null
            || !snapshot.Responses.Any(response => submitted.Matches(request, response)
                && string.Equals(response.ResponseHash, plan.ResponseToAppend.ResponseHash, StringComparison.Ordinal)))
        {
            return false;
        }
        var selection = plan.SelectionToAppend is null ? null : HumanInputResponseSelectionReference.Create(plan.SelectionToAppend);
        return Equals(selection, evidence.Selection);
    }

    private static bool RequiresAttemptedResponse(HumanInputResponseOperationEvidence evidence)
        => evidence.Kind == HumanInputResponseOperationKind.Submit
            && evidence.Outcome != HumanInputResponseOperationOutcome.Committed
            && evidence.FailureCode is HumanInputResponseOperationFailureCode.MalformedResponse
                or HumanInputResponseOperationFailureCode.DuplicateResponse
                or HumanInputResponseOperationFailureCode.ResponseLimitExceeded
                or HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded;

    private static bool TryCaptureEvidence(
        HumanInputResponseOperationEvidence? evidence,
        out HumanInputResponseOperationEvidence? captured)
    {
        captured = null;
        return evidence is not null
            && HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out captured, out _)
            && captured is not null
            && HumanInputResponseEligibilityEvidenceHash.Matches(captured);
    }

    private static bool TryReconstructCommand(
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot,
        out HumanInputResponseLifecycleCommand? command)
    {
        command = null;
        HumanInputResponseArtifact? source = null;
        var exactIntent = evidence.Kind is HumanInputResponseOperationKind.Withdraw or HumanInputResponseOperationKind.Select;
        if (evidence.Kind == HumanInputResponseOperationKind.Submit)
        {
            if (evidence.AttemptedResponse is not null)
            {
                source = evidence.AttemptedResponse;
                exactIntent = true;
            }
            else if (evidence.Outcome == HumanInputResponseOperationOutcome.Committed)
            {
                if (snapshot is null
                    || evidence.SubmittedResponse is not { } submitted
                    || FindRequest(snapshot, evidence.Request) is not { } request)
                {
                    return false;
                }
                source = snapshot.Responses.SingleOrDefault(response => submitted.Matches(request, response));
                if (source is null)
                {
                    return false;
                }
                exactIntent = true;
            }
            else if (!IsPreinspectionSubmitFailure(evidence.FailureCode))
            {
                return false;
            }
        }

        var candidate = new HumanInputResponseLifecycleCommand(
            HumanInputResponseLifecycleCommand.CurrentSchemaVersion,
            evidence.OperationId,
            evidence.Kind,
            evidence.Request.RequestId,
            evidence.ExpectedLifecycleVersion,
            evidence.ExpectedLifecycleStatus,
            evidence.Request,
            evidence.ExpectedBinding,
            source?.ResponseId ?? (evidence.Kind == HumanInputResponseOperationKind.Submit ? "unobserved-response" : null),
            source?.Value ?? (evidence.Kind == HumanInputResponseOperationKind.Submit ? UnobservedValue() : null),
            source?.Explanation,
            evidence.TargetResponses,
            exactIntent ? evidence.CommandHash : string.Empty);
        command = exactIntent
            ? candidate
            : HumanInputResponseLifecycleCommandHash.Apply(candidate);
        return HumanInputResponseLifecycleCommandValidator.Validate(command).Count == 0;
    }

    private static bool IsPreinspectionSubmitFailure(HumanInputResponseOperationFailureCode failureCode)
        => failureCode is HumanInputResponseOperationFailureCode.RequestNotFound
            or HumanInputResponseOperationFailureCode.RequestTerminal
            or HumanInputResponseOperationFailureCode.StaleResponse
            or HumanInputResponseOperationFailureCode.OptimisticStateConflict
            or HumanInputResponseOperationFailureCode.IneligibleRespondent
            or HumanInputResponseOperationFailureCode.LateResponse;

    private static HumanInputResponseValue UnobservedValue()
        => new(HumanInputResponseKind.Text, "unobserved", null, null, null, null);

    private static bool OperationMatchesCommand(
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleCommand command)
        => string.Equals(evidence.OperationId, command.OperationId, StringComparison.Ordinal)
            && string.Equals(evidence.CommandHash, command.CommandHash, StringComparison.Ordinal)
            && evidence.Kind == command.Kind
            && Equals(evidence.Request, command.ExpectedRequest)
            && Equals(evidence.ExpectedBinding, command.ExpectedBinding)
            && evidence.ExpectedLifecycleVersion == command.ExpectedLifecycleVersion
            && evidence.ExpectedLifecycleStatus == command.ExpectedLifecycleStatus
            && evidence.TargetResponses.SequenceEqual(command.TargetResponses);

    private static bool SubmittedValueIsValid(
        HumanInputRequest request,
        HumanInputResponseLifecycleCommand command,
        DateTimeOffset recordedAtUtc)
    {
        var representative = request.EligibleRespondents[0];
        var response = new HumanInputResponse(
            request.RequestId,
            request.RequestVersionId,
            request.Binding,
            representative.RespondentId,
            representative.RespondentRoleId,
            recordedAtUtc,
            command.Value!,
            command.Explanation);
        return HumanInputValidator.ValidateResponse(request, response).Kind == HumanInputResponseOutcomeKind.Valid;
    }

    private static string? EligibleRole(HumanInputRequest request, AuthorityActorId actorId)
        => request.EligibleRespondents.SingleOrDefault(respondent => string.Equals(respondent.RespondentId, actorId.Value, StringComparison.Ordinal))?.RespondentRoleId;

    private static bool TryCaptureCommand(
        HumanInputResponseLifecycleCommand? source,
        out HumanInputResponseLifecycleCommand? captured)
    {
        captured = null;
        try
        {
            if (source is null || source.TargetResponses.IsDefault)
            {
                return false;
            }
            var targets = source.TargetResponses.Select(target => target with { Request = target.Request with { } }).ToImmutableArray();
            ImmutableArray<HumanInputStructuredFieldValue>? structured = source.Value?.StructuredFields is not { } fields
                ? null
                : fields.Select(field => field is null ? null! : field with { }).ToImmutableArray();
            var value = source.Value is null
                ? null
                : source.Value with
                {
                    StructuredFields = structured,
                    Reference = source.Value.Reference is null ? null : source.Value.Reference with { }
                };
            captured = source with
            {
                ExpectedRequest = source.ExpectedRequest with { },
                ExpectedBinding = source.ExpectedBinding with { },
                Value = value,
                TargetResponses = targets
            };
            return true;
        }
        catch (Exception)
        {
            captured = null;
            return false;
        }
    }

    private static HumanInputRequest? FindRequest(
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        HumanInputRequestReference reference)
    {
        try
        {
            return snapshot.Request.RequestVersions.SingleOrDefault(reference.Matches);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
