using EmbodySense.Core.Application.Loops.ReceiptRetention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents a custom loop invocation receipt retention operation.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
/// <param name="ReplayCutoffUtc">The replay cutoff UTC.</param>
/// <param name="OwnershipStartedAtUtc">The ownership started at UTC.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="Candidates">The candidates.</param>
/// <param name="State">The state.</param>
/// <param name="DeletedReceiptCount">The deleted receipt count.</param>
/// <param name="DeletedReceiptUtf8Bytes">The deleted receipt UTF-8 bytes.</param>
public sealed record CustomLoopInvocationReceiptRetentionOperation(
    int SchemaVersion,
    string OperationId,
    string Actor,
    string Surface,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReplayCutoffUtc,
    DateTimeOffset OwnershipStartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopInvocationReceiptRetentionCandidate[] Candidates,
    CustomLoopInvocationReceiptRetentionOperationState State,
    int DeletedReceiptCount,
    long DeletedReceiptUtf8Bytes)
{
    /// <summary>
    /// Identifies the current schema version custom loop invocation receipt retention operation.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
