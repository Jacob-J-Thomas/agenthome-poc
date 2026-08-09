using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Persistence.ToolResults;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.LocalWorkspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Creates the fresh, attempt-owned inference client used for one custom-loop model attempt.
/// </summary>
/// <param name="options">The effective provider options, including the run-admitted model.</param>
/// <param name="toolBroker">The bounded governed broker when the attempt admits tools; otherwise null.</param>
/// <returns>A non-null disposable inference client that is not shared across attempts.</returns>
public delegate ILlmInferenceClient CustomLoopInferenceClientFactory(LlmInferenceClientOptions options, IToolBroker? toolBroker);

/// <summary>
/// Executes one fresh-provider custom-loop inference attempt with run-scoped governed tool authority
/// and mandatory correlated evidence.
/// </summary>
public sealed class CustomLoopInferenceAttemptExecutor : ICustomLoopInferenceAttemptExecutor, ICustomLoopModelAvailability
{
    private readonly LlmInferenceClientOptions _options;
    private readonly WorkspacePaths _paths;
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly CustomLoopInferenceClientFactory? _clientFactory;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly ICustomLoopToolEvidenceSink _evidenceSink;
    private readonly ToolResultRetentionStore _toolResultRetentionStore;
    private readonly ICapabilityAdmissionService _capabilityAdmissionService;
    private readonly ICapabilityAuthorityTransaction _capabilityAuthorityTransaction;

    /// <summary>
    /// Creates the production attempt executor over the workspace's live role authority and run evidence.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="approvalPrompt">The approval prompt.</param>
    /// <param name="clientFactory">The client factory.</param>
    public CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IAgentToolApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory? clientFactory = null) : this(options, approvalPrompt, clientFactory, CreateCapabilityComposition(options))
    {
    }

    private CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IAgentToolApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory? clientFactory,
        (WorkspacePaths Paths, ICapabilityAuthorityTransaction Authority, ICapabilityAdmissionService Admission) composition) : this(
            options,
            new ToolApprovalPromptAdapter(approvalPrompt),
            new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(composition.Paths, composition.Authority)),
            new CustomLoopRunToolEvidenceSink(new CustomLoopRunStore(composition.Paths)),
            composition.Admission,
            clientFactory,
            composition.Authority)
    {
    }

    internal CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IToolApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory? clientFactory = null) : this(options, approvalPrompt, clientFactory, CreateCapabilityComposition(options))
    {
    }

    private CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IToolApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory? clientFactory,
        (WorkspacePaths Paths, ICapabilityAuthorityTransaction Authority, ICapabilityAdmissionService Admission) composition) : this(options, approvalPrompt, new AdmittedMaximumAuthorityProvider(), new NullToolEvidenceSink(), composition.Admission, clientFactory, composition.Authority)
    {
    }

    /// <summary>
    /// Creates an attempt executor over explicit authority and evidence boundaries.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="approvalPrompt">The approval prompt.</param>
    /// <param name="authorityProvider">The authority provider.</param>
    /// <param name="evidenceSink">The evidence sink.</param>
    /// <param name="capabilityAdmissionService">The exact capability effect-boundary revalidator.</param>
    /// <param name="clientFactory">The client factory.</param>
    /// <param name="capabilityAuthorityTransaction">The optional capability-authority transaction shared with admission and governed skill mutation.</param>
    public CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IToolApprovalPrompt approvalPrompt,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ICustomLoopToolEvidenceSink evidenceSink,
        ICapabilityAdmissionService capabilityAdmissionService,
        CustomLoopInferenceClientFactory? clientFactory = null,
        ICapabilityAuthorityTransaction? capabilityAuthorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentNullException.ThrowIfNull(authorityProvider);
        ArgumentNullException.ThrowIfNull(evidenceSink);
        ArgumentNullException.ThrowIfNull(capabilityAdmissionService);
        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            throw new ArgumentException("Custom-loop inference requires a working directory.", nameof(options));
        }

        _options = options with { WorkingDirectory = Path.GetFullPath(options.WorkingDirectory) };
        _paths = new WorkspacePaths(_options.WorkingDirectory);
        _approvalPrompt = approvalPrompt;
        _clientFactory = clientFactory;
        _auditLog = new AuditLog(_paths);
        _authorityProvider = authorityProvider;
        _evidenceSink = evidenceSink;
        _capabilityAdmissionService = capabilityAdmissionService;
        _capabilityAuthorityTransaction = capabilityAuthorityTransaction ?? new CapabilityAuthorityTransaction(_paths);
        _toolResultRetentionStore = new ToolResultRetentionStore(_paths);
    }

    private static (WorkspacePaths Paths, ICapabilityAuthorityTransaction Authority, ICapabilityAdmissionService Admission) CreateCapabilityComposition(LlmInferenceClientOptions options)
    {
        var paths = CreatePaths(options);
        var authority = new CapabilityAuthorityTransaction(paths);
        var admission = CapabilityAdmissionFactory.Create(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), authority);
        return (paths, authority, admission);
    }

    private static WorkspacePaths CreatePaths(LlmInferenceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            throw new ArgumentException("Custom-loop inference requires a working directory.", nameof(options));
        }

        return new WorkspacePaths(Path.GetFullPath(options.WorkingDirectory));
    }

    /// <summary>
    /// Validates one admitted attempt, composes its optional bounded tool broker, executes a fresh
    /// provider client, and disposes that client after the attempt.
    /// </summary>
    /// <param name="request">The run, model, instruction, authority, tool-budget, and correlation evidence for the attempt.</param>
    /// <param name="cancellationToken">The token used to cancel authority resolution, governance, and provider work.</param>
    /// <param name="providerRequestStarted">An optional callback invoked when the provider request starts.</param>
    /// <returns>A task whose result contains canonical model output identity and consumed governed-tool count.</returns>
    /// <remarks>
    /// Every attempt requires a disposable client so provider transport state is not reused. Client
    /// cleanup failures are intentionally suppressed so they cannot replace the authoritative attempt
    /// outcome. Request, authority, and model mismatches fail before provider execution.
    /// </remarks>
    public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CapabilityAdmission);
        var allowedCapabilities = LoopCapabilityRequirements.GetAssignedCapabilityIds(request.CapabilityAdmission.Requirements);
        var currentCapabilities = await _capabilityAdmissionService.RevalidateAsync(request.CapabilityAdmission, allowedCapabilities, cancellationToken);
        if (!currentCapabilities.IsValid)
        {
            throw new InvalidOperationException($"Capability authority changed before provider dispatch: {currentCapabilities.Detail}");
        }

        var authority = request.AuthoritySnapshot ?? await _authorityProvider.ResolveAsync(request.RoleId, request.AdmittedToolAssignments, cancellationToken);
        request = request with { AuthoritySnapshot = authority };
        ValidateRequest(request, _options.Surface);
        BoundedCorrelatedToolBroker? boundedBroker = null;
        IToolBroker? toolBroker = null;
        if (request.AllowTools)
        {
            var loopDefinition = CreateRunScopedToolDefinition(request);
            var permissionService = new ReloadingToolPermissionService(_paths, new PermissionPolicyStore());
            var observer = new CorrelatedToolEvidenceObserver(_evidenceSink, request);
            var retention = new ToolResultRetentionService(_auditLog, loopDefinition, _toolResultRetentionStore);
            var revalidator = new CustomLoopToolActuationAuthorityRevalidator(_authorityProvider, request, observer, _capabilityAdmissionService);
            var mutationBoundary = new CapabilityAuthorityWorkspaceMutationCommitBoundary(_paths, _capabilityAuthorityTransaction);
            var broker = new ToolBroker(_paths, permissionService, _approvalPrompt, new LocalWorkspaceClient(_paths, mutationBoundary), _auditLog, loopDefinition, _toolResultRetentionStore, observer, revalidator);
            boundedBroker = new BoundedCorrelatedToolBroker(broker, _auditLog, _authorityProvider, retention, observer, _paths, request);
            toolBroker = boundedBroker;
        }

        var effectiveOptions = _options with { Model = request.ModelSnapshot.Model };
        var usesInjectedFactory = _clientFactory is not null;
        var client = usesInjectedFactory
            ? _clientFactory!(effectiveOptions, toolBroker)
            : new LlmInferenceClient(effectiveOptions, toolBroker, providerRequestStarted: providerRequestStarted);
        if (client is null)
        {
            throw new InvalidOperationException("The custom-loop inference client factory returned null.");
        }
        if (client is not IAsyncDisposable && client is not IDisposable)
        {
            throw new InvalidOperationException("Custom-loop inference clients must be disposable so every attempt owns a fresh provider transport.");
        }

        try
        {
            if (usesInjectedFactory)
            {
                providerRequestStarted?.Invoke();
            }

            var response = await client.GenerateAsync(request.InferenceRequest, cancellationToken: cancellationToken);
            return new CustomLoopInferenceAttemptResult(
                response.OutputText,
                response.Surface.ToString(),
                response.Model,
                response.ProviderResponseId,
                boundedBroker?.ToolRequestsConsumed ?? 0);
        }
        finally
        {
            try
            {
                if (client is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    ((IDisposable)client).Dispose();
                }
            }
            catch
            {
                // Attempt outcome is authoritative; per-attempt transport cleanup must not replace it.
            }
        }
    }

    /// <summary>
    /// Determines whether this executor can honor the exact provider and model captured at admission.
    /// </summary>
    /// <param name="modelSnapshot">The immutable admitted provider and model.</param>
    /// <param name="cancellationToken">The token checked before returning the synchronous result.</param>
    /// <returns>A task whose result is true only for a supported provider and exact configured model.</returns>
    public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSnapshot.Provider);
        cancellationToken.ThrowIfCancellationRequested();
        var available = (_clientFactory is not null || _options.Surface == LlmInferenceSurface.OpenAiCodex)
            && ProviderMatches(_options.Surface, modelSnapshot.Provider)
            && string.Equals(_options.Model, modelSnapshot.Model, StringComparison.Ordinal);
        return Task.FromResult(available);
    }

    private static LoopDefinition CreateRunScopedToolDefinition(CustomLoopInferenceAttemptRequest request)
    {
        var capabilityIds = request.AuthoritySnapshot!.EffectiveAssignments.Select(MapCapability).Order(StringComparer.Ordinal).ToArray();
        return LoopDefinition.CreateDefaultConversation() with
        {
            Id = request.LoopId,
            DisplayName = $"Custom loop {request.LoopId}",
            Description = "Run-scoped governed authority for one admitted custom-loop inference attempt.",
            RoleId = request.RoleId,
            Trigger = LoopTrigger.Manual,
            CapabilityIds = capabilityIds
        };
    }

    private static string MapCapability(CustomLoopToolAssignment assignment)
    {
        return assignment switch
        {
            CustomLoopToolAssignment.List => LoopCapabilityIds.WorkspaceCommandFor(ToolCommand.List),
            CustomLoopToolAssignment.Read => LoopCapabilityIds.WorkspaceCommandFor(ToolCommand.Read),
            CustomLoopToolAssignment.Search => LoopCapabilityIds.WorkspaceCommandFor(ToolCommand.Search),
            _ => throw new ArgumentOutOfRangeException(nameof(assignment), assignment, "Only admitted list, read, and search assignments are implemented.")
        };
    }

    private static void ValidateRequest(CustomLoopInferenceAttemptRequest request, LlmInferenceSurface configuredSurface)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LoopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RoleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DefinitionHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AttemptCorrelationId);
        ArgumentNullException.ThrowIfNull(request.ModelSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelSnapshot.Provider);
        ArgumentNullException.ThrowIfNull(request.AdmittedToolAssignments);
        ArgumentNullException.ThrowIfNull(request.InferenceRequest);
        ArgumentNullException.ThrowIfNull(request.AuthoritySnapshot);

        if (request.DefinitionVersion < 1 || request.Iteration < 1 || request.Attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Definition version, iteration, and attempt must be positive.");
        }

        if (request.DefinitionHash.Length != CustomLoopLimits.Sha256HexCharacters || request.DefinitionHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Definition hash must be a lowercase SHA-256 hexadecimal value.", nameof(request));
        }

        if (request.ToolRequestsUsedInRun < 0 || request.ToolRequestsUsedInRun > CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerRun)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.ToolRequestsUsedInRun, "Persisted run tool-request usage is outside the governed evidence limit.");
        }

        if (!ProviderMatches(configuredSurface, request.ModelSnapshot.Provider))
        {
            throw new ArgumentException("The admitted provider snapshot does not match this inference executor.", nameof(request));
        }

        if (request.AdmittedToolAssignments.Any(assignment => !Enum.IsDefined(assignment) || assignment == CustomLoopToolAssignment.Unknown)
            || request.AdmittedToolAssignments.Distinct().Count() != request.AdmittedToolAssignments.Count)
        {
            throw new ArgumentException("Admitted tool assignments must be unique implemented list, read, or search values.", nameof(request));
        }

        if (!request.AuthoritySnapshot.IsValid
            || !string.Equals(request.AuthoritySnapshot.RoleId, request.RoleId, StringComparison.Ordinal)
            || request.AuthoritySnapshot.AdmittedMaximum.Length != request.AdmittedToolAssignments.Count
            || request.AuthoritySnapshot.AdmittedMaximum.Any(value => !request.AdmittedToolAssignments.Contains(value))
            || request.AuthoritySnapshot.EffectiveAssignments.Any(value => !request.AdmittedToolAssignments.Contains(value)))
        {
            throw new ArgumentException("The attempt authority snapshot is invalid or widens the immutable admitted maximum.", nameof(request));
        }

        if (request.IsExit)
        {
            if (request.AllowTools || request.AdmittedToolAssignments.Count > 0 || !string.Equals(request.StepId, "exit", StringComparison.Ordinal))
            {
                throw new ArgumentException("Exit attempts must be tool-less and use the deterministic `exit` step id.", nameof(request));
            }
        }
        else if (request.AllowTools != request.AuthoritySnapshot.EffectiveAssignments.Length > 0 || string.Equals(request.StepId, "exit", StringComparison.Ordinal))
        {
            throw new ArgumentException("Inference attempt tool exposure must exactly match the current effective intersection.", nameof(request));
        }
    }

    private static bool ProviderMatches(LlmInferenceSurface surface, string provider)
    {
        return surface switch
        {
            LlmInferenceSurface.OpenAiCodex => provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("openai-codex", StringComparison.OrdinalIgnoreCase)
                || provider.Equals(nameof(LlmInferenceSurface.OpenAiCodex), StringComparison.OrdinalIgnoreCase),
            LlmInferenceSurface.AzureAiFoundry => provider.Equals("azure", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("azure-ai-foundry", StringComparison.OrdinalIgnoreCase)
                || provider.Equals(nameof(LlmInferenceSurface.AzureAiFoundry), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
