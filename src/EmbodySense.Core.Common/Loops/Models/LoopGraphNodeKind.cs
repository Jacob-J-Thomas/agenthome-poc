namespace EmbodySense.Core.Common.Loops.Models;

public enum LoopGraphNodeKind
{
    Unknown = 0,
    Trigger,
    ContextAssembly,
    ModelInference,
    ToolActuation,
    MemoryOperation,
    ReviewGate,
    Subloop,
    FailureHandler,
    TranscriptPersistence,
    RunFinalization
}
