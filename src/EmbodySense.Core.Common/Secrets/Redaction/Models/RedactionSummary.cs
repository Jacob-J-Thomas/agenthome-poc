namespace EmbodySense.Core.Common.Secrets.Redaction.Models;

/// <summary>
/// Reports bounded, value-free evidence from one text-redaction operation.
/// </summary>
/// <param name="Status">Whether the operation completed or failed closed at a configured limit.</param>
/// <param name="SensitiveValueCount">Number of non-empty sensitive values admitted to the scope.</param>
/// <param name="IgnoredValueCount">Number of empty or duplicate sensitive values ignored by the scope.</param>
/// <param name="ReplacementCount">Number of non-overlapping supported patterns replaced before completion or fail-closed termination.</param>
/// <param name="ExaminedCharacterCount">Number of input character positions examined before completion or termination.</param>
/// <param name="WorkUnitCount">Number of bounded pattern checks and character comparisons performed.</param>
public sealed record RedactionSummary(
    RedactionStatus Status,
    int SensitiveValueCount,
    int IgnoredValueCount,
    int ReplacementCount,
    int ExaminedCharacterCount,
    int WorkUnitCount);
