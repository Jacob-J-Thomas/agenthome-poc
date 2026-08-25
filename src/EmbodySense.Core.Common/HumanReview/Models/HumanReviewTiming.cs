namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines the trusted finite UTC creation, due, and expiry boundaries for one Human Review request.</summary>
/// <param name="CreatedAtUtc">The trusted UTC timestamp at which the immutable request was created.</param>
/// <param name="DueAtUtc">The trusted UTC timestamp at which the request becomes due for wake or escalation.</param>
/// <param name="ExpiresAtUtc">The inclusive trusted UTC deadline after which a new decision cannot be accepted.</param>
public sealed record HumanReviewTiming(DateTimeOffset CreatedAtUtc, DateTimeOffset DueAtUtc, DateTimeOffset ExpiresAtUtc);
