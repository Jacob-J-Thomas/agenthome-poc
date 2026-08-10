namespace EmbodySense.Core.Startup.ContextualRoles.Models;

/// <summary>Returns one bounded deterministic page of redacted contextual-role posture.</summary>
public sealed record ContextualRoleCatalogResponse(string Status, IReadOnlyList<ContextualRoleSnapshot> Roles, string? NextCursor, ContextualRoleError? Error)
{
    /// <summary>Gets a defensive read-only role snapshot.</summary>
    public IReadOnlyList<ContextualRoleSnapshot> Roles { get; } = Array.AsReadOnly((Roles ?? []).ToArray());
}
