namespace EmbodySense.Core.Common.Loops.Failures.Models;

/// <summary>Identifies the server-owned subsystem that produced a bounded failure observation.</summary>
public enum GovernedLoopFailureSource
{
    /// <summary>An undefined source.</summary>
    Unknown = 0,
    /// <summary>Graph or immutable configuration validation.</summary>
    Validation,
    /// <summary>Current authority or permission evaluation.</summary>
    Authority,
    /// <summary>Authenticated human review.</summary>
    HumanReview,
    /// <summary>A required dependency or adapter.</summary>
    Dependency,
    /// <summary>A model provider boundary.</summary>
    Provider,
    /// <summary>A governed actuator boundary.</summary>
    Actuator,
    /// <summary>A governed workspace boundary.</summary>
    Workspace,
    /// <summary>A durable wait boundary.</summary>
    Wait,
    /// <summary>A deterministic pure-node evaluator.</summary>
    PureNode,
    /// <summary>An enclosing runtime bound.</summary>
    Runtime,
    /// <summary>Durable persistence.</summary>
    Persistence,
    /// <summary>Append-only audit.</summary>
    Audit,
    /// <summary>Evidence authentication or consistency.</summary>
    Evidence,
    /// <summary>An authenticated user lifecycle action.</summary>
    User,
    /// <summary>An explicit admitted Fail terminal.</summary>
    Agent,
}
