namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Associates one closed reason with a non-executing authority boundary decision.
/// </summary>
/// <param name="Decision">The direct, review, pause, or denial decision.</param>
/// <param name="Reason">The closed reason for that decision.</param>
public sealed record AuthorityBoundaryCondition(AuthorityBoundaryDecision Decision, AuthorityBoundaryReason Reason);
