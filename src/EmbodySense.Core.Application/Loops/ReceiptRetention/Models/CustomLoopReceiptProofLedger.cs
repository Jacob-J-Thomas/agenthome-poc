using System.Collections.Immutable;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents the canonical compact proof ledger replacing expired full receipts and tombstones.
/// </summary>
/// <param name="SchemaVersion">The ledger schema version.</param>
/// <param name="Generation">The monotonically increasing ledger generation.</param>
/// <param name="CreatedAtUtc">The ledger creation timestamp.</param>
/// <param name="PreviousLedgerHash">The preceding canonical ledger hash, or null for the first generation.</param>
/// <param name="DefinitionLineage">The canonical loop lineage and non-reuse proofs.</param>
/// <param name="ExpiredOperations">The canonical expired-operation fingerprint proofs.</param>
public sealed record CustomLoopReceiptProofLedger(
    int SchemaVersion,
    long Generation,
    DateTimeOffset CreatedAtUtc,
    string? PreviousLedgerHash,
    ImmutableArray<CustomLoopDefinitionLineageProof> DefinitionLineage,
    ImmutableArray<CustomLoopExpiredOperationProof> ExpiredOperations)
{
    /// <summary>
    /// Current compact proof-ledger schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
