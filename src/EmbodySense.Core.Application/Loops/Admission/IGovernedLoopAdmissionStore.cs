using EmbodySense.Core.Application.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Loops.Admission;

/// <summary>Atomically persists immutable governed-loop admission outcomes under workspace-global operation identities.</summary>
public interface IGovernedLoopAdmissionStore
{
    /// <summary>Reads the terminal outcome already bound to one exact workspace and operation identity.</summary>
    /// <param name="workspaceId">The server-owned canonical workspace scope.</param>
    /// <param name="operationId">The workspace-global admission operation identity.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The exact store generation and retained outcome when safely available.</returns>
    Task<GovernedLoopAdmissionStoreReadResult> ReadByOperationAsync(
        string workspaceId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Commits one validated terminal outcome against an exact optimistic store generation.</summary>
    /// <param name="mutation">The exact application-prepared admission mutation.</param>
    /// <param name="cancellationToken">A token used only until durable intent begins.</param>
    /// <returns>The exact durable, replay, conflict, unavailable, or ambiguous disposition.</returns>
    Task<GovernedLoopAdmissionStoreCommitResult> CommitAsync(
        GovernedLoopAdmissionStoreMutation mutation,
        CancellationToken cancellationToken = default);
}
