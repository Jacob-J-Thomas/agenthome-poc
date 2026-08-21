namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies one closed inference capability.</summary>
public enum GovernedModelCapability
{
    /// <summary>The value is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Schema-constrained structured output.</summary>
    StructuredOutput = 1,
    /// <summary>Governed tool calling.</summary>
    ToolCalling = 2,
    /// <summary>Incremental response streaming.</summary>
    Streaming = 3
}
