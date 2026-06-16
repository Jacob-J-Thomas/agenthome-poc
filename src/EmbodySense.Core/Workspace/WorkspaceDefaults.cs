using EmbodySense.Core.Permissions.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Workspace;

internal static class WorkspaceDefaults
{
    public static IReadOnlyList<string> GetDirectories(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return
        [
            paths.RootPath,
            paths.AgentPath,
            paths.TasksPath,
            paths.LogsPath,
            paths.AuditPath,
            paths.ExportsPath,
            paths.SkillsPath,
            paths.HooksPath,
            paths.RecipesPath,
            paths.WorkspacePath,
            paths.WorkspacePrivatePath,
            paths.WorkspaceSharedPath,
            paths.WorkspaceGeneratedPath,
            paths.WorkspaceSystemPath
        ];
    }

    public static IReadOnlyList<WorkspaceSeedFile> GetSeedFiles(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var permissions = PermissionsDocument.CreateDefault(paths);

        return
        [
            new WorkspaceSeedFile(paths.AgentFile("AGENT.md"), DefaultAgentMd(), Overwrite: false),
            new WorkspaceSeedFile(paths.AgentFile("CONTEXT.md"), DefaultContextMd(), Overwrite: false),
            new WorkspaceSeedFile(paths.AgentFile("MEMORY.md"), DefaultMemoryMd(), Overwrite: false),
            new WorkspaceSeedFile(paths.AgentFile("models.json"), DefaultModelsJson(), Overwrite: false),
            new WorkspaceSeedFile(paths.AuditReadmePath, DefaultAuditReadme(), Overwrite: true),
            new WorkspaceSeedFile(paths.PermissionsReadmePath, DefaultPermissionsReadme(), Overwrite: true),
            new WorkspaceSeedFile(paths.PermissionsPath, permissions.ToJson() + Environment.NewLine, Overwrite: false),
            new WorkspaceSeedFile(paths.EventsLogPath, string.Empty, Overwrite: false)
        ];
    }

    private static string DefaultAgentMd() => """
            # Agent operating guide

            This workspace is managed by EmbodySense Agent Harness.

            The agent should treat `.agent/` as the durable environment and `workspace/` as the working area.

            Core principles:

            - Missing policy or missing directory permission means request human approval before proceeding.
            - Explicit denied directory permissions mean do not repeatedly request the same inappropriate access.
            - Governed workspace commands are requested through the `embodysense.command` dynamic tool and executed only through permission, approval, and audit checks.
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

            The audit registry is intended to explain what the harness did without becoming a raw transcript, secret store, or prompt dump. LLM inference and app-server request handling events should record provider, model, message counts, character counts, request method, duration, and outcome where applicable, but should not store raw prompts or model responses by default.

            The harness may write this folder. Humans and trusted harness commands may inspect it. Agent context should not include raw audit events unless a human or policy explicitly requests that context.

            This README is generated from hard-coded EmbodySense CLI text every time `init` runs so the audit policy explanation stays consistent across workspaces.

            """;

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
