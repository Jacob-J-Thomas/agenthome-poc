namespace EmbodySense.Core.Common.Loops.Models;

public sealed record LoopGraphNodeDefinition(
    string Id,
    string DisplayName,
    string Description,
    LoopGraphNodeKind Kind,
    LoopGraphNodeEditMode EditMode,
    string[] CapabilityIds);
