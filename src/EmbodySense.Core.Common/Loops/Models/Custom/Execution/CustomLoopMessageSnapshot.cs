using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopMessageSnapshot(
    LlmMessageRole Role,
    string Content);
