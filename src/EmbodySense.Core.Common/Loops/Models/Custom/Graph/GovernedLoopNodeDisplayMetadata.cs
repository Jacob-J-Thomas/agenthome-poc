namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Contains display-only node metadata that never contributes to executable hashing.</summary>
/// <param name="NodeId">The node identifier.</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">The human-readable description.</param>
/// <param name="CanvasX">The optional canvas X coordinate.</param>
/// <param name="CanvasY">The optional canvas Y coordinate.</param>
public sealed record GovernedLoopNodeDisplayMetadata(string NodeId, string DisplayName, string Description, int? CanvasX = null, int? CanvasY = null);
