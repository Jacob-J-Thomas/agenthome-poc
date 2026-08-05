namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Captures one immutable-in-use view of currently advertised node descriptor semantics.</summary>
/// <param name="IsAvailable">Whether the catalog could be resolved authoritatively.</param>
/// <param name="SourceEvidenceId">The stable source evidence identity included in deterministic validation evidence.</param>
/// <param name="Descriptors">The exact descriptor entries.</param>
public sealed record GovernedLoopNodeCatalogSnapshot(bool IsAvailable, string SourceEvidenceId, IReadOnlyList<GovernedLoopNodeCatalogDescriptor> Descriptors);
