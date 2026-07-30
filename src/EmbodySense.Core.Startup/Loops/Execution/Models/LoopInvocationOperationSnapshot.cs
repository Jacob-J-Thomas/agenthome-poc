namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects the durable reconciliation receipt for one idempotent loop invocation.
/// </summary>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="LoopId">The loop identifier.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="AdmissionStatus">The admission status.</param>
/// <param name="RunId">The run identifier.</param>
/// <param name="CreatedAtUtc">The created at utc.</param>
/// <param name="UpdatedAtUtc">The updated at utc.</param>
/// <param name="Detail">The detail.</param>
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
