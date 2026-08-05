namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies the response-selection policy for a request.
/// </summary>
public enum HumanInputResponsePolicyKind
{
    /// <summary>Unspecified and invalid.</summary>
    Unknown = 0,
    /// <summary>Exactly one response from one explicitly eligible respondent is accepted for later lifecycle handling.</summary>
    FirstEligibleResponse = 1
}
