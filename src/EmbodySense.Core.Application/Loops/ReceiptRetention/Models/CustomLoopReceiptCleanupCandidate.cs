using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents one immutable raw artifact selected by a durable cleanup intent.
/// </summary>
/// <param name="ArtifactId">The filename-safe artifact identity.</param>
/// <param name="ArtifactHash">The canonical raw artifact hash selected under ownership.</param>
/// <param name="ArtifactUtf8Bytes">The selected raw artifact byte count.</param>
/// <param name="Category">The safety category observed during selection.</param>
/// <param name="OutcomeAuditRecorded">Whether the terminal outcome audit is durably marked.</param>
/// <param name="OwnershipResolved">Whether exclusive cross-process ownership was established.</param>
/// <param name="ExpiredOperationProof">The compact expired-operation proof produced before removal.</param>
/// <param name="DefinitionLineageProof">Optional compact definition lineage or non-reuse proof produced before removal.</param>
public sealed record CustomLoopReceiptCleanupCandidate(
    string ArtifactId,
    string ArtifactHash,
    long ArtifactUtf8Bytes,
    CustomLoopReceiptArtifactCategory Category,
    bool OutcomeAuditRecorded,
    bool OwnershipResolved,
    CustomLoopExpiredOperationProof? ExpiredOperationProof,
    CustomLoopDefinitionLineageProof? DefinitionLineageProof);
