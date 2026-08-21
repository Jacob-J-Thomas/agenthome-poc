namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Retains every authoritative deadline that can narrow one credential lease.</summary>
public sealed record CredentialLeaseDeadlines(
    DateTimeOffset ProofExpiresAtUtc,
    DateTimeOffset? ReferenceExpiresAtUtc,
    DateTimeOffset? ScopeExpiresAtUtc,
    DateTimeOffset? GrantExpiresAtUtc,
    DateTimeOffset? DelegationExpiresAtUtc,
    DateTimeOffset? ProfileExpiresAtUtc,
    DateTimeOffset? EffectExpiresAtUtc,
    DateTimeOffset? RuntimeExpiresAtUtc);
