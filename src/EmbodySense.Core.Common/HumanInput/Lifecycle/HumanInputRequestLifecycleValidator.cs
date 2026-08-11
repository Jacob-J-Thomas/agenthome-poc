using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Lifecycle;

/// <summary>Validates durable Human Input request references, heads, evidence, and committed non-response lifecycle transitions.</summary>
public static class HumanInputRequestLifecycleValidator
{
    /// <summary>Validates one exact immutable Human Input request reference.</summary>
    /// <param name="reference">The reference to inspect.</param>
    /// <returns>Every bounded deterministic contract violation.</returns>
    public static HumanInputRequestLifecycleValidationResult ValidateReference(HumanInputRequestReference? reference)
    {
        var errors = new List<HumanInputRequestLifecycleValidationError>();
        ValidateReference(reference, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one exact optimistic Human Input request lifecycle head.</summary>
    /// <param name="head">The lifecycle head to inspect.</param>
    /// <returns>Every bounded deterministic contract violation.</returns>
    public static HumanInputRequestLifecycleValidationResult ValidateHead(HumanInputRequestLifecycleHead? head)
    {
        var errors = new List<HumanInputRequestLifecycleValidationError>();
        ValidateHead(head, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one append-only lifecycle operation evidence record without consulting mutable store state.</summary>
    /// <param name="evidence">The evidence to inspect.</param>
    /// <returns>Every bounded deterministic contract violation.</returns>
    public static HumanInputRequestLifecycleValidationResult ValidateEvidence(HumanInputRequestLifecycleOperationEvidence? evidence)
    {
        var errors = new List<HumanInputRequestLifecycleValidationError>();
        if (evidence is null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$", "Lifecycle operation evidence is required.");
            return Result(errors);
        }

        ValidateSchema(evidence.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(evidence.OperationId, "$.operationId", HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters, errors);
        ValidateSha256(evidence.RequestHash, "$.requestHash", HumanInputRequestLifecycleValidationErrorCode.InvalidHash, errors);
        ValidateOperationVocabulary(evidence, errors);
        ValidateIdentifier(evidence.TargetRequestId, "$.targetRequestId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateExpectedEvidence(evidence, errors);
        ValidateHead(evidence.PreviousHead, "$.previousHead", errors, required: false);
        ValidateHead(evidence.ResultHead, "$.resultHead", errors, required: false);
        ValidateAttribution(evidence, errors);
        ValidateUtc(evidence.RecordedAtUtc, "$.recordedAtUtc", errors);

        if (evidence.PreviousHead is { } previous && !string.Equals(previous.RequestId, evidence.TargetRequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.previousHead.requestId", "The previous head must identify the exact target request.");
        }

        if (evidence.ResultHead is { } result && !string.Equals(result.RequestId, evidence.TargetRequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.resultHead.requestId", "The result head must identify the exact target request.");
        }

        var candidateRequired = evidence.Kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;
        if (candidateRequired != (evidence.CandidateRequest is not null))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.candidateRequest", "Candidate evidence is required only for create, reroute, amend, and supersede.");
        }
        if (evidence.CandidateRequest is not null)
        {
            ValidateReference(evidence.CandidateRequest, "$.candidateRequest", errors);
            var expectedCandidateRequestId = evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede
                ? evidence.RelatedRequestId
                : evidence.TargetRequestId;
            if (candidateRequired
                && !string.Equals(evidence.CandidateRequest.RequestId, expectedCandidateRequestId, StringComparison.Ordinal))
            {
                Add(
                    errors,
                    HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape,
                    "$.candidateRequest.requestId",
                    "The candidate request must identify the exact target lifecycle, or the exact related lifecycle for supersede.");
            }
        }

        ValidateRelatedShape(evidence, errors);
        ValidateGrantShape(evidence, errors);
        ValidateOutcomeHeadShape(evidence, errors);
        return Result(errors);
    }

    /// <summary>Validates one planned committed transition against exact immutable request artifacts.</summary>
    /// <param name="evidence">The complete committed operation evidence.</param>
    /// <param name="previousRequest">The exact request artifact referenced by the previous head, or null for creation.</param>
    /// <param name="candidateRequest">The exact appended request artifact for a candidate-bearing operation.</param>
    /// <returns>Every bounded deterministic transition violation.</returns>
    public static HumanInputRequestLifecycleValidationResult ValidateCommittedTransition(
        HumanInputRequestLifecycleOperationEvidence? evidence,
        HumanInputRequest? previousRequest,
        HumanInputRequest? candidateRequest)
    {
        var errors = ValidateEvidence(evidence).Errors.ToList();
        if (evidence is null)
        {
            return Result(errors);
        }

        if (evidence.Outcome != HumanInputRequestLifecycleOperationOutcome.Committed || evidence.FailureCode != HumanInputRequestLifecycleOperationFailureCode.None)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.outcome", "Committed transition validation requires a committed outcome without failure.");
            return Result(errors);
        }

        HumanInputRequest? capturedPrevious = null;
        HumanInputRequest? capturedCandidate = null;
        if (previousRequest is not null && !HumanInputRequestSnapshot.TryCapture(previousRequest, out capturedPrevious, out _))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, "$.previousRequest", "The previous immutable request artifact is invalid.");
        }
        if (candidateRequest is not null && !HumanInputRequestSnapshot.TryCapture(candidateRequest, out capturedCandidate, out _))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, "$.candidateRequestArtifact", "The candidate immutable request artifact is invalid.");
        }

        if (evidence.PreviousHead is { } previousHead
            && (capturedPrevious is null || !previousHead.CurrentRequest.Matches(capturedPrevious)))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, "$.previousRequest", "The previous artifact must exactly match the previous lifecycle head.");
        }
        if (evidence.CandidateRequest is { } candidateReference
            && (capturedCandidate is null || !candidateReference.Matches(capturedCandidate)))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, "$.candidateRequestArtifact", "The candidate artifact must exactly match the candidate reference.");
        }

        if (errors.Count > 0)
        {
            return Result(errors);
        }

        ValidateCommittedExpectation(evidence, capturedPrevious, errors);
        if (errors.Count > 0)
        {
            return Result(errors);
        }

        switch (evidence.Kind)
        {
            case HumanInputRequestLifecycleOperationKind.Create:
                ValidateCreate(evidence, capturedPrevious, capturedCandidate!, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Remind:
                ValidateRemind(evidence, capturedPrevious!, capturedCandidate, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Reroute:
                ValidateReroute(evidence, capturedPrevious!, capturedCandidate!, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Amend:
                ValidateAmend(evidence, capturedPrevious!, capturedCandidate!, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Reject:
                ValidateTerminal(evidence, capturedPrevious!, capturedCandidate, HumanInputRequestLifecycleStatus.Rejected, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Cancel:
                ValidateTerminal(evidence, capturedPrevious!, capturedCandidate, HumanInputRequestLifecycleStatus.Cancelled, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Expire:
                ValidateExpire(evidence, capturedPrevious!, capturedCandidate, errors);
                break;
            case HumanInputRequestLifecycleOperationKind.Supersede:
                ValidateSupersede(evidence, capturedPrevious!, capturedCandidate!, errors);
                break;
            default:
                Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidOperationKind, "$.kind", "A supported committed lifecycle operation is required.");
                break;
        }

        return Result(errors);
    }

    private static void ValidateCreate(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest? previousRequest, HumanInputRequest candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (evidence.PreviousHead is not null || previousRequest is not null || evidence.RelatedRequestId is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.previousHead", "Create requires an absent target lifecycle and no related request.");
        }
        if (!string.Equals(evidence.TargetRequestId, candidate.RequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.candidateRequest", "Create must target the exact candidate request.");
        }
        if (!ContainsTrustedTime(candidate, evidence.RecordedAtUtc))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict, "$.candidateRequest.timing", "Create requires trusted operation time within the candidate's inclusive response window.");
        }
        ValidateInitialHead(evidence.ResultHead, candidate, evidence.OperationId, null, evidence.RecordedAtUtc, errors, "$.resultHead");
    }

    private static void ValidateRemind(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, HumanInputRequest? candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (candidate is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.candidateRequest", "Remind cannot append a request version.");
        }
        if (!ValidatePendingBoundary(evidence, previousRequest, expiresAfterEndpoint: false, errors))
        {
            return;
        }

        var previous = evidence.PreviousHead!;
        var result = evidence.ResultHead!;
        ValidateContiguousHead(evidence, previous, result, errors);
        if (result.Status != HumanInputRequestLifecycleStatus.Pending
            || !Equals(result.CurrentRequest, previous.CurrentRequest)
            || result.ReminderCount != previous.ReminderCount + 1
            || !SameLinks(previous, result))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.resultHead", "Remind may change only lifecycle version, reminder count, operation, and trusted time.");
        }
    }

    private static void ValidateReroute(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, HumanInputRequest candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (!ValidatePendingBoundary(evidence, previousRequest, expiresAfterEndpoint: false, errors))
        {
            return;
        }
        ValidateCandidateSuccessorHead(evidence, candidate, errors);
        var changedRouting = !RespondentsEqual(previousRequest.EligibleRespondents, candidate.EligibleRespondents);
        var preserved = SameRequestIdentity(previousRequest, candidate)
            && Equals(previousRequest.Binding, candidate.Binding)
            && string.Equals(previousRequest.Purpose, candidate.Purpose, StringComparison.Ordinal)
            && string.Equals(previousRequest.Prompt, candidate.Prompt, StringComparison.Ordinal)
            && SchemaEquals(previousRequest.ResponseSchema, candidate.ResponseSchema)
            && previousRequest.PrivacyClass == candidate.PrivacyClass
            && Equals(previousRequest.Timing, candidate.Timing)
            && Equals(previousRequest.ResponsePolicy, candidate.ResponsePolicy)
            && Equals(previousRequest.ContinuationBinding, candidate.ContinuationBinding);
        if (!preserved || !changedRouting)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation, "$.candidateRequest", "Reroute must change routing and preserve every other request behavior field.");
        }
    }

    private static void ValidateAmend(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, HumanInputRequest candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (!ValidatePendingBoundary(evidence, previousRequest, expiresAfterEndpoint: false, errors))
        {
            return;
        }
        ValidateCandidateSuccessorHead(evidence, candidate, errors);
        var preserved = SameRequestIdentity(previousRequest, candidate)
            && Equals(previousRequest.Binding, candidate.Binding)
            && RespondentsEqual(previousRequest.EligibleRespondents, candidate.EligibleRespondents)
            && Equals(previousRequest.ResponsePolicy, candidate.ResponsePolicy)
            && Equals(previousRequest.ContinuationBinding, candidate.ContinuationBinding)
            && previousRequest.Timing.RequestedAtUtc == candidate.Timing.RequestedAtUtc;
        if (!preserved)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation, "$.candidateRequest", "Amend must preserve request identity, binding, routing, response policy, continuation, and original request time.");
        }
        if (IsPrivacyDowngrade(previousRequest.PrivacyClass, candidate.PrivacyClass))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.PrivacyDowngrade, "$.candidateRequest.privacyClass", "Amend cannot weaken the retained privacy classification.");
        }
        var changed = !string.Equals(previousRequest.Purpose, candidate.Purpose, StringComparison.Ordinal)
            || !string.Equals(previousRequest.Prompt, candidate.Prompt, StringComparison.Ordinal)
            || !SchemaEquals(previousRequest.ResponseSchema, candidate.ResponseSchema)
            || previousRequest.Timing.ExpiresAtUtc != candidate.Timing.ExpiresAtUtc
            || previousRequest.PrivacyClass != candidate.PrivacyClass;
        if (!changed)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.candidateRequest", "Amend must change at least one admitted content, expiry, or privacy field.");
        }
        if (evidence.RecordedAtUtc > candidate.Timing.ExpiresAtUtc)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict, "$.candidateRequest.timing.expiresAtUtc", "Amend cannot create an already expired request version.");
        }
    }

    private static void ValidateTerminal(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, HumanInputRequest? candidate, HumanInputRequestLifecycleStatus status, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (candidate is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.candidateRequest", "Terminal lifecycle operations cannot append a request version.");
        }
        if (!ValidatePendingBoundary(evidence, previousRequest, expiresAfterEndpoint: false, errors))
        {
            return;
        }
        ValidateStatusOnlySuccessor(evidence, status, errors);
    }

    private static void ValidateExpire(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, HumanInputRequest? candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (candidate is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.candidateRequest", "Expire cannot append a request version.");
        }
        if (!ValidatePendingBoundary(evidence, previousRequest, expiresAfterEndpoint: true, errors))
        {
            return;
        }
        ValidateStatusOnlySuccessor(evidence, HumanInputRequestLifecycleStatus.Expired, errors);
    }

    private static void ValidateSupersede(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, HumanInputRequest candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (!ValidatePendingBoundary(evidence, previousRequest, expiresAfterEndpoint: false, errors))
        {
            return;
        }
        if (string.Equals(previousRequest.RequestId, candidate.RequestId, StringComparison.Ordinal)
            || !Equals(previousRequest.Binding, candidate.Binding))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.RequestMutationOutsideOperation, "$.candidateRequest", "Supersede requires a different request ID and the exact original binding.");
        }
        if (!ContainsTrustedTime(candidate, evidence.RecordedAtUtc))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict, "$.candidateRequest.timing", "Supersede requires trusted operation time within the candidate's inclusive response window.");
        }
        if (IsPrivacyDowngrade(previousRequest.PrivacyClass, candidate.PrivacyClass))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.PrivacyDowngrade, "$.candidateRequest.privacyClass", "Supersede cannot weaken the retained privacy classification.");
        }

        var previous = evidence.PreviousHead!;
        var result = evidence.ResultHead!;
        ValidateContiguousHead(evidence, previous, result, errors);
        if (result.Status != HumanInputRequestLifecycleStatus.Superseded
            || !Equals(result.CurrentRequest, previous.CurrentRequest)
            || result.ReminderCount != previous.ReminderCount
            || !string.Equals(result.SupersedesRequestId, previous.SupersedesRequestId, StringComparison.Ordinal)
            || !string.Equals(result.SupersededByRequestId, candidate.RequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, "$.resultHead", "Supersede must terminally link the old lifecycle to the new request without changing its request version or reminders.");
        }
        if (!string.Equals(evidence.RelatedRequestId, candidate.RequestId, StringComparison.Ordinal) || evidence.RelatedPreviousHead is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, "$.relatedRequestId", "Committed supersede requires one absent related lifecycle matching the candidate request.");
        }
        ValidateInitialHead(evidence.RelatedResultHead, candidate, evidence.OperationId, previousRequest.RequestId, evidence.RecordedAtUtc, errors, "$.relatedResultHead");
    }

    private static void ValidateCandidateSuccessorHead(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest candidate, List<HumanInputRequestLifecycleValidationError> errors)
    {
        var previous = evidence.PreviousHead!;
        var result = evidence.ResultHead!;
        ValidateContiguousHead(evidence, previous, result, errors);
        if (result.Status != HumanInputRequestLifecycleStatus.Pending
            || evidence.CandidateRequest is null
            || !Equals(result.CurrentRequest, evidence.CandidateRequest)
            || !result.CurrentRequest.Matches(candidate)
            || result.ReminderCount != previous.ReminderCount
            || !SameLinks(previous, result))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.resultHead", "The candidate successor head must retain pending lifecycle metadata and reference the exact candidate request.");
        }
    }

    private static bool ValidatePendingBoundary(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequest previousRequest, bool expiresAfterEndpoint, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (evidence.PreviousHead is not { } previous || evidence.ResultHead is null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.previousHead", "This transition requires exact previous and resulting lifecycle heads.");
            return false;
        }
        if (previous.Status != HumanInputRequestLifecycleStatus.Pending)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.previousHead.status", "Only a pending request may perform this transition.");
        }
        if (!previous.CurrentRequest.Matches(previousRequest))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, "$.previousRequest", "The previous request artifact must match the exact pending head.");
        }
        if (evidence.RelatedRequestId is not null && evidence.Kind != HumanInputRequestLifecycleOperationKind.Supersede)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.relatedRequestId", "Only supersede may affect a related lifecycle.");
        }

        var boundarySatisfied = expiresAfterEndpoint
            ? evidence.RecordedAtUtc > previousRequest.Timing.ExpiresAtUtc
            : evidence.RecordedAtUtc <= previousRequest.Timing.ExpiresAtUtc;
        if (!boundarySatisfied)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict, "$.recordedAtUtc", expiresAfterEndpoint
                ? "Expire requires trusted time strictly after the inclusive response endpoint."
                : "The pending transition cannot occur after the inclusive response endpoint.");
        }
        return errors.Count == 0;
    }

    private static void ValidateStatusOnlySuccessor(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequestLifecycleStatus status, List<HumanInputRequestLifecycleValidationError> errors)
    {
        var previous = evidence.PreviousHead!;
        var result = evidence.ResultHead!;
        ValidateContiguousHead(evidence, previous, result, errors);
        if (result.Status != status
            || !Equals(result.CurrentRequest, previous.CurrentRequest)
            || result.ReminderCount != previous.ReminderCount
            || !SameLinks(previous, result))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.resultHead", "The terminal successor may change only status, lifecycle version, operation, and trusted time.");
        }
    }

    private static void ValidateContiguousHead(HumanInputRequestLifecycleOperationEvidence evidence, HumanInputRequestLifecycleHead previous, HumanInputRequestLifecycleHead result, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (previous.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion
            || result.LifecycleVersion != previous.LifecycleVersion + 1
            || !string.Equals(result.RequestId, previous.RequestId, StringComparison.Ordinal)
            || !string.Equals(result.LastOperationId, evidence.OperationId, StringComparison.Ordinal)
            || result.UpdatedAtUtc != evidence.RecordedAtUtc
            || evidence.RecordedAtUtc < previous.UpdatedAtUtc)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.resultHead", "The successor head must be contiguous, target-stable, operation-bound, and monotonic at trusted time.");
        }
    }

    private static void ValidateInitialHead(HumanInputRequestLifecycleHead? head, HumanInputRequest candidate, string operationId, string? supersedesRequestId, DateTimeOffset recordedAtUtc, List<HumanInputRequestLifecycleValidationError> errors, string path)
    {
        if (head is null
            || head.SchemaVersion != HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion
            || !string.Equals(head.RequestId, candidate.RequestId, StringComparison.Ordinal)
            || head.LifecycleVersion != 1
            || head.Status != HumanInputRequestLifecycleStatus.Pending
            || !head.CurrentRequest.Matches(candidate)
            || head.ReminderCount != 0
            || !string.Equals(head.SupersedesRequestId, supersedesRequestId, StringComparison.Ordinal)
            || head.SupersededByRequestId is not null
            || !string.Equals(head.LastOperationId, operationId, StringComparison.Ordinal)
            || head.UpdatedAtUtc != recordedAtUtc)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, path, "A new lifecycle head must be pending version 1 and exactly bind the candidate, operation, lineage, and trusted time.");
        }
    }

    private static void ValidateReference(HumanInputRequestReference? reference, string path, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (reference is null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, path, "An exact immutable request reference is required.");
            return;
        }
        ValidateSchema(reference.SchemaVersion, path + ".schemaVersion", errors);
        ValidateIdentifier(reference.RequestId, path + ".requestId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateIdentifier(reference.RequestVersionId, path + ".requestVersionId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateSha256(reference.RequestHash, path + ".requestHash", HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, errors);
    }

    private static void ValidateExpectedEvidence(
        HumanInputRequestLifecycleOperationEvidence evidence,
        List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (evidence.Kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            if (evidence.ExpectedLifecycleVersion != 0
                || evidence.ExpectedLifecycleStatus != HumanInputRequestLifecycleStatus.Unknown
                || evidence.ExpectedRequest is not null
                || evidence.ExpectedBinding is not null)
            {
                Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.expectedLifecycleVersion", "Create evidence requires a canonically absent optimistic expectation.");
            }

            return;
        }

        if (evidence.ExpectedLifecycleVersion is < 1 or > HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidLifecycleVersion, "$.expectedLifecycleVersion", "A non-create optimistic expectation requires a positive bounded lifecycle version.");
        }
        if (evidence.ExpectedLifecycleStatus != HumanInputRequestLifecycleStatus.Pending)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidLifecycleStatus, "$.expectedLifecycleStatus", "A non-create optimistic expectation must target a pending lifecycle.");
        }

        ValidateReference(evidence.ExpectedRequest, "$.expectedRequest", errors);
        if (evidence.ExpectedRequest is { } expectedRequest
            && !string.Equals(expectedRequest.RequestId, evidence.TargetRequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidRequestReference, "$.expectedRequest.requestId", "The optimistic request reference must identify the exact target lifecycle.");
        }

        ValidateExpectedBinding(evidence.ExpectedBinding, errors);
    }

    private static void ValidateExpectedBinding(
        HumanInputRequestBinding? binding,
        List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (binding is null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.expectedBinding", "A non-create optimistic expectation requires the full request binding.");
            return;
        }

        ValidateIdentifier(binding.WorkspaceId, "$.expectedBinding.workspaceId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateIdentifier(binding.LoopGraphId, "$.expectedBinding.loopGraphId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateIdentifier(binding.LoopRevisionId, "$.expectedBinding.loopRevisionId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateIdentifier(binding.NodeId, "$.expectedBinding.nodeId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateIdentifier(binding.RunId, "$.expectedBinding.runId", HumanInputLimits.MaxIdentifierCharacters, errors);
        ValidateIdentifier(binding.CheckpointId, "$.expectedBinding.checkpointId", HumanInputLimits.MaxIdentifierCharacters, errors);
    }

    private static void ValidateCommittedExpectation(
        HumanInputRequestLifecycleOperationEvidence evidence,
        HumanInputRequest? previousRequest,
        List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (evidence.Kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            return;
        }

        if (evidence.PreviousHead is not { } previous
            || previousRequest is null
            || evidence.ExpectedLifecycleVersion != previous.LifecycleVersion
            || evidence.ExpectedLifecycleStatus != previous.Status
            || !Equals(evidence.ExpectedRequest, previous.CurrentRequest)
            || !Equals(evidence.ExpectedBinding, previousRequest.Binding))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidTransition, "$.expectedLifecycleVersion", "A committed non-create transition requires its authenticated optimistic expectation to match the exact previous head and request binding.");
        }
    }

    private static void ValidateHead(HumanInputRequestLifecycleHead? head, string path, List<HumanInputRequestLifecycleValidationError> errors, bool required = true)
    {
        if (head is null)
        {
            if (required)
            {
                Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidHeadShape, path, "A lifecycle head is required.");
            }
            return;
        }
        ValidateSchema(head.SchemaVersion, path + ".schemaVersion", errors);
        ValidateIdentifier(head.RequestId, path + ".requestId", HumanInputLimits.MaxIdentifierCharacters, errors);
        if (head.LifecycleVersion is < 1 or > HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidLifecycleVersion, path + ".lifecycleVersion", "Lifecycle version must remain within the positive interoperable schema-1 range.");
        }
        if (!Enum.IsDefined(head.Status) || head.Status == HumanInputRequestLifecycleStatus.Unknown)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidLifecycleStatus, path + ".status", "A supported lifecycle status is required.");
        }
        ValidateReference(head.CurrentRequest, path + ".currentRequest", errors);
        if (head.CurrentRequest is not null && !string.Equals(head.RequestId, head.CurrentRequest.RequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidHeadShape, path + ".currentRequest.requestId", "The current request reference must identify this lifecycle.");
        }
        if (head.ReminderCount is < 0 or > HumanInputRequestLifecycleContractLimits.MaxReminderCount)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidReminderCount, path + ".reminderCount", "Reminder count must remain within the finite schema-1 bound.");
        }
        ValidateOptionalIdentifier(head.SupersedesRequestId, path + ".supersedesRequestId", errors);
        ValidateOptionalIdentifier(head.SupersededByRequestId, path + ".supersededByRequestId", errors);
        if (string.Equals(head.RequestId, head.SupersedesRequestId, StringComparison.Ordinal)
            || string.Equals(head.RequestId, head.SupersededByRequestId, StringComparison.Ordinal)
            || head.SupersedesRequestId is not null && string.Equals(head.SupersedesRequestId, head.SupersededByRequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, path, "Supersession links must identify distinct request lifecycles.");
        }
        if (head.Status == HumanInputRequestLifecycleStatus.Superseded && head.SupersededByRequestId is null
            || head.Status != HumanInputRequestLifecycleStatus.Superseded && head.SupersededByRequestId is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, path + ".supersededByRequestId", "Only a superseded lifecycle requires a successor request link.");
        }
        ValidateIdentifier(head.LastOperationId, path + ".lastOperationId", HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters, errors);
        ValidateUtc(head.UpdatedAtUtc, path + ".updatedAtUtc", errors);
    }

    private static void ValidateOperationVocabulary(HumanInputRequestLifecycleOperationEvidence evidence, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (!Enum.IsDefined(evidence.Kind) || evidence.Kind == HumanInputRequestLifecycleOperationKind.Unknown)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidOperationKind, "$.kind", "A supported lifecycle operation kind is required.");
        }
        if (!Enum.IsDefined(evidence.Outcome) || evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Unknown)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidOperationOutcome, "$.outcome", "A supported terminal lifecycle outcome is required.");
        }
        if (!Enum.IsDefined(evidence.FailureCode) || evidence.FailureCode == HumanInputRequestLifecycleOperationFailureCode.Unknown)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidFailureCode, "$.failureCode", "A supported lifecycle failure classification is required.");
        }
        var outcomeFailurePairIsValid = IsOutcomeFailurePair(evidence.Outcome, evidence.FailureCode);
        if (!outcomeFailurePairIsValid)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidOutcomeFailurePair, "$.failureCode", "Lifecycle outcome and failure classification are inconsistent.");
        }
        else if (!IsOperationFailurePair(evidence.Kind, evidence.FailureCode))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidOutcomeFailurePair, "$.failureCode", "The lifecycle failure classification cannot arise from the requested operation kind.");
        }
    }

    private static bool IsOutcomeFailurePair(HumanInputRequestLifecycleOperationOutcome outcome, HumanInputRequestLifecycleOperationFailureCode failureCode) => outcome switch
    {
        HumanInputRequestLifecycleOperationOutcome.Committed => failureCode == HumanInputRequestLifecycleOperationFailureCode.None,
        HumanInputRequestLifecycleOperationOutcome.Conflict => failureCode is HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict
            or HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists
            or HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal
            or HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict
            or HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict,
        HumanInputRequestLifecycleOperationOutcome.NotFound => failureCode == HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound,
        HumanInputRequestLifecycleOperationOutcome.LimitExceeded => failureCode is HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded
            or HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded
            or HumanInputRequestLifecycleOperationFailureCode.OperationEvidenceLimitExceeded
            or HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded
            or HumanInputRequestLifecycleOperationFailureCode.LifecycleVersionLimitExceeded,
        _ => false
    };

    private static bool IsOperationFailurePair(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleOperationFailureCode failureCode)
    {
        return failureCode switch
        {
            HumanInputRequestLifecycleOperationFailureCode.None => true,
            HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict => IsSupportedOperation(kind)
                && kind != HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.OperationIntentConflict => false,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound => IsSupportedOperation(kind)
                && kind != HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists => kind == HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal => IsSupportedOperation(kind)
                && kind != HumanInputRequestLifecycleOperationKind.Create,
            HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict => kind is HumanInputRequestLifecycleOperationKind.Reroute
                or HumanInputRequestLifecycleOperationKind.Amend
                or HumanInputRequestLifecycleOperationKind.Supersede,
            HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict => IsSupportedOperation(kind),
            HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded => RequiresCandidate(kind),
            HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded => kind == HumanInputRequestLifecycleOperationKind.Remind,
            HumanInputRequestLifecycleOperationFailureCode.OperationEvidenceLimitExceeded => IsSupportedOperation(kind),
            HumanInputRequestLifecycleOperationFailureCode.RequestLimitExceeded => kind is HumanInputRequestLifecycleOperationKind.Create
                or HumanInputRequestLifecycleOperationKind.Supersede,
            HumanInputRequestLifecycleOperationFailureCode.LifecycleVersionLimitExceeded => IsSupportedOperation(kind)
                && kind != HumanInputRequestLifecycleOperationKind.Create,
            _ => false
        };
    }

    private static bool IsSupportedOperation(HumanInputRequestLifecycleOperationKind kind)
        => kind is >= HumanInputRequestLifecycleOperationKind.Create and <= HumanInputRequestLifecycleOperationKind.Supersede;

    private static bool RequiresCandidate(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;

    private static bool ContainsTrustedTime(HumanInputRequest request, DateTimeOffset recordedAtUtc)
        => recordedAtUtc >= request.Timing.RequestedAtUtc
            && recordedAtUtc <= request.Timing.ExpiresAtUtc;

    private static void ValidateRelatedShape(HumanInputRequestLifecycleOperationEvidence evidence, List<HumanInputRequestLifecycleValidationError> errors)
    {
        var isSupersede = evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede;
        if (!isSupersede && (evidence.RelatedRequestId is not null || evidence.RelatedPreviousHead is not null || evidence.RelatedResultHead is not null))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.relatedRequestId", "Only supersede may carry a related request lifecycle.");
            return;
        }
        if (!isSupersede)
        {
            return;
        }

        ValidateIdentifier(evidence.RelatedRequestId, "$.relatedRequestId", HumanInputLimits.MaxIdentifierCharacters, errors);
        if (string.Equals(evidence.TargetRequestId, evidence.RelatedRequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, "$.relatedRequestId", "Supersede target and related request identities must differ.");
        }
        ValidateHead(evidence.RelatedPreviousHead, "$.relatedPreviousHead", errors, required: false);
        ValidateHead(evidence.RelatedResultHead, "$.relatedResultHead", errors, required: evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed);
        if (evidence.RelatedPreviousHead is { } previous && !string.Equals(previous.RequestId, evidence.RelatedRequestId, StringComparison.Ordinal)
            || evidence.RelatedResultHead is { } result && !string.Equals(result.RequestId, evidence.RelatedRequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, "$.relatedRequestId", "Every related head must identify the exact related request.");
        }
        if (evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed && evidence.RelatedPreviousHead is not null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, "$.relatedPreviousHead", "Committed supersede requires the new related lifecycle to be absent before commit.");
        }
        if (evidence.Outcome != HumanInputRequestLifecycleOperationOutcome.Committed
            && (evidence.RelatedPreviousHead is null != (evidence.RelatedResultHead is null)
                || evidence.RelatedPreviousHead is not null && !Equals(evidence.RelatedPreviousHead, evidence.RelatedResultHead)))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidSupersession, "$.relatedResultHead", "A non-mutating supersede disposition must retain the exact observed related head or keep both related heads absent.");
        }
    }

    private static void ValidateGrantShape(HumanInputRequestLifecycleOperationEvidence evidence, List<HumanInputRequestLifecycleValidationError> errors)
    {
        var requiresGrant = evidence.Kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;
        if (requiresGrant != (evidence.GrantReference is not null) || requiresGrant != (evidence.GrantDependencyEvidenceHash is not null))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidGrantEvidence, "$.grantReference", "Delivery-producing operations require exact grant and dependency evidence; terminal cleanup operations prohibit both.");
        }
        if (evidence.GrantReference is { } reference
            && (reference.GrantId is null
                || reference.Revision is null
                || !AuthorityGrantId.TryParse(reference.GrantId.Value, out _, out _)
                || !AuthorityGrantRevision.TryParse(reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _)
                || !IsPrefixedSha256(reference.ContentHash)))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidGrantEvidence, "$.grantReference", "The grant reference must identify one exact canonical immutable grant revision.");
        }
        if (evidence.GrantDependencyEvidenceHash is not null)
        {
            ValidateSha256(evidence.GrantDependencyEvidenceHash, "$.grantDependencyEvidenceHash", HumanInputRequestLifecycleValidationErrorCode.InvalidGrantEvidence, errors);
        }
    }

    private static void ValidateOutcomeHeadShape(HumanInputRequestLifecycleOperationEvidence evidence, List<HumanInputRequestLifecycleValidationError> errors)
    {
        switch (evidence.Outcome)
        {
            case HumanInputRequestLifecycleOperationOutcome.Committed:
                if (evidence.ResultHead is null)
                {
                    Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.resultHead", "A committed operation requires its exact resulting target head.");
                }
                if (evidence.Kind == HumanInputRequestLifecycleOperationKind.Create && evidence.PreviousHead is not null
                    || evidence.Kind != HumanInputRequestLifecycleOperationKind.Create && evidence.PreviousHead is null)
                {
                    Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.previousHead", "Only create commits without a previous target head.");
                }
                break;
            case HumanInputRequestLifecycleOperationOutcome.NotFound:
                if (evidence.PreviousHead is not null || evidence.ResultHead is not null)
                {
                    Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.previousHead", "Not-found evidence cannot claim an observed target lifecycle.");
                }
                break;
            case HumanInputRequestLifecycleOperationOutcome.Conflict:
            case HumanInputRequestLifecycleOperationOutcome.LimitExceeded:
                var requiresObservedTarget = evidence.Kind != HumanInputRequestLifecycleOperationKind.Create
                    || evidence.FailureCode == HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists;
                if (requiresObservedTarget
                    && (evidence.PreviousHead is null
                        || evidence.ResultHead is null
                        || !Equals(evidence.PreviousHead, evidence.ResultHead)))
                {
                    Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.resultHead", "This non-mutating disposition requires the exact observed target head before and after the operation.");
                }
                else if (!requiresObservedTarget && (evidence.PreviousHead is not null || evidence.ResultHead is not null))
                {
                    Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidEvidenceShape, "$.previousHead", "This create disposition requires a conclusively absent target lifecycle.");
                }
                break;
        }
    }

    private static void ValidateAttribution(HumanInputRequestLifecycleOperationEvidence evidence, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (evidence.ActorId is null || evidence.Reason is null)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidAttribution, "$.actorId", "Authenticated actor and bounded non-secret reason attribution are required.");
        }
        ValidateSha256(evidence.AuthorityEvidenceHash, "$.authorityEvidenceHash", HumanInputRequestLifecycleValidationErrorCode.InvalidAuthorityEvidence, errors);
    }

    private static bool SameRequestIdentity(HumanInputRequest previous, HumanInputRequest candidate)
        => string.Equals(previous.RequestId, candidate.RequestId, StringComparison.Ordinal)
            && !string.Equals(previous.RequestVersionId, candidate.RequestVersionId, StringComparison.Ordinal)
            && !string.Equals(previous.RequestHash, candidate.RequestHash, StringComparison.Ordinal);

    private static bool SameLinks(HumanInputRequestLifecycleHead left, HumanInputRequestLifecycleHead right)
        => string.Equals(left.SupersedesRequestId, right.SupersedesRequestId, StringComparison.Ordinal)
            && string.Equals(left.SupersededByRequestId, right.SupersededByRequestId, StringComparison.Ordinal);

    private static bool IsPrivacyDowngrade(HumanInputPrivacyClass previous, HumanInputPrivacyClass candidate)
        => previous == HumanInputPrivacyClass.Sensitive && candidate != HumanInputPrivacyClass.Sensitive;

    private static bool RespondentsEqual(HumanInputEligibleRespondent[]? left, HumanInputEligibleRespondent[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }
        for (var index = 0; index < left.Length; index++)
        {
            if (!Equals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SchemaEquals(HumanInputResponseSchema? left, HumanInputResponseSchema? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null
            || left.Kind != right.Kind
            || left.MaxTextCharacters != right.MaxTextCharacters
            || !Equals(left.ReferencePolicy, right.ReferencePolicy)
            || !ChoicesEqual(left.Choices, right.Choices))
        {
            return false;
        }
        if (left.StructuredFields is null || right.StructuredFields is null)
        {
            return left.StructuredFields is null && right.StructuredFields is null;
        }
        if (left.StructuredFields.Length != right.StructuredFields.Length)
        {
            return false;
        }
        for (var index = 0; index < left.StructuredFields.Length; index++)
        {
            var first = left.StructuredFields[index];
            var second = right.StructuredFields[index];
            if (first is null || second is null)
            {
                if (!ReferenceEquals(first, second))
                {
                    return false;
                }
                continue;
            }
            if (!string.Equals(first.FieldId, second.FieldId, StringComparison.Ordinal)
                || first.Kind != second.Kind
                || first.Required != second.Required
                || first.MaxTextCharacters != second.MaxTextCharacters
                || !ChoicesEqual(first.Choices, second.Choices))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ChoicesEqual(HumanInputChoice[]? left, HumanInputChoice[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }
        for (var index = 0; index < left.Length; index++)
        {
            if (!Equals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateSchema(int schemaVersion, string path, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (schemaVersion != HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.UnsupportedSchemaVersion, path, "Human Input request lifecycle schema version must be 1.");
        }
    }

    private static void ValidateIdentifier(string? value, string path, int maximum, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(value, maximum))
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidIdentifier, path, "A bounded canonical lowercase Human Input identifier is required.");
        }
    }

    private static void ValidateOptionalIdentifier(string? value, string path, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (value is not null)
        {
            ValidateIdentifier(value, path, HumanInputLimits.MaxIdentifierCharacters, errors);
        }
    }

    private static void ValidateSha256(string? value, string path, HumanInputRequestLifecycleValidationErrorCode code, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (!IsSha256(value))
        {
            Add(errors, code, path, "A 64-character lowercase SHA-256 digest is required.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<HumanInputRequestLifecycleValidationError> errors)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            Add(errors, HumanInputRequestLifecycleValidationErrorCode.InvalidUtcTime, path, "A non-default UTC time is required.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: HumanInputRequestLifecycleContractLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsPrefixedSha256(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && IsSha256(value[7..]);

    private static HumanInputRequestLifecycleValidationResult Result(List<HumanInputRequestLifecycleValidationError> errors) => new(errors);

    private static void Add(List<HumanInputRequestLifecycleValidationError> errors, HumanInputRequestLifecycleValidationErrorCode code, string path, string message)
    {
        if (errors.Count >= HumanInputRequestLifecycleContractLimits.MaxValidationErrors)
        {
            return;
        }
        var boundedPath = path.Length <= HumanInputRequestLifecycleContractLimits.MaxErrorPathCharacters
            ? path
            : path[..HumanInputRequestLifecycleContractLimits.MaxErrorPathCharacters];
        errors.Add(new HumanInputRequestLifecycleValidationError(code, boundedPath, message));
    }
}
