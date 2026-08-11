using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring;

internal sealed class GovernedLoopGraphRevisionStorePaths
{
    public GovernedLoopGraphRevisionStorePaths(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RootPath = Path.Combine(paths.AgentPath, "loops", "revisions", "graph-authoring");
        ArtifactsPath = Path.Combine(RootPath, "artifacts");
        OperationsPath = Path.Combine(RootPath, "operations");
        LockPath = Path.Combine(RootPath, ".mutations.lock");
    }

    public string RootPath { get; }

    public string ArtifactsPath { get; }

    public string OperationsPath { get; }

    public string LockPath { get; }

    public string ArtifactPath(string graphId, string revisionId)
        => Path.Combine(ArtifactsPath, graphId, revisionId + ".json");

    public string OperationPath(string operationId)
        => Path.Combine(OperationsPath, operationId + ".json");
}
