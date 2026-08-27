namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Reports one bounded Startup-owned stop request for canonical governed-loop background delivery.</summary>
/// <param name="Status">The closed stop outcome or bounded-drain state.</param>
/// <param name="Readiness">The non-sensitive process readiness after the request.</param>
/// <param name="Ownership">The active-ownership classification without exposing durable owner identities.</param>
/// <param name="Detail">A stable non-sensitive diagnostic for operators and process hosts.</param>
public sealed record AgentRuntimeGovernedLoopBackgroundStopResult(
    AgentRuntimeGovernedLoopBackgroundStopStatus Status,
    AgentRuntimeGovernedLoopBackgroundReadiness Readiness,
    AgentRuntimeGovernedLoopBackgroundOwnership Ownership,
    string Detail);
