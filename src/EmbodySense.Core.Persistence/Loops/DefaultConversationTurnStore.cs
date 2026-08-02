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
/// workspace active-set lease serialize cooperating operations across processes. Terminal archival first claims and re-proves the
/// exact source identity and bytes, then publishes immutable history atomically without replacement. Unsupported schemas, pathname
/// substitutions, and altered transition history fail closed. Each terminal history artifact retains one byte-identical immutable
/// source-proof sidecar so cleanup never unlinks an unverified pathname; terminal history therefore consumes roughly twice the record bytes.
/// </remarks>
public sealed class DefaultConversationTurnStore : IDefaultConversationTurnStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
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
    /// <param name="coordination">An optional observer invoked at active-set and archival phases while the store owns the active-turn-set lease.</param>
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
            await RecoverInterruptedArchivesAsync(DefaultConversationTurnStoreOperation.Create, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.Create, cancellationToken);
            var activeBytes = await ArchiveResolvedActiveAsync(cancellationToken);
            var existingRead = await ReadOptionalAsync(path, record.TurnId, MaximumActiveArtifactBytes, cancellationToken);
            var historicalRead = await ReadHistoryOptionalAsync(record.TurnId, cancellationToken);
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
            await RecoverInterruptedArchivesAsync(DefaultConversationTurnStoreOperation.Update, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.Update, cancellationToken);
            var activeBytes = await MeasureActiveAggregateBytesAsync(cancellationToken);
            var existingRead = await ReadOptionalAsync(path, record.TurnId, MaximumActiveArtifactBytes, cancellationToken);
            var historicalRead = await ReadHistoryOptionalAsync(record.TurnId, cancellationToken);
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
            if (ShouldArchive(record))
            {
                var written = await WriteWithProofAsync(path, record, serializedRecord, cancellationToken);
                await ObserveArchivePhaseAsync(DefaultConversationTurnStoreOperation.Update, record.TurnId, DefaultConversationTurnArchivePhase.AfterTerminalWritePublication, cancellationToken);
                await ArchiveActiveAsync(path, written, DefaultConversationTurnStoreOperation.Update, cancellationToken);
            }
            else
            {
                await WriteAsync(path, serializedRecord, cancellationToken);
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
            await RecoverInterruptedArchivesAsync(DefaultConversationTurnStoreOperation.Load, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.Load, cancellationToken);
            var active = await ReadOptionalAsync(path, turnId, MaximumActiveArtifactBytes, cancellationToken);
            var history = await ReadHistoryOptionalAsync(turnId, cancellationToken);
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

    private static async Task<(DefaultConversationTurnRecord Record, long ByteCount, byte[] Bytes, DefaultConversationTurnFileIdentity Identity)?> ReadOptionalAsync(string path, string expectedTurnId, long maximumBytes, CancellationToken cancellationToken)
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

    private async Task<(DefaultConversationTurnRecord Record, long ByteCount, byte[] Bytes, DefaultConversationTurnFileIdentity Identity)?> ReadHistoryOptionalAsync(string turnId, CancellationToken cancellationToken)
    {
        var history = await ReadOptionalAsync(GetHistoryPath(turnId), turnId, MaximumActiveArtifactBytes, cancellationToken);
        var sourceProof = await ReadOptionalAsync(GetHistorySourceProofPath(turnId), turnId, MaximumActiveArtifactBytes, cancellationToken);
        if (EntryExists(GetPendingArchiveHistoryPath(turnId)))
        {
            throw new FormatException($"Default-conversation turn `{turnId}` has interrupted archival staging outside recovery.");
        }

        if (history is null && sourceProof is null)
        {
            return null;
        }

        if (history is null || sourceProof is null)
        {
            throw new FormatException($"Default-conversation turn `{turnId}` has incomplete immutable archival evidence.");
        }

        if (!history.Value.Bytes.AsSpan().SequenceEqual(sourceProof.Value.Bytes))
        {
            throw new FormatException($"Default-conversation turn `{turnId}` has conflicting immutable archival evidence.");
        }

        return history;
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
            await RecoverInterruptedArchivesAsync(DefaultConversationTurnStoreOperation.List, cancellationToken);
            await CoordinateActiveSetAsync(DefaultConversationTurnStoreOperation.List, cancellationToken);
            var records = new List<DefaultConversationTurnRecord>();
            var totalBytes = 0L;
            foreach (var path in EnumerateBoundedActivePaths())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await ReadActiveAsync(path, MaximumActiveAggregateBytes - totalBytes, archiveResolved: true, DefaultConversationTurnStoreOperation.List, cancellationToken);
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

    private static async Task<(DefaultConversationTurnRecord Record, long ByteCount, byte[] Bytes, DefaultConversationTurnFileIdentity Identity)> ReadRequiredAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
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

            var identity = DefaultConversationTurnNativeFileSystem.GetIdentity(stream);
            EnsureNoDuplicateJsonProperties(bytes);
            var record = JsonSerializer.Deserialize<DefaultConversationTurnRecord>(bytes, _jsonOptions);

            if (record is null)
            {
                throw new FormatException($"Default-conversation turn artifact `{path}` was empty.");
            }

            ValidateRecord(record);
            return (record, bytes.LongLength, bytes, identity);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Default-conversation turn artifact `{path}` contains invalid JSON or unsupported enum values.", exception);
        }

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

    private static async Task<(DefaultConversationTurnRecord Record, long ByteCount, byte[] Bytes, DefaultConversationTurnFileIdentity Identity)> WriteWithProofAsync(
        string path,
        DefaultConversationTurnRecord record,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        return await LoopArtifactFileWriter.WriteTextWithProofAsync(path, Encoding.UTF8.GetString(bytes), (stream, writtenBytes) =>
        {
            if (!writtenBytes.AsSpan().SequenceEqual(bytes))
            {
                throw new FormatException("The default-conversation turn artifact changed while its terminal write was staged.");
            }

            return (record, writtenBytes.LongLength, writtenBytes, DefaultConversationTurnNativeFileSystem.GetIdentity(stream));
        }, cancellationToken);
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
            var read = await ReadActiveAsync(path, MaximumActiveAggregateBytes - scannedBytes, archiveResolved: true, DefaultConversationTurnStoreOperation.Create, cancellationToken);
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
            var read = await ReadActiveAsync(path, MaximumActiveAggregateBytes - totalBytes, archiveResolved: false, DefaultConversationTurnStoreOperation.Update, cancellationToken);
            if (read is not null)
            {
                totalBytes += read.Value.ByteCount;
            }
        }

        return totalBytes;
    }

    private string GetPendingArchiveSourcePath(string turnId)
    {
        return Path.Combine(_paths.DefaultConversationActiveTurnsPath, $".{LoopArtifactPaths.ValidateArtifactId(turnId)}.json.archive-source");
    }

    private string GetPendingArchiveHistoryPath(string turnId)
    {
        return Path.Combine(_paths.DefaultConversationTurnHistoryPath, $".{LoopArtifactPaths.ValidateArtifactId(turnId)}.json.archive-history.tmp");
    }

    private string GetHistorySourceProofPath(string turnId)
    {
        return Path.Combine(_paths.DefaultConversationTurnHistoryPath, $".{LoopArtifactPaths.ValidateArtifactId(turnId)}.json.archive-source-proof");
    }

    private async Task ArchiveActiveAsync(string activePath, (DefaultConversationTurnRecord Record, long ByteCount, byte[] Bytes, DefaultConversationTurnFileIdentity Identity) proof, DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken)
    {
        await ObserveArchivePhaseAsync(operation, proof.Record.TurnId, DefaultConversationTurnArchivePhase.BeforeSourceClaim, cancellationToken);
        var pendingSourcePath = GetPendingArchiveSourcePath(proof.Record.TurnId);
        var pendingHistoryPath = GetPendingArchiveHistoryPath(proof.Record.TurnId);
        var historyPath = GetHistoryPath(proof.Record.TurnId);
        var sourceProofPath = GetHistorySourceProofPath(proof.Record.TurnId);
        Directory.CreateDirectory(_paths.DefaultConversationTurnHistoryPath);
        File.Move(activePath, pendingSourcePath, overwrite: false);
        DefaultConversationTurnFileIdentity? historyStageIdentity = null;
        var historyStageProved = false;
        var historyPublished = false;
        try
        {
            var claimed = await ReadRequiredAsync(pendingSourcePath, MaximumActiveArtifactBytes, cancellationToken);
            if (claimed.Identity != proof.Identity || !claimed.Bytes.AsSpan().SequenceEqual(proof.Bytes))
            {
                throw new FormatException("The default-conversation turn artifact pathname was substituted before archival.");
            }

            if (EntryExists(activePath))
            {
                throw new FormatException("The default-conversation turn artifact pathname was replaced during archival.");
            }

            await WriteHistoryStageAsync(
                pendingHistoryPath,
                proof.Bytes,
                operation,
                proof.Record.TurnId,
                identity => historyStageIdentity = identity,
                cancellationToken);
            var stagedHistory = await ReadRequiredAsync(pendingHistoryPath, MaximumActiveArtifactBytes, cancellationToken);
            if (historyStageIdentity is null || stagedHistory.Identity != historyStageIdentity.Value || !stagedHistory.Bytes.AsSpan().SequenceEqual(proof.Bytes))
            {
                throw new FormatException("The default-conversation turn history staging artifact changed before publication.");
            }

            historyStageProved = true;
            await ObserveArchivePhaseAsync(operation, proof.Record.TurnId, DefaultConversationTurnArchivePhase.BeforeHistoryPublication, cancellationToken);
            File.Move(pendingHistoryPath, historyPath, overwrite: false);
            historyPublished = true;
            await ObserveArchivePhaseAsync(operation, proof.Record.TurnId, DefaultConversationTurnArchivePhase.BeforeInitialHistoryRevalidation, cancellationToken);
            await RevalidatePublishedHistoryAsync(historyPath, proof.Record.TurnId, stagedHistory.Identity, proof.Bytes, cancellationToken);
            await ObserveArchivePhaseAsync(operation, proof.Record.TurnId, DefaultConversationTurnArchivePhase.AfterHistoryPublication, cancellationToken);
            File.Move(pendingSourcePath, sourceProofPath, overwrite: false);
            await ObserveArchivePhaseAsync(operation, proof.Record.TurnId, DefaultConversationTurnArchivePhase.AfterSourceProofPublication, cancellationToken);
            var sourceProof = await ReadRequiredAsync(sourceProofPath, MaximumActiveArtifactBytes, cancellationToken);
            if (sourceProof.Identity != proof.Identity || !sourceProof.Bytes.AsSpan().SequenceEqual(proof.Bytes))
            {
                TryRestorePendingSource(sourceProofPath, activePath);
                throw new FormatException("The default-conversation turn source proof was substituted during archival.");
            }

            if (EntryExists(activePath))
            {
                throw new FormatException("The default-conversation turn artifact pathname was replaced during archival.");
            }

            await ObserveArchivePhaseAsync(operation, proof.Record.TurnId, DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, cancellationToken);
            await RevalidatePublishedHistoryAsync(historyPath, proof.Record.TurnId, stagedHistory.Identity, sourceProof.Bytes, cancellationToken);
        }
        catch (Exception exception)
        {
            if ((!historyPublished && !historyStageProved && TryRemoveOwnedIncompleteHistoryStage(pendingHistoryPath, historyStageIdentity))
                || (historyPublished && exception is FormatException))
            {
                TryRestorePendingSource(pendingSourcePath, activePath);
                TryRestorePendingSource(sourceProofPath, activePath);
            }

            throw;
        }
    }

    private async Task RecoverInterruptedArchivesAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.DefaultConversationActiveTurnsPath))
        {
            return;
        }

        var (_, pendingSourcePaths) = EnumerateBoundedActiveEntries();
        foreach (var pendingSourcePath in pendingSourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turnId = ParsePendingArchiveTurnId(pendingSourcePath);
            var activePath = GetActivePath(turnId);
            var historyPath = GetHistoryPath(turnId);
            var pendingHistoryPath = GetPendingArchiveHistoryPath(turnId);
            var sourceProofPath = GetHistorySourceProofPath(turnId);
            if (EntryExists(activePath))
            {
                throw new FormatException($"Default-conversation turn `{turnId}` has both active and interrupted archival sources.");
            }

            var pending = await ReadRequiredAsync(pendingSourcePath, MaximumActiveArtifactBytes, cancellationToken);
            if (!ShouldArchive(pending.Record))
            {
                throw new FormatException($"Default-conversation turn `{turnId}` has an invalid interrupted archival source.");
            }

            var history = await ReadOptionalAsync(historyPath, turnId, MaximumActiveArtifactBytes, cancellationToken);
            var sourceProof = await ReadOptionalAsync(sourceProofPath, turnId, MaximumActiveArtifactBytes, cancellationToken);
            if (history is not null)
            {
                if (!history.Value.Bytes.AsSpan().SequenceEqual(pending.Bytes))
                {
                    throw new FormatException($"Default-conversation turn `{turnId}` has conflicting interrupted archival history.");
                }

                if (EntryExists(pendingHistoryPath) || sourceProof is not null)
                {
                    throw new FormatException($"Default-conversation turn `{turnId}` has duplicate interrupted archival evidence.");
                }

                File.Move(pendingSourcePath, sourceProofPath, overwrite: false);
                try
                {
                    var recoveredProof = await ReadRequiredAsync(sourceProofPath, MaximumActiveArtifactBytes, cancellationToken);
                    if (recoveredProof.Identity != pending.Identity || !recoveredProof.Bytes.AsSpan().SequenceEqual(history.Value.Bytes))
                    {
                        throw new FormatException($"Default-conversation turn `{turnId}` has a substituted interrupted source proof.");
                    }

                    await ObserveArchivePhaseAsync(operation, turnId, DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, cancellationToken);
                    await RevalidatePublishedHistoryAsync(historyPath, turnId, history.Value.Identity, recoveredProof.Bytes, cancellationToken);
                }
                catch (Exception exception)
                {
                    if (exception is FormatException)
                    {
                        TryRestorePendingSource(sourceProofPath, activePath);
                    }

                    throw;
                }

                continue;
            }

            if (sourceProof is not null)
            {
                throw new FormatException($"Default-conversation turn `{turnId}` has a source proof without canonical history.");
            }

            if (EntryExists(pendingHistoryPath))
            {
                var staged = await ReadRequiredAsync(pendingHistoryPath, MaximumActiveArtifactBytes, cancellationToken);
                if (!staged.Bytes.AsSpan().SequenceEqual(pending.Bytes))
                {
                    throw new FormatException($"Default-conversation turn `{turnId}` has conflicting interrupted archival staging.");
                }

                await ObserveArchivePhaseAsync(operation, turnId, DefaultConversationTurnArchivePhase.BeforeHistoryPublication, cancellationToken);
                File.Move(pendingHistoryPath, historyPath, overwrite: false);
                try
                {
                    await ObserveArchivePhaseAsync(operation, turnId, DefaultConversationTurnArchivePhase.BeforeInitialHistoryRevalidation, cancellationToken);
                    await RevalidatePublishedHistoryAsync(historyPath, turnId, staged.Identity, pending.Bytes, cancellationToken);
                    File.Move(pendingSourcePath, sourceProofPath, overwrite: false);
                    var recoveredProof = await ReadRequiredAsync(sourceProofPath, MaximumActiveArtifactBytes, cancellationToken);
                    if (recoveredProof.Identity != pending.Identity || !recoveredProof.Bytes.AsSpan().SequenceEqual(staged.Bytes))
                    {
                        throw new FormatException($"Default-conversation turn `{turnId}` has a substituted interrupted source proof.");
                    }

                    await ObserveArchivePhaseAsync(operation, turnId, DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, cancellationToken);
                    await RevalidatePublishedHistoryAsync(historyPath, turnId, staged.Identity, recoveredProof.Bytes, cancellationToken);
                }
                catch (Exception exception)
                {
                    if (exception is FormatException)
                    {
                        TryRestorePendingSource(pendingSourcePath, activePath);
                        TryRestorePendingSource(sourceProofPath, activePath);
                    }

                    throw;
                }

                continue;
            }

            File.Move(pendingSourcePath, activePath, overwrite: false);
        }
    }

    private IReadOnlyList<string> EnumerateBoundedActivePaths()
    {
        return EnumerateBoundedActiveEntries().ActivePaths;
    }

    private (IReadOnlyList<string> ActivePaths, IReadOnlyList<string> PendingSourcePaths) EnumerateBoundedActiveEntries()
    {
        var entries = Directory.EnumerateFileSystemEntries(_paths.DefaultConversationActiveTurnsPath, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumActiveDirectoryEntries + 1)
            .ToArray();
        if (entries.Length > MaximumActiveDirectoryEntries)
        {
            throw new IOException("The bounded default-conversation active-turn set is exhausted.");
        }

        var activePaths = new List<string>(MaximumActiveArtifacts);
        var pendingSourcePaths = new List<string>();
        foreach (var entry in entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(entry);
            if (string.Equals(fileName, ".active-set.lock", StringComparison.Ordinal))
            {
                continue;
            }

            if (fileName.Length > 0 && fileName[0] == '.' && fileName.EndsWith(".json.archive-source", StringComparison.Ordinal))
            {
                _ = ParsePendingArchiveTurnId(entry);
                pendingSourcePaths.Add(entry);
                continue;
            }

            if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.Ordinal))
            {
                throw new FormatException("The bounded default-conversation active-turn set contains an unexpected entry.");
            }

            activePaths.Add(entry);
        }

        if (activePaths.Count + pendingSourcePaths.Count > MaximumActiveArtifacts)
        {
            throw new IOException("The bounded default-conversation active-turn set is exhausted.");
        }

        return (activePaths, pendingSourcePaths);
    }

    private static string ParsePendingArchiveTurnId(string path)
    {
        const string Suffix = ".json.archive-source";
        var fileName = Path.GetFileName(path);
        if (fileName.Length <= Suffix.Length + 1 || fileName[0] != '.' || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            throw new FormatException("The bounded default-conversation active-turn set contains an invalid interrupted archive source.");
        }

        return LoopArtifactPaths.ValidateArtifactId(fileName[1..^Suffix.Length]);
    }

    private async Task WriteHistoryStageAsync(
        string path,
        byte[] bytes,
        DefaultConversationTurnStoreOperation operation,
        string turnId,
        Action<DefaultConversationTurnFileIdentity> captureIdentity,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        captureIdentity(DefaultConversationTurnNativeFileSystem.GetIdentity(stream));
        await stream.WriteAsync(bytes.AsMemory(0, 1), cancellationToken);
        await ObserveArchivePhaseAsync(operation, turnId, DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite, cancellationToken);
        await stream.WriteAsync(bytes.AsMemory(1), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool TryRemoveOwnedIncompleteHistoryStage(string path, DefaultConversationTurnFileIdentity? expectedIdentity)
    {
        if (!EntryExists(path))
        {
            return true;
        }

        if (expectedIdentity is null)
        {
            return false;
        }

        try
        {
            using (var stream = DefaultConversationTurnNativeFileSystem.OpenRegularRead(path))
            {
                if (DefaultConversationTurnNativeFileSystem.GetIdentity(stream) != expectedIdentity.Value)
                {
                    return false;
                }
            }

            File.Delete(path);
            return !EntryExists(path);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryRestorePendingSource(string pendingSourcePath, string activePath)
    {
        try
        {
            File.Move(pendingSourcePath, activePath, overwrite: false);
        }
        catch (FileNotFoundException)
        {
        }
        catch (IOException) when (EntryExists(activePath))
        {
        }
    }

    private static async Task RevalidatePublishedHistoryAsync(string historyPath, string turnId, DefaultConversationTurnFileIdentity expectedIdentity, byte[] expectedBytes, CancellationToken cancellationToken)
    {
        var published = await ReadOptionalAsync(historyPath, turnId, MaximumActiveArtifactBytes, cancellationToken);
        if (published is null || published.Value.Identity != expectedIdentity || !published.Value.Bytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new FormatException($"Default-conversation turn `{turnId}` canonical history was substituted during publication.");
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private Task ObserveArchivePhaseAsync(DefaultConversationTurnStoreOperation operation, string turnId, DefaultConversationTurnArchivePhase phase, CancellationToken cancellationToken)
    {
        return _coordination?.ObserveArchivePhaseAsync(operation, turnId, phase, cancellationToken) ?? Task.CompletedTask;
    }

    private async Task<(DefaultConversationTurnRecord Record, long ByteCount, bool Archived)?> ReadActiveAsync(string activePath, long remainingAggregateBytes, bool archiveResolved, DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken)
    {
        var expectedTurnId = Path.GetFileNameWithoutExtension(activePath);
        var activeRead = await ReadOptionalAsync(activePath, expectedTurnId, Math.Min(MaximumActiveArtifactBytes, remainingAggregateBytes), cancellationToken);
        if (activeRead is null)
        {
            return null;
        }

        var active = activeRead.Value.Record;
        var history = await ReadHistoryOptionalAsync(active.TurnId, cancellationToken);
        RequireNoDuplicate(active, history?.Record, active.TurnId);
        if (archiveResolved && ShouldArchive(active))
        {
            await ArchiveActiveAsync(activePath, activeRead.Value, operation, cancellationToken);
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
            && (existing.ReviewDetail is null || string.Equals(existing.ReviewDetail, candidate.ReviewDetail, StringComparison.Ordinal))
            && (existing.ReviewResolution is null || existing.ReviewResolution == candidate.ReviewResolution)
            && (!existing.RunProjectionSynchronized || candidate.RunProjectionSynchronized)
            && (existing.Run.Status == LoopRunStatus.Started || RunsEqual(existing.Run, candidate.Run));
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
