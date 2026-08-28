using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Validates immutable authenticated response, reference, selection, and append-only evidence contracts without authorizing, persisting, or continuing a loop.</summary>
public static class HumanInputResponseContractValidator
{
    /// <summary>Validates one immutable authenticated response artifact against its exact retained request version.</summary>
    /// <param name="request">The exact immutable request version.</param>
    /// <param name="artifact">The response artifact to inspect.</param>
    /// <returns>Every bounded deterministic value-free contract violation.</returns>
    public static HumanInputResponseValidationResult ValidateArtifact(HumanInputRequest? request, HumanInputResponseArtifact? artifact)
    {
        var errors = new List<HumanInputResponseValidationError>();
        if (!HumanInputValidator.ValidateRequest(request).IsValid || request is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidRequestReference, "$.request", "A valid exact immutable Human Input request is required before response data is inspected.");
            return Result(errors);
        }
        if (artifact is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidValue, "$", "An immutable authenticated Human Input response artifact is required.");
            return Result(errors);
        }

        ValidateSchema(artifact.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(artifact.ResponseId, "$.responseId", errors);
        ValidateRequestReference(artifact.Request, "$.request", errors);
        if (artifact.Request is not null && !artifact.Request.Matches(request))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidRequestReference, "$.request", "The response must identify the exact retained request version and canonical hash.");
        }
        ValidateBinding(artifact.Binding, "$.binding", errors);
        if (!Equals(artifact.Binding, request.Binding))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidBinding, "$.binding", "The response binding must exactly match the request workspace, graph, revision, node, run, and checkpoint.");
        }
        if (artifact.ActorId is null || !AuthorityActorId.TryParse(artifact.ActorId.Value, out _, out _))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidActor, "$.actorId", "Canonical authenticated actor attribution is required.");
        }
        ValidateIdentifier(artifact.RespondentRoleId, "$.respondentRoleId", HumanInputResponseValidationErrorCode.InvalidRole, errors);
        ValidateUtc(artifact.SubmittedAtUtc, "$.submittedAtUtc", errors);
        if (!Enum.IsDefined(artifact.PrivacyClass) || artifact.PrivacyClass == HumanInputPrivacyClass.Unknown || artifact.PrivacyClass != request.PrivacyClass)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidPrivacyClass, "$.privacyClass", "The response must retain the exact supported request privacy classification.");
        }

        if (artifact.ActorId is not null)
        {
            var response = new HumanInputResponse(request.RequestId, request.RequestVersionId, request.Binding, artifact.ActorId.Value, artifact.RespondentRoleId, artifact.SubmittedAtUtc, artifact.Value, artifact.Explanation);
            foreach (var validationError in HumanInputValidator.ValidateResponse(request, response).Errors)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidValue, "$." + validationError.Field, validationError.Message);
            }
        }
        else
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidValue, "$.value", "Response value cannot be validated without authenticated actor attribution.");
        }

        if (!HumanInputResponseArtifactHash.IsBounded(artifact))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidValue, "$", "The response artifact exceeds canonical schema-1 bounds.");
        }
        else if (!HumanInputResponseArtifactHash.Matches(artifact))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidHash, "$.responseHash", "Response value and artifact hashes must match the exact immutable response.");
        }
        return Result(errors);
    }

    /// <summary>Validates one privacy-safe exact response reference.</summary>
    /// <param name="reference">The response reference to inspect.</param>
    /// <returns>Every bounded deterministic contract violation.</returns>
    public static HumanInputResponseValidationResult ValidateReference(HumanInputResponseReference? reference)
    {
        var errors = new List<HumanInputResponseValidationError>();
        ValidateResponseReference(reference, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one privacy-safe exact response-selection reference.</summary>
    /// <param name="reference">The selection reference to inspect.</param>
    /// <returns>Every bounded deterministic contract violation.</returns>
    public static HumanInputResponseValidationResult ValidateSelectionReference(HumanInputResponseSelectionReference? reference)
    {
        var errors = new List<HumanInputResponseValidationError>();
        ValidateSelectionReference(reference, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one immutable deterministic response selection against the exact request and bounded active response set.</summary>
    /// <param name="request">The exact immutable request version.</param>
    /// <param name="selection">The selection to inspect.</param>
    /// <param name="activeResponses">All exact active, non-withdrawn response artifacts in durable response-operation order.</param>
    /// <returns>Every bounded deterministic value-free contract violation.</returns>
    public static HumanInputResponseValidationResult ValidateSelection(HumanInputRequest? request, HumanInputResponseSelection? selection, IReadOnlyList<HumanInputResponseArtifact>? activeResponses)
    {
        var errors = new List<HumanInputResponseValidationError>();
        if (!HumanInputValidator.ValidateRequest(request).IsValid || request is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidRequestReference, "$.request", "A valid exact immutable request is required before a response selection is inspected.");
            return Result(errors);
        }
        if (selection is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$", "An immutable Human Input response selection is required.");
            return Result(errors);
        }

        ValidateSchema(selection.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(selection.SelectionId, "$.selectionId", errors);
        ValidateRequestReference(selection.Request, "$.request", errors);
        if (selection.Request is not null && !selection.Request.Matches(request))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidRequestReference, "$.request", "The selection must identify the exact retained request version.");
        }
        if (selection.PolicyKind != request.ResponsePolicy.Kind)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.policyKind", "The selection must retain the exact authored response policy kind.");
        }
        ValidateUtc(selection.SelectedAtUtc, "$.selectedAtUtc", errors);
        if (selection.SelectedAtUtc < request.Timing.RequestedAtUtc || selection.SelectedAtUtc > request.Timing.ExpiresAtUtc)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidUtcTime, "$.selectedAtUtc", "Selection time must remain within the inclusive exact request window.");
        }

        if (selection.Responses.IsDefault || selection.Responses.Length is < 1 or > HumanInputResponseContractLimits.MaxSelectedResponses)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", "A selection requires a bounded non-empty ordered response-reference set.");
        }
        if (!TrySnapshotActiveResponseList(activeResponses, out var activeCandidates, errors))
        {
            return Result(errors);
        }

        var active = new HumanInputResponseArtifact[activeCandidates.Length];
        var activeById = new Dictionary<string, HumanInputResponseArtifact>(StringComparer.Ordinal);
        var activeActorIds = new HashSet<string>(StringComparer.Ordinal);
        var activeSetIsValid = true;
        for (var index = 0; index < activeCandidates.Length; index++)
        {
            var candidate = activeCandidates[index];
            if (candidate is null
                || !HumanInputResponseArtifactSnapshot.TryCapture(request, candidate, out var artifact, out _)
                || artifact is null)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, $"$.activeResponses[{index}]", "Every active response must be a valid exact-bound immutable artifact.");
                activeSetIsValid = false;
                continue;
            }

            active[index] = artifact;
            var responseIsUnique = activeById.TryAdd(artifact.ResponseId, artifact);
            var actorIsUnique = activeActorIds.Add(artifact.ActorId.Value);
            if (!responseIsUnique || !actorIsUnique)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, $"$.activeResponses[{index}]", "Active response and authenticated actor identities must each be unique.");
                activeSetIsValid = false;
            }
        }
        if (!activeSetIsValid)
        {
            return Result(errors);
        }

        var selected = new List<HumanInputResponseArtifact>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        if (!selection.Responses.IsDefault)
        {
            for (var index = 0; index < selection.Responses.Length && index < HumanInputResponseContractLimits.MaxSelectedResponses; index++)
            {
                var reference = selection.Responses[index];
                ValidateResponseReference(reference, $"$.responses[{index}]", errors);
                if (reference is null || !selectedIds.Add(reference.ResponseId))
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, $"$.responses[{index}]", "Selected response identities must be non-null and unique.");
                    continue;
                }
                if (!activeById.TryGetValue(reference.ResponseId, out var artifact) || !reference.Matches(request, artifact))
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidResponseReference, $"$.responses[{index}]", "Every selected reference must exactly identify one active retained response.");
                    continue;
                }
                if (artifact.SubmittedAtUtc > selection.SelectedAtUtc)
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, $"$.responses[{index}]", "A selection cannot precede a selected response's trusted commit time.");
                }
                selected.Add(artifact);
            }
        }

        ValidateSelectionPolicy(request, selection, selected, active, errors);
        if (!HumanInputResponseSelectionHash.IsBounded(selection))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$", "The selection exceeds canonical schema-1 bounds.");
        }
        else if (!HumanInputResponseSelectionHash.Matches(selection))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidHash, "$.selectionHash", "Selection hash must match the exact ordered response set and policy attribution.");
        }
        return Result(errors);
    }

    private static bool TrySnapshotActiveResponseList(
        IReadOnlyList<HumanInputResponseArtifact>? activeResponses,
        out HumanInputResponseArtifact[] snapshot,
        List<HumanInputResponseValidationError> errors)
    {
        snapshot = [];
        if (activeResponses is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.activeResponses", "A bounded active response set is required.");
            return false;
        }

        try
        {
            var count = activeResponses.Count;
            if (count is < 0 or > HumanInputResponseContractLimits.MaxResponsesPerRequest)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.activeResponses", "A bounded active response set is required.");
                return false;
            }

            snapshot = new HumanInputResponseArtifact[count];
            for (var index = 0; index < count; index++)
            {
                snapshot[index] = activeResponses[index];
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.activeResponses", "The bounded active response set changed while its durable order was captured.");
            snapshot = [];
            return false;
        }
    }

    /// <summary>Validates one append-only authenticated-response operation evidence record without consulting mutable store state.</summary>
    /// <param name="evidence">The response-operation evidence to inspect.</param>
    /// <returns>Every bounded deterministic value-free contract violation.</returns>
    public static HumanInputResponseValidationResult ValidateEvidence(HumanInputResponseOperationEvidence? evidence)
    {
        var errors = new List<HumanInputResponseValidationError>();
        if (evidence is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$", "Authenticated Human Input response operation evidence is required.");
            return Result(errors);
        }

        ValidateSchema(evidence.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(evidence.OperationId, "$.operationId", errors);
        ValidateSha256(evidence.CommandHash, "$.commandHash", HumanInputResponseValidationErrorCode.InvalidHash, errors);
        ValidateVocabulary(evidence, errors);
        ValidateRequestReference(evidence.Request, "$.request", errors);
        ValidateBinding(evidence.ExpectedBinding, "$.expectedBinding", errors);
        if (evidence.ObservedBinding is not null)
        {
            ValidateBinding(evidence.ObservedBinding, "$.observedBinding", errors);
        }
        if (evidence.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound != (evidence.ObservedBinding is null))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidBinding, "$.observedBinding", "Trusted observed binding is absent only when the request was not found.");
        }
        if (evidence.ExpectedLifecycleVersion is < 1 or > HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion
            || evidence.ExpectedLifecycleStatus != HumanInputRequestLifecycleStatus.Pending)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.expectedLifecycleVersion", "Response operations require one exact bounded pending lifecycle expectation.");
        }
        ValidateEvidenceHeads(evidence, errors);
        ValidateEvidenceOperationShape(evidence, errors);
        if (evidence.ActorId is null || !AuthorityActorId.TryParse(evidence.ActorId.Value, out _, out _))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidActor, "$.actorId", "Canonical authenticated actor attribution is required.");
        }
        if (evidence.Outcome == HumanInputResponseOperationOutcome.Committed || evidence.ActorRoleId is not null)
        {
            ValidateIdentifier(evidence.ActorRoleId, "$.actorRoleId", HumanInputResponseValidationErrorCode.InvalidRole, errors);
        }
        if (evidence.ActorRoleId is not null
            && evidence.FailureCode is HumanInputResponseOperationFailureCode.RequestNotFound
                or HumanInputResponseOperationFailureCode.IneligibleRespondent
                or HumanInputResponseOperationFailureCode.IneligibleSelector)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidRole, "$.actorRoleId", "This failure cannot establish trusted role attribution.");
        }
        ValidateSha256(evidence.AuthenticationEvidenceHash, "$.authenticationEvidenceHash", HumanInputResponseValidationErrorCode.InvalidAuthenticationEvidence, errors);
        ValidateSha256(evidence.EligibilityEvidenceHash, "$.eligibilityEvidenceHash", HumanInputResponseValidationErrorCode.InvalidEligibilityEvidence, errors);
        ValidateUtc(evidence.RecordedAtUtc, "$.recordedAtUtc", errors);
        if (!HumanInputResponseEligibilityEvidenceHash.Matches(evidence))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidEligibilityEvidence, "$.eligibilityEvidenceHash", "Eligibility evidence must match every exact retained authority-bearing input.");
        }
        return Result(errors);
    }

    private static void ValidateSelectionPolicy(HumanInputRequest request, HumanInputResponseSelection selection, IReadOnlyList<HumanInputResponseArtifact> selected, IEnumerable<HumanInputResponseArtifact> activeResponses, List<HumanInputResponseValidationError> errors)
    {
        var policy = request.ResponsePolicy;
        switch (policy.Kind)
        {
            case HumanInputResponsePolicyKind.FirstValid:
                ValidateAutomaticSelector(selection, errors);
                if (selected.Count != 1 || activeResponses.FirstOrDefault() is not { } first || !string.Equals(selected[0].ResponseId, first.ResponseId, StringComparison.Ordinal))
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", "First-valid policy selects exactly the earliest active response in durable operation order.");
                }
                break;
            case HumanInputResponsePolicyKind.Quorum:
                ValidateAutomaticSelector(selection, errors);
                var winning = FindFirstQuorum(activeResponses, policy.RequiredResponseCount ?? 0);
                if (winning is null || selected.Count != winning.Count || !selected.Select(response => response.ResponseId).SequenceEqual(winning.Select(response => response.ResponseId), StringComparer.Ordinal))
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", "Quorum selects the first configured number of distinct respondents for the earliest winning value hash in durable operation order.");
                }
                break;
            case HumanInputResponsePolicyKind.NamedRoles:
                ValidateAutomaticSelector(selection, errors);
                ValidateExactRoleOrder(selected, policy.OrderedRoleIds, "Named-role selection must follow the complete authored required-role order.", errors);
                break;
            case HumanInputResponsePolicyKind.Merge:
                ValidateAutomaticSelector(selection, errors);
                ValidateMergeSelection(selected, activeResponses, policy, errors);
                break;
            case HumanInputResponsePolicyKind.ManualSelection:
                ValidateManualSelector(request, selection, errors);
                if (selected.Count != 1)
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", "Manual selection chooses exactly one exact active response.");
                }
                break;
            default:
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.policyKind", "A supported deterministic policy is required.");
                break;
        }
    }

    private static IReadOnlyList<HumanInputResponseArtifact>? FindFirstQuorum(IEnumerable<HumanInputResponseArtifact> activeResponses, int requiredCount)
    {
        if (requiredCount < 2)
        {
            return null;
        }

        var byValueHash = new Dictionary<string, List<HumanInputResponseArtifact>>(StringComparer.Ordinal);
        var actorsByValueHash = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var response in activeResponses)
        {
            if (!byValueHash.TryGetValue(response.ValueHash, out var matching))
            {
                matching = [];
                byValueHash.Add(response.ValueHash, matching);
                actorsByValueHash.Add(response.ValueHash, new HashSet<string>(StringComparer.Ordinal));
            }
            if (!actorsByValueHash[response.ValueHash].Add(response.ActorId.Value))
            {
                continue;
            }
            matching.Add(response);
            if (matching.Count == requiredCount)
            {
                return matching;
            }
        }
        return null;
    }

    private static void ValidateAutomaticSelector(HumanInputResponseSelection selection, List<HumanInputResponseValidationError> errors)
    {
        if (selection.SelectorActorId is not null || selection.SelectorRoleId is not null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.selectorActorId", "Automatic response policies prohibit manual selector attribution.");
        }
    }

    private static void ValidateManualSelector(HumanInputRequest request, HumanInputResponseSelection selection, List<HumanInputResponseValidationError> errors)
    {
        if (selection.SelectorActorId is null
            || !AuthorityActorId.TryParse(selection.SelectorActorId.Value, out _, out _)
            || !HumanInputIdentifier.IsValid(selection.SelectorRoleId)
            || request.ResponsePolicy.OrderedRoleIds is not { } selectorRoles
            || !selectorRoles.Contains(selection.SelectorRoleId!, StringComparer.Ordinal)
            || !IsEligible(request.EligibleRespondents, selection.SelectorActorId.Value, selection.SelectorRoleId!))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.selectorActorId", "Manual selection requires one exact authenticated actor and eligible authored selector role.");
        }
    }

    private static void ValidateExactRoleOrder(IReadOnlyList<HumanInputResponseArtifact> selected, System.Collections.Immutable.ImmutableArray<string>? orderedRoles, string message, List<HumanInputResponseValidationError> errors)
    {
        if (orderedRoles is not { } roles || roles.IsDefault || selected.Count != roles.Length)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", message);
            return;
        }
        for (var index = 0; index < roles.Length; index++)
        {
            if (!string.Equals(selected[index].RespondentRoleId, roles[index], StringComparison.Ordinal))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, $"$.responses[{index}]", message);
            }
        }
    }

    private static void ValidateMergeSelection(IReadOnlyList<HumanInputResponseArtifact> selected, IEnumerable<HumanInputResponseArtifact> activeResponses, HumanInputResponsePolicy policy, List<HumanInputResponseValidationError> errors)
    {
        if (policy.OrderedRoleIds is not { } roles || roles.IsDefault)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", "Merge requires its exact authored contributor-role order.");
            return;
        }

        var active = activeResponses.ToArray();
        var expected = new List<HumanInputResponseArtifact>();
        for (var index = 0; index < roles.Length; index++)
        {
            var matches = active.Where(response => string.Equals(response.RespondentRoleId, roles[index], StringComparison.Ordinal)).ToArray();
            if (matches.Length > 1)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.activeResponses", "Merge contributor roles must retain at most one active response.");
            }
            else if (matches.Length == 1)
            {
                expected.Add(matches[0]);
            }
        }
        if (expected.Count < policy.RequiredResponseCount || selected.Count != expected.Count)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$.responses", "Merge selects every active configured contributor in authored role order only after its threshold is met.");
            return;
        }
        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(selected[index].ResponseId, expected[index].ResponseId, StringComparison.Ordinal))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionShape, $"$.responses[{index}]", "Merge selection references must follow authored contributor-role order.");
            }
        }
    }

    private static void ValidateEvidenceHeads(HumanInputResponseOperationEvidence evidence, List<HumanInputResponseValidationError> errors)
    {
        if (evidence.PreviousHead is not null && !HumanInputRequestLifecycleValidator.ValidateHead(evidence.PreviousHead).IsValid
            || evidence.ResultHead is not null && !HumanInputRequestLifecycleValidator.ValidateHead(evidence.ResultHead).IsValid)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.previousHead", "Every retained request lifecycle head must be independently valid.");
        }
        if (evidence.FailureCode != HumanInputResponseOperationFailureCode.RequestNotFound)
        {
            if (evidence.PreviousHead is { } previousIdentity
                && !string.Equals(previousIdentity.RequestId, evidence.Request?.RequestId, StringComparison.Ordinal))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.previousHead.requestId", "The observed previous head must belong to the exact requested lifecycle.");
            }
            if (evidence.ResultHead is { } resultIdentity
                && !string.Equals(resultIdentity.RequestId, evidence.Request?.RequestId, StringComparison.Ordinal))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.resultHead.requestId", "The observed result head must belong to the exact requested lifecycle.");
            }
        }
        if (evidence.PreviousHead is { } previous)
        {
            if (evidence.RecordedAtUtc < previous.UpdatedAtUtc)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidUtcTime, "$.recordedAtUtc", "Response evidence time cannot precede the observed request head.");
            }
            var requestMatches = Equals(previous.CurrentRequest, evidence.Request);
            var bindingMatches = Equals(evidence.ObservedBinding, evidence.ExpectedBinding);
            var expectedMatches = requestMatches
                && bindingMatches
                && previous.LifecycleVersion == evidence.ExpectedLifecycleVersion
                && previous.Status == evidence.ExpectedLifecycleStatus;
            var observedShapeIsValid = evidence.FailureCode switch
            {
                HumanInputResponseOperationFailureCode.OptimisticStateConflict => previous.Status == HumanInputRequestLifecycleStatus.Pending
                    && requestMatches
                    && bindingMatches
                    && previous.LifecycleVersion != evidence.ExpectedLifecycleVersion,
                HumanInputResponseOperationFailureCode.StaleResponse => previous.Status == HumanInputRequestLifecycleStatus.Pending
                    && (!requestMatches || !bindingMatches),
                HumanInputResponseOperationFailureCode.RequestTerminal => previous.Status != HumanInputRequestLifecycleStatus.Pending,
                _ => expectedMatches
            };
            if (!observedShapeIsValid)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.previousHead", "Observed request-head truth is inconsistent with the exact expected state and terminal failure classification.");
            }
        }

        if (evidence.Outcome == HumanInputResponseOperationOutcome.Committed && evidence.Selection is not null)
        {
            if (evidence.PreviousHead is not { } committedPrevious || evidence.ResultHead is not { } committedResult
                || committedPrevious.Status != HumanInputRequestLifecycleStatus.Pending
                || committedResult.LifecycleVersion != committedPrevious.LifecycleVersion + 1
                || !string.Equals(committedResult.LastOperationId, evidence.OperationId, StringComparison.Ordinal)
                || committedResult.UpdatedAtUtc != evidence.RecordedAtUtc
                || committedResult.ReminderCount != committedPrevious.ReminderCount
                || !string.Equals(committedResult.SupersedesRequestId, committedPrevious.SupersedesRequestId, StringComparison.Ordinal)
                || !string.Equals(committedResult.SupersededByRequestId, committedPrevious.SupersededByRequestId, StringComparison.Ordinal))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.resultHead", "A selection-producing commit requires one contiguous exact answered request-head successor.");
            }
        }
        else if (evidence.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound)
        {
            if (evidence.PreviousHead is not null || evidence.ResultHead is not null)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.previousHead", "Request-not-found evidence cannot claim an observed request head.");
            }
        }
        else if (evidence.PreviousHead is null || !Equals(evidence.PreviousHead, evidence.ResultHead))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.resultHead", "Every non-selection response disposition, including a committed pending operation, must retain the exact observed request head.");
        }

        if (evidence.Selection is null)
        {
            if (evidence.Outcome == HumanInputResponseOperationOutcome.Committed
                && (evidence.ResultHead?.Status == HumanInputRequestLifecycleStatus.Answered || evidence.ResultHead?.AnswerSelection is not null))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.selection", "Only one exact committed selection may project the request head to answered.");
            }
        }
        else
        {
            ValidateSelectionReference(evidence.Selection, "$.selection", errors);
            if (evidence.Outcome != HumanInputResponseOperationOutcome.Committed
                || evidence.ResultHead?.Status != HumanInputRequestLifecycleStatus.Answered
                || !Equals(evidence.ResultHead.AnswerSelection, evidence.Selection)
                || !Equals(evidence.Selection.Request, evidence.Request))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidLifecycleState, "$.selection", "A selection must commit atomically with the exact answered request head.");
            }
        }
    }

    private static void ValidateEvidenceOperationShape(HumanInputResponseOperationEvidence evidence, List<HumanInputResponseValidationError> errors)
    {
        if (evidence.TargetResponses.IsDefault || evidence.TargetResponses.Length > HumanInputResponseContractLimits.MaxSelectedResponses)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.targetResponses", "Response operation targets must be a bounded initialized immutable array.");
            return;
        }
        for (var index = 0; index < evidence.TargetResponses.Length; index++)
        {
            ValidateResponseReference(evidence.TargetResponses[index], $"$.targetResponses[{index}]", errors);
            if (evidence.TargetResponses[index] is { } reference && !Equals(reference.Request, evidence.Request))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, $"$.targetResponses[{index}]", "Every target response must identify the exact request version.");
            }
        }
        if (evidence.SubmittedResponse is not null)
        {
            ValidateResponseReference(evidence.SubmittedResponse, "$.submittedResponse", errors);
            if (!Equals(evidence.SubmittedResponse.Request, evidence.Request))
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.submittedResponse", "The submitted response must identify the exact request version.");
            }
        }

        var attemptedResponseRequired = RequiresAttemptedResponse(evidence);
        if (attemptedResponseRequired != (evidence.AttemptedResponse is not null))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.attemptedResponse", "An exact attempted response is retained only for a submit failure reached after response-content inspection.");
        }
        if (evidence.AttemptedResponse is not null)
        {
            if (!HumanInputResponseArtifactSnapshot.TryCaptureBoundedAttempt(evidence.AttemptedResponse, out var attempt, out _)
                || attempt is null)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.attemptedResponse", "The attempted response must be a bounded immutable artifact with matching value and artifact hashes.");
            }
            else if (!Equals(attempt.Request, evidence.Request)
                || !Equals(attempt.Binding, evidence.ExpectedBinding)
                || !Equals(attempt.Binding, evidence.ObservedBinding)
                || !Equals(attempt.ActorId, evidence.ActorId)
                || !string.Equals(attempt.RespondentRoleId, evidence.ActorRoleId, StringComparison.Ordinal)
                || attempt.SubmittedAtUtc != evidence.RecordedAtUtc)
            {
                Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.attemptedResponse", "The attempted response must exactly match the inspected request, trusted binding, actor, role, and evidence time.");
            }
        }

        switch (evidence.Kind)
        {
            case HumanInputResponseOperationKind.Submit:
                if (evidence.TargetResponses.Length != 0 || evidence.Outcome == HumanInputResponseOperationOutcome.Committed != (evidence.SubmittedResponse is not null))
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.submittedResponse", "Only a committed submit appends one response and submit has no target-response list.");
                }
                break;
            case HumanInputResponseOperationKind.Withdraw:
                if (evidence.SubmittedResponse is not null || evidence.TargetResponses.Length != 1 || evidence.Selection is not null)
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.targetResponses", "Withdraw targets exactly one retained response, appends no response, and cannot answer the request.");
                }
                break;
            case HumanInputResponseOperationKind.Select:
                if (evidence.SubmittedResponse is not null || evidence.TargetResponses.Length != 1
                    || evidence.Outcome == HumanInputResponseOperationOutcome.Committed != (evidence.Selection is not null))
                {
                    Add(errors, HumanInputResponseValidationErrorCode.InvalidEvidenceShape, "$.selection", "Only a committed manual select retains exactly one exact selected response.");
                }
                break;
        }
    }

    private static bool RequiresAttemptedResponse(HumanInputResponseOperationEvidence evidence)
        => evidence.Kind == HumanInputResponseOperationKind.Submit
            && evidence.Outcome != HumanInputResponseOperationOutcome.Committed
            && evidence.FailureCode is HumanInputResponseOperationFailureCode.MalformedResponse
                or HumanInputResponseOperationFailureCode.DuplicateResponse
                or HumanInputResponseOperationFailureCode.ResponseLimitExceeded
                or HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded;

    private static void ValidateVocabulary(HumanInputResponseOperationEvidence evidence, List<HumanInputResponseValidationError> errors)
    {
        if (!Enum.IsDefined(evidence.Kind) || evidence.Kind == HumanInputResponseOperationKind.Unknown)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidOperationKind, "$.kind", "A supported response operation kind is required.");
        }
        if (!Enum.IsDefined(evidence.Outcome) || evidence.Outcome == HumanInputResponseOperationOutcome.Unknown)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidOperationOutcome, "$.outcome", "A supported terminal response operation outcome is required.");
        }
        if (!Enum.IsDefined(evidence.FailureCode) || evidence.FailureCode == HumanInputResponseOperationFailureCode.Unknown
            || !IsOutcomeFailurePair(evidence.Outcome, evidence.FailureCode)
            || !IsOperationFailurePair(evidence.Kind, evidence.FailureCode))
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidFailureCode, "$.failureCode", "Response operation outcome, kind, and value-free failure classification are inconsistent.");
        }
    }

    private static bool IsOutcomeFailurePair(HumanInputResponseOperationOutcome outcome, HumanInputResponseOperationFailureCode failure) => outcome switch
    {
        HumanInputResponseOperationOutcome.Committed => failure == HumanInputResponseOperationFailureCode.None,
        HumanInputResponseOperationOutcome.Conflict => failure is HumanInputResponseOperationFailureCode.OperationIntentConflict
            or HumanInputResponseOperationFailureCode.OptimisticStateConflict
            or HumanInputResponseOperationFailureCode.ResponseAlreadyWithdrawn
            or HumanInputResponseOperationFailureCode.DuplicateResponse
            or HumanInputResponseOperationFailureCode.StaleResponse
            or HumanInputResponseOperationFailureCode.SelectionConflict,
        HumanInputResponseOperationOutcome.Rejected => failure is HumanInputResponseOperationFailureCode.RequestTerminal
            or HumanInputResponseOperationFailureCode.LateResponse
            or HumanInputResponseOperationFailureCode.MalformedResponse
            or HumanInputResponseOperationFailureCode.IneligibleRespondent
            or HumanInputResponseOperationFailureCode.IneligibleSelector,
        HumanInputResponseOperationOutcome.NotFound => failure is HumanInputResponseOperationFailureCode.RequestNotFound
            or HumanInputResponseOperationFailureCode.ResponseNotFound,
        HumanInputResponseOperationOutcome.LimitExceeded => failure is HumanInputResponseOperationFailureCode.ResponseLimitExceeded
            or HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded
            or HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded,
        _ => false
    };

    private static bool IsOperationFailurePair(HumanInputResponseOperationKind kind, HumanInputResponseOperationFailureCode failure) => failure switch
    {
        HumanInputResponseOperationFailureCode.None => kind is HumanInputResponseOperationKind.Submit or HumanInputResponseOperationKind.Withdraw or HumanInputResponseOperationKind.Select,
        HumanInputResponseOperationFailureCode.OperationIntentConflict => true,
        HumanInputResponseOperationFailureCode.OptimisticStateConflict => true,
        HumanInputResponseOperationFailureCode.RequestNotFound => true,
        HumanInputResponseOperationFailureCode.RequestTerminal => true,
        HumanInputResponseOperationFailureCode.ResponseNotFound => kind is HumanInputResponseOperationKind.Withdraw or HumanInputResponseOperationKind.Select,
        HumanInputResponseOperationFailureCode.ResponseAlreadyWithdrawn => kind == HumanInputResponseOperationKind.Withdraw,
        HumanInputResponseOperationFailureCode.DuplicateResponse => kind == HumanInputResponseOperationKind.Submit,
        HumanInputResponseOperationFailureCode.StaleResponse => kind is HumanInputResponseOperationKind.Submit
            or HumanInputResponseOperationKind.Withdraw
            or HumanInputResponseOperationKind.Select,
        HumanInputResponseOperationFailureCode.LateResponse => kind is HumanInputResponseOperationKind.Submit
            or HumanInputResponseOperationKind.Select,
        HumanInputResponseOperationFailureCode.MalformedResponse => kind == HumanInputResponseOperationKind.Submit,
        HumanInputResponseOperationFailureCode.IneligibleRespondent => kind is HumanInputResponseOperationKind.Submit or HumanInputResponseOperationKind.Withdraw,
        HumanInputResponseOperationFailureCode.IneligibleSelector => kind == HumanInputResponseOperationKind.Select,
        HumanInputResponseOperationFailureCode.SelectionConflict => kind == HumanInputResponseOperationKind.Select,
        HumanInputResponseOperationFailureCode.ResponseLimitExceeded => kind == HumanInputResponseOperationKind.Submit,
        HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded => true,
        HumanInputResponseOperationFailureCode.LifecycleVersionLimitExceeded => kind is HumanInputResponseOperationKind.Submit
            or HumanInputResponseOperationKind.Select,
        _ => false
    };

    private static void ValidateResponseReference(HumanInputResponseReference? reference, string path, List<HumanInputResponseValidationError> errors)
    {
        if (reference is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidResponseReference, path, "An exact immutable response reference is required.");
            return;
        }
        ValidateSchema(reference.SchemaVersion, path + ".schemaVersion", errors);
        ValidateIdentifier(reference.ResponseId, path + ".responseId", errors);
        ValidateRequestReference(reference.Request, path + ".request", errors);
        ValidateSha256(reference.ValueHash, path + ".valueHash", HumanInputResponseValidationErrorCode.InvalidResponseReference, errors);
        ValidateSha256(reference.ResponseHash, path + ".responseHash", HumanInputResponseValidationErrorCode.InvalidResponseReference, errors);
    }

    private static void ValidateSelectionReference(HumanInputResponseSelectionReference? reference, string path, List<HumanInputResponseValidationError> errors)
    {
        if (reference is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidSelectionReference, path, "An exact immutable response selection reference is required.");
            return;
        }
        ValidateSchema(reference.SchemaVersion, path + ".schemaVersion", errors);
        ValidateIdentifier(reference.SelectionId, path + ".selectionId", errors);
        ValidateRequestReference(reference.Request, path + ".request", errors);
        ValidateSha256(reference.SelectionHash, path + ".selectionHash", HumanInputResponseValidationErrorCode.InvalidSelectionReference, errors);
    }

    private static void ValidateRequestReference(HumanInputRequestReference? reference, string path, List<HumanInputResponseValidationError> errors)
    {
        if (reference is null || !HumanInputRequestLifecycleValidator.ValidateReference(reference).IsValid)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidRequestReference, path, "An exact canonical immutable request reference is required.");
        }
    }

    private static void ValidateBinding(HumanInputRequestBinding? binding, string path, List<HumanInputResponseValidationError> errors)
    {
        if (binding is null)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidBinding, path, "An exact request binding is required.");
            return;
        }
        ValidateWorkspaceId(binding.WorkspaceId, path + ".workspaceId", errors);
        ValidateIdentifier(binding.LoopGraphId, path + ".loopGraphId", HumanInputResponseValidationErrorCode.InvalidBinding, errors);
        ValidateIdentifier(binding.LoopRevisionId, path + ".loopRevisionId", HumanInputResponseValidationErrorCode.InvalidBinding, errors);
        ValidateIdentifier(binding.NodeId, path + ".nodeId", HumanInputResponseValidationErrorCode.InvalidBinding, errors);
        ValidateIdentifier(binding.RunId, path + ".runId", HumanInputResponseValidationErrorCode.InvalidBinding, errors);
        ValidateIdentifier(binding.CheckpointId, path + ".checkpointId", HumanInputResponseValidationErrorCode.InvalidBinding, errors);
    }

    private static bool IsEligible(HumanInputEligibleRespondent[] respondents, string actorId, string roleId)
        => respondents.Any(respondent => respondent is not null
            && string.Equals(respondent.RespondentId, actorId, StringComparison.Ordinal)
            && string.Equals(respondent.RespondentRoleId, roleId, StringComparison.Ordinal));

    private static void ValidateSchema(int schemaVersion, string path, List<HumanInputResponseValidationError> errors)
    {
        if (schemaVersion != HumanInputResponseContractLimits.CurrentSchemaVersion)
        {
            Add(errors, HumanInputResponseValidationErrorCode.UnsupportedSchemaVersion, path, "Authenticated Human Input response schema version must be 1.");
        }
    }

    private static void ValidateIdentifier(string? value, string path, List<HumanInputResponseValidationError> errors)
        => ValidateIdentifier(value, path, HumanInputResponseValidationErrorCode.InvalidIdentifier, errors);

    private static void ValidateIdentifier(string? value, string path, HumanInputResponseValidationErrorCode code, List<HumanInputResponseValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(value))
        {
            Add(errors, code, path, "A bounded canonical lowercase Human Input identifier is required.");
        }
    }

    private static void ValidateWorkspaceId(string? value, string path, List<HumanInputResponseValidationError> errors)
    {
        if (!ContextualRoleWorkspaceId.IsValid(value)) Add(errors, HumanInputResponseValidationErrorCode.InvalidBinding, path, "A canonical workspace-sha256 workspace scope is required.");
    }

    private static void ValidateSha256(string? value, string path, HumanInputResponseValidationErrorCode code, List<HumanInputResponseValidationError> errors)
    {
        if (!HumanInputResponseHashRules.IsSha256(value))
        {
            Add(errors, code, path, "A 64-character lowercase SHA-256 digest is required.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<HumanInputResponseValidationError> errors)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            Add(errors, HumanInputResponseValidationErrorCode.InvalidUtcTime, path, "A non-default UTC time is required.");
        }
    }

    private static HumanInputResponseValidationResult Result(List<HumanInputResponseValidationError> errors) => new(errors);

    private static void Add(List<HumanInputResponseValidationError> errors, HumanInputResponseValidationErrorCode code, string path, string message)
    {
        if (errors.Count >= HumanInputResponseContractLimits.MaxValidationErrors)
        {
            return;
        }
        var boundedPath = path.Length <= HumanInputResponseContractLimits.MaxErrorPathCharacters
            ? path
            : path[..HumanInputResponseContractLimits.MaxErrorPathCharacters];
        errors.Add(new HumanInputResponseValidationError(code, boundedPath, message));
    }
}
