namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Returns the bounded outcome of exact ceiling-subset evaluation.</summary>
/// <param name="Violations">The bounded value-free subset violations.</param>
/// <param name="IsSubset">Whether the requested ceiling is a subset of every supplied maximum.</param>
public sealed record AuthorityCeilingSubsetResult(IReadOnlyList<AuthorityCeilingSubsetViolation> Violations, bool IsSubset);
