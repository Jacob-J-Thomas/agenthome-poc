using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Persistence.Triggers.Models;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Reads and writes only the exact canonical schema-version-1 trigger queue ledger.</summary>
internal static class TriggerQueueLedgerCodec
{
    private static readonly string[] _rootProperties = ["entries", "generation", "lastWorkerObservedAtUtc", "previousGenerationHash", "quota", "schemaVersion"];
    private static readonly string[] _quotaProperties = ["maxDurabilityTombstones", "maxEntryBytes", "maxQueuedBytes", "maxQueuedEntries", "maxQueuedEntriesPerLoop", "maxRetainedBytes", "maxRetainedEntries"];
    private static readonly string[] _entryProperties = ["admissionReason", "admissionStatus", "canonicalEnvelope", "canonicalEnvelopeHash", "dispatchAuthorityEvidenceHash", "dispatchDetail", "dispatchIntentRecordedAtUtc", "dispatchOperationId", "dispatchOutcome", "dispatchOutcomeRecordedAtUtc", "dispatchRequestHash", "governedAdmissionRequestHash", "governedDefinitionHash", "governedDefinitionVersion", "governedLoopId", "governedOperationId", "governedRunId", "leaseAcquiredAtUtc", "leaseExpiresAtUtc", "leaseGeneration", "leaseReleasedAtUtc", "leaseRenewalCount", "leaseWorkerId", "priority", "receiptRecordedAtUtc", "receiptReplayBindingHash", "receiptSchemaVersion", "recordedAtUtc", "revision", "state", "terminalAtUtc", "terminalReason"];
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Serializes one already validated ledger.</summary>
    public static byte[] Serialize(TriggerQueueLedger ledger)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (var entry in ledger.Entries.OrderBy(item => item.Envelope.DeliveryId.Value, StringComparer.Ordinal))
        {
            WriteEntry(writer, entry);
        }

        writer.WriteEndArray();
        writer.WriteNumber("generation", ledger.Generation);
        WriteTimestamp(writer, "lastWorkerObservedAtUtc", ledger.LastWorkerObservedAtUtc);
        WriteNullableString(writer, "previousGenerationHash", ledger.PreviousGenerationHash);
        writer.WritePropertyName("quota");
        writer.WriteStartObject();
        writer.WriteNumber("maxDurabilityTombstones", ledger.Quota.MaxDurabilityTombstones);
        writer.WriteNumber("maxEntryBytes", ledger.Quota.MaxEntryBytes);
        writer.WriteNumber("maxQueuedBytes", ledger.Quota.MaxQueuedBytes);
        writer.WriteNumber("maxQueuedEntries", ledger.Quota.MaxQueuedEntries);
        writer.WriteNumber("maxQueuedEntriesPerLoop", ledger.Quota.MaxQueuedEntriesPerLoop);
        writer.WriteNumber("maxRetainedBytes", ledger.Quota.MaxRetainedBytes);
        writer.WriteNumber("maxRetainedEntries", ledger.Quota.MaxRetainedEntries);
        writer.WriteEndObject();
        writer.WriteNumber("schemaVersion", TriggerQueueSnapshot.CurrentSchemaVersion);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Measures the exact canonical serialized byte length of one ledger entry.</summary>
    public static int MeasureEntry(TriggerQueueLedgerEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteEntry(writer, entry);
        writer.Flush();
        return buffer.WrittenCount;
    }

    /// <summary>Parses a byte-for-byte canonical ledger and preflights all retained bounds before entry materialization.</summary>
    public static TriggerQueueLedger Deserialize(byte[] content, TriggerQueueQuota expectedQuota)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expectedQuota);
        string json;
        try
        {
            json = _strictUtf8.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatException("Trigger queue ledger is not valid UTF-8.", exception);
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var root = document.RootElement;
            if (!IsExactObject(root, _rootProperties)
                || !TryInt(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != TriggerQueueSnapshot.CurrentSchemaVersion
                || !TryLong(root, "generation", out var generation)
                || generation < 1
                || !TryTimestamp(root, "lastWorkerObservedAtUtc", nullable: true, out var lastWorkerObservedAtUtc)
                || !TryNullableHash(root, "previousGenerationHash", out var previousGenerationHash)
                || generation == 1 != (previousGenerationHash is null)
                || !TryQuota(root.GetProperty("quota"), out var quota)
                || quota != expectedQuota)
            {
                throw Invalid();
            }

            var array = root.GetProperty("entries");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > quota.MaxRetainedEntries)
            {
                throw Invalid();
            }

            long retainedBytes = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (!IsExactObject(element, _entryProperties) || !TryString(element, "canonicalEnvelope", out var canonicalEnvelope))
                {
                    throw Invalid();
                }

                var bytes = Encoding.UTF8.GetByteCount(element.GetRawText());
                retainedBytes = checked(retainedBytes + bytes);
                if (bytes > quota.MaxEntryBytes || retainedBytes > quota.MaxRetainedBytes)
                {
                    throw Invalid();
                }
            }

            var entries = new List<TriggerQueueLedgerEntry>(array.GetArrayLength());
            foreach (var element in array.EnumerateArray())
            {
                entries.Add(ReadEntry(element));
            }

            var ledger = new TriggerQueueLedger(generation, previousGenerationHash, lastWorkerObservedAtUtc, quota, entries);
            if (!content.AsSpan().SequenceEqual(Serialize(ledger)))
            {
                throw Invalid();
            }

            return ledger;
        }
        catch (Exception exception) when (exception is JsonException or OverflowException or InvalidOperationException or FormatException && exception.Message != "Trigger queue ledger is not valid UTF-8.")
        {
            throw new FormatException("Trigger queue ledger is malformed, noncanonical, unsupported, or violates configured bounds.", exception);
        }
    }

    private static TriggerQueueLedgerEntry ReadEntry(JsonElement element)
    {
        if (!TryString(element, "canonicalEnvelope", out var canonicalEnvelope)
            || !TriggerDeliveryJson.TryDeserialize(canonicalEnvelope, out var envelope, out _)
            || !TryString(element, "canonicalEnvelopeHash", out var canonicalEnvelopeHash)
            || !TriggerDeliveryHash.TryCompute(envelope, out var computedHash, out _)
            || !string.Equals(canonicalEnvelopeHash, computedHash, StringComparison.Ordinal)
            || !TryEnum<TriggerAdmissionStatus>(element, "admissionStatus", out var admissionStatus)
            || !TryEnum<TriggerAdmissionReason>(element, "admissionReason", out var admissionReason)
            || !TryEnum<TriggerQueuePriority>(element, "priority", out var priority)
            || !TryEnum<TriggerQueueEntryState>(element, "state", out var state)
            || !TryEnum<TriggerQueueTerminalReason>(element, "terminalReason", out var terminalReason)
            || !TryLong(element, "revision", out var revision)
            || revision < 1
            || !TryTimestamp(element, "recordedAtUtc", nullable: false, out var recordedAtUtc)
            || !TryTimestamp(element, "terminalAtUtc", nullable: true, out var terminalAtUtc)
            || !TryTimestamp(element, "receiptRecordedAtUtc", nullable: true, out var receiptRecordedAtUtc)
            || !TryTimestamp(element, "leaseAcquiredAtUtc", nullable: true, out var leaseAcquiredAtUtc)
            || !TryTimestamp(element, "leaseExpiresAtUtc", nullable: true, out var leaseExpiresAtUtc)
            || !TryTimestamp(element, "leaseReleasedAtUtc", nullable: true, out var leaseReleasedAtUtc)
            || !TryTimestamp(element, "dispatchIntentRecordedAtUtc", nullable: true, out var dispatchIntentRecordedAtUtc)
            || !TryTimestamp(element, "dispatchOutcomeRecordedAtUtc", nullable: true, out var dispatchOutcomeRecordedAtUtc))
        {
            throw Invalid();
        }

        TriggerDeliveryAdmissionReceipt? receipt = null;
        var receiptSchema = element.GetProperty("receiptSchemaVersion");
        var receiptBinding = element.GetProperty("receiptReplayBindingHash");
        if (receiptSchema.ValueKind == JsonValueKind.Null && receiptBinding.ValueKind == JsonValueKind.Null && receiptRecordedAtUtc is null)
        {
            if (!IsReceiptlessOutcome(admissionStatus, admissionReason))
            {
                throw Invalid();
            }
        }
        else
        {
            if (!receiptSchema.TryGetInt32(out var schema) || receiptBinding.ValueKind != JsonValueKind.String || receiptRecordedAtUtc is null)
            {
                throw Invalid();
            }

            receipt = new TriggerDeliveryAdmissionReceipt(schema, envelope!.DeliveryId, envelope.DeduplicationId, canonicalEnvelopeHash!, receiptBinding.GetString()!, admissionStatus, admissionReason, receiptRecordedAtUtc.Value);
            if (!TriggerDeliveryAdmissionReceiptFactory.Validate(receipt, envelope).IsValid)
            {
                throw Invalid();
            }
        }

        TriggerWorkerLease? lease = null;
        var leaseWorkerId = element.GetProperty("leaseWorkerId");
        var leaseGeneration = element.GetProperty("leaseGeneration");
        var leaseRenewalCount = element.GetProperty("leaseRenewalCount");
        if (leaseWorkerId.ValueKind == JsonValueKind.Null && leaseGeneration.ValueKind == JsonValueKind.Null && leaseRenewalCount.ValueKind == JsonValueKind.Null && leaseAcquiredAtUtc is null && leaseExpiresAtUtc is null && leaseReleasedAtUtc is null)
        {
        }
        else if (leaseWorkerId.ValueKind == JsonValueKind.String && leaseGeneration.TryGetInt64(out var parsedLeaseGeneration) && leaseRenewalCount.TryGetInt32(out var parsedRenewalCount) && leaseAcquiredAtUtc is not null && leaseExpiresAtUtc is not null)
        {
            lease = new TriggerWorkerLease(leaseWorkerId.GetString()!, parsedLeaseGeneration, leaseAcquiredAtUtc.Value, leaseExpiresAtUtc.Value, parsedRenewalCount, leaseReleasedAtUtc);
        }
        else
        {
            throw Invalid();
        }

        TriggerDispatchEvidence? dispatch = null;
        var dispatchOperationId = element.GetProperty("dispatchOperationId");
        var dispatchRequestHash = element.GetProperty("dispatchRequestHash");
        var dispatchAuthorityEvidenceHash = element.GetProperty("dispatchAuthorityEvidenceHash");
        var dispatchOutcome = element.GetProperty("dispatchOutcome");
        var dispatchDetail = element.GetProperty("dispatchDetail");
        if (dispatchOperationId.ValueKind == JsonValueKind.Null && dispatchRequestHash.ValueKind == JsonValueKind.Null && dispatchAuthorityEvidenceHash.ValueKind == JsonValueKind.Null && dispatchOutcome.ValueKind == JsonValueKind.Null && dispatchDetail.ValueKind == JsonValueKind.Null && dispatchIntentRecordedAtUtc is null && dispatchOutcomeRecordedAtUtc is null)
        {
        }
        else if (dispatchOperationId.ValueKind == JsonValueKind.String
            && dispatchRequestHash.ValueKind == JsonValueKind.String
            && dispatchAuthorityEvidenceHash.ValueKind == JsonValueKind.String
            && dispatchOutcome.TryGetInt32(out var parsedOutcome)
            && Enum.IsDefined(typeof(TriggerDispatchOutcome), parsedOutcome)
            && dispatchDetail.ValueKind == JsonValueKind.String
            && dispatchIntentRecordedAtUtc is not null)
        {
            TriggerGovernedInvocationEvidence? governedInvocation = null;
            var governedOperationId = element.GetProperty("governedOperationId");
            var governedRunId = element.GetProperty("governedRunId");
            var governedAdmissionRequestHash = element.GetProperty("governedAdmissionRequestHash");
            var governedLoopId = element.GetProperty("governedLoopId");
            var governedDefinitionVersion = element.GetProperty("governedDefinitionVersion");
            var governedDefinitionHash = element.GetProperty("governedDefinitionHash");
            if (governedOperationId.ValueKind == JsonValueKind.Null && governedRunId.ValueKind == JsonValueKind.Null && governedAdmissionRequestHash.ValueKind == JsonValueKind.Null && governedLoopId.ValueKind == JsonValueKind.Null && governedDefinitionVersion.ValueKind == JsonValueKind.Null && governedDefinitionHash.ValueKind == JsonValueKind.Null)
            {
            }
            else if (governedOperationId.ValueKind == JsonValueKind.String
                && governedRunId.ValueKind == JsonValueKind.String
                && governedAdmissionRequestHash.ValueKind == JsonValueKind.String
                && governedLoopId.ValueKind == JsonValueKind.String
                && governedDefinitionVersion.TryGetInt32(out var parsedDefinitionVersion)
                && governedDefinitionHash.ValueKind == JsonValueKind.String)
            {
                governedInvocation = new TriggerGovernedInvocationEvidence(governedOperationId.GetString()!, governedRunId.GetString()!, governedAdmissionRequestHash.GetString()!, governedLoopId.GetString()!, parsedDefinitionVersion, governedDefinitionHash.GetString()!);
            }
            else
            {
                throw Invalid();
            }

            dispatch = new TriggerDispatchEvidence(dispatchOperationId.GetString()!, dispatchRequestHash.GetString()!, dispatchAuthorityEvidenceHash.GetString()!, dispatchIntentRecordedAtUtc.Value, (TriggerDispatchOutcome)parsedOutcome, dispatchOutcomeRecordedAtUtc, dispatchDetail.GetString()!, governedInvocation);
        }
        else
        {
            throw Invalid();
        }

        return new TriggerQueueLedgerEntry(envelope!, canonicalEnvelope!, receipt, admissionStatus, admissionReason, canonicalEnvelopeHash!, priority, state, terminalReason, revision, recordedAtUtc!.Value, terminalAtUtc, lease, dispatch);
    }

    private static void WriteEntry(Utf8JsonWriter writer, TriggerQueueLedgerEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteNumber("admissionReason", (int)entry.AdmissionReason);
        writer.WriteNumber("admissionStatus", (int)entry.AdmissionStatus);
        writer.WriteString("canonicalEnvelope", entry.CanonicalEnvelope);
        writer.WriteString("canonicalEnvelopeHash", entry.CanonicalEnvelopeHash);
        WriteNullableString(writer, "dispatchAuthorityEvidenceHash", entry.Dispatch?.AuthorityEvidenceHash);
        WriteNullableString(writer, "dispatchDetail", entry.Dispatch?.Detail);
        WriteTimestamp(writer, "dispatchIntentRecordedAtUtc", entry.Dispatch?.IntentRecordedAtUtc);
        WriteNullableString(writer, "dispatchOperationId", entry.Dispatch?.OperationId);
        if (entry.Dispatch is null)
        {
            writer.WriteNull("dispatchOutcome");
        }
        else
        {
            writer.WriteNumber("dispatchOutcome", (int)entry.Dispatch.Outcome);
        }

        WriteTimestamp(writer, "dispatchOutcomeRecordedAtUtc", entry.Dispatch?.OutcomeRecordedAtUtc);
        WriteNullableString(writer, "dispatchRequestHash", entry.Dispatch?.RequestHash);
        WriteNullableString(writer, "governedAdmissionRequestHash", entry.Dispatch?.GovernedInvocation?.AdmissionRequestHash);
        WriteNullableString(writer, "governedDefinitionHash", entry.Dispatch?.GovernedInvocation?.DefinitionHash);
        if (entry.Dispatch?.GovernedInvocation is null)
        {
            writer.WriteNull("governedDefinitionVersion");
        }
        else
        {
            writer.WriteNumber("governedDefinitionVersion", entry.Dispatch.GovernedInvocation.DefinitionVersion);
        }

        WriteNullableString(writer, "governedLoopId", entry.Dispatch?.GovernedInvocation?.LoopId);
        WriteNullableString(writer, "governedOperationId", entry.Dispatch?.GovernedInvocation?.OperationId);
        WriteNullableString(writer, "governedRunId", entry.Dispatch?.GovernedInvocation?.RunId);
        WriteTimestamp(writer, "leaseAcquiredAtUtc", entry.WorkerLease?.AcquiredAtUtc);
        WriteTimestamp(writer, "leaseExpiresAtUtc", entry.WorkerLease?.ExpiresAtUtc);
        if (entry.WorkerLease is null)
        {
            writer.WriteNull("leaseGeneration");
        }
        else
        {
            writer.WriteNumber("leaseGeneration", entry.WorkerLease.Generation);
        }

        WriteTimestamp(writer, "leaseReleasedAtUtc", entry.WorkerLease?.ReleasedAtUtc);
        if (entry.WorkerLease is null)
        {
            writer.WriteNull("leaseRenewalCount");
        }
        else
        {
            writer.WriteNumber("leaseRenewalCount", entry.WorkerLease.RenewalCount);
        }

        WriteNullableString(writer, "leaseWorkerId", entry.WorkerLease?.WorkerId);
        writer.WriteNumber("priority", (int)entry.Priority);
        WriteTimestamp(writer, "receiptRecordedAtUtc", entry.Receipt?.RecordedAtUtc);
        WriteNullableString(writer, "receiptReplayBindingHash", entry.Receipt?.ReplayBindingHash);
        if (entry.Receipt is null)
        {
            writer.WriteNull("receiptSchemaVersion");
        }
        else
        {
            writer.WriteNumber("receiptSchemaVersion", entry.Receipt.SchemaVersion);
        }

        WriteTimestamp(writer, "recordedAtUtc", entry.RecordedAtUtc);
        writer.WriteNumber("revision", entry.Revision);
        writer.WriteNumber("state", (int)entry.State);
        WriteTimestamp(writer, "terminalAtUtc", entry.TerminalAtUtc);
        writer.WriteNumber("terminalReason", (int)entry.TerminalReason);
        writer.WriteEndObject();
    }

    private static bool TryQuota(JsonElement element, out TriggerQueueQuota quota)
    {
        quota = null!;
        if (!IsExactObject(element, _quotaProperties)
            || !TryInt(element, "maxQueuedEntries", out var maxQueuedEntries)
            || !TryInt(element, "maxRetainedEntries", out var maxRetainedEntries)
            || !TryInt(element, "maxEntryBytes", out var maxEntryBytes)
            || !TryLong(element, "maxQueuedBytes", out var maxQueuedBytes)
            || !TryLong(element, "maxRetainedBytes", out var maxRetainedBytes)
            || !TryInt(element, "maxQueuedEntriesPerLoop", out var maxQueuedEntriesPerLoop)
            || !TryInt(element, "maxDurabilityTombstones", out var maxDurabilityTombstones))
        {
            return false;
        }

        quota = new TriggerQueueQuota(maxQueuedEntries, maxRetainedEntries, maxEntryBytes, maxQueuedBytes, maxRetainedBytes, maxQueuedEntriesPerLoop, maxDurabilityTombstones);
        try
        {
            TriggerQueueQuotaValidator.Validate(quota);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsReceiptlessOutcome(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        return status == TriggerAdmissionStatus.NotYetEligible && reason == TriggerAdmissionReason.NotBefore;
    }

    private static bool IsExactObject(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Count && names.Distinct(StringComparer.Ordinal).Count() == names.Length && names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool TryString(JsonElement parent, string name, out string? value)
    {
        var element = parent.GetProperty(name);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryNullableHash(JsonElement parent, string name, out string? value)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value?.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool TryInt(JsonElement parent, string name, out int value) => parent.GetProperty(name).TryGetInt32(out value);

    private static bool TryLong(JsonElement parent, string name, out long value) => parent.GetProperty(name).TryGetInt64(out value);

    private static bool TryEnum<T>(JsonElement parent, string name, out T value) where T : struct, Enum
    {
        value = default;
        return parent.GetProperty(name).TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number) && (value = (T)Enum.ToObject(typeof(T), number)) is var _;
    }

    private static bool TryTimestamp(JsonElement parent, string name, bool nullable, out DateTimeOffset? value)
    {
        var element = parent.GetProperty(name);
        if (nullable && element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(element.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            && timestamp.Offset == TimeSpan.Zero)
        {
            value = timestamp;
            return true;
        }

        value = null;
        return false;
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, timestamp.Value.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static FormatException Invalid() => new("Trigger queue ledger contract is invalid.");
}
