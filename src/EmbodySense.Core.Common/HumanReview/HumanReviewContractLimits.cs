namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Defines the finite schema-version-1 bounds for durable Human Review contracts.</summary>
public static class HumanReviewContractLimits
{
    /// <summary>The only supported Human Review schema version.</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>The maximum character count of one canonical Human Review identifier.</summary>
    public const int MaxIdentifierCharacters = 120;
    /// <summary>The fixed character count of a lowercase SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
    /// <summary>The maximum number of decision kinds offered by one request.</summary>
    public const int MaxRequestedDecisions = 4;
    /// <summary>The maximum number of eligible reviewer role and scope entries.</summary>
    public const int MaxEligibleReviewers = 16;
    /// <summary>The maximum number of exact scopes retained for one reviewer role or submitted decision.</summary>
    public const int MaxScopesPerReviewer = 16;
    /// <summary>The maximum number of redacted previews retained by one artifact.</summary>
    public const int MaxPreviews = 3;
    /// <summary>The maximum character count of a preview label.</summary>
    public const int MaxPreviewLabelCharacters = 120;
    /// <summary>The maximum character count of one redacted preview.</summary>
    public const int MaxPreviewDetailCharacters = 1_024;
    /// <summary>The maximum character count of untrusted reviewer decision detail.</summary>
    public const int MaxDecisionDetailCharacters = 1_024;
    /// <summary>The maximum positive node-attempt number.</summary>
    public const int MaxNodeAttempt = 1_000_000;
    /// <summary>The maximum positive activation or visit ordinal.</summary>
    public const int MaxActivationOrVisit = 1_000_000;
    /// <summary>The maximum retained positive version value.</summary>
    public const long MaxVersion = 1_000_000_000;
    /// <summary>The longest permitted request window from creation through expiry.</summary>
    public static readonly TimeSpan MaxReviewWindow = TimeSpan.FromDays(30);
}
