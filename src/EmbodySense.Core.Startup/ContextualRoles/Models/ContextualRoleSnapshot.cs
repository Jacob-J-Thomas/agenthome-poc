namespace EmbodySense.Core.Startup.ContextualRoles.Models;

/// <summary>Contains bounded redacted current contextual-role posture without instruction content or authority.</summary>
public sealed record ContextualRoleSnapshot(
    string RoleId,
    int Revision,
    string ContentHash,
    string DisplayName,
    string Purpose,
    string RevisionStatus,
    string LifecycleState,
    string AuthorId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset LifecycleUpdatedAtUtc,
    string InstructionSourceKind,
    string InstructionSourceId,
    string SourceStatus,
    bool IsApplicableToWorkspace,
    bool IsAdmissionReady,
    IReadOnlyList<string> CapabilityMaximumIds,
    IReadOnlyList<ContextualRoleDependentSnapshot> Dependents,
    bool AreDependentsComplete,
    bool DependentsTruncated)
{
    /// <summary>Gets defensive ordered non-granting capability ceiling identities.</summary>
    public IReadOnlyList<string> CapabilityMaximumIds { get; } = Array.AsReadOnly((CapabilityMaximumIds ?? []).ToArray());

    /// <summary>Gets a defensive read-only dependent snapshot.</summary>
    public IReadOnlyList<ContextualRoleDependentSnapshot> Dependents { get; } = Array.AsReadOnly((Dependents ?? []).ToArray());
}
