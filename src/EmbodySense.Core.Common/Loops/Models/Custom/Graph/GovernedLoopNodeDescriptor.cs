namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Describes an extensible governed node contract without claiming runtime support.</summary>
/// <param name="Kind">The stable schema-1 kind classification.</param>
/// <param name="TypeId">The stable lowercase descriptor identifier.</param>
/// <param name="Version">The positive descriptor contract version.</param>
public sealed record GovernedLoopNodeDescriptor(GovernedLoopNodeKind Kind, string TypeId, int Version);
