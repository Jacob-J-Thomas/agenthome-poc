namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Represents a current contextual-role lifecycle read.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="Snapshot">The proved current lifecycle projection when found.</param>
public sealed record ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus Status, ContextualRoleLifecycleSnapshot? Snapshot);
