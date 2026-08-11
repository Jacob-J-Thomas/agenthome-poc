using EmbodySense.Core.Common.Governance.Audit;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Audit;

/// <summary>
/// Persists append-only audit events as newline-delimited JSON beneath the workspace audit directory.
/// </summary>
/// <remarks>
/// Appends targeting the same file are serialized across instances in this process. The store does not claim a cross-process
/// transaction. Tail reads preserve file order and ignore blank or malformed lines. A missing path, or one for which the
/// existence probe returns <see langword="false"/>, produces an empty result; cancellation and read failures after open propagate.
/// </remarks>
public sealed class AuditLog : IAuditLog, IGovernedLoopSequentialAuditRecorder
{
    private const string SequentialAuditDelivery = "governed-loop-sequential-v1";
    private const string AuditDeliveryMetadataKey = "governedLoopSequentialAuditDelivery";
    private const string AuditOperationIdMetadataKey = "governedLoopSequentialAuditOperationId";
    private const string AuditSchemaVersionMetadataKey = "governedLoopSequentialAuditSchemaVersion";
    private const string SequentialEvidenceHashMetadataKey = "governedLoopSequentialAuditEvidenceHash";
    private const int SequentialAuditSchemaVersion = 1;
    private const int MaxAuditRecordUtf8Bytes = 128 * 1024;
    private const long MaxSequentialAuditLedgerUtf8Bytes = 16L * 1024 * 1024;
    private const int MaxAuditMetadataEntries = 128;
    private const int MaxAuditMetadataKeyCharacters = 256;
    private const int MaxAuditMetadataStringCharacters = 16 * 1024;
    private const int MaxAuditFieldCharacters = 4096;
    private const int MaxAuditDetailCharacters = 16 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions _strictJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly WorkspacePaths _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditLog"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    public AuditLog(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    /// <summary>
    /// Creates an audit sink only when the requested root already contains EmbodySense agent scaffolding.
    /// </summary>
    /// <param name="rootPath">The absolute workspace root path.</param>
    /// <returns>The workspace audit sink, or <see langword="null"/> for a missing, blank, or uninitialized root.</returns>
    public static AuditLog? TryCreateForExistingWorkspace(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var paths = new WorkspacePaths(rootPath);

        return Directory.Exists(paths.AgentPath) ? new AuditLog(paths) : null;
    }

    /// <summary>
    /// Appends one canonical audit event as a single newline-delimited JSON record.
    /// </summary>
    /// <param name="auditEvent">The audit event.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes after the record has been appended to the workspace audit file.</returns>
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        Directory.CreateDirectory(_paths.AuditPath);
        var fileLock = _fileLocks.GetOrAdd(_paths.EventsLogPath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var line = JsonSerializer.Serialize(auditEvent, _jsonOptions);
            await File.AppendAllTextAsync(_paths.EventsLogPath, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Appends one exact sequential audit event once, or proves that its complete operation/evidence/event identity is already durable.
    /// </summary>
    /// <param name="operationId">The stable domain-separated sequential audit operation identity.</param>
    /// <param name="evidenceHash">The exact lowercase SHA-256 evidence identity.</param>
    /// <param name="auditEvent">The complete deterministic audit event.</param>
    /// <param name="cancellationToken">The token used while waiting, scanning, restoring a missing newline after a complete final record, and appending.</param>
    /// <returns>The closed durable record disposition.</returns>
    public async Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(
        string operationId,
        string evidenceHash,
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        var preparedEvent = PrepareSequentialAuditEvent(operationId, evidenceHash, auditEvent);
        var canonicalLine = SerializeCanonical(preparedEvent);
        if (_strictUtf8.GetByteCount(canonicalLine) > MaxAuditRecordUtf8Bytes)
        {
            throw new ArgumentException($"Sequential audit records cannot exceed {MaxAuditRecordUtf8Bytes} UTF-8 bytes.", nameof(auditEvent));
        }

        try
        {
            Directory.CreateDirectory(_paths.AuditPath);
            var fileLock = _fileLocks.GetOrAdd(_paths.EventsLogPath, _ => new SemaphoreSlim(1, 1));
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                return await RecordSequentialAuditUnderLockAsync(operationId, evidenceHash, preparedEvent, canonicalLine, cancellationToken);
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or DecoderFallbackException
            or NotSupportedException
            or SecurityException)
        {
            return Result(GovernedLoopSequentialAuditRecordStatus.Unavailable, "Sequential audit durability could not be proved from the authoritative ledger.");
        }
    }

    /// <summary>
    /// Reads the last bounded set of nonblank audit lines and returns the records that deserialize successfully in file order.
    /// </summary>
    /// <param name="limit">The limit.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// Up to <paramref name="limit"/> deserialized events from the last <paramref name="limit"/> nonblank lines. Malformed
    /// lines are skipped without backfilling from older lines, and a missing audit file produces an empty result.
    /// </returns>
    public async Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be greater than zero.");
        }

        if (!File.Exists(_paths.EventsLogPath))
        {
            return [];
        }

        var tailLines = new Queue<string>(limit);
        await using var stream = new FileStream(_paths.EventsLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (tailLines.Count == limit)
            {
                tailLines.Dequeue();
            }

            tailLines.Enqueue(line);
        }

        var events = new List<AuditEvent>();
        foreach (var line in tailLines)
        {
            try
            {
                var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, _jsonOptions);
                if (auditEvent is not null)
                {
                    events.Add(auditEvent);
                }
            }
            catch (JsonException)
            {
            }
        }

        return events;
    }

    private async Task<GovernedLoopSequentialAuditRecordResult> RecordSequentialAuditUnderLockAsync(
        string operationId,
        string evidenceHash,
        AuditEvent preparedEvent,
        string canonicalLine,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _paths.EventsLogPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (stream.Length > MaxSequentialAuditLedgerUtf8Bytes)
        {
            return Result(GovernedLoopSequentialAuditRecordStatus.Unavailable, "The authoritative audit ledger exceeds the bounded sequential reconciliation limit.");
        }

        if (!await RepairIncompleteTailAsync(stream, cancellationToken))
        {
            return Result(GovernedLoopSequentialAuditRecordStatus.Unavailable, "The authoritative audit ledger has an incomplete or corrupt final record that cannot be reconciled safely.");
        }

        var matchingOperationCount = 0;
        var exactMatch = false;
        stream.Position = 0;
        using (var reader = new StreamReader(stream, _strictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (_strictUtf8.GetByteCount(line) > MaxAuditRecordUtf8Bytes)
                {
                    throw new FormatException("An audit ledger record exceeds the bounded sequential reconciliation limit.");
                }

                var existingEvent = DeserializeStrict(line);
                if (!TryGetSequentialIdentity(existingEvent, out var existingOperationId, out var existingEvidenceHash))
                {
                    continue;
                }

                if (!string.Equals(existingOperationId, operationId, StringComparison.Ordinal))
                {
                    continue;
                }

                matchingOperationCount++;
                exactMatch |= string.Equals(existingEvidenceHash, evidenceHash, StringComparison.Ordinal)
                    && string.Equals(SerializeCanonical(existingEvent), canonicalLine, StringComparison.Ordinal);
                if (matchingOperationCount > 1 || !exactMatch)
                {
                    return Result(GovernedLoopSequentialAuditRecordStatus.Conflict, "Sequential audit operation identity is already bound to divergent or duplicate durable content.");
                }
            }
        }

        if (matchingOperationCount == 1)
        {
            return Result(GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded, "The exact sequential audit operation, evidence, and event were already durable.");
        }

        var record = _strictUtf8.GetBytes(canonicalLine + "\n");
        if (stream.Length + record.Length > MaxSequentialAuditLedgerUtf8Bytes)
        {
            return Result(GovernedLoopSequentialAuditRecordStatus.Unavailable, "Appending would exceed the bounded sequential audit ledger limit.");
        }

        stream.Position = stream.Length;
        await stream.WriteAsync(record, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        return Result(GovernedLoopSequentialAuditRecordStatus.Recorded, "The exact sequential audit operation, evidence, and event were recorded durably.");
    }

    private static async Task<bool> RepairIncompleteTailAsync(FileStream stream, CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == '\n')
        {
            return true;
        }

        var searchStart = Math.Max(0, stream.Length - MaxAuditRecordUtf8Bytes - 1L);
        long tailStart = 0;
        for (var position = stream.Length - 1; position >= searchStart; position--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = position;
            if (stream.ReadByte() == '\n')
            {
                tailStart = position + 1;
                break;
            }
        }

        var tailLength = stream.Length - tailStart;
        if (tailLength > MaxAuditRecordUtf8Bytes)
        {
            return false;
        }

        var tail = new byte[checked((int)tailLength)];
        stream.Position = tailStart;
        var offset = 0;
        while (offset < tail.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(tail.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("The audit ledger tail changed during exclusive reconciliation.");
            }

            offset += read;
        }

        var completeEvent = false;
        try
        {
            _ = DeserializeStrict(_strictUtf8.GetString(tail));
            completeEvent = true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or DecoderFallbackException)
        {
        }

        if (completeEvent)
        {
            if (stream.Length >= MaxSequentialAuditLedgerUtf8Bytes)
            {
                return false;
            }

            stream.Position = stream.Length;
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }
        else
        {
            return false;
        }

        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        return true;
    }

    private static AuditEvent PrepareSequentialAuditEvent(string operationId, string evidenceHash, AuditEvent? auditEvent)
    {
        RequireOperationId(operationId);
        RequireHash(evidenceHash, nameof(evidenceHash));
        ArgumentNullException.ThrowIfNull(auditEvent);
        ValidateAuditEvent(auditEvent, nameof(auditEvent), MaxAuditMetadataEntries - 4);
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in auditEvent.Metadata)
        {
            if (IsReservedSequentialMetadata(item.Key))
            {
                throw new ArgumentException("Caller audit metadata cannot own reserved sequential delivery fields.", nameof(auditEvent));
            }

            metadata.Add(item.Key, NormalizeMetadataValue(item.Value, nameof(auditEvent)));
        }

        metadata.Add(AuditDeliveryMetadataKey, SequentialAuditDelivery);
        metadata.Add(AuditOperationIdMetadataKey, operationId);
        metadata.Add(AuditSchemaVersionMetadataKey, SequentialAuditSchemaVersion);
        metadata.Add(SequentialEvidenceHashMetadataKey, evidenceHash);
        return auditEvent with { Metadata = metadata };
    }

    private static bool TryGetSequentialIdentity(AuditEvent auditEvent, out string operationId, out string evidenceHash)
    {
        operationId = string.Empty;
        evidenceHash = string.Empty;
        var metadata = auditEvent.Metadata;
        var reservedCount = metadata.Keys.Count(IsReservedSequentialMetadata);
        if (reservedCount == 0)
        {
            return false;
        }

        if (reservedCount != 4
            || !TryGetString(metadata, AuditDeliveryMetadataKey, out var delivery)
            || !string.Equals(delivery, SequentialAuditDelivery, StringComparison.Ordinal)
            || !TryGetInt32(metadata, AuditSchemaVersionMetadataKey, out var schemaVersion)
            || schemaVersion != SequentialAuditSchemaVersion
            || !TryGetString(metadata, AuditOperationIdMetadataKey, out operationId)
            || !TryGetString(metadata, SequentialEvidenceHashMetadataKey, out evidenceHash))
        {
            throw new FormatException("Sequential audit delivery metadata is incomplete or malformed.");
        }

        try
        {
            RequireOperationId(operationId);
            RequireHash(evidenceHash, SequentialEvidenceHashMetadataKey);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Sequential audit delivery metadata contains invalid identities.", exception);
        }

        try
        {
            foreach (var item in metadata)
            {
                _ = NormalizeMetadataValue(item.Value, "sequential audit metadata");
            }
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Sequential audit delivery metadata contains a noncanonical value.", exception);
        }

        return true;
    }

    private static AuditEvent DeserializeStrict(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        RejectDuplicateProperties(document.RootElement);
        var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, _strictJsonOptions)
            ?? throw new FormatException("The audit ledger contains an empty event.");
        try
        {
            ValidateAuditEvent(auditEvent, "audit ledger event", MaxAuditMetadataEntries);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("The audit ledger contains an event outside the bounded contract.", exception);
        }

        return auditEvent;
    }

    private static string SerializeCanonical(AuditEvent auditEvent)
    {
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in auditEvent.Metadata)
        {
            metadata.Add(item.Key, NormalizeMetadataValue(item.Value, "auditEvent"));
        }

        return JsonSerializer.Serialize(auditEvent with { Metadata = metadata }, _strictJsonOptions);
    }

    private static void ValidateAuditEvent(AuditEvent auditEvent, string parameterName, int maximumMetadataEntries)
    {
        if (auditEvent.TimestampUtc == default
            || auditEvent.TimestampUtc.Offset != TimeSpan.Zero
            || !IsBoundedText(auditEvent.Actor, MaxAuditFieldCharacters)
            || !IsBoundedText(auditEvent.Action, MaxAuditFieldCharacters)
            || !IsBoundedText(auditEvent.Target, MaxAuditFieldCharacters)
            || !IsBoundedText(auditEvent.Outcome, MaxAuditFieldCharacters)
            || !IsBoundedText(auditEvent.Detail, MaxAuditDetailCharacters)
            || auditEvent.Metadata is null
            || auditEvent.Metadata.Count > maximumMetadataEntries
            || auditEvent.Metadata.Any(item => !IsBoundedText(item.Key, MaxAuditMetadataKeyCharacters)))
        {
            throw new ArgumentException("The audit event is outside the bounded deterministic sequential-audit contract.", parameterName);
        }

    }

    private static object? NormalizeMetadataValue(object? value, string parameterName)
    {
        return value switch
        {
            null => null,
            string text => NormalizeMetadataString(text, parameterName),
            bool boolean => boolean,
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => NormalizeNumericMetadataValue(value, parameterName),
            float number when float.IsFinite(number) => NormalizeNumericMetadataValue(number, parameterName),
            double number when double.IsFinite(number) => NormalizeNumericMetadataValue(number, parameterName),
            JsonElement element => NormalizeJsonMetadataValue(element, parameterName),
            _ => throw new ArgumentException("Sequential audit metadata values must be null, strings, booleans, or finite numbers.", parameterName),
        };
    }

    private static object? NormalizeJsonMetadataValue(JsonElement element, string parameterName)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => NormalizeMetadataString(element.GetString() ?? string.Empty, parameterName),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var signed) => signed,
            JsonValueKind.Number when element.TryGetUInt64(out var unsigned) => unsigned,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalNumber) => decimalNumber,
            JsonValueKind.Number when element.TryGetDouble(out var doubleNumber) && double.IsFinite(doubleNumber) => doubleNumber,
            _ => throw new ArgumentException("Sequential audit metadata JSON values must be null, strings, booleans, or finite numbers.", parameterName),
        };
    }

    private static object NormalizeNumericMetadataValue(object value, string parameterName)
    {
        var element = JsonSerializer.SerializeToElement(value, value.GetType(), _strictJsonOptions);
        return NormalizeJsonMetadataValue(element, parameterName)
            ?? throw new ArgumentException("Sequential audit numeric metadata cannot normalize to null.", parameterName);
    }

    private static string NormalizeMetadataString(string value, string parameterName)
    {
        if (value.Length > MaxAuditMetadataStringCharacters)
        {
            throw new ArgumentException($"Sequential audit metadata strings cannot exceed {MaxAuditMetadataStringCharacters} characters.", parameterName);
        }

        return value;
    }

    private static void RequireOperationId(string? operationId)
    {
        const string Prefix = "sequential-audit-";
        if (operationId is null
            || !operationId.StartsWith(Prefix, StringComparison.Ordinal)
            || operationId.Length != Prefix.Length + 64
            || operationId[Prefix.Length..].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A derived sequential audit operation identity is required.", nameof(operationId));
        }
    }

    private static void RequireHash(string? value, string parameterName)
    {
        if (value is not { Length: 64 }
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        }
    }

    private static bool TryGetString(IReadOnlyDictionary<string, object?> metadata, string key, out string value)
    {
        value = string.Empty;
        if (!metadata.TryGetValue(key, out var candidate))
        {
            return false;
        }

        if (candidate is string text)
        {
            value = text;
            return true;
        }

        if (candidate is JsonElement { ValueKind: JsonValueKind.String } element)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryGetInt32(IReadOnlyDictionary<string, object?> metadata, string key, out int value)
    {
        value = default;
        if (!metadata.TryGetValue(key, out var candidate))
        {
            return false;
        }

        if (candidate is int integer)
        {
            value = integer;
            return true;
        }

        return candidate is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out value);
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
                    throw new JsonException("The audit ledger contains a duplicate JSON property.");
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

    private static bool IsReservedSequentialMetadata(string key)
        => string.Equals(key, AuditDeliveryMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, AuditOperationIdMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, AuditSchemaVersionMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, SequentialEvidenceHashMetadataKey, StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedText(string? value, int maximumCharacters)
        => value is { Length: > 0 } && value.Length <= maximumCharacters && !string.IsNullOrWhiteSpace(value);

    private static GovernedLoopSequentialAuditRecordResult Result(GovernedLoopSequentialAuditRecordStatus status, string detail)
        => new(status, detail);
}
