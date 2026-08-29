namespace EmbodySense.Core.Persistence.HumanInput.Continuations.Models;

/// <summary>Retains one bounded exclusive recovery scan position, including an append-only checkpoint ordinal when a run needs a tail probe.</summary>
internal sealed record HumanInputResponseContinuationRecoveryCursor(
    int SchemaVersion,
    string? AfterRunCursor,
    string? ResumeRunId,
    long? ResumeRunCreatedAtUtcTicks,
    int NextCheckpointOrdinal);
