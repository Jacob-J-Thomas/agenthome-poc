namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies one authenticated respondent and one unambiguous routing reference eligible to submit untrusted response data.
/// </summary>
/// <param name="RespondentId">The stable respondent ID expected from authentication.</param>
/// <param name="RoutingReference">The opaque, bounded route reference.</param>
public sealed record HumanInputEligibleRespondent(string RespondentId, string RoutingReference);
