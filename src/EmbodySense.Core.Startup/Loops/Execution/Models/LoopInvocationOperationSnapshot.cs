namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopInvocationOperationSnapshot(
    string OperationId,
    string LoopId,
    string State,
    string Outcome,
    string AdmissionStatus,
    string? RunId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Detail);
