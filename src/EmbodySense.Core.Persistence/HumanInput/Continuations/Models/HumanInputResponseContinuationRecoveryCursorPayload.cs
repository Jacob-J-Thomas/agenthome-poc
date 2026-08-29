namespace EmbodySense.Core.Persistence.HumanInput.Continuations.Models;

/// <summary>Defines the canonical serialized payload for one opaque Human Input response-continuation recovery cursor.</summary>
internal sealed record HumanInputResponseContinuationRecoveryCursorPayload(
    int SchemaVersion,
    string? AfterRunCursor,
    string? ResumeRunId,
    long? ResumeRunCreatedAtUtcTicks,
    int NextCheckpointOrdinal);
