using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>
/// Represents a bounded, value-free projection of an evaluated authority boundary decision.
/// </summary>
public sealed record AuthorityBoundaryProjection
{
    internal AuthorityBoundaryProjection(AuthorityBoundaryDecision decision, IReadOnlyList<AuthorityBoundaryReason> reasons, DateTimeOffset evaluatedAtUtc)
    {
        Decision = decision;
        Reasons = reasons;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    /// <summary>Gets the direct, review, pause, or deny decision.</summary>
    public AuthorityBoundaryDecision Decision { get; }

    /// <summary>Gets the bounded closed reasons contributing to the decision.</summary>
    public IReadOnlyList<AuthorityBoundaryReason> Reasons { get; }

    /// <summary>Gets the exact UTC evaluation time.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }
}
