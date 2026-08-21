namespace EmbodySense.Web.Models;

/// <summary>Requests one exact control from the shared governed-loop operational facade.</summary>
/// <param name="OperationId">The caller-owned idempotency identity reused after ambiguous outcomes.</param>
/// <param name="Kind">The exact kebab-case control kind advertised by authoritative posture.</param>
/// <param name="TargetId">The exact caller-observed target identity.</param>
/// <param name="ExpectedRevision">The exact optimistic target or catalog revision.</param>
/// <param name="ExpectedEvidenceHash">The exact optimistic target or catalog evidence hash.</param>
/// <param name="ExpectedAuthorityEvidenceHash">The exact authority hash from the same posture snapshot.</param>
/// <param name="MaximumBatchItems">One for exact controls, or the explicit bounded batch maximum.</param>
public sealed record LoopOperationalControlRequest(
    string OperationId,
    string Kind,
    string TargetId,
    long ExpectedRevision,
    string ExpectedEvidenceHash,
    string ExpectedAuthorityEvidenceHash,
    int MaximumBatchItems = 1);
