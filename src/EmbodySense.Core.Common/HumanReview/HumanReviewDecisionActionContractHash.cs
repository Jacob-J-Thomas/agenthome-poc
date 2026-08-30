using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Computes canonical schema-1 hashes for durable non-approval Human Review action artifacts.</summary>
/// <remarks>This contract is deliberately disjoint from approval continuation and effect-release artifacts.</remarks>
public static class HumanReviewDecisionActionContractHash
{
    /// <summary>Returns a reservation carrying its exact canonical hash.</summary>
    public static HumanReviewDecisionActionReservation ApplyReservation(HumanReviewDecisionActionReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var prepared = reservation with { Provenance = HumanReviewContractHash.ApplyProvenance(reservation.Provenance) };
        return prepared with { ReservationHash = Compute("human-review-decision-action-reservation-v1", builder => AppendReservation(builder, prepared, false)) };
    }

    /// <summary>Returns a wake carrying its exact canonical hash.</summary>
    public static HumanReviewDecisionActionWake ApplyWake(HumanReviewDecisionActionWake wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        var prepared = wake with { Provenance = HumanReviewContractHash.ApplyProvenance(wake.Provenance) };
        return prepared with { WakeHash = Compute("human-review-decision-action-wake-v1", builder => AppendWake(builder, prepared, false)) };
    }

    /// <summary>Returns a claim carrying its exact canonical hash.</summary>
    public static HumanReviewDecisionActionClaim ApplyClaim(HumanReviewDecisionActionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var prepared = claim with { Provenance = HumanReviewContractHash.ApplyProvenance(claim.Provenance) };
        return prepared with { ClaimHash = Compute("human-review-decision-action-claim-v1", builder => AppendClaim(builder, prepared, false)) };
    }

    /// <summary>Returns a completion carrying its exact canonical nested and artifact hashes.</summary>
    public static HumanReviewDecisionActionCompletion ApplyCompletion(HumanReviewDecisionActionCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var prepared = completion with { Evidence = ApplyEvidence(completion.Evidence), Provenance = HumanReviewContractHash.ApplyProvenance(completion.Provenance) };
        return prepared with { CompletionHash = Compute("human-review-decision-action-completion-v1", builder => AppendCompletion(builder, prepared, false)) };
    }

    /// <summary>Returns a retirement carrying its exact canonical nested and artifact hashes.</summary>
    public static HumanReviewDecisionActionRetirement ApplyRetirement(HumanReviewDecisionActionRetirement retirement)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        var prepared = retirement with { Evidence = ApplyEvidence(retirement.Evidence), Provenance = HumanReviewContractHash.ApplyProvenance(retirement.Provenance) };
        return prepared with { RetirementHash = Compute("human-review-decision-action-retirement-v1", builder => AppendRetirement(builder, prepared, false)) };
    }

    /// <summary>Returns an action state carrying all nested canonical hashes and its exact state hash.</summary>
    public static HumanReviewDecisionActionState ApplyState(HumanReviewDecisionActionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var prepared = state with
        {
            Reservation = ApplyReservation(state.Reservation),
            Wake = state.Wake is null ? null : ApplyWake(state.Wake),
            Claims = state.Claims.IsDefault ? default : state.Claims.Select(ApplyClaim).ToImmutableArray(),
            Completion = state.Completion is null ? null : ApplyCompletion(state.Completion),
            Retirement = state.Retirement is null ? null : ApplyRetirement(state.Retirement)
        };
        return prepared with { StateHash = Compute("human-review-decision-action-state-v1", builder => AppendState(builder, prepared, false)) };
    }

    /// <summary>Gets whether a reservation carries an exact canonical hash.</summary>
    public static bool MatchesReservation(HumanReviewDecisionActionReservation? value) => value is not null && HumanReviewContractHash.IsSha256(value.ReservationHash) && FixedEquals(ApplyReservation(value).ReservationHash, value.ReservationHash) && HumanReviewContractHash.MatchesProvenance(value.Provenance);

    /// <summary>Gets whether a wake carries an exact canonical hash.</summary>
    public static bool MatchesWake(HumanReviewDecisionActionWake? value) => value is not null && HumanReviewContractHash.IsSha256(value.WakeHash) && FixedEquals(ApplyWake(value).WakeHash, value.WakeHash) && HumanReviewContractHash.MatchesProvenance(value.Provenance);

    /// <summary>Gets whether a claim carries an exact canonical hash.</summary>
    public static bool MatchesClaim(HumanReviewDecisionActionClaim? value) => value is not null && HumanReviewContractHash.IsSha256(value.ClaimHash) && FixedEquals(ApplyClaim(value).ClaimHash, value.ClaimHash) && HumanReviewContractHash.MatchesProvenance(value.Provenance);

    /// <summary>Gets whether a completion carries exact canonical hashes.</summary>
    public static bool MatchesCompletion(HumanReviewDecisionActionCompletion? value) => value is not null && HumanReviewContractHash.IsSha256(value.CompletionHash) && EvidenceMatches(value.Evidence) && HumanReviewContractHash.MatchesProvenance(value.Provenance) && FixedEquals(ApplyCompletion(value).CompletionHash, value.CompletionHash);

    /// <summary>Gets whether a retirement carries exact canonical hashes.</summary>
    public static bool MatchesRetirement(HumanReviewDecisionActionRetirement? value) => value is not null && HumanReviewContractHash.IsSha256(value.RetirementHash) && EvidenceMatches(value.Evidence) && HumanReviewContractHash.MatchesProvenance(value.Provenance) && FixedEquals(ApplyRetirement(value).RetirementHash, value.RetirementHash);

    /// <summary>Gets whether an action state carries exact nested and state hashes.</summary>
    public static bool MatchesState(HumanReviewDecisionActionState? value) => value is not null && HumanReviewContractHash.IsSha256(value.StateHash) && MatchesReservation(value.Reservation) && (value.Wake is null || MatchesWake(value.Wake)) && !value.Claims.IsDefault && value.Claims.All(MatchesClaim) && (value.Completion is null || MatchesCompletion(value.Completion)) && (value.Retirement is null || MatchesRetirement(value.Retirement)) && FixedEquals(ApplyState(value).StateHash, value.StateHash);

    private static ImmutableArray<HumanReviewRedactedPreview> ApplyEvidence(ImmutableArray<HumanReviewRedactedPreview> evidence) => evidence.IsDefault ? default : evidence.Select(HumanReviewContractHash.ApplyPreview).ToImmutableArray();
    private static bool EvidenceMatches(ImmutableArray<HumanReviewRedactedPreview> evidence) => !evidence.IsDefault && evidence.All(HumanReviewContractHash.MatchesPreview);
    private static string Compute(string domain, Action<StringBuilder> append)
    {
        var builder = new StringBuilder(768);
        Append(builder, domain);
        append(builder);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static bool FixedEquals(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    private static void AppendReservation(StringBuilder builder, HumanReviewDecisionActionReservation value, bool includeHash) { Append(builder, value.SchemaVersion); AppendRequest(builder, value.Request); AppendDecision(builder, value.Decision); Append(builder, value.ReservationId); Append(builder, value.ReservedAtUtc); AppendProvenance(builder, value.Provenance); if (includeHash) Append(builder, value.ReservationHash); }
    private static void AppendWake(StringBuilder builder, HumanReviewDecisionActionWake value, bool includeHash) { Append(builder, value.SchemaVersion); Append(builder, value.WakeId); AppendRequest(builder, value.Request); AppendDecision(builder, value.Decision); AppendReservationReference(builder, value.Reservation); Append(builder, value.BindingHash); Append(builder, value.ExpectedGeneration); Append(builder, value.PublishedAtUtc); Append(builder, value.ExpiresAtUtc); AppendProvenance(builder, value.Provenance); if (includeHash) Append(builder, value.WakeHash); }
    private static void AppendClaim(StringBuilder builder, HumanReviewDecisionActionClaim value, bool includeHash) { Append(builder, value.SchemaVersion); Append(builder, value.ClaimId); AppendWakeReference(builder, value.Wake); AppendReservationReference(builder, value.Reservation); Append(builder, value.ExpectedGeneration); Append(builder, value.WorkerId); Append(builder, value.ClaimedAtUtc); Append(builder, value.LeaseExpiresAtUtc); AppendProvenance(builder, value.Provenance); if (includeHash) Append(builder, value.ClaimHash); }
    private static void AppendCompletion(StringBuilder builder, HumanReviewDecisionActionCompletion value, bool includeHash) { Append(builder, value.SchemaVersion); Append(builder, value.CompletionId); AppendWakeReference(builder, value.Wake); AppendClaimReference(builder, value.Claim); AppendReservationReference(builder, value.Reservation); Append(builder, value.ExpectedGeneration); Append(builder, (int)value.Disposition); Append(builder, value.ResultHash); Append(builder, value.FrontierReceiptHash); Append(builder, value.CompletedAtUtc); AppendEvidence(builder, value.Evidence); AppendProvenance(builder, value.Provenance); if (includeHash) Append(builder, value.CompletionHash); }
    private static void AppendRetirement(StringBuilder builder, HumanReviewDecisionActionRetirement value, bool includeHash) { Append(builder, value.SchemaVersion); Append(builder, value.RetirementId); AppendWakeReference(builder, value.Wake); AppendReservationReference(builder, value.Reservation); Append(builder, value.ExpectedGeneration); Append(builder, (int)value.Outcome); Append(builder, (int)value.Reason); Append(builder, value.RetiredAtUtc); AppendEvidence(builder, value.Evidence); AppendProvenance(builder, value.Provenance); if (includeHash) Append(builder, value.RetirementHash); }
    private static void AppendState(StringBuilder builder, HumanReviewDecisionActionState value, bool includeHash) { Append(builder, value.SchemaVersion); AppendReservation(builder, value.Reservation, true); Append(builder, value.BindingHash); Append(builder, value.ExpectedGeneration); Append(builder, value.ReservedLifecycleVersion); if (value.Wake is null) Append(builder, null); else AppendWake(builder, value.Wake, true); if (value.Claims.IsDefault) Append(builder, null); else { Append(builder, value.Claims.Length); foreach (var claim in value.Claims) AppendClaim(builder, claim, true); } if (value.Completion is null) Append(builder, null); else AppendCompletion(builder, value.Completion, true); if (value.Retirement is null) Append(builder, null); else AppendRetirement(builder, value.Retirement, true); if (includeHash) Append(builder, value.StateHash); }
    private static void AppendEvidence(StringBuilder builder, ImmutableArray<HumanReviewRedactedPreview> values) { if (values.IsDefault) { Append(builder, null); return; } Append(builder, values.Length); foreach (var value in values) { if (value is null) { Append(builder, null); } else { Append(builder, (int)value.Kind); Append(builder, value.Label); Append(builder, value.Detail); Append(builder, value.DetailHash); } } }
    private static void AppendRequest(StringBuilder builder, HumanReviewRequestReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.RequestId); Append(builder, value.RequestHash); }
    private static void AppendDecision(StringBuilder builder, HumanReviewDecisionReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.DecisionId); Append(builder, value.DecisionOperationId); Append(builder, (int)value.Kind); Append(builder, value.DecisionHash); }
    private static void AppendReservationReference(StringBuilder builder, HumanReviewDecisionActionReservationReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.ReservationId); Append(builder, value.ReservationHash); }
    private static void AppendWakeReference(StringBuilder builder, HumanReviewDecisionActionWakeReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.WakeId); Append(builder, value.WakeHash); }
    private static void AppendClaimReference(StringBuilder builder, HumanReviewDecisionActionClaimReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.ClaimId); Append(builder, value.ClaimHash); }
    private static void AppendProvenance(StringBuilder builder, HumanReviewProvenance? value) { if (value is null) { Append(builder, null); return; } Append(builder, (int)value.Kind); Append(builder, value.SourceId); Append(builder, value.CorrelationId); Append(builder, value.ObservedAtUtc); Append(builder, value.ProvenanceHash); }
    private static void Append(StringBuilder builder, DateTimeOffset value) => Append(builder, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, int value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, long value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, string? value) { if (value is null) { builder.Append("-1:"); return; } var normalized = value.Normalize(NormalizationForm.FormC); builder.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture)); builder.Append(':'); builder.Append(normalized); }
}
