namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed record RecoveryCursor(
    int SchemaVersion,
    string? AfterRunCursor,
    string? ResumeRunId,
    long? ResumeRunCreatedAtUtcTicks,
    int NextCheckpointOrdinal);
