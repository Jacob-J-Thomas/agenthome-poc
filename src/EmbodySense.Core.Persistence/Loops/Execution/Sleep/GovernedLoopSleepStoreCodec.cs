using System.Buffers;
using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

internal static class GovernedLoopSleepStoreCodec
{
    internal const int MaximumWakeEvidenceItems = 3;
    internal const int MaximumGenerationDigits = 19;
    private const int SchemaVersion = 1;
    private const int MaximumDepth = 16;
    private static readonly string[] _catalogProperties = ["entries", "generation", "schemaVersion"];
    private static readonly string[] _entryProperties = ["checkpoint", "publicationPostureHash", "wakeClaimPostureHash", "wakeEvidence"];
    private static readonly string[] _checkpointProperties = ["authenticatedEventReference", "binding", "checkpointId", "contentHash", "publishedAtUtc", "schemaVersion", "wakeDeadlineUtc", "wakeMode"];
    private static readonly string[] _bindingProperties = ["activationOrdinal", "cycleId", "cycleIteration", "execution", "frontierHash", "frontierVersion", "nodeId", "nodeVisitOrdinal", "publication", "waitAttempt", "waitOperationId"];
    private static readonly string[] _executionProperties = ["executionGeneration", "revision", "runId", "schemaVersion"];
    private static readonly string[] _revisionProperties = ["executableHash", "graphId", "revisionId", "schemaVersion"];
    private static readonly string[] _publicationProperties = ["publicationOperationId", "revision", "schemaVersion", "validationEvidenceHash"];
    private static readonly string[] _wakeProperties = ["continuationEvidenceHash", "continuationOperationId", "contentHash", "disposition", "dispositionEvidenceReference", "evidenceVersion", "identity", "recordedAtUtc", "schemaVersion"];
    private static readonly string[] _identityProperties = ["authenticatedEventReference", "authenticationEvidenceHash", "checkpointHash", "checkpointId", "contentHash", "schemaVersion", "wakeId", "wakeMode"];

    public static byte[] Serialize(GovernedLoopSleepStoreCatalog catalog, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != SchemaVersion || catalog.Generation < 1 || catalog.Entries.Count == 0)
        {
            throw Invalid();
        }

        var checkpointIds = new HashSet<string>(StringComparer.Ordinal);
        var wakeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            ValidateEntry(entry);
            if (!checkpointIds.Add(entry.Checkpoint.CheckpointId)
                || entry.WakeEvidence.Count > 0 && !wakeIds.Add(entry.WakeEvidence[0].Identity.WakeId))
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

        var reservedBytes = catalog.Entries.Sum(ReservedWakeUtf8Bytes);
        var generationReservation = MaximumGenerationDigits - catalog.Generation.ToString(CultureInfo.InvariantCulture).Length;
        if (checked((long)buffer.WrittenCount + reservedBytes + generationReservation) > maximumBytes)
        {
            throw new GovernedLoopSleepStoreLimitException();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static GovernedLoopSleepStoreCatalog Deserialize(byte[] bytes, int maximumCheckpoints, int maximumBytes)
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

            if (entriesElement.GetArrayLength() > maximumCheckpoints)
            {
                throw new GovernedLoopSleepStoreLimitException();
            }

            var entries = new List<GovernedLoopSleepStoreEntry>(entriesElement.GetArrayLength());
            var checkpointIds = new HashSet<string>(StringComparer.Ordinal);
            var wakeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in entriesElement.EnumerateArray())
            {
                var entry = ParseEntry(element);
                if (!checkpointIds.Add(entry.Checkpoint.CheckpointId)
                    || entry.WakeEvidence.Count > 0 && !wakeIds.Add(entry.WakeEvidence[0].Identity.WakeId))
                {
                    throw Invalid();
                }

                entries.Add(entry);
            }

            var catalog = new GovernedLoopSleepStoreCatalog(schemaVersion, generation, Ordered(entries));
            if (!bytes.AsSpan().SequenceEqual(Serialize(catalog, maximumBytes)))
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

    private static GovernedLoopSleepStoreEntry ParseEntry(JsonElement element)
    {
        if (!IsExactObject(element, _entryProperties)
            || !TryString(element, "publicationPostureHash", out var postureHash)
            || !TryNullableString(element, "wakeClaimPostureHash", out var wakeClaimPostureHash))
        {
            throw Invalid();
        }

        var checkpoint = ParseCheckpoint(element.GetProperty("checkpoint"));
        var wakeEvidenceElement = element.GetProperty("wakeEvidence");
        if (wakeEvidenceElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        if (wakeEvidenceElement.GetArrayLength() > MaximumWakeEvidenceItems)
        {
            throw new GovernedLoopSleepStoreLimitException();
        }

        var wakeEvidence = Array.AsReadOnly(wakeEvidenceElement.EnumerateArray().Select(ParseWake).ToArray());
        var entry = new GovernedLoopSleepStoreEntry(checkpoint, postureHash!, wakeClaimPostureHash, wakeEvidence);
        ValidateEntry(entry);
        return entry;
    }

    private static GovernedLoopSleepCheckpoint ParseCheckpoint(JsonElement element)
    {
        if (!IsExactObject(element, _checkpointProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "checkpointId", out var checkpointId)
            || !TryNullableString(element, "authenticatedEventReference", out var eventReference)
            || !TryNullableUtc(element, "wakeDeadlineUtc", out var wakeDeadlineUtc)
            || !TryString(element, "wakeMode", out var wakeModeText)
            || !TryWakeMode(wakeModeText, out var wakeMode)
            || !TryUtc(element, "publishedAtUtc", out var publishedAtUtc)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopSleepCheckpoint(
            schemaVersion,
            checkpointId!,
            ParseBinding(element.GetProperty("binding")),
            wakeMode,
            wakeDeadlineUtc,
            eventReference,
            publishedAtUtc,
            contentHash!);
    }

    private static GovernedLoopSleepBinding ParseBinding(JsonElement element)
    {
        if (!IsExactObject(element, _bindingProperties)
            || !TryInt64(element, "frontierVersion", out var frontierVersion)
            || !TryString(element, "frontierHash", out var frontierHash)
            || !TryInt32(element, "activationOrdinal", out var activationOrdinal)
            || !TryNullableString(element, "cycleId", out var cycleId)
            || !TryNullableInt32(element, "cycleIteration", out var cycleIteration)
            || !TryString(element, "nodeId", out var nodeId)
            || !TryInt32(element, "nodeVisitOrdinal", out var nodeVisitOrdinal)
            || !TryInt32(element, "waitAttempt", out var waitAttempt)
            || !TryString(element, "waitOperationId", out var waitOperationId))
        {
            throw Invalid();
        }

        return new GovernedLoopSleepBinding(
            ParseExecution(element.GetProperty("execution")),
            ParsePublication(element.GetProperty("publication")),
            frontierVersion,
            frontierHash!,
            activationOrdinal,
            cycleId,
            cycleIteration,
            nodeId!,
            nodeVisitOrdinal,
            waitAttempt,
            waitOperationId!);
    }

    private static GovernedLoopExecutionBinding ParseExecution(JsonElement element)
    {
        if (!IsExactObject(element, _executionProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "runId", out var runId)
            || !TryInt64(element, "executionGeneration", out var generation))
        {
            throw Invalid();
        }

        return GovernedLoopExecutionBinding.Create(schemaVersion, runId!, ParseRevision(element.GetProperty("revision")), generation);
    }

    private static GovernedLoopRevisionPublicationPin ParsePublication(JsonElement element)
    {
        if (!IsExactObject(element, _publicationProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "publicationOperationId", out var operationId)
            || !TryString(element, "validationEvidenceHash", out var validationHash))
        {
            throw Invalid();
        }

        return new GovernedLoopRevisionPublicationPin(schemaVersion, ParseRevision(element.GetProperty("revision")), operationId!, validationHash!);
    }

    private static GovernedLoopRevisionReference ParseRevision(JsonElement element)
    {
        if (!IsExactObject(element, _revisionProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "graphId", out var graphId)
            || !TryString(element, "revisionId", out var revisionId)
            || !TryString(element, "executableHash", out var executableHash))
        {
            throw Invalid();
        }

        return GovernedLoopRevisionReference.Create(schemaVersion, graphId!, revisionId!, executableHash!);
    }

    private static GovernedLoopWakeEvidence ParseWake(JsonElement element)
    {
        if (!IsExactObject(element, _wakeProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryInt64(element, "evidenceVersion", out var evidenceVersion)
            || !TryString(element, "disposition", out var dispositionText)
            || !TryWakeDisposition(dispositionText, out var disposition)
            || !TryNullableString(element, "continuationOperationId", out var continuationOperationId)
            || !TryNullableString(element, "continuationEvidenceHash", out var continuationEvidenceHash)
            || !TryNullableString(element, "dispositionEvidenceReference", out var dispositionEvidenceReference)
            || !TryUtc(element, "recordedAtUtc", out var recordedAtUtc)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopWakeEvidence(
            schemaVersion,
            evidenceVersion,
            ParseIdentity(element.GetProperty("identity")),
            disposition,
            continuationOperationId,
            continuationEvidenceHash,
            dispositionEvidenceReference,
            recordedAtUtc,
            contentHash!);
    }

    private static GovernedLoopWakeIdentity ParseIdentity(JsonElement element)
    {
        if (!IsExactObject(element, _identityProperties)
            || !TryInt32(element, "schemaVersion", out var schemaVersion)
            || !TryString(element, "wakeId", out var wakeId)
            || !TryString(element, "checkpointId", out var checkpointId)
            || !TryString(element, "checkpointHash", out var checkpointHash)
            || !TryString(element, "wakeMode", out var wakeModeText)
            || !TryWakeMode(wakeModeText, out var wakeMode)
            || !TryNullableString(element, "authenticatedEventReference", out var eventReference)
            || !TryNullableString(element, "authenticationEvidenceHash", out var authenticationEvidenceHash)
            || !TryString(element, "contentHash", out var contentHash))
        {
            throw Invalid();
        }

        return new GovernedLoopWakeIdentity(
            schemaVersion,
            wakeId!,
            checkpointId!,
            checkpointHash!,
            wakeMode,
            eventReference,
            authenticationEvidenceHash,
            contentHash!);
    }

    private static void ValidateEntry(GovernedLoopSleepStoreEntry? entry)
    {
        if (entry is null
            || !IsHash(entry.PublicationPostureHash)
            || !GovernedLoopSleepContractValidator.Validate(entry.Checkpoint).IsValid
            || entry.WakeEvidence is null
            || entry.WakeEvidence.Count > MaximumWakeEvidenceItems
            || (entry.WakeEvidence.Count == 0) != (entry.WakeClaimPostureHash is null)
            || entry.WakeClaimPostureHash is not null && !IsHash(entry.WakeClaimPostureHash)
            || entry.WakeEvidence.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != entry.WakeEvidence.Count
            || entry.WakeEvidence.Count > 0 && entry.WakeEvidence[0].EvidenceVersion != 1
            || entry.WakeEvidence.Any(item => !GovernedLoopSleepContractValidator.ValidateComposition(entry.Checkpoint, item).IsValid)
            || !TransitionsAreValid(entry.WakeEvidence))
        {
            throw Invalid();
        }
    }

    private static bool TransitionsAreValid(IReadOnlyList<GovernedLoopWakeEvidence> evidence)
        => evidence.Skip(1)
            .Select((item, index) => GovernedLoopSleepContractValidator.ValidateTransition(evidence[index], item))
            .All(result => result.IsValid);

    private static IReadOnlyList<GovernedLoopSleepStoreEntry> Ordered(IEnumerable<GovernedLoopSleepStoreEntry> entries)
        => Array.AsReadOnly(entries.OrderBy(entry => entry.Checkpoint.CheckpointId, StringComparer.Ordinal).ToArray());

    private static void WriteEntry(Utf8JsonWriter writer, GovernedLoopSleepStoreEntry entry)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("checkpoint");
        WriteCheckpoint(writer, entry.Checkpoint);
        writer.WriteString("publicationPostureHash", entry.PublicationPostureHash);
        WriteNullableString(writer, "wakeClaimPostureHash", entry.WakeClaimPostureHash);
        writer.WriteStartArray("wakeEvidence");
        foreach (var evidence in entry.WakeEvidence)
        {
            WriteWake(writer, evidence);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static int ReservedWakeUtf8Bytes(GovernedLoopSleepStoreEntry entry)
    {
        var actualBytes = SerializedEntryUtf8Bytes(entry);
        var maximumBytes = SerializedEntryUtf8Bytes(MaximumWakeEntry(entry));
        return Math.Max(0, maximumBytes - actualBytes);
    }

    private static int SerializedEntryUtf8Bytes(GovernedLoopSleepStoreEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteEntry(writer, entry);
        writer.Flush();
        return buffer.WrittenCount;
    }

    private static GovernedLoopSleepStoreEntry MaximumWakeEntry(GovernedLoopSleepStoreEntry entry)
    {
        var maximumIdentifier = new string('z', GovernedLoopSleepContractLimits.MaxIdentifierCharacters);
        var maximumEvidenceReference = new string('z', GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters);
        var maximumHash = new string('f', GovernedLoopSleepContractLimits.Sha256HexCharacters);
        var identity = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            SchemaVersion,
            string.Empty,
            entry.Checkpoint.CheckpointId,
            entry.Checkpoint.ContentHash,
            entry.Checkpoint.WakeMode,
            entry.Checkpoint.AuthenticatedEventReference,
            entry.Checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent ? maximumHash : null,
            string.Empty));
        var recordedAtUtc = DateTimeOffset.MaxValue;
        var prepared = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            SchemaVersion,
            1,
            identity,
            GovernedLoopWakeDisposition.Prepared,
            maximumIdentifier,
            null,
            null,
            recordedAtUtc,
            string.Empty));
        var ambiguous = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            SchemaVersion,
            2,
            identity,
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            maximumIdentifier,
            null,
            maximumEvidenceReference,
            recordedAtUtc,
            string.Empty));
        var committed = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            SchemaVersion,
            3,
            identity,
            GovernedLoopWakeDisposition.Committed,
            maximumIdentifier,
            maximumHash,
            null,
            recordedAtUtc,
            string.Empty));
        return entry with
        {
            WakeClaimPostureHash = maximumHash,
            WakeEvidence = Array.AsReadOnly([prepared, ambiguous, committed])
        };
    }

    private static void WriteCheckpoint(Utf8JsonWriter writer, GovernedLoopSleepCheckpoint checkpoint)
    {
        writer.WriteStartObject();
        WriteNullableString(writer, "authenticatedEventReference", checkpoint.AuthenticatedEventReference);
        writer.WritePropertyName("binding");
        WriteBinding(writer, checkpoint.Binding);
        writer.WriteString("checkpointId", checkpoint.CheckpointId);
        writer.WriteString("contentHash", checkpoint.ContentHash);
        WriteUtc(writer, "publishedAtUtc", checkpoint.PublishedAtUtc);
        writer.WriteNumber("schemaVersion", checkpoint.SchemaVersion);
        WriteNullableUtc(writer, "wakeDeadlineUtc", checkpoint.WakeDeadlineUtc);
        writer.WriteString("wakeMode", WakeMode(checkpoint.WakeMode));
        writer.WriteEndObject();
    }

    private static void WriteBinding(Utf8JsonWriter writer, GovernedLoopSleepBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteNumber("activationOrdinal", binding.ActivationOrdinal);
        WriteNullableString(writer, "cycleId", binding.CycleId);
        WriteNullableInt32(writer, "cycleIteration", binding.CycleIteration);
        writer.WritePropertyName("execution");
        WriteExecution(writer, binding.Execution);
        writer.WriteString("frontierHash", binding.FrontierHash);
        writer.WriteNumber("frontierVersion", binding.FrontierVersion);
        writer.WriteString("nodeId", binding.NodeId);
        writer.WriteNumber("nodeVisitOrdinal", binding.NodeVisitOrdinal);
        writer.WritePropertyName("publication");
        WritePublication(writer, binding.Publication);
        writer.WriteNumber("waitAttempt", binding.WaitAttempt);
        writer.WriteString("waitOperationId", binding.WaitOperationId);
        writer.WriteEndObject();
    }

    private static void WriteExecution(Utf8JsonWriter writer, GovernedLoopExecutionBinding execution)
    {
        writer.WriteStartObject();
        writer.WriteNumber("executionGeneration", execution.ExecutionGeneration);
        writer.WritePropertyName("revision");
        WriteRevision(writer, execution.Revision);
        writer.WriteString("runId", execution.RunId);
        writer.WriteNumber("schemaVersion", execution.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WritePublication(Utf8JsonWriter writer, GovernedLoopRevisionPublicationPin publication)
    {
        writer.WriteStartObject();
        writer.WriteString("publicationOperationId", publication.PublicationOperationId);
        writer.WritePropertyName("revision");
        WriteRevision(writer, publication.Revision);
        writer.WriteNumber("schemaVersion", publication.SchemaVersion);
        writer.WriteString("validationEvidenceHash", publication.ValidationEvidenceHash);
        writer.WriteEndObject();
    }

    private static void WriteRevision(Utf8JsonWriter writer, GovernedLoopRevisionReference revision)
    {
        writer.WriteStartObject();
        writer.WriteString("executableHash", revision.ExecutableHash);
        writer.WriteString("graphId", revision.GraphId);
        writer.WriteString("revisionId", revision.RevisionId);
        writer.WriteNumber("schemaVersion", revision.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteWake(Utf8JsonWriter writer, GovernedLoopWakeEvidence evidence)
    {
        writer.WriteStartObject();
        WriteNullableString(writer, "continuationEvidenceHash", evidence.ContinuationEvidenceHash);
        WriteNullableString(writer, "continuationOperationId", evidence.ContinuationOperationId);
        writer.WriteString("contentHash", evidence.ContentHash);
        writer.WriteString("disposition", WakeDisposition(evidence.Disposition));
        WriteNullableString(writer, "dispositionEvidenceReference", evidence.DispositionEvidenceReference);
        writer.WriteNumber("evidenceVersion", evidence.EvidenceVersion);
        writer.WritePropertyName("identity");
        WriteIdentity(writer, evidence.Identity);
        WriteUtc(writer, "recordedAtUtc", evidence.RecordedAtUtc);
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, GovernedLoopWakeIdentity identity)
    {
        writer.WriteStartObject();
        WriteNullableString(writer, "authenticatedEventReference", identity.AuthenticatedEventReference);
        WriteNullableString(writer, "authenticationEvidenceHash", identity.AuthenticationEvidenceHash);
        writer.WriteString("checkpointHash", identity.CheckpointHash);
        writer.WriteString("checkpointId", identity.CheckpointId);
        writer.WriteString("contentHash", identity.ContentHash);
        writer.WriteNumber("schemaVersion", identity.SchemaVersion);
        writer.WriteString("wakeId", identity.WakeId);
        writer.WriteString("wakeMode", WakeMode(identity.WakeMode));
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

    private static bool TryNullableInt32(JsonElement element, string property, out int? value)
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

        if (candidate.ValueKind != JsonValueKind.Number || !candidate.TryGetInt32(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
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

    private static void WriteNullableInt32(Utf8JsonWriter writer, string property, int? value)
    {
        if (value is null)
        {
            writer.WriteNull(property);
        }
        else
        {
            writer.WriteNumber(property, value.Value);
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

    private static string WakeMode(GovernedLoopWakeMode mode)
        => mode switch
        {
            GovernedLoopWakeMode.Timestamp => "timestamp",
            GovernedLoopWakeMode.AuthenticatedEvent => "authenticated-event",
            _ => throw Invalid()
        };

    private static bool TryWakeMode(string? value, out GovernedLoopWakeMode mode)
    {
        mode = value switch
        {
            "timestamp" => GovernedLoopWakeMode.Timestamp,
            "authenticated-event" => GovernedLoopWakeMode.AuthenticatedEvent,
            _ => 0
        };
        return Enum.IsDefined(mode);
    }

    private static string WakeDisposition(GovernedLoopWakeDisposition disposition)
        => disposition switch
        {
            GovernedLoopWakeDisposition.Prepared => "prepared",
            GovernedLoopWakeDisposition.Committed => "committed",
            GovernedLoopWakeDisposition.Duplicate => "duplicate",
            GovernedLoopWakeDisposition.Late => "late",
            GovernedLoopWakeDisposition.Stale => "stale",
            GovernedLoopWakeDisposition.Conflict => "conflict",
            GovernedLoopWakeDisposition.Cancelled => "cancelled",
            GovernedLoopWakeDisposition.Expired => "expired",
            GovernedLoopWakeDisposition.Paused => "paused",
            GovernedLoopWakeDisposition.ReviewBlocked => "review-blocked",
            GovernedLoopWakeDisposition.AmbiguousAttempt => "ambiguous-attempt",
            GovernedLoopWakeDisposition.Failed => "failed",
            _ => throw Invalid()
        };

    private static bool TryWakeDisposition(string? value, out GovernedLoopWakeDisposition disposition)
    {
        disposition = value switch
        {
            "prepared" => GovernedLoopWakeDisposition.Prepared,
            "committed" => GovernedLoopWakeDisposition.Committed,
            "duplicate" => GovernedLoopWakeDisposition.Duplicate,
            "late" => GovernedLoopWakeDisposition.Late,
            "stale" => GovernedLoopWakeDisposition.Stale,
            "conflict" => GovernedLoopWakeDisposition.Conflict,
            "cancelled" => GovernedLoopWakeDisposition.Cancelled,
            "expired" => GovernedLoopWakeDisposition.Expired,
            "paused" => GovernedLoopWakeDisposition.Paused,
            "review-blocked" => GovernedLoopWakeDisposition.ReviewBlocked,
            "ambiguous-attempt" => GovernedLoopWakeDisposition.AmbiguousAttempt,
            "failed" => GovernedLoopWakeDisposition.Failed,
            _ => 0
        };
        return Enum.IsDefined(disposition);
    }

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopSleepContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static FormatException Invalid(Exception? innerException = null)
        => new("The sleep ledger is not the exact canonical bounded schema-version-1 document.", innerException);
}
