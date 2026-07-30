namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Represents a loop graph node definition.
/// </summary>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">The human-readable description.</param>
/// <param name="Kind">The kind.</param>
/// <param name="EditMode">The edit mode.</param>
/// <param name="CapabilityIds">The capability IDs.</param>
public sealed record LoopGraphNodeDefinition(
    string Id,
    string DisplayName,
    string Description,
    LoopGraphNodeKind Kind,
    LoopGraphNodeEditMode EditMode,
    string[] CapabilityIds);
