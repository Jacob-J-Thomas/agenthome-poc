namespace EmbodySense.Core.Common.Inference.Models;

public sealed record LlmInferenceOptions
{
    public static LlmInferenceOptions Default { get; } = new();

    public decimal? Temperature { get; init; }

    public int? MaxOutputTokenCount { get; init; }
}
