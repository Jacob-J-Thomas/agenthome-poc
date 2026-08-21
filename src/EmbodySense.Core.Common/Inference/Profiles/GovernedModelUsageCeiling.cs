using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines explicit per-dimension hard provider-usage ceilings.</summary>
public sealed class GovernedModelUsageCeiling
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageCeiling(GovernedModelUsageLimit inputTokens, GovernedModelUsageLimit outputTokens, GovernedModelUsageLimit cachedTokens, GovernedModelUsageLimit totalTokens, GovernedModelMonetaryLimit monetaryCost)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedTokens = cachedTokens;
        TotalTokens = totalTokens;
        MonetaryCost = monetaryCost;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-usage-ceiling.v1", WriteCanonical);
    }

    /// <summary>Gets the input-token ceiling.</summary>
    public GovernedModelUsageLimit InputTokens { get; }
    /// <summary>Gets the output-token ceiling.</summary>
    public GovernedModelUsageLimit OutputTokens { get; }
    /// <summary>Gets the cached-token ceiling.</summary>
    public GovernedModelUsageLimit CachedTokens { get; }
    /// <summary>Gets the total-token ceiling.</summary>
    public GovernedModelUsageLimit TotalTokens { get; }
    /// <summary>Gets the monetary-cost ceiling.</summary>
    public GovernedModelMonetaryLimit MonetaryCost { get; }
    /// <summary>Gets the canonical content hash.</summary>
    public string ContentHash { get; }
    /// <summary>Gets whether at least one dimension is hard bounded.</summary>
    public bool HasAnyLimit => InputTokens.IsBounded || OutputTokens.IsBounded || CachedTokens.IsBounded || TotalTokens.IsBounded || MonetaryCost.IsBounded;

    /// <summary>Creates a complete ceiling declaration.</summary>
    public static GovernedModelUsageCeiling Create(GovernedModelUsageLimit inputTokens, GovernedModelUsageLimit outputTokens, GovernedModelUsageLimit cachedTokens, GovernedModelUsageLimit totalTokens, GovernedModelMonetaryLimit monetaryCost)
    {
        ArgumentNullException.ThrowIfNull(inputTokens);
        ArgumentNullException.ThrowIfNull(outputTokens);
        ArgumentNullException.ThrowIfNull(cachedTokens);
        ArgumentNullException.ThrowIfNull(totalTokens);
        ArgumentNullException.ThrowIfNull(monetaryCost);
        Validate(inputTokens, nameof(inputTokens));
        Validate(outputTokens, nameof(outputTokens));
        Validate(cachedTokens, nameof(cachedTokens));
        Validate(totalTokens, nameof(totalTokens));
        if (monetaryCost.IsBounded != (monetaryCost.Currency is not null) || monetaryCost.IsBounded != (monetaryCost.MaximumMicros > 0))
        {
            throw new ArgumentException("The monetary ceiling is structurally inconsistent.", nameof(monetaryCost));
        }

        return new GovernedModelUsageCeiling(inputTokens, outputTokens, cachedTokens, totalTokens, monetaryCost);
    }

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteLimit(writer, "cachedTokens", CachedTokens);
        writer.WriteString("currency", MonetaryCost.Currency);
        writer.WriteBoolean("monetaryBounded", MonetaryCost.IsBounded);
        writer.WriteNumber("monetaryMaximumMicros", MonetaryCost.MaximumMicros);
        WriteLimit(writer, "inputTokens", InputTokens);
        WriteLimit(writer, "outputTokens", OutputTokens);
        WriteLimit(writer, "totalTokens", TotalTokens);
        writer.WriteEndObject();
    }

    private static void Validate(GovernedModelUsageLimit limit, string parameterName)
    {
        if (limit.IsBounded != (limit.Maximum > 0) || limit.Maximum > GovernedModelContractLimits.MaxTokens)
        {
            throw new ArgumentException("The token ceiling is structurally inconsistent.", parameterName);
        }
    }

    private static void WriteLimit(Utf8JsonWriter writer, string name, GovernedModelUsageLimit limit)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteBoolean("bounded", limit.IsBounded);
        writer.WriteNumber("maximum", limit.Maximum);
        writer.WriteEndObject();
    }
}
