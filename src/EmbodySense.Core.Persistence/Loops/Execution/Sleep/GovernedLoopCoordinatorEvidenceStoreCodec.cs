using System.Buffers;
using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

internal static class GovernedLoopCoordinatorEvidenceStoreCodec
{
    private const int SchemaVersion = 1;
    private const int MaximumDepth = 12;
    private static readonly string[] _catalogProperties = ["entries", "generation", "schemaVersion"];
    private static readonly string[] _entryProperties = ["coordinatorId", "failures", "heartbeatRetirements", "heartbeats", "lifecycles", "ownerships"];
    private static readonly string[] _ownershipProperties = ["acquiredAtUtc", "contentHash", "coordinatorId", "ownerId", "ownershipEpoch", "schemaVersion"];
    private static readonly string[] _lifecycleProperties = ["contentHash", "lifecycleVersion", "ownershipHash", "schemaVersion", "status", "terminalAtUtc", "updatedAtUtc"];
    private static readonly string[] _heartbeatProperties = ["contentHash", "heartbeatSequence", "leaseExpiresAtUtc", "ownershipHash", "recordedAtUtc", "schemaVersion"];
    private static readonly string[] _heartbeatRetirementProperties = ["chainHash", "contentHash", "initialHeartbeatHash", "ownershipHash", "retiredCount", "retiredThroughHeartbeatHash", "retiredThroughLeaseExpiresAtUtc", "retiredThroughRecordedAtUtc", "retiredThroughSequence", "schemaVersion"];
    private static readonly string[] _failureProperties = ["contentHash", "detailEvidenceReference", "failureSequence", "kind", "occurredAtUtc", "ownershipHash", "schemaVersion"];
    private static readonly string[] _evidenceArrayProperties = ["ownerships", "lifecycles", "heartbeatRetirements", "heartbeats", "failures"];

    public static byte[] Serialize(
        GovernedLoopCoordinatorEvidenceStoreCatalog catalog,
        int maximumEvidenceItems,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != SchemaVersion || catalog.Generation < 1 || catalog.Entries.Count == 0)
        {
            throw Invalid();
        }

        var coordinatorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            ValidateEntry(entry, maximumEvidenceItems);
            if (!coordinatorIds.Add(entry.CoordinatorId))
            {
                throw Invalid();
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var entry in catalog.Entries)
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();
            writer.WriteNumber("generation", catalog.Generation);
            writer.WriteNumber("schemaVersion", catalog.SchemaVersion);
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > maximumBytes)
        {
            throw new GovernedLoopSleepStoreLimitException();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static GovernedLoopCoordinatorEvidenceStoreCatalog Deserialize(
        byte[] bytes,
        int maximumCoordinators,
        int maximumEvidenceItems,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > maximumBytes)
        {
            throw new GovernedLoopSleepStoreLimitException();
        }

        if (bytes.Length == 0 || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth,
            });
            var root = document.RootElement;
            if (!IsExactObject(root, _catalogProperties)
                || !TryInt32(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != SchemaVersion
                || !TryInt64(root, "generation", out var generation)
                || generation < 1)
            {
                throw Invalid();
            }

            var entriesElement = root.GetProperty("entries");
            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid();
            }

            if (entriesElement.GetArrayLength() > maximumCoordinators)
            {
                throw new GovernedLoopSleepStoreLimitException();
            }

            var entries = new List<GovernedLoopCoordinatorEvidenceStoreEntry>(entriesElement.GetArrayLength());
            var coordinatorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in entriesElement.EnumerateArray())
            {
                var entry = ParseEntry(element, maximumEvidenceItems);
                if (!coordinatorIds.Add(entry.CoordinatorId))
                {
                    throw Invalid();
                }

                entries.Add(entry);
            }

            var catalog = new GovernedLoopCoordinatorEvidenceStoreCatalog(schemaVersion, generation, Ordered(entries));
            if (!bytes.AsSpan().SequenceEqual(Serialize(catalog, maximumEvidenceItems, maximumBytes)))
            {
                throw Invalid();
            }

            return catalog;
        }
        catch (GovernedLoopSleepStoreLimitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException or InvalidOperationException or FormatException)
        {
            throw Invalid(exception);
        }
    }

    private static GovernedLoopCoordinatorEvidenceStoreEntry ParseEntry(JsonElement element, int maximumEvidenceItems)
    {
        if (!IsExactObject(element, _entryProperties)
            || !TryString(element, "coordinatorId", out var coordinatorId))
        {
            throw Invalid();
        }

        PreflightEvidenceBounds(element, maximumEvidenceItems);

        var ownerships = ParseArray(element, "ownerships", ParseOwnership);
        var ownershipByHash = ownerships.ToDictionary(item => item.ContentHash, StringComparer.Ordinal);
        var lifecycles = ParseArray(element, "lifecycles", item => ParseLifecycle(item, ownershipByHash));
        var heartbeatRetirements = ParseArray(element, "heartbeatRetirements", item => ParseHeartbeatRetirement(item, ownershipByHash));
        var heartbeats = ParseArray(element, "heartbeats", item => ParseHeartbeat(item, ownershipByHash));
        var failures = ParseArray(element, "failures", item => ParseFailure(item, ownershipByHash));
        var entry = new GovernedLoopCoordinatorEvidenceStoreEntry(
            coordinatorId!,
            Array.AsReadOnly(ownerships.ToArray()),
            Array.AsReadOnly(lifecycles.ToArray()),
            Array.AsReadOnly(heartbeatRetirements.ToArray()),
            Array.AsReadOnly(heartbeats.ToArray()),
            Array.AsReadOnly(failures.ToArray()));
        ValidateEntry(entry, maximumEvidenceItems);
        return entry;
    }

    private static void PreflightEvidenceBounds(JsonElement element, int maximumEvidenceItems)
    {
        var aggregate = 0;
        foreach (var property in _evidenceArrayProperties)
        {
            var array = element.GetProperty(property);
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw Invalid();
            }

            var count = array.GetArrayLength();
            if (count > maximumEvidenceItems)
            {
                throw new GovernedLoopSleepStoreLimitException();
            }

            try
            {
                aggregate = checked(aggregate + count);
            }
            catch (OverflowException)
            {
                throw new GovernedLoopSleepStoreLimitException();
            }

            if (aggregate > maximumEvidenceItems)
            {
                throw new GovernedLoopSleepStoreLimitException();
            }
        }
    }

    private static GovernedLoopCoordinatorOwnership ParseOwnership(JsonElement element)
    {
        if (!IsExactObject(element, _ownershipProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "coordinatorId", out var coordinatorId)
            || !TryString(element, "ownerId", out var ownerId)
            || !TryInt64(element, "ownershipEpoch", out var epoch)
            || !TryUtc(element, "acquiredAtUtc", out var acquiredAtUtc)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopCoordinatorOwnership(
            schemaVersion,
            coordinatorId!,
            ownerId!,
            epoch,
            acquiredAtUtc,
            contentHash!);
    }

    private static GovernedLoopCoordinatorLifecycle ParseLifecycle(
        JsonElement element,
        IReadOnlyDictionary<string, GovernedLoopCoordinatorOwnership> ownerships)
    {
        if (!IsExactObject(element, _lifecycleProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "lifecycleVersion", out var version)
            || !TryString(element, "ownershipHash", out var ownershipHash)
            || !ownerships.TryGetValue(ownershipHash!, out var ownership)
            || !TryString(element, "status", out var statusText)
            || !TryStatus(statusText, out var status)
            || !TryUtc(element, "updatedAtUtc", out var updatedAtUtc)
            || !TryNullableUtc(element, "terminalAtUtc", out var terminalAtUtc)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopCoordinatorLifecycle(
            schemaVersion,
            version,
            ownership,
            status,
            updatedAtUtc,
            terminalAtUtc,
            contentHash!);
    }

    private static GovernedLoopCoordinatorHeartbeat ParseHeartbeat(
        JsonElement element,
        IReadOnlyDictionary<string, GovernedLoopCoordinatorOwnership> ownerships)
    {
        if (!IsExactObject(element, _heartbeatProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "heartbeatSequence", out var sequence)
            || !TryString(element, "ownershipHash", out var ownershipHash)
            || !ownerships.TryGetValue(ownershipHash!, out var ownership)
            || !TryUtc(element, "recordedAtUtc", out var recordedAtUtc)
            || !TryUtc(element, "leaseExpiresAtUtc", out var leaseExpiresAtUtc)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopCoordinatorHeartbeat(
            schemaVersion,
            sequence,
            ownership,
            recordedAtUtc,
            leaseExpiresAtUtc,
            contentHash!);
    }

    private static GovernedLoopCoordinatorHeartbeatRetirement ParseHeartbeatRetirement(
        JsonElement element,
        IReadOnlyDictionary<string, GovernedLoopCoordinatorOwnership> ownerships)
    {
        if (!IsExactObject(element, _heartbeatRetirementProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "ownershipHash", out var ownershipHash)
            || !ownerships.TryGetValue(ownershipHash!, out var ownership)
            || !TryInt64(element, "retiredCount", out var retiredCount)
            || !TryString(element, "initialHeartbeatHash", out var initialHeartbeatHash)
            || !TryInt64(element, "retiredThroughSequence", out var retiredThroughSequence)
            || !TryUtc(element, "retiredThroughRecordedAtUtc", out var recordedAtUtc)
            || !TryUtc(element, "retiredThroughLeaseExpiresAtUtc", out var leaseExpiresAtUtc)
            || !TryString(element, "retiredThroughHeartbeatHash", out var heartbeatHash)
            || !TryString(element, "chainHash", out var chainHash)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopCoordinatorHeartbeatRetirement(
            schemaVersion,
            ownership,
            retiredCount,
            initialHeartbeatHash!,
            retiredThroughSequence,
            recordedAtUtc,
            leaseExpiresAtUtc,
            heartbeatHash!,
            chainHash!,
            contentHash!);
    }

    private static GovernedLoopCoordinatorFailure ParseFailure(
        JsonElement element,
        IReadOnlyDictionary<string, GovernedLoopCoordinatorOwnership> ownerships)
    {
        if (!IsExactObject(element, _failureProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "failureSequence", out var sequence)
            || !TryString(element, "ownershipHash", out var ownershipHash)
            || !ownerships.TryGetValue(ownershipHash!, out var ownership)
            || !TryString(element, "kind", out var kindText)
            || !TryFailureKind(kindText, out var kind)
            || !TryNullableString(element, "detailEvidenceReference", out var detailReference)
            || !TryUtc(element, "occurredAtUtc", out var occurredAtUtc)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopCoordinatorFailure(
            schemaVersion,
            sequence,
            ownership,
            kind,
            detailReference,
            occurredAtUtc,
            contentHash!);
    }

    private static List<T> ParseArray<T>(JsonElement element, string property, Func<JsonElement, T> parser)
    {
        var array = element.GetProperty(property);
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        return array.EnumerateArray().Select(parser).ToList();
    }

    private static void ValidateEntry(GovernedLoopCoordinatorEvidenceStoreEntry? entry, int maximumEvidenceItems)
    {
        if (entry is null
            || !GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId(entry.CoordinatorId)
            || entry.Ownerships.Count == 0
            || entry.Lifecycles.Count == 0
            || entry.Heartbeats.Count == 0
            || EvidenceCount(entry) > maximumEvidenceItems
            || entry.Ownerships.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.Ownerships.Count
            || entry.Lifecycles.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.Lifecycles.Count
            || entry.HeartbeatRetirements.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.HeartbeatRetirements.Count
            || entry.HeartbeatRetirements.Select(item => item.Ownership.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.HeartbeatRetirements.Count
            || entry.Heartbeats.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.Heartbeats.Count
            || entry.Failures.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.Failures.Count
            || !OwnershipOrderIsMonotonic(entry.Lifecycles.Select(item => item.Ownership))
            || !OwnershipOrderIsMonotonic(entry.HeartbeatRetirements.Select(item => item.Ownership))
            || !OwnershipOrderIsMonotonic(entry.Heartbeats.Select(item => item.Ownership))
            || !OwnershipOrderIsMonotonic(entry.Failures.Select(item => item.Ownership)))
        {
            throw Invalid();
        }

        for (var index = 0; index < entry.Ownerships.Count; index++)
        {
            var ownership = entry.Ownerships[index];
            if (!GovernedLoopSleepContractValidator.Validate(ownership).IsValid
                || !string.Equals(ownership.CoordinatorId, entry.CoordinatorId, StringComparison.Ordinal)
                || ownership.OwnershipEpoch != index + 1)
            {
                throw Invalid();
            }

            var lifecycles = entry.Lifecycles.Where(item => SameOwner(item.Ownership, ownership)).ToArray();
            var retirement = entry.HeartbeatRetirements.SingleOrDefault(item => SameOwner(item.Ownership, ownership));
            var heartbeats = entry.Heartbeats.Where(item => SameOwner(item.Ownership, ownership)).ToArray();
            var failures = entry.Failures.Where(item => SameOwner(item.Ownership, ownership)).ToArray();
            if (lifecycles.Length == 0
                || retirement is null && heartbeats.Length == 0
                || index == entry.Ownerships.Count - 1 && heartbeats.Length == 0
                || lifecycles[0].LifecycleVersion != 1
                || lifecycles[0].Status != GovernedLoopCoordinatorStatus.Starting
                || retirement is null && heartbeats[0].HeartbeatSequence != 1
                || retirement is not null && !RetirementIsValid(retirement)
                || retirement is not null && heartbeats.Length > 0
                    && !GovernedLoopSleepContractValidator.ValidateTransition(ToHeartbeat(retirement), heartbeats[0]).IsValid
                || !lifecycles.All(item => GovernedLoopSleepContractValidator.ValidateComposition(ownership, item).IsValid)
                || !heartbeats.All(item => GovernedLoopSleepContractValidator.ValidateComposition(ownership, item).IsValid)
                || !failures.All(item => GovernedLoopSleepContractValidator.ValidateComposition(ownership, item).IsValid)
                || !TransitionsAreValid(lifecycles, GovernedLoopSleepContractValidator.ValidateTransition)
                || !TransitionsAreValid(heartbeats, GovernedLoopSleepContractValidator.ValidateTransition)
                || !TransitionsAreValid(failures, GovernedLoopSleepContractValidator.ValidateTransition)
                || failures.Length > 0 && failures[0].FailureSequence != 1)
            {
                throw Invalid();
            }

            if (index > 0)
            {
                var previous = entry.Ownerships[index - 1];
                var previousHeartbeat = entry.Heartbeats.LastOrDefault(item => SameOwner(item.Ownership, previous))
                    ?? ToHeartbeat(entry.HeartbeatRetirements.Single(item => SameOwner(item.Ownership, previous)));
                if (!GovernedLoopSleepContractValidator.ValidateHandoff(previous, previousHeartbeat, ownership).IsValid)
                {
                    throw Invalid();
                }
            }
        }

        if (entry.Lifecycles.Any(item => !entry.Ownerships.Any(owner => SameOwner(owner, item.Ownership)))
            || entry.HeartbeatRetirements.Any(item => !entry.Ownerships.Any(owner => SameOwner(owner, item.Ownership)))
            || entry.Heartbeats.Any(item => !entry.Ownerships.Any(owner => SameOwner(owner, item.Ownership)))
            || entry.Failures.Any(item => !entry.Ownerships.Any(owner => SameOwner(owner, item.Ownership))))
        {
            throw Invalid();
        }
    }

    private static bool RetirementIsValid(GovernedLoopCoordinatorHeartbeatRetirement retirement)
    {
        var retiredThrough = ToHeartbeat(retirement);
        return retirement.SchemaVersion == SchemaVersion
            && retirement.RetiredCount == retirement.RetiredThroughSequence
            && retirement.RetiredCount > 0
            && IsHash(retirement.InitialHeartbeatHash)
            && IsHash(retirement.RetiredThroughHeartbeatHash)
            && IsHash(retirement.ChainHash)
            && GovernedLoopSleepContractValidator.ValidateComposition(retirement.Ownership, retiredThrough).IsValid
            && GovernedLoopHeartbeatRetirementStartsCorrectly(retirement)
            && GovernedLoopCoordinatorHeartbeatRetirementHash.Matches(retirement);
    }

    private static bool GovernedLoopHeartbeatRetirementStartsCorrectly(GovernedLoopCoordinatorHeartbeatRetirement retirement)
        => retirement.RetiredCount != 1
            || string.Equals(retirement.InitialHeartbeatHash, retirement.RetiredThroughHeartbeatHash, StringComparison.Ordinal);

    private static GovernedLoopCoordinatorHeartbeat ToHeartbeat(GovernedLoopCoordinatorHeartbeatRetirement retirement)
        => new(
            SchemaVersion,
            retirement.RetiredThroughSequence,
            retirement.Ownership,
            retirement.RetiredThroughRecordedAtUtc,
            retirement.RetiredThroughLeaseExpiresAtUtc,
            retirement.RetiredThroughHeartbeatHash);

    private static bool TransitionsAreValid<T>(
        IReadOnlyList<T> evidence,
        Func<T, T, GovernedLoopSleepValidationResult> validator)
        => evidence.Skip(1).Select((item, index) => validator(evidence[index], item)).All(result => result.IsValid);

    private static IReadOnlyList<GovernedLoopCoordinatorEvidenceStoreEntry> Ordered(
        IEnumerable<GovernedLoopCoordinatorEvidenceStoreEntry> entries)
        => Array.AsReadOnly(entries.OrderBy(entry => entry.CoordinatorId, StringComparer.Ordinal).ToArray());

    private static void WriteEntry(Utf8JsonWriter writer, GovernedLoopCoordinatorEvidenceStoreEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("coordinatorId", entry.CoordinatorId);
        writer.WriteStartArray("failures");
        foreach (var failure in entry.Failures)
        {
            WriteFailure(writer, failure);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("heartbeatRetirements");
        foreach (var retirement in entry.HeartbeatRetirements)
        {
            WriteHeartbeatRetirement(writer, retirement);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("heartbeats");
        foreach (var heartbeat in entry.Heartbeats)
        {
            WriteHeartbeat(writer, heartbeat);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("lifecycles");
        foreach (var lifecycle in entry.Lifecycles)
        {
            WriteLifecycle(writer, lifecycle);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("ownerships");
        foreach (var ownership in entry.Ownerships)
        {
            WriteOwnership(writer, ownership);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOwnership(Utf8JsonWriter writer, GovernedLoopCoordinatorOwnership ownership)
    {
        writer.WriteStartObject();
        WriteUtc(writer, "acquiredAtUtc", ownership.AcquiredAtUtc);
        writer.WriteString("contentHash", ownership.ContentHash);
        writer.WriteString("coordinatorId", ownership.CoordinatorId);
        writer.WriteString("ownerId", ownership.OwnerId);
        writer.WriteNumber("ownershipEpoch", ownership.OwnershipEpoch);
        writer.WriteNumber("schemaVersion", ownership.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteLifecycle(Utf8JsonWriter writer, GovernedLoopCoordinatorLifecycle lifecycle)
    {
        writer.WriteStartObject();
        writer.WriteString("contentHash", lifecycle.ContentHash);
        writer.WriteNumber("lifecycleVersion", lifecycle.LifecycleVersion);
        writer.WriteString("ownershipHash", lifecycle.Ownership.ContentHash);
        writer.WriteNumber("schemaVersion", lifecycle.SchemaVersion);
        writer.WriteString("status", Status(lifecycle.Status));
        WriteNullableUtc(writer, "terminalAtUtc", lifecycle.TerminalAtUtc);
        WriteUtc(writer, "updatedAtUtc", lifecycle.UpdatedAtUtc);
        writer.WriteEndObject();
    }

    private static void WriteHeartbeat(Utf8JsonWriter writer, GovernedLoopCoordinatorHeartbeat heartbeat)
    {
        writer.WriteStartObject();
        writer.WriteString("contentHash", heartbeat.ContentHash);
        writer.WriteNumber("heartbeatSequence", heartbeat.HeartbeatSequence);
        WriteUtc(writer, "leaseExpiresAtUtc", heartbeat.LeaseExpiresAtUtc);
        writer.WriteString("ownershipHash", heartbeat.Ownership.ContentHash);
        WriteUtc(writer, "recordedAtUtc", heartbeat.RecordedAtUtc);
        writer.WriteNumber("schemaVersion", heartbeat.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteHeartbeatRetirement(Utf8JsonWriter writer, GovernedLoopCoordinatorHeartbeatRetirement retirement)
    {
        writer.WriteStartObject();
        writer.WriteString("chainHash", retirement.ChainHash);
        writer.WriteString("contentHash", retirement.ContentHash);
        writer.WriteString("initialHeartbeatHash", retirement.InitialHeartbeatHash);
        writer.WriteString("ownershipHash", retirement.Ownership.ContentHash);
        writer.WriteNumber("retiredCount", retirement.RetiredCount);
        writer.WriteString("retiredThroughHeartbeatHash", retirement.RetiredThroughHeartbeatHash);
        WriteUtc(writer, "retiredThroughLeaseExpiresAtUtc", retirement.RetiredThroughLeaseExpiresAtUtc);
        WriteUtc(writer, "retiredThroughRecordedAtUtc", retirement.RetiredThroughRecordedAtUtc);
        writer.WriteNumber("retiredThroughSequence", retirement.RetiredThroughSequence);
        writer.WriteNumber("schemaVersion", retirement.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteFailure(Utf8JsonWriter writer, GovernedLoopCoordinatorFailure failure)
    {
        writer.WriteStartObject();
        writer.WriteString("contentHash", failure.ContentHash);
        WriteNullableString(writer, "detailEvidenceReference", failure.DetailEvidenceReference);
        writer.WriteNumber("failureSequence", failure.FailureSequence);
        writer.WriteString("kind", FailureKind(failure.Kind));
        WriteUtc(writer, "occurredAtUtc", failure.OccurredAtUtc);
        writer.WriteString("ownershipHash", failure.Ownership.ContentHash);
        writer.WriteNumber("schemaVersion", failure.SchemaVersion);
        writer.WriteEndObject();
    }

    private static bool IsExactObject(JsonElement element, IReadOnlyCollection<string> expected)
        => element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Count() == expected.Count
            && element.EnumerateObject().All(property => expected.Contains(property.Name, StringComparer.Ordinal));

    private static bool TryString(JsonElement element, string property, out string? value)
    {
        value = null;
        return element.TryGetProperty(property, out var candidate)
            && candidate.ValueKind == JsonValueKind.String
            && (value = candidate.GetString()) is not null;
    }

    private static bool TryNullableString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate))
        {
            return false;
        }

        if (candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return candidate.ValueKind == JsonValueKind.String && (value = candidate.GetString()) is not null;
    }

    private static bool TryInt32(JsonElement element, string property, out int value)
    {
        value = default;
        return element.TryGetProperty(property, out var candidate)
            && candidate.ValueKind == JsonValueKind.Number
            && candidate.TryGetInt32(out value);
    }

    private static bool TryInt64(JsonElement element, string property, out long value)
    {
        value = default;
        return element.TryGetProperty(property, out var candidate)
            && candidate.ValueKind == JsonValueKind.Number
            && candidate.TryGetInt64(out value);
    }

    private static bool TryUtc(JsonElement element, string property, out DateTimeOffset value)
    {
        value = default;
        return TryString(element, property, out var text)
            && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            && value.Offset == TimeSpan.Zero
            && string.Equals(text, Utc(value), StringComparison.Ordinal);
    }

    private static bool TryNullableUtc(JsonElement element, string property, out DateTimeOffset? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var candidate))
        {
            return false;
        }

        if (candidate.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (candidate.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(candidate.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !string.Equals(candidate.GetString(), Utc(parsed), StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(property);
        }
        else
        {
            writer.WriteString(property, value);
        }
    }

    private static void WriteUtc(Utf8JsonWriter writer, string property, DateTimeOffset value)
        => writer.WriteString(property, Utc(value));

    private static void WriteNullableUtc(Utf8JsonWriter writer, string property, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(property);
        }
        else
        {
            WriteUtc(writer, property, value.Value);
        }
    }

    private static string Utc(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static string Status(GovernedLoopCoordinatorStatus status)
        => status switch
        {
            GovernedLoopCoordinatorStatus.Starting => "starting",
            GovernedLoopCoordinatorStatus.Running => "running",
            GovernedLoopCoordinatorStatus.Stopping => "stopping",
            GovernedLoopCoordinatorStatus.Stopped => "stopped",
            GovernedLoopCoordinatorStatus.Failed => "failed",
            _ => throw Invalid()
        };

    private static bool TryStatus(string? value, out GovernedLoopCoordinatorStatus status)
    {
        status = value switch
        {
            "starting" => GovernedLoopCoordinatorStatus.Starting,
            "running" => GovernedLoopCoordinatorStatus.Running,
            "stopping" => GovernedLoopCoordinatorStatus.Stopping,
            "stopped" => GovernedLoopCoordinatorStatus.Stopped,
            "failed" => GovernedLoopCoordinatorStatus.Failed,
            _ => 0
        };
        return Enum.IsDefined(status);
    }

    private static string FailureKind(GovernedLoopCoordinatorFailureKind kind)
        => kind switch
        {
            GovernedLoopCoordinatorFailureKind.OwnershipLost => "ownership-lost",
            GovernedLoopCoordinatorFailureKind.HeartbeatExpired => "heartbeat-expired",
            GovernedLoopCoordinatorFailureKind.StoreUnavailable => "store-unavailable",
            GovernedLoopCoordinatorFailureKind.CorruptState => "corrupt-state",
            GovernedLoopCoordinatorFailureKind.Backpressured => "backpressured",
            GovernedLoopCoordinatorFailureKind.ShutdownInterrupted => "shutdown-interrupted",
            GovernedLoopCoordinatorFailureKind.Unexpected => "unexpected",
            _ => throw Invalid()
        };

    private static bool TryFailureKind(string? value, out GovernedLoopCoordinatorFailureKind kind)
    {
        kind = value switch
        {
            "ownership-lost" => GovernedLoopCoordinatorFailureKind.OwnershipLost,
            "heartbeat-expired" => GovernedLoopCoordinatorFailureKind.HeartbeatExpired,
            "store-unavailable" => GovernedLoopCoordinatorFailureKind.StoreUnavailable,
            "corrupt-state" => GovernedLoopCoordinatorFailureKind.CorruptState,
            "backpressured" => GovernedLoopCoordinatorFailureKind.Backpressured,
            "shutdown-interrupted" => GovernedLoopCoordinatorFailureKind.ShutdownInterrupted,
            "unexpected" => GovernedLoopCoordinatorFailureKind.Unexpected,
            _ => 0
        };
        return Enum.IsDefined(kind);
    }

    private static bool SameOwner(GovernedLoopCoordinatorOwnership first, GovernedLoopCoordinatorOwnership second)
        => string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal);

    private static bool OwnershipOrderIsMonotonic(IEnumerable<GovernedLoopCoordinatorOwnership> ownerships)
    {
        long previousEpoch = 0;
        foreach (var ownership in ownerships)
        {
            if (ownership.OwnershipEpoch < previousEpoch)
            {
                return false;
            }

            previousEpoch = ownership.OwnershipEpoch;
        }

        return true;
    }

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopSleepContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int EvidenceCount(GovernedLoopCoordinatorEvidenceStoreEntry entry)
        => checked(entry.Ownerships.Count + entry.Lifecycles.Count + entry.HeartbeatRetirements.Count + entry.Heartbeats.Count + entry.Failures.Count);

    private static FormatException Invalid(Exception? innerException = null)
        => new("The coordinator ledger is not the exact canonical bounded schema-version-1 document.", innerException);
}
