namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies the response-selection policy for a request.
/// </summary>
public enum HumanInputResponsePolicyKind
{
    /// <summary>Unspecified and invalid.</summary>
    Unknown = 0,
    /// <summary>The first durably committed valid response becomes the sole selected response.</summary>
    FirstValid = 1,
    /// <summary>A configured number of distinct eligible respondents must submit the same canonical value hash.</summary>
    Quorum = 2,
    /// <summary>Every authored required respondent role must contribute one active response.</summary>
    NamedRoles = 3,
    /// <summary>One active response from each authored contributor role is selected in authored order without content synthesis.</summary>
    Merge = 4,
    /// <summary>An authenticated selector in an authored selector role must explicitly choose the retained response references.</summary>
    ManualSelection = 5
}
