using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Provides deterministic canonical serialization, hashing, and equality for receipt proof and cleanup contracts.
/// </summary>
public static class CustomLoopReceiptRetentionContractCodec
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>
    /// Serializes a canonical bounded cleanup request.
    /// </summary>
    /// <param name="request">The validated cleanup request.</param>
    /// <returns>The deterministic UTF-8 JSON bytes.</returns>
    public static byte[] SerializeCleanupRequest(CustomLoopReceiptCleanupRequest request)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupRequest(request);
        return JsonSerializer.SerializeToUtf8Bytes(request, _jsonOptions);
    }

    /// <summary>
    /// Computes the canonical SHA-256 cleanup request hash.
    /// </summary>
    /// <param name="request">The validated cleanup request.</param>
    /// <returns>The lowercase hexadecimal hash.</returns>
    public static string ComputeCleanupRequestHash(CustomLoopReceiptCleanupRequest request) => ComputeHash(SerializeCleanupRequest(request));

    /// <summary>
    /// Measures one canonical compact expired-operation proof for class byte accounting.
    /// </summary>
    /// <param name="proof">The proof to validate and measure.</param>
    /// <returns>The canonical serialized UTF-8 byte count.</returns>
    public static int MeasureExpiredOperationProofUtf8Bytes(CustomLoopExpiredOperationProof proof)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateExpiredOperationProof(proof);
        return MeasureExpiredOperationProofUtf8BytesUnchecked(proof);
    }

    /// <summary>
    /// Measures one canonical compact definition-lineage proof for class byte accounting.
    /// </summary>
    /// <param name="proof">The proof to validate and measure.</param>
    /// <returns>The canonical serialized UTF-8 byte count.</returns>
    public static int MeasureDefinitionLineageProofUtf8Bytes(CustomLoopDefinitionLineageProof proof)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateDefinitionLineageProof(proof);
        return MeasureDefinitionLineageProofUtf8BytesUnchecked(proof);
    }

    /// <summary>
    /// Computes the canonical binding between one successful Deleted request/outcome fingerprint and its complete deleted-definition lineage.
    /// </summary>
    /// <param name="requestHash">The canonical original Delete request hash.</param>
    /// <param name="outcomeHash">The canonical terminal Delete outcome hash.</param>
    /// <param name="lineage">The deleted-definition lineage owned by the operation.</param>
    /// <returns>The lowercase hexadecimal SHA-256 binding hash.</returns>
    public static string ComputeDeleteLineageBindingHash(string requestHash, string outcomeHash, CustomLoopDefinitionLineageProof lineage)
    {
        CustomLoopReceiptRetentionContractValidator.RequireHash(requestHash, nameof(requestHash));
        CustomLoopReceiptRetentionContractValidator.RequireHash(outcomeHash, nameof(outcomeHash));
        CustomLoopReceiptRetentionContractValidator.ValidateDefinitionLineageProof(lineage);
        if (!lineage.IsDeleted)
        {
            throw new ArgumentException("A Delete fingerprint can bind only to deleted definition lineage.", nameof(lineage));
        }

        return ComputeDeleteLineageBindingHashUnchecked(requestHash, outcomeHash, lineage);
    }

    /// <summary>
    /// Serializes a proof ledger after deterministic ordering and strict schema validation.
    /// </summary>
    /// <param name="ledger">The proof ledger.</param>
    /// <returns>The deterministic UTF-8 JSON bytes.</returns>
    public static byte[] SerializeProofLedger(CustomLoopReceiptProofLedger ledger)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(ledger);
        var normalized = ledger with
        {
            DefinitionLineage = ledger.DefinitionLineage.OrderBy(item => item.LoopId, StringComparer.Ordinal).ToImmutableArray(),
            ExpiredOperations = ledger.ExpiredOperations.OrderBy(item => item.ArtifactClass).ThenBy(item => item.OperationId, StringComparer.Ordinal).ToImmutableArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, _jsonOptions);
        if (bytes.LongLength > CustomLoopReceiptRetentionPolicy.MaxProofLedgerUtf8Bytes)
        {
            throw new ArgumentException("Compact proof ledger exceeds its aggregate UTF-8 byte ceiling.", nameof(ledger));
        }

        return bytes;
    }

    /// <summary>
    /// Computes the canonical SHA-256 compact proof-ledger hash.
    /// </summary>
    /// <param name="ledger">The proof ledger.</param>
    /// <returns>The lowercase hexadecimal hash.</returns>
    public static string ComputeProofLedgerHash(CustomLoopReceiptProofLedger ledger) => ComputeHash(SerializeProofLedger(ledger));

    /// <summary>
    /// Deserializes a bounded schema-1 proof ledger without compatibility fallback.
    /// </summary>
    /// <param name="utf8Json">The persisted UTF-8 JSON bytes.</param>
    /// <returns>The strictly validated proof ledger.</returns>
    public static CustomLoopReceiptProofLedger DeserializeProofLedger(ReadOnlySpan<byte> utf8Json)
    {
        RequireInputSize(utf8Json, CustomLoopReceiptRetentionPolicy.MaxProofLedgerUtf8Bytes, "proof ledger");
        try
        {
            RejectDuplicateProperties(utf8Json, "proof ledger");
            var ledger = JsonSerializer.Deserialize<CustomLoopReceiptProofLedger>(utf8Json, _jsonOptions)
                ?? throw new FormatException("Compact proof ledger cannot be null.");
            CustomLoopReceiptRetentionContractValidator.ValidateProofLedger(ledger);
            return ledger;
        }
        catch (JsonException exception)
        {
            throw new FormatException("Compact proof ledger is not canonical schema-1 JSON.", exception);
        }
    }

    /// <summary>
    /// Compares proof ledgers by canonical content rather than input collection order or array identity.
    /// </summary>
    /// <param name="left">The left ledger.</param>
    /// <param name="right">The right ledger.</param>
    /// <returns><see langword="true"/> when canonical bytes are equal.</returns>
    public static bool ProofLedgersEqual(CustomLoopReceiptProofLedger left, CustomLoopReceiptProofLedger right)
    {
        return CryptographicOperations.FixedTimeEquals(SerializeProofLedger(left), SerializeProofLedger(right));
    }

    /// <summary>
    /// Serializes a cleanup journal after deterministic candidate ordering and strict state validation.
    /// </summary>
    /// <param name="journal">The cleanup journal.</param>
    /// <returns>The deterministic UTF-8 JSON bytes.</returns>
    public static byte[] SerializeCleanupJournal(CustomLoopReceiptCleanupJournal journal)
    {
        CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal);
        var normalized = journal with { Candidates = journal.Candidates.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToImmutableArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, _jsonOptions);
        if (bytes.LongLength > CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes)
        {
            throw new ArgumentException("Cleanup journal exceeds its aggregate UTF-8 byte ceiling.", nameof(journal));
        }

        return bytes;
    }

    /// <summary>
    /// Computes the canonical SHA-256 cleanup-journal hash.
    /// </summary>
    /// <param name="journal">The cleanup journal.</param>
    /// <returns>The lowercase hexadecimal hash.</returns>
    public static string ComputeCleanupJournalHash(CustomLoopReceiptCleanupJournal journal) => ComputeHash(SerializeCleanupJournal(journal));

    /// <summary>
    /// Deserializes a bounded schema-1 cleanup journal without compatibility fallback.
    /// </summary>
    /// <param name="utf8Json">The persisted UTF-8 JSON bytes.</param>
    /// <returns>The strictly validated cleanup journal.</returns>
    public static CustomLoopReceiptCleanupJournal DeserializeCleanupJournal(ReadOnlySpan<byte> utf8Json)
    {
        RequireInputSize(utf8Json, CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes, "cleanup journal");
        try
        {
            RejectDuplicateProperties(utf8Json, "cleanup journal");
            var journal = JsonSerializer.Deserialize<CustomLoopReceiptCleanupJournal>(utf8Json, _jsonOptions)
                ?? throw new FormatException("Receipt cleanup journal cannot be null.");
            CustomLoopReceiptRetentionContractValidator.ValidateCleanupJournal(journal);
            return journal;
        }
        catch (JsonException exception)
        {
            throw new FormatException("Receipt cleanup journal is not canonical schema-1 JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Receipt cleanup journal violates the persisted cleanup contract.", exception);
        }
    }

    /// <summary>
    /// Compares cleanup journals by canonical content rather than candidate input order or array identity.
    /// </summary>
    /// <param name="left">The left journal.</param>
    /// <param name="right">The right journal.</param>
    /// <returns><see langword="true"/> when canonical bytes are equal.</returns>
    public static bool CleanupJournalsEqual(CustomLoopReceiptCleanupJournal left, CustomLoopReceiptCleanupJournal right)
    {
        return CryptographicOperations.FixedTimeEquals(SerializeCleanupJournal(left), SerializeCleanupJournal(right));
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static int MeasureExpiredOperationProofUtf8BytesUnchecked(CustomLoopExpiredOperationProof proof) => JsonSerializer.SerializeToUtf8Bytes(proof, _jsonOptions).Length;

    internal static int MeasureDefinitionLineageProofUtf8BytesUnchecked(CustomLoopDefinitionLineageProof proof) => JsonSerializer.SerializeToUtf8Bytes(proof, _jsonOptions).Length;

    internal static string ComputeDeleteLineageBindingHashUnchecked(string requestHash, string outcomeHash, CustomLoopDefinitionLineageProof lineage)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("requestHash", requestHash);
        writer.WriteString("outcomeHash", outcomeHash);
        writer.WriteString("definitionMutationOutcome", "deleted");
        writer.WriteNumber("lineageSchemaVersion", lineage.SchemaVersion);
        writer.WriteString("loopId", lineage.LoopId);
        writer.WriteString("roleId", lineage.RoleId);
        writer.WriteNumber("lastDefinitionVersion", lineage.LastDefinitionVersion);
        writer.WriteString("lastDefinitionHash", lineage.LastDefinitionHash);
        writer.WriteString("lastMutationOperationId", lineage.LastMutationOperationId);
        writer.WriteBoolean("isDeleted", lineage.IsDeleted);
        writer.WriteString("deletedAtUtc", lineage.DeletedAtUtc!.Value);
        writer.WriteEndObject();
        writer.Flush();
        return ComputeHash(buffer.WrittenSpan);
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8Json, string artifact)
    {
        var reader = new Utf8JsonReader(utf8Json);
        using var document = JsonDocument.ParseValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException($"Receipt retention {artifact} contains trailing JSON content.");
        }

        RejectDuplicateProperties(document.RootElement, "$", artifact);
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, string artifact)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new FormatException($"Receipt retention {artifact} JSON object `{path}` contains duplicate property `{property.Name}`.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", artifact);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", artifact);
                index++;
            }
        }
    }

    private static void RequireInputSize(ReadOnlySpan<byte> utf8Json, long maximumUtf8Bytes, string artifact)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > maximumUtf8Bytes)
        {
            throw new FormatException($"Receipt retention {artifact} is empty or exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        }
    }
}
