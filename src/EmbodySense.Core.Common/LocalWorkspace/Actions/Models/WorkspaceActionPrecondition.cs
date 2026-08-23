namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains exactly one closed optimistic precondition and its required evidence.</summary>
/// <param name="Kind">The selected precondition kind.</param>
/// <param name="ExpectedContentHash">The exact lowercase SHA-256 content hash for content-hash checks.</param>
/// <param name="ExpectedGovernedVersion">The positive governed version for version checks.</param>
/// <param name="PriorAfterEvidenceId">The exact prior committed after-evidence reference for version checks.</param>
/// <param name="PriorAfterEvidenceHash">The exact prior committed after-evidence content hash for version checks.</param>
public sealed record WorkspaceActionPrecondition(
    WorkspaceActionPreconditionKind Kind,
    string? ExpectedContentHash,
    long? ExpectedGovernedVersion,
    string? PriorAfterEvidenceId,
    string? PriorAfterEvidenceHash);
