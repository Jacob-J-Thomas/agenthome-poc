using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Captures the complete server-owned immutable intent for one governed-loop admission operation.</summary>
/// <param name="SchemaVersion">The intent schema version, which must be 1.</param>
/// <param name="WorkspaceId">The canonical non-secret workspace scope.</param>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="RequestHash">The exact canonical hash of stable invocation request data.</param>
/// <param name="Publication">The exact published revision validated for admission.</param>
/// <param name="AuthorityGrant">The exact effective grant revision considered at admission.</param>
/// <param name="Role">The exact contextual-role revision owned by both graph and grant.</param>
/// <param name="ActorId">The authenticated actor retained as attribution, not authority.</param>
/// <param name="Surface">The canonical surface token.</param>
/// <param name="GraphArtifactHash">The exact immutable full graph-artifact hash.</param>
/// <param name="GraphLayoutHash">The exact immutable graph-layout hash.</param>
/// <remarks>This contract contains no graph payload, role instructions, source paths, grant JSON, credentials, secrets, or diagnostics.</remarks>
public sealed record GovernedLoopAdmissionIntent(
    int SchemaVersion,
    string WorkspaceId,
    string OperationId,
    string RequestHash,
    GovernedLoopRevisionPublicationPin Publication,
    AuthorityGrantReference AuthorityGrant,
    ContextualRoleRevisionPin Role,
    AuthorityActorId ActorId,
    string Surface,
    string GraphArtifactHash,
    string GraphLayoutHash)
{
    /// <summary>Gets the only supported experimental intent schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;
}
