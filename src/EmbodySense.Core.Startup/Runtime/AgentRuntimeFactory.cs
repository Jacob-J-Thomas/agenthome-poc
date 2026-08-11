using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Composes concrete clients, persistence adapters, governance services, and loop orchestration behind <see cref="AgentRuntime"/>.
/// </summary>
/// <remarks>
/// Construction is side-effect free. <c>CreateAsync</c> resolves a compatible Codex runtime, performs bounded custom-run
/// recovery, coordinates the durable conversation, and transfers ownership of all long-lived resources to the returned runtime.
/// A failed or cancelled composition disposes the workspace execution gate before propagating the failure.
/// </remarks>
public sealed class AgentRuntimeFactory
{
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly IAgentRuntimeConversationPublicationObserver? _conversationPublicationObserver;
    private readonly CodexRuntimeStatus? _codexRuntimeStatus;
    private readonly ICapabilityCatalogTrustProvider _capabilityTrustProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRuntimeFactory"/> type.
    /// </summary>
    /// <param name="approvalPrompt">The interface callback used for governed tool approvals.</param>
    public AgentRuntimeFactory(IAgentToolApprovalPrompt approvalPrompt) : this(new ToolApprovalPromptAdapter(approvalPrompt), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRuntimeFactory"/> type.
    /// </summary>
    /// <param name="approvalPrompt">The interface callback used for governed tool approvals.</param>
    /// <param name="codexRuntimeStatus">A previously verified compatible runtime status to reuse during composition.</param>
    public AgentRuntimeFactory(IAgentToolApprovalPrompt approvalPrompt, CodexRuntimeStatus codexRuntimeStatus)
        : this(new ToolApprovalPromptAdapter(approvalPrompt), null, codexRuntimeStatus)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRuntimeFactory"/> type.
    /// </summary>
    /// <param name="approvalPrompt">The interface callback used for governed tool approvals.</param>
    /// <param name="conversationPublicationObserver">The observer notified after custom-loop output commits to the active conversation.</param>
    public AgentRuntimeFactory(IAgentToolApprovalPrompt approvalPrompt, IAgentRuntimeConversationPublicationObserver conversationPublicationObserver)
        : this(new ToolApprovalPromptAdapter(approvalPrompt), conversationPublicationObserver)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRuntimeFactory"/> type.
    /// </summary>
    /// <param name="approvalPrompt">The interface callback used for governed tool approvals.</param>
    /// <param name="conversationPublicationObserver">The observer notified after custom-loop output commits to the active conversation.</param>
    /// <param name="codexRuntimeStatus">A previously verified compatible runtime status to reuse during composition.</param>
    public AgentRuntimeFactory(
        IAgentToolApprovalPrompt approvalPrompt,
        IAgentRuntimeConversationPublicationObserver conversationPublicationObserver,
        CodexRuntimeStatus codexRuntimeStatus)
        : this(new ToolApprovalPromptAdapter(approvalPrompt), conversationPublicationObserver, codexRuntimeStatus)
    {
    }

    /// <summary>Creates a runtime factory bound to one explicit server-owned capability trust root.</summary>
    public static AgentRuntimeFactory ForFileCapabilityTrustRoot(
        IAgentToolApprovalPrompt approvalPrompt,
        string trustRootPath,
        CodexRuntimeStatus? codexRuntimeStatus = null,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver = null)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRootPath);
        return new AgentRuntimeFactory(new ToolApprovalPromptAdapter(approvalPrompt), conversationPublicationObserver, codexRuntimeStatus, new FileCapabilityCatalogTrustProvider(trustRootPath));
    }

    internal AgentRuntimeFactory(
        IToolApprovalPrompt approvalPrompt,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver = null,
        CodexRuntimeStatus? codexRuntimeStatus = null,
        ICapabilityCatalogTrustProvider? capabilityTrustProvider = null)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        if (codexRuntimeStatus is not null && codexRuntimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible)
        {
            throw new ArgumentException("A pre-resolved Codex runtime status must be usable.", nameof(codexRuntimeStatus));
        }

        if (codexRuntimeStatus is not null && string.IsNullOrWhiteSpace(codexRuntimeStatus.ResolvedExecutablePath))
        {
            throw new ArgumentException("A pre-resolved Codex runtime status must identify the compatible executable.", nameof(codexRuntimeStatus));
        }

        _approvalPrompt = approvalPrompt;
        _conversationPublicationObserver = conversationPublicationObserver;
        _codexRuntimeStatus = codexRuntimeStatus;
        _capabilityTrustProvider = capabilityTrustProvider ?? FileCapabilityCatalogTrustProvider.CreateDefault();
    }

    /// <summary>
    /// Creates a runtime and starts a fresh conversation unless recovery requires the current conversation to be preserved.
    /// </summary>
    /// <param name="model">The nonblank configured Codex model that must be advertised exactly by the resolved runtime.</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <param name="codexExecutablePath">An optional explicit Codex executable path.</param>
    /// <param name="codexSandbox">The sandbox policy passed to the Codex app-server client.</param>
    /// <param name="runtimeSurface">The interface surface used for attribution and actor selection.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result owns the composed inference, persistence, governance, and custom-loop resources.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="model"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="CodexRuntimeUnavailableException">Thrown when no compatible Codex executable and configured model can be resolved.</exception>
    public Task<AgentRuntime> CreateAsync(
        string model,
        string workingDirectory,
        string? codexExecutablePath,
        string codexSandbox,
        AgentRuntimeSurface runtimeSurface,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return CreateAsync(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = model,
            WorkingDirectory = workingDirectory,
            CodexExecutablePath = codexExecutablePath,
            CodexSandbox = codexSandbox
        }, runtimeSurface, cancellationToken);
    }

    /// <summary>
    /// Creates a runtime with an explicit choice about preserving the current durable conversation.
    /// </summary>
    /// <param name="model">The nonblank configured Codex model that must be advertised exactly by the resolved runtime.</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <param name="codexExecutablePath">An optional explicit Codex executable path.</param>
    /// <param name="codexSandbox">The sandbox policy passed to the Codex app-server client.</param>
    /// <param name="runtimeSurface">The interface surface used for attribution and actor selection.</param>
    /// <param name="preserveCurrentConversation">Whether to hydrate the existing transcript instead of rotating to a fresh conversation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result owns the composed inference, persistence, governance, and custom-loop resources.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="model"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="CodexRuntimeUnavailableException">Thrown when no compatible Codex executable and configured model can be resolved.</exception>
    public Task<AgentRuntime> CreateAsync(
        string model,
        string workingDirectory,
        string? codexExecutablePath,
        string codexSandbox,
        AgentRuntimeSurface runtimeSurface,
        bool preserveCurrentConversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return CreateAsync(new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = model,
            WorkingDirectory = workingDirectory,
            CodexExecutablePath = codexExecutablePath,
            CodexSandbox = codexSandbox
        }, runtimeSurface, cancellationToken, preserveCurrentConversation);
    }

    internal async Task<AgentRuntime> CreateAsync(
        LlmInferenceClientOptions options,
        AgentRuntimeSurface runtimeSurface,
        CancellationToken cancellationToken = default,
        bool preserveCurrentConversation = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeSurface);

        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Directory.GetCurrentDirectory() : options.WorkingDirectory;
        var codexRuntimeStatus = await ResolveCodexRuntimeStatusAsync(options, cancellationToken);
        if (codexRuntimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible)
        {
            throw new CodexRuntimeUnavailableException(codexRuntimeStatus);
        }

        var effectiveOptions = options with { WorkingDirectory = workingDirectory, CodexExecutablePath = codexRuntimeStatus.ResolvedExecutablePath };
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
            if (recoveryOwnership.Status is not (CustomLoopExecutionLeaseStatus.Acquired or CustomLoopExecutionLeaseStatus.WorkspaceBusy or CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable))
            {
                throw new InvalidOperationException("custom_workspace_host_busy: restart recovery could not obtain exclusive custom-loop execution ownership without waiting.");
            }

            IReadOnlyList<CustomLoopRecoveryResult> recoveryResults = [];
            var customExecutionAvailable = recoveryOwnership.Status == CustomLoopExecutionLeaseStatus.Acquired;
            var customExecutionReacquisitionAllowed = recoveryOwnership.Status is CustomLoopExecutionLeaseStatus.WorkspaceBusy or CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable;
            var customRecoveryRequired = false;
            preserveCurrentConversation |= !customExecutionAvailable;
            using var recoveryLease = recoveryOwnership.Lease;
            if (recoveryOwnership.Status == CustomLoopExecutionLeaseStatus.Acquired)
            {
                try
                {
                    recoveryResults = await recovery.RecoverAsync(actor, cancellationToken);
                    var recoveryFailed = recoveryResults.Any(result => result.Status is CustomLoopRecoveryStatus.Conflict or CustomLoopRecoveryStatus.Failed);
                    customExecutionAvailable &= !recoveryFailed;
                    preserveCurrentConversation |= recoveryFailed;
                }
                catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException)
                {
                    customExecutionAvailable = false;
                    customExecutionReacquisitionAllowed = true;
                    customRecoveryRequired = true;
                    preserveCurrentConversation = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    customExecutionAvailable = false;
                    preserveCurrentConversation = true;
                }
            }

            if (!customExecutionAvailable && !customExecutionReacquisitionAllowed)
            {
                customExecutionGate.RelinquishWorkspaceHost();
            }

            var capabilityAuthority = new CapabilityAuthorityTransaction(paths);
            var workspaceClient = new LocalWorkspaceClient(paths, new CapabilityAuthorityWorkspaceMutationCommitBoundary(paths, capabilityAuthority));
            var loopDefinitionStore = new LoopDefinitionStore(paths, capabilityAuthority);
            var defaultLoop = await loopDefinitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation, cancellationToken) ?? LoopDefinition.CreateDefaultConversation();
            var capabilityAdmission = CapabilityAdmissionFactory.Create(paths, _capabilityTrustProvider, capabilityAuthority);
            var conversationTurnStore = new DefaultConversationTurnStore(paths);
            var defaultCapabilityRevalidator = new DefaultConversationCapabilityAuthorityRevalidator(conversationTurnStore, loopDefinitionStore, capabilityAdmission, capabilityAuthority);
            var toolBroker = new ToolBroker(paths, permissionService, _approvalPrompt, workspaceClient, auditLog, defaultLoop, new ToolResultRetentionStore(paths), actuationAuthorityBoundary: defaultCapabilityRevalidator);
            var conversationMemory = new ConversationMemoryStore(paths);
            var loopRunStore = new LoopRunStore(paths);
            var conversationTurnRecovery = await new DefaultConversationTurnRecoveryService(
                conversationTurnStore,
                conversationMemory,
                loopRunStore,
                new FileConversationWorkspaceLease(paths),
                capabilityAdmissionService: capabilityAdmission).RecoverAsync(cancellationToken);
            preserveCurrentConversation |= conversationTurnRecovery.PreserveCurrentConversation;
            var startupContext = await new AgentContextProvider(new WorkspaceContextStore()).LoadAsync(paths, cancellationToken);
            var inferenceClient = new LlmInferenceClient(effectiveOptions, toolBroker);
            var conversationState = new ConversationRuntimeState(startupContext, inferenceClient, Path.TrimEndingDirectorySeparator(paths.RootPath), new FileConversationWorkspaceLease(paths));
            using (await conversationState.AcquireExclusiveAccessAsync(cancellationToken))
            {
                var activeConversation = await conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
                if (preserveCurrentConversation || ShouldPreserveCurrentConversation(recoveryResults, activeConversation.Version))
                {
                    conversationState.SynchronizeConversationTranscript(activeConversation.Messages);
                }
                else
                {
                    await conversationMemory.StartFreshConversationAsync(cancellationToken);
                    activeConversation = await conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
                }

                conversationState.SetDurableConversationVersion(activeConversation.Version);
            }

            var loopRunner = new DefaultConversationLoopRunner(inferenceClient, conversationState, conversationMemory, defaultLoop, loopRunStore, runtimeSurface.SurfaceId, conversationTurnStore, capabilityAdmissionService: capabilityAdmission);
            var defaultConversationReviews = new DefaultConversationTurnReviewService(conversationTurnStore, inferenceClient, new FileConversationWorkspaceLease(paths));
            var customDefinitionStore = new CustomLoopDefinitionStore(paths, capabilityAuthority);
            var customInvocationOperations = new CustomLoopInvocationOperationStore(paths);
            var customInvocationReceiptRetention = new CustomLoopInvocationReceiptRetentionService(customInvocationOperations, auditLog);
            var customInvocationReceiptWriter = new CustomLoopInvocationReceiptWriter(customInvocationOperations, customInvocationReceiptRetention);
            var customControlOperations = new CustomLoopControlOperationStore(paths, auditLog);
            var customToolAuthority = new CustomLoopToolAuthorityProvider(loopDefinitionStore);
            var customToolEvidence = new CustomLoopRunToolEvidenceSink(customRunStore);
            var customAdmission = new CustomLoopAdmissionService(customDefinitionStore, customRunStore, auditLog, customToolAuthority, capabilityAdmission);
            var customRuntimeContext = new CustomLoopRuntimeContext(paths, conversationState, conversationMemory);
            var customPublisher = new CurrentConversationLoopPublisher(conversationState, conversationMemory, _conversationPublicationObserver);
            _capabilityTrustProvider.RequireDisjointWorkspace(paths.RootPath);
            var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
            var governedRevisionStore = new GovernedLoopRevisionLifecycleStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var governedGraphStore = new GovernedLoopGraphRevisionStore(paths, governedRevisionStore, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var governedPublicationSource = new GovernedLoopPublishedRevisionSource(governedRevisionStore, capabilityAuthority);
            var governedBindingSource = new GovernedLoopGrantBindingSource(governedPublicationSource, governedGraphStore, capabilityAuthority);
            var governedRoleStore = new ContextualRoleRevisionStore(paths, workspaceId, authorityTransaction: capabilityAuthority);
            var governedRoleSource = new AuthorityGrantRoleSource(
                workspaceId,
                governedRoleStore,
                governedRoleStore,
                new WorkspaceContextualRoleInstructionSourceProbe(paths),
                capabilityAuthority);
            var governedAuthorityStore = new AuthorityProfileStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var governedGrantResolver = new AuthorityGrantResolver(
                governedAuthorityStore,
                new AuthorityGrantProfileSource(governedAuthorityStore),
                governedRoleSource,
                governedPublicationSource,
                governedBindingSource,
                capabilityAuthority);
            var governedEffectAuthorityEvidence = new GovernedLoopEffectAuthorityEvidenceStore(
                paths,
                _capabilityTrustProvider,
                authorityTransaction: capabilityAuthority);
            var governedRunCompletion = new GovernedLoopFirstBoundRunCompletionBoundary(
                governedEffectAuthorityEvidence,
                capabilityAuthority);
            var governedEffectAuthority = new GovernedLoopEffectAuthorityBoundary(
                governedGrantResolver,
                capabilityAdmission,
                governedEffectAuthorityEvidence,
                governedEffectAuthorityEvidence,
                capabilityAuthority);
            var governedPublicationAuthority = new GovernedLoopConversationPublicationAuthorityBoundaryProvider(
                governedEffectAuthority);
            var governedToolAuthority = new GovernedLoopReadOnlyWorkspaceToolAdapter();
            var customInferenceExecutor = new CustomLoopInferenceAttemptExecutor(
                effectiveOptions,
                _approvalPrompt,
                governedToolAuthority,
                customToolEvidence,
                capabilityAdmission,
                capabilityAuthorityTransaction: capabilityAuthority,
                effectAuthorityBoundary: governedEffectAuthority);
            var customRunner = new CustomLoopOrderedRunner(
                customRunStore,
                new CustomLoopContextResolver(),
                customInferenceExecutor,
                customPublisher,
                auditLog,
                governedToolAuthority,
                attemptCancellationBroker: customExecutionGate,
                capabilityAdmissionService: capabilityAdmission,
                conversationPublicationAuthorityBoundaryProvider: governedPublicationAuthority,
                firstBoundRunCompletionBoundary: governedRunCompletion);
            var governedAdmissionStore = new GovernedLoopAdmissionStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var governedAdmission = new GovernedLoopAdmissionService(
                workspaceId,
                governedAdmissionStore,
                governedGraphStore,
                governedBindingSource,
                governedRoleSource,
                governedGrantResolver,
                capabilityAdmission,
                capabilityAuthority,
                new GovernedLoopAdmissionRunIdentityGenerator());
            var governedOrderedRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
                customRunner,
                customRunStore,
                customRunStore,
                auditLog);
            var governedMaterializer = new GovernedLoopSequentialRunMaterializer(
                customRunStore,
                auditLog,
                new GovernedLoopSequentialEventIdentityGenerator());
            var governedCoordinator = new GovernedLoopSequentialInvocationCoordinator(
                workspaceId,
                customInvocationOperations,
                customInvocationReceiptWriter,
                governedAdmission,
                governedMaterializer,
                governedOrderedRuntime);
            var governedResumeExecutor = new GovernedLoopSequentialResumeExecutor(
                customRunStore,
                customRunStore,
                governedAdmissionStore,
                governedGraphStore,
                governedOrderedRuntime,
                customRunner);
            var customLifecycle = new CustomLoopLifecycleService(customRunStore, customControlOperations, governedResumeExecutor, customInferenceExecutor, customRunner, auditLog, customExecutionGate, receiptRetention: customControlOperations, surface: runtimeSurface.SurfaceId.Id);
            var customModelSnapshot = new CustomLoopModelSnapshot(effectiveOptions.Surface.ToString(), effectiveOptions.Model);
            var customLoops = new CustomLoopRuntimeFacade(
                customDefinitionStore,
                customRunStore,
                customInvocationOperations,
                customInvocationReceiptWriter,
                customControlOperations,
                customExecutionGate,
                customAdmission,
                recovery,
                customLifecycle,
                customRunner,
                customRuntimeContext,
                customExecutionAvailable,
                customExecutionReacquisitionAllowed,
                customRecoveryRequired,
                runtimeSurface.Id,
                actor,
                defaultLoop.RoleId,
                customModelSnapshot);
            var governedLoops = new GovernedLoopRuntimeFacade(
                governedGraphStore,
                customRunStore,
                customInvocationOperations,
                customInvocationReceiptWriter,
                governedCoordinator,
                customExecutionGate,
                customLoops,
                customRuntimeContext,
                actor,
                runtimeSurface.Id,
                customModelSnapshot,
                governedRoleStore);

            return new AgentRuntime(
                paths,
                runtimeSurface,
                conversationMemory,
                startupContext,
                conversationState,
                inferenceClient,
                loopRunner,
                customLoops,
                governedLoops,
                defaultConversationReviews,
                codexRuntimeStatus);
        }
        catch
        {
            await customExecutionGate.DisposeAsync();
            throw;
        }
    }

    private async Task<CodexRuntimeStatus> ResolveCodexRuntimeStatusAsync(LlmInferenceClientOptions options, CancellationToken cancellationToken)
    {
        if (_codexRuntimeStatus is null)
        {
            return await new CodexRuntimeStatusReader().ReadAsync(options.CodexExecutablePath, options.Model, cancellationToken);
        }

        var configuredModel = NormalizeOptional(options.Model);
        if (!string.Equals(configuredModel, NormalizeOptional(_codexRuntimeStatus.ConfiguredModel), StringComparison.Ordinal))
        {
            throw new ArgumentException("The pre-resolved Codex runtime status was produced for a different configured model.", nameof(options));
        }

        var requestedExecutablePath = NormalizeOptional(options.CodexExecutablePath);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(requestedExecutablePath, NormalizeOptional(_codexRuntimeStatus.RequestedExecutablePath), pathComparison))
        {
            throw new ArgumentException("The pre-resolved Codex runtime status was produced for a different explicit executable request.", nameof(options));
        }

        return _codexRuntimeStatus;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
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

        return WorkspaceActors.ForSurface(surface.SurfaceId);
    }

    private static bool ShouldPreserveCurrentConversation(IReadOnlyList<CustomLoopRecoveryResult> recoveryResults, string currentConversationIdentity)
    {
        return recoveryResults.Any(result => CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(result.Run, currentConversationIdentity));
    }
}
