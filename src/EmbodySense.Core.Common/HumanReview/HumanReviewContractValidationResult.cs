namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewContractValidationResult
{
    /// <summary>
    /// Gets whether the inspected contract is valid.
    /// </summary>
    public bool IsValid => Errors.Count == 0;
}
