using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Persists strict version-1 default-conversation turn protocol artifacts with optimistic append-only updates.
/// </summary>
/// <remarks>
/// Each lifecycle version is atomically published through a same-directory replacement. A process-local gate and exclusive
/// workspace active-set lease serialize cooperating operations across processes. Unsupported schemas and altered transition history fail closed.
/// </remarks>
public sealed class DefaultConversationTurnStore : IDefaultConversationTurnStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    // TODO(#268): Reject unmapped root and nested members instead of accepting an implicit compatibility shape. https://github.com/Jacob-J-Thomas/agenthome-poc/issues/268
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
    };
    private static readonly TimeSpan _leaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private const int MaximumActiveArtifacts = 128;
    private const int MaximumActiveDirectoryEntries = MaximumActiveArtifacts + 1;
    private const long MaximumActiveArtifactBytes = 1024 * 1024;
    private const long MaximumActiveAggregateBytes = 8 * 1024 * 1024;
    private readonly WorkspacePaths _paths;
    private readonly IDefaultConversationTurnStoreCoordination? _coordination;

    /// <summary>Initializes the store for one workspace.</summary>
    /// <param name="paths">The workspace paths that own the active and historical turn artifacts.</param>
    /// <param name="coordination">An optional observer invoked while an operation owns the active-turn-set lease.</param>
    public DefaultConversationTurnStore(WorkspacePaths paths, IDefaultConversationTurnStoreCoordination? coordination = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _coordination = coordination;
    }

    /// <inheritdoc />
    public async Task<DefaultConversationTurnStoreResult> CreateAsync(DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
    {
        ValidateRecord(record);
        var serializedRecord = SerializeRecord(record);
        EnsureSupportedLayout();
        var path = GetActivePath(record.TurnId);
        EnsureActiveDirectory();
        var activeSetPath = GetActiveSetPath();
        var activeSetGate = GetGate(activeSetPath);
        await activeSetGate.WaitAsync(cancellationToken);
        try
        {
            await using var activeSetLease = await AcquireLeaseAsync(activeSetPath, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.Create, cancellationToken);
            var activeBytes = await ArchiveResolvedActiveAsync(cancellationToken);
            var existingRead = await ReadOptionalAsync(path, record.TurnId, MaximumActiveArtifactBytes, cancellationToken);
            var historicalRead = await ReadOptionalAsync(GetHistoryPath(record.TurnId), record.TurnId, MaximumActiveArtifactBytes, cancellationToken);
            var existing = existingRead?.Record;
            var historical = historicalRead?.Record;
            RequireNoDuplicate(existing, historical, record.TurnId);
            if (existing is not null)
            {
                var status = RecordsEqual(existing, record) ? DefaultConversationTurnStoreStatus.Replay : DefaultConversationTurnStoreStatus.Conflict;
                return new DefaultConversationTurnStoreResult(status, existing);
            }

            if (historical is not null)
            {
                var status = RecordsEqual(historical, record) ? DefaultConversationTurnStoreStatus.Replay : DefaultConversationTurnStoreStatus.Conflict;
                return new DefaultConversationTurnStoreResult(status, historical);
            }

            EnsureActiveCapacity();
            EnsureActiveAggregateBytes(activeBytes, serializedRecord.LongLength);
            await WriteAsync(path, serializedRecord, cancellationToken);
            return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Created, record);
        }
        finally
        {
            activeSetGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DefaultConversationTurnStoreResult> UpdateAsync(DefaultConversationTurnRecord record, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        ValidateRecord(record);
        var serializedRecord = SerializeRecord(record);
        if (expectedLifecycleVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleVersion));
        }

        EnsureSupportedLayout();
        EnsureActiveDirectory();
        var path = GetActivePath(record.TurnId);
        var activeSetPath = GetActiveSetPath();
        var activeSetGate = GetGate(activeSetPath);
        await activeSetGate.WaitAsync(cancellationToken);
        try
        {
            await using var activeSetLease = await AcquireLeaseAsync(activeSetPath, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.Update, cancellationToken);
            var activeBytes = await MeasureActiveAggregateBytesAsync(cancellationToken);
            var existingRead = await ReadOptionalAsync(path, record.TurnId, MaximumActiveArtifactBytes, cancellationToken);
            var historicalRead = await ReadOptionalAsync(GetHistoryPath(record.TurnId), record.TurnId, MaximumActiveArtifactBytes, cancellationToken);
            var existing = existingRead?.Record;
            var historical = historicalRead?.Record;
            RequireNoDuplicate(existing, historical, record.TurnId);
            if (existing is null)
            {
                return historical is not null && RecordsEqual(historical, record) ? new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Replay, historical) : new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Conflict, historical);
            }

            if (RecordsEqual(existing, record))
            {
                return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Replay, existing);
            }

            if (existing.LifecycleVersion != expectedLifecycleVersion
                || record.LifecycleVersion != expectedLifecycleVersion + 1
                || !ImmutableIdentityMatches(existing, record)
                || !EvidenceAdvances(existing, record)
                || !TransitionHistoryExtends(existing, record))
            {
                return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Conflict, existing);
            }

            EnsureActiveAggregateBytes(activeBytes - existingRead!.Value.ByteCount, serializedRecord.LongLength);
            await WriteAsync(path, serializedRecord, cancellationToken);
            if (ShouldArchive(record))
            {
                MoveToHistory(path, record.TurnId);
            }
            return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Updated, record);
        }
        finally
        {
            activeSetGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DefaultConversationTurnRecord?> LoadAsync(string turnId, CancellationToken cancellationToken = default)
    {
        EnsureSupportedLayout();
        EnsureActiveDirectory();
        var path = GetActivePath(turnId);
        var activeSetPath = GetActiveSetPath();
        var activeSetGate = GetGate(activeSetPath);
        await activeSetGate.WaitAsync(cancellationToken);
        try
        {
            await using var activeSetLease = await AcquireLeaseAsync(activeSetPath, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.Load, cancellationToken);
            var active = await ReadOptionalAsync(path, turnId, MaximumActiveArtifactBytes, cancellationToken);
            var history = await ReadOptionalAsync(GetHistoryPath(turnId), turnId, MaximumActiveArtifactBytes, cancellationToken);
            RequireNoDuplicate(active?.Record, history?.Record, turnId);
            return active?.Record ?? history?.Record;
        }
        finally
        {
            activeSetGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default)
    {
        return await ListAsync(record => record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListNeedsReviewAsync(CancellationToken cancellationToken = default)
    {
        return await ListAsync(record => record.Checkpoint == DefaultConversationTurnCheckpoint.Terminal && record.Run.Status == LoopRunStatus.NeedsReview && record.ReviewResolution is null, cancellationToken);
    }

    private string GetActivePath(string turnId)
    {
        return Path.Combine(_paths.DefaultConversationActiveTurnsPath, LoopArtifactPaths.ValidateArtifactId(turnId) + ".json");
    }

    private string GetActiveSetPath() => Path.Combine(_paths.DefaultConversationActiveTurnsPath, ".active-set");

    private string GetHistoryPath(string turnId) => Path.Combine(_paths.DefaultConversationTurnHistoryPath, LoopArtifactPaths.ValidateArtifactId(turnId) + ".json");

    private static SemaphoreSlim GetGate(string path)
    {
        return _gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
    }

    private static async Task<FileStream> AcquireLeaseAsync(string path, CancellationToken cancellationToken)
    {
        var leasePath = path + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = DefaultConversationTurnNativeFileSystem.TryAcquireExclusiveLease(leasePath);
            if (lease is not null)
            {
                return lease;
            }

            await Task.Delay(_leaseRetryDelay, cancellationToken);
        }
    }

    private Task CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken)
    {
        return _coordination?.BeforeActiveSetOperationAsync(operation, cancellationToken) ?? Task.CompletedTask;
    }

    private static async Task<(DefaultConversationTurnRecord Record, long ByteCount)?> ReadOptionalAsync(string path, string expectedTurnId, long maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            var record = await ReadRequiredAsync(path, maximumBytes, cancellationToken);
            if (!string.Equals(record.Record.TurnId, expectedTurnId, StringComparison.Ordinal))
            {
                throw new FormatException($"Default-conversation turn artifact `{path}` does not match its stable turn identity.");
            }

            return record;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListAsync(Func<DefaultConversationTurnRecord, bool> predicate, CancellationToken cancellationToken)
    {
        EnsureSupportedLayout();
        if (!Directory.Exists(_paths.DefaultConversationActiveTurnsPath))
        {
            return [];
        }

        var activeSetPath = GetActiveSetPath();
        var activeSetGate = GetGate(activeSetPath);
        await activeSetGate.WaitAsync(cancellationToken);
        try
        {
            await using var activeSetLease = await AcquireLeaseAsync(activeSetPath, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.List, cancellationToken);
            var records = new List<DefaultConversationTurnRecord>();
            var totalBytes = 0L;
            foreach (var path in EnumerateBoundedActivePaths())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await ReadActiveAsync(path, MaximumActiveAggregateBytes - totalBytes, archiveResolved: true, cancellationToken);
                if (read is null)
                {
                    continue;
                }

                totalBytes += read.Value.ByteCount;
                if (read.Value.Archived)
                {
                    continue;
                }
                if (predicate(read.Value.Record))
                {
                    records.Add(read.Value.Record);
                }
            }

            return records.OrderBy(record => record.Run.StartedAtUtc).ThenBy(record => record.TurnId, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            activeSetGate.Release();
        }
    }

    private static async Task<(DefaultConversationTurnRecord Record, long ByteCount)> ReadRequiredAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        DefaultConversationTurnRecord? record;
        byte[] bytes;
        try
        {
            await using var stream = DefaultConversationTurnNativeFileSystem.OpenRegularRead(path);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                throw new FormatException("The bounded default-conversation active-turn set contains an invalid artifact size.");
            }

            bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    throw new FormatException($"Default-conversation turn artifact `{path}` changed while it was being read.");
                }

                offset += read;
            }

            if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            {
                throw new FormatException("The bounded default-conversation active-turn set contains an invalid artifact size.");
            }

            EnsureNoDuplicateJsonProperties(bytes);
            record = JsonSerializer.Deserialize<DefaultConversationTurnRecord>(bytes, _jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Default-conversation turn artifact `{path}` contains invalid JSON or unsupported enum values.", exception);
        }

        if (record is null)
        {
            throw new FormatException($"Default-conversation turn artifact `{path}` was empty.");
        }

        ValidateRecord(record);
        return (record, bytes.LongLength);
    }

    private static byte[] SerializeRecord(DefaultConversationTurnRecord record)
    {
        var json = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.LongLength > MaximumActiveArtifactBytes)
        {
            throw new FormatException("The default-conversation turn artifact exceeds the maximum serialized size.");
        }

        return bytes;
    }

    private static void EnsureNoDuplicateJsonProperties(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        EnsureNoDuplicateJsonProperties(document.RootElement);
    }

    private static void EnsureNoDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("A default-conversation turn artifact contains duplicate JSON properties.");
                }

                EnsureNoDuplicateJsonProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateJsonProperties(item);
            }
        }
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await LoopArtifactFileWriter.WriteTextAsync(path, Encoding.UTF8.GetString(bytes), cancellationToken);
    }

    private void EnsureSupportedLayout()
    {
        if (Directory.Exists(_paths.DefaultConversationTurnsPath) && Directory.EnumerateFiles(_paths.DefaultConversationTurnsPath, "*.json", SearchOption.TopDirectoryOnly).Any())
        {
            throw new FormatException("The default-conversation turn layout predates bounded active-turn discovery. Reinitialize the workspace after preserving any required history.");
        }
    }

    private void EnsureActiveDirectory()
    {
        Directory.CreateDirectory(_paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(_paths.DefaultConversationTurnHistoryPath);
    }

    private void EnsureActiveCapacity()
    {
        if (Directory.Exists(_paths.DefaultConversationActiveTurnsPath) && EnumerateBoundedActivePaths().Count >= MaximumActiveArtifacts)
        {
            throw new IOException("The bounded default-conversation active-turn set is exhausted.");
        }
    }

    private async Task<long> ArchiveResolvedActiveAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.DefaultConversationActiveTurnsPath))
        {
            return 0;
        }

        var scannedBytes = 0L;
        var retainedBytes = 0L;
        foreach (var path in EnumerateBoundedActivePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadActiveAsync(path, MaximumActiveAggregateBytes - scannedBytes, archiveResolved: true, cancellationToken);
            if (read is not null)
            {
                scannedBytes += read.Value.ByteCount;
                if (!read.Value.Archived)
                {
                    retainedBytes += read.Value.ByteCount;
                }
            }
        }

        return retainedBytes;
    }

    private async Task<long> MeasureActiveAggregateBytesAsync(CancellationToken cancellationToken)
    {
        var totalBytes = 0L;
        foreach (var path in EnumerateBoundedActivePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadActiveAsync(path, MaximumActiveAggregateBytes - totalBytes, archiveResolved: false, cancellationToken);
            if (read is not null)
            {
                totalBytes += read.Value.ByteCount;
            }
        }

        return totalBytes;
    }

    private IReadOnlyList<string> EnumerateBoundedActivePaths()
    {
        var entries = Directory.EnumerateFileSystemEntries(_paths.DefaultConversationActiveTurnsPath, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumActiveDirectoryEntries + 1)
            .ToArray();
        if (entries.Length > MaximumActiveDirectoryEntries)
        {
            throw new IOException("The bounded default-conversation active-turn set is exhausted.");
        }

        var paths = new List<string>(MaximumActiveArtifacts);
        foreach (var entry in entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new FormatException("The bounded default-conversation active-turn set contains an unexpected entry.");
            }

            if (string.Equals(fileName, ".active-set.lock", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.Ordinal))
            {
                throw new FormatException("The bounded default-conversation active-turn set contains an unexpected entry.");
            }

            paths.Add(entry);
        }

        if (paths.Count > MaximumActiveArtifacts)
        {
            throw new IOException("The bounded default-conversation active-turn set is exhausted.");
        }

        return paths;
    }

    private void MoveToHistory(string activePath, string turnId)
    {
        var historyPath = GetHistoryPath(turnId);
        Directory.CreateDirectory(_paths.DefaultConversationTurnHistoryPath);
        if (File.Exists(historyPath))
        {
            throw new IOException("The immutable default-conversation terminal history already contains this turn.");
        }
        File.Move(activePath, historyPath);
    }

    private async Task<(DefaultConversationTurnRecord Record, long ByteCount, bool Archived)?> ReadActiveAsync(string activePath, long remainingAggregateBytes, bool archiveResolved, CancellationToken cancellationToken)
    {
        var expectedTurnId = Path.GetFileNameWithoutExtension(activePath);
        var activeRead = await ReadOptionalAsync(activePath, expectedTurnId, Math.Min(MaximumActiveArtifactBytes, remainingAggregateBytes), cancellationToken);
        if (activeRead is null)
        {
            return null;
        }

        var active = activeRead.Value.Record;
        var history = await ReadOptionalAsync(GetHistoryPath(active.TurnId), active.TurnId, MaximumActiveArtifactBytes, cancellationToken);
        RequireNoDuplicate(active, history?.Record, active.TurnId);
        if (archiveResolved && ShouldArchive(active))
        {
            MoveToHistory(activePath, active.TurnId);
            return (active, activeRead.Value.ByteCount, true);
        }

        return (active, activeRead.Value.ByteCount, false);
    }

    private static void EnsureActiveAggregateBytes(long existingBytes, long candidateBytes)
    {
        if (existingBytes < 0 || candidateBytes > MaximumActiveAggregateBytes - existingBytes)
        {
            throw new FormatException("The bounded default-conversation active-turn set exceeds its aggregate serialized size.");
        }
    }

    private static bool ShouldArchive(DefaultConversationTurnRecord record) => record.Checkpoint >= DefaultConversationTurnCheckpoint.Terminal && (record.Run.Status != LoopRunStatus.NeedsReview || record.ReviewResolution is not null);

    private static void RequireNoDuplicate(DefaultConversationTurnRecord? active, DefaultConversationTurnRecord? history, string turnId)
    {
        if (active is not null && history is not null)
        {
            throw new FormatException($"Default-conversation turn `{turnId}` exists in both active and immutable history storage.");
        }
    }

    private static void ValidateRecord(DefaultConversationTurnRecord record)
    {
        DefaultConversationTurnProtocolValidator.Validate(record);
        LoopArtifactPaths.ValidateArtifactId(record.TurnId);
    }

    private static bool ImmutableIdentityMatches(DefaultConversationTurnRecord left, DefaultConversationTurnRecord right)
    {
        return left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal)
            && string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
            && string.Equals(left.Run.RunId, right.Run.RunId, StringComparison.Ordinal)
            && string.Equals(left.Run.LoopId, right.Run.LoopId, StringComparison.Ordinal)
            && string.Equals(left.Run.RoleId, right.Run.RoleId, StringComparison.Ordinal)
            && left.Run.SchemaVersion == right.Run.SchemaVersion
            && string.Equals(left.Run.Surface, right.Run.Surface, StringComparison.Ordinal)
            && left.Run.Trigger == right.Run.Trigger
            && left.Run.StartedAtUtc == right.Run.StartedAtUtc
            && DictionariesEqual(left.Run.Metadata, right.Run.Metadata)
            && string.Equals(left.ConversationId, right.ConversationId, StringComparison.Ordinal)
            && string.Equals(left.ConversationVersion, right.ConversationVersion, StringComparison.Ordinal)
            && MessagesEqual(left.BaseTranscript, right.BaseTranscript)
            && left.UserMessage == right.UserMessage
            && string.Equals(left.ProviderAttemptId, right.ProviderAttemptId, StringComparison.Ordinal)
            && string.Equals(left.ProviderCorrelationId, right.ProviderCorrelationId, StringComparison.Ordinal)
            && string.Equals(left.UserPublicationId, right.UserPublicationId, StringComparison.Ordinal)
            && string.Equals(left.AssistantPublicationId, right.AssistantPublicationId, StringComparison.Ordinal);
    }

    private static bool EvidenceAdvances(DefaultConversationTurnRecord existing, DefaultConversationTurnRecord candidate)
    {
        return candidate.ProviderOutcome >= existing.ProviderOutcome
            && (existing.AssistantMessage is null || existing.AssistantMessage == candidate.AssistantMessage)
            && (existing.ProviderResponseId is null || string.Equals(existing.ProviderResponseId, candidate.ProviderResponseId, StringComparison.Ordinal))
            && ReviewCauseAdvances(existing, candidate)
            && (existing.ReviewDetail is null || string.Equals(existing.ReviewDetail, candidate.ReviewDetail, StringComparison.Ordinal))
            && (existing.ReviewResolution is null || existing.ReviewResolution == candidate.ReviewResolution)
            && (!existing.RunProjectionSynchronized || candidate.RunProjectionSynchronized)
            && (existing.Run.Status == LoopRunStatus.Started || RunsEqual(existing.Run, candidate.Run));
    }

    private static bool ReviewCauseAdvances(DefaultConversationTurnRecord existing, DefaultConversationTurnRecord candidate)
    {
        return existing.ReviewCause == candidate.ReviewCause
            || existing.ReviewCause == DefaultConversationTurnReviewCause.None
            && candidate.ReviewCause != DefaultConversationTurnReviewCause.None
            && existing.Checkpoint < DefaultConversationTurnCheckpoint.TerminalPrepared
            && candidate.Checkpoint == DefaultConversationTurnCheckpoint.TerminalPrepared
            && candidate.Run.Status == LoopRunStatus.NeedsReview;
    }

    private static bool TransitionHistoryExtends(DefaultConversationTurnRecord existing, DefaultConversationTurnRecord candidate)
    {
        return candidate.Transitions.Count == existing.Transitions.Count + 1
            && existing.Transitions.Zip(candidate.Transitions).All(pair => pair.First == pair.Second);
    }

    private static bool RecordsEqual(DefaultConversationTurnRecord left, DefaultConversationTurnRecord right)
    {
        return JsonSerializer.Serialize(left, _jsonOptions) == JsonSerializer.Serialize(right, _jsonOptions);
    }

    private static bool RunsEqual(EmbodySense.Core.Common.Loops.LoopRunRecord left, EmbodySense.Core.Common.Loops.LoopRunRecord right)
    {
        return left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && string.Equals(left.LoopId, right.LoopId, StringComparison.Ordinal)
            && string.Equals(left.RoleId, right.RoleId, StringComparison.Ordinal)
            && left.Status == right.Status
            && string.Equals(left.Surface, right.Surface, StringComparison.Ordinal)
            && left.Trigger == right.Trigger
            && left.StartedAtUtc == right.StartedAtUtc
            && left.CompletedAtUtc == right.CompletedAtUtc
            && string.Equals(left.FailureDetail, right.FailureDetail, StringComparison.Ordinal)
            && DictionariesEqual(left.Metadata, right.Metadata);
    }

    private static bool DictionariesEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool MessagesEqual(IReadOnlyList<EmbodySense.Core.Common.Inference.LlmMessage> left, IReadOnlyList<EmbodySense.Core.Common.Inference.LlmMessage> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
    }
}
