using EmbodySense.Core.Application.Loops.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class CustomLoopControlOperationStore : ICustomLoopControlOperationStore
{
    private const long MaximumArtifactBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = CustomLoopJsonDepthPolicy.ShallowReceiptMaximumDepth,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _root;
    private readonly CustomLoopArtifactPathGuard _pathGuard;
    private readonly SemaphoreSlim _processGate;

    public CustomLoopControlOperationStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.GetFullPath(paths.CustomLoopControlOperationsPath);
        _pathGuard = new CustomLoopArtifactPathGuard(paths.RootPath);
        _processGate = ProcessGates.GetOrAdd(_root, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: true);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            if (existing is not null)
            {
                if (!SameRequest(existing, operation))
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
                }

                if (existing.State == CustomLoopControlOperationState.Complete)
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing);
                }

                var replayLease = TryAcquireOperationOwnership(operation.OperationId);
                if (replayLease is null)
                {
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.OwnershipUnproven, existing);
                }

                try
                {
                    var recovered = WithOwnership(existing, replayLease, "The orphaned custom-loop control operation was claimed by a new bounded execution owner.");
                    await WriteAsync(recovered, cancellationToken);
                    return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, recovered, replayLease);
                }
                catch
                {
                    replayLease.Dispose();
                    throw;
                }
            }

            var lease = TryAcquireOperationOwnership(operation.OperationId) ?? throw new InvalidOperationException("The new custom-loop control operation could not acquire its bounded execution ownership.");
            try
            {
                var owned = WithOwnership(operation, lease, operation.Detail);
                await WriteAsync(owned, cancellationToken);
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Created, owned, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var safeOperationId = CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadIfExistsAsync(safeOperationId, cancellationToken);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: false);
        if (operation.State != CustomLoopControlOperationState.Complete)
        {
            throw new ArgumentException("Completed control operation must have Complete state.", nameof(operation));
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            if (existing is null)
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.NotFound, null);
            }

            if (!SameRequest(existing, operation))
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            if (existing.OwnerGenerationId is not null && !string.Equals(existing.OwnerGenerationId, operation.OwnerGenerationId, StringComparison.Ordinal))
            {
                return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            if (existing.State == CustomLoopControlOperationState.Complete)
            {
                return existing == operation
                    ? new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing)
                    : new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            await WriteAsync(operation, cancellationToken);
            return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Completed, operation);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<CustomLoopControlOperation?> ReadIfExistsAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return null;
        }

        var path = _pathGuard.GetFilePath(_root, operationId + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, MaximumArtifactBytes, "Custom-loop control operation", cancellationToken);
        CustomLoopJsonDepthPolicy.ValidatePersistedJsonDepth(bytes, JsonOptions.MaxDepth, "Custom-loop control operation", path);
        CustomLoopControlOperation? operation;
        try
        {
            operation = JsonSerializer.Deserialize<CustomLoopControlOperation>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Custom-loop control operation `{path}` is invalid JSON.", exception);
        }

        Validate(operation, requirePending: operation?.State == CustomLoopControlOperationState.Pending);
        if (operation!.OwnerGenerationId is null || operation.OwnerProcessId is null || operation.OwnerAcquiredAtUtc is null)
        {
            throw new FormatException("Persisted custom-loop control operation is missing ownership metadata.");
        }

        if (!string.Equals(operation!.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new FormatException($"Custom-loop control operation filename `{operationId}` does not match embedded id `{operation.OperationId}`.");
        }

        return operation;
    }

    private async Task WriteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken)
    {
        var path = _pathGuard.GetFilePath(_root, operation.OperationId + ".json");
        string json;
        try
        {
            json = JsonSerializer.Serialize(operation, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw CustomLoopJsonDepthPolicy.SerializationDepthException("Custom-loop control operation", JsonOptions.MaxDepth, exception, path);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumArtifactBytes)
        {
            throw new ArgumentException($"Custom-loop control operation exceeds {MaximumArtifactBytes} UTF-8 bytes.", nameof(operation));
        }

        await _pathGuard.WriteTextAtomicallyAsync(_root, path, json, cancellationToken);
    }

    private ControlOperationLease? TryAcquireOperationOwnership(string operationId)
    {
        var path = _pathGuard.GetFilePath(_root, $".{operationId}.owner.lock");
        FileStream? ownership = null;
        try
        {
            ownership = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.WriteThrough);
            _pathGuard.GetFilePath(_root, $".{operationId}.owner.lock");
            if (!CustomLoopCrossProcessFileLock.TryAcquire(ownership))
            {
                ownership.Dispose();
                return null;
            }

            return new ControlOperationLease(operationId, "control-owner-" + Guid.NewGuid().ToString("N"), ownership);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ownership?.Dispose();
            return null;
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }
    }

    private static CustomLoopControlOperation WithOwnership(CustomLoopControlOperation operation, ControlOperationLease lease, string detail)
    {
        var acquiredAtUtc = DateTimeOffset.UtcNow.ToUniversalTime();
        if (acquiredAtUtc < operation.CreatedAtUtc)
        {
            acquiredAtUtc = operation.CreatedAtUtc;
        }

        var updatedAtUtc = acquiredAtUtc > operation.UpdatedAtUtc ? acquiredAtUtc : operation.UpdatedAtUtc;
        return operation with
        {
            OwnerGenerationId = lease.OwnerGenerationId,
            OwnerProcessId = Environment.ProcessId,
            OwnerAcquiredAtUtc = acquiredAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            Detail = detail
        };
    }

    private static bool SameRequest(CustomLoopControlOperation left, CustomLoopControlOperation right)
    {
        return string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal)
            && left.Kind == right.Kind
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && left.ExpectedLifecycleVersion == right.ExpectedLifecycleVersion
            && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal);
    }

    private static void Validate(CustomLoopControlOperation? operation, bool requirePending)
    {
        if (operation is null)
        {
            throw new FormatException("Custom-loop control operation cannot be null.");
        }

        if (operation.SchemaVersion != CustomLoopControlOperation.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(operation.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            || !CustomLoopArtifactIdentifier.IsValid(operation.RunId)
            || operation.ExpectedLifecycleVersion < 1
            || !Enum.IsDefined(operation.Kind)
            || operation.Kind == CustomLoopControlKind.Unknown
            || !Enum.IsDefined(operation.State)
            || operation.State == CustomLoopControlOperationState.Unknown
            || !Enum.IsDefined(operation.Outcome)
            || string.IsNullOrWhiteSpace(operation.Actor)
            || operation.Actor.Length > CustomLoopLimits.MaxTraceReferenceCharacters
            || !operation.Actor.IsNormalized(NormalizationForm.FormC)
            || operation.Actor.Any(character => char.IsControl(character) || char.IsSurrogate(character))
            || operation.RequestHash is not { Length: CustomLoopLimits.Sha256HexCharacters }
            || !operation.RequestHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !CustomLoopControlRequestHash.Matches(operation)
            || operation.CreatedAtUtc == default
            || operation.CreatedAtUtc.Offset != TimeSpan.Zero
            || operation.UpdatedAtUtc == default
            || operation.UpdatedAtUtc.Offset != TimeSpan.Zero
            || operation.UpdatedAtUtc < operation.CreatedAtUtc
            || string.IsNullOrWhiteSpace(operation.Detail)
            || operation.Detail.Length > CustomLoopLimits.MaxRunDetailCharacters)
        {
            throw new FormatException("Custom-loop control operation failed canonical validation.");
        }

        if (requirePending && (operation.State != CustomLoopControlOperationState.Pending || operation.Outcome != CustomLoopControlStatus.Unknown || operation.ResultLifecycleVersion is not null || operation.ResultRunStatus is not null || operation.OutcomeAuditRecorded))
        {
            throw new FormatException("Pending custom-loop control operation contains completed outcome fields.");
        }

        var hasLifecycleVersion = operation.ResultLifecycleVersion is not null;
        var hasRunStatus = operation.ResultRunStatus is not null;
        var allowsMissingRun = operation.Outcome is CustomLoopControlStatus.NotFound or CustomLoopControlStatus.Failed;
        if (operation.State == CustomLoopControlOperationState.Complete && (operation.Outcome == CustomLoopControlStatus.Unknown || hasLifecycleVersion != hasRunStatus || !hasLifecycleVersion && !allowsMissingRun))
        {
            throw new FormatException("Completed custom-loop control operation is missing its durable outcome.");
        }

        var hasAnyOwner = operation.OwnerGenerationId is not null || operation.OwnerProcessId is not null || operation.OwnerAcquiredAtUtc is not null;
        var hasCompleteOwner = operation.OwnerGenerationId is not null && operation.OwnerProcessId is not null && operation.OwnerAcquiredAtUtc is not null;
        if (hasAnyOwner && (!hasCompleteOwner
            || !CustomLoopArtifactIdentifier.IsValid(operation.OwnerGenerationId!)
            || operation.OwnerProcessId <= 0
            || operation.OwnerAcquiredAtUtc!.Value.Offset != TimeSpan.Zero
            || operation.OwnerAcquiredAtUtc.Value < operation.CreatedAtUtc
            || operation.OwnerAcquiredAtUtc.Value > operation.UpdatedAtUtc))
        {
            throw new FormatException("Custom-loop control operation ownership metadata is invalid.");
        }
    }

    private sealed class ControlOperationLease(string operationId, string ownerGenerationId, FileStream ownership) : ICustomLoopControlOperationLease
    {
        private int _disposed;

        public string OperationId { get; } = operationId;

        public string OwnerGenerationId { get; } = ownerGenerationId;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ownership.Dispose();
            }
        }
    }
}
