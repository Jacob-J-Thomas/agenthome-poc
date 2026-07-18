namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopContextPolicy(
    CustomLoopContextInputPolicy ContextIn,
    CustomLoopContextOutputPolicy ContextOut);
