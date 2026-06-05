namespace AgentHome.Core.Policy;

public sealed record PolicyEvaluation(
    string Action,
    string Target,
    PermissionDecision Decision,
    string Source,
    string? Reason);
