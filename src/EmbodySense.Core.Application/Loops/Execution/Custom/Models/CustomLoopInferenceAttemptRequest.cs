using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop inference attempt request.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="DefinitionVersion">The monotonically increasing definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="StepId">The step ID.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="AttemptCorrelationId">The attempt correlation ID.</param>
/// <param name="IsExit">The is exit.</param>
/// <param name="AllowTools">The allow tools.</param>
/// <param name="ModelSnapshot">The provider and model identity admitted for the run.</param>
/// <param name="AdmittedToolAssignments">The admitted tool assignments.</param>
/// <param name="ToolRequestsUsedInRun">The tool requests used in run.</param>
/// <param name="InferenceRequest">The inference request.</param>
/// <param name="AuthoritySnapshot">The authority snapshot.</param>
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
    CustomLoopToolAuthoritySnapshot? AuthoritySnapshot = null)
{
    /// <summary>Gets the immutable capability pins and resolution evidence admitted for the owning run.</summary>
    public CapabilityAdmissionSnapshot CapabilityAdmission { get; init; } = null!;
}
