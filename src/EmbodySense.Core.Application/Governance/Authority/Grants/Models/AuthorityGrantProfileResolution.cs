using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns exact current profile-revision posture without following replacement.</summary>
/// <param name="Status">The closed exact-dependency posture.</param>
/// <param name="RequestedPin">The exact caller-supplied profile pin when valid.</param>
/// <param name="Profile">The exact immutable profile revision when safely proved.</param>
/// <param name="EvidenceHash">The canonical current-state evidence digest when safely proved.</param>
public sealed record AuthorityGrantProfileResolution(
    AuthorityGrantDependencyStatus Status,
    AuthorityGrantProfilePin? RequestedPin,
    AuthorityProfile? Profile,
    string EvidenceHash);
