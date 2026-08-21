using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Retains explicit provider-supplied authoritative or unavailable usage without inference or price fabrication.</summary>
public sealed class LlmInferenceUsageEvidence
{
    [System.Text.Json.Serialization.JsonConstructor]
    private LlmInferenceUsageEvidence(string sourceId, string sourceContractVersion, GovernedModelUsageMeasurement inputTokens, GovernedModelUsageMeasurement outputTokens, GovernedModelUsageMeasurement cachedTokens, GovernedModelUsageMeasurement totalTokens, GovernedModelMonetaryUsageMeasurement monetaryCost)
    {
        SourceId = sourceId;
        SourceContractVersion = sourceContractVersion;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedTokens = cachedTokens;
        TotalTokens = totalTokens;
        MonetaryCost = monetaryCost;
        ContentHash = GovernedModelContractHash.Compute("embodysense.llm-inference-usage.v1", WriteCanonical);
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion => GovernedModelContractLimits.CurrentSchemaVersion;
    /// <summary>Gets the bounded measurement-source identity.</summary>
    public string SourceId { get; }
    /// <summary>Gets the exact bounded source contract version.</summary>
    public string SourceContractVersion { get; }
    /// <summary>Gets input-token evidence.</summary>
    public GovernedModelUsageMeasurement InputTokens { get; }
    /// <summary>Gets output-token evidence.</summary>
    public GovernedModelUsageMeasurement OutputTokens { get; }
    /// <summary>Gets cached-token evidence.</summary>
    public GovernedModelUsageMeasurement CachedTokens { get; }
    /// <summary>Gets total-token evidence.</summary>
    public GovernedModelUsageMeasurement TotalTokens { get; }
    /// <summary>Gets monetary-cost evidence.</summary>
    public GovernedModelMonetaryUsageMeasurement MonetaryCost { get; }
    /// <summary>Gets the canonical evidence hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates complete explicit usage evidence.</summary>
    public static LlmInferenceUsageEvidence Create(int schemaVersion, string sourceId, string sourceContractVersion, GovernedModelUsageMeasurement inputTokens, GovernedModelUsageMeasurement outputTokens, GovernedModelUsageMeasurement cachedTokens, GovernedModelUsageMeasurement totalTokens, GovernedModelMonetaryUsageMeasurement monetaryCost)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(inputTokens);
        ArgumentNullException.ThrowIfNull(outputTokens);
        ArgumentNullException.ThrowIfNull(cachedTokens);
        ArgumentNullException.ThrowIfNull(totalTokens);
        ArgumentNullException.ThrowIfNull(monetaryCost);
        ValidateMeasurement(inputTokens, nameof(inputTokens));
        ValidateMeasurement(outputTokens, nameof(outputTokens));
        ValidateMeasurement(cachedTokens, nameof(cachedTokens));
        ValidateMeasurement(totalTokens, nameof(totalTokens));
        ValidateMonetary(monetaryCost);
        ValidateCachedSubset(inputTokens, cachedTokens);
        ValidateTotal(inputTokens, outputTokens, totalTokens);
        return new LlmInferenceUsageEvidence(
            GovernedModelContractRules.RequireIdentifier(sourceId, nameof(sourceId)),
            GovernedModelContractRules.RequireIdentifier(sourceContractVersion, nameof(sourceContractVersion)),
            inputTokens,
            outputTokens,
            cachedTokens,
            totalTokens,
            monetaryCost);
    }

    private static void ValidateCachedSubset(GovernedModelUsageMeasurement inputTokens, GovernedModelUsageMeasurement cachedTokens)
    {
        if (inputTokens.Status == GovernedModelUsageEvidenceStatus.Authoritative
            && cachedTokens.Status == GovernedModelUsageEvidenceStatus.Authoritative
            && cachedTokens.Value > inputTokens.Value)
        {
            throw new ArgumentException("Authoritative cached tokens cannot exceed authoritative input tokens.", nameof(cachedTokens));
        }
    }

    /// <summary>Creates explicit all-dimensions-unavailable evidence for an adapter contract.</summary>
    public static LlmInferenceUsageEvidence Unavailable(string sourceId, string sourceContractVersion)
        => Create(GovernedModelContractLimits.CurrentSchemaVersion, sourceId, sourceContractVersion, GovernedModelUsageMeasurement.Unavailable, GovernedModelUsageMeasurement.Unavailable, GovernedModelUsageMeasurement.Unavailable, GovernedModelUsageMeasurement.Unavailable, GovernedModelMonetaryUsageMeasurement.Unavailable);

    private static void ValidateMeasurement(GovernedModelUsageMeasurement measurement, string parameterName)
    {
        if (!Enum.IsDefined(measurement.Status)
            || measurement.Status == GovernedModelUsageEvidenceStatus.Unavailable && measurement.Value != 0
            || measurement.Status == GovernedModelUsageEvidenceStatus.Authoritative && (measurement.Value < 0 || measurement.Value > GovernedModelContractLimits.MaxTokens))
        {
            throw new ArgumentException("Usage measurement status and value are inconsistent.", parameterName);
        }
    }

    private static void ValidateMonetary(GovernedModelMonetaryUsageMeasurement measurement)
    {
        if (!Enum.IsDefined(measurement.Status)
            || measurement.Status == GovernedModelUsageEvidenceStatus.Unavailable && (measurement.Currency is not null || measurement.Micros != 0)
            || measurement.Status == GovernedModelUsageEvidenceStatus.Authoritative && (measurement.Currency is null || measurement.Micros < 0 || measurement.Micros > GovernedModelContractLimits.MaxCurrencyMicros))
        {
            throw new ArgumentException("Monetary usage status, currency, and value are inconsistent.", nameof(measurement));
        }

        if (measurement.Currency is not null)
        {
            _ = GovernedModelContractRules.RequireCurrency(measurement.Currency, nameof(measurement));
        }
    }

    private static void ValidateTotal(GovernedModelUsageMeasurement inputTokens, GovernedModelUsageMeasurement outputTokens, GovernedModelUsageMeasurement totalTokens)
    {
        // Schema 1 treats cached tokens as a subset of input tokens. Therefore total is input + output;
        // cached tokens are retained separately and are never added a second time.
        if (inputTokens.Status == GovernedModelUsageEvidenceStatus.Authoritative
            && outputTokens.Status == GovernedModelUsageEvidenceStatus.Authoritative
            && totalTokens.Status == GovernedModelUsageEvidenceStatus.Authoritative
            && totalTokens.Value != checked(inputTokens.Value + outputTokens.Value))
        {
            throw new ArgumentException("Authoritative total tokens must equal authoritative input plus output tokens; cached tokens are a subset of input.", nameof(totalTokens));
        }
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteMeasurement(writer, "cachedTokens", CachedTokens);
        writer.WriteString("contentCurrency", MonetaryCost.Currency);
        writer.WriteNumber("costMicros", MonetaryCost.Micros);
        writer.WriteNumber("costStatus", (int)MonetaryCost.Status);
        WriteMeasurement(writer, "inputTokens", InputTokens);
        WriteMeasurement(writer, "outputTokens", OutputTokens);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("sourceContractVersion", SourceContractVersion);
        writer.WriteString("sourceId", SourceId);
        WriteMeasurement(writer, "totalTokens", TotalTokens);
        writer.WriteEndObject();
    }

    private static void WriteMeasurement(Utf8JsonWriter writer, string name, GovernedModelUsageMeasurement measurement)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("status", (int)measurement.Status);
        writer.WriteNumber("value", measurement.Value);
        writer.WriteEndObject();
    }
}
