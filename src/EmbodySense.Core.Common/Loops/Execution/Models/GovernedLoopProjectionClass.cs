namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Classifies a derived projection without treating it as the underlying effect or frontier truth.</summary>
public enum GovernedLoopProjectionClass
{
    /// <summary>No supported projection class was supplied.</summary>
    Unknown = 0,
    /// <summary>A local executor projection that can be regenerated from authoritative evidence.</summary>
    LocalRuntime,
    /// <summary>A durable read model synchronized through idempotency and optimistic preconditions.</summary>
    DurableReadModel,
    /// <summary>A surface-facing projection regenerated through a shared runtime contract.</summary>
    Surface
}
