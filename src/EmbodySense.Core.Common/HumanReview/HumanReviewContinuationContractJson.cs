using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Serializes and parses the closed canonical schema-1 continuation state JSON contract without providing a compatibility reader or migration path.</summary>
public static class HumanReviewContinuationContractJson
{
    private const int MaxJsonCharacters = 131_072;
    private static readonly string[] _claimProperties = ["claimHash", "claimId", "claimedAtUtc", "expectedGeneration", "leaseExpiresAtUtc", "provenance", "reservation", "schemaVersion", "wake", "workerId"];
    private static readonly string[] _claimReferenceProperties = ["claimHash", "claimId"];
    private static readonly string[] _completionProperties = ["claim", "completedAtUtc", "completionHash", "completionId", "evidence", "expectedGeneration", "provenance", "releaseReceipt", "reservation", "schemaVersion", "wake"];
    private static readonly string[] _decisionProperties = ["decisionHash", "decisionId", "decisionOperationId", "kind"];
    private static readonly string[] _previewProperties = ["detail", "detailHash", "kind", "label"];
    private static readonly string[] _provenanceProperties = ["correlationId", "kind", "observedAtUtc", "provenanceHash", "sourceId"];
    private static readonly string[] _releaseReceiptProperties = ["claim", "disposition", "effectReceiptHash", "expectedGeneration", "frontierReceiptHash", "kind", "releaseOperationId", "releaseReceiptHash", "reservation", "resultHash", "schemaVersion", "wake"];
    private static readonly string[] _requestProperties = ["requestHash", "requestId"];
    private static readonly string[] _reservationProperties = ["reservationHash", "reservationId"];
    private static readonly string[] _retirementProperties = ["evidence", "expectedGeneration", "outcome", "provenance", "reservation", "retiredAtUtc", "retirementHash", "retirementId", "schemaVersion", "wake"];
    private static readonly string[] _stateProperties = ["claims", "completion", "retirement", "schemaVersion", "stateHash", "wake"];
    private static readonly string[] _wakeProperties = ["bindingHash", "decision", "expectedGeneration", "expiresAtUtc", "provenance", "publishedAtUtc", "request", "reservation", "schemaVersion", "wakeHash", "wakeId"];
    private static readonly string[] _wakeReferenceProperties = ["wakeHash", "wakeId"];

    /// <summary>Serializes a validated continuation state into deterministic compact schema-1 JSON.</summary>
    /// <param name="request">The exact request that binds the state.</param>
    /// <param name="reservation">The exact approved continuation reservation that binds the state.</param>
    /// <param name="state">The continuation state to serialize.</param>
    /// <param name="json">The canonical compact JSON when successful.</param>
    /// <param name="validation">The validation failures when serialization is rejected.</param>
    /// <returns><see langword="true"/> when serialization succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TrySerializeState(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, HumanReviewContinuationState? state, out string? json, out HumanReviewContractValidationResult validation)
    {
        validation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, state);
        json = null;
        if (!validation.IsValid) return false;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteState(writer, state!);
            writer.Flush();
        }
        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (json.Length <= MaxJsonCharacters) return true;
        json = null;
        validation = Invalid("continuation_json_too_large", "$", "Canonical continuation JSON exceeds the schema-1 size bound.");
        return false;
    }

    /// <summary>Parses only the closed canonical schema-1 continuation state shape, rejecting unknown, duplicate, missing, malformed, or forward-versioned fields.</summary>
    /// <param name="request">The exact request that must bind the parsed state.</param>
    /// <param name="reservation">The exact approved continuation reservation that must bind the parsed state.</param>
    /// <param name="json">The candidate continuation JSON.</param>
    /// <param name="state">The detached validated state when parsing succeeds.</param>
    /// <param name="validation">The structured parse or contract validation failures.</param>
    /// <returns><see langword="true"/> when parsing and validation succeed; otherwise, <see langword="false"/>.</returns>
    public static bool TryDeserializeState(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, string? json, out HumanReviewContinuationState? state, out HumanReviewContractValidationResult validation)
    {
        state = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonCharacters)
        {
            validation = Invalid("invalid_continuation_json", "$", "Continuation JSON must be non-empty and within the schema-1 size bound.");
            return false;
        }

        var errors = new List<HumanReviewContractValidationError>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var parsed = ReadState(document.RootElement, "$", errors);
            if (errors.Count != 0 || parsed is null)
            {
                validation = new HumanReviewContractValidationResult(errors);
                return false;
            }
            validation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, parsed);
            if (!validation.IsValid) return false;
            state = parsed;
            return true;
        }
        catch (JsonException exception)
        {
            validation = Invalid("invalid_continuation_json", "$", $"Continuation JSON is malformed: {exception.Message}");
            return false;
        }
    }

    private static void WriteState(Utf8JsonWriter writer, HumanReviewContinuationState state)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("claims");
        writer.WriteStartArray();
        foreach (var claim in state.Claims) WriteClaim(writer, claim);
        writer.WriteEndArray();
        WriteNullableCompletion(writer, state.Completion);
        WriteNullableRetirement(writer, state.Retirement);
        writer.WriteNumber("schemaVersion", state.SchemaVersion);
        writer.WriteString("stateHash", state.StateHash);
        writer.WritePropertyName("wake");
        WriteWake(writer, state.Wake);
        writer.WriteEndObject();
    }

    private static void WriteWake(Utf8JsonWriter writer, HumanReviewContinuationWake wake)
    {
        writer.WriteStartObject();
        writer.WriteString("bindingHash", wake.BindingHash);
        writer.WritePropertyName("decision");
        WriteDecisionReference(writer, wake.Decision);
        writer.WriteNumber("expectedGeneration", wake.ExpectedGeneration);
        WriteTime(writer, "expiresAtUtc", wake.ExpiresAtUtc);
        writer.WritePropertyName("provenance");
        WriteProvenance(writer, wake.Provenance);
        WriteTime(writer, "publishedAtUtc", wake.PublishedAtUtc);
        writer.WritePropertyName("request");
        WriteRequestReference(writer, wake.Request);
        writer.WritePropertyName("reservation");
        WriteReservationReference(writer, wake.Reservation);
        writer.WriteNumber("schemaVersion", wake.SchemaVersion);
        writer.WriteString("wakeHash", wake.WakeHash);
        writer.WriteString("wakeId", wake.WakeId);
        writer.WriteEndObject();
    }

    private static void WriteClaim(Utf8JsonWriter writer, HumanReviewContinuationClaim claim)
    {
        writer.WriteStartObject();
        writer.WriteString("claimHash", claim.ClaimHash);
        writer.WriteString("claimId", claim.ClaimId);
        WriteTime(writer, "claimedAtUtc", claim.ClaimedAtUtc);
        writer.WriteNumber("expectedGeneration", claim.ExpectedGeneration);
        WriteTime(writer, "leaseExpiresAtUtc", claim.LeaseExpiresAtUtc);
        writer.WritePropertyName("provenance");
        WriteProvenance(writer, claim.Provenance);
        writer.WritePropertyName("reservation");
        WriteReservationReference(writer, claim.Reservation);
        writer.WriteNumber("schemaVersion", claim.SchemaVersion);
        writer.WritePropertyName("wake");
        WriteWakeReference(writer, claim.Wake);
        writer.WriteString("workerId", claim.WorkerId);
        writer.WriteEndObject();
    }

    private static void WriteNullableCompletion(Utf8JsonWriter writer, HumanReviewContinuationCompletion? completion)
    {
        writer.WritePropertyName("completion");
        if (completion is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("claim");
        WriteClaimReference(writer, completion.Claim);
        WriteTime(writer, "completedAtUtc", completion.CompletedAtUtc);
        writer.WriteString("completionHash", completion.CompletionHash);
        writer.WriteString("completionId", completion.CompletionId);
        writer.WritePropertyName("evidence");
        WritePreviews(writer, completion.Evidence);
        writer.WriteNumber("expectedGeneration", completion.ExpectedGeneration);
        writer.WritePropertyName("provenance");
        WriteProvenance(writer, completion.Provenance);
        writer.WritePropertyName("releaseReceipt");
        WriteReleaseReceipt(writer, completion.ReleaseReceipt);
        writer.WritePropertyName("reservation");
        WriteReservationReference(writer, completion.Reservation);
        writer.WriteNumber("schemaVersion", completion.SchemaVersion);
        writer.WritePropertyName("wake");
        WriteWakeReference(writer, completion.Wake);
        writer.WriteEndObject();
    }

    private static void WriteReleaseReceipt(Utf8JsonWriter writer, HumanReviewContinuationReleaseReceipt receipt)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("claim");
        WriteClaimReference(writer, receipt.Claim);
        writer.WriteNumber("disposition", (int)receipt.Disposition);
        writer.WriteString("effectReceiptHash", receipt.EffectReceiptHash);
        writer.WriteNumber("expectedGeneration", receipt.ExpectedGeneration);
        writer.WriteString("frontierReceiptHash", receipt.FrontierReceiptHash);
        writer.WriteNumber("kind", (int)receipt.Kind);
        writer.WriteString("releaseOperationId", receipt.ReleaseOperationId);
        writer.WriteString("releaseReceiptHash", receipt.ReleaseReceiptHash);
        writer.WritePropertyName("reservation");
        WriteReservationReference(writer, receipt.Reservation);
        writer.WriteString("resultHash", receipt.ResultHash);
        writer.WriteNumber("schemaVersion", receipt.SchemaVersion);
        writer.WritePropertyName("wake");
        WriteWakeReference(writer, receipt.Wake);
        writer.WriteEndObject();
    }

    private static void WriteNullableRetirement(Utf8JsonWriter writer, HumanReviewContinuationRetirement? retirement)
    {
        writer.WritePropertyName("retirement");
        if (retirement is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("evidence");
        WritePreviews(writer, retirement.Evidence);
        writer.WriteNumber("expectedGeneration", retirement.ExpectedGeneration);
        writer.WriteNumber("outcome", (int)retirement.Outcome);
        writer.WritePropertyName("provenance");
        WriteProvenance(writer, retirement.Provenance);
        writer.WritePropertyName("reservation");
        WriteReservationReference(writer, retirement.Reservation);
        WriteTime(writer, "retiredAtUtc", retirement.RetiredAtUtc);
        writer.WriteString("retirementHash", retirement.RetirementHash);
        writer.WriteString("retirementId", retirement.RetirementId);
        writer.WriteNumber("schemaVersion", retirement.SchemaVersion);
        writer.WritePropertyName("wake");
        WriteWakeReference(writer, retirement.Wake);
        writer.WriteEndObject();
    }

    private static void WritePreviews(Utf8JsonWriter writer, ImmutableArray<HumanReviewRedactedPreview> previews)
    {
        writer.WriteStartArray();
        foreach (var preview in previews)
        {
            writer.WriteStartObject();
            writer.WriteString("detail", preview.Detail);
            writer.WriteString("detailHash", preview.DetailHash);
            writer.WriteNumber("kind", (int)preview.Kind);
            writer.WriteString("label", preview.Label);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteProvenance(Utf8JsonWriter writer, HumanReviewProvenance provenance)
    {
        writer.WriteStartObject();
        writer.WriteString("correlationId", provenance.CorrelationId);
        writer.WriteNumber("kind", (int)provenance.Kind);
        WriteTime(writer, "observedAtUtc", provenance.ObservedAtUtc);
        writer.WriteString("provenanceHash", provenance.ProvenanceHash);
        writer.WriteString("sourceId", provenance.SourceId);
        writer.WriteEndObject();
    }

    private static void WriteRequestReference(Utf8JsonWriter writer, HumanReviewRequestReference request)
    {
        writer.WriteStartObject();
        writer.WriteString("requestHash", request.RequestHash);
        writer.WriteString("requestId", request.RequestId);
        writer.WriteEndObject();
    }

    private static void WriteDecisionReference(Utf8JsonWriter writer, HumanReviewDecisionReference decision)
    {
        writer.WriteStartObject();
        writer.WriteString("decisionHash", decision.DecisionHash);
        writer.WriteString("decisionId", decision.DecisionId);
        writer.WriteString("decisionOperationId", decision.DecisionOperationId);
        writer.WriteNumber("kind", (int)decision.Kind);
        writer.WriteEndObject();
    }

    private static void WriteReservationReference(Utf8JsonWriter writer, HumanReviewContinuationReservationReference reservation)
    {
        writer.WriteStartObject();
        writer.WriteString("reservationHash", reservation.ReservationHash);
        writer.WriteString("reservationId", reservation.ReservationId);
        writer.WriteEndObject();
    }

    private static void WriteWakeReference(Utf8JsonWriter writer, HumanReviewContinuationWakeReference wake)
    {
        writer.WriteStartObject();
        writer.WriteString("wakeHash", wake.WakeHash);
        writer.WriteString("wakeId", wake.WakeId);
        writer.WriteEndObject();
    }

    private static void WriteClaimReference(Utf8JsonWriter writer, HumanReviewContinuationClaimReference claim)
    {
        writer.WriteStartObject();
        writer.WriteString("claimHash", claim.ClaimHash);
        writer.WriteString("claimId", claim.ClaimId);
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, string propertyName, DateTimeOffset value) => writer.WriteString(propertyName, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static HumanReviewContinuationState? ReadState(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _stateProperties, errors)) return null;
        var claims = ReadClaims(value.GetProperty("claims"), path + ".claims", errors);
        var completion = ReadNullableCompletion(value.GetProperty("completion"), path + ".completion", errors);
        var retirement = ReadNullableRetirement(value.GetProperty("retirement"), path + ".retirement", errors);
        return new HumanReviewContinuationState(ReadInt(value, "schemaVersion", path, errors), ReadWake(value.GetProperty("wake"), path + ".wake", errors)!, claims, completion, retirement, ReadString(value, "stateHash", path, errors)!);
    }

    private static HumanReviewContinuationWake? ReadWake(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _wakeProperties, errors)) return null;
        return new HumanReviewContinuationWake(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "wakeId", path, errors)!, ReadRequestReference(value.GetProperty("request"), path + ".request", errors)!, ReadDecisionReference(value.GetProperty("decision"), path + ".decision", errors)!, ReadReservationReference(value.GetProperty("reservation"), path + ".reservation", errors)!, ReadString(value, "bindingHash", path, errors)!, ReadLong(value, "expectedGeneration", path, errors), ReadTime(value, "publishedAtUtc", path, errors), ReadTime(value, "expiresAtUtc", path, errors), ReadProvenance(value.GetProperty("provenance"), path + ".provenance", errors)!, ReadString(value, "wakeHash", path, errors)!);
    }

    private static ImmutableArray<HumanReviewContinuationClaim> ReadClaims(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array is required."); return default; }
        var values = ImmutableArray.CreateBuilder<HumanReviewContinuationClaim>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var claim = ReadClaim(item, $"{path}[{index}]", errors);
            if (claim is not null) values.Add(claim);
            index++;
        }
        return values.ToImmutable();
    }

    private static HumanReviewContinuationClaim? ReadClaim(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _claimProperties, errors)) return null;
        return new HumanReviewContinuationClaim(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "claimId", path, errors)!, ReadWakeReference(value.GetProperty("wake"), path + ".wake", errors)!, ReadReservationReference(value.GetProperty("reservation"), path + ".reservation", errors)!, ReadLong(value, "expectedGeneration", path, errors), ReadString(value, "workerId", path, errors)!, ReadTime(value, "claimedAtUtc", path, errors), ReadTime(value, "leaseExpiresAtUtc", path, errors), ReadProvenance(value.GetProperty("provenance"), path + ".provenance", errors)!, ReadString(value, "claimHash", path, errors)!);
    }

    private static HumanReviewContinuationCompletion? ReadNullableCompletion(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!Shape(value, path, _completionProperties, errors)) return null;
        return new HumanReviewContinuationCompletion(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "completionId", path, errors)!, ReadWakeReference(value.GetProperty("wake"), path + ".wake", errors)!, ReadClaimReference(value.GetProperty("claim"), path + ".claim", errors)!, ReadReservationReference(value.GetProperty("reservation"), path + ".reservation", errors)!, ReadLong(value, "expectedGeneration", path, errors), ReadReleaseReceipt(value.GetProperty("releaseReceipt"), path + ".releaseReceipt", errors)!, ReadTime(value, "completedAtUtc", path, errors), ReadPreviews(value.GetProperty("evidence"), path + ".evidence", errors), ReadProvenance(value.GetProperty("provenance"), path + ".provenance", errors)!, ReadString(value, "completionHash", path, errors)!);
    }

    private static HumanReviewContinuationReleaseReceipt? ReadReleaseReceipt(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _releaseReceiptProperties, errors)) return null;
        return new HumanReviewContinuationReleaseReceipt(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "releaseOperationId", path, errors)!, ReadWakeReference(value.GetProperty("wake"), path + ".wake", errors)!, ReadClaimReference(value.GetProperty("claim"), path + ".claim", errors)!, ReadReservationReference(value.GetProperty("reservation"), path + ".reservation", errors)!, ReadLong(value, "expectedGeneration", path, errors), ReadEnum<HumanReviewContinuationReleaseKind>(value, "kind", path, errors), ReadEnum<HumanReviewContinuationReleaseDisposition>(value, "disposition", path, errors), ReadString(value, "resultHash", path, errors)!, ReadString(value, "frontierReceiptHash", path, errors)!, ReadNullableString(value, "effectReceiptHash", path, errors), ReadString(value, "releaseReceiptHash", path, errors)!);
    }

    private static HumanReviewContinuationRetirement? ReadNullableRetirement(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!Shape(value, path, _retirementProperties, errors)) return null;
        return new HumanReviewContinuationRetirement(ReadInt(value, "schemaVersion", path, errors), ReadString(value, "retirementId", path, errors)!, ReadWakeReference(value.GetProperty("wake"), path + ".wake", errors)!, ReadReservationReference(value.GetProperty("reservation"), path + ".reservation", errors)!, ReadLong(value, "expectedGeneration", path, errors), ReadEnum<HumanReviewContinuationOutcome>(value, "outcome", path, errors), ReadTime(value, "retiredAtUtc", path, errors), ReadPreviews(value.GetProperty("evidence"), path + ".evidence", errors), ReadProvenance(value.GetProperty("provenance"), path + ".provenance", errors)!, ReadString(value, "retirementHash", path, errors)!);
    }

    private static ImmutableArray<HumanReviewRedactedPreview> ReadPreviews(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array) { Add(errors, "invalid_json_type", path, "A JSON array is required."); return default; }
        var values = ImmutableArray.CreateBuilder<HumanReviewRedactedPreview>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (Shape(item, $"{path}[{index}]", _previewProperties, errors)) values.Add(new HumanReviewRedactedPreview(ReadEnum<HumanReviewPreviewKind>(item, "kind", $"{path}[{index}]", errors), ReadString(item, "label", $"{path}[{index}]", errors)!, ReadString(item, "detail", $"{path}[{index}]", errors)!, ReadString(item, "detailHash", $"{path}[{index}]", errors)!));
            index++;
        }
        return values.ToImmutable();
    }

    private static HumanReviewProvenance? ReadProvenance(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _provenanceProperties, errors)) return null;
        return new HumanReviewProvenance(ReadEnum<HumanReviewProvenanceKind>(value, "kind", path, errors), ReadString(value, "sourceId", path, errors)!, ReadString(value, "correlationId", path, errors)!, ReadTime(value, "observedAtUtc", path, errors), ReadString(value, "provenanceHash", path, errors)!);
    }

    private static HumanReviewRequestReference? ReadRequestReference(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _requestProperties, errors)) return null;
        return new HumanReviewRequestReference(ReadString(value, "requestId", path, errors)!, ReadString(value, "requestHash", path, errors)!);
    }

    private static HumanReviewDecisionReference? ReadDecisionReference(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _decisionProperties, errors)) return null;
        return new HumanReviewDecisionReference(ReadString(value, "decisionId", path, errors)!, ReadString(value, "decisionOperationId", path, errors)!, ReadEnum<HumanReviewDecisionKind>(value, "kind", path, errors), ReadString(value, "decisionHash", path, errors)!);
    }

    private static HumanReviewContinuationReservationReference? ReadReservationReference(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _reservationProperties, errors)) return null;
        return new HumanReviewContinuationReservationReference(ReadString(value, "reservationId", path, errors)!, ReadString(value, "reservationHash", path, errors)!);
    }

    private static HumanReviewContinuationWakeReference? ReadWakeReference(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _wakeReferenceProperties, errors)) return null;
        return new HumanReviewContinuationWakeReference(ReadString(value, "wakeId", path, errors)!, ReadString(value, "wakeHash", path, errors)!);
    }

    private static HumanReviewContinuationClaimReference? ReadClaimReference(JsonElement value, string path, List<HumanReviewContractValidationError> errors)
    {
        if (!Shape(value, path, _claimReferenceProperties, errors)) return null;
        return new HumanReviewContinuationClaimReference(ReadString(value, "claimId", path, errors)!, ReadString(value, "claimHash", path, errors)!);
    }

    private static bool Shape(JsonElement value, string path, string[] properties, List<HumanReviewContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object) { Add(errors, "invalid_json_type", path, "A JSON object is required."); return false; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (Array.IndexOf(properties, property.Name) < 0) Add(errors, "unknown_json_property", path + "." + property.Name, "Unknown fields are not supported by schema version 1.");
            else if (!names.Add(property.Name)) Add(errors, "duplicate_json_property", path + "." + property.Name, "Duplicate JSON fields are not supported by schema version 1.");
        }
        foreach (var property in properties) if (!names.Contains(property)) Add(errors, "required_json_property_missing", path + "." + property, "Every schema-1 contract property is required.");
        return errors.Count == 0;
    }

    private static string? ReadString(JsonElement value, string propertyName, string path, List<HumanReviewContractValidationError> errors)
    {
        var property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.String && property.GetString() is { } text) return text;
        Add(errors, "invalid_json_type", path + "." + propertyName, "A JSON string is required.");
        return null;
    }

    private static string? ReadNullableString(JsonElement value, string propertyName, string path, List<HumanReviewContractValidationError> errors)
    {
        var property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind == JsonValueKind.String && property.GetString() is { } text) return text;
        Add(errors, "invalid_json_type", path + "." + propertyName, "A JSON string or null is required.");
        return null;
    }

    private static int ReadInt(JsonElement value, string propertyName, string path, List<HumanReviewContractValidationError> errors)
    {
        var property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)) return number;
        Add(errors, "invalid_json_number", path + "." + propertyName, "An Int32 JSON number is required.");
        return 0;
    }

    private static long ReadLong(JsonElement value, string propertyName, string path, List<HumanReviewContractValidationError> errors)
    {
        var property = value.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number)) return number;
        Add(errors, "invalid_json_number", path + "." + propertyName, "An Int64 JSON number is required.");
        return 0;
    }

    private static TEnum ReadEnum<TEnum>(JsonElement value, string propertyName, string path, List<HumanReviewContractValidationError> errors) where TEnum : struct, Enum
    {
        var number = ReadInt(value, propertyName, path, errors);
        if (Enum.IsDefined(typeof(TEnum), number)) return (TEnum)Enum.ToObject(typeof(TEnum), number);
        Add(errors, "unsupported_json_enum", path + "." + propertyName, "The enum value is unsupported by schema version 1.");
        return default;
    }

    private static DateTimeOffset ReadTime(JsonElement value, string propertyName, string path, List<HumanReviewContractValidationError> errors)
    {
        var text = ReadString(value, propertyName, path, errors);
        if (text is not null && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) && time.Offset == TimeSpan.Zero) return time;
        Add(errors, "invalid_json_timestamp", path + "." + propertyName, "A canonical UTC round-trip timestamp is required.");
        return default;
    }

    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message) => errors.Add(new HumanReviewContractValidationError(code, path, message));
    private static HumanReviewContractValidationResult Invalid(string code, string path, string message) => new([new HumanReviewContractValidationError(code, path, message)]);
}
