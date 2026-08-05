using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Common.Workspace;

/// <summary>
/// Derives the canonical workspace and <c>.agent</c> paths used by runtime, persistence, and governance components.
/// </summary>
/// <remarks>Construction normalizes the workspace root to an absolute path. Derived properties do not create or verify files and directories.</remarks>
public sealed class WorkspacePaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspacePaths"/> type.
    /// </summary>
    /// <param name="rootPath">The workspace root to normalize to an absolute path.</param>
    public WorkspacePaths(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        AgentPath = Path.Combine(RootPath, ".agent");
        WorkspacePath = RootPath;
    }

    /// <summary>
    /// Gets the root path.
    /// </summary>
    /// <value>The root path.</value>
    public string RootPath { get; }
    /// <summary>
    /// Gets the agent path.
    /// </summary>
    /// <value>The agent path.</value>
    public string AgentPath { get; }
    /// <summary>
    /// Gets the workspace path.
    /// </summary>
    /// <value>The workspace path.</value>
    public string WorkspacePath { get; }

    /// <summary>
    /// Resolves a file path contained by the <c>.agent</c> directory.
    /// </summary>
    /// <param name="relativePath">A nonempty, non-rooted path whose canonical target must remain within <see cref="AgentPath"/>.</param>
    /// <returns>The canonical absolute path beneath <see cref="AgentPath"/>.</returns>
    /// <exception cref="ArgumentNullException">The path is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path is empty, rooted, or does not resolve to a descendant of <see cref="AgentPath"/>.</exception>
    public string AgentFile(string relativePath) => ContainedFile(AgentPath, relativePath);

    /// <summary>
    /// Resolves a file path contained by the workspace root.
    /// </summary>
    /// <param name="relativePath">A nonempty, non-rooted path whose canonical target must remain within <see cref="WorkspacePath"/>.</param>
    /// <returns>The canonical absolute path beneath <see cref="WorkspacePath"/>.</returns>
    /// <exception cref="ArgumentNullException">The path is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path is empty, rooted, or does not resolve to a descendant of <see cref="WorkspacePath"/>.</exception>
    public string WorkspaceFile(string relativePath) => ContainedFile(WorkspacePath, relativePath);

    /// <summary>
    /// Gets the logs path.
    /// </summary>
    /// <value>The logs path.</value>
    public string LogsPath => Path.Combine(AgentPath, "logs");

    /// <summary>
    /// Gets the tool responses path.
    /// </summary>
    /// <value>The tool responses path.</value>
    public string ToolResponsesPath => Path.Combine(LogsPath, "tool-responses");

    /// <summary>
    /// Gets the tool response retention lock path.
    /// </summary>
    /// <value>The tool response retention lock path.</value>
    public string ToolResponseRetentionLockPath => Path.Combine(ToolResponsesPath, ".retention.lock");

    /// <summary>
    /// Gets the audit path.
    /// </summary>
    /// <value>The audit path.</value>
    public string AuditPath => Path.Combine(AgentPath, "audit");

    /// <summary>
    /// Gets the audit README path.
    /// </summary>
    /// <value>The audit README path.</value>
    public string AuditReadmePath => Path.Combine(AuditPath, "README.md");

    /// <summary>
    /// Gets the events log path.
    /// </summary>
    /// <value>The events log path.</value>
    public string EventsLogPath => Path.Combine(AuditPath, "events.ndjson");

    /// <summary>
    /// Gets the memory path.
    /// </summary>
    /// <value>The memory path.</value>
    public string MemoryPath => Path.Combine(AgentPath, "memory");

    /// <summary>
    /// Gets the memory README path.
    /// </summary>
    /// <value>The memory README path.</value>
    public string MemoryReadmePath => Path.Combine(MemoryPath, "README.md");

    /// <summary>
    /// Gets the conversation memory path.
    /// </summary>
    /// <value>The conversation memory path.</value>
    public string ConversationMemoryPath => Path.Combine(MemoryPath, "conversations");

    /// <summary>
    /// Gets the archived conversation memory path.
    /// </summary>
    /// <value>The archived conversation memory path.</value>
    public string ArchivedConversationMemoryPath => Path.Combine(ConversationMemoryPath, "archive");

    /// <summary>
    /// Gets the current conversation path.
    /// </summary>
    /// <value>The current conversation path.</value>
    public string CurrentConversationPath => Path.Combine(ConversationMemoryPath, "current.ndjson");

    /// <summary>
    /// Gets the conversation turn lock path.
    /// </summary>
    /// <value>The conversation turn lock path.</value>
    public string ConversationTurnLockPath => Path.Combine(ConversationMemoryPath, ".workspace-turn.lock");

    /// <summary>
    /// Gets the loops path.
    /// </summary>
    /// <value>The loops path.</value>
    public string LoopsPath => Path.Combine(AgentPath, "loops");

    /// <summary>
    /// Gets the loop definitions path.
    /// </summary>
    /// <value>The loop definitions path.</value>
    public string LoopDefinitionsPath => Path.Combine(LoopsPath, "definitions");

    /// <summary>
    /// Gets the custom loop definitions path.
    /// </summary>
    /// <value>The custom loop definitions path.</value>
    public string CustomLoopDefinitionsPath => Path.Combine(LoopDefinitionsPath, "custom");

    /// <summary>
    /// Gets the custom loop definition tombstones path.
    /// </summary>
    /// <value>The custom loop definition tombstones path.</value>
    public string CustomLoopDefinitionTombstonesPath => Path.Combine(LoopDefinitionsPath, "custom-tombstones");

    /// <summary>
    /// Gets the custom loop definition operations path.
    /// </summary>
    /// <value>The custom loop definition operations path.</value>
    public string CustomLoopDefinitionOperationsPath => Path.Combine(LoopDefinitionsPath, "custom-create-operations");

    /// <summary>
    /// Gets the shared custom-loop receipt-retention state path.
    /// </summary>
    /// <value>The receipt-retention state path.</value>
    public string CustomLoopReceiptRetentionPath => Path.Combine(LoopsPath, "receipt-retention");

    /// <summary>
    /// Gets the canonical compact custom-loop receipt proof ledger path.
    /// </summary>
    /// <value>The proof-ledger path.</value>
    public string CustomLoopReceiptProofLedgerPath => Path.Combine(CustomLoopReceiptRetentionPath, "proof-ledger.json");

    /// <summary>
    /// Gets the definition-mutation receipt cleanup journal path.
    /// </summary>
    /// <value>The definition-mutation cleanup journal path.</value>
    public string CustomLoopDefinitionMutationReceiptCleanupJournalPath => Path.Combine(CustomLoopReceiptRetentionPath, "definition-mutation-receipt-cleanup.json");

    /// <summary>
    /// Gets the definition-tombstone cleanup journal path.
    /// </summary>
    /// <value>The definition-tombstone cleanup journal path.</value>
    public string CustomLoopDefinitionTombstoneCleanupJournalPath => Path.Combine(CustomLoopReceiptRetentionPath, "definition-tombstone-cleanup.json");

    /// <summary>
    /// Gets the bounded completed cleanup-operation history root.
    /// </summary>
    /// <value>The receipt cleanup history root.</value>
    public string CustomLoopReceiptCleanupHistoryPath => Path.Combine(LoopsPath, "receipt-cleanup-history");

    /// <summary>
    /// Gets the completed definition-mutation receipt cleanup history path.
    /// </summary>
    /// <value>The definition-mutation cleanup history path.</value>
    public string CustomLoopDefinitionMutationReceiptCleanupHistoryPath => Path.Combine(CustomLoopReceiptCleanupHistoryPath, "definition-mutation-receipt");

    /// <summary>
    /// Gets the completed definition-tombstone cleanup history path.
    /// </summary>
    /// <value>The definition-tombstone cleanup history path.</value>
    public string CustomLoopDefinitionTombstoneCleanupHistoryPath => Path.Combine(CustomLoopReceiptCleanupHistoryPath, "definition-tombstone");

    /// <summary>
    /// Gets the completed lifecycle-control receipt cleanup history path.
    /// </summary>
    /// <value>The lifecycle-control cleanup history path.</value>
    public string CustomLoopLifecycleControlReceiptCleanupHistoryPath => Path.Combine(CustomLoopReceiptCleanupHistoryPath, "lifecycle-control-receipt");

    /// <summary>
    /// Gets the loop runs path.
    /// </summary>
    /// <value>The loop runs path.</value>
    public string LoopRunsPath => Path.Combine(LoopsPath, "runs");

    /// <summary>
    /// Gets the durable default-conversation turn protocol path.
    /// </summary>
    /// <value>The default-conversation turn protocol path.</value>
    public string DefaultConversationTurnsPath => Path.Combine(LoopRunsPath, "default-conversation-turns");

    /// <summary>
    /// Gets the custom loop runs path.
    /// </summary>
    /// <value>The custom loop runs path.</value>
    public string CustomLoopRunsPath => Path.Combine(LoopRunsPath, "custom");

    /// <summary>
    /// Gets the custom loop control operations path.
    /// </summary>
    /// <value>The custom loop control operations path.</value>
    public string CustomLoopControlOperationsPath => Path.Combine(LoopRunsPath, "custom-control-operations");

    /// <summary>
    /// Gets the lifecycle-control receipt cleanup journal path.
    /// </summary>
    /// <value>The path that owns the one active lifecycle-control cleanup journal.</value>
    public string CustomLoopControlReceiptCleanupPath => Path.Combine(CustomLoopReceiptRetentionPath, "lifecycle-control");

    /// <summary>
    /// Gets the custom loop invocation operations path.
    /// </summary>
    /// <value>The custom loop invocation operations path.</value>
    public string CustomLoopInvocationOperationsPath => Path.Combine(LoopRunsPath, "custom-invocation-operations");

    /// <summary>
    /// Gets the custom loop invocation receipt retention path.
    /// </summary>
    /// <value>The custom loop invocation receipt retention path.</value>
    public string CustomLoopInvocationReceiptRetentionPath => Path.Combine(LoopRunsPath, "custom-invocation-receipt-retention");

    /// <summary>
    /// Gets the custom loop trace deletion operations path.
    /// </summary>
    /// <value>The custom loop trace deletion operations path.</value>
    public string CustomLoopTraceDeletionOperationsPath => Path.Combine(LoopRunsPath, "custom-trace-deletion-operations");

    /// <summary>
    /// Gets the custom loop host lock path.
    /// </summary>
    /// <value>The custom loop host lock path.</value>
    public string CustomLoopHostLockPath => Path.Combine(LoopRunsPath, ".custom-workspace-host.lock");

    /// <summary>
    /// Gets the custom loop cancellation owner path.
    /// </summary>
    /// <value>The custom loop cancellation owner path.</value>
    public string CustomLoopCancellationOwnerPath => Path.Combine(LoopRunsPath, ".custom-workspace-cancellation-owner.json");

    /// <summary>
    /// Gets the default conversation loop definition path.
    /// </summary>
    /// <value>The default conversation loop definition path.</value>
    public string DefaultConversationLoopDefinitionPath => Path.Combine(LoopDefinitionsPath, BuiltInLoopIds.DefaultConversation + ".json");

    /// <summary>
    /// Gets the tasks path.
    /// </summary>
    /// <value>The tasks path.</value>
    public string TasksPath => Path.Combine(AgentPath, "tasks");

    /// <summary>
    /// Gets the exports path.
    /// </summary>
    /// <value>The exports path.</value>
    public string ExportsPath => Path.Combine(AgentPath, "exports");

    /// <summary>
    /// Gets the skills path.
    /// </summary>
    /// <value>The skills path.</value>
    public string SkillsPath => Path.Combine(AgentPath, "skills");

    /// <summary>
    /// Gets the hooks path.
    /// </summary>
    /// <value>The hooks path.</value>
    public string HooksPath => Path.Combine(AgentPath, "hooks");

    /// <summary>
    /// Gets the recipes path.
    /// </summary>
    /// <value>The recipes path.</value>
    public string RecipesPath => Path.Combine(AgentPath, "recipes");

    /// <summary>
    /// Gets the permissions path.
    /// </summary>
    /// <value>The permissions path.</value>
    public string PermissionsPath => AgentFile("permissions.json");

    /// <summary>
    /// Gets the permissions README path.
    /// </summary>
    /// <value>The permissions README path.</value>
    public string PermissionsReadmePath => AgentFile("PERMISSIONS.md");

    /// <summary>
    /// Gets the role path.
    /// </summary>
    /// <value>The role path.</value>
    public string RolePath => AgentFile("ROLE.md");

    /// <summary>
    /// Gets the workspace private path.
    /// </summary>
    /// <value>The workspace private path.</value>
    public string WorkspacePrivatePath => Path.Combine(WorkspacePath, "private");

    /// <summary>
    /// Gets the workspace shared path.
    /// </summary>
    /// <value>The workspace shared path.</value>
    public string WorkspaceSharedPath => Path.Combine(WorkspacePath, "shared");

    /// <summary>
    /// Gets the workspace generated path.
    /// </summary>
    /// <value>The workspace generated path.</value>
    public string WorkspaceGeneratedPath => Path.Combine(WorkspacePath, "generated");

    /// <summary>
    /// Gets the workspace system path.
    /// </summary>
    /// <value>The workspace system path.</value>
    public string WorkspaceSystemPath => Path.Combine(WorkspacePath, "system");

    /// <summary>
    /// Gets a value indicating whether the minimum workspace scaffold exists.
    /// </summary>
    /// <value><see langword="true"/> when the <c>.agent</c> directory, permissions document, and role document exist; otherwise, <see langword="false"/>.</value>
    public bool IsInitialized => Directory.Exists(AgentPath) && File.Exists(PermissionsPath) && File.Exists(RolePath);

    private static string ContainedFile(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Path must be relative to its declared workspace boundary.", nameof(relativePath));
        }

        var candidate = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var candidateWithoutTrailingSeparator = Path.TrimEndingDirectorySeparator(candidate);
        var rootWithoutTrailingSeparator = Path.TrimEndingDirectorySeparator(rootPath);
        var rootWithSeparator = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        if (string.Equals(candidateWithoutTrailingSeparator, rootWithoutTrailingSeparator, StringComparison.Ordinal) || !candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path must resolve to a descendant of its declared workspace boundary.", nameof(relativePath));
        }

        return candidate;
    }
}
