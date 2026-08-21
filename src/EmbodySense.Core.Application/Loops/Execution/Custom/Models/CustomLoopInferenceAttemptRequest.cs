using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

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

    /// <summary>Gets the complete immutable canonical admission proof retained by the sequential binding.</summary>
    public GovernedLoopAdmissionReceipt? AdmissionReceipt { get; init; }

    /// <summary>Gets the exact canonical run, graph revision, and execution generation.</summary>
    public GovernedLoopExecutionBinding? ExecutionBinding { get; init; }

    /// <summary>Gets the exact immutable graph artifact whose node ceiling governs this attempt.</summary>
    public GovernedLoopGraphRevisionArtifact? GraphArtifact { get; init; }

    /// <summary>Gets the exact zero-based admitted-plan coordinate for a canonical attempt, or -1 for legacy dispatch.</summary>
    public int PlanOrdinal { get; init; } = -1;

    /// <summary>Gets the exact zero-based durable activation coordinate for a canonical attempt, or -1 for legacy dispatch.</summary>
    public int ActivationOrdinal { get; init; } = -1;

    /// <summary>Gets the exact positive node visit coordinate for a canonical attempt, or zero for legacy dispatch.</summary>
    public int VisitOrdinal { get; init; }

    /// <summary>Gets the server-owned exact frontier attempt operation for canonical dispatch.</summary>
    public string? AttemptOperationId { get; init; }
}
