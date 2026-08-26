using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates strict, dependency-free continuation artifacts without claiming, publishing, persisting, authorizing, or releasing a loop.</summary>
public static class HumanReviewContinuationContractValidator
{
    /// <summary>Validates a wake against the exact request and approved continuation reservation that it may represent.</summary>
    public static HumanReviewContractValidationResult ValidateWake(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, HumanReviewContinuationWake? wake)
    {
        var errors = Start(request, reservation);
        if (wake is null) { Add(errors, "wake_required", "$", "A continuation wake is required."); return Result(errors); }
        ValidateSchema(wake.SchemaVersion, "$.schemaVersion", errors);
        ValidateId(wake.WakeId, "$.wakeId", errors);
        ExactRequest(request, wake.Request, "$.request", errors);
        ExactDecision(reservation, wake.Decision, "$.decision", errors);
        ExactReservation(reservation, wake.Reservation, "$.reservation", errors);
        Hash(wake.BindingHash, "$.bindingHash", errors);
        if (request is not null && !string.Equals(wake.BindingHash, request.Binding.BindingHash, StringComparison.Ordinal)) Add(errors, "wake_binding_mismatch", "$.bindingHash", "Wake binding must exactly match the immutable reviewed frontier binding.");
        Generation(wake.ExpectedGeneration, "$.expectedGeneration", errors);
        if (!Utc(wake.PublishedAtUtc) || !Utc(wake.ExpiresAtUtc) || wake.PublishedAtUtc < reservation?.ReservedAtUtc || wake.ExpiresAtUtc <= wake.PublishedAtUtc) Add(errors, "invalid_wake_window", "$.expiresAtUtc", "Wake publication and expiry must be trusted UTC, ordered, and not predate reservation.");
        Provenance(wake.Provenance, wake.PublishedAtUtc, "$.provenance", errors);
        Hash(wake.WakeHash, "$.wakeHash", errors);
        if (errors.Count == 0 && !HumanReviewContinuationContractHash.MatchesWake(wake)) Add(errors, "wake_hash_mismatch", "$.wakeHash", "Wake hash must exactly match its canonical contract.");
        return Result(errors);
    }

    /// <summary>Validates a claim against its exact wake and reservation without assigning runtime ownership.</summary>
    public static HumanReviewContractValidationResult ValidateClaim(HumanReviewContinuationWake? wake, HumanReviewContinuationReservation? reservation, HumanReviewContinuationClaim? claim)
    {
        var errors = new List<HumanReviewContractValidationError>();
        if (wake is null) { Add(errors, "wake_required", "$.wake", "An exact continuation wake is required."); return Result(errors); }
        if (reservation is null) { Add(errors, "reservation_required", "$.reservation", "An exact continuation reservation is required."); return Result(errors); }
        if (claim is null) { Add(errors, "claim_required", "$", "A continuation claim is required."); return Result(errors); }
        ValidateSchema(claim.SchemaVersion, "$.schemaVersion", errors);
        ValidateId(claim.ClaimId, "$.claimId", errors);
        ExactWake(wake, claim.Wake, "$.wake", errors);
        ExactReservation(reservation, claim.Reservation, "$.reservation", errors);
        ExactGeneration(wake.ExpectedGeneration, claim.ExpectedGeneration, "$.expectedGeneration", errors);
        ValidateId(claim.WorkerId, "$.workerId", errors);
        if (!Utc(claim.ClaimedAtUtc) || !Utc(claim.LeaseExpiresAtUtc) || claim.ClaimedAtUtc < wake.PublishedAtUtc || claim.ClaimedAtUtc >= wake.ExpiresAtUtc || claim.LeaseExpiresAtUtc <= claim.ClaimedAtUtc || claim.LeaseExpiresAtUtc > wake.ExpiresAtUtc || claim.LeaseExpiresAtUtc - claim.ClaimedAtUtc > HumanReviewContractLimits.MaxContinuationClaimLease) Add(errors, "invalid_claim_lease", "$.leaseExpiresAtUtc", "Claim lease must be finite, trusted UTC, bounded, and contained by the wake window.");
        Provenance(claim.Provenance, claim.ClaimedAtUtc, "$.provenance", errors);
        Hash(claim.ClaimHash, "$.claimHash", errors);
        if (errors.Count == 0 && !HumanReviewContinuationContractHash.MatchesClaim(claim)) Add(errors, "claim_hash_mismatch", "$.claimHash", "Claim hash must exactly match its canonical contract.");
        return Result(errors);
    }

    /// <summary>Validates a terminal completion against the exact wake, reservation, and active claim without releasing work.</summary>
    public static HumanReviewContractValidationResult ValidateCompletion(HumanReviewContinuationWake? wake, HumanReviewContinuationReservation? reservation, HumanReviewContinuationClaim? claim, HumanReviewContinuationCompletion? completion)
    {
        var errors = new List<HumanReviewContractValidationError>();
        if (wake is null || reservation is null || claim is null) { Add(errors, "completion_context_required", "$", "Exact wake, reservation, and claim context is required."); return Result(errors); }
        if (completion is null) { Add(errors, "completion_required", "$", "A continuation completion is required."); return Result(errors); }
        ValidateSchema(completion.SchemaVersion, "$.schemaVersion", errors);
        ValidateId(completion.CompletionId, "$.completionId", errors);
        ExactWake(wake, completion.Wake, "$.wake", errors);
        ExactClaim(claim, completion.Claim, "$.claim", errors);
        ExactReservation(reservation, completion.Reservation, "$.reservation", errors);
        ExactGeneration(wake.ExpectedGeneration, completion.ExpectedGeneration, "$.expectedGeneration", errors);
        if (!Utc(completion.CompletedAtUtc) || completion.CompletedAtUtc < claim.ClaimedAtUtc || completion.CompletedAtUtc > claim.LeaseExpiresAtUtc) Add(errors, "invalid_completion_time", "$.completedAtUtc", "Completion must occur at trusted UTC inside the exact active claim lease.");
        Evidence(completion.Evidence, "$.evidence", errors);
        Provenance(completion.Provenance, completion.CompletedAtUtc, "$.provenance", errors);
        Hash(completion.CompletionHash, "$.completionHash", errors);
        if (errors.Count == 0 && !HumanReviewContinuationContractHash.MatchesCompletion(completion)) Add(errors, "completion_hash_mismatch", "$.completionHash", "Completion hash must exactly match its canonical contract.");
        return Result(errors);
    }

    /// <summary>Validates a terminal fail-closed retirement against the exact wake and reservation without allowing a completion outcome.</summary>
    public static HumanReviewContractValidationResult ValidateRetirement(HumanReviewContinuationWake? wake, HumanReviewContinuationReservation? reservation, HumanReviewContinuationRetirement? retirement)
    {
        var errors = new List<HumanReviewContractValidationError>();
        if (wake is null || reservation is null) { Add(errors, "retirement_context_required", "$", "Exact wake and reservation context is required."); return Result(errors); }
        if (retirement is null) { Add(errors, "retirement_required", "$", "A continuation retirement is required."); return Result(errors); }
        ValidateSchema(retirement.SchemaVersion, "$.schemaVersion", errors);
        ValidateId(retirement.RetirementId, "$.retirementId", errors);
        ExactWake(wake, retirement.Wake, "$.wake", errors);
        ExactReservation(reservation, retirement.Reservation, "$.reservation", errors);
        ExactGeneration(wake.ExpectedGeneration, retirement.ExpectedGeneration, "$.expectedGeneration", errors);
        if (!Enum.IsDefined(retirement.Outcome) || retirement.Outcome is HumanReviewContinuationOutcome.Unknown or HumanReviewContinuationOutcome.Completed) Add(errors, "unsupported_retirement_outcome", "$.outcome", "Retirement requires one supported non-completion outcome.");
        if (!Utc(retirement.RetiredAtUtc) || retirement.RetiredAtUtc < wake.PublishedAtUtc || retirement.Outcome == HumanReviewContinuationOutcome.Expired && retirement.RetiredAtUtc < wake.ExpiresAtUtc) Add(errors, "invalid_retirement_time", "$.retiredAtUtc", "Retirement time must be trusted UTC and expiry retirement cannot predate wake expiry.");
        Evidence(retirement.Evidence, "$.evidence", errors);
        Provenance(retirement.Provenance, retirement.RetiredAtUtc, "$.provenance", errors);
        Hash(retirement.RetirementHash, "$.retirementHash", errors);
        if (errors.Count == 0 && !HumanReviewContinuationContractHash.MatchesRetirement(retirement)) Add(errors, "retirement_hash_mismatch", "$.retirementHash", "Retirement hash must exactly match its canonical contract.");
        return Result(errors);
    }

    /// <summary>Validates the closed append-only state machine for one wake, including lease takeover and terminal exclusivity.</summary>
    public static HumanReviewContractValidationResult ValidateState(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, HumanReviewContinuationState? state)
    {
        var errors = Start(request, reservation);
        if (state is null) { Add(errors, "state_required", "$", "Continuation state is required."); return Result(errors); }
        ValidateSchema(state.SchemaVersion, "$.schemaVersion", errors);
        var wake = state.Wake;
        errors.AddRange(ValidateWake(request, reservation, wake).Errors);
        if (state.Claims.IsDefault || state.Claims.Length > HumanReviewContractLimits.MaxContinuationClaims) Add(errors, "invalid_claim_count", "$.claims", "Claim history must be defined and bounded.");
        else
        {
            var claimIds = new HashSet<string>(StringComparer.Ordinal);
            var claimHashes = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < state.Claims.Length; index++)
            {
                var claim = state.Claims[index];
                errors.AddRange(ValidateClaim(wake, reservation, claim).Errors);
                if (claim is null)
                {
                    continue;
                }

                if (!claimIds.Add(claim.ClaimId)) Add(errors, "duplicate_claim_id", $"$.claims[{index}].claimId", "Claim identities cannot be reused anywhere in the append-only history.");
                if (!claimHashes.Add(claim.ClaimHash)) Add(errors, "duplicate_claim_hash", $"$.claims[{index}].claimHash", "Claim hashes cannot be reused anywhere in the append-only history.");
                if (index == 0 || state.Claims[index - 1] is not { } previous)
                {
                    continue;
                }

                if (claim.ClaimedAtUtc <= previous.LeaseExpiresAtUtc) Add(errors, "claim_takeover_before_expiry", $"$.claims[{index}].claimedAtUtc", "A later claim can take over only after the prior lease expires.");
            }
        }
        if (state.Completion is not null && state.Retirement is not null) Add(errors, "multiple_terminal_outcomes", "$", "Completion and retirement are mutually exclusive terminal outcomes.");
        if (state.Completion is not null)
        {
            if (state.Claims.IsDefaultOrEmpty) Add(errors, "completion_without_claim", "$.completion", "Completion requires one exact active claim.");
            else errors.AddRange(ValidateCompletion(wake, reservation, state.Claims[^1], state.Completion).Errors);
        }
        if (state.Retirement is not null) errors.AddRange(ValidateRetirement(wake, reservation, state.Retirement).Errors);
        Hash(state.StateHash, "$.stateHash", errors);
        if (errors.Count == 0 && !HumanReviewContinuationContractHash.MatchesState(state)) Add(errors, "state_hash_mismatch", "$.stateHash", "State hash must exactly match the complete ordered continuation state.");
        return Result(errors);
    }

    private static List<HumanReviewContractValidationError> Start(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation)
    {
        var errors = new List<HumanReviewContractValidationError>();
        errors.AddRange(HumanReviewContractValidator.ValidateRequest(request).Errors);
        errors.AddRange(HumanReviewContractValidator.ValidateContinuationReservation(request, reservation).Errors);
        return errors;
    }
    private static void ValidateSchema(int value, string path, List<HumanReviewContractValidationError> errors) { if (value != HumanReviewContractLimits.CurrentSchemaVersion) Add(errors, "unsupported_schema_version", path, "Only schema version 1 is supported."); }
    private static void ValidateId(string? value, string path, List<HumanReviewContractValidationError> errors) { if (!HumanReviewIdentifier.IsValid(value)) Add(errors, "invalid_identifier", path, "Identifiers must be bounded canonical lowercase ASCII values."); }
    private static void Hash(string? value, string path, List<HumanReviewContractValidationError> errors) { if (!HumanReviewContractHash.IsSha256(value)) Add(errors, "invalid_hash", path, "A lowercase SHA-256 hash is required."); }
    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    private static void Generation(long value, string path, List<HumanReviewContractValidationError> errors) { if (value is < 1 or > HumanReviewContractLimits.MaxVersion) Add(errors, "invalid_generation", path, "Generation must be positive and within schema-1 bounds."); }
    private static void ExactGeneration(long expected, long actual, string path, List<HumanReviewContractValidationError> errors) { Generation(actual, path, errors); if (expected != actual) Add(errors, "generation_mismatch", path, "Generation must exactly match the immutable wake generation."); }
    private static void ExactRequest(HumanReviewRequest? request, HumanReviewRequestReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null) { Add(errors, "request_reference_required", path, "An exact request reference is required."); return; } ValidateId(value.RequestId, path + ".requestId", errors); Hash(value.RequestHash, path + ".requestHash", errors); if (request is not null && (value.RequestId != request.RequestId || value.RequestHash != request.RequestHash)) Add(errors, "request_reference_mismatch", path, "Request reference must exactly match the immutable request."); }
    private static void ExactDecision(HumanReviewContinuationReservation? reservation, HumanReviewDecisionReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null) { Add(errors, "decision_reference_required", path, "An exact decision reference is required."); return; } ValidateId(value.DecisionId, path + ".decisionId", errors); ValidateId(value.DecisionOperationId, path + ".decisionOperationId", errors); Hash(value.DecisionHash, path + ".decisionHash", errors); if (value.Kind != HumanReviewDecisionKind.Approve || reservation is not null && !Equals(value, reservation.Decision)) Add(errors, "decision_reference_mismatch", path, "Wake decision must exactly match the accepted approval reservation."); }
    private static void ExactReservation(HumanReviewContinuationReservation? reservation, HumanReviewContinuationReservationReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null) { Add(errors, "reservation_reference_required", path, "An exact continuation reservation reference is required."); return; } ValidateId(value.ReservationId, path + ".reservationId", errors); Hash(value.ReservationHash, path + ".reservationHash", errors); if (reservation is not null && (value.ReservationId != reservation.ReservationId || value.ReservationHash != reservation.ReservationHash)) Add(errors, "reservation_reference_mismatch", path, "Reservation reference must exactly match the one approved continuation reservation."); }
    private static void ExactWake(HumanReviewContinuationWake wake, HumanReviewContinuationWakeReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.WakeId != wake.WakeId || value.WakeHash != wake.WakeHash) Add(errors, "wake_reference_mismatch", path, "Wake reference must exactly match the immutable published wake."); }
    private static void ExactClaim(HumanReviewContinuationClaim claim, HumanReviewContinuationClaimReference? value, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.ClaimId != claim.ClaimId || value.ClaimHash != claim.ClaimHash) Add(errors, "claim_reference_mismatch", path, "Claim reference must exactly match the active claim."); }
    private static void Evidence(ImmutableArray<HumanReviewRedactedPreview> value, string path, List<HumanReviewContractValidationError> errors) { if (value.IsDefault || value.Length > HumanReviewContractLimits.MaxContinuationEvidence) { Add(errors, "invalid_evidence_count", path, "Evidence must be defined and bounded."); return; } var previous = HumanReviewPreviewKind.Unknown; for (var index = 0; index < value.Length; index++) { var preview = value[index]; if (preview is null || !Enum.IsDefined(preview.Kind) || preview.Kind == HumanReviewPreviewKind.Unknown || preview.Kind <= previous || !HumanReviewSafeText.IsValid(preview.Label, HumanReviewContractLimits.MaxPreviewLabelCharacters, true) || !HumanReviewSafeText.IsValid(preview.Detail, HumanReviewContractLimits.MaxPreviewDetailCharacters, true) || !HumanReviewContractHash.MatchesPreview(preview)) Add(errors, "invalid_evidence", $"{path}[{index}]", "Evidence must be canonical ordered bounded redacted previews."); previous = preview?.Kind ?? previous; } }
    private static void Provenance(HumanReviewProvenance? value, DateTimeOffset time, string path, List<HumanReviewContractValidationError> errors) { if (value is null || value.Kind != HumanReviewProvenanceKind.Coordinator || value.ObservedAtUtc != time || !HumanReviewIdentifier.IsValid(value.SourceId) || !HumanReviewIdentifier.IsValid(value.CorrelationId) || !HumanReviewContractHash.MatchesProvenance(value)) Add(errors, "invalid_continuation_provenance", path, "Continuation provenance must be canonical coordinator provenance at the exact artifact time."); }
    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message) => errors.Add(new HumanReviewContractValidationError(code, path, message));
    private static HumanReviewContractValidationResult Result(List<HumanReviewContractValidationError> errors) => new(errors);
}
