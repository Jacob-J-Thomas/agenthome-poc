namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Restricts one delegation to trusted time and, optionally, exact target completion.</summary>
/// <param name="EffectiveAtUtc">The inclusive UTC instant at which use may begin.</param>
/// <param name="ExpiresAtUtc">The optional exclusive UTC expiry instant.</param>
/// <param name="CompletionConstraint">The exact local completion constraint.</param>
public sealed record AuthorityDelegationBoundary(
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    AuthorityDelegationCompletionConstraintKind CompletionConstraint);
