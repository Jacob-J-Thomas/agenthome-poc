using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Inference;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class AgentRuntimeFactory
{
    private readonly IToolApprovalPrompt _approvalPrompt;

    public AgentRuntimeFactory(IToolApprovalPrompt approvalPrompt)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);

        _approvalPrompt = approvalPrompt;
    }

    public async Task<AgentRuntime> CreateAsync(LlmInferenceClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Directory.GetCurrentDirectory() : options.WorkingDirectory;
        var effectiveOptions = options with { WorkingDirectory = workingDirectory };
        var paths = new WorkspacePaths(workingDirectory);
        var permissionPolicy = new PermissionPolicyStore().Load(paths);
        var permissionService = new ToolPermissionService(paths, permissionPolicy);
        var auditLog = new AuditLog(paths);
        var workspaceClient = new LocalWorkspaceClient(paths);
        var toolBroker = new ToolBroker(paths, permissionService, _approvalPrompt, workspaceClient, auditLog);
        var conversationMemory = new ConversationMemoryStore(paths);
        var startupContext = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths, cancellationToken);
        await conversationMemory.StartFreshConversationAsync(cancellationToken);
        var inferenceClient = new LlmInferenceClient(effectiveOptions, toolBroker);
        var session = new AgentHarnessSession(inferenceClient, conversationMemory, startupContext);

        return new AgentRuntime(paths, conversationMemory, startupContext, session, inferenceClient);
    }
}
