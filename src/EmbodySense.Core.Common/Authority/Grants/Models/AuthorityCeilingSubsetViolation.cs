namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Reports one value-free requested-authority subset violation.</summary>
/// <param name="Code">The closed violation classification.</param>
public sealed record AuthorityCeilingSubsetViolation(AuthorityCeilingSubsetViolationCode Code);
