using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopContextBlock(
    CustomLoopContextSource Source,
    string SourceId,
    LlmMessageRole Role,
    bool Included,
    string? OmissionReason,
    string Content,
    string ContentHash,
    int CharacterCount,
    bool Truncated,
    string? SourceVersion = null);
