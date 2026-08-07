using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Projects one immutable canonical profile revision and the operation that created it.</summary>
/// <param name="Profile">The complete canonical profile revision.</param>
/// <param name="Hash">The canonical profile hash.</param>
/// <param name="OperationId">The immutable operation identity that retained this revision.</param>
/// <param name="RecordedAtUtc">The trusted durable-record time.</param>
public sealed record AuthorityProfileRevisionEvidence(AuthorityProfile Profile, AuthorityProfileHash Hash, string OperationId, DateTimeOffset RecordedAtUtc);
