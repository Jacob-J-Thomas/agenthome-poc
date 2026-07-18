using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopContextAssembly(
    LlmInferenceRequest Request,
    CustomLoopContextBlock[] Blocks,
    CustomLoopContextOutputPolicy ResolvedOutputPolicy);
