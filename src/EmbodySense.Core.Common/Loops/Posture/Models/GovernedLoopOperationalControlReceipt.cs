namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Persists one schema-1 operational-control intent, progress set, and closed outcome.</summary>
public sealed record GovernedLoopOperationalControlReceipt
{
    /// <summary>Creates a receipt over a caller-supplied immutable progress collection.</summary>
    public GovernedLoopOperationalControlReceipt(
        int schemaVersion,
        string workspaceId,
        string operationId,
        string requestHash,
        GovernedLoopOperationalControlKind kind,
        string targetId,
        long expectedRevision,
        string expectedEvidenceHash,
        string actorId,
        string surfaceId,
        string authorityEvidenceHash,
        string? previousContentHash,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset updatedAtUtc,
        GovernedLoopOperationalControlReceiptState state,
        GovernedLoopOperationalControlStatus outcome,
        string reasonCode,
        IReadOnlyList<GovernedLoopOperationalControlProgress> progress,
        string contentHash)
    {
        SchemaVersion = schemaVersion;
        WorkspaceId = workspaceId;
        OperationId = operationId;
        RequestHash = requestHash;
        Kind = kind;
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        ExpectedEvidenceHash = expectedEvidenceHash;
        ActorId = actorId;
        SurfaceId = surfaceId;
        AuthorityEvidenceHash = authorityEvidenceHash;
        PreviousContentHash = previousContentHash;
        RequestedAtUtc = requestedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        State = state;
        Outcome = outcome;
        ReasonCode = reasonCode;
        Progress = progress;
        ContentHash = contentHash;
    }

    /// <summary>Gets the only supported receipt schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopOperationalPostureLimits.CurrentSchemaVersion;

    /// <summary>Gets the receipt schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the trusted workspace identity.</summary>
    public string WorkspaceId { get; }

    /// <summary>Gets the caller-owned idempotency identity.</summary>
    public string OperationId { get; }

    /// <summary>Gets the exact canonical request hash.</summary>
    public string RequestHash { get; }

    /// <summary>Gets the closed control kind.</summary>
    public GovernedLoopOperationalControlKind Kind { get; }

    /// <summary>Gets the top-level target identity.</summary>
    public string TargetId { get; }

    /// <summary>Gets the caller-observed target revision.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Gets the caller-observed target evidence hash.</summary>
    public string ExpectedEvidenceHash { get; }

    /// <summary>Gets the trusted actor retained at admission.</summary>
    public string ActorId { get; }

    /// <summary>Gets the authenticated caller surface retained at admission.</summary>
    public string SurfaceId { get; }

    /// <summary>Gets the exact current authority evidence hash retained before mutation.</summary>
    public string AuthorityEvidenceHash { get; }

    /// <summary>Gets the predecessor receipt hash, or <see langword="null"/> for the durable intent generation.</summary>
    public string? PreviousContentHash { get; }

    /// <summary>Gets the trusted request instant.</summary>
    public DateTimeOffset RequestedAtUtc { get; }

    /// <summary>Gets the trusted latest receipt update instant.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Gets the durable receipt phase.</summary>
    public GovernedLoopOperationalControlReceiptState State { get; }

    /// <summary>Gets the current closed outcome.</summary>
    public GovernedLoopOperationalControlStatus Outcome { get; }

    /// <summary>Gets a stable value-free reason.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the defensively captured bounded target progress.</summary>
    public IReadOnlyList<GovernedLoopOperationalControlProgress> Progress { get; }

    /// <summary>Gets the canonical content hash.</summary>
    public string ContentHash { get; }
}
