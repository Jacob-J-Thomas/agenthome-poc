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
        MaxDepth = 32,
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
                return SameRequest(existing, operation)
                    ? new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing)
                    : new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing);
            }

            await WriteAsync(operation, cancellationToken);
            return new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Created, operation);
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
        if (!string.Equals(operation!.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new FormatException($"Custom-loop control operation filename `{operationId}` does not match embedded id `{operation.OperationId}`.");
        }

        return operation;
    }

    private async Task WriteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(operation, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumArtifactBytes)
        {
            throw new ArgumentException($"Custom-loop control operation exceeds {MaximumArtifactBytes} UTF-8 bytes.", nameof(operation));
        }

        var path = _pathGuard.GetFilePath(_root, operation.OperationId + ".json");
        await _pathGuard.WriteTextAtomicallyAsync(_root, path, json, cancellationToken);
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
    }
}
