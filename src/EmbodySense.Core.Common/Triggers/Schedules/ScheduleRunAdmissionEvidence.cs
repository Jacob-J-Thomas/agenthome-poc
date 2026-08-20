using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Retains bounded append-only atomic run-admission evidence for one exact schedule delivery.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="CanonicalEnvelope">The exact canonical time-trigger envelope carried through queue delivery.</param>
/// <param name="CanonicalEnvelopeHash">The lowercase SHA-256 digest of the canonical envelope.</param>
/// <param name="LoopId">The canonical run-store loop identity.</param>
/// <param name="Attempts">The bounded append-only admission observations.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest of every preceding field.</param>
public sealed record ScheduleRunAdmissionEvidence(
    int SchemaVersion,
    string CanonicalEnvelope,
    string CanonicalEnvelopeHash,
    string LoopId,
    IReadOnlyList<ScheduleRunAdmissionAttempt> Attempts,
    string ContentHash)
{
    private IReadOnlyList<ScheduleRunAdmissionAttempt>? _attempts = Attempts is null
        ? null
        : Array.AsReadOnly(Attempts.Take(ScheduleRunAdmissionEvidenceLimits.MaxAttempts + 1).ToArray());

    /// <summary>Gets the only supported evidence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;

    /// <summary>Gets an immutable snapshot of the bounded append-only attempts.</summary>
    public IReadOnlyList<ScheduleRunAdmissionAttempt> Attempts
    {
        get => _attempts!;
        init => _attempts = value is null
            ? null
            : Array.AsReadOnly(value.Take(ScheduleRunAdmissionEvidenceLimits.MaxAttempts + 1).ToArray());
    }
}
