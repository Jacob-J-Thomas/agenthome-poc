using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop message snapshot.
/// </summary>
/// <param name="Role">The model message role assigned to the content.</param>
/// <param name="Content">The exact content.</param>
public sealed record CustomLoopMessageSnapshot(
    LlmMessageRole Role,
    string Content);
