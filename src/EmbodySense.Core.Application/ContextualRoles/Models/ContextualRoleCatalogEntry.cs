using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Pairs one proved current lifecycle projection with its exact immutable role revision.</summary>
/// <param name="Revision">The exact current immutable revision.</param>
/// <param name="Lifecycle">The proved current lifecycle projection.</param>
public sealed record ContextualRoleCatalogEntry(ContextualRoleRevision Revision, ContextualRoleLifecycleSnapshot Lifecycle);
