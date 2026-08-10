namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Represents one bounded deterministic contextual-role catalog page.</summary>
/// <param name="Status">The closed page outcome.</param>
/// <param name="Entries">The safe orchestration entries when available.</param>
/// <param name="NextCursor">The exclusive role cursor when more entries remain.</param>
public sealed record ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus Status, IReadOnlyList<ContextualRoleCatalogEntry> Entries, string? NextCursor)
{
    /// <summary>Gets a defensive read-only entry snapshot.</summary>
    public IReadOnlyList<ContextualRoleCatalogEntry> Entries { get; } = Array.AsReadOnly((Entries ?? []).ToArray());
}
