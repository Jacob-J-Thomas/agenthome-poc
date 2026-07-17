using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;

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
        var customExecutionGate = new CustomLoopWorkspaceExecutionGate(paths);
        try
        {
            var permissionPolicy = new PermissionPolicyStore().Load(paths);
            var permissionService = new ToolPermissionService(paths, permissionPolicy);
            var auditLog = new AuditLog(paths);
            var actor = ResolveActor(runtimeSurface);
            var customRunStore = new CustomLoopRunStore(paths);
            var recovery = new CustomLoopRecoveryService(customRunStore, auditLog);
            var recoveryOperationId = "recovery-" + Guid.NewGuid().ToString("N");
            var recoveryOwnership = customExecutionGate.TryAcquire(recoveryOperationId, new string('0', CustomLoopLimits.Sha256HexCharacters));
            if (recoveryOwnership.Status != CustomLoopExecutionLeaseStatus.Acquired || recoveryOwnership.Lease is null)
            {
                throw new InvalidOperationException("custom_workspace_host_busy: restart recovery could not obtain exclusive custom-loop execution ownership without waiting.");
            }

            using (recoveryOwnership.Lease)
            {
                var recoveryResults = await recovery.RecoverAsync(actor, cancellationToken);
                if (recoveryResults.Any(result => result.Status is CustomLoopRecoveryStatus.Conflict or CustomLoopRecoveryStatus.Failed))
                {
                    throw new InvalidOperationException("custom_loop_recovery_failed: persisted custom-loop state could not be recovered exactly; no automatic execution was started.");
                }
            }

            var workspaceClient = new LocalWorkspaceClient(paths);
            var loopDefinitionStore = new LoopDefinitionStore(paths);
            var defaultLoop = await loopDefinitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation, cancellationToken) ?? LoopDefinition.CreateDefaultConversation();
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
            var customDefinitionStore = new CustomLoopDefinitionStore(paths);
            var customInvocationOperations = new CustomLoopInvocationOperationStore(paths);
            var customControlOperations = new CustomLoopControlOperationStore(paths);
            var customToolAuthority = new CustomLoopToolAuthorityProvider(loopDefinitionStore);
            var customToolEvidence = new CustomLoopRunToolEvidenceSink(customRunStore);
            var customAdmission = new CustomLoopAdmissionService(customDefinitionStore, customRunStore, auditLog, customToolAuthority);
            var customRuntimeContext = new CustomLoopRuntimeContext(paths, conversationState);
            var customPublisher = new CurrentConversationLoopPublisher(conversationState, conversationMemory);
            var customInferenceExecutor = new CustomLoopInferenceAttemptExecutor(effectiveOptions, _approvalPrompt, customToolAuthority, customToolEvidence);
            var customRunner = new CustomLoopOrderedRunner(customRunStore, new CustomLoopContextResolver(), customInferenceExecutor, customPublisher, auditLog, customToolAuthority);
            var customLifecycle = new CustomLoopLifecycleService(customRunStore, customControlOperations, customRunner, customInferenceExecutor, customRunner, auditLog, customExecutionGate);
            var customModelSnapshot = new CustomLoopModelSnapshot(effectiveOptions.Surface.ToString(), effectiveOptions.Model);
            var customLoops = new CustomLoopRuntimeFacade(
                customDefinitionStore,
                customRunStore,
                customInvocationOperations,
                customExecutionGate,
                customAdmission,
                recovery,
                customLifecycle,
                customRunner,
                customRuntimeContext,
                runtimeSurface.Id,
                actor,
                defaultLoop.RoleId,
                customModelSnapshot);

            return new AgentRuntime(
                paths,
                runtimeSurface,
                conversationMemory,
                startupContext,
                conversationState,
                inferenceClient,
                loopRunner,
                customLoops);
        }
        catch
        {
            await customExecutionGate.DisposeAsync();
            throw;
        }
    }

    private static string ResolveActor(AgentRuntimeSurface surface)
    {
        if (surface == AgentRuntimeSurface.Web)
        {
            return WorkspaceActors.Web;
        }

        if (surface == AgentRuntimeSurface.Cli)
        {
            return WorkspaceActors.Cli;
        }

        return "embodysense." + surface.Id;
    }
}
