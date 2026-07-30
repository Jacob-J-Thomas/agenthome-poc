namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects one role/content message from captured or composed run context.
/// </summary>
/// <param name="Role">The role.</param>
/// <param name="Content">The content.</param>
public sealed record LoopRunMessageSnapshot(string Role, string Content);
