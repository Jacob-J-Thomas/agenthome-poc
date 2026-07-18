using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Core.Startup.Runtime.Models;

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
        AgentRuntimeSurface runtimeSurface,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = model,
            WorkingDirectory = workingDirectory,
            CodexExecutablePath = codexExecutablePath,
            CodexSandbox = codexSandbox
        }, runtimeSurface, cancellationToken);
    }

    internal async Task<AgentRuntime> CreateAsync(
        LlmInferenceClientOptions options,
        AgentRuntimeSurface runtimeSurface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeSurface);

        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Directory.GetCurrentDirectory() : options.WorkingDirectory;
        var effectiveOptions = options with { WorkingDirectory = workingDirectory };
        var paths = new WorkspacePaths(workingDirectory);
        var permissionPolicy = new PermissionPolicyStore().Load(paths);
        var permissionService = new ToolPermissionService(paths, permissionPolicy);
        var auditLog = new AuditLog(paths);
        var workspaceClient = new LocalWorkspaceClient(paths);
        var loopDefinitionStore = new LoopDefinitionStore(paths);
        var defaultLoop = await loopDefinitionStore.LoadAsync("default-conversation", cancellationToken) ?? EmbodySense.Core.Common.Loops.Models.LoopDefinition.CreateDefaultConversation();
        var toolBroker = new ToolBroker(paths, permissionService, _approvalPrompt, workspaceClient, auditLog, defaultLoop);
        var conversationMemory = new ConversationMemoryStore(paths);
        var loopRunStore = new LoopRunStore(paths);
        var startupContext = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths, cancellationToken);
        var inferenceClient = new LlmInferenceClient(effectiveOptions, toolBroker);
        var conversationState = new ConversationRuntimeState(startupContext, inferenceClient, Path.TrimEndingDirectorySeparator(paths.RootPath), new FileConversationWorkspaceLease(paths));
        using (await conversationState.AcquireExclusiveAccessAsync(cancellationToken))
        {
            await conversationMemory.StartFreshConversationAsync(cancellationToken);
        }

        var loopRunner = new DefaultConversationLoopRunner(inferenceClient, conversationState, conversationMemory, defaultLoop, loopRunStore, runtimeSurface.SurfaceId);

        return new AgentRuntime(
            paths,
            runtimeSurface,
            conversationMemory,
            startupContext,
            conversationState,
            inferenceClient,
            loopRunner);
    }
}
