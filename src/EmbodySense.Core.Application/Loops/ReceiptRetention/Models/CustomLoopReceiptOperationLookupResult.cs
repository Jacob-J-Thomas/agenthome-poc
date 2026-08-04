using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents exact, expired, or unknown idempotency lookup posture.
/// </summary>
/// <param name="ArtifactClass">The requested receipt artifact class.</param>
/// <param name="OperationId">The requested operation identity.</param>
/// <param name="Status">The exact, expired, or unknown status.</param>
/// <param name="ExpiredProof">Compact proof required for an expired result.</param>
/// <param name="Detail">A bounded actionable detail.</param>
public sealed record CustomLoopReceiptOperationLookupResult(
    CustomLoopReceiptArtifactClass ArtifactClass,
    string OperationId,
    CustomLoopReceiptOperationLookupStatus Status,
    CustomLoopExpiredOperationProof? ExpiredProof,
    string Detail);
