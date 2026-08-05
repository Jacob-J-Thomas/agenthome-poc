using EmbodySense.Core.Application.Governance.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority;

/// <summary>
/// Defines the application boundary for evaluating profile declarations through an externally governed authority source.
/// </summary>
/// <remarks>Implementations own trusted-source selection; this port does not permit a caller, profile, descriptor, provider, or role file to self-grant authority.</remarks>
public interface IAuthorityProfileEvaluator
{
    /// <summary>
    /// Evaluates candidate profiles and projects their non-executing boundary decision.
    /// </summary>
    /// <param name="request">The bounded evaluation request.</param>
    /// <param name="cancellationToken">The token used to cancel the evaluation.</param>
    /// <returns>The candidate/effective ceilings and auditable boundary receipt.</returns>
    Task<AuthorityEvaluationResult> EvaluateAsync(AuthorityEvaluationRequest request, CancellationToken cancellationToken = default);
}
