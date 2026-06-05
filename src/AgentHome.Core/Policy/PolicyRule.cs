namespace AgentHome.Core.Policy;

public sealed record PolicyRule(
    string Action,
    string Target,
    PermissionDecision Decision,
    string? Reason = null);
