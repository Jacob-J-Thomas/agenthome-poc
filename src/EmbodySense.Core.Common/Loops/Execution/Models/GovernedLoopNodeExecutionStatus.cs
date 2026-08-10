namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Identifies the committed posture of one exact graph node execution.</summary>
public enum GovernedLoopNodeExecutionStatus
{
    /// <summary>No supported node posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The node is ready to be selected.</summary>
    Ready,
    /// <summary>The node is executing.</summary>
    Running,
    /// <summary>The node committed a successful outcome.</summary>
    Completed,
    /// <summary>The node was deterministically skipped.</summary>
    Skipped,
    /// <summary>The node is waiting for a durable wake condition.</summary>
    Waiting,
    /// <summary>The node committed a failed outcome.</summary>
    Failed,
    /// <summary>The node is blocked on explicit human review or reconciliation.</summary>
    ReviewBlocked
}
