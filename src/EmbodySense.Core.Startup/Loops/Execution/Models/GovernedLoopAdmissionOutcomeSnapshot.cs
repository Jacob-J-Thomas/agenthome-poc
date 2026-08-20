using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one validated immutable admission outcome without graph, authority, capability, or context payloads.</summary>
/// <param name="Status">The actual closed application admission status.</param>
/// <param name="Disposition">The validated immutable terminal disposition.</param>
/// <param name="OperationId">The exact workspace-global invocation operation.</param>
/// <param name="RequestHash">The exact canonical caller-stable admission request hash.</param>
/// <param name="WorkspaceId">The exact server-owned workspace scope.</param>
/// <param name="Publication">The exact admitted publication pin.</param>
/// <param name="AuthorityGrant">The exact admitted authority-grant revision.</param>
/// <param name="Role">The exact server-resolved contextual-role revision.</param>
/// <param name="ActorId">The exact authenticated actor attribution.</param>
/// <param name="Surface">The exact server-owned invocation surface.</param>
/// <param name="RunId">The exact admitted run identity, or <see langword="null"/> for rejection.</param>
/// <param name="FailureCode">The value-free definitive rejection code, or <see langword="null"/> for admission.</param>
/// <param name="OutcomeHash">The validated immutable terminal-outcome content hash.</param>
public sealed record GovernedLoopAdmissionOutcomeSnapshot(
    string Status,
    string Disposition,
    string OperationId,
    string RequestHash,
    string WorkspaceId,
    GovernedLoopRevisionPublicationPin Publication,
    AuthorityGrantReference AuthorityGrant,
    ContextualRoleRevisionPin Role,
    string ActorId,
    string Surface,
    string? RunId,
    string? FailureCode,
    string OutcomeHash);
