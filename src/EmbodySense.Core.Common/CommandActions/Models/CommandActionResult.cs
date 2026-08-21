namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Returns one bounded value-free graph command Action result.</summary>
/// <param name="SchemaVersion">The result schema, which must be 1.</param>
/// <param name="Status">Whether the exact effect outcome was first committed or replayed.</param>
/// <param name="Outcome">The conclusive command outcome.</param>
/// <param name="OutcomeEvidenceId">The exact retained redacted outcome-evidence reference.</param>
/// <param name="EffectGeneration">The exact positive effect generation.</param>
public sealed record CommandActionResult(
    int SchemaVersion,
    CommandActionResultStatus Status,
    CommandActionResultOutcome Outcome,
    string OutcomeEvidenceId,
    long EffectGeneration)
{
    /// <summary>Gets the only supported experimental result schema.</summary>
    public const int CurrentSchemaVersion = 1;
}
