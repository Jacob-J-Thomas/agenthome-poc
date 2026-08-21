using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Binds delegation to one exact immutable role, loop, or node target.</summary>
/// <param name="Kind">The exact target kind.</param>
/// <param name="Role">The exact contextual-role revision.</param>
/// <param name="Loop">The exact published loop revision for loop and node targets.</param>
/// <param name="NodeId">The exact node identity for node targets.</param>
/// <param name="BindingEvidenceHash">The stable server-owned semantic binding-evidence hash.</param>
public sealed record AuthorityDelegationTargetBinding(
    AuthorityDelegationTargetKind Kind,
    ContextualRoleRevisionPin Role,
    GovernedLoopRevisionPublicationPin? Loop,
    string? NodeId,
    string BindingEvidenceHash);
