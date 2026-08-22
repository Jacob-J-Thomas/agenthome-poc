using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Common.Inference.Profiles;

/// <summary>Reads and writes strict canonical schema-1 governed model JSON without migration or fallback readers.</summary>
/// <remarks>Readers reject unknown or duplicate properties, unsupported shapes, invalid nested values, excess depth/count/size, and any noncanonical byte representation.</remarks>
public static class GovernedModelContractJson
{
    private const int MaximumJsonCharacters = 262_144;
    private const int MaximumJsonBytes = 524_288;
    private const int MaximumJsonDepth = 64;
    private const int MaximumJsonElements = 8_192;
    private static readonly JsonSerializerOptions _options = new()
    {
        MaxDepth = MaximumJsonDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    /// <summary>Serializes canonical profile metadata after complete validation.</summary>
    public static bool TrySerializeProfileMetadata(GovernedModelProfileMetadata? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads canonical profile metadata and rejects any hostile or noncanonical representation.</summary>
    public static bool TryDeserializeProfileMetadata(string? json, out GovernedModelProfileMetadata? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    /// <summary>Serializes an exact capability-backed profile pin after complete validation.</summary>
    public static bool TrySerializeProfilePin(GovernedModelProfilePin? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads an exact capability-backed profile pin and rejects forged nested capability evidence.</summary>
    public static bool TryDeserializeProfilePin(string? json, out GovernedModelProfilePin? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    /// <summary>Serializes a canonical typed routing policy.</summary>
    public static bool TrySerializeRoutingPolicy(GovernedModelRoutingPolicy? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads a canonical typed routing policy without aliases or permissive fallback parsing.</summary>
    public static bool TryDeserializeRoutingPolicy(string? json, out GovernedModelRoutingPolicy? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    /// <summary>Serializes a complete canonical model-routing admission snapshot.</summary>
    public static bool TrySerializeRoutingAdmission(GovernedModelRoutingAdmissionSnapshot? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads a complete canonical model-routing admission snapshot.</summary>
    public static bool TryDeserializeRoutingAdmission(string? json, out GovernedModelRoutingAdmissionSnapshot? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    /// <summary>Serializes explicit authoritative-or-unavailable provider usage.</summary>
    public static bool TrySerializeUsageEvidence(LlmInferenceUsageEvidence? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads explicit authoritative-or-unavailable provider usage without synthesizing missing dimensions.</summary>
    public static bool TryDeserializeUsageEvidence(string? json, out LlmInferenceUsageEvidence? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    /// <summary>Serializes one canonical append-only model-usage ledger entry.</summary>
    public static bool TrySerializeUsageLedgerEntry(GovernedModelUsageLedgerEntry? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads one canonical append-only model-usage ledger entry.</summary>
    public static bool TryDeserializeUsageLedgerEntry(string? json, out GovernedModelUsageLedgerEntry? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    /// <summary>Serializes exact completed provider-attempt profile, reservation, and usage evidence.</summary>
    public static bool TrySerializeAttemptExecutionEvidence(GovernedModelAttemptExecutionEvidence? value, out string? json, out string? error)
        => TrySerialize(value, GovernedModelContractValidator.IsValid, out json, out error);

    /// <summary>Reads exact completed provider-attempt evidence without aliases or omitted usage dimensions.</summary>
    public static bool TryDeserializeAttemptExecutionEvidence(string? json, out GovernedModelAttemptExecutionEvidence? value, out string? error)
        => TryDeserialize(json, GovernedModelContractValidator.IsValid, out value, out error);

    private static bool TrySerialize<T>(T? value, Func<T?, bool> validate, out string? json, out string? error) where T : class
    {
        json = null;
        if (!validate(value))
        {
            error = "invalid_governed_model_contract";
            return false;
        }

        try
        {
            json = JsonSerializer.Serialize(value, _options);
            if (!IsBounded(json))
            {
                json = null;
                error = "governed_model_contract_too_large";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            json = null;
            error = "invalid_governed_model_contract";
            return false;
        }
    }

    private static bool TryDeserialize<T>(string? json, Func<T?, bool> validate, out T? value, out string? error) where T : class
    {
        value = null;
        if (!IsBounded(json))
        {
            error = "governed_model_contract_too_large";
            return false;
        }

        try
        {
            if (!HasStrictShape(json!, out error))
            {
                return false;
            }

            value = JsonSerializer.Deserialize<T>(json!, _options);
            if (!validate(value))
            {
                value = null;
                error = "invalid_governed_model_contract";
                return false;
            }

            var canonical = JsonSerializer.Serialize(value, _options);
            if (!string.Equals(json, canonical, StringComparison.Ordinal))
            {
                value = null;
                error = "noncanonical_governed_model_contract";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            value = null;
            error = "invalid_governed_model_contract";
            return false;
        }
    }

    private static bool IsBounded(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaximumJsonCharacters)
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(json) <= MaximumJsonBytes;
    }

    private static bool HasStrictShape(string json, out string? error)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumJsonDepth });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            error = "invalid_governed_model_contract";
            return false;
        }

        var count = 0;
        if (!HasStrictShape(document.RootElement, ref count))
        {
            error = "invalid_governed_model_contract_shape";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasStrictShape(JsonElement element, ref int count)
    {
        if (++count > MaximumJsonElements)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasStrictShape(property.Value, ref count))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!HasStrictShape(item, ref count))
                {
                    return false;
                }
            }
        }

        return element.ValueKind != JsonValueKind.Undefined;
    }
}
