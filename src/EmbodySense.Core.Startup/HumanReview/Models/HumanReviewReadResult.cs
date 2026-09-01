namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Returns one exact detached Human Review detail projection.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="Detail">The detached review detail when the read succeeded.</param>
public sealed record HumanReviewReadResult(HumanReviewReadStatus Status, HumanReviewDetail? Detail = null);
