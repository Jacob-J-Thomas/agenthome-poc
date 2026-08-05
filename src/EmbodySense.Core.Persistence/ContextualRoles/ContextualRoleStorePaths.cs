using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.ContextualRoles;

internal sealed class ContextualRoleStorePaths
{
    public ContextualRoleStorePaths(WorkspacePaths workspacePaths)
    {
        ArgumentNullException.ThrowIfNull(workspacePaths);
        WorkspaceRoot = workspacePaths.RootPath;
        Root = Path.Combine(workspacePaths.AgentPath, "contextual-roles");
        Revisions = Path.Combine(Root, "revisions");
        States = Path.Combine(Root, "states");
        Operations = Path.Combine(Root, "operations");
        Proofs = Path.Combine(Root, "proofs");
        Anchor = Path.Combine(Root, "workspace-anchor.json");
        Lock = Path.Combine(Root, ".mutations.lock");
    }

    public string WorkspaceRoot { get; }
    public string Root { get; }
    public string Revisions { get; }
    public string States { get; }
    public string Operations { get; }
    public string Proofs { get; }
    public string Anchor { get; }
    public string Lock { get; }

    public string Revision(string roleId, int revision) => Path.Combine(Revisions, $"{roleId}.{revision}.json");
    public string State(string roleId) => Path.Combine(States, $"{roleId}.json");
    public string Intent(string operationId) => Path.Combine(Operations, $"{operationId}.intent.json");
    public string Result(string operationId) => Path.Combine(Operations, $"{operationId}.result.json");
    public string Proof(string operationId) => Path.Combine(Proofs, $"{operationId}.json");
}
