using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Defines the server-owned native exact-target preparation, commit, and read-only reconciliation seam.</summary>
public interface IWorkspaceActionNativeHost
{
    /// <summary>Opens and inspects an exact target without mutating it, then retains immutable value-free before evidence.</summary>
    Task<WorkspaceActionNativePreparation?> PrepareAsync(WorkspaceActionInput input, CancellationToken cancellationToken = default);

    /// <summary>Reauthenticates exact retained preparation evidence without creating or cleaning artifacts.</summary>
    Task<bool> IsPreparationCurrentAsync(
        WorkspaceActionInput input,
        string targetFingerprint,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default);

    /// <summary>Reauthenticates retained before evidence and executes one native commit inside the supplied durable boundary.</summary>
    Task<WorkspaceActionNativeCommitResult> ExecuteAsync(
        WorkspaceActionNativeExecutionRequest request,
        IWorkspaceActionNativeDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken = default);

    /// <summary>Performs one read-only exact-evidence probe without retry, repair, restore, compensation, or disposition.</summary>
    Task<WorkspaceActionReconciliationProbeResult> ProbeAsync(
        WorkspaceActionReconciliationProbeRequest request,
        CancellationToken cancellationToken = default);
}
