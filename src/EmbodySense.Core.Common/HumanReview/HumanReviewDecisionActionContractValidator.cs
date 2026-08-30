using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates strict schema-1 non-approval decision-action artifacts without executing, approving, or releasing work.</summary>
public static class HumanReviewDecisionActionContractValidator
{
    /// <summary>Validates a complete durable action chain against its exact immutable Human Review request.</summary>
    public static HumanReviewContractValidationResult ValidateState(HumanReviewRequest? request, HumanReviewDecisionActionState? state)
    {
        var errors = new List<HumanReviewContractValidationError>();
        if (request is null || !HumanReviewContractValidator.ValidateRequest(request).IsValid)
        {
            Add(errors, "action_request_required", "$.request", "A valid immutable Human Review request is required.");
            return Result(errors);
        }
        if (state is null)
        {
            Add(errors, "action_state_required", "$", "A durable action state is required.");
            return Result(errors);
        }

        Schema(state.SchemaVersion, "$.schemaVersion", errors);
        Reservation(request, state.Reservation, "$.reservation", errors);
        Hash(state.BindingHash, "$.bindingHash", errors);
        if (!string.Equals(state.BindingHash, request.Binding.BindingHash, StringComparison.Ordinal)) Add(errors, "action_binding_mismatch", "$.bindingHash", "Action state must retain the reviewed immutable binding hash.");
        Generation(state.ExpectedGeneration, "$.expectedGeneration", errors);
        if (state.ReservedLifecycleVersion < 1) Add(errors, "action_lifecycle_version_invalid", "$.reservedLifecycleVersion", "Action reservation must retain a positive whole-run lifecycle version.");
        if (state.Claims.IsDefault || state.Claims.Length > HumanReviewContractLimits.MaxContinuationClaims) Add(errors, "action_claim_count_invalid", "$.claims", "Action claim history must be defined and bounded.");
        if (state.Completion is not null && state.Retirement is not null) Add(errors, "action_terminal_conflict", "$", "An action chain may retain completion or retirement, never both.");

        if (state.Wake is null)
        {
            if (!state.Claims.IsDefaultOrEmpty || state.Completion is not null || state.Retirement is not null) Add(errors, "action_wake_required", "$.wake", "Claims and terminal artifacts require one immutable published wake.");
        }
        else
        {
            Wake(request, state, state.Wake, errors);
            Claims(state, errors);
            Completion(state, errors);
            Retirement(state, errors);
        }

        if (!HumanReviewDecisionActionContractHash.MatchesState(state)) Add(errors, "action_state_hash_mismatch", "$.stateHash", "Action state must carry exact canonical nested and state hashes.");
        return Result(errors);
    }

    private static void Reservation(HumanReviewRequest request, HumanReviewDecisionActionReservation? value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value is null) { Add(errors, "action_reservation_required", path, "A non-approval action reservation is required."); return; }
        Schema(value.SchemaVersion, path + ".schemaVersion", errors);
        Identifier(value.ReservationId, path + ".reservationId", errors);
        Request(request, value.Request, path + ".request", errors);
        Decision(value.Decision, path + ".decision", errors);
        if (!Utc(value.ReservedAtUtc) || value.ReservedAtUtc > request.Timing.ExpiresAtUtc) Add(errors, "action_reservation_time_invalid", path + ".reservedAtUtc", "Reservation time must be trusted UTC and inside the review window.");
        Provenance(value.Provenance, value.ReservedAtUtc, HumanReviewProvenanceKind.Server, HumanReviewProvenanceKind.Coordinator, path + ".provenance", errors);
        Hash(value.ReservationHash, path + ".reservationHash", errors);
        if (!HumanReviewDecisionActionContractHash.MatchesReservation(value)) Add(errors, "action_reservation_hash_mismatch", path + ".reservationHash", "Reservation must carry its exact canonical hash.");
    }

    private static void Wake(HumanReviewRequest request, HumanReviewDecisionActionState state, HumanReviewDecisionActionWake value, List<HumanReviewContractValidationError> errors)
    {
        const string Path = "$.wake";
        Schema(value.SchemaVersion, Path + ".schemaVersion", errors);
        Identifier(value.WakeId, Path + ".wakeId", errors);
        Request(request, value.Request, Path + ".request", errors);
        Decision(value.Decision, Path + ".decision", errors);
        ReservationReference(state.Reservation, value.Reservation, Path + ".reservation", errors);
        Hash(value.BindingHash, Path + ".bindingHash", errors);
        if (!string.Equals(value.BindingHash, state.BindingHash, StringComparison.Ordinal)) Add(errors, "action_wake_binding_mismatch", Path + ".bindingHash", "Wake must retain the exact reserved binding hash.");
        Generation(value.ExpectedGeneration, Path + ".expectedGeneration", errors);
        if (value.ExpectedGeneration != state.ExpectedGeneration) Add(errors, "action_wake_generation_mismatch", Path + ".expectedGeneration", "Wake must retain the exact reserved generation.");
        if (!Utc(value.PublishedAtUtc) || !Utc(value.ExpiresAtUtc) || value.PublishedAtUtc < state.Reservation.ReservedAtUtc || value.ExpiresAtUtc <= value.PublishedAtUtc || value.ExpiresAtUtc > request.Timing.ExpiresAtUtc) Add(errors, "action_wake_window_invalid", Path + ".expiresAtUtc", "Wake must use a trusted, bounded window inside the accepted review window.");
        Provenance(value.Provenance, value.PublishedAtUtc, HumanReviewProvenanceKind.Coordinator, HumanReviewProvenanceKind.Server, Path + ".provenance", errors);
        Hash(value.WakeHash, Path + ".wakeHash", errors);
        if (!HumanReviewDecisionActionContractHash.MatchesWake(value)) Add(errors, "action_wake_hash_mismatch", Path + ".wakeHash", "Wake must carry its exact canonical hash.");
    }

    private static void Claims(HumanReviewDecisionActionState state, List<HumanReviewContractValidationError> errors)
    {
        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        var claimHashes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < state.Claims.Length; index++)
        {
            var claim = state.Claims[index];
            var path = "$.claims[" + index + "]";
            if (claim is null) { Add(errors, "action_claim_required", path, "A non-null action claim is required."); continue; }
            Schema(claim.SchemaVersion, path + ".schemaVersion", errors);
            Identifier(claim.ClaimId, path + ".claimId", errors);
            if (!claimIds.Add(claim.ClaimId)) Add(errors, "action_claim_id_duplicate", path + ".claimId", "Action claim identities must be unique.");
            WakeReference(state.Wake!, claim.Wake, path + ".wake", errors);
            ReservationReference(state.Reservation, claim.Reservation, path + ".reservation", errors);
            Generation(claim.ExpectedGeneration, path + ".expectedGeneration", errors);
            if (claim.ExpectedGeneration != state.ExpectedGeneration) Add(errors, "action_claim_generation_mismatch", path + ".expectedGeneration", "Claim must retain the exact action generation.");
            Identifier(claim.WorkerId, path + ".workerId", errors);
            if (!Utc(claim.ClaimedAtUtc) || !Utc(claim.LeaseExpiresAtUtc) || claim.ClaimedAtUtc < state.Wake!.PublishedAtUtc || claim.ClaimedAtUtc >= state.Wake.ExpiresAtUtc || claim.LeaseExpiresAtUtc <= claim.ClaimedAtUtc || claim.LeaseExpiresAtUtc > state.Wake.ExpiresAtUtc || claim.LeaseExpiresAtUtc - claim.ClaimedAtUtc > HumanReviewContractLimits.MaxContinuationClaimLease) Add(errors, "action_claim_lease_invalid", path + ".leaseExpiresAtUtc", "Claim lease must be trusted, bounded, and contained by the wake window.");
            if (index > 0 && claim.ClaimedAtUtc <= state.Claims[index - 1].LeaseExpiresAtUtc) Add(errors, "action_claim_takeover_early", path + ".claimedAtUtc", "A later claim can take over only after strict expiry of the prior claim.");
            Provenance(claim.Provenance, claim.ClaimedAtUtc, HumanReviewProvenanceKind.Coordinator, path + ".provenance", errors);
            Hash(claim.ClaimHash, path + ".claimHash", errors);
            if (!claimHashes.Add(claim.ClaimHash)) Add(errors, "action_claim_hash_duplicate", path + ".claimHash", "Action claim hashes must be unique.");
            if (!HumanReviewDecisionActionContractHash.MatchesClaim(claim)) Add(errors, "action_claim_hash_mismatch", path + ".claimHash", "Claim must carry its exact canonical hash.");
        }
    }

    private static void Completion(HumanReviewDecisionActionState state, List<HumanReviewContractValidationError> errors)
    {
        if (state.Completion is not { } value) return;
        const string Path = "$.completion";
        var claim = state.Claims.IsDefaultOrEmpty ? null : state.Claims[^1];
        if (claim is null) Add(errors, "action_completion_claim_required", Path, "Completion requires one exact active claim.");
        Schema(value.SchemaVersion, Path + ".schemaVersion", errors);
        Identifier(value.CompletionId, Path + ".completionId", errors);
        WakeReference(state.Wake!, value.Wake, Path + ".wake", errors);
        ClaimReference(claim, value.Claim, Path + ".claim", errors);
        ReservationReference(state.Reservation, value.Reservation, Path + ".reservation", errors);
        Generation(value.ExpectedGeneration, Path + ".expectedGeneration", errors);
        if (value.ExpectedGeneration != state.ExpectedGeneration) Add(errors, "action_completion_generation_mismatch", Path + ".expectedGeneration", "Completion must retain the exact action generation.");
        if (!ExpectedDisposition(state.Reservation.Decision.Kind, value.Disposition)) Add(errors, "action_completion_disposition_mismatch", Path + ".disposition", "Completion disposition must match the accepted non-approval decision kind.");
        Hash(value.ResultHash, Path + ".resultHash", errors);
        Hash(value.FrontierReceiptHash, Path + ".frontierReceiptHash", errors);
        if (claim is not null && (!Utc(value.CompletedAtUtc) || value.CompletedAtUtc < claim.ClaimedAtUtc || value.CompletedAtUtc >= claim.LeaseExpiresAtUtc)) Add(errors, "action_completion_time_invalid", Path + ".completedAtUtc", "Completion must occur inside the exact active claim lease.");
        Evidence(value.Evidence, Path + ".evidence", errors);
        Provenance(value.Provenance, value.CompletedAtUtc, HumanReviewProvenanceKind.Coordinator, Path + ".provenance", errors);
        Hash(value.CompletionHash, Path + ".completionHash", errors);
        if (!HumanReviewDecisionActionContractHash.MatchesCompletion(value)) Add(errors, "action_completion_hash_mismatch", Path + ".completionHash", "Completion must carry its exact canonical hash.");
    }

    private static void Retirement(HumanReviewDecisionActionState state, List<HumanReviewContractValidationError> errors)
    {
        if (state.Retirement is not { } value) return;
        const string Path = "$.retirement";
        Schema(value.SchemaVersion, Path + ".schemaVersion", errors);
        Identifier(value.RetirementId, Path + ".retirementId", errors);
        WakeReference(state.Wake!, value.Wake, Path + ".wake", errors);
        ReservationReference(state.Reservation, value.Reservation, Path + ".reservation", errors);
        Generation(value.ExpectedGeneration, Path + ".expectedGeneration", errors);
        if (value.ExpectedGeneration != state.ExpectedGeneration) Add(errors, "action_retirement_generation_mismatch", Path + ".expectedGeneration", "Retirement must retain the exact action generation.");
        if (!Enum.IsDefined(value.Outcome) || value.Outcome is HumanReviewContinuationOutcome.Unknown or HumanReviewContinuationOutcome.Completed) Add(errors, "action_retirement_outcome_invalid", Path + ".outcome", "Retirement must retain a closed non-completion outcome.");
        if (!ExpectedRetirement(value.Outcome, value.Reason)) Add(errors, "action_retirement_reason_invalid", Path + ".reason", "Retirement reason must exactly match its closed non-completion outcome.");
        if (!Utc(value.RetiredAtUtc) || value.RetiredAtUtc < state.Wake!.PublishedAtUtc || value.Outcome == HumanReviewContinuationOutcome.Expired && value.RetiredAtUtc < state.Wake.ExpiresAtUtc) Add(errors, "action_retirement_time_invalid", Path + ".retiredAtUtc", "Retirement must occur at trusted UTC and expiry cannot predate wake expiry.");
        Evidence(value.Evidence, Path + ".evidence", errors);
        Provenance(value.Provenance, value.RetiredAtUtc, HumanReviewProvenanceKind.Coordinator, Path + ".provenance", errors);
        Hash(value.RetirementHash, Path + ".retirementHash", errors);
        if (!HumanReviewDecisionActionContractHash.MatchesRetirement(value)) Add(errors, "action_retirement_hash_mismatch", Path + ".retirementHash", "Retirement must carry its exact canonical hash.");
    }

    /// <summary>Gets whether an action still binds the exact current Human Review lifecycle and accepted-decision head.</summary>
    /// <remarks>This check deliberately does not make a terminal action eligible; callers must separately require their operation's nonterminal predecessor.</remarks>
    public static bool IsCurrentActionHead(HumanReviewRunState? review, HumanReviewDecisionActionState? action)
    {
        try
        {
            if (review?.Request is null || review.Lifecycle is null || action is null || review.AcceptedDecisions.IsDefaultOrEmpty || !ValidateState(review.Request, action).IsValid)
            {
                return false;
            }

            var expected = action.Reservation.Decision;
            return SameDecision(expected, review.AcceptedDecisions[^1])
                && SameDecision(expected, review.Lifecycle.LastDecision)
                && review.Lifecycle.Status == LifecycleStatus(expected.Kind);
        }
        catch
        {
            return false;
        }
    }

    private static bool ExpectedDisposition(HumanReviewDecisionKind kind, HumanReviewDecisionActionDisposition disposition) => (kind, disposition) switch { (HumanReviewDecisionKind.Reject, HumanReviewDecisionActionDisposition.Rejected) => true, (HumanReviewDecisionKind.Cancel, HumanReviewDecisionActionDisposition.Cancelled) => true, (HumanReviewDecisionKind.RequestInformation, HumanReviewDecisionActionDisposition.InformationParked) => true, _ => false };
    private static bool ExpectedRetirement(HumanReviewContinuationOutcome outcome, HumanReviewDecisionActionRetirementReason reason) => (outcome, reason) switch { (HumanReviewContinuationOutcome.Expired, HumanReviewDecisionActionRetirementReason.Expired) => true, (HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid) => true, (HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.ReleaseInvalid) => true, (HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.ClaimLimitExceeded) => true, _ => false };
    private static HumanReviewLifecycleStatus LifecycleStatus(HumanReviewDecisionKind kind) => kind switch { HumanReviewDecisionKind.Reject => HumanReviewLifecycleStatus.Rejected, HumanReviewDecisionKind.Cancel => HumanReviewLifecycleStatus.Cancelled, HumanReviewDecisionKind.RequestInformation => HumanReviewLifecycleStatus.AwaitingInformation, _ => HumanReviewLifecycleStatus.Unknown };
    private static bool SameDecision(HumanReviewDecisionReference expected, HumanReviewDecision? value) => value is not null && SameDecision(expected, new HumanReviewDecisionReference(value.DecisionId, value.DecisionOperationId, value.Kind, value.DecisionHash));
    private static bool SameDecision(HumanReviewDecisionReference expected, HumanReviewDecisionReference? value) => value is not null && expected.DecisionId == value.DecisionId && expected.DecisionOperationId == value.DecisionOperationId && expected.Kind == value.Kind && expected.DecisionHash == value.DecisionHash;
    private static void Request(HumanReviewRequest request, HumanReviewRequestReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.RequestId != request.RequestId || value.RequestHash != request.RequestHash) Add(errors, "action_request_mismatch", path, "Action artifact must reference the exact immutable request."); }
    private static void Decision(HumanReviewDecisionReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null || !HumanReviewIdentifier.IsValid(value.DecisionId) || !HumanReviewIdentifier.IsValid(value.DecisionOperationId) || !HumanReviewContractHash.IsSha256(value.DecisionHash) || value.Kind is not (HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation)) Add(errors, "action_decision_invalid", path, "Action reservation may reference only one canonical accepted non-approval decision."); }
    private static void ReservationReference(HumanReviewDecisionActionReservation reservation, HumanReviewDecisionActionReservationReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.ReservationId != reservation.ReservationId || value.ReservationHash != reservation.ReservationHash) Add(errors, "action_reservation_reference_mismatch", path, "Action artifact must reference the exact reservation."); }
    private static void WakeReference(HumanReviewDecisionActionWake wake, HumanReviewDecisionActionWakeReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.WakeId != wake.WakeId || value.WakeHash != wake.WakeHash) Add(errors, "action_wake_reference_mismatch", path, "Action artifact must reference the exact wake."); }
    private static void ClaimReference(HumanReviewDecisionActionClaim? claim, HumanReviewDecisionActionClaimReference? value, string path, List<HumanReviewContractValidationError> errors) { if (claim is null || value is null || value.ClaimId != claim.ClaimId || value.ClaimHash != claim.ClaimHash) Add(errors, "action_claim_reference_mismatch", path, "Action completion must reference the exact active claim."); }
    private static void Evidence(System.Collections.Immutable.ImmutableArray<HumanReviewRedactedPreview> values, string path, List<HumanReviewContractValidationError> errors) { if (values.IsDefault || values.Length > HumanReviewContractLimits.MaxContinuationEvidence || values.Any(value => value is null || !HumanReviewContractHash.MatchesPreview(value))) Add(errors, "action_evidence_invalid", path, "Action evidence must be defined, bounded, redacted, and canonical."); }
    private static void Provenance(HumanReviewProvenance? value, DateTimeOffset timestamp, HumanReviewProvenanceKind requiredKind, string path, List<HumanReviewContractValidationError> errors) => Provenance(value, timestamp, requiredKind, requiredKind, path, errors);
    private static void Provenance(HumanReviewProvenance? value, DateTimeOffset timestamp, HumanReviewProvenanceKind first, HumanReviewProvenanceKind second, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.Kind != first && value.Kind != second || value.ObservedAtUtc != timestamp || !HumanReviewContractHash.MatchesProvenance(value)) Add(errors, "action_provenance_invalid", path, "Action artifact must retain exact canonical trusted provenance."); }
    private static void Schema(int value, string path, List<HumanReviewContractValidationError> errors) { if (value != HumanReviewContractLimits.CurrentSchemaVersion) Add(errors, "unsupported_schema_version", path, "Only schema version 1 is supported."); }
    private static void Identifier(string? value, string path, List<HumanReviewContractValidationError> errors) { if (!HumanReviewIdentifier.IsValid(value)) Add(errors, "invalid_identifier", path, "Identifier must be bounded canonical lowercase ASCII."); }
    private static void Hash(string? value, string path, List<HumanReviewContractValidationError> errors) { if (!HumanReviewContractHash.IsSha256(value)) Add(errors, "invalid_hash", path, "A lowercase SHA-256 hash is required."); }
    private static void Generation(long value, string path, List<HumanReviewContractValidationError> errors) { if (value is < 1 or > HumanReviewContractLimits.MaxVersion) Add(errors, "invalid_generation", path, "Generation must be positive and bounded."); }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message) => errors.Add(new HumanReviewContractValidationError(code, path, message));
    private static HumanReviewContractValidationResult Result(List<HumanReviewContractValidationError> errors) => new(errors.AsReadOnly());
}
