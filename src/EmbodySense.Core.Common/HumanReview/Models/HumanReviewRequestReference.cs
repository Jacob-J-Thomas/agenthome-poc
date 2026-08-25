namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable Human Review request version by its globally unique request identity and canonical hash.</summary>
/// <param name="RequestId">The globally unique request identity.</param>
/// <param name="RequestHash">The exact canonical request hash.</param>
public sealed record HumanReviewRequestReference(string RequestId, string RequestHash);
