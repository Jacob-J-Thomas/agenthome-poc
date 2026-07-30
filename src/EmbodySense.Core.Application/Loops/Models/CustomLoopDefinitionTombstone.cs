namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop definition tombstone.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="LastDefinitionVersion">The last definition version.</param>
/// <param name="LastContentHash">The last content hash.</param>
/// <param name="MutationOperationId">The mutation operation ID.</param>
/// <param name="DeletedAtUtc">The deleted at UTC.</param>
public sealed record CustomLoopDefinitionTombstone(
    int SchemaVersion,
    string LoopId,
    int LastDefinitionVersion,
    string LastContentHash,
    string MutationOperationId,
    DateTimeOffset DeletedAtUtc)
{
    /// <summary>
    /// Identifies the current schema version custom loop definition tombstone.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
