using EmbodySense.Core.Common.LocalWorkspace.Actions;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Defines workspace-local persistence ceilings that may only narrow the canonical schema-1 bounds.</summary>
public sealed record WorkspaceActionStorageLimits(
    int MaximumEvidenceRecordsPerKind,
    int MaximumStagingEntries,
    int MaximumTombstones,
    long MaximumQuarantineBytes)
{
    /// <summary>Gets the canonical production ceilings.</summary>
    public static WorkspaceActionStorageLimits Default { get; } = new(
        WorkspaceActionContractLimits.MaxEvidenceRecordsPerKind,
        WorkspaceActionContractLimits.MaxStagingEntries,
        WorkspaceActionContractLimits.MaxTombstones,
        WorkspaceActionContractLimits.MaxQuarantineBytes);

    /// <summary>Validates that a local policy remains positive and never expands a canonical ceiling.</summary>
    public static WorkspaceActionStorageLimits Validate(WorkspaceActionStorageLimits? quota)
    {
        var effective = quota ?? Default;
        if (effective.MaximumEvidenceRecordsPerKind is < 1 or > WorkspaceActionContractLimits.MaxEvidenceRecordsPerKind
            || effective.MaximumStagingEntries is < 1 or > WorkspaceActionContractLimits.MaxStagingEntries
            || effective.MaximumTombstones is < 1 or > WorkspaceActionContractLimits.MaxTombstones
            || effective.MaximumQuarantineBytes is < 1 or > WorkspaceActionContractLimits.MaxQuarantineBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(quota), "Workspace action persistence policy must positively narrow the canonical schema-1 ceilings.");
        }
        return effective;
    }
}
