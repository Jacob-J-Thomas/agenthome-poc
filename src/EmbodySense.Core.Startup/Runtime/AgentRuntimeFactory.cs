using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class AgentRuntimeFactory
{
    private readonly IToolApprovalPrompt _approvalPrompt;

    public AgentRuntimeFactory(IAgentToolApprovalPrompt approvalPrompt) : this(new ToolApprovalPromptAdapter(approvalPrompt))
    {
    }

    internal AgentRuntimeFactory(IToolApprovalPrompt approvalPrompt)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);

        _approvalPrompt = approvalPrompt;
    }

    public Task<AgentRuntime> CreateAsync(
        string? model,
        string workingDirectory,
        string? codexExecutablePath,
        string codexSandbox,
        string runtimeSurface,
        CancellationToken cancellationToken = default)
    {
        // TODO(runtime-surface-api): Replace the loose "cli"/"web" runtimeSurface strings with a Core.Startup-owned surface identifier API.
        // Deferred to keep this cutover focused; revisit when runtime factory ergonomics are cleaned up without blocking future custom surfaces.
        return CreateAsync(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = model,
            WorkingDirectory = workingDirectory,
            CodexExecutablePath = codexExecutablePath,
            CodexSandbox = codexSandbox
        }, runtimeSurface, cancellationToken);
    }

    internal async Task<AgentRuntime> CreateAsync(LlmInferenceClientOptions options, string runtimeSurface, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var surface = RuntimeSurface.Create(runtimeSurface);
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
        var conversationState = new ConversationRuntimeState(startupContext, inferenceClient);
        var loopRunner = new DefaultConversationLoopRunner(inferenceClient, conversationState, conversationMemory);

        return new AgentRuntime(
            paths,
            conversationMemory,
            startupContext,
            conversationState,
            inferenceClient,
            loopRunner,
            surface);
    }
}
