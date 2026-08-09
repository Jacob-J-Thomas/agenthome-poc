using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Persists strict version-1 default-conversation turn protocol artifacts with optimistic append-only updates.
/// </summary>
/// <remarks>
/// Each lifecycle version is atomically published through a same-directory replacement. A process-local gate and exclusive
/// per-turn lock file serialize cooperating writers across processes. Unsupported schemas and altered transition history fail closed.
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
    private static readonly JsonDocumentOptions _jsonDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };
    private static readonly TimeSpan _leaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly WorkspacePaths _paths;

    /// <summary>Initializes the store for one workspace.</summary>
    public DefaultConversationTurnStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<DefaultConversationTurnStoreResult> CreateAsync(DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
    {
        ValidateRecord(record);
        var path = GetPath(record.TurnId);
        var gate = GetGate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.DefaultConversationTurnsPath);
            await using var lease = await AcquireLeaseAsync(path, cancellationToken);
            var existing = await ReadOptionalAsync(path, cancellationToken);
            if (existing is not null)
            {
                var status = RecordsEqual(existing, record) ? DefaultConversationTurnStoreStatus.Replay : DefaultConversationTurnStoreStatus.Conflict;
                return new DefaultConversationTurnStoreResult(status, existing);
            }

            await WriteAsync(path, record, cancellationToken);
            return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Created, record);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DefaultConversationTurnStoreResult> UpdateAsync(DefaultConversationTurnRecord record, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        ValidateRecord(record);
        if (expectedLifecycleVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLifecycleVersion));
        }

        var path = GetPath(record.TurnId);
        var gate = GetGate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireLeaseAsync(path, cancellationToken);
            var existing = await ReadOptionalAsync(path, cancellationToken);
            if (existing is null)
            {
                return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Conflict, null);
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

            await WriteAsync(path, record, cancellationToken);
            return new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Updated, record);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DefaultConversationTurnRecord?> LoadAsync(string turnId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(turnId);
        var gate = GetGate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadOptionalAsync(path, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default)
    {
        // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/259): Replace full historical scans with a bounded active/review index while preserving immutable terminal evidence.
        return await ListAsync(record => record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListNeedsReviewAsync(CancellationToken cancellationToken = default)
    {
        return await ListAsync(record => record.Checkpoint == DefaultConversationTurnCheckpoint.Terminal && record.Run.Status == LoopRunStatus.NeedsReview && record.ReviewResolution is null, cancellationToken);
    }

    private string GetPath(string turnId)
    {
        return Path.Combine(_paths.DefaultConversationTurnsPath, LoopArtifactPaths.ValidateArtifactId(turnId) + ".json");
    }

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
            try
            {
                return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(_leaseRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task<DefaultConversationTurnRecord?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
    {
        return File.Exists(path) ? await ReadRequiredAsync(path, cancellationToken) : null;
    }

    private async Task<IReadOnlyList<DefaultConversationTurnRecord>> ListAsync(Func<DefaultConversationTurnRecord, bool> predicate, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.DefaultConversationTurnsPath))
        {
            return [];
        }

        var records = new List<DefaultConversationTurnRecord>();
        foreach (var path in Directory.EnumerateFiles(_paths.DefaultConversationTurnsPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ReadRequiredAsync(path, cancellationToken);
            if (HasCanonicalFileName(path, record.TurnId) && predicate(record))
            {
                records.Add(record);
            }
        }

        return records.OrderBy(record => record.Run.StartedAtUtc).ThenBy(record => record.TurnId, StringComparer.Ordinal).ToArray();
    }

    private static async Task<DefaultConversationTurnRecord> ReadRequiredAsync(string path, CancellationToken cancellationToken)
    {
        DefaultConversationTurnRecord? record;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // TODO(#259): Bound the persisted-artifact byte budget before DOM materialization so one corrupt turn cannot consume unbounded memory.
            using var document = await JsonDocument.ParseAsync(stream, _jsonDocumentOptions, cancellationToken);
            RejectDuplicateProperties(document.RootElement);
            record = JsonSerializer.Deserialize<DefaultConversationTurnRecord>(document.RootElement, _jsonOptions);
        }
        catch (JsonException)
        {
            throw new FormatException($"Default-conversation turn artifact `{path}` contains invalid JSON, unsupported fields, or unsupported enum values.");
        }

        if (record is null)
        {
            throw new FormatException($"Default-conversation turn artifact `{path}` was empty.");
        }

        ValidateRecord(record);
        return record;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("Default-conversation turn artifact contains duplicate JSON object members.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static async Task WriteAsync(string path, DefaultConversationTurnRecord record, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
        await LoopArtifactFileWriter.WriteTextAsync(path, json, cancellationToken);
    }

    private static void ValidateRecord(DefaultConversationTurnRecord record)
    {
        DefaultConversationTurnProtocolValidator.Validate(record);
        LoopArtifactPaths.ValidateArtifactId(record.TurnId);
    }

    private static bool HasCanonicalFileName(string path, string turnId)
    {
        var expectedFileName = LoopArtifactPaths.ValidateArtifactId(turnId) + ".json";
        return string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.Ordinal);
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
            && string.Equals(left.AssistantPublicationId, right.AssistantPublicationId, StringComparison.Ordinal)
            && string.Equals(JsonSerializer.Serialize(left.CapabilityAdmission, _jsonOptions), JsonSerializer.Serialize(right.CapabilityAdmission, _jsonOptions), StringComparison.Ordinal);
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
