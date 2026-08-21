namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Returns one bounded value-free graph Action result.</summary>
/// <param name="SchemaVersion">The result schema, which must be 1.</param>
/// <param name="Status">Whether the exact outcome was committed or replayed.</param>
/// <param name="AfterEvidenceId">The exact retained after-state evidence reference.</param>
/// <param name="EffectGeneration">The exact positive effect generation.</param>
public sealed record WorkspaceActionResult(
    int SchemaVersion,
    WorkspaceActionResultStatus Status,
    string AfterEvidenceId,
    long EffectGeneration)
{
    /// <summary>Gets the only supported experimental result schema.</summary>
    public const int CurrentSchemaVersion = 1;
}
