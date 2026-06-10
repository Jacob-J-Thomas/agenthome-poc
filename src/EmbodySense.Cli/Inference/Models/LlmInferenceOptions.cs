namespace EmbodySense.Cli.Inference.Models;

internal sealed record LlmInferenceOptions
{
    public static LlmInferenceOptions Default { get; } = new();

    public decimal? Temperature { get; init; }

    public int? MaxOutputTokenCount { get; init; }
}
