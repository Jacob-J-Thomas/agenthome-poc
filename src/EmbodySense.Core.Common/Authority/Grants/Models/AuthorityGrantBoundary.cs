namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Declares trusted-time and later runtime-completion limits for one grant revision.</summary>
/// <param name="EffectiveAtUtc">The inclusive exact UTC instant before which the grant is ineffective.</param>
/// <param name="ExpiresAtUtc">The optional exclusive-use expiry boundary; the grant is expired when this value is at or before trusted now.</param>
/// <param name="CompletionConstraint">The declarative bound consumed by later runtime enforcement.</param>
public sealed record AuthorityGrantBoundary(DateTimeOffset EffectiveAtUtc, DateTimeOffset? ExpiresAtUtc, AuthorityGrantCompletionConstraintKind CompletionConstraint);
