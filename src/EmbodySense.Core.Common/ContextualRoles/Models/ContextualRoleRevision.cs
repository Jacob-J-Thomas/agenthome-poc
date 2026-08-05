namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Represents an exact immutable contextual-role revision and its declarative constraints.</summary>
/// <param name="SchemaVersion">The supported schema version.</param>
/// <param name="Identity">The stable role and immutable revision identity.</param>
/// <param name="ContentHash">The canonical semantic content hash.</param>
/// <param name="DisplayName">Human-facing display metadata excluded from canonical semantic hashing.</param>
/// <param name="Purpose">The role's bounded semantic purpose.</param>
/// <param name="Status">The closed lifecycle status.</param>
/// <param name="Provenance">The recorded author and timestamps.</param>
/// <param name="WorkspaceApplicability">The declarative applicable workspace identifiers.</param>
/// <param name="InstructionSource">The classified, opaque role instruction source reference.</param>
/// <param name="PolicyMaxima">The non-granting capability ceilings.</param>
public sealed record ContextualRoleRevision(
    int SchemaVersion,
    ContextualRoleRevisionIdentity Identity,
    string ContentHash,
    string DisplayName,
    string Purpose,
    ContextualRoleStatus Status,
    ContextualRoleProvenance Provenance,
    ContextualRoleWorkspaceApplicability WorkspaceApplicability,
    ContextualRoleInstructionSourceReference InstructionSource,
    ContextualRolePolicyMaxima PolicyMaxima);
