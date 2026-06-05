namespace AgentHome.Core.Workspace;

public sealed class WorkspacePaths
{
    public WorkspacePaths(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        AgentPath = Path.Combine(RootPath, ".agent");
        WorkspacePath = Path.Combine(RootPath, "workspace");
    }

    public string RootPath { get; }
    public string AgentPath { get; }
    public string WorkspacePath { get; }

    public string AgentFile(string relativePath) => Path.Combine(AgentPath, relativePath);

    public string WorkspaceFile(string relativePath) => Path.Combine(WorkspacePath, relativePath);

    public string LogsPath => Path.Combine(AgentPath, "logs");

    public string EventsLogPath => Path.Combine(LogsPath, "events.ndjson");

    public string TasksPath => Path.Combine(AgentPath, "tasks");

    public string ExportsPath => Path.Combine(AgentPath, "exports");

    public bool IsInitialized => Directory.Exists(AgentPath) && File.Exists(AgentFile("permissions.json"));
}
