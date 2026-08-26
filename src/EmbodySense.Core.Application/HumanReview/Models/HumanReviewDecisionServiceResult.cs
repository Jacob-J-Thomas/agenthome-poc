using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one safe decision-service disposition and, only for an authorized replay or recorded outcome, its detached receipt.</summary>
/// <param name="Status">The result status.</param>
/// <param name="Receipt">The detached receipt when disclosure is authorized.</param>
public sealed record HumanReviewDecisionServiceResult(HumanReviewDecisionServiceStatus Status, HumanReviewDecisionOperationReceipt? Receipt);
