using System.Collections.Immutable;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents the durable cross-process cleanup intent and restart-recovery journal.
/// </summary>
/// <param name="SchemaVersion">The journal schema version.</param>
/// <param name="Request">The bounded governed cleanup request.</param>
/// <param name="RequestHash">The canonical request hash.</param>
/// <param name="OwnerGenerationId">The unique cross-process owner generation.</param>
/// <param name="OwnerProcessId">The owner process identity.</param>
/// <param name="OwnershipAcquiredAtUtc">The bounded ownership acquisition timestamp.</param>
/// <param name="Stage">The last durable cleanup stage.</param>
/// <param name="Outcome">The durable outcome when one is known.</param>
/// <param name="UpdatedAtUtc">The last durable transition timestamp.</param>
/// <param name="Candidates">The immutable selected artifact batch.</param>
/// <param name="ProofLedgerHash">The replacement proof-ledger hash after its durable write.</param>
/// <param name="RemovedArtifactCount">The raw artifact count attributed to this cleanup.</param>
/// <param name="RemovedArtifactUtf8Bytes">The raw artifact bytes attributed to this cleanup.</param>
/// <param name="Detail">A bounded actionable recovery detail.</param>
public sealed record CustomLoopReceiptCleanupJournal(
    int SchemaVersion,
    CustomLoopReceiptCleanupRequest Request,
    string RequestHash,
    string OwnerGenerationId,
    int OwnerProcessId,
    DateTimeOffset OwnershipAcquiredAtUtc,
    CustomLoopReceiptCleanupStage Stage,
    CustomLoopReceiptCleanupOutcome Outcome,
    DateTimeOffset UpdatedAtUtc,
    ImmutableArray<CustomLoopReceiptCleanupCandidate> Candidates,
    string? ProofLedgerHash,
    int RemovedArtifactCount,
    long RemovedArtifactUtf8Bytes,
    string Detail)
{
    /// <summary>
    /// Current cleanup-journal schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
