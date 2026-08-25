namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>
/// Describes one deterministic schema-contract validation failure.
/// </summary>
/// <param name="Code">The stable machine-readable validation code.</param>
/// <param name="Path">The contract path containing the invalid value.</param>
/// <param name="Message">The safe, human-readable validation message.</param>
public sealed record HumanReviewContractValidationError(string Code, string Path, string Message);
