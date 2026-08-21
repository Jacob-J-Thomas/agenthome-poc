using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Declares authoritative reporting and hard-enforcement support for every provider-usage dimension.</summary>
public sealed class GovernedModelUsageSupportPolicy
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelUsageSupportPolicy(GovernedModelUsageSupport inputTokens, GovernedModelUsageSupport outputTokens, GovernedModelUsageSupport cachedTokens, GovernedModelUsageSupport totalTokens, GovernedModelUsageSupport monetaryCost)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedTokens = cachedTokens;
        TotalTokens = totalTokens;
        MonetaryCost = monetaryCost;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-usage-support.v1", WriteCanonical);
    }

    /// <summary>Gets input-token support.</summary>
    public GovernedModelUsageSupport InputTokens { get; }
    /// <summary>Gets output-token support.</summary>
    public GovernedModelUsageSupport OutputTokens { get; }
    /// <summary>Gets cached-token support.</summary>
    public GovernedModelUsageSupport CachedTokens { get; }
    /// <summary>Gets total-token support.</summary>
    public GovernedModelUsageSupport TotalTokens { get; }
    /// <summary>Gets monetary-cost support.</summary>
    public GovernedModelUsageSupport MonetaryCost { get; }
    /// <summary>Gets the canonical content hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a complete explicit support declaration.</summary>
    public static GovernedModelUsageSupportPolicy Create(GovernedModelUsageSupport inputTokens, GovernedModelUsageSupport outputTokens, GovernedModelUsageSupport cachedTokens, GovernedModelUsageSupport totalTokens, GovernedModelUsageSupport monetaryCost)
    {
        RequireDefined(inputTokens, nameof(inputTokens));
        RequireDefined(outputTokens, nameof(outputTokens));
        RequireDefined(cachedTokens, nameof(cachedTokens));
        RequireDefined(totalTokens, nameof(totalTokens));
        RequireDefined(monetaryCost, nameof(monetaryCost));
        return new GovernedModelUsageSupportPolicy(inputTokens, outputTokens, cachedTokens, totalTokens, monetaryCost);
    }

    internal void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("cachedTokens", (int)CachedTokens);
        writer.WriteNumber("inputTokens", (int)InputTokens);
        writer.WriteNumber("monetaryCost", (int)MonetaryCost);
        writer.WriteNumber("outputTokens", (int)OutputTokens);
        writer.WriteNumber("totalTokens", (int)TotalTokens);
        writer.WriteEndObject();
    }

    private static void RequireDefined(GovernedModelUsageSupport value, string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Usage support must use a defined schema-1 value.");
        }
    }
}
