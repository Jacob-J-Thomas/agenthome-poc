namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Contains graph display and layout metadata excluded from executable identity.</summary>
/// <param name="DisplayName">The graph display name.</param>
/// <param name="Description">The graph description.</param>
/// <param name="Nodes">The display metadata keyed by node identity.</param>
public sealed record GovernedLoopDisplayMetadata(string DisplayName, string Description, IReadOnlyList<GovernedLoopNodeDisplayMetadata> Nodes);
