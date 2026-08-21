using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Records one immutable append-only provider-usage ledger transition.</summary>
public sealed class GovernedModelUsageLedgerEntry
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageLedgerEntry(GovernedModelUsageLedgerIdentity identity, long generation, GovernedModelUsageLedgerPhase phase, GovernedModelUsageCeiling? reservation, LlmInferenceUsageEvidence? usage, GovernedModelUsageVector? used, GovernedModelUsageVector? released, bool usageUnknown, string evidenceHash, string? previousEntryHash, DateTimeOffset recordedAtUtc)
    {
        Identity = identity;
        Generation = generation;
        Phase = phase;
        Reservation = reservation;
        Usage = usage;
        Used = used;
        Released = released;
        UsageUnknown = usageUnknown;
        EvidenceHash = evidenceHash;
        PreviousEntryHash = previousEntryHash;
        RecordedAtUtc = recordedAtUtc;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-usage-ledger-entry.v1", WriteCanonical);
    }

    /// <summary>Gets the exact provider-attempt identity.</summary>
    public GovernedModelUsageLedgerIdentity Identity { get; }
    /// <summary>Gets the positive optimistic ledger generation.</summary>
    public long Generation { get; }
    /// <summary>Gets the append-only phase.</summary>
    public GovernedModelUsageLedgerPhase Phase { get; }
    /// <summary>Gets the exact pre-dispatch reservation when present.</summary>
    public GovernedModelUsageCeiling? Reservation { get; }
    /// <summary>Gets provider usage or explicit unavailable posture when observed.</summary>
    public LlmInferenceUsageEvidence? Usage { get; }
    /// <summary>Gets authoritative used values when reconciled.</summary>
    public GovernedModelUsageVector? Used { get; }
    /// <summary>Gets only reservation affirmatively proved unused.</summary>
    public GovernedModelUsageVector? Released { get; }
    /// <summary>Gets whether at least one usage dimension remains unknown and conservatively reserved.</summary>
    public bool UsageUnknown { get; }
    /// <summary>Gets the exact bounded external provider/effect evidence hash.</summary>
    public string EvidenceHash { get; }
    /// <summary>Gets the previous append-only entry hash, or null for generation one.</summary>
    public string? PreviousEntryHash { get; }
    /// <summary>Gets the trusted UTC record time.</summary>
    public DateTimeOffset RecordedAtUtc { get; }
    /// <summary>Gets the canonical complete entry hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated immutable append-only ledger entry.</summary>
    public static GovernedModelUsageLedgerEntry Create(int schemaVersion, GovernedModelUsageLedgerIdentity identity, long generation, GovernedModelUsageLedgerPhase phase, GovernedModelUsageCeiling? reservation, LlmInferenceUsageEvidence? usage, GovernedModelUsageVector? used, GovernedModelUsageVector? released, bool usageUnknown, string evidenceHash, string? previousEntryHash, DateTimeOffset recordedAtUtc)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(identity);
        if (!GovernedModelContractValidator.IsValid(identity)
            || reservation is not null && !GovernedModelContractValidator.IsValid(reservation)
            || usage is not null && !GovernedModelContractValidator.IsValid(usage)
            || used is not null && !GovernedModelContractValidator.IsValid(used)
            || released is not null && !GovernedModelContractValidator.IsValid(released))
        {
            throw new ArgumentException("Ledger identity and nested evidence must be canonical.");
        }
        if (!Enum.IsDefined(phase) || phase == GovernedModelUsageLedgerPhase.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Ledger phases must use defined schema-1 values.");
        }

        generation = GovernedModelContractRules.RequireQuantity(generation, long.MaxValue, nameof(generation), positive: true);
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Ledger record time must be non-default UTC.", nameof(recordedAtUtc));
        }

        if ((generation == 1) != (previousEntryHash is null))
        {
            throw new ArgumentException("Only generation one omits a previous entry hash.", nameof(previousEntryHash));
        }

        if (previousEntryHash is not null)
        {
            previousEntryHash = GovernedModelContractRules.RequireHash(previousEntryHash, nameof(previousEntryHash));
        }

        ValidatePhaseShape(phase, reservation, usage, used, released, usageUnknown);
        return new GovernedModelUsageLedgerEntry(identity, generation, phase, reservation, usage, used, released, usageUnknown, GovernedModelContractRules.RequireHash(evidenceHash, nameof(evidenceHash)), previousEntryHash, recordedAtUtc);
    }

    private static void ValidatePhaseShape(GovernedModelUsageLedgerPhase phase, GovernedModelUsageCeiling? reservation, LlmInferenceUsageEvidence? usage, GovernedModelUsageVector? used, GovernedModelUsageVector? released, bool usageUnknown)
    {
        if (phase == GovernedModelUsageLedgerPhase.ReservationCommitted && (reservation is null || usage is not null || used is not null || released is not null || usageUnknown)
            || phase is GovernedModelUsageLedgerPhase.DispatchProvedNotStarted or GovernedModelUsageLedgerPhase.DispatchBoundaryReached && (reservation is null || usage is not null || used is not null || released is not null)
            || phase == GovernedModelUsageLedgerPhase.UsageObserved && (reservation is null || usage is null || used is not null || released is not null || usageUnknown != HasUnknownUsage(usage, reservation))
            || phase == GovernedModelUsageLedgerPhase.Reconciled && (reservation is null || usage is null || used is null || released is null)
            || phase == GovernedModelUsageLedgerPhase.AttentionRequired && reservation is null)
        {
            throw new ArgumentException("The ledger phase and evidence shape are inconsistent.");
        }

        if (phase == GovernedModelUsageLedgerPhase.DispatchProvedNotStarted && usageUnknown)
        {
            throw new ArgumentException("Affirmative pre-dispatch proof cannot retain unknown usage.");
        }
    }

    private static bool HasUnknownUsage(LlmInferenceUsageEvidence usage, GovernedModelUsageCeiling reservation)
        => reservation.InputTokens.IsBounded && usage.InputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.OutputTokens.IsBounded && usage.OutputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.CachedTokens.IsBounded && usage.CachedTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.TotalTokens.IsBounded && usage.TotalTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.MonetaryCost.IsBounded && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Unavailable;

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("evidenceHash", EvidenceHash);
        writer.WriteNumber("generation", Generation);
        writer.WriteString("identityHash", Identity.ContentHash);
        writer.WriteNumber("phase", (int)Phase);
        writer.WriteString("previousEntryHash", PreviousEntryHash);
        writer.WriteString("recordedAtUtc", RecordedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("releasedHash", Released?.ContentHash);
        writer.WriteString("reservationHash", Reservation?.ContentHash);
        writer.WriteString("usageHash", Usage?.ContentHash);
        writer.WriteBoolean("usageUnknown", UsageUnknown);
        writer.WriteString("usedHash", Used?.ContentHash);
        writer.WriteEndObject();
    }
}
