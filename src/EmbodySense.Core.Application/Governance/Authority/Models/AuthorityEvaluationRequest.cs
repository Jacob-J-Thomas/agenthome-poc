using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>
/// Requests evaluation of bounded candidate authority profiles at an exact UTC instant.
/// </summary>
/// <remarks>The request does not contain an approval, grant, trust assertion, or effect request.</remarks>
public sealed record AuthorityEvaluationRequest
{
    internal AuthorityEvaluationRequest(IReadOnlyList<AuthorityProfile> profiles, DateTimeOffset evaluatedAtUtc)
    {
        Profiles = profiles;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    /// <summary>Gets the candidate profiles selected by the implementation's separately governed source.</summary>
    public IReadOnlyList<AuthorityProfile> Profiles { get; }

    /// <summary>Gets the exact UTC time used for expiry and receipt evidence.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }
}
