using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Cli.Audit;

namespace EmbodySense.Cli.Workspace
{
    public sealed class WorkspaceInitializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public async Task InitializeAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            var paths = new WorkspacePaths(rootPath);

            Directory.CreateDirectory(paths.RootPath);
            Directory.CreateDirectory(paths.AgentPath);
            Directory.CreateDirectory(paths.TasksPath);
            Directory.CreateDirectory(paths.LogsPath);
            Directory.CreateDirectory(paths.AuditPath);
            Directory.CreateDirectory(paths.ExportsPath);
            Directory.CreateDirectory(Path.Combine(paths.AgentPath, "skills"));
            Directory.CreateDirectory(Path.Combine(paths.AgentPath, "hooks"));
            Directory.CreateDirectory(Path.Combine(paths.AgentPath, "recipes"));

            Directory.CreateDirectory(paths.WorkspacePath);
            Directory.CreateDirectory(Path.Combine(paths.WorkspacePath, "private"));
            Directory.CreateDirectory(Path.Combine(paths.WorkspacePath, "shared"));
            Directory.CreateDirectory(Path.Combine(paths.WorkspacePath, "generated"));
            Directory.CreateDirectory(Path.Combine(paths.WorkspacePath, "system"));

            await WriteIfMissingAsync(paths.AgentFile("AGENT.md"), DefaultAgentMd(), cancellationToken);
            await WriteIfMissingAsync(paths.AgentFile("CONTEXT.md"), DefaultContextMd(), cancellationToken);
            await WriteIfMissingAsync(paths.AgentFile("MEMORY.md"), DefaultMemoryMd(), cancellationToken);
            await WriteIfMissingAsync(paths.AgentFile("models.json"), DefaultModelsJson(), cancellationToken);
            await WriteRequiredAsync(paths.AuditReadmePath, DefaultAuditReadme(), cancellationToken);
            //await WriteIfMissingAsync(paths.AgentFile("tools.json"), DefaultToolsJson(), cancellationToken);

            //var permissions = PermissionsDocument.CreateDefault();
            //var permissionsJson = JsonSerializer.Serialize(permissions, JsonOptions);
            //await WriteIfMissingAsync(paths.AgentFile("permissions.json"), permissionsJson + Environment.NewLine, cancellationToken);

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

            - Missing policy means ask for human approval.
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

        private static string DefaultAuditReadme() => """
            # Audit Registry

            This folder is the EmbodySense audit registry for this initialized workspace.

            `events.ndjson` is an append-only JSON-lines event stream. Each line records one high-level harness action with timestamp, actor, action, target, outcome, detail, and structured metadata.

            The audit registry is intended to explain what the harness did without becoming a raw transcript, secret store, or prompt dump. LLM inference events should record provider, model, message counts, character counts, duration, and outcome, but should not store raw prompts or model responses by default.

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

}
