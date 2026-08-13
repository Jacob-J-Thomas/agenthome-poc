using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Loops.Sequential.Models;

/// <summary>Retains the complete canonical external trigger envelope that originated one sequential run.</summary>
/// <param name="SchemaVersion">The origin-evidence schema version, which must be 1.</param>
/// <param name="ScheduleId">The exact immutable schedule definition identity.</param>
/// <param name="DefinitionRevision">The exact immutable schedule definition revision.</param>
/// <param name="DefinitionHash">The exact immutable schedule definition hash.</param>
/// <param name="Occurrence">The exact time-zone-resolved occurrence coordinates.</param>
/// <param name="CanonicalEnvelope">The exact bounded canonical trigger-delivery JSON admitted by the durable worker.</param>
/// <param name="CanonicalEnvelopeHash">The lowercase SHA-256 hash of <paramref name="CanonicalEnvelope"/>.</param>
/// <remarks>
/// This immutable value is provenance evidence, not reusable authority. Schema 1 admits only a deterministic schedule-derived
/// time delivery here; human/manual invocations retain <see langword="null"/> instead.
/// </remarks>
public sealed record GovernedLoopSequentialTriggerOrigin(
    int SchemaVersion,
    string ScheduleId,
    long DefinitionRevision,
    string DefinitionHash,
    ScheduleOccurrence Occurrence,
    string CanonicalEnvelope,
    string CanonicalEnvelopeHash)
{
    /// <summary>Gets the only supported experimental trigger-origin schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSequentialContractLimits.CurrentSchemaVersion;
}
