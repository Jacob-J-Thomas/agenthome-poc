namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunRecoverySnapshot(bool Completed, bool PreserveCurrentConversation);
