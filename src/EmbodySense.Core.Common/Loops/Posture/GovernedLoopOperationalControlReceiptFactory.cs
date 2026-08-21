using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Common.Loops.Posture;

/// <summary>Creates hash-bound immutable operational-control receipt generations.</summary>
public static class GovernedLoopOperationalControlReceiptFactory
{
    /// <summary>Creates one canonical receipt generation and computes its content hash.</summary>
    public static GovernedLoopOperationalControlReceipt Create(
        GovernedLoopOperationalControlRequest request,
        string requestHash,
        GovernedLoopOperationalControlAuthority authority,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset updatedAtUtc,
        GovernedLoopOperationalControlReceiptState state,
        GovernedLoopOperationalControlStatus outcome,
        string reasonCode,
        IReadOnlyList<GovernedLoopOperationalControlProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(progress);
        var provisional = new GovernedLoopOperationalControlReceipt(
            GovernedLoopOperationalControlReceipt.CurrentSchemaVersion,
            request.WorkspaceId,
            request.OperationId,
            requestHash,
            request.Kind,
            request.TargetId,
            request.ExpectedRevision,
            request.ExpectedEvidenceHash,
            request.ActorId,
            request.SurfaceId,
            authority.EvidenceHash,
            null,
            requestedAtUtc,
            updatedAtUtc,
            state,
            outcome,
            reasonCode,
            CopyProgress(progress),
            new string('0', GovernedLoopOperationalPostureLimits.Sha256HexCharacters));
        return new GovernedLoopOperationalControlReceipt(
            provisional.SchemaVersion,
            provisional.WorkspaceId,
            provisional.OperationId,
            provisional.RequestHash,
            provisional.Kind,
            provisional.TargetId,
            provisional.ExpectedRevision,
            provisional.ExpectedEvidenceHash,
            provisional.ActorId,
            provisional.SurfaceId,
            provisional.AuthorityEvidenceHash,
            provisional.PreviousContentHash,
            provisional.RequestedAtUtc,
            provisional.UpdatedAtUtc,
            provisional.State,
            provisional.Outcome,
            provisional.ReasonCode,
            provisional.Progress,
            GovernedLoopOperationalHash.Receipt(provisional));
    }

    /// <summary>Creates one successor while retaining the exact original request and authority transaction.</summary>
    public static GovernedLoopOperationalControlReceipt Successor(
        GovernedLoopOperationalControlReceipt current,
        DateTimeOffset updatedAtUtc,
        GovernedLoopOperationalControlReceiptState state,
        GovernedLoopOperationalControlStatus outcome,
        string reasonCode,
        IReadOnlyList<GovernedLoopOperationalControlProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(progress);
        var provisional = new GovernedLoopOperationalControlReceipt(
            current.SchemaVersion,
            current.WorkspaceId,
            current.OperationId,
            current.RequestHash,
            current.Kind,
            current.TargetId,
            current.ExpectedRevision,
            current.ExpectedEvidenceHash,
            current.ActorId,
            current.SurfaceId,
            current.AuthorityEvidenceHash,
            current.ContentHash,
            current.RequestedAtUtc,
            updatedAtUtc,
            state,
            outcome,
            reasonCode,
            CopyProgress(progress),
            new string('0', GovernedLoopOperationalPostureLimits.Sha256HexCharacters));
        return new GovernedLoopOperationalControlReceipt(
            provisional.SchemaVersion,
            provisional.WorkspaceId,
            provisional.OperationId,
            provisional.RequestHash,
            provisional.Kind,
            provisional.TargetId,
            provisional.ExpectedRevision,
            provisional.ExpectedEvidenceHash,
            provisional.ActorId,
            provisional.SurfaceId,
            provisional.AuthorityEvidenceHash,
            provisional.PreviousContentHash,
            provisional.RequestedAtUtc,
            provisional.UpdatedAtUtc,
            provisional.State,
            provisional.Outcome,
            provisional.ReasonCode,
            provisional.Progress,
            GovernedLoopOperationalHash.Receipt(provisional));
    }

    private static IReadOnlyList<GovernedLoopOperationalControlProgress> CopyProgress(
        IReadOnlyList<GovernedLoopOperationalControlProgress> progress)
        => Array.AsReadOnly(progress.Select(item => item with { }).ToArray());
}
