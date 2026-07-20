namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopNodeContextPolicy(
    LoopContextPolicyMode Mode,
    LoopContextPolicy? CustomPolicy);
