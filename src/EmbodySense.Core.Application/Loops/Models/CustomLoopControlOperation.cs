using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop control operation.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="Kind">The kind.</param>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The expected lifecycle version.</param>
/// <param name="Actor">The actor.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="ResultLifecycleVersion">The result lifecycle version.</param>
/// <param name="ResultRunStatus">The result run status.</param>
/// <param name="OutcomeAuditRecorded">The outcome audit recorded.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopControlOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    CustomLoopControlKind Kind,
    string RunId,
    int ExpectedLifecycleVersion,
    string Actor,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopControlOperationState State,
    CustomLoopControlStatus Outcome,
    int? ResultLifecycleVersion,
    CustomLoopRunStatus? ResultRunStatus,
    bool OutcomeAuditRecorded,
    string Detail)
{
    /// <summary>
    /// Identifies the current schema version custom loop control operation.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets the owner generation ID.
    /// </summary>
    /// <value>The owner generation ID.</value>
    public string? OwnerGenerationId { get; init; }

    /// <summary>
    /// Gets the owner process ID.
    /// </summary>
    /// <value>The owner process ID.</value>
    public int? OwnerProcessId { get; init; }

    /// <summary>
    /// Gets the owner acquired at UTC.
    /// </summary>
    /// <value>The owner acquired at UTC.</value>
    public DateTimeOffset? OwnerAcquiredAtUtc { get; init; }
}
