using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Represents a custom loop definition store result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Definition">The definition.</param>
/// <param name="Conflict">The conflict.</param>
/// <param name="Tombstone">The tombstone.</param>
/// <param name="OperationIntegrity">The operation integrity.</param>
/// <param name="RetentionExhaustionReason">The exact receipt-retention boundary that rejected admission, or none for non-retention outcomes.</param>
public sealed record CustomLoopDefinitionStoreResult(
    CustomLoopDefinitionStoreStatus Status,
    CustomLoopDefinition? Definition,
    CustomLoopDefinitionConflict? Conflict,
    CustomLoopDefinitionTombstone? Tombstone,
    CustomLoopOperationIntegrity OperationIntegrity = CustomLoopOperationIntegrity.NotTracked,
    CustomLoopReceiptQuotaExhaustionReason RetentionExhaustionReason = CustomLoopReceiptQuotaExhaustionReason.None)
{
    /// <summary>
    /// Creates a successful definition-creation result.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="integrity">The integrity.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult Created(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity = CustomLoopOperationIntegrity.NotTracked) => new(CustomLoopDefinitionStoreStatus.Created, definition, null, null, integrity);

    /// <summary>
    /// Creates a custom loop definition store result representing already created.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="integrity">The integrity.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult AlreadyCreated(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity) => new(CustomLoopDefinitionStoreStatus.AlreadyCreated, definition, null, null, integrity);

    /// <summary>
    /// Creates a custom loop definition store result representing updated.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult Updated(CustomLoopDefinition definition) => new(CustomLoopDefinitionStoreStatus.Updated, definition, null, null);

    /// <summary>
    /// Creates a custom loop definition store result representing deleted.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="tombstone">The tombstone.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult Deleted(CustomLoopDefinition definition, CustomLoopDefinitionTombstone tombstone) => new(CustomLoopDefinitionStoreStatus.Deleted, definition, null, tombstone);

    /// <summary>
    /// Creates a custom loop definition store result representing version conflict.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult VersionConflict(CustomLoopDefinition definition, int expectedDefinitionVersion)
    {
        var conflict = new CustomLoopDefinitionConflict(definition.Id, expectedDefinitionVersion, definition.DefinitionVersion, definition.ContentHash, definition.UpdatedAtUtc);
        return new CustomLoopDefinitionStoreResult(CustomLoopDefinitionStoreStatus.Conflict, null, conflict, null);
    }

    /// <summary>
    /// Creates a version-conflict result from a durable deletion tombstone.
    /// </summary>
    /// <param name="tombstone">The tombstone.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult TombstoneConflict(CustomLoopDefinitionTombstone tombstone, int expectedDefinitionVersion)
    {
        var conflict = new CustomLoopDefinitionConflict(tombstone.LoopId, expectedDefinitionVersion, tombstone.LastDefinitionVersion, tombstone.LastContentHash, tombstone.DeletedAtUtc);
        return new CustomLoopDefinitionStoreResult(CustomLoopDefinitionStoreStatus.Conflict, null, conflict, tombstone);
    }

    /// <summary>
    /// Creates a custom loop definition store result representing not found.
    /// </summary>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult NotFound() => new(CustomLoopDefinitionStoreStatus.NotFound, null, null, null);

    /// <summary>
    /// Creates a custom loop definition store result representing limit exceeded.
    /// </summary>
    /// <param name="retentionExhaustionReason">The exact retention boundary, or none when the definition-count limit was reached.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult LimitExceeded(CustomLoopReceiptQuotaExhaustionReason retentionExhaustionReason = CustomLoopReceiptQuotaExhaustionReason.None) => new(CustomLoopDefinitionStoreStatus.LimitExceeded, null, null, null, RetentionExhaustionReason: retentionExhaustionReason);

    /// <summary>
    /// Creates a custom loop definition store result representing already deleted.
    /// </summary>
    /// <param name="tombstone">The tombstone.</param>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult AlreadyDeleted(CustomLoopDefinitionTombstone tombstone) => new(CustomLoopDefinitionStoreStatus.AlreadyDeleted, null, null, tombstone);

    /// <summary>
    /// Creates a custom loop definition store result representing operation conflict.
    /// </summary>
    /// <returns>The custom loop definition store result.</returns>
    public static CustomLoopDefinitionStoreResult OperationConflict() => new(CustomLoopDefinitionStoreStatus.OperationConflict, null, null, null);
}
