namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Reports one explicit Startup-owned request to start canonical governed-loop background delivery.</summary>
/// <param name="Status">The closed startup outcome.</param>
/// <param name="Readiness">The non-sensitive process readiness after the request.</param>
/// <param name="Ownership">The exact active-ownership classification without exposing durable owner identities.</param>
/// <param name="RetryAllowed">Whether a later process-host retry may safely attempt acquisition again.</param>
/// <param name="Detail">A stable non-sensitive diagnostic for operators and process hosts.</param>
public sealed record AgentRuntimeGovernedLoopBackgroundStartResult(
    AgentRuntimeGovernedLoopBackgroundStartStatus Status,
    AgentRuntimeGovernedLoopBackgroundReadiness Readiness,
    AgentRuntimeGovernedLoopBackgroundOwnership Ownership,
    bool RetryAllowed,
    string Detail);
