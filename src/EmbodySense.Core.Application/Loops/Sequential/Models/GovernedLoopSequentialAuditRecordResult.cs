namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns the closed durable disposition of one append-once sequential audit operation.</summary>
/// <param name="Status">The durable disposition.</param>
/// <param name="Detail">A bounded diagnostic that contains no audit payload or secret value.</param>
public sealed record GovernedLoopSequentialAuditRecordResult(
    GovernedLoopSequentialAuditRecordStatus Status,
    string Detail);
