namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Identifies the supported loop graph node kind values.
/// </summary>
public enum LoopGraphNodeKind
{
    /// <summary>
    /// Identifies the unknown loop graph node kind.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the trigger loop graph node kind.
    /// </summary>
    Trigger,
    /// <summary>
    /// Identifies the context assembly loop graph node kind.
    /// </summary>
    ContextAssembly,
    /// <summary>
    /// Identifies the model inference loop graph node kind.
    /// </summary>
    ModelInference,
    /// <summary>
    /// Identifies the tool actuation loop graph node kind.
    /// </summary>
    ToolActuation,
    /// <summary>
    /// Identifies the memory operation loop graph node kind.
    /// </summary>
    MemoryOperation,
    /// <summary>
    /// Identifies the review gate loop graph node kind.
    /// </summary>
    ReviewGate,
    /// <summary>
    /// Identifies the subloop loop graph node kind.
    /// </summary>
    Subloop,
    /// <summary>
    /// Identifies the failure handler loop graph node kind.
    /// </summary>
    FailureHandler,
    /// <summary>
    /// Identifies the transcript persistence loop graph node kind.
    /// </summary>
    TranscriptPersistence,
    /// <summary>
    /// Identifies the run finalization loop graph node kind.
    /// </summary>
    RunFinalization
}
