using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Represents one exact nonnegative provider-usage reservation, consumption, or release vector.</summary>
public sealed class GovernedModelUsageVector
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageVector(long inputTokens, long outputTokens, long cachedTokens, long totalTokens, string? currency, long costMicros)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedTokens = cachedTokens;
        TotalTokens = totalTokens;
        Currency = currency;
        CostMicros = costMicros;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-usage-vector.v1", WriteCanonical);
    }

    /// <summary>Gets input tokens.</summary>
    public long InputTokens { get; }
    /// <summary>Gets output tokens.</summary>
    public long OutputTokens { get; }
    /// <summary>Gets cached tokens.</summary>
    public long CachedTokens { get; }
    /// <summary>Gets total tokens.</summary>
    public long TotalTokens { get; }
    /// <summary>Gets the exact optional currency.</summary>
    public string? Currency { get; }
    /// <summary>Gets integer micros in <see cref="Currency"/>.</summary>
    public long CostMicros { get; }
    /// <summary>Gets the canonical vector hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated exact usage vector.</summary>
    public static GovernedModelUsageVector Create(long inputTokens, long outputTokens, long cachedTokens, long totalTokens, string? currency, long costMicros)
    {
        inputTokens = GovernedModelContractRules.RequireQuantity(inputTokens, GovernedModelContractLimits.MaxTokens, nameof(inputTokens));
        outputTokens = GovernedModelContractRules.RequireQuantity(outputTokens, GovernedModelContractLimits.MaxTokens, nameof(outputTokens));
        cachedTokens = GovernedModelContractRules.RequireQuantity(cachedTokens, GovernedModelContractLimits.MaxTokens, nameof(cachedTokens));
        totalTokens = GovernedModelContractRules.RequireQuantity(totalTokens, GovernedModelContractLimits.MaxTokens, nameof(totalTokens));
        costMicros = GovernedModelContractRules.RequireQuantity(costMicros, GovernedModelContractLimits.MaxCurrencyMicros, nameof(costMicros));
        if (costMicros > 0 && currency is null)
        {
            throw new ArgumentException("A nonzero integer-micros value requires an exact currency.", nameof(currency));
        }

        var canonicalCurrency = currency is null ? null : GovernedModelContractRules.RequireCurrency(currency, nameof(currency));
        return new GovernedModelUsageVector(inputTokens, outputTokens, cachedTokens, totalTokens, canonicalCurrency, costMicros);
    }

    /// <summary>Gets an all-zero, currency-free vector.</summary>
    public static GovernedModelUsageVector Zero { get; } = Create(0, 0, 0, 0, null, 0);

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("cachedTokens", CachedTokens);
        writer.WriteNumber("costMicros", CostMicros);
        writer.WriteString("currency", Currency);
        writer.WriteNumber("inputTokens", InputTokens);
        writer.WriteNumber("outputTokens", OutputTokens);
        writer.WriteNumber("totalTokens", TotalTokens);
        writer.WriteEndObject();
    }
}
