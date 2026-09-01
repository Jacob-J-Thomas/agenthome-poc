namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Projects one bounded Human Review recovery pass without exposing Application candidates or authority evidence.</summary>
/// <param name="Status">The aggregate fail-closed pass posture.</param>
/// <param name="Publication">The wake-less approval publication lane result.</param>
/// <param name="ContinuationScanCursor">The opaque next approved-continuation scan cursor.</param>
/// <param name="DecisionActionScanCursor">The opaque next non-approval action scan cursor.</param>
public sealed record HumanReviewRecoveryPassResult(
    HumanReviewRecoveryPassStatus Status,
    HumanReviewPublicationRecoveryResult Publication,
    string? ContinuationScanCursor,
    string? DecisionActionScanCursor);
