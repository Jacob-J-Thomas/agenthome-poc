namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Classifies the executor-neutral origin of an externally meaningful effect attempt.</summary>
public enum GovernedLoopEffectOrigin
{
    /// <summary>No supported effect origin was supplied.</summary>
    Unknown = 0,
    /// <summary>A model-provider dispatch.</summary>
    Provider,
    /// <summary>A governed actuator invocation.</summary>
    Actuator,
    /// <summary>A conversation or other externally meaningful publication.</summary>
    Publication,
    /// <summary>A governed durable-memory mutation.</summary>
    MemoryMutation,
    /// <summary>A notification delivery.</summary>
    Notification,
    /// <summary>A harness-owned system job.</summary>
    SystemJob
}
