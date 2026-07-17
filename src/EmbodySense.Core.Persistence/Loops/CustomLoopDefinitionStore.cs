using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class CustomLoopDefinitionStore : ICustomLoopDefinitionStore
{
    private const int DefinitionMutationOperationSchemaVersion = 1;
    private const long MaxDefinitionArtifactBytes = 512 * 1024;
    private const long MaxTombstoneArtifactBytes = 16 * 1024;
    private const long MaxDefinitionMutationOperationArtifactBytes = 640 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly WorkspacePaths _paths;
    private readonly CustomLoopArtifactPathGuard _pathGuard;
    private readonly SemaphoreSlim _mutationGate;

    public CustomLoopDefinitionStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _pathGuard = new CustomLoopArtifactPathGuard(paths.RootPath);
        _mutationGate = MutationGates.GetOrAdd(Path.GetFullPath(paths.CustomLoopDefinitionsPath), _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalDefinition(definition);
        var mutation = new CustomLoopDefinitionMutationRequest(
            CustomLoopDefinitionMutationKind.Create,
            definition.LastMutationOperationId,
            ComputeCreateRequestHash(definition.RoleId),
            definition.Id,
            definition.RoleId,
            null,
            definition,
            null,
            definition.CreatedAtUtc);
        var result = await CreateAsync(definition, mutation, cancellationToken);
        if (result.Status != CustomLoopDefinitionStoreStatus.OperationConflict)
        {
            return result;
        }

        var existing = await GetMutationOperationAsync(definition.LastMutationOperationId, cancellationToken);
        var original = existing.Operation?.PlannedDefinition ?? throw new FormatException($"Create operation `{definition.LastMutationOperationId}` is missing its original definition snapshot.");
        return CustomLoopDefinitionStoreResult.VersionConflict(original, expectedDefinitionVersion: 0);
    }

    public async Task<CustomLoopDefinitionStoreResult> CreateAsync(CustomLoopDefinition definition, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalDefinition(definition);
        if (definition.DefinitionVersion != 1)
        {
            throw new ArgumentException("New custom loop definitions must have definition version 1.", nameof(definition));
        }

        ValidateMutationRequest(mutation, CustomLoopDefinitionMutationKind.Create, definition.Id, definition.RoleId, null, definition, null);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            var operationId = mutation.OperationId;
            var existingOperation = state.Operations.SingleOrDefault(operation => string.Equals(operation.OperationId, operationId, StringComparison.Ordinal));
            if (existingOperation is not null)
            {
                ValidateWorkspaceState(state, allowedPendingOperationId: operationId);
                if (!MutationRequestMatches(existingOperation, mutation))
                {
                    return CustomLoopDefinitionStoreResult.OperationConflict();
                }

                if (existingOperation.State == CustomLoopDefinitionMutationState.PendingMutation || !HasCommittedCreateArtifact(state, existingOperation))
                {
                    if (existingOperation.OutcomeAuditRecorded)
                    {
                        throw new FormatException($"Create operation `{operationId}` records a completed audit outcome without a committed definition.");
                    }

                    var original = existingOperation.OriginalDefinition ?? existingOperation.PlannedDefinition ?? throw new FormatException($"Create operation `{operationId}` is missing its original definition snapshot.");
                    var recovered = HasDefinitionSnapshot(state, original)
                        ? CustomLoopDefinitionStoreResult.Created(original, CustomLoopOperationIntegrity.PendingOutcomeAudit)
                        : await ExecuteCreateAsync(state, original, cancellationToken);
                    existingOperation = CompleteOperation(existingOperation, recovered, existingOperation.UpdatedAtUtc);
                    await WriteOperationAsync(existingOperation, cancellationToken);
                }

                var replay = existingOperation.ToPublic().ToStoreResult();
                return replay.Status == CustomLoopDefinitionStoreStatus.Created
                    ? CustomLoopDefinitionStoreResult.AlreadyCreated(replay.Definition!, replay.OperationIntegrity)
                    : replay;
            }

            var orphanedCreates = state.Definitions.Where(candidate => string.Equals(candidate.LastMutationOperationId, operationId, StringComparison.Ordinal)).ToArray();
            if (orphanedCreates.Length > 1)
            {
                throw new FormatException($"Create operation `{operationId}` is associated with multiple definition artifacts.");
            }

            if (orphanedCreates.Length == 1)
            {
                var orphaned = orphanedCreates[0];
                if (orphaned.DefinitionVersion != 1 || !string.Equals(orphaned.RoleId, definition.RoleId, StringComparison.Ordinal))
                {
                    return CustomLoopDefinitionStoreResult.VersionConflict(orphaned, expectedDefinitionVersion: 0);
                }

                var recoveredRequest = mutation with { LoopId = orphaned.Id, PlannedDefinition = orphaned, RequestedAtUtc = orphaned.CreatedAtUtc };
                var recoveredOperation = CreatePendingOperation(recoveredRequest, originalDefinition: orphaned);
                var recoveredResult = CustomLoopDefinitionStoreResult.Created(orphaned, CustomLoopOperationIntegrity.PendingOutcomeAudit);
                recoveredOperation = CompleteOperation(recoveredOperation, recoveredResult, orphaned.CreatedAtUtc);
                var recoveredState = state with { Operations = [.. state.Operations, recoveredOperation] };
                ValidateWorkspaceState(recoveredState, allowedPendingOperationId: operationId);
                await WriteOperationAsync(recoveredOperation, cancellationToken);
                return CustomLoopDefinitionStoreResult.AlreadyCreated(orphaned, CustomLoopOperationIntegrity.PendingOutcomeAudit);
            }

            ValidateWorkspaceState(state);
            var tombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, definition.Id, StringComparison.Ordinal));
            if (tombstone is not null)
            {
                return CustomLoopDefinitionStoreResult.TombstoneConflict(tombstone, expectedDefinitionVersion: 0);
            }

            var current = state.Definitions.SingleOrDefault(candidate => string.Equals(candidate.Id, definition.Id, StringComparison.Ordinal));
            if (current is not null)
            {
                return CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion: 0);
            }

            if (state.Definitions.Count >= CustomLoopLimits.MaxDefinitionsPerWorkspace)
            {
                return CustomLoopDefinitionStoreResult.LimitExceeded();
            }

            var operation = CreatePendingOperation(mutation, originalDefinition: definition);
            await WriteOperationAsync(operation, cancellationToken);
            var storeResult = await ExecuteCreateAsync(state, definition, cancellationToken);
            operation = CompleteOperation(operation, storeResult, definition.CreatedAtUtc);
            await WriteOperationAsync(operation, cancellationToken);
            return operation.ToPublic().ToStoreResult();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopCreateOperationLookupResult> GetCreateOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            var operation = state.Operations.SingleOrDefault(candidate => string.Equals(candidate.OperationId, safeOperationId, StringComparison.Ordinal));
            if (operation is null || operation.Kind != CustomLoopDefinitionMutationKind.Create)
            {
                ValidateWorkspaceState(state);
                return CustomLoopCreateOperationLookupResult.NotFound();
            }

            ValidateWorkspaceState(state, allowedPendingOperationId: safeOperationId);
            var original = operation.OriginalDefinition ?? operation.PlannedDefinition ?? throw new FormatException($"Create operation `{safeOperationId}` is missing its original definition snapshot.");
            return operation.State == CustomLoopDefinitionMutationState.OutcomeCommitted && HasCommittedCreateArtifact(state, operation)
                ? CustomLoopCreateOperationLookupResult.Committed(original, operation.ToPublic().Integrity)
                : CustomLoopCreateOperationLookupResult.PendingDefinitionCommit(original);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopDefinitionMutationLookupResult> GetMutationOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            var operation = state.Operations.SingleOrDefault(candidate => string.Equals(candidate.OperationId, safeOperationId, StringComparison.Ordinal));
            if (operation is null)
            {
                ValidateWorkspaceState(state);
                return CustomLoopDefinitionMutationLookupResult.NotFound();
            }

            ValidateWorkspaceState(state, allowedPendingOperationId: safeOperationId);
            var publicOperation = operation.ToPublic();
            if (operation.Kind == CustomLoopDefinitionMutationKind.Create && !HasCommittedCreateArtifact(state, operation))
            {
                publicOperation = publicOperation with
                {
                    State = CustomLoopDefinitionMutationState.PendingMutation,
                    Outcome = CustomLoopDefinitionStoreStatus.Unknown,
                    ResultDefinition = null,
                    ResultConflict = null,
                    ResultTombstone = null
                };
            }

            return CustomLoopDefinitionMutationLookupResult.Found(publicOperation);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopDefinition?> GetAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            ValidateWorkspaceState(state);
            return state.Definitions.SingleOrDefault(definition => string.Equals(definition.Id, safeLoopId, StringComparison.Ordinal));
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<IReadOnlyList<CustomLoopDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            ValidateWorkspaceState(state);
            return state.Definitions.OrderBy(definition => definition.Id, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalDefinition(definition);
        ValidateExpectedVersion(expectedDefinitionVersion);
        if (definition.DefinitionVersion != checked(expectedDefinitionVersion + 1))
        {
            throw new ArgumentException("Updated custom loop definition version must be exactly one greater than the expected version.", nameof(definition));
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            ValidateWorkspaceState(state);
            var current = state.Definitions.SingleOrDefault(candidate => string.Equals(candidate.Id, definition.Id, StringComparison.Ordinal));
            if (current is null)
            {
                var tombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, definition.Id, StringComparison.Ordinal));
                return tombstone is null
                    ? CustomLoopDefinitionStoreResult.NotFound()
                    : CustomLoopDefinitionStoreResult.TombstoneConflict(tombstone, expectedDefinitionVersion);
            }

            if (current.DefinitionVersion != expectedDefinitionVersion)
            {
                return CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion);
            }

            await WriteJsonAsync(_paths.CustomLoopDefinitionsPath, GetDefinitionPath(definition.Id), definition, cancellationToken);
            return CustomLoopDefinitionStoreResult.Updated(definition);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopDefinitionStoreResult> UpdateAsync(CustomLoopDefinition definition, int expectedDefinitionVersion, CustomLoopDefinitionMutationRequest mutation, CancellationToken cancellationToken = default)
    {
        ValidateCanonicalDefinition(definition);
        ValidateExpectedVersion(expectedDefinitionVersion);
        if (definition.DefinitionVersion != checked(expectedDefinitionVersion + 1))
        {
            throw new ArgumentException("Updated custom loop definition version must be exactly one greater than the expected version.", nameof(definition));
        }

        ValidateMutationRequest(mutation, CustomLoopDefinitionMutationKind.Update, definition.Id, definition.RoleId, expectedDefinitionVersion, definition, mutation.PriorDefinition);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            var existingOperation = state.Operations.SingleOrDefault(operation => string.Equals(operation.OperationId, mutation.OperationId, StringComparison.Ordinal));
            if (existingOperation is not null)
            {
                ValidateWorkspaceState(state, allowedPendingOperationId: mutation.OperationId);
                if (!MutationRequestMatches(existingOperation, mutation))
                {
                    return CustomLoopDefinitionStoreResult.OperationConflict();
                }

                if (existingOperation.State == CustomLoopDefinitionMutationState.PendingMutation)
                {
                    var planned = existingOperation.PlannedDefinition ?? throw new FormatException($"Update operation `{mutation.OperationId}` is missing its planned definition snapshot.");
                    var recovered = HasDefinitionSnapshot(state, planned)
                        ? CustomLoopDefinitionStoreResult.Updated(planned)
                        : await ExecuteUpdateAsync(state, planned, existingOperation.ExpectedDefinitionVersion!.Value, cancellationToken);
                    existingOperation = CompleteOperation(existingOperation, recovered, planned.UpdatedAtUtc);
                    await WriteOperationAsync(existingOperation, cancellationToken);
                }

                return existingOperation.ToPublic().ToStoreResult();
            }

            ValidateWorkspaceState(state);
            var operation = CreatePendingOperation(mutation);
            await WriteOperationAsync(operation, cancellationToken);
            var result = await ExecuteUpdateAsync(state, definition, expectedDefinitionVersion, cancellationToken);
            operation = CompleteOperation(operation, result, definition.UpdatedAtUtc);
            await WriteOperationAsync(operation, cancellationToken);
            return operation.ToPublic().ToStoreResult();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopDefinitionStoreResult> DeleteAsync(
        string loopId,
        int expectedDefinitionVersion,
        string mutationOperationId,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        ValidateExpectedVersion(expectedDefinitionVersion);
        var safeOperationId = CustomLoopArtifactIdentifier.Require(mutationOperationId, nameof(mutationOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (deletedAtUtc == default || deletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Custom loop deletion timestamps must be non-default UTC values.", nameof(deletedAtUtc));
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            ValidateWorkspaceState(state);
            var current = state.Definitions.SingleOrDefault(candidate => string.Equals(candidate.Id, safeLoopId, StringComparison.Ordinal));
            if (current is null)
            {
                var existingTombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, safeLoopId, StringComparison.Ordinal));
                if (existingTombstone is null)
                {
                    return CustomLoopDefinitionStoreResult.NotFound();
                }

                return existingTombstone.LastDefinitionVersion == expectedDefinitionVersion && string.Equals(existingTombstone.MutationOperationId, safeOperationId, StringComparison.Ordinal)
                    ? CustomLoopDefinitionStoreResult.AlreadyDeleted(existingTombstone)
                    : CustomLoopDefinitionStoreResult.TombstoneConflict(existingTombstone, expectedDefinitionVersion);
            }

            if (current.DefinitionVersion != expectedDefinitionVersion)
            {
                return CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion);
            }

            var tombstone = new CustomLoopDefinitionTombstone(
                CustomLoopDefinitionTombstone.CurrentSchemaVersion,
                safeLoopId,
                current.DefinitionVersion,
                current.ContentHash,
                safeOperationId,
                deletedAtUtc);
            await WriteJsonAsync(_paths.CustomLoopDefinitionTombstonesPath, GetTombstonePath(safeLoopId), tombstone, cancellationToken);
            _pathGuard.DeleteFile(_paths.CustomLoopDefinitionsPath, GetDefinitionPath(safeLoopId));
            return CustomLoopDefinitionStoreResult.Deleted(current, tombstone);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopDefinitionStoreResult> DeleteAsync(
        string loopId,
        int expectedDefinitionVersion,
        string mutationOperationId,
        DateTimeOffset deletedAtUtc,
        CustomLoopDefinitionMutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        ValidateExpectedVersion(expectedDefinitionVersion);
        var safeOperationId = CustomLoopArtifactIdentifier.Require(mutationOperationId, nameof(mutationOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (deletedAtUtc == default || deletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Custom loop deletion timestamps must be non-default UTC values.", nameof(deletedAtUtc));
        }

        ValidateMutationRequest(mutation, CustomLoopDefinitionMutationKind.Delete, safeLoopId, mutation.RoleId, expectedDefinitionVersion, null, mutation.PriorDefinition);
        if (!string.Equals(safeOperationId, mutation.OperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Delete operation id must match its durable mutation request.", nameof(mutationOperationId));
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            var existingOperation = state.Operations.SingleOrDefault(operation => string.Equals(operation.OperationId, safeOperationId, StringComparison.Ordinal));
            if (existingOperation is not null)
            {
                ValidateWorkspaceState(state, allowedPendingOperationId: safeOperationId);
                if (!MutationRequestMatches(existingOperation, mutation))
                {
                    return CustomLoopDefinitionStoreResult.OperationConflict();
                }

                if (existingOperation.State == CustomLoopDefinitionMutationState.PendingMutation)
                {
                    var matchingTombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, safeLoopId, StringComparison.Ordinal)
                        && candidate.LastDefinitionVersion == expectedDefinitionVersion
                        && string.Equals(candidate.MutationOperationId, safeOperationId, StringComparison.Ordinal));
                    var recovered = matchingTombstone is not null
                        ? CustomLoopDefinitionStoreResult.Deleted(existingOperation.PriorDefinition!, matchingTombstone)
                        : await ExecuteDeleteAsync(state, safeLoopId, expectedDefinitionVersion, safeOperationId, deletedAtUtc, cancellationToken);
                    existingOperation = CompleteOperation(existingOperation, recovered, deletedAtUtc);
                    await WriteOperationAsync(existingOperation, cancellationToken);
                }

                return existingOperation.ToPublic().ToStoreResult();
            }

            ValidateWorkspaceState(state);
            var operation = CreatePendingOperation(mutation);
            await WriteOperationAsync(operation, cancellationToken);
            var result = await ExecuteDeleteAsync(state, safeLoopId, expectedDefinitionVersion, safeOperationId, deletedAtUtc, cancellationToken);
            operation = CompleteOperation(operation, result, deletedAtUtc);
            await WriteOperationAsync(operation, cancellationToken);
            return operation.ToPublic().ToStoreResult();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CustomLoopOperationAuditMarkStatus> MarkOperationOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_paths.LoopDefinitionsPath);
            var state = await ReadWorkspaceStateAsync(cancellationToken);
            var operation = state.Operations.SingleOrDefault(candidate => string.Equals(candidate.OperationId, safeOperationId, StringComparison.Ordinal));
            if (operation is null)
            {
                ValidateWorkspaceState(state);
                return CustomLoopOperationAuditMarkStatus.NotFound;
            }

            ValidateWorkspaceState(state, allowedPendingOperationId: safeOperationId);
            if (operation.State != CustomLoopDefinitionMutationState.OutcomeCommitted)
            {
                throw new InvalidOperationException($"Definition mutation operation `{safeOperationId}` cannot record an outcome audit before its mutation outcome is durable.");
            }

            if (operation.Kind == CustomLoopDefinitionMutationKind.Create && !HasCommittedCreateArtifact(state, operation))
            {
                throw new InvalidOperationException($"Create operation `{safeOperationId}` cannot record an outcome audit before its definition commit is durable.");
            }

            if (operation.OutcomeAuditRecorded)
            {
                return CustomLoopOperationAuditMarkStatus.AlreadyMarked;
            }

            var completed = operation with { OutcomeAuditRecorded = true };
            await WriteOperationAsync(completed, cancellationToken);
            return CustomLoopOperationAuditMarkStatus.Marked;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<WorkspaceState> ReadWorkspaceStateAsync(CancellationToken cancellationToken)
    {
        var definitions = await ReadDefinitionsAsync(cancellationToken);
        var tombstones = await ReadTombstonesAsync(cancellationToken);
        var operations = await ReadMutationOperationsAsync(cancellationToken);
        return new WorkspaceState(definitions, tombstones, operations);
    }

    private async Task<IReadOnlyList<CustomLoopDefinition>> ReadDefinitionsAsync(CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_paths.CustomLoopDefinitionsPath))
        {
            return [];
        }

        var definitions = new List<CustomLoopDefinition>();
        foreach (var path in Directory.EnumerateFiles(_paths.CustomLoopDefinitionsPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedId = Path.GetFileNameWithoutExtension(path);
            var definition = await ReadStrictJsonAsync<CustomLoopDefinition>(_paths.CustomLoopDefinitionsPath, path, MaxDefinitionArtifactBytes, "Custom loop definition", cancellationToken);
            ValidateCanonicalDefinition(definition);
            if (!string.Equals(definition.Id, expectedId, StringComparison.Ordinal))
            {
                throw new FormatException($"Custom loop definition `{path}` id does not match its filename.");
            }

            definitions.Add(definition);
        }

        return definitions;
    }

    private async Task<IReadOnlyList<CustomLoopDefinitionTombstone>> ReadTombstonesAsync(CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_paths.CustomLoopDefinitionTombstonesPath))
        {
            return [];
        }

        var tombstones = new List<CustomLoopDefinitionTombstone>();
        foreach (var path in Directory.EnumerateFiles(_paths.CustomLoopDefinitionTombstonesPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedId = Path.GetFileNameWithoutExtension(path);
            var tombstone = await ReadStrictJsonAsync<CustomLoopDefinitionTombstone>(_paths.CustomLoopDefinitionTombstonesPath, path, MaxTombstoneArtifactBytes, "Custom loop definition tombstone", cancellationToken);
            ValidateTombstone(tombstone);
            if (!string.Equals(tombstone.LoopId, expectedId, StringComparison.Ordinal))
            {
                throw new FormatException($"Custom loop definition tombstone `{path}` id does not match its filename.");
            }

            tombstones.Add(tombstone);
        }

        return tombstones;
    }

    private async Task<IReadOnlyList<CustomLoopDefinitionMutationOperationRecord>> ReadMutationOperationsAsync(CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_paths.CustomLoopDefinitionOperationsPath))
        {
            return [];
        }

        var operations = new List<CustomLoopDefinitionMutationOperationRecord>();
        foreach (var path in Directory.EnumerateFiles(_paths.CustomLoopDefinitionOperationsPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedId = Path.GetFileNameWithoutExtension(path);
            var operation = await ReadStrictJsonAsync<CustomLoopDefinitionMutationOperationRecord>(_paths.CustomLoopDefinitionOperationsPath, path, MaxDefinitionMutationOperationArtifactBytes, "Custom loop definition mutation operation", cancellationToken);
            ValidateMutationOperation(operation);
            if (!string.Equals(operation.OperationId, expectedId, StringComparison.Ordinal))
            {
                throw new FormatException($"Custom loop definition mutation operation `{path}` id does not match its filename.");
            }

            operations.Add(operation);
        }

        return operations;
    }

    private async Task<T> ReadStrictJsonAsync<T>(string root, string path, long maximumBytes, string artifactName, CancellationToken cancellationToken)
    {
        try
        {
            var utf8Json = await _pathGuard.ReadAllBytesAsync(root, path, maximumBytes, artifactName, cancellationToken);
            RejectDuplicateProperties(utf8Json);
            return JsonSerializer.Deserialize<T>(utf8Json, JsonOptions) ?? throw new FormatException($"{artifactName} `{path}` was empty.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{artifactName} `{path}` contains invalid JSON, unknown fields, or unsupported enum values.", exception);
        }
    }

    private async Task WriteJsonAsync<T>(string root, string path, T artifact, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(artifact, JsonOptions) + Environment.NewLine;
        await _pathGuard.WriteTextAtomicallyAsync(root, path, json, cancellationToken);
    }

    private string GetDefinitionPath(string loopId)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        return _pathGuard.GetFilePath(_paths.CustomLoopDefinitionsPath, safeLoopId + ".json");
    }

    private string GetTombstonePath(string loopId)
    {
        var safeLoopId = CustomLoopArtifactIdentifier.Require(loopId, nameof(loopId));
        return _pathGuard.GetFilePath(_paths.CustomLoopDefinitionTombstonesPath, safeLoopId + ".json");
    }

    private string GetOperationPath(string operationId)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        return _pathGuard.GetFilePath(_paths.CustomLoopDefinitionOperationsPath, safeOperationId + ".json");
    }

    private static string ComputeCreateRequestHash(string roleId)
    {
        var canonicalRequest = "custom-loop-create\0" + roleId.Normalize(NormalizationForm.FormC);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
    }

    private async Task WriteOperationAsync(CustomLoopDefinitionMutationOperationRecord operation, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(_paths.CustomLoopDefinitionOperationsPath, GetOperationPath(operation.OperationId), operation, cancellationToken);
    }

    private static CustomLoopDefinitionMutationOperationRecord CreatePendingOperation(CustomLoopDefinitionMutationRequest mutation, CustomLoopDefinition? originalDefinition = null)
    {
        return new CustomLoopDefinitionMutationOperationRecord(
            DefinitionMutationOperationSchemaVersion,
            mutation.Kind,
            mutation.OperationId,
            mutation.RequestHash,
            mutation.LoopId,
            mutation.RoleId,
            mutation.ExpectedDefinitionVersion,
            mutation.PlannedDefinition,
            mutation.PriorDefinition,
            mutation.RequestedAtUtc,
            mutation.RequestedAtUtc,
            CustomLoopDefinitionMutationState.PendingMutation,
            CustomLoopDefinitionStoreStatus.Unknown,
            null,
            null,
            null,
            OutcomeAuditRecorded: false,
            originalDefinition,
            mutation.RequestedAtUtc);
    }

    private static CustomLoopDefinitionMutationOperationRecord CompleteOperation(CustomLoopDefinitionMutationOperationRecord operation, CustomLoopDefinitionStoreResult result, DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc == default || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Definition mutation outcome timestamps must be non-default UTC values.", nameof(updatedAtUtc));
        }

        return operation with
        {
            UpdatedAtUtc = updatedAtUtc,
            State = CustomLoopDefinitionMutationState.OutcomeCommitted,
            Outcome = result.Status,
            ResultDefinition = result.Definition,
            ResultConflict = result.Conflict,
            ResultTombstone = result.Tombstone,
            OutcomeAuditRecorded = false
        };
    }

    private static bool MutationRequestMatches(CustomLoopDefinitionMutationOperationRecord operation, CustomLoopDefinitionMutationRequest mutation)
    {
        return operation.Kind == mutation.Kind
            && string.Equals(operation.OperationId, mutation.OperationId, StringComparison.Ordinal)
            && string.Equals(operation.RequestHash, mutation.RequestHash, StringComparison.Ordinal)
            && string.Equals(operation.RoleId, mutation.RoleId, StringComparison.Ordinal)
            && operation.ExpectedDefinitionVersion == mutation.ExpectedDefinitionVersion
            && (operation.Kind == CustomLoopDefinitionMutationKind.Create || string.Equals(operation.LoopId, mutation.LoopId, StringComparison.Ordinal));
    }

    private static bool HasCommittedCreateArtifact(WorkspaceState state, CustomLoopDefinitionMutationOperationRecord operation)
    {
        return state.Definitions.Any(definition => string.Equals(definition.Id, operation.LoopId, StringComparison.Ordinal))
            || state.Tombstones.Any(tombstone => string.Equals(tombstone.LoopId, operation.LoopId, StringComparison.Ordinal));
    }

    private static bool HasDefinitionSnapshot(WorkspaceState state, CustomLoopDefinition snapshot)
    {
        return state.Definitions.Any(definition => DefinitionSnapshotsEqual(definition, snapshot));
    }

    private static bool DefinitionSnapshotsEqual(CustomLoopDefinition left, CustomLoopDefinition right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && left.DefinitionVersion == right.DefinitionVersion
            && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal)
            && string.Equals(left.LastMutationOperationId, right.LastMutationOperationId, StringComparison.Ordinal);
    }

    private static void ValidateWorkspaceState(WorkspaceState state, string? allowedPendingOperationId = null)
    {
        var definitions = UniqueBy(state.Definitions, definition => definition.Id, "definition");
        var tombstones = UniqueBy(state.Tombstones, tombstone => tombstone.LoopId, "tombstone");
        var operations = UniqueBy(state.Operations, operation => operation.OperationId, "definition mutation operation");
        var createOperations = state.Operations.Where(operation => operation.Kind == CustomLoopDefinitionMutationKind.Create).ToArray();
        var createOperationsByLoop = UniqueBy(createOperations, operation => operation.LoopId, "Create operation loop identity");

        foreach (var definition in state.Definitions)
        {
            if (tombstones.ContainsKey(definition.Id))
            {
                throw new FormatException($"Custom loop `{definition.Id}` has both a live definition and a deletion tombstone. The persisted state requires review.");
            }

            if (!createOperationsByLoop.ContainsKey(definition.Id))
            {
                throw new FormatException($"Custom loop `{definition.Id}` is missing its durable Create operation record.");
            }
        }

        foreach (var tombstone in state.Tombstones)
        {
            if (!createOperationsByLoop.ContainsKey(tombstone.LoopId))
            {
                throw new FormatException($"Custom loop tombstone `{tombstone.LoopId}` is missing its durable Create operation record.");
            }
        }

        foreach (var operation in operations.Values)
        {
            if (operation.Kind == CustomLoopDefinitionMutationKind.Create)
            {
                definitions.TryGetValue(operation.LoopId, out var current);
                tombstones.TryGetValue(operation.LoopId, out var tombstone);
                if (current is null && tombstone is null
                    && (!string.Equals(operation.OperationId, allowedPendingOperationId, StringComparison.Ordinal) || operation.OutcomeAuditRecorded))
                {
                    throw new FormatException($"Create operation `{operation.OperationId}` has no committed definition or deletion tombstone.");
                }

                var original = operation.OriginalDefinition ?? throw new FormatException($"Create operation `{operation.OperationId}` is missing its original definition snapshot.");
                if (current is not null && (!string.Equals(current.RoleId, operation.RoleId, StringComparison.Ordinal) || current.CreatedAtUtc != original.CreatedAtUtc))
                {
                    throw new FormatException($"Create operation `{operation.OperationId}` does not match the current definition lineage.");
                }

                if (current?.DefinitionVersion == 1 && !string.Equals(current.ContentHash, original.ContentHash, StringComparison.Ordinal))
                {
                    throw new FormatException($"Create operation `{operation.OperationId}` original definition snapshot does not match the current version-one artifact.");
                }
            }

            if ((operation.State == CustomLoopDefinitionMutationState.PendingMutation || !operation.OutcomeAuditRecorded)
                && !string.Equals(operation.OperationId, allowedPendingOperationId, StringComparison.Ordinal))
            {
                throw new FormatException($"Definition mutation operation `{operation.OperationId}` has pending mutation or outcome-audit integrity and requires recovery.");
            }
        }
    }

    private static Dictionary<string, T> UniqueBy<T>(IReadOnlyList<T> values, Func<T, string> keySelector, string artifactName)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                throw new FormatException($"Custom-loop persistence contains duplicate {artifactName} identity `{key}`.");
            }
        }

        return result;
    }

    private static void ValidateMutationRequest(
        CustomLoopDefinitionMutationRequest mutation,
        CustomLoopDefinitionMutationKind expectedKind,
        string loopId,
        string roleId,
        int? expectedDefinitionVersion,
        CustomLoopDefinition? plannedDefinition,
        CustomLoopDefinition? priorDefinition)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Kind != expectedKind || mutation.Kind == CustomLoopDefinitionMutationKind.Unknown)
        {
            throw new ArgumentException("Definition mutation kind does not match the requested store operation.", nameof(mutation));
        }

        CustomLoopArtifactIdentifier.Require(mutation.OperationId, nameof(mutation.OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        CustomLoopArtifactIdentifier.Require(mutation.LoopId, nameof(mutation.LoopId));
        CustomLoopArtifactIdentifier.Require(mutation.RoleId, nameof(mutation.RoleId));
        ValidateSha256(mutation.RequestHash, "Definition mutation request hash");
        if (!string.Equals(mutation.LoopId, loopId, StringComparison.Ordinal)
            || !string.Equals(mutation.RoleId, roleId, StringComparison.Ordinal)
            || mutation.ExpectedDefinitionVersion != expectedDefinitionVersion
            || !OptionalDefinitionMatches(mutation.PlannedDefinition, plannedDefinition)
            || !OptionalDefinitionMatches(mutation.PriorDefinition, priorDefinition))
        {
            throw new ArgumentException("Definition mutation request metadata does not match the requested store operation.", nameof(mutation));
        }

        if (mutation.RequestedAtUtc == default || mutation.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Definition mutation request timestamp must be a non-default UTC value.", nameof(mutation));
        }
    }

    private static bool OptionalDefinitionMatches(CustomLoopDefinition? left, CustomLoopDefinition? right)
    {
        return left is null && right is null || left is not null && right is not null && DefinitionSnapshotsEqual(left, right);
    }

    private static void ValidateMutationOperation(CustomLoopDefinitionMutationOperationRecord operation)
    {
        if (operation.SchemaVersion != DefinitionMutationOperationSchemaVersion)
        {
            throw new FormatException($"Unsupported custom loop definition mutation operation schema version `{operation.SchemaVersion}`.");
        }

        if (operation.Kind == CustomLoopDefinitionMutationKind.Unknown || operation.State == CustomLoopDefinitionMutationState.Unknown)
        {
            throw new FormatException("Custom loop definition mutation operation kind and state must be known.");
        }

        CustomLoopArtifactIdentifier.Require(operation.OperationId, nameof(operation.OperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        CustomLoopArtifactIdentifier.Require(operation.LoopId, nameof(operation.LoopId));
        CustomLoopArtifactIdentifier.Require(operation.RoleId, nameof(operation.RoleId));
        ValidateSha256(operation.RequestHash, "Definition mutation request hash");
        ValidateUtc(operation.RequestedAtUtc, "Definition mutation request timestamp");
        ValidateUtc(operation.UpdatedAtUtc, "Definition mutation update timestamp");
        ValidateUtc(operation.RecordedAtUtc, "Definition mutation recorded timestamp");
        if (operation.RecordedAtUtc != operation.RequestedAtUtc || operation.UpdatedAtUtc < operation.RequestedAtUtc)
        {
            throw new FormatException("Custom loop definition mutation operation timestamps are inconsistent.");
        }

        if (operation.PlannedDefinition is not null)
        {
            ValidateCanonicalDefinition(operation.PlannedDefinition);
        }

        if (operation.PriorDefinition is not null)
        {
            ValidateCanonicalDefinition(operation.PriorDefinition);
        }

        if (operation.Kind == CustomLoopDefinitionMutationKind.Create)
        {
            ValidateCanonicalDefinition(operation.OriginalDefinition);
            if (operation.ExpectedDefinitionVersion is not null
                || operation.PriorDefinition is not null
                || operation.PlannedDefinition is null
                || operation.OriginalDefinition is null
                || operation.PlannedDefinition.DefinitionVersion != 1
                || !DefinitionSnapshotsEqual(operation.PlannedDefinition, operation.OriginalDefinition)
                || !string.Equals(operation.OperationId, operation.OriginalDefinition.LastMutationOperationId, StringComparison.Ordinal)
                || !string.Equals(operation.LoopId, operation.OriginalDefinition.Id, StringComparison.Ordinal)
                || !string.Equals(operation.RoleId, operation.OriginalDefinition.RoleId, StringComparison.Ordinal)
                || !string.Equals(operation.RequestHash, ComputeCreateRequestHash(operation.RoleId), StringComparison.Ordinal)
                || operation.RecordedAtUtc != operation.OriginalDefinition.CreatedAtUtc)
            {
                throw new FormatException("Custom loop Create operation metadata does not match its original canonical definition or request.");
            }
        }
        else if (operation.Kind == CustomLoopDefinitionMutationKind.Update)
        {
            if (operation.ExpectedDefinitionVersion is null || operation.PlannedDefinition is null || operation.OriginalDefinition is not null
                || operation.PlannedDefinition.DefinitionVersion != checked(operation.ExpectedDefinitionVersion.Value + 1)
                || !string.Equals(operation.LoopId, operation.PlannedDefinition.Id, StringComparison.Ordinal)
                || !string.Equals(operation.RoleId, operation.PlannedDefinition.RoleId, StringComparison.Ordinal)
                || !string.Equals(operation.OperationId, operation.PlannedDefinition.LastMutationOperationId, StringComparison.Ordinal))
            {
                throw new FormatException("Custom loop Update operation metadata does not match its planned canonical definition.");
            }
        }
        else if (operation.Kind == CustomLoopDefinitionMutationKind.Delete)
        {
            if (operation.ExpectedDefinitionVersion is null || operation.PlannedDefinition is not null || operation.OriginalDefinition is not null
                || operation.PriorDefinition is not null && (!string.Equals(operation.LoopId, operation.PriorDefinition.Id, StringComparison.Ordinal) || !string.Equals(operation.RoleId, operation.PriorDefinition.RoleId, StringComparison.Ordinal)))
            {
                throw new FormatException("Custom loop Delete operation metadata is inconsistent.");
            }
        }

        if (operation.State == CustomLoopDefinitionMutationState.PendingMutation)
        {
            if (operation.Outcome != CustomLoopDefinitionStoreStatus.Unknown || operation.ResultDefinition is not null || operation.ResultConflict is not null || operation.ResultTombstone is not null || operation.OutcomeAuditRecorded)
            {
                throw new FormatException("A pending custom loop definition mutation operation cannot contain an outcome or completed audit marker.");
            }

            return;
        }

        if (operation.Outcome is CustomLoopDefinitionStoreStatus.Unknown or CustomLoopDefinitionStoreStatus.OperationConflict)
        {
            throw new FormatException("A committed custom loop definition mutation operation contains an unsupported outcome.");
        }

        var outcomeMatchesKind = operation.Kind switch
        {
            CustomLoopDefinitionMutationKind.Create => operation.Outcome == CustomLoopDefinitionStoreStatus.Created,
            CustomLoopDefinitionMutationKind.Update => operation.Outcome is CustomLoopDefinitionStoreStatus.Updated or CustomLoopDefinitionStoreStatus.Conflict or CustomLoopDefinitionStoreStatus.NotFound,
            CustomLoopDefinitionMutationKind.Delete => operation.Outcome is CustomLoopDefinitionStoreStatus.Deleted or CustomLoopDefinitionStoreStatus.Conflict or CustomLoopDefinitionStoreStatus.NotFound or CustomLoopDefinitionStoreStatus.AlreadyDeleted,
            _ => false
        };
        if (!outcomeMatchesKind)
        {
            throw new FormatException("A committed custom loop definition mutation operation outcome does not match its mutation kind.");
        }

        if (operation.Outcome is CustomLoopDefinitionStoreStatus.Created or CustomLoopDefinitionStoreStatus.Updated or CustomLoopDefinitionStoreStatus.Deleted && operation.ResultDefinition is null)
        {
            throw new FormatException("A successful custom loop definition mutation operation is missing its result definition snapshot.");
        }

        if (operation.Outcome == CustomLoopDefinitionStoreStatus.Conflict && operation.ResultConflict is null)
        {
            throw new FormatException("A conflicted custom loop definition mutation operation is missing conflict metadata.");
        }
    }

    private static void ValidateSha256(string value, string fieldName)
    {
        if (value.Length != CustomLoopLimits.Sha256HexCharacters || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new FormatException($"{fieldName} is invalid.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string fieldName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new FormatException($"{fieldName} must be a non-default UTC value.");
        }
    }

    private async Task<CustomLoopDefinitionStoreResult> ExecuteCreateAsync(WorkspaceState state, CustomLoopDefinition definition, CancellationToken cancellationToken)
    {
        var tombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, definition.Id, StringComparison.Ordinal));
        if (tombstone is not null)
        {
            return CustomLoopDefinitionStoreResult.TombstoneConflict(tombstone, expectedDefinitionVersion: 0);
        }

        var current = state.Definitions.SingleOrDefault(candidate => string.Equals(candidate.Id, definition.Id, StringComparison.Ordinal));
        if (current is not null)
        {
            return CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion: 0);
        }

        if (state.Definitions.Count >= CustomLoopLimits.MaxDefinitionsPerWorkspace)
        {
            return CustomLoopDefinitionStoreResult.LimitExceeded();
        }

        await WriteJsonAsync(_paths.CustomLoopDefinitionsPath, GetDefinitionPath(definition.Id), definition, cancellationToken);
        return CustomLoopDefinitionStoreResult.Created(definition, CustomLoopOperationIntegrity.PendingOutcomeAudit);
    }

    private async Task<CustomLoopDefinitionStoreResult> ExecuteUpdateAsync(WorkspaceState state, CustomLoopDefinition definition, int expectedDefinitionVersion, CancellationToken cancellationToken)
    {
        var current = state.Definitions.SingleOrDefault(candidate => string.Equals(candidate.Id, definition.Id, StringComparison.Ordinal));
        if (current is null)
        {
            var tombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, definition.Id, StringComparison.Ordinal));
            return tombstone is null
                ? CustomLoopDefinitionStoreResult.NotFound()
                : CustomLoopDefinitionStoreResult.TombstoneConflict(tombstone, expectedDefinitionVersion);
        }

        if (current.DefinitionVersion != expectedDefinitionVersion)
        {
            return CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion);
        }

        await WriteJsonAsync(_paths.CustomLoopDefinitionsPath, GetDefinitionPath(definition.Id), definition, cancellationToken);
        return CustomLoopDefinitionStoreResult.Updated(definition);
    }

    private async Task<CustomLoopDefinitionStoreResult> ExecuteDeleteAsync(WorkspaceState state, string loopId, int expectedDefinitionVersion, string operationId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken)
    {
        var current = state.Definitions.SingleOrDefault(candidate => string.Equals(candidate.Id, loopId, StringComparison.Ordinal));
        if (current is null)
        {
            var existingTombstone = state.Tombstones.SingleOrDefault(candidate => string.Equals(candidate.LoopId, loopId, StringComparison.Ordinal));
            if (existingTombstone is null)
            {
                return CustomLoopDefinitionStoreResult.NotFound();
            }

            return existingTombstone.LastDefinitionVersion == expectedDefinitionVersion && string.Equals(existingTombstone.MutationOperationId, operationId, StringComparison.Ordinal)
                ? CustomLoopDefinitionStoreResult.AlreadyDeleted(existingTombstone)
                : CustomLoopDefinitionStoreResult.TombstoneConflict(existingTombstone, expectedDefinitionVersion);
        }

        if (current.DefinitionVersion != expectedDefinitionVersion)
        {
            return CustomLoopDefinitionStoreResult.VersionConflict(current, expectedDefinitionVersion);
        }

        var tombstone = new CustomLoopDefinitionTombstone(
            CustomLoopDefinitionTombstone.CurrentSchemaVersion,
            loopId,
            current.DefinitionVersion,
            current.ContentHash,
            operationId,
            deletedAtUtc);
        await WriteJsonAsync(_paths.CustomLoopDefinitionTombstonesPath, GetTombstonePath(loopId), tombstone, cancellationToken);
        _pathGuard.DeleteFile(_paths.CustomLoopDefinitionsPath, GetDefinitionPath(loopId));
        return CustomLoopDefinitionStoreResult.Deleted(current, tombstone);
    }

    private static void ValidateCanonicalDefinition(CustomLoopDefinition? definition)
    {
        var validation = CustomLoopDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            var details = string.Join(" ", validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new FormatException($"Custom loop definition is invalid. {details}");
        }
    }

    private static void ValidateTombstone(CustomLoopDefinitionTombstone tombstone)
    {
        if (tombstone.SchemaVersion != CustomLoopDefinitionTombstone.CurrentSchemaVersion)
        {
            throw new FormatException($"Unsupported custom loop definition tombstone schema version `{tombstone.SchemaVersion}`.");
        }

        CustomLoopArtifactIdentifier.Require(tombstone.LoopId, nameof(tombstone.LoopId));
        ValidateExpectedVersion(tombstone.LastDefinitionVersion);
        if (tombstone.LastContentHash.Length != CustomLoopLimits.Sha256HexCharacters || tombstone.LastContentHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new FormatException("Custom loop definition tombstone content hash is invalid.");
        }

        CustomLoopArtifactIdentifier.Require(tombstone.MutationOperationId, nameof(tombstone.MutationOperationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (tombstone.DeletedAtUtc == default || tombstone.DeletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("Custom loop definition tombstone deletion timestamp must be a non-default UTC value.");
        }
    }

    private static void ValidateExpectedVersion(int expectedDefinitionVersion)
    {
        if (expectedDefinitionVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDefinitionVersion), expectedDefinitionVersion, "Expected definition version must be at least 1.");
        }
    }

    private static void RejectDuplicateProperties(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        RejectDuplicateProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            names.Clear();
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException($"JSON object `{path}` contains duplicate property `{property.Name}`.");
                }

                RejectDuplicateProperties(property.Value, path + "." + property.Name, new HashSet<string>(StringComparer.Ordinal));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", new HashSet<string>(StringComparer.Ordinal));
                index++;
            }
        }
    }

    private sealed record CustomLoopDefinitionMutationOperationRecord(
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
        bool OutcomeAuditRecorded,
        CustomLoopDefinition? OriginalDefinition,
        DateTimeOffset RecordedAtUtc)
    {
        public CustomLoopDefinitionMutationOperation ToPublic()
        {
            return new CustomLoopDefinitionMutationOperation(
                SchemaVersion,
                Kind,
                OperationId,
                RequestHash,
                LoopId,
                RoleId,
                ExpectedDefinitionVersion,
                PlannedDefinition,
                PriorDefinition,
                RequestedAtUtc,
                UpdatedAtUtc,
                State,
                Outcome,
                ResultDefinition,
                ResultConflict,
                ResultTombstone,
                OutcomeAuditRecorded);
        }
    }

    private sealed record WorkspaceState(
        IReadOnlyList<CustomLoopDefinition> Definitions,
        IReadOnlyList<CustomLoopDefinitionTombstone> Tombstones,
        IReadOnlyList<CustomLoopDefinitionMutationOperationRecord> Operations);
}
