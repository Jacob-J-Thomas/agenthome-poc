namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Declares one bounded portable value schema used by explicit graph ports.</summary>
/// <param name="Id">The stable value-schema identifier.</param>
/// <param name="Kind">The portable value kind.</param>
/// <param name="Nullable">Whether the value may be null.</param>
/// <param name="Format">An optional stable lowercase format identifier.</param>
/// <param name="ElementSchemaId">The required element schema for arrays, otherwise <see langword="null"/>.</param>
public sealed record GovernedLoopValueSchemaDefinition(string Id, GovernedLoopValueKind Kind, bool Nullable, string? Format = null, string? ElementSchemaId = null);
