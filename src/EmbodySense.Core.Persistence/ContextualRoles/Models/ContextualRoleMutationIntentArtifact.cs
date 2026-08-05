using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

internal sealed record ContextualRoleMutationIntentArtifact(
    int SchemaVersion,
    string WorkspaceAnchorHash,
    ContextualRoleRevisionMutationRequest Request,
    ContextualRolePrimaryStateArtifact? PriorState,
    ContextualRolePrimaryStateArtifact? PlannedState,
    ContextualRoleRevisionMutationStatus IntendedOutcome,
    DateTimeOffset RecordedAtUtc,
    string IntegrityHash);
