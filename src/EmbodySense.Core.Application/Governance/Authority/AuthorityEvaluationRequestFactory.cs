using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority;

/// <summary>
/// Creates application evaluation requests only from validated, bounded profile inputs.
/// </summary>
public static class AuthorityEvaluationRequestFactory
{
    /// <summary>
    /// Validates and snapshots a candidate profile set before it can cross the evaluator port.
    /// </summary>
    public static bool TryCreate(IReadOnlyList<AuthorityProfile>? profiles, DateTimeOffset evaluatedAtUtc, out AuthorityEvaluationRequest? request, out AuthorityContractValidationResult validation)
    {
        if (!AuthorityProfileSetValidator.TryValidateAndSnapshot(profiles, evaluatedAtUtc, out var snapshot, out validation))
        {
            request = null;
            return false;
        }

        request = new AuthorityEvaluationRequest(snapshot, evaluatedAtUtc);
        return true;
    }
}
