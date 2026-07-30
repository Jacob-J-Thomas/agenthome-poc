namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Reports whether interrupted-run recovery completed and whether conversation hydration is required.
/// </summary>
/// <param name="Completed">The completed.</param>
/// <param name="PreserveCurrentConversation">The preserve current conversation.</param>
public sealed record LoopRunRecoverySnapshot(bool Completed, bool PreserveCurrentConversation);
