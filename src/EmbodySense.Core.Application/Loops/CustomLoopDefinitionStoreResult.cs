using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopDefinitionStoreResult(
    CustomLoopDefinitionStoreStatus Status,
    CustomLoopDefinition? Definition,
    CustomLoopDefinitionConflict? Conflict,
    CustomLoopDefinitionTombstone? Tombstone,
    CustomLoopOperationIntegrity OperationIntegrity = CustomLoopOperationIntegrity.NotTracked)
{
    public static CustomLoopDefinitionStoreResult Created(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity = CustomLoopOperationIntegrity.NotTracked) => new(CustomLoopDefinitionStoreStatus.Created, definition, null, null, integrity);

    public static CustomLoopDefinitionStoreResult AlreadyCreated(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity) => new(CustomLoopDefinitionStoreStatus.AlreadyCreated, definition, null, null, integrity);

    public static CustomLoopDefinitionStoreResult Updated(CustomLoopDefinition definition) => new(CustomLoopDefinitionStoreStatus.Updated, definition, null, null);

    public static CustomLoopDefinitionStoreResult Deleted(CustomLoopDefinition definition, CustomLoopDefinitionTombstone tombstone) => new(CustomLoopDefinitionStoreStatus.Deleted, definition, null, tombstone);

    public static CustomLoopDefinitionStoreResult VersionConflict(CustomLoopDefinition definition, int expectedDefinitionVersion)
    {
        var conflict = new CustomLoopDefinitionConflict(definition.Id, expectedDefinitionVersion, definition.DefinitionVersion, definition.ContentHash, definition.UpdatedAtUtc);
        return new CustomLoopDefinitionStoreResult(CustomLoopDefinitionStoreStatus.Conflict, null, conflict, null);
    }

    public static CustomLoopDefinitionStoreResult TombstoneConflict(CustomLoopDefinitionTombstone tombstone, int expectedDefinitionVersion)
    {
        var conflict = new CustomLoopDefinitionConflict(tombstone.LoopId, expectedDefinitionVersion, tombstone.LastDefinitionVersion, tombstone.LastContentHash, tombstone.DeletedAtUtc);
        return new CustomLoopDefinitionStoreResult(CustomLoopDefinitionStoreStatus.Conflict, null, conflict, tombstone);
    }

    public static CustomLoopDefinitionStoreResult NotFound() => new(CustomLoopDefinitionStoreStatus.NotFound, null, null, null);

    public static CustomLoopDefinitionStoreResult LimitExceeded() => new(CustomLoopDefinitionStoreStatus.LimitExceeded, null, null, null);

    public static CustomLoopDefinitionStoreResult AlreadyDeleted(CustomLoopDefinitionTombstone tombstone) => new(CustomLoopDefinitionStoreStatus.AlreadyDeleted, null, null, tombstone);

    public static CustomLoopDefinitionStoreResult OperationConflict() => new(CustomLoopDefinitionStoreStatus.OperationConflict, null, null, null);
}
