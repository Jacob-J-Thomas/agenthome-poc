using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns exact grant posture and an effective ceiling only when every check is active.</summary>
/// <param name="Status">The closed lifecycle, time, or dependency posture.</param>
/// <param name="RequestedReference">The exact requested grant reference when valid.</param>
/// <param name="Grant">The exact immutable grant revision when safely proved.</param>
/// <param name="EffectiveCeiling">The requested ceiling only for an active result; otherwise the canonical empty ceiling.</param>
/// <param name="DependencyEvidenceHash">The combined exact dependency proof only for an active result.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC resolution instant, or default when unavailable.</param>
/// <param name="CurrentGrant">The exact current immutable revision when the store proved it, including the non-authorizing replacement of a stale requested revision.</param>
public sealed record AuthorityGrantResolution(
    AuthorityGrantResolutionStatus Status,
    AuthorityGrantReference? RequestedReference,
    AuthorityGrant? Grant,
    AuthorityCeiling EffectiveCeiling,
    string DependencyEvidenceHash,
    DateTimeOffset EvaluatedAtUtc,
    AuthorityGrant? CurrentGrant = null);
