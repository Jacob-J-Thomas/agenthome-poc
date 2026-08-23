namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Reports one bounded, authenticated expired-preparation cleanup pass.</summary>
/// <param name="EvidenceComplete">Whether all attempt and before-evidence needed by the pass was authenticated.</param>
/// <param name="RemovedCount">The number of exact unreferenced before records removed.</param>
public sealed record WorkspaceActionPreparationCleanupResult(bool EvidenceComplete, int RemovedCount)
{
    /// <summary>Gets the fail-closed result used when reference or cleanup evidence is unavailable.</summary>
    public static WorkspaceActionPreparationCleanupResult Unknown { get; } = new(false, 0);
}
