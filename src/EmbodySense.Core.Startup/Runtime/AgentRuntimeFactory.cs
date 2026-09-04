using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Loops.Authoring;
using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Retry;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
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
using EmbodySense.Core.Persistence.HumanInput.Continuations;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Policies;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Core.Persistence.Workspace;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Loops.Execution.Retry;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Posture;
using EmbodySense.Core.Startup.Loops.GraphAuthoring;
using EmbodySense.Core.Startup.Loops.InvocationPreparation;
using EmbodySense.Core.Startup.Loops.Schedules;
using EmbodySense.Core.Startup.ContextualRoles;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Triggers.Schedules;
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
    private readonly IToolApprovalPrompt _customLoopApprovalPrompt;
    private readonly IAgentRuntimeConversationPublicationObserver? _conversationPublicationObserver;
    private readonly CodexRuntimeStatus? _codexRuntimeStatus;
    private readonly ICapabilityCatalogTrustProvider _capabilityTrustProvider;
    private readonly IAgentRuntimeAuthenticatedWakeVerifier? _authenticatedWakeVerifier;
    private readonly IAgentRuntimeHumanInputAuthorityProvider? _humanInputAuthorityProvider;
    private readonly IHumanInputSupersedeCandidateRegistry? _humanInputSupersedeCandidateRegistry;
    private readonly IHumanReviewDecisionAuthorizationProvider? _humanReviewDecisionAuthorizationProvider;
    private readonly IGovernedLoopEffectReconciliationAuthorizationProvider? _governedLoopEffectReconciliationAuthorizationProvider;
    private readonly IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider? _governedLoopCoordinatorRepairAuthorityProvider;
    private readonly IGovernedModelPrimaryExecutionBoundaryObserver? _governedModelExecutionObserver;
    private readonly IGovernedLoopLocalCoordinatorBoundaryObserver? _governedLoopLocalCoordinatorBoundaryObserver;
    private readonly IReadOnlyList<ModelProfileRuntimeProvider> _additionalModelProfileProviders;
    private readonly CommandActionRuntimeProvider? _commandActionRuntimeProvider;
    private readonly CustomLoopRunStoreProvider? _customLoopRunStoreProvider;

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
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver = null,
        IGovernedModelPrimaryExecutionBoundaryObserver? governedModelExecutionObserver = null,
        IReadOnlyList<ModelProfileRuntimeProvider>? additionalModelProfileProviders = null,
        CommandActionRuntimeProvider? commandActionRuntimeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRootPath);
        return new AgentRuntimeFactory(
            new ToolApprovalPromptAdapter(approvalPrompt),
            conversationPublicationObserver,
            codexRuntimeStatus,
            new FileCapabilityCatalogTrustProvider(trustRootPath),
            governedModelExecutionObserver,
            additionalModelProfileProviders,
            commandActionRuntimeProvider: commandActionRuntimeProvider);
    }

    /// <summary>Returns an equivalent factory that composes one explicit surface-owned authenticated-event verifier.</summary>
    /// <param name="verifier">The authoritative verifier used only for exact authenticated-event Wait wakes.</param>
    /// <returns>A factory preserving this instance's approval, runtime, observer, and trust-root configuration.</returns>
    public AgentRuntimeFactory WithAuthenticatedWakeVerifier(IAgentRuntimeAuthenticatedWakeVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            verifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory that composes one explicit server-owned Human Input authority provider.</summary>
    /// <remarks>Without this provider, runtime Human Input reads remain available while every mutation returns unavailable.</remarks>
    /// <param name="provider">The authenticated surface boundary supplying canonical lifecycle terms, authorization, and response authentication.</param>
    /// <returns>A factory preserving existing runtime composition with the supplied Human Input boundary.</returns>
    public AgentRuntimeFactory WithHumanInputAuthorityProvider(IAgentRuntimeHumanInputAuthorityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            provider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory with one bounded Web supersede-candidate registry.</summary>
    /// <param name="registry">The Startup-owned short-lived registry that retains no durable lifecycle authority.</param>
    /// <returns>A factory preserving all existing runtime composition and the supplied candidate registry.</returns>
    public AgentRuntimeFactory WithHumanInputSupersedeCandidateRegistry(IHumanInputSupersedeCandidateRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            registry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory with one explicit server-owned Human Review decision authority provider.</summary>
    /// <remarks>Without this provider, Human Review remains structurally catalogued but is unavailable and non-executable.</remarks>
    /// <param name="provider">The authenticated server boundary used to authorize exact Human Review decisions.</param>
    /// <returns>A factory preserving existing runtime composition with the supplied Human Review authority boundary.</returns>
    public AgentRuntimeFactory WithHumanReviewDecisionAuthorizationProvider(IHumanReviewDecisionAuthorizationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            provider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory with request-scoped authenticated Effect Reconciliation authority.</summary>
    /// <remarks>Without this provider, reads remain available while assessment, probe, disposition, and resolution operations fail closed as unavailable.</remarks>
    /// <param name="provider">The trusted interface boundary that derives current actor and scope for exact reconciliation purposes.</param>
    /// <returns>A factory preserving the single runtime and stores with the supplied authority provider.</returns>
    public AgentRuntimeFactory WithGovernedLoopEffectReconciliationAuthorizationProvider(IGovernedLoopEffectReconciliationAuthorizationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            provider);
    }

    /// <summary>Returns an equivalent factory with authenticated current-operator authority for coordinator repair.</summary>
    /// <remarks>Without this provider, coordinator repair preview and submission fail closed as unavailable.</remarks>
    /// <param name="provider">The request-scoped authenticated interface boundary used only for coordinator repair.</param>
    /// <returns>A factory preserving existing runtime composition with the supplied coordinator repair authority boundary.</returns>
    public AgentRuntimeFactory WithGovernedLoopCoordinatorRepairAuthorityProvider(
        IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            provider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory with one explicit server-owned structured command Action provider.</summary>
    /// <param name="provider">The finite template, artifact resolver, and pre-launch isolation composition.</param>
    /// <returns>A factory preserving this instance's approval, runtime, observers, trust root, and wake verifier.</returns>
    public AgentRuntimeFactory WithCommandActionRuntimeProvider(CommandActionRuntimeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            provider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>
    /// Returns an equivalent factory whose runtimes borrow one inference-independent canonical custom-loop run store.
    /// </summary>
    /// <param name="provider">The workspace-host owner that outlives every runtime composed by the returned factory.</param>
    /// <returns>A factory that borrows the provider's store and never transfers its disposal ownership to a runtime.</returns>
    public AgentRuntimeFactory WithCustomLoopRunStoreProvider(CustomLoopRunStoreProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            provider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory with one diagnostic observer for local coordinator heartbeat boundaries.</summary>
    /// <remarks>The observer cannot grant ownership, delay durable mutations, or alter runtime control flow.</remarks>
    /// <param name="observer">The non-authoritative observer composed into the canonical local coordinator.</param>
    /// <returns>A factory preserving the existing runtime composition with the supplied diagnostic observer.</returns>
    public AgentRuntimeFactory WithGovernedLoopLocalCoordinatorBoundaryObserver(
        IGovernedLoopLocalCoordinatorBoundaryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            observer,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            _customLoopApprovalPrompt,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    /// <summary>Returns an equivalent factory that rejects legacy custom-loop tool approvals at the runtime boundary.</summary>
    /// <remarks>
    /// Approval-required custom-loop effects must be represented by canonical durable Human Review before dispatch.
    /// This setting does not change default-conversation tool approvals, which continue to use the surface prompt.
    /// </remarks>
    /// <returns>A factory preserving existing composition while removing connection- or prompt-owned custom-loop approval authority.</returns>
    public AgentRuntimeFactory WithoutLegacyCustomLoopToolApprovals()
    {
        return new AgentRuntimeFactory(
            _approvalPrompt,
            _conversationPublicationObserver,
            _codexRuntimeStatus,
            _capabilityTrustProvider,
            _governedModelExecutionObserver,
            _additionalModelProfileProviders,
            _authenticatedWakeVerifier,
            _humanInputAuthorityProvider,
            _commandActionRuntimeProvider,
            _customLoopRunStoreProvider,
            _governedLoopLocalCoordinatorBoundaryObserver,
            _governedLoopCoordinatorRepairAuthorityProvider,
            _humanReviewDecisionAuthorizationProvider,
            _humanInputSupersedeCandidateRegistry,
            CanonicalGovernedLoopApprovalPrompt.Instance,
            _governedLoopEffectReconciliationAuthorizationProvider);
    }

    internal AgentRuntimeFactory(
        IToolApprovalPrompt approvalPrompt,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver = null,
        CodexRuntimeStatus? codexRuntimeStatus = null,
        ICapabilityCatalogTrustProvider? capabilityTrustProvider = null,
        IGovernedModelPrimaryExecutionBoundaryObserver? governedModelExecutionObserver = null,
        IReadOnlyList<ModelProfileRuntimeProvider>? additionalModelProfileProviders = null,
        IAgentRuntimeAuthenticatedWakeVerifier? authenticatedWakeVerifier = null,
        IAgentRuntimeHumanInputAuthorityProvider? humanInputAuthorityProvider = null,
        CommandActionRuntimeProvider? commandActionRuntimeProvider = null,
        CustomLoopRunStoreProvider? customLoopRunStoreProvider = null,
        IGovernedLoopLocalCoordinatorBoundaryObserver? governedLoopLocalCoordinatorBoundaryObserver = null,
        IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider? governedLoopCoordinatorRepairAuthorityProvider = null,
        IHumanReviewDecisionAuthorizationProvider? humanReviewDecisionAuthorizationProvider = null,
        IHumanInputSupersedeCandidateRegistry? humanInputSupersedeCandidateRegistry = null,
        IToolApprovalPrompt? customLoopApprovalPrompt = null,
        IGovernedLoopEffectReconciliationAuthorizationProvider? governedLoopEffectReconciliationAuthorizationProvider = null)
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
        _customLoopApprovalPrompt = customLoopApprovalPrompt ?? approvalPrompt;
        _conversationPublicationObserver = conversationPublicationObserver;
        _codexRuntimeStatus = codexRuntimeStatus;
        _capabilityTrustProvider = capabilityTrustProvider ?? FileCapabilityCatalogTrustProvider.CreateDefault();
        _authenticatedWakeVerifier = authenticatedWakeVerifier;
        _humanInputAuthorityProvider = humanInputAuthorityProvider;
        _humanInputSupersedeCandidateRegistry = humanInputSupersedeCandidateRegistry;
        _humanReviewDecisionAuthorizationProvider = humanReviewDecisionAuthorizationProvider;
        _governedLoopEffectReconciliationAuthorizationProvider = governedLoopEffectReconciliationAuthorizationProvider;
        _governedLoopCoordinatorRepairAuthorityProvider = governedLoopCoordinatorRepairAuthorityProvider;
        _governedModelExecutionObserver = governedModelExecutionObserver;
        _governedLoopLocalCoordinatorBoundaryObserver = governedLoopLocalCoordinatorBoundaryObserver;
        var additionalProviders = (additionalModelProfileProviders ?? [])
            .Take(33)
            .ToArray();
        if (additionalProviders.Length > 31 || additionalProviders.Any(provider => provider is null))
        {
            throw new ArgumentException("Choose no more than thirty-one non-null additional model-profile providers.", nameof(additionalModelProfileProviders));
        }
        _additionalModelProfileProviders = Array.AsReadOnly(additionalProviders);
        _commandActionRuntimeProvider = commandActionRuntimeProvider;
        _customLoopRunStoreProvider = customLoopRunStoreProvider;
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
        CustomLoopRunStore? customRunStore = null;
        var ownsCustomRunStore = _customLoopRunStoreProvider is null;
        GovernedLoopBackgroundRuntimeHost? governedBackgroundRuntimeHost = null;
        GovernedLoopSleepService? governedSleep = null;
        try
        {
            var permissionPolicy = new PermissionPolicyStore().Load(paths);
            var permissionService = new ToolPermissionService(paths, permissionPolicy);
            var auditLog = new AuditLog(paths);
            var actor = ResolveActor(runtimeSurface);
            var humanInputAuthorityProvider = _humanInputAuthorityProvider
                ?? (runtimeSurface == AgentRuntimeSurface.Cli ? new AgentRuntimeSurfaceHumanInputAuthorityProvider(actor) : null);
            customRunStore = _customLoopRunStoreProvider?.Borrow(paths) ?? new CustomLoopRunStore(paths);
            _capabilityTrustProvider.RequireDisjointWorkspace(paths.RootPath);
            var capabilityAuthority = new CapabilityAuthorityTransaction(paths);
            var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
            var operationalClock = TimeProvider.System;
            var customControlOperations = new CustomLoopControlOperationStore(paths, auditLog);
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
            var humanInputResponses = new HumanInputRequestStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var humanInputCandidateRegistry = _humanInputSupersedeCandidateRegistry ?? new HumanInputSupersedeCandidateRegistry();
            var humanInputCandidatePreparer = new HumanInputSupersedeCandidatePreparer(
                (IHumanInputRequestCatalog)humanInputResponses,
                governedGrantResolver,
                humanInputCandidateRegistry,
                workspaceId,
                actor,
                operationalClock,
                new CanonicalHumanInputRouteIntentSource());
            var humanInputCancellationConvergence = new CustomLoopHumanInputCancellationConvergenceService(
                customRunStore,
                customControlOperations,
                humanInputResponses,
                governedGrantResolver,
                capabilityAuthority,
                workspaceId,
                operationalClock);
            var recovery = new CustomLoopRecoveryService(customRunStore, auditLog, humanInputCancellationConvergence: humanInputCancellationConvergence);
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
            using (recoveryOwnership.Lease)
            {
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
            }

            if (!customExecutionAvailable && !customExecutionReacquisitionAllowed)
            {
                customExecutionGate.RelinquishWorkspaceHost();
            }

            var workspaceClient = new LocalWorkspaceClient(paths);
            var loopDefinitionStore = new LoopDefinitionStore(paths, capabilityAuthority);
            var defaultLoop = await loopDefinitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation, cancellationToken) ?? LoopDefinition.CreateDefaultConversation();
            var capabilityAdmission = CapabilityAdmissionFactory.Create(paths, _capabilityTrustProvider, capabilityAuthority);
            var modelProfileRegistry = new EmbodySense.Core.Startup.Inference.Profiles.ConfiguredModelProfileRegistry(
                effectiveOptions,
                codexRuntimeStatus);
            var modelProfileRuntime = ModelProfileRuntimeComposition.Create(
                new ModelProfileRuntimeProvider(
                    modelProfileRegistry,
                    modelProfileRegistry,
                    adapters => new ConfiguredModelProfileInferenceClientResolver(
                        effectiveOptions,
                        modelProfileRegistry,
                        adapters)),
                _additionalModelProfileProviders);
            var modelProfileMetadata = modelProfileRuntime.MetadataSource;
            var modelProfileAdapters = modelProfileRuntime.AdapterRegistry;
            var modelProfileCapabilityCatalog = new CapabilityCatalogStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var graphCapabilityLifecycleStore = new CapabilityLifecycleMutationStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var graphCapabilityCatalog = new CapabilityLifecycleCatalogStore(modelProfileCapabilityCatalog, graphCapabilityLifecycleStore, capabilityAuthority);
            var modelProfileCatalogFacade = new EmbodySense.Core.Startup.Inference.Profiles.ModelProfileCatalogFacade(
                new EmbodySense.Core.Application.Inference.Profiles.ModelProfileCatalogService(
                    modelProfileCapabilityCatalog,
                    modelProfileMetadata,
                    modelProfileAdapters),
                modelProfileRegistry);
            var modelRoutingAdmission = new EmbodySense.Core.Application.Inference.Profiles.GovernedModelRoutingAdmissionService(
                modelProfileCapabilityCatalog,
                modelProfileMetadata,
                modelProfileRegistry,
                modelProfileAdapters);
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
            var loopAuthoring = new LoopAuthoringFacade(new CustomLoopAuthoringService(customDefinitionStore, auditLog, runStore: customRunStore), loopDefinitionStore, paths, actor);
            var customInvocationOperations = new CustomLoopInvocationOperationStore(paths);
            var customInvocationReceiptRetention = new CustomLoopInvocationReceiptRetentionService(customInvocationOperations, auditLog);
            var customInvocationReceiptWriter = new CustomLoopInvocationReceiptWriter(customInvocationOperations, customInvocationReceiptRetention);
            var customToolAuthority = new CustomLoopToolAuthorityProvider(loopDefinitionStore);
            var customToolEvidence = new CustomLoopRunToolEvidenceSink(customRunStore);
            var customAdmission = new CustomLoopAdmissionService(customDefinitionStore, customRunStore, auditLog, customToolAuthority, capabilityAdmission);
            var customRuntimeContext = new CustomLoopRuntimeContext(paths, conversationState, conversationMemory);
            var customPublisher = new CurrentConversationLoopPublisher(conversationState, conversationMemory, _conversationPublicationObserver);
            var failureClassifier = new GovernedLoopFailureClassifier();
            var legacyInferenceExecutor = new CustomLoopInferenceAttemptExecutor(
                effectiveOptions,
                _customLoopApprovalPrompt,
                customToolAuthority,
                customToolEvidence,
                capabilityAdmission,
                capabilityAuthorityTransaction: capabilityAuthority);
            var legacyRunner = new CustomLoopOrderedRunner(
                customRunStore,
                new CustomLoopContextResolver(),
                legacyInferenceExecutor,
                customPublisher,
                auditLog,
                customToolAuthority,
                attemptCancellationBroker: customExecutionGate,
                capabilityAdmissionService: capabilityAdmission,
                failureClassifier: failureClassifier);
            var triggerWorkspaceId = workspaceId["workspace-sha256:".Length..];
            var triggerQueueStore = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime, timeProvider: operationalClock);
            var triggerQueueAdmission = new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(triggerQueueStore), triggerQueueStore);
            var scheduleStore = new ScheduleStore(paths);
            var scheduleTimeZones = new SystemScheduleTimeZoneAdapter(TimeZoneInfo.GetSystemTimeZones());
            var schedulePayloadSource = new GovernedLoopSchedulePayloadSource(scheduleStore, governedGraphStore);
            var scheduleCurrentEvidence = new ScheduleCurrentEvidenceAdapter(
                triggerWorkspaceId,
                governedBindingSource,
                governedGrantResolver,
                new AuthorityGrantProfileSource(governedAuthorityStore),
                modelProfileCapabilityCatalog,
                schedulePayloadSource,
                capabilityAuthority,
                operationalClock);
            var scheduleRuntime = ScheduleRuntimeFactory.Create(
                scheduleStore,
                scheduleCurrentEvidence,
                new ScheduleRunOverlapAdapter(customRunStore),
                scheduleTimeZones,
                triggerQueueAdmission,
                triggerQueueStore,
                operationalClock);
            var coordinatorEvidenceStore = new GovernedLoopCoordinatorEvidenceStore(paths);
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
            var governedWorkspaceActionRegistry = GovernedWorkspaceActionFactory.CreateRegistry(
                paths,
                capabilityAuthority,
                permissionService);
            var governedEffectAttemptComposition = GovernedLoopEffectAttemptComposition.Create(paths, customRunStore);
            _ = await governedEffectAttemptComposition.IsStorageHealthyAsync(cancellationToken).ConfigureAwait(false);
            var governedWorkspaceActionFacade = governedEffectAttemptComposition.CreateFacade(
                graphCapabilityCatalog,
                governedWorkspaceActionRegistry,
                governedEffectAuthority,
                CapabilityHostRuntime.HostContractVersion,
                CapabilityHostRuntime.Platform,
                operationalClock);
            var governedWorkspaceActionExecutor = new GovernedLoopWorkspaceActionExecutor(governedWorkspaceActionFacade);
            GovernedLoopCommandActionExecutor? governedCommandActionExecutor = null;
            CommandActionRegistrationRegistry? governedCommandActionRegistrations = null;
            EmbodySense.Core.Application.Loops.Execution.Effects.GovernedActuatorOperationRegistry? governedCommandActionOperations = null;
            ICommandActionNativeHost? governedCommandActionNativeHost = null;
            if (_commandActionRuntimeProvider is { } commandActionRuntime)
            {
                var commandActions = GovernedCommandActionFactory.Create(
                    paths,
                    commandActionRuntime.Registrations,
                    commandActionRuntime.ArtifactResolver,
                    commandActionRuntime.IsolationBoundary);
                var commandActionFacade = governedEffectAttemptComposition.CreateFacade(
                    graphCapabilityCatalog,
                    commandActions.Operations,
                    governedEffectAuthority,
                    CapabilityHostRuntime.HostContractVersion,
                    CapabilityHostRuntime.Platform,
                    operationalClock);
                governedCommandActionRegistrations = commandActions.Registrations;
                governedCommandActionOperations = commandActions.Operations;
                governedCommandActionNativeHost = commandActions.NativeHost;
                governedCommandActionExecutor = new GovernedLoopCommandActionExecutor(commandActionFacade, commandActions.Registrations);
            }
            var reconciliationInputSource = new GovernedLoopEffectReconciliationRuntimeInputSource(
                customRunStore,
                governedGraphStore,
                governedEffectAttemptComposition.AttemptReadStore,
                governedCommandActionRegistrations,
                governedWorkspaceActionRegistry);
            var reconciliationProbeRegistry = GovernedLoopEffectReconciliationProbeRegistry.Create(
                [governedWorkspaceActionRegistry, governedCommandActionOperations],
                governedEffectAttemptComposition.ReconciliationCases,
                reconciliationInputSource,
                operationalClock);
            var reconciliationAdmissionApplicationService = new EmbodySense.Core.Application.Loops.Execution.Reconciliation.GovernedLoopEffectReconciliationService(
                governedEffectAttemptComposition.ReconciliationCases,
                new GovernedLoopEffectReconciliationAdmissionAuthorizationSource(),
                reconciliationInputSource,
                reconciliationProbeRegistry,
                governedEffectAttemptComposition.ReconciliationProbeReservations,
                operationalClock);
            var reconciliationAdmission = new GovernedLoopEffectReconciliationAdmissionService(
                workspaceId,
                customRunStore,
                governedEffectAttemptComposition.AttemptReadStore,
                reconciliationProbeRegistry,
                reconciliationAdmissionApplicationService,
                operationalClock);
            var reconciliationRecovery = await reconciliationAdmission.RecoverAsync(cancellationToken).ConfigureAwait(false);
            // Corrupt run evidence remains non-executable, but the read-only reconciliation facade must stay available to inspect already-durable cases.
            if (reconciliationRecovery is not (GovernedLoopEffectReconciliationAdmissionStatus.NotApplicable
                or GovernedLoopEffectReconciliationAdmissionStatus.Opened
                or GovernedLoopEffectReconciliationAdmissionStatus.Replayed
                or GovernedLoopEffectReconciliationAdmissionStatus.Corrupt))
            {
                throw new InvalidOperationException($"effect_reconciliation_recovery_failed: durable ambiguity admission returned {reconciliationRecovery}.");
            }
            var reconciliationService = new EmbodySense.Core.Application.Loops.Execution.Reconciliation.GovernedLoopEffectReconciliationService(
                governedEffectAttemptComposition.ReconciliationCases,
                new AgentRuntimeGovernedLoopEffectReconciliationAuthorizationAdapter(
                    workspaceId,
                    runtimeSurface.Id,
                    _governedLoopEffectReconciliationAuthorizationProvider),
                reconciliationInputSource,
                reconciliationProbeRegistry,
                governedEffectAttemptComposition.ReconciliationProbeReservations,
                operationalClock);
            var reconciliationFacade = new GovernedLoopEffectReconciliationFacade(
                governedEffectAttemptComposition.ReconciliationCases,
                reconciliationService,
                reconciliationProbeRegistry,
                governedEffectAttemptComposition.ReconciliationResolutions);
            var governedModelUsageLedger = new EmbodySense.Core.Persistence.Inference.Profiles.GovernedModelUsageLedgerStore(
                paths,
                _capabilityTrustProvider,
                authorityTransaction: capabilityAuthority);
            var governedModelAttemptAdmission = new EmbodySense.Core.Application.Inference.Profiles.GovernedModelAttemptAdmissionService(
                modelProfileCapabilityCatalog,
                modelProfileMetadata,
                modelProfileAdapters,
                new EmbodySense.Core.Application.Inference.Profiles.ConservativeModelInferenceDataPostureSource(),
                new EmbodySense.Core.Application.Inference.Profiles.CurrentGovernedModelAttemptAuthorityRevalidator(
                    governedGrantResolver,
                    capabilityAdmission),
                governedModelUsageLedger);
            var governedModelPrimaryExecution = new EmbodySense.Core.Application.Inference.Profiles.GovernedModelPrimaryExecutionService(
                governedModelAttemptAdmission,
                new EmbodySense.Core.Application.Inference.Profiles.GovernedModelUsageReconciliationService(governedModelUsageLedger),
                modelProfileRuntime.ClientResolver,
                _governedModelExecutionObserver);
            var governedPublicationAuthority = new GovernedLoopConversationPublicationAuthorityBoundaryProvider(
                governedEffectAuthority);
            var governedToolAuthority = new GovernedLoopReadOnlyWorkspaceToolAdapter();
            var governedInferenceExecutor = new CustomLoopInferenceAttemptExecutor(
                effectiveOptions,
                _customLoopApprovalPrompt,
                governedToolAuthority,
                customToolEvidence,
                capabilityAdmission,
                capabilityAuthorityTransaction: capabilityAuthority,
                effectAuthorityBoundary: governedEffectAuthority,
                modelPrimaryExecution: governedModelPrimaryExecution);
            var governedWaitNodeRelay = new GovernedLoopWaitNodeExecutionRelay();
            var governedRetryNodeRelay = new GovernedLoopRetryNodeExecutionRelay();
            var governedWaitContinuationRelay = new GovernedLoopWaitContinuationRelay();
            var humanInputPolicyStore = new HumanInputPolicyFileStore(paths);
            var humanInputPolicyResolutionService = new HumanInputPolicyResolutionService(humanInputPolicyStore);
            var humanInputPublication = new HumanInputRequestPublicationService(
                customRunStore,
                humanInputResponses,
                governedGrantResolver,
                capabilityAuthority,
                workspaceId,
                operationalClock);
            var humanInputFacade = new HumanInputRuntimeFacade(
                workspaceId,
                (IHumanInputRequestCatalog)humanInputResponses,
                (IHumanInputRequestLifecycleStore)humanInputResponses,
                (IHumanInputResponseLifecycleStore)humanInputResponses,
                governedGrantResolver,
                capabilityAuthority,
                operationalClock,
                humanInputAuthorityProvider,
                humanInputCandidatePreparer);
            var humanInputBindingSource = new HumanInputResponseContinuationBindingSource(humanInputResponses);
            var humanInputRecovery = new HumanInputResponseContinuationRecoveryStore(customRunStore);
            var humanInputReadiness = new HumanInputContinuationReadinessSignal();
            var humanReviewAdmission = new HumanReviewAdmissionService(customRunStore);
            var governedWaitPosture = new GovernedLoopCanonicalWaitCurrentPostureAdapter(
                customRunStore,
                governedGrantResolver);
            var governedSleepStore = new GovernedLoopSleepStore(paths);
            var governedRunner = new CustomLoopOrderedRunner(
                customRunStore,
                new CustomLoopContextResolver(),
                governedInferenceExecutor,
                customPublisher,
                auditLog,
                governedToolAuthority,
                attemptCancellationBroker: customExecutionGate,
                capabilityAdmissionService: capabilityAdmission,
                conversationPublicationAuthorityBoundaryProvider: governedPublicationAuthority,
                firstBoundRunCompletionBoundary: governedRunCompletion,
                waitNodeExecutor: governedWaitNodeRelay,
                retryNodeExecutor: governedRetryNodeRelay,
                workspaceActionExecutor: governedWorkspaceActionExecutor,
                commandActionExecutor: governedCommandActionExecutor,
                failureClassifier: failureClassifier,
                humanInputPolicyResolutionService: humanInputPolicyResolutionService,
                humanInputBindingSource: humanInputBindingSource,
                humanInputRequestPublicationService: humanInputPublication,
                humanInputCancellationConvergence: humanInputCancellationConvergence,
                humanReviewAdmissionService: humanReviewAdmission,
                effectReconciliationAdmissionService: reconciliationAdmission);
            var governedAdmissionStore = new GovernedLoopAdmissionStore(paths, _capabilityTrustProvider, authorityTransaction: capabilityAuthority);
            var governedAdmission = new GovernedLoopAdmissionService(
                workspaceId,
                governedAdmissionStore,
                governedGraphStore,
                governedBindingSource,
                governedRoleSource,
                governedGrantResolver,
                capabilityAdmission,
                modelRoutingAdmission,
                capabilityAuthority,
                new GovernedLoopAdmissionRunIdentityGenerator());
            var governedOrderedRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
                governedRunner,
                customRunStore,
                customRunStore,
                auditLog);
            var governedWaitResume = new GovernedLoopSequentialWaitResumeExecutor(
                customRunStore,
                governedAdmissionStore,
                governedGraphStore,
                governedOrderedRuntime);
            var humanReviewTrustedClock = new TimeProviderHumanReviewTrustedClock(operationalClock);
            var humanReviewContinuationAuthority = new CurrentHumanReviewContinuationAuthoritySource(
                governedGrantResolver,
                capabilityAdmission);
            var humanReviewDecisionAuthorizer = new ServerOwnedHumanReviewDecisionAuthorizer(
                _humanReviewDecisionAuthorizationProvider);
            var humanReviewDecisionService = new HumanReviewDecisionService(
                customRunStore,
                humanReviewDecisionAuthorizer,
                humanReviewTrustedClock);
            var humanReviewContinuationPublicationStore = new HumanReviewContinuationRunStore(customRunStore);
            var humanReviewContinuationPublication = new HumanReviewContinuationPublicationService(
                customRunStore,
                humanReviewContinuationPublicationStore);
            var humanReviewContinuationRecoveryStore = new HumanReviewContinuationRecoveryStore(
                customRunStore,
                governedGraphStore);
            var humanReviewDecisionActionRecoveryStore = new HumanReviewDecisionActionRunStore(
                customRunStore,
                governedGraphStore);
            var humanReviewContinuationConsumer = new HumanReviewContinuationConsumer(
                humanReviewContinuationAuthority,
                governedEffectAttemptComposition.HumanReviewEffectEvidence,
                governedEffectAttemptComposition.HumanReviewEffectCertainty,
                humanReviewTrustedClock);
            var humanReviewOrderedRelease = new HumanReviewOrderedReleaseService(
                customRunStore,
                governedWaitResume,
                governedOrderedRuntime,
                operationalClock,
                humanReviewContinuationAuthority,
                governedEffectAttemptComposition.HumanReviewEffectEvidence,
                governedEffectAttemptComposition.HumanReviewEffectCertainty);
            var humanReviewContinuationRecovery = new HumanReviewContinuationRecoveryCoordinator(
                humanReviewContinuationRecoveryStore,
                humanReviewContinuationConsumer,
                humanReviewOrderedRelease,
                humanReviewTrustedClock);
            var humanReviewDecisionActionRecovery = new HumanReviewDecisionActionRecoveryCoordinator(
                humanReviewDecisionActionRecoveryStore,
                humanReviewContinuationConsumer,
                humanReviewOrderedRelease,
                humanReviewTrustedClock);
            var humanReviewFacade = new HumanReviewRuntimeFacade(
                customRunStore,
                humanReviewDecisionService,
                governedEffectAttemptComposition.HumanReviewEffectEvidence,
                governedEffectAttemptComposition.HumanReviewEffectCertainty);
            var humanReviewComposition = new HumanReviewRuntimeCompositionReadiness(
                humanReviewAdmission,
                humanReviewContinuationPublication,
                humanReviewContinuationConsumer,
                humanReviewContinuationRecovery,
                humanReviewDecisionActionRecovery,
                humanReviewDecisionService,
                humanReviewFacade,
                humanReviewOrderedRelease);
            var humanReviewDependencyReadinessProbe = new HumanReviewRuntimeDependencyReadinessProbe(
                paths,
                governedGraphStore,
                capabilityAdmission,
                capabilityAuthority,
                governedGrantResolver,
                governedEffectAttemptComposition,
                operationalClock,
                _humanReviewDecisionAuthorizationProvider,
                humanReviewComposition);
            var humanReviewRecoveryReadiness = new HumanReviewRecoveryReadinessSignal(
                humanReviewDependencyReadinessProbe.ProbeAsync,
                operationalClock);
            var humanInputContinuation = new HumanInputResponseContinuationService(
                customRunStore,
                humanInputResponses,
                governedSleepStore,
                governedWaitPosture,
                governedWaitResume,
                governedOrderedRuntime,
                operationalClock);
            IGovernedLoopAuthenticatedWakeVerificationPort externalAuthenticatedWakeVerification = _authenticatedWakeVerifier is null
                ? new GovernedLoopUnavailableAuthenticatedWakeVerificationPort()
                : new AgentRuntimeAuthenticatedWakeVerificationAdapter(_authenticatedWakeVerifier);
            var authenticatedWakeVerification = new GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPort(
                externalAuthenticatedWakeVerification,
                humanInputContinuation);
            governedSleep = new GovernedLoopSleepService(
                governedSleepStore,
                governedWaitPosture,
                governedWaitContinuationRelay,
                authenticatedWakeVerification,
                operationalClock);
            humanInputContinuation.BindSleep(governedSleep);
            var governedWait = new GovernedLoopWaitExecutionService(
                customRunStore,
                governedSleep,
                governedWaitPosture,
                capabilityAuthority,
                customExecutionGate,
                governedWaitResume);
            var governedRetryPosture = new GovernedLoopCanonicalRetryCurrentPostureAdapter(
                customRunStore,
                governedWaitPosture,
                capabilityAdmission);
            var governedRetryResume = new GovernedLoopSequentialRetryResumeExecutor(
                governedWaitResume,
                governedOrderedRuntime);
            var governedRetry = new GovernedLoopRetryExecutionService(
                customRunStore,
                governedSleep,
                governedRetryPosture,
                governedRetryResume);
            governedWaitNodeRelay.Bind(governedWait);
            governedRetryNodeRelay.Bind(governedRetry);
            governedWaitContinuationRelay.Bind(governedWait);
            governedWaitContinuationRelay.BindRetry(governedRetry);
            governedWaitContinuationRelay.BindHumanInput(humanInputContinuation);
            governedBackgroundRuntimeHost = new GovernedLoopBackgroundRuntimeHost(
                coordinatorEvidenceStore,
                governedWait,
                governedRetry,
                operationalClock,
                _governedLoopLocalCoordinatorBoundaryObserver);
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
                legacyRunner);
            var originAwareResumeExecutor = new CustomLoopOriginAwareResumeExecutor(
                customRunStore,
                governedResumeExecutor);
            var lifecycleCancellationSignal = new CustomLoopExecutionCancellationSignalGroup(
                legacyRunner,
                governedRunner);
            var customLifecycle = new CustomLoopLifecycleService(
                customRunStore,
                customControlOperations,
                originAwareResumeExecutor,
                legacyInferenceExecutor,
                lifecycleCancellationSignal,
                auditLog,
                customExecutionGate,
                receiptRetention: customControlOperations,
                surface: runtimeSurface.SurfaceId.Id,
                cancellationAuthorityTransaction: capabilityAuthority,
                humanInputCancellationConvergence: humanInputCancellationConvergence);
            var operationalAuthority = new GovernedLoopLocalOperationalControlAuthority(
                workspaceId,
                actor,
                runtimeSurface.Id,
                operationalClock);
            var operationalPosture = new GovernedLoopOperationalPostureService(
                workspaceId,
                triggerWorkspaceId,
                GovernedLoopBackgroundRuntimeHost.CoordinatorId,
                new TriggerQueueOperationalPostureAdapter(triggerQueueStore, triggerWorkspaceId),
                scheduleStore,
                governedSleepStore,
                new CustomLoopRunOperationalPostureAdapter(customRunStore),
                coordinatorEvidenceStore,
                operationalAuthority,
                operationalClock);
            var operationalControls = new GovernedLoopOperationalControlService(
                operationalAuthority,
                new GovernedLoopOperationalControlReceiptStore(paths),
                triggerQueueStore,
                triggerQueueStore,
                scheduleStore,
                customRunStore,
                customLifecycle,
                operationalClock);
            var operationalFacade = new GovernedLoopOperationalFacade(
                workspaceId,
                actor,
                runtimeSurface.Id,
                operationalPosture,
                operationalControls);
            var executableCommandActions = new HashSet<string>(StringComparer.Ordinal);
            if (governedCommandActionRegistrations is not null && governedCommandActionNativeHost is not null)
            {
                foreach (var registration in governedCommandActionRegistrations.Registrations)
                {
                    var availability = await governedCommandActionNativeHost.CheckExecutableAvailabilityAsync(registration, cancellationToken).ConfigureAwait(false);
                    if (availability.Status == EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Available)
                    {
                        executableCommandActions.Add(registration.Template.ContentHash);
                    }
                }
            }
            var graphCatalog = new BuiltInGovernedLoopNodeCatalog(
                governedCommandActionRegistrations?.Registrations ?? [],
                registration => executableCommandActions.Contains(registration.Template.ContentHash),
                graphCapabilityCatalog,
                governedCommandActionNativeHost,
                isHumanInputExecutable: () => humanInputReadiness.IsExecutable,
                isHumanReviewExecutable: () => _humanReviewDecisionAuthorizationProvider is not null
                    && humanReviewRecoveryReadiness.IsExecutable
                    && IsHealthyTrustedUtcClock(operationalClock));
            var graphAuthority = new GovernedLoopAuthoritySnapshotProvider(governedRoleSource);
            var graphAuthoringFacade = new GovernedLoopGraphAuthoringFacade(
                workspaceId,
                actor,
                runtimeSurface.Id,
                governedGraphStore,
                graphCatalog,
                graphAuthority,
                capabilityAuthority,
                new ContextualRoleCatalogFacade(paths.RootPath),
                modelProfileCatalogFacade,
                governedCommandActionRegistrations);
            var governedLoopInvocationPreparation = new GovernedLoopInvocationPreparationFacade(
                workspaceId,
                actor,
                runtimeSurface == AgentRuntimeSurface.Web,
                governedRevisionStore,
                governedBindingSource,
                governedRoleSource,
                governedAuthorityStore,
                governedGrantResolver,
                governedEffectAuthorityEvidence,
                governedAuthorityStore,
                governedAuthorityStore,
                capabilityAdmission,
                modelProfileMetadata,
                modelProfileAdapters,
                capabilityAuthority,
                operationalClock);
            var governedLoopScheduleAuthoring = new GovernedLoopScheduleAuthoringFacade(
                triggerWorkspaceId,
                actor,
                runtimeSurface.Id,
                graphAuthoringFacade,
                governedLoopInvocationPreparation,
                governedGrantResolver,
                scheduleRuntime,
                scheduleTimeZones);
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
                legacyRunner,
                governedRunner,
                customRuntimeContext,
                governedBackgroundRuntimeHost,
                customExecutionAvailable,
                customExecutionReacquisitionAllowed,
                customRecoveryRequired,
                runtimeSurface.Id,
                actor,
                defaultLoop.RoleId,
                customModelSnapshot,
                governedModelUsageLedger,
                workspaceId);
            var scheduleDeliveryProvenance = new ScheduleStore(paths);
            var governedLoops = new GovernedLoopRuntimeFacade(
                governedGraphStore,
                customRunStore,
                customInvocationOperations,
                customInvocationReceiptWriter,
                governedCoordinator,
                customExecutionGate,
                customLoops,
                customRuntimeContext,
                scheduleDeliveryProvenance,
                actor,
                runtimeSurface.Id,
                workspaceId,
                customModelSnapshot,
                governedRoleStore);
            var triggerAuthorizer = new TriggerWorkerCurrentEvidenceAuthorizer(
                workspaceId,
                governedBindingSource,
                governedGrantResolver,
                new AuthorityGrantProfileSource(governedAuthorityStore),
                modelProfileCapabilityCatalog,
                capabilityAuthority,
                operationalClock);
            var triggerWorker = new TriggerWorkerService(
                triggerQueueStore,
                new TriggerWorkerCurrentEvidenceAuthorizerAdapter(triggerAuthorizer),
                new TriggerCustomLoopDispatcher(customLoops, governedLoops),
                new ScheduleTriggerDispatchReadinessService(scheduleStore),
                operationalClock);
            var localBackgroundWork = new GovernedLoopLocalWorkRunner(
                new GovernedLoopBackgroundWorkSource(scheduleStore, governedSleepStore),
                new GovernedLoopWaitAndTriggerOneShotServices(
                    customRunStore,
                    scheduleRuntime,
                    triggerQueueStore,
                    triggerWorker,
                    governedSleep,
                    GovernedLoopLocalWorkRunnerOptions.MaximumCandidateReadLimit),
                new GovernedLoopLocalWorkRunnerOptions(
                    "agent-runtime-trigger-" + Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(30),
                    1,
                    GovernedLoopLocalWorkRunnerOptions.MaximumCandidateReadLimit),
                operationalClock);
            var repairableBackgroundWork = new HumanInputResponseContinuationWorkRunner(
                localBackgroundWork,
                humanInputRecovery,
                humanInputPolicyStore,
                humanInputPublication,
                humanInputContinuation,
                CustomLoopLimits.MaxRecentRunsPageSize,
                operationalClock,
                humanInputReadiness);
            var humanReviewBackgroundWork = new HumanReviewRecoveryRunner(
                repairableBackgroundWork,
                customRunStore,
                humanReviewContinuationPublication,
                humanReviewContinuationRecovery,
                humanReviewDecisionActionRecovery,
                new HumanReviewRecoveryRunnerOptions(
                    CustomLoopLimits.MaxRecentRunsPageSize,
                    "human-review-" + Guid.NewGuid().ToString("N"),
                    "local-background-human-review",
                    TimeSpan.FromMinutes(2)),
                humanReviewRecoveryReadiness);
            var coordinatorRepairDependencies = new GovernedLoopCoordinatorRepairDependencyProbe(humanReviewBackgroundWork, operationalClock);
            governedBackgroundRuntimeHost.BindBackgroundWork(humanReviewBackgroundWork, coordinatorRepairDependencies, workspaceId);
            var coordinatorRepair = new GovernedLoopCoordinatorRepairFacade(
                new GovernedLoopCoordinatorRepairService(
                    workspaceId,
                    new AgentRuntimeGovernedLoopCoordinatorRepairAuthorityAdapter(
                        workspaceId,
                        runtimeSurface.Id,
                        _governedLoopCoordinatorRepairAuthorityProvider,
                        operationalClock),
                    coordinatorEvidenceStore,
                    coordinatorEvidenceStore,
                    coordinatorRepairDependencies,
                    operationalClock),
                governedBackgroundRuntimeHost);
            var runtime = new AgentRuntime(
                paths,
                runtimeSurface,
                conversationMemory,
                startupContext,
                conversationState,
                inferenceClient,
                loopRunner,
                customRunStore,
                ownsCustomRunStore,
                customLoops,
                loopAuthoring,
                governedLoops,
                scheduleDeliveryProvenance,
                operationalFacade,
                graphAuthoringFacade,
                governedLoopInvocationPreparation,
                governedLoopScheduleAuthoring,
                modelProfileCatalogFacade,
                humanInputFacade,
                humanReviewFacade,
                reconciliationFacade,
                defaultConversationReviews,
                codexRuntimeStatus,
                triggerAuthorizer,
                governedBackgroundRuntimeHost,
                coordinatorRepair,
                governedSleep);
            customRunStore = null;
            return runtime;
        }
        catch
        {
            try
            {
                if (governedBackgroundRuntimeHost is not null)
                {
                    await governedBackgroundRuntimeHost.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (ownsCustomRunStore)
                    {
                        customRunStore?.Dispose();
                    }
                }
                finally
                {
                    await customExecutionGate.DisposeAsync();
                }
            }

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

    private static bool IsHealthyTrustedUtcClock(TimeProvider timeProvider)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            return now != default && now.Offset == TimeSpan.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldPreserveCurrentConversation(IReadOnlyList<CustomLoopRecoveryResult> recoveryResults, string currentConversationIdentity)
    {
        return recoveryResults.Any(result => CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(result.Run, currentConversationIdentity));
    }
}
