namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>
/// Holds deterministic results from schema-contract validation.
/// </summary>
/// <param name="Errors">The validation errors in canonical order.</param>
public sealed partial record HumanReviewContractValidationResult(IReadOnlyList<HumanReviewContractValidationError> Errors);
