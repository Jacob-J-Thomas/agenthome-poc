using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Computes canonical schema-1 hashes for Human Review continuation artifacts without changing lifecycle state or granting authority.</summary>
public static class HumanReviewContinuationContractHash
{
    /// <summary>Computes the canonical hash of a continuation wake excluding its self-referential hash field.</summary>
    public static string ComputeWake(HumanReviewContinuationWake wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        return Compute("human-review-continuation-wake-v1", builder => AppendWake(builder, wake, false));
    }

    /// <summary>Returns a wake carrying its exact canonical hash.</summary>
    public static HumanReviewContinuationWake ApplyWake(HumanReviewContinuationWake wake) => wake with { WakeHash = ComputeWake(wake) };

    /// <summary>Gets whether a wake carries an exact canonical hash.</summary>
    public static bool MatchesWake(HumanReviewContinuationWake? wake) => wake is not null && HumanReviewContractHash.IsSha256(wake.WakeHash) && FixedEquals(ComputeWake(wake), wake.WakeHash);

    /// <summary>Computes the canonical hash of a continuation claim excluding its self-referential hash field.</summary>
    public static string ComputeClaim(HumanReviewContinuationClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return Compute("human-review-continuation-claim-v1", builder => AppendClaim(builder, claim, false));
    }

    /// <summary>Returns a claim carrying its exact canonical hash.</summary>
    public static HumanReviewContinuationClaim ApplyClaim(HumanReviewContinuationClaim claim) => claim with { ClaimHash = ComputeClaim(claim) };

    /// <summary>Gets whether a claim carries an exact canonical hash.</summary>
    public static bool MatchesClaim(HumanReviewContinuationClaim? claim) => claim is not null && HumanReviewContractHash.IsSha256(claim.ClaimHash) && FixedEquals(ComputeClaim(claim), claim.ClaimHash);

    /// <summary>Computes the canonical hash of a continuation completion excluding its self-referential hash field.</summary>
    public static string ComputeCompletion(HumanReviewContinuationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return Compute("human-review-continuation-completion-v1", builder => AppendCompletion(builder, completion, false));
    }

    /// <summary>Returns a completion carrying nested preview hashes and its exact canonical hash.</summary>
    public static HumanReviewContinuationCompletion ApplyCompletion(HumanReviewContinuationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var prepared = completion with { Evidence = ApplyPreviews(completion.Evidence), Provenance = HumanReviewContractHash.ApplyProvenance(completion.Provenance) };
        return prepared with { CompletionHash = ComputeCompletion(prepared) };
    }

    /// <summary>Gets whether a completion carries exact canonical nested and artifact hashes.</summary>
    public static bool MatchesCompletion(HumanReviewContinuationCompletion? completion) => completion is not null && HumanReviewContractHash.IsSha256(completion.CompletionHash) && MatchesPreviews(completion.Evidence) && HumanReviewContractHash.MatchesProvenance(completion.Provenance) && FixedEquals(ComputeCompletion(completion), completion.CompletionHash);

    /// <summary>Computes the canonical hash of a continuation retirement excluding its self-referential hash field.</summary>
    public static string ComputeRetirement(HumanReviewContinuationRetirement retirement)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        return Compute("human-review-continuation-retirement-v1", builder => AppendRetirement(builder, retirement, false));
    }

    /// <summary>Returns a retirement carrying nested preview hashes and its exact canonical hash.</summary>
    public static HumanReviewContinuationRetirement ApplyRetirement(HumanReviewContinuationRetirement retirement)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        var prepared = retirement with { Evidence = ApplyPreviews(retirement.Evidence), Provenance = HumanReviewContractHash.ApplyProvenance(retirement.Provenance) };
        return prepared with { RetirementHash = ComputeRetirement(prepared) };
    }

    /// <summary>Gets whether a retirement carries exact canonical nested and artifact hashes.</summary>
    public static bool MatchesRetirement(HumanReviewContinuationRetirement? retirement) => retirement is not null && HumanReviewContractHash.IsSha256(retirement.RetirementHash) && MatchesPreviews(retirement.Evidence) && HumanReviewContractHash.MatchesProvenance(retirement.Provenance) && FixedEquals(ComputeRetirement(retirement), retirement.RetirementHash);

    /// <summary>Computes the canonical hash of a complete append-only continuation state excluding its self-referential hash field.</summary>
    public static string ComputeState(HumanReviewContinuationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Compute("human-review-continuation-state-v1", builder => AppendState(builder, state, false));
    }

    /// <summary>Returns a state carrying all nested canonical hashes and its exact canonical state hash.</summary>
    public static HumanReviewContinuationState ApplyState(HumanReviewContinuationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var prepared = state with
        {
            Wake = ApplyWake(state.Wake),
            Claims = state.Claims.IsDefault ? default : state.Claims.Select(ApplyClaim).ToImmutableArray(),
            Completion = state.Completion is null ? null : ApplyCompletion(state.Completion),
            Retirement = state.Retirement is null ? null : ApplyRetirement(state.Retirement)
        };
        return prepared with { StateHash = ComputeState(prepared) };
    }

    /// <summary>Gets whether a state carries exact canonical hashes for every nested artifact and its ordered state chain.</summary>
    public static bool MatchesState(HumanReviewContinuationState? state) => state is not null && HumanReviewContractHash.IsSha256(state.StateHash) && MatchesWake(state.Wake) && !state.Claims.IsDefault && state.Claims.All(MatchesClaim) && (state.Completion is null || MatchesCompletion(state.Completion)) && (state.Retirement is null || MatchesRetirement(state.Retirement)) && FixedEquals(ComputeState(state), state.StateHash);

    private static ImmutableArray<HumanReviewRedactedPreview> ApplyPreviews(ImmutableArray<HumanReviewRedactedPreview> previews) => previews.IsDefault ? default : previews.Select(HumanReviewContractHash.ApplyPreview).ToImmutableArray();

    private static bool MatchesPreviews(ImmutableArray<HumanReviewRedactedPreview> previews) => !previews.IsDefault && previews.All(HumanReviewContractHash.MatchesPreview);

    private static string Compute(string domain, Action<StringBuilder> append)
    {
        var builder = new StringBuilder(1024);
        Append(builder, domain);
        append(builder);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static void AppendWake(StringBuilder builder, HumanReviewContinuationWake wake, bool includeHash) { Append(builder, wake.SchemaVersion); Append(builder, wake.WakeId); AppendRequest(builder, wake.Request); AppendDecision(builder, wake.Decision); AppendReservation(builder, wake.Reservation); Append(builder, wake.BindingHash); Append(builder, wake.ExpectedGeneration); Append(builder, wake.PublishedAtUtc); Append(builder, wake.ExpiresAtUtc); AppendProvenance(builder, wake.Provenance); if (includeHash) Append(builder, wake.WakeHash); }
    private static void AppendClaim(StringBuilder builder, HumanReviewContinuationClaim claim, bool includeHash) { Append(builder, claim.SchemaVersion); Append(builder, claim.ClaimId); AppendWakeReference(builder, claim.Wake); AppendReservation(builder, claim.Reservation); Append(builder, claim.ExpectedGeneration); Append(builder, claim.WorkerId); Append(builder, claim.ClaimedAtUtc); Append(builder, claim.LeaseExpiresAtUtc); AppendProvenance(builder, claim.Provenance); if (includeHash) Append(builder, claim.ClaimHash); }
    private static void AppendCompletion(StringBuilder builder, HumanReviewContinuationCompletion completion, bool includeHash) { Append(builder, completion.SchemaVersion); Append(builder, completion.CompletionId); AppendWakeReference(builder, completion.Wake); AppendClaimReference(builder, completion.Claim); AppendReservation(builder, completion.Reservation); Append(builder, completion.ExpectedGeneration); Append(builder, completion.CompletedAtUtc); AppendPreviews(builder, completion.Evidence); AppendProvenance(builder, completion.Provenance); if (includeHash) Append(builder, completion.CompletionHash); }
    private static void AppendRetirement(StringBuilder builder, HumanReviewContinuationRetirement retirement, bool includeHash) { Append(builder, retirement.SchemaVersion); Append(builder, retirement.RetirementId); AppendWakeReference(builder, retirement.Wake); AppendReservation(builder, retirement.Reservation); Append(builder, retirement.ExpectedGeneration); Append(builder, (int)retirement.Outcome); Append(builder, retirement.RetiredAtUtc); AppendPreviews(builder, retirement.Evidence); AppendProvenance(builder, retirement.Provenance); if (includeHash) Append(builder, retirement.RetirementHash); }
    private static void AppendState(StringBuilder builder, HumanReviewContinuationState state, bool includeHash) { Append(builder, state.SchemaVersion); AppendWake(builder, state.Wake, true); if (state.Claims.IsDefault) Append(builder, null); else { Append(builder, state.Claims.Length); foreach (var claim in state.Claims) AppendClaim(builder, claim, true); } if (state.Completion is null) Append(builder, null); else AppendCompletion(builder, state.Completion, true); if (state.Retirement is null) Append(builder, null); else AppendRetirement(builder, state.Retirement, true); if (includeHash) Append(builder, state.StateHash); }
    private static void AppendPreviews(StringBuilder builder, ImmutableArray<HumanReviewRedactedPreview> previews) { if (previews.IsDefault) { Append(builder, null); return; } Append(builder, previews.Length); foreach (var preview in previews) { if (preview is null) { Append(builder, null); } else { Append(builder, (int)preview.Kind); Append(builder, preview.Label); Append(builder, preview.Detail); Append(builder, preview.DetailHash); } } }
    private static void AppendRequest(StringBuilder builder, HumanReviewRequestReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.RequestId); Append(builder, value.RequestHash); }
    private static void AppendDecision(StringBuilder builder, HumanReviewDecisionReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.DecisionId); Append(builder, value.DecisionOperationId); Append(builder, (int)value.Kind); Append(builder, value.DecisionHash); }
    private static void AppendReservation(StringBuilder builder, HumanReviewContinuationReservationReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.ReservationId); Append(builder, value.ReservationHash); }
    private static void AppendWakeReference(StringBuilder builder, HumanReviewContinuationWakeReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.WakeId); Append(builder, value.WakeHash); }
    private static void AppendClaimReference(StringBuilder builder, HumanReviewContinuationClaimReference? value) { if (value is null) { Append(builder, null); return; } Append(builder, value.ClaimId); Append(builder, value.ClaimHash); }
    private static void AppendProvenance(StringBuilder builder, HumanReviewProvenance? value) { if (value is null) { Append(builder, null); return; } Append(builder, (int)value.Kind); Append(builder, value.SourceId); Append(builder, value.CorrelationId); Append(builder, value.ObservedAtUtc); Append(builder, value.ProvenanceHash); }
    private static void Append(StringBuilder builder, DateTimeOffset value) => Append(builder, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, int value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, long value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, string? value) { if (value is null) { builder.Append("-1:"); return; } var normalized = value.Normalize(NormalizationForm.FormC); builder.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture)); builder.Append(':'); builder.Append(normalized); }
}
