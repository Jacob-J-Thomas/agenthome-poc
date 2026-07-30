namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopNodeContextPolicy(
    LoopContextPolicyMode Mode,
    LoopContextPolicy? CustomPolicy);
