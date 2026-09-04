namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Describes one opaque server-generated reroute option without respondent or route material.</summary>
/// <param name="CandidateKey">The short-lived opaque key used by the exact lifecycle commit.</param>
/// <param name="Label">The generic safe label for this option.</param>
/// <param name="EligibleRespondentCount">The bounded number of canonical eligible respondents retained by this option.</param>
/// <param name="ExpiresAtUtc">The trusted candidate-registration expiry.</param>
public sealed record HumanInputRerouteCandidateOption(
    string CandidateKey,
    string Label,
    int EligibleRespondentCount,
    DateTimeOffset ExpiresAtUtc);
