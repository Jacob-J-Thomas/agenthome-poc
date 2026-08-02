namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Classifies the sensitivity of an untrusted human-input exchange.
/// </summary>
public enum HumanInputPrivacyClass
{
    /// <summary>Unspecified and invalid for schema-1 requests.</summary>
    Unknown = 0,
    /// <summary>The exchange is limited to the explicit eligible respondents and bound continuation.</summary>
    Private = 1,
    /// <summary>The exchange has heightened handling requirements but remains limited to the explicit eligible respondents and bound continuation.</summary>
    Sensitive = 2
}
