namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Selects how a node's canonical output may be retained or published.
/// </summary>
/// <param name="RetainForLoopReasoning">Whether later nodes and iterations may consume the output.</param>
/// <param name="PublishToInvokingConversation">Whether the verified output is appended to the admitted invoking conversation.</param>
public sealed record LoopContextOutputPolicy(
    bool RetainForLoopReasoning,
    bool PublishToInvokingConversation);
