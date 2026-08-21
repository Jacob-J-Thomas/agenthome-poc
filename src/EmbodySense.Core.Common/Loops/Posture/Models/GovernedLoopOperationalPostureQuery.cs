namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Requests finite independent pages from every operational family, using an opaque generation-bound cursor for queue evidence.</summary>
public sealed record GovernedLoopOperationalPostureQuery(
    int MaximumQueueEntries,
    int MaximumSchedules,
    int MaximumWakes,
    int MaximumRuns,
    string? QueueCursor = null,
    string? AfterScheduleId = null,
    string? AfterCheckpointId = null,
    string? AfterRunId = null);
