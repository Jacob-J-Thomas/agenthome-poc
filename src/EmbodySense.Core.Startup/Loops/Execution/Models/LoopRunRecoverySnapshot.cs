namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunRecoverySnapshot(bool Completed, bool PreserveCurrentConversation);
