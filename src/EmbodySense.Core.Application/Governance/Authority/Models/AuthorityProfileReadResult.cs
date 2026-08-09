namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Returns one profile record or a fail-closed read outcome.</summary>
/// <param name="Status">The trustworthy read status.</param>
/// <param name="Record">The profile record when available.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record AuthorityProfileReadResult(AuthorityProfileReadStatus Status, AuthorityProfileRecord? Record, string Detail);
