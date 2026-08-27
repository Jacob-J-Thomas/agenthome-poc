namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Reports a non-sensitive current status of canonical governed-loop background delivery.</summary>
/// <param name="Readiness">The process readiness derived from current durable coordinator evidence.</param>
/// <param name="Ownership">The active-ownership classification without exposing durable owner identities.</param>
/// <param name="Detail">A stable non-sensitive diagnostic for operators and process hosts.</param>
public sealed record AgentRuntimeGovernedLoopBackgroundStatus(
    AgentRuntimeGovernedLoopBackgroundReadiness Readiness,
    AgentRuntimeGovernedLoopBackgroundOwnership Ownership,
    string Detail);
