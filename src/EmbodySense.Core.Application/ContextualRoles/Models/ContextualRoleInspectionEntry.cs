using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Combines safe role metadata with current lifecycle, registered-source, and dependency posture.</summary>
public sealed record ContextualRoleInspectionEntry(
    ContextualRoleRevision Revision,
    ContextualRoleLifecycleSnapshot Lifecycle,
    ContextualRoleInstructionSourceProbeStatus SourceStatus,
    bool IsApplicableToWorkspace,
    bool IsAdmissionReady,
    IReadOnlyList<ContextualRoleDependencyImpact> Dependents,
    bool AreDependentsComplete,
    bool DependentsTruncated)
{
    /// <summary>Gets a defensive read-only dependent snapshot.</summary>
    public IReadOnlyList<ContextualRoleDependencyImpact> Dependents { get; } = Array.AsReadOnly((Dependents ?? []).ToArray());
}
