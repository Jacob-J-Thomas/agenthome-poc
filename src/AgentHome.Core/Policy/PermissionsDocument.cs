namespace AgentHome.Core.Policy;

public sealed record PermissionsDocument(
    int Version,
    PermissionDecision DefaultDecision,
    IReadOnlyList<PolicyRule> Rules)
{
    public static PermissionsDocument CreateDefault()
    {
        return new PermissionsDocument(
            Version: 1,
            DefaultDecision: PermissionDecision.Prompt,
            Rules: new List<PolicyRule>
            {
                new("audit.append", ".agent/logs/**", PermissionDecision.Allow, "The runtime may append audit events."),
                new("task.write", ".agent/tasks/**", PermissionDecision.Allow, "The runtime may write task state."),
                new("file.read", ".agent/**", PermissionDecision.Allow, "Agents may read their operating environment."),
                new("file.write", ".agent/logs/**", PermissionDecision.Allow, "Audit log appends are allowed."),
                new("file.write", ".agent/tasks/**", PermissionDecision.Allow, "Task state writes are allowed."),
                new("file.read", "workspace/shared/**", PermissionDecision.Allow, "Shared workspace is readable."),
                new("file.write", "workspace/shared/**", PermissionDecision.Prompt, "Shared workspace writes require approval."),
                new("file.read", "workspace/generated/**", PermissionDecision.Allow, "Generated workspace is readable."),
                new("file.write", "workspace/generated/**", PermissionDecision.Allow, "Generated artifacts are agent-writable."),
                new("file.read", "workspace/private/**", PermissionDecision.Prompt, "Private workspace access requires approval."),
                new("file.write", "workspace/private/**", PermissionDecision.Deny, "Agents cannot write human-private files by default."),
                new("shell.execute", "**", PermissionDecision.Prompt, "Shell execution requires approval."),
                new("network.request", "**", PermissionDecision.Prompt, "Network access requires approval."),
                new("secret.use", "**", PermissionDecision.Prompt, "Secret usage requires scoped approval."),
                new("memory.write", ".agent/MEMORY.md", PermissionDecision.Prompt, "Memory writes require approval.")
            });
    }
}
