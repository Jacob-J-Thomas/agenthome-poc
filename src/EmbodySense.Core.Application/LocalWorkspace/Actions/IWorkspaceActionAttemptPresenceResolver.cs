using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Reads whether one exact workspace artifact owner has canonical effect-attempt intent.</summary>
public interface IWorkspaceActionAttemptPresenceResolver
{
    /// <summary>Resolves one exact effect/idempotency/generation/before-evidence binding without choosing recovery policy.</summary>
    Task<WorkspaceActionAttemptPresence> ResolveAsync(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes at most the requested number of exact expired preparation records while one canonical attempt-store
    /// mutation lock proves which records are unreferenced. Ambiguous records are preserved.
    /// </summary>
    Task<WorkspaceActionPreparationCleanupResult> TryCleanupPreparationsAsync(
        IReadOnlyList<WorkspaceActionBeforeEvidence> beforeEvidence,
        int maximumRemovals,
        CancellationToken cancellationToken = default);
}
