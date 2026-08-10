namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Returns one bounded deterministic role catalog with current fail-closed source posture.</summary>
public sealed record ContextualRoleInspectionCatalogResult(ContextualRoleCatalogReadStatus Status, IReadOnlyList<ContextualRoleInspectionEntry> Entries, string? NextCursor)
{
    /// <summary>Gets a defensive read-only inspected entry snapshot.</summary>
    public IReadOnlyList<ContextualRoleInspectionEntry> Entries { get; } = Array.AsReadOnly((Entries ?? []).ToArray());
}
