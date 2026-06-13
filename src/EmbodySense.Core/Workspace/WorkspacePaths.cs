namespace EmbodySense.Core.Workspace
{
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

        public string AuditPath => Path.Combine(AgentPath, "audit");

        public string AuditReadmePath => Path.Combine(AuditPath, "README.md");

        public string EventsLogPath => Path.Combine(AuditPath, "events.ndjson");

        public string TasksPath => Path.Combine(AgentPath, "tasks");

        public string ExportsPath => Path.Combine(AgentPath, "exports");

        // TODO: Implement below instead of just initializing the locations and enforcing protections on them.
        public string SkillsPath => Path.Combine(AgentPath, "skills");

        public string HooksPath => Path.Combine(AgentPath, "hooks");

        public string RecipesPath => Path.Combine(AgentPath, "recipes");

        public string PermissionsPath => AgentFile("permissions.json");

        public string PermissionsReadmePath => AgentFile("PERMISSIONS.md");

        public string WorkspacePrivatePath => Path.Combine(WorkspacePath, "private");

        public string WorkspaceSharedPath => Path.Combine(WorkspacePath, "shared");

        public string WorkspaceGeneratedPath => Path.Combine(WorkspacePath, "generated");

        public string WorkspaceSystemPath => Path.Combine(WorkspacePath, "system");

        public bool IsInitialized => Directory.Exists(AgentPath) && File.Exists(PermissionsPath);
    }
}
