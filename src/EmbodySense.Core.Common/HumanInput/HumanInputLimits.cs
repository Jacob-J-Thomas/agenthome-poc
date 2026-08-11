namespace EmbodySense.Core.Common.HumanInput;

/// <summary>
/// Defines the fixed resource limits for schema-1 human-input contracts.
/// </summary>
public static class HumanInputLimits
{
    /// <summary>Maximum characters in any stable human-input identifier.</summary>
    public const int MaxIdentifierCharacters = 120;
    /// <summary>Maximum purpose characters.</summary>
    public const int MaxPurposeCharacters = 240;
    /// <summary>Maximum prompt characters.</summary>
    public const int MaxPromptCharacters = 4_000;
    /// <summary>Maximum respondent routing-reference characters.</summary>
    public const int MaxRoutingReferenceCharacters = 240;
    /// <summary>Maximum explicitly eligible respondents in one request.</summary>
    public const int MaxEligibleRespondents = 16;
    /// <summary>Maximum authored respondent-role entries in one response policy.</summary>
    public const int MaxResponsePolicyRoles = MaxEligibleRespondents;
    /// <summary>Maximum choices in a choice schema.</summary>
    public const int MaxChoices = 16;
    /// <summary>Maximum display characters in a choice.</summary>
    public const int MaxChoiceDisplayCharacters = 240;
    /// <summary>Maximum structured fields in a schema.</summary>
    public const int MaxStructuredFields = 12;
    /// <summary>Maximum characters in a text response or structured text value.</summary>
    public const int MaxResponseTextCharacters = 4_000;
    /// <summary>Maximum explanation characters.</summary>
    public const int MaxExplanationCharacters = 1_000;
    /// <summary>Maximum artifact or safe-reference characters.</summary>
    public const int MaxReferenceCharacters = 512;
    /// <summary>Minimum bounded response window.</summary>
    public static readonly TimeSpan MinResponseWindow = TimeSpan.FromMinutes(1);
    /// <summary>Maximum bounded response window.</summary>
    public static readonly TimeSpan MaxResponseWindow = TimeSpan.FromDays(30);
    /// <summary>Number of lowercase hexadecimal characters in a SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
}
