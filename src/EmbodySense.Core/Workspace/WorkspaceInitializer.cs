using EmbodySense.Core.Audit;
using EmbodySense.Core.Permissions;

namespace EmbodySense.Core.Workspace
{
    public sealed class WorkspaceInitializer
    {
        public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            var paths = new WorkspacePaths(rootPath);

            Directory.CreateDirectory(paths.RootPath);
            Directory.CreateDirectory(paths.AgentPath);
            Directory.CreateDirectory(paths.TasksPath);
            Directory.CreateDirectory(paths.LogsPath);
            Directory.CreateDirectory(paths.AuditPath);
            Directory.CreateDirectory(paths.ExportsPath);
            Directory.CreateDirectory(paths.SkillsPath);
            Directory.CreateDirectory(paths.HooksPath);
            Directory.CreateDirectory(paths.RecipesPath);

            Directory.CreateDirectory(paths.WorkspacePath);
            Directory.CreateDirectory(paths.WorkspacePrivatePath);
            Directory.CreateDirectory(paths.WorkspaceSharedPath);
            Directory.CreateDirectory(paths.WorkspaceGeneratedPath);
            Directory.CreateDirectory(paths.WorkspaceSystemPath);

            await WriteIfMissingAsync(paths.AgentFile("AGENT.md"), DefaultAgentMd(), cancellationToken);
            await WriteIfMissingAsync(paths.AgentFile("CONTEXT.md"), DefaultContextMd(), cancellationToken);
            await WriteIfMissingAsync(paths.AgentFile("MEMORY.md"), DefaultMemoryMd(), cancellationToken);
            await WriteIfMissingAsync(paths.AgentFile("models.json"), DefaultModelsJson(), cancellationToken);
            await WriteRequiredAsync(paths.AuditReadmePath, DefaultAuditReadme(), cancellationToken);
            await WriteRequiredAsync(paths.PermissionsReadmePath, DefaultPermissionsReadme(), cancellationToken);
            //await WriteIfMissingAsync(paths.AgentFile("tools.json"), DefaultToolsJson(), cancellationToken);

            var permissions = PermissionsDocument.CreateDefault(paths);
            await WriteIfMissingAsync(paths.PermissionsPath, permissions.ToJson() + Environment.NewLine, cancellationToken);

            if (!File.Exists(paths.EventsLogPath))
            {
                await File.WriteAllTextAsync(paths.EventsLogPath, string.Empty, cancellationToken);
            }

            var audit = new AuditLog(paths);
            await audit.AppendAsync(AuditEvent.Create(
                actor: "embodysense.cli",
                action: "workspace.init",
                target: paths.RootPath,
                outcome: "succeeded",
                detail: "Initialized or refreshed EmbodySense workspace scaffolding.",
                metadata: new Dictionary<string, object?>
                {
                    ["agent_path"] = paths.AgentPath,
                    ["audit_path"] = paths.AuditPath,
                    ["permissions_path"] = paths.PermissionsPath,
                    ["workspace_path"] = paths.WorkspacePath
                }), cancellationToken);
        }

        private static async Task WriteIfMissingAsync(string path, string content, CancellationToken cancellationToken)
        {
            if (File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        private static async Task WriteRequiredAsync(string path, string content, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        private static string DefaultAgentMd() => """
            # Agent operating guide

            This workspace is managed by EmbodySense Agent Harness.

            The agent should treat `.agent/` as the durable environment and `workspace/` as the working area.

            Core principles:

            - Missing policy or missing directory permission means request human approval before proceeding.
            - Explicit denied directory permissions mean do not repeatedly request the same inappropriate access.
            - Write durable task state before substantial work.
            - Append meaningful actions to the audit log.
            - Do not treat chat history as the system of record.
            - Do not request or expose raw secrets.

            """;

        private static string DefaultContextMd() => """
            # Workspace context

            Describe the project, repository, human preferences, constraints, and important operating context here.

            This file is intended to be coauthored by humans and agents.

            """;

        private static string DefaultMemoryMd() => """
            # Memory

            Durable memory belongs here or in structured files under `.agent/memory/` once that directory is added.

            Do not store secrets here.

            """;

        // TODO: Add additional agentic files in here for SOUL, USER, PROJECT, PERMISSIONS, ROLE, etc.

        private static string DefaultPermissionsReadme() => """
            # File-System Permissions

            This file explains `.agent/permissions.json`.

            The active policy is directory-level only and applies to agent-mediated file-system access in this workspace. It does not grant blanket process access to the host machine.

            Default behavior:

            - Missing or unsupported permissions require human approval before proceeding.
            - Unmatched directories require human approval before proceeding.
            - Explicit denied entries stop the agent from repeatedly requesting inappropriate access.
            - Directory entries include subdirectories by system design.
            - More specific directory entries override broader directory entries.

            Configuration shape:

            - `approved`: directories the agent may use. Approved entries can set `requiresApproval` to require human approval before use.
            - `denied`: directories the agent should not request again unless the human changes the policy.
            - `operations`: `list`, `read`, `create`, `append`, `modify`, and `delete`.

            Default approved directories include `workspace/shared`, `workspace/generated`, `workspace/system`, `.agent/tasks`, `.agent/exports`, `.agent/skills`, and `.agent/recipes`. Mutable skill and recipe operations require approval by default.

            Default denied directories include `workspace/private`, `.agent/audit`, `.agent/logs`, and `.agent/hooks`. Use `embodysense audit` to inspect audit events instead of reading raw audit files.

            This README is generated from hard-coded EmbodySense CLI text every time `init` runs so the permission explanation stays consistent across workspaces.

            """;

        private static string DefaultAuditReadme() => """
            # Audit Registry

            This folder is the EmbodySense audit registry for this initialized workspace.

            `events.ndjson` is an append-only JSON-lines event stream. Each line records one high-level harness action with timestamp, actor, action, target, outcome, detail, and structured metadata.

            The audit registry is intended to explain what the harness did without becoming a raw transcript, secret store, or prompt dump. LLM inference events should record provider, model, message counts, character counts, duration, and outcome, but should not store raw prompts or model responses by default.

            The harness may write this folder. Humans and trusted harness commands may inspect it. Agent context should not include raw audit events unless a human or policy explicitly requests that context.

            This README is generated from hard-coded EmbodySense CLI text every time `init` runs so the audit policy explanation stays consistent across workspaces.

            """;

        // TODO: Reevaluate below structure (the role should only be concerned with model choice, not hosting service)... we should concern ourselves about support for various models later
        private static string DefaultModelsJson() => """
        {
          "version": 1,
          "roles": {
            "planner": {
              "provider": "openai",
              "model": "configured-externally",
              "notes": "Consumer/developer direct provider placeholder."
            },
            "enterprise_planner": {
              "provider": "azure-or-aws",
              "model": "configured-externally",
              "notes": "Enterprise provider placeholder."
            },
            "local_fast": {
              "provider": "local",
              "model": "configured-externally",
              "notes": "Local inference placeholder."
            }
          }
        }
        """;
    }

}
