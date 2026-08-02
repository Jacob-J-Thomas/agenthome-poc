using System.Collections.Immutable;

namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Declares the workspace identifiers to which a role revision applies without resolving filesystem paths.</summary>
/// <param name="WorkspaceIds">The immutable explicit workspace identifiers.</param>
public sealed record ContextualRoleWorkspaceApplicability(ImmutableArray<string> WorkspaceIds)
;
