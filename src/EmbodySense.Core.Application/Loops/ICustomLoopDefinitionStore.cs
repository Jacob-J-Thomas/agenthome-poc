using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopDefinitionStore
{
    Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => CreateAsync(definition, cancellationToken);

    Task<CustomLoopCreateOperationLookupResult> GetCreateOperationAsync(string operationId, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionMutationLookupResult> GetMutationOperationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopDefinitionMutationLookupResult.NotFound());

    Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => UpdateAsync(definition, expectedDefinitionVersion, cancellationToken);

    Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default);

    Task<CustomLoopDefinitionStoreResult> DeleteAsync(string loopId, int expectedDefinitionVersion, string mutationOperationId, DateTimeOffset deletedAtUtc, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default) => DeleteAsync(loopId, expectedDefinitionVersion, mutationOperationId, deletedAtUtc, cancellationToken);

    Task<CustomLoopOperationAuditMarkStatus> MarkOperationOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default);
}

public enum CustomLoopDefinitionStoreStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    Deleted = 3,
    Conflict = 4,
    NotFound = 5,
    LimitExceeded = 6,
    AlreadyDeleted = 7,
    AlreadyCreated = 8,
    OperationConflict = 9
}

public enum CustomLoopOperationIntegrity
{
    Unknown = 0,
    NotTracked = 1,
    PendingMutation = 2,
    PendingOutcomeAudit = 3,
    Complete = 4
}

public enum CustomLoopDefinitionMutationKind
{
    Unknown = 0,
    Create = 1,
    Update = 2,
    Delete = 3
}

public enum CustomLoopDefinitionMutationState
{
    Unknown = 0,
    PendingMutation = 1,
    OutcomeCommitted = 2
}

public sealed record CustomLoopDefinitionMutationRequest(
    CustomLoopDefinitionMutationKind Kind,
    string OperationId,
    string RequestHash,
    string LoopId,
    string RoleId,
    int? ExpectedDefinitionVersion,
    CustomLoopDefinition? PlannedDefinition,
    CustomLoopDefinition? PriorDefinition,
    DateTimeOffset RequestedAtUtc);

public sealed record CustomLoopDefinitionMutationOperation(
    int SchemaVersion,
    CustomLoopDefinitionMutationKind Kind,
    string OperationId,
    string RequestHash,
    string LoopId,
    string RoleId,
    int? ExpectedDefinitionVersion,
    CustomLoopDefinition? PlannedDefinition,
    CustomLoopDefinition? PriorDefinition,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopDefinitionMutationState State,
    CustomLoopDefinitionStoreStatus Outcome,
    CustomLoopDefinition? ResultDefinition,
    CustomLoopDefinitionConflict? ResultConflict,
    CustomLoopDefinitionTombstone? ResultTombstone,
    bool OutcomeAuditRecorded)
{
    public const int CurrentSchemaVersion = 1;

    public CustomLoopOperationIntegrity Integrity => State == CustomLoopDefinitionMutationState.PendingMutation
        ? CustomLoopOperationIntegrity.PendingMutation
        : OutcomeAuditRecorded ? CustomLoopOperationIntegrity.Complete : CustomLoopOperationIntegrity.PendingOutcomeAudit;

    public CustomLoopDefinitionStoreResult ToStoreResult()
    {
        if (State != CustomLoopDefinitionMutationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A pending mutation operation has no replayable store result.");
        }

        return new CustomLoopDefinitionStoreResult(Outcome, ResultDefinition, ResultConflict, ResultTombstone, Integrity);
    }
}

public enum CustomLoopDefinitionMutationLookupStatus
{
    Unknown = 0,
    NotFound = 1,
    PendingMutation = 2,
    OutcomeCommitted = 3
}

public sealed record CustomLoopDefinitionMutationLookupResult(CustomLoopDefinitionMutationLookupStatus Status, CustomLoopDefinitionMutationOperation? Operation)
{
    public static CustomLoopDefinitionMutationLookupResult NotFound() => new(CustomLoopDefinitionMutationLookupStatus.NotFound, null);

    public static CustomLoopDefinitionMutationLookupResult Found(CustomLoopDefinitionMutationOperation operation)
    {
        var status = operation.State == CustomLoopDefinitionMutationState.PendingMutation ? CustomLoopDefinitionMutationLookupStatus.PendingMutation : CustomLoopDefinitionMutationLookupStatus.OutcomeCommitted;
        return new CustomLoopDefinitionMutationLookupResult(status, operation);
    }
}

public enum CustomLoopOperationAuditMarkStatus
{
    Unknown = 0,
    Marked = 1,
    AlreadyMarked = 2,
    NotFound = 3
}

public enum CustomLoopCreateOperationLookupStatus
{
    Unknown = 0,
    NotFound = 1,
    PendingDefinitionCommit = 2,
    Committed = 3
}

public sealed record CustomLoopCreateOperationLookupResult(
    CustomLoopCreateOperationLookupStatus Status,
    CustomLoopDefinition? Definition,
    CustomLoopOperationIntegrity OperationIntegrity)
{
    public static CustomLoopCreateOperationLookupResult NotFound() => new(CustomLoopCreateOperationLookupStatus.NotFound, null, CustomLoopOperationIntegrity.NotTracked);

    public static CustomLoopCreateOperationLookupResult PendingDefinitionCommit(CustomLoopDefinition definition) => new(CustomLoopCreateOperationLookupStatus.PendingDefinitionCommit, definition, CustomLoopOperationIntegrity.PendingOutcomeAudit);

    public static CustomLoopCreateOperationLookupResult Committed(CustomLoopDefinition definition, CustomLoopOperationIntegrity integrity) => new(CustomLoopCreateOperationLookupStatus.Committed, definition, integrity);
}

public sealed record CustomLoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);

public sealed record CustomLoopDefinitionTombstone(
    int SchemaVersion,
    string LoopId,
    int LastDefinitionVersion,
    string LastContentHash,
    string MutationOperationId,
    DateTimeOffset DeletedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

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
