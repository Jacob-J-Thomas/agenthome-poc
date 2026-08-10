namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies one authenticated respondent, contextual role, and unambiguous routing reference eligible to submit untrusted response data.
/// </summary>
/// <param name="RespondentId">The stable respondent ID expected from authentication.</param>
/// <param name="RespondentRoleId">The stable contextual role that authenticated eligibility must prove for this request.</param>
/// <param name="RoutingReference">The opaque, bounded route reference.</param>
public sealed record HumanInputEligibleRespondent(string RespondentId, string RespondentRoleId, string RoutingReference);
