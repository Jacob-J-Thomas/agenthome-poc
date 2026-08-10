using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Binds one grant revision to exact profile, role, and published loop revisions.</summary>
/// <param name="Profile">The exact authority-profile revision.</param>
/// <param name="Role">The exact contextual-role revision.</param>
/// <param name="Loop">The exact governed-loop publication pin.</param>
public sealed record AuthorityGrantBinding(AuthorityGrantProfilePin Profile, ContextualRoleRevisionPin Role, GovernedLoopRevisionPublicationPin Loop);
