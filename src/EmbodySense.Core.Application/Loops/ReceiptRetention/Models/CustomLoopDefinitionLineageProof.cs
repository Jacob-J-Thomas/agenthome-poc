namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents compact custom-loop definition lineage and permanent identity non-reuse proof.
/// </summary>
/// <param name="SchemaVersion">The proof schema version.</param>
/// <param name="LoopId">The durable loop identity.</param>
/// <param name="RoleId">The immutable contextual role identity.</param>
/// <param name="LastDefinitionVersion">The last committed definition version.</param>
/// <param name="LastDefinitionHash">The canonical last-definition content hash.</param>
/// <param name="LastMutationOperationId">The last mutation operation identity.</param>
/// <param name="IsDeleted">Whether the identity has been durably deleted and cannot be reused.</param>
/// <param name="DeletedAtUtc">The deletion timestamp for a deleted identity.</param>
public sealed record CustomLoopDefinitionLineageProof(
    int SchemaVersion,
    string LoopId,
    string RoleId,
    int LastDefinitionVersion,
    string LastDefinitionHash,
    string LastMutationOperationId,
    bool IsDeleted,
    DateTimeOffset? DeletedAtUtc)
{
    /// <summary>
    /// Current compact definition-lineage proof schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
