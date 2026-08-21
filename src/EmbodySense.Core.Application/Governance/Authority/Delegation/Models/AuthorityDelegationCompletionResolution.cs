namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Reports exact parent and target completion posture without mutating either source.</summary>
/// <param name="Status">The closed completion posture.</param>
public sealed record AuthorityDelegationCompletionResolution(AuthorityDelegationCompletionStatus Status);
