namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Proves that one value-free workspace after-evidence reference belongs to a committed canonical effect attempt.</summary>
public interface IWorkspaceActionCommittedAfterEvidenceResolver
{
    /// <summary>Returns true only when the exact effect generation is durably committed with the supplied after-evidence identity.</summary>
    Task<bool> IsCommittedAsync(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        string afterEvidenceId,
        string afterEvidenceHash,
        CancellationToken cancellationToken = default);
}
