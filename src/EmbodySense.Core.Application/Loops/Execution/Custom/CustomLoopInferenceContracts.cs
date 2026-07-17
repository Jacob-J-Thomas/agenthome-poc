using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopInferenceAttemptRequest(
    string RunId,
    string LoopId,
    string RoleId,
    int DefinitionVersion,
    string DefinitionHash,
    int Iteration,
    string StepId,
    int Attempt,
    string AttemptCorrelationId,
    bool IsExit,
    bool AllowTools,
    CustomLoopModelSnapshot ModelSnapshot,
    IReadOnlyList<CustomLoopToolAssignment> AdmittedToolAssignments,
    int ToolRequestsUsedInRun,
    LlmInferenceRequest InferenceRequest,
    CustomLoopToolAuthoritySnapshot? AuthoritySnapshot = null);

public sealed record CustomLoopInferenceAttemptResult(
    string OutputText,
    string Provider,
    string? Model,
    string? ProviderResponseId,
    int ToolRequestsConsumed = 0);

public interface ICustomLoopInferenceAttemptExecutor
{
    Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default);
}

public sealed record CustomLoopContextAssembly(
    LlmInferenceRequest Request,
    CustomLoopContextBlock[] Blocks,
    CustomLoopContextOutputPolicy ResolvedOutputPolicy);
