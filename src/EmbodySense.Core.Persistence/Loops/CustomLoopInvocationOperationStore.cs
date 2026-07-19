using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class CustomLoopInvocationOperationStore : ICustomLoopInvocationOperationStore
{
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

    public CustomLoopInvocationOperationStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = Path.GetFullPath(paths.CustomLoopInvocationOperationsPath);
        _pathGuard = new CustomLoopArtifactPathGuard(paths.RootPath);
        _processGate = ProcessGates.GetOrAdd(_root, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default)
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
                    ? new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Replayed, existing)
                    : new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            var json = SerializeBounded(operation);
            EnsureCapacityForNewOperation(Encoding.UTF8.GetByteCount(json));
            await WriteAsync(operation, json, cancellationToken);
            return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Created, operation);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
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

    public async Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default)
    {
        Validate(operation, requirePending: false);
        if (operation.State != CustomLoopInvocationOperationState.Complete)
        {
            throw new ArgumentException("Completed invocation operation must have Complete state.", nameof(operation));
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            using var workspaceLock = _pathGuard.AcquireExclusiveMutationLock(_root);
            var existing = await ReadIfExistsAsync(operation.OperationId, cancellationToken);
            if (existing is null)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.NotFound, null);
            }

            if (!SameRequest(existing, operation))
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            var normalized = operation with { CreatedAtUtc = existing.CreatedAtUtc };
            if (existing.State == CustomLoopInvocationOperationState.Complete)
            {
                return existing == normalized
                    ? new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Replayed, existing)
                    : new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            if (operation.UpdatedAtUtc < existing.UpdatedAtUtc)
            {
                return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Conflict, existing);
            }

            Validate(normalized, requirePending: false);
            var json = SerializeBounded(normalized);
            EnsureCapacity(Encoding.UTF8.GetByteCount(json), normalized.OperationId);
            await WriteAsync(normalized, json, cancellationToken);
            return new CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus.Completed, normalized);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<CustomLoopInvocationOperation?> ReadIfExistsAsync(string operationId, CancellationToken cancellationToken)
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

        var bytes = await _pathGuard.ReadAllBytesAsync(_root, path, CustomLoopLimits.MaxInvocationOperationUtf8Bytes, "Custom-loop invocation operation", cancellationToken);
        CustomLoopInvocationOperation? operation;
        try
        {
            operation = JsonSerializer.Deserialize<CustomLoopInvocationOperation>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Custom-loop invocation operation `{path}` is invalid JSON.", exception);
        }

        Validate(operation, requirePending: operation?.State == CustomLoopInvocationOperationState.Pending);
        if (!string.Equals(operation!.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new FormatException($"Custom-loop invocation operation filename `{operationId}` does not match embedded id `{operation.OperationId}`.");
        }

        return operation;
    }

    private async Task WriteAsync(CustomLoopInvocationOperation operation, string json, CancellationToken cancellationToken)
    {
        var path = _pathGuard.GetFilePath(_root, operation.OperationId + ".json");
        await _pathGuard.WriteTextAtomicallyAsync(_root, path, json, cancellationToken);
    }

    private static string SerializeBounded(CustomLoopInvocationOperation operation)
    {
        var json = JsonSerializer.Serialize(operation, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > CustomLoopLimits.MaxInvocationOperationUtf8Bytes)
        {
            throw new ArgumentException($"Custom-loop invocation operation exceeds {CustomLoopLimits.MaxInvocationOperationUtf8Bytes} UTF-8 bytes.", nameof(operation));
        }

        return json;
    }

    private void EnsureCapacityForNewOperation(long newArtifactBytes) => EnsureCapacity(newArtifactBytes, replacingOperationId: null);

    private void EnsureCapacity(long newArtifactBytes, string? replacingOperationId)
    {
        var paths = EnumerateOperationPaths();
        if (replacingOperationId is null && paths.Count >= CustomLoopLimits.MaxInvocationOperationReceiptsPerWorkspace)
        {
            throw new InvalidOperationException($"Custom-loop invocation receipt count reached the workspace limit of {CustomLoopLimits.MaxInvocationOperationReceiptsPerWorkspace}.");
        }

        long accountedBytes = 0;
        foreach (var path in paths)
        {
            if (replacingOperationId is not null && string.Equals(Path.GetFileNameWithoutExtension(path), replacingOperationId, StringComparison.Ordinal))
            {
                continue;
            }

            accountedBytes = checked(accountedBytes + new FileInfo(path).Length);
        }

        if (accountedBytes > CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes - newArtifactBytes)
        {
            throw new InvalidOperationException($"Custom-loop invocation receipts reached the workspace limit of {CustomLoopLimits.MaxInvocationOperationWorkspaceUtf8Bytes} UTF-8 bytes.");
        }
    }

    private IReadOnlyList<string> EnumerateOperationPaths()
    {
        if (!_pathGuard.DirectoryExists(_root))
        {
            return [];
        }

        return Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => _pathGuard.GetFilePath(_root, Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool SameRequest(CustomLoopInvocationOperation left, CustomLoopInvocationOperation right)
    {
        return string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal)
            && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && string.Equals(left.LoopId, right.LoopId, StringComparison.Ordinal)
            && left.ExpectedDefinitionVersion == right.ExpectedDefinitionVersion
            && string.Equals(left.ExpectedDefinitionHash, right.ExpectedDefinitionHash, StringComparison.Ordinal)
            && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal)
            && string.Equals(left.Surface, right.Surface, StringComparison.Ordinal)
            && string.Equals(left.CurrentRoleId, right.CurrentRoleId, StringComparison.Ordinal)
            && string.Equals(left.InvocationPromptHash, right.InvocationPromptHash, StringComparison.Ordinal)
            && string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
            && string.Equals(left.Model, right.Model, StringComparison.Ordinal);
    }

    private static void Validate(CustomLoopInvocationOperation? operation, bool requirePending)
    {
        if (operation is null)
        {
            throw new FormatException("Custom-loop invocation operation cannot be null.");
        }

        var valid = operation.SchemaVersion == CustomLoopInvocationOperation.CurrentSchemaVersion
            && CustomLoopArtifactIdentifier.IsValid(operation.OperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            && CustomLoopArtifactIdentifier.IsValid(operation.LoopId)
            && operation.ExpectedDefinitionVersion >= 1
            && IsHash(operation.ExpectedDefinitionHash)
            && IsBoundedText(operation.Actor, CustomLoopLimits.MaxTraceReferenceCharacters)
            && CustomLoopArtifactIdentifier.IsValid(operation.Surface)
            && CustomLoopArtifactIdentifier.IsValid(operation.CurrentRoleId)
            && IsHash(operation.InvocationPromptHash)
            && IsBoundedText(operation.Provider, CustomLoopLimits.MaxTraceReferenceCharacters)
            && (operation.Model is null || IsBoundedText(operation.Model, CustomLoopLimits.MaxTraceReferenceCharacters))
            && IsHash(operation.RequestHash)
            && CustomLoopInvocationRequestHash.Matches(operation)
            && operation.CreatedAtUtc != default
            && operation.CreatedAtUtc.Offset == TimeSpan.Zero
            && operation.UpdatedAtUtc != default
            && operation.UpdatedAtUtc.Offset == TimeSpan.Zero
            && operation.UpdatedAtUtc >= operation.CreatedAtUtc
            && Enum.IsDefined(operation.State)
            && operation.State != CustomLoopInvocationOperationState.Unknown
            && Enum.IsDefined(operation.Outcome)
            && operation.Detail.Length is > 0 and <= CustomLoopLimits.MaxRunDetailCharacters;
        if (!valid)
        {
            throw new FormatException("Custom-loop invocation operation failed canonical validation.");
        }

        if (requirePending && (operation.State != CustomLoopInvocationOperationState.Pending
            || operation.Outcome != CustomLoopInvocationOutcome.Unknown
            || operation.AdmissionStatus.Length != 0
            || operation.RunId is not null))
        {
            throw new FormatException("Pending custom-loop invocation operation contains completed outcome fields.");
        }

        if (operation.State == CustomLoopInvocationOperationState.Complete && !ValidCompletedOutcome(operation))
        {
            throw new FormatException("Completed custom-loop invocation operation is missing its durable outcome.");
        }
    }

    private static bool ValidCompletedOutcome(CustomLoopInvocationOperation operation)
    {
        if (operation.Outcome == CustomLoopInvocationOutcome.Unknown || !IsBoundedText(operation.AdmissionStatus, 120))
        {
            return false;
        }

        return operation.Outcome switch
        {
            CustomLoopInvocationOutcome.WorkspaceExecutionBusy => operation.RunId is null && string.Equals(operation.AdmissionStatus, "WorkspaceExecutionBusy", StringComparison.Ordinal),
            CustomLoopInvocationOutcome.Admitted => CustomLoopArtifactIdentifier.IsValid(operation.RunId) && string.Equals(operation.AdmissionStatus, "Admitted", StringComparison.Ordinal),
            CustomLoopInvocationOutcome.Rejected => operation.RunId is null || CustomLoopArtifactIdentifier.IsValid(operation.RunId),
            _ => false
        };
    }

    private static bool IsHash(string? value)
    {
        return value is { Length: CustomLoopLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsBoundedText(string? value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && value.IsNormalized(NormalizationForm.FormC)
            && !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));
    }
}
