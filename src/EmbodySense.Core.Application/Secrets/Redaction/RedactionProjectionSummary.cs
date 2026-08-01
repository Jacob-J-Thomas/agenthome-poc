namespace EmbodySense.Core.Application.Secrets.Redaction;

/// <summary>
/// Reports bounded, value-free evidence for a structured redaction projection.
/// </summary>
/// <param name="SensitiveValueCount">Number of non-empty sensitive values admitted to the per-use scope.</param>
/// <param name="IgnoredValueCount">Number of empty or duplicate sensitive values ignored by the scope.</param>
/// <param name="TextReplacementCount">Total supported-pattern replacements across sanitized text fields.</param>
/// <param name="VisitedNodeCount">Total structured or exception nodes visited.</param>
/// <param name="ProjectedCharacterCount">Total sanitized characters retained in the aggregate projection.</param>
/// <param name="LimitCount">Total bound or cycle conditions encountered, including conditions represented by an empty fail-closed projection.</param>
/// <param name="FailureCount">Total read or unsupported-value conditions encountered.</param>
/// <param name="ProjectionSafetyFailureCount">Total text projections rejected because replacement text synthesized another scoped sensitive value.</param>
public sealed record RedactionProjectionSummary(
    int SensitiveValueCount,
    int IgnoredValueCount,
    int TextReplacementCount,
    int VisitedNodeCount,
    int ProjectedCharacterCount,
    int LimitCount,
    int FailureCount,
    int ProjectionSafetyFailureCount)
{
    /// <summary>Gets whether the projection completed without a bound, cycle, read, unsupported-value, or projection-safety marker.</summary>
    public bool IsComplete => LimitCount == 0 && FailureCount == 0 && ProjectionSafetyFailureCount == 0;
}
