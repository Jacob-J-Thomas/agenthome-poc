using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation;

internal sealed record InvocationPreparationTerms(
    GovernedLoopInvocationPreparationStatus Status,
    GovernedLoopRevisionPublicationPin? Publication,
    GovernedLoopGrantBindingResolution? Binding,
    AuthorityGrantRoleResolution? Role,
    AuthorityCeiling? Ceiling,
    string SemanticHash,
    string Detail)
{
    public static InvocationPreparationTerms Failure(GovernedLoopInvocationPreparationStatus status, GovernedLoopRevisionPublicationPin? publication, string detail)
        => new(status, publication, null, null, null, string.Empty, detail);

    public static InvocationPreparationTerms Success(GovernedLoopRevisionPublicationPin publication, GovernedLoopGrantBindingResolution binding, AuthorityGrantRoleResolution role, AuthorityCeiling ceiling, string semanticHash)
        => new(GovernedLoopInvocationPreparationStatus.Ready, publication, binding, role, ceiling, semanticHash, "Current exact least-authority terms are available.");
}
