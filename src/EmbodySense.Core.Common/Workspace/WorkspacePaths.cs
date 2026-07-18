namespace EmbodySense.Core.Common.Workspace;

public sealed class WorkspacePaths
{
    public WorkspacePaths(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        AgentPath = Path.Combine(RootPath, ".agent");
        WorkspacePath = RootPath;
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

    public string MemoryPath => Path.Combine(AgentPath, "memory");

    public string MemoryReadmePath => Path.Combine(MemoryPath, "README.md");

    public string ConversationMemoryPath => Path.Combine(MemoryPath, "conversations");

    public string ArchivedConversationMemoryPath => Path.Combine(ConversationMemoryPath, "archive");

    public string CurrentConversationPath => Path.Combine(ConversationMemoryPath, "current.ndjson");

    public string ConversationTurnLockPath => Path.Combine(ConversationMemoryPath, ".workspace-turn.lock");

    public string LoopsPath => Path.Combine(AgentPath, "loops");

    public string LoopDefinitionsPath => Path.Combine(LoopsPath, "definitions");

    public string CustomLoopDefinitionsPath => Path.Combine(LoopDefinitionsPath, "custom");

    public string CustomLoopDefinitionTombstonesPath => Path.Combine(LoopDefinitionsPath, "custom-tombstones");

    public string CustomLoopDefinitionOperationsPath => Path.Combine(LoopDefinitionsPath, "custom-create-operations");

    public string LoopRunsPath => Path.Combine(LoopsPath, "runs");

    public string CustomLoopRunsPath => Path.Combine(LoopRunsPath, "custom");

    public string CustomLoopControlOperationsPath => Path.Combine(LoopRunsPath, "custom-control-operations");

    public string CustomLoopInvocationOperationsPath => Path.Combine(LoopRunsPath, "custom-invocation-operations");

    public string CustomLoopTraceDeletionOperationsPath => Path.Combine(LoopRunsPath, "custom-trace-deletion-operations");

    public string CustomLoopHostLockPath => Path.Combine(LoopRunsPath, ".custom-workspace-host.lock");

    public string DefaultConversationLoopDefinitionPath => Path.Combine(LoopDefinitionsPath, "default-conversation.json");

    public string TasksPath => Path.Combine(AgentPath, "tasks");

    public string ExportsPath => Path.Combine(AgentPath, "exports");

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
