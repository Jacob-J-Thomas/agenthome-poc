using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions;
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
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
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
    private readonly IGovernedLoopEffectAuthorityBoundary? _effectAuthorityBoundary;

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
            composition.Authority,
            effectAuthorityBoundary: null)
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
        (WorkspacePaths Paths, ICapabilityAuthorityTransaction Authority, ICapabilityAdmissionService Admission) composition) : this(
            options,
            approvalPrompt,
            new AdmittedMaximumAuthorityProvider(),
            new NullToolEvidenceSink(),
            composition.Admission,
            clientFactory,
            composition.Authority,
            effectAuthorityBoundary: null)
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
    /// <param name="effectAuthorityBoundary">The fresh exact-reference authority boundary that fences provider transport.</param>
    public CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IToolApprovalPrompt approvalPrompt,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ICustomLoopToolEvidenceSink evidenceSink,
        ICapabilityAdmissionService capabilityAdmissionService,
        CustomLoopInferenceClientFactory? clientFactory = null,
        ICapabilityAuthorityTransaction? capabilityAuthorityTransaction = null,
        IGovernedLoopEffectAuthorityBoundary? effectAuthorityBoundary = null)
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
        _effectAuthorityBoundary = effectAuthorityBoundary;
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
        ValidateRequestEnvelope(request, _options.Surface);
        var providerAuthorityRequest = ResolveProviderAuthorityRequest(request);
        var authority = request.AuthoritySnapshot ?? await _authorityProvider.ResolveAsync(request.RoleId, request.AdmittedToolAssignments, cancellationToken);
        request = request with { AuthoritySnapshot = authority };
        ValidateAuthoritySnapshot(request);
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
            : new LlmInferenceClient(effectiveOptions, toolBroker);
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
            var response = await client.GenerateAsync(
                request.InferenceRequest,
                responseChunkHandler: null,
                cancellationToken,
                CreateProviderTransportBoundary(providerAuthorityRequest, providerRequestStarted));
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

    private GovernedLoopEffectAuthorityRequest ResolveProviderAuthorityRequest(CustomLoopInferenceAttemptRequest request)
    {
        var hasAdmission = request.AdmissionReceipt is not null;
        var hasExecution = request.ExecutionBinding is not null;
        var hasArtifact = request.GraphArtifact is not null;
        if (!hasAdmission && !hasExecution && !hasArtifact)
        {
            throw Stopped(
                "Canonical provider dispatch requires the complete immutable admission, execution-binding, and graph-artifact proof.",
                GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
                decision: null);
        }

        if (!hasAdmission || !hasExecution || !hasArtifact || !TryCreateProviderAuthorityRequest(request, out var authorityRequest))
        {
            throw Stopped(
                "The canonical provider authority proof was incomplete, substituted, or inconsistent; no transport was created.",
                GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
                decision: null);
        }

        if (_effectAuthorityBoundary is null)
        {
            throw Stopped(
                "No fresh execution-authority boundary was composed for canonical provider dispatch.",
                GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
                decision: null);
        }

        return authorityRequest!;
    }

    private static bool TryCreateProviderAuthorityRequest(
        CustomLoopInferenceAttemptRequest request,
        out GovernedLoopEffectAuthorityRequest? authorityRequest)
    {
        authorityRequest = null;
        try
        {
            var admission = request.AdmissionReceipt!;
            var execution = request.ExecutionBinding!;
            var artifact = request.GraphArtifact!;
            if (!GovernedLoopAdmissionValidator.Validate(admission).IsValid
                || !Equals(execution, admission.Evidence.Binding)
                || !string.Equals(execution.RunId, request.RunId, StringComparison.Ordinal)
                || !string.Equals(artifact.Graph.GraphId, request.LoopId, StringComparison.Ordinal)
                || !string.Equals(artifact.Graph.OwningRole.Identity.RoleId, request.RoleId, StringComparison.Ordinal)
                || !string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifact.ArtifactHash, admission.Intent.GraphArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifact.LayoutHash, admission.Intent.GraphLayoutHash, StringComparison.Ordinal)
                || !Equals(artifact.RevisionArtifact.Revision, execution.Revision)
                || !Equals(admission.Intent.Publication.Revision, execution.Revision)
                || !Equals(artifact.Graph.OwningRole, admission.Intent.Role)
                || !CapabilityAdmissionMatches(request.CapabilityAdmission, admission.Evidence.CapabilityAdmission)
                || request.IsExit
                || !CustomLoopArtifactIdentifier.IsValid(request.StepId, GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters)
                || !CustomLoopArtifactIdentifier.IsValid(request.AttemptCorrelationId, GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters)
                || request.Attempt is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt)
            {
                return false;
            }

            var node = artifact.Graph.Nodes.SingleOrDefault(candidate => string.Equals(candidate.Id, request.StepId, StringComparison.Ordinal));
            if (node is null || !Equals(node.Descriptor, EmbodySense.Core.Application.Loops.Sequential.GovernedLoopSequentialNodeDescriptors.ProviderInference))
            {
                return false;
            }

            var nodeCapabilities = node.AuthorityCeiling.CapabilityIds;
            var requiresWorkspace = nodeCapabilities.Contains(WorkspaceCommandCapabilityId, StringComparer.Ordinal);
            if (!nodeCapabilities.Contains(ModelInferenceCapabilityId, StringComparer.Ordinal)
                || nodeCapabilities.Any(value => !string.Equals(value, ModelInferenceCapabilityId, StringComparison.Ordinal)
                    && !string.Equals(value, WorkspaceCommandCapabilityId, StringComparison.Ordinal))
                || request.AllowTools && !requiresWorkspace)
            {
                return false;
            }

            var requiredIds = requiresWorkspace
                ? new[] { ModelInferenceCapabilityId, WorkspaceCommandCapabilityId }
                : [ModelInferenceCapabilityId];
            var pins = requiredIds.Select(id => admission.Evidence.CapabilityAdmission.Pins.SingleOrDefault(pin => string.Equals(pin.DescriptorIdentity.Id.Value, id, StringComparison.Ordinal))).ToArray();
            if (pins.Any(pin => pin is null)
                || pins.Select(pin => pin!.DescriptorIdentity.Id.Value).Distinct(StringComparer.Ordinal).Count() != requiredIds.Length)
            {
                return false;
            }

            var requiredPins = pins.Select(pin => pin!).ToArray();
            var requiredIdentities = requiredPins.Select(pin => pin.DescriptorIdentity).ToArray();
            if (!admission.Evidence.EffectiveAuthority.Capabilities.ToHashSet().IsSupersetOf(requiredIdentities))
            {
                return false;
            }

            var admittedAuthority = admission.Evidence.EffectiveAuthority;
            var requiredAuthority = new AuthorityCeiling(
                requiredIdentities,
                admittedAuthority.DataClasses,
                MaxTargetCount: 1,
                requiresWorkspace ? CapabilitySideEffectClass.ReadOnly : CapabilitySideEffectClass.None,
                AllowsRecurrence: false,
                AllowsExternalPublication: false,
                AllowsIrreversibleAction: false);
            var effectOperationId = "provider-" + CustomLoopTraceContentHash.Compute(
                $"provider-transport-v1\n{request.RunId}\n{request.StepId}\n{request.Attempt}\n{request.AttemptCorrelationId}");
            authorityRequest = new GovernedLoopEffectAuthorityRequest(
                admission,
                execution,
                artifact,
                request.StepId,
                request.Attempt,
                effectOperationId,
                request.AttemptCorrelationId,
                GovernedLoopEffectBoundaryKind.ProviderTransport,
                requiredAuthority,
                requiredPins);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            authorityRequest = null;
            return false;
        }
    }

    private InferenceProviderTransportCommitBoundary CreateProviderTransportBoundary(
        GovernedLoopEffectAuthorityRequest authorityRequest,
        Action? injectedProviderRequestStarted)
    {
        return async (commitTransportWrite, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(commitTransportWrite);
            var boundaryOpen = 1;
            var commitCount = 0;
            var commitCompleted = 0;
            GovernedLoopEffectAuthorityExecutionResult<bool>? result = null;
            using var boundaryLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                result = await _effectAuthorityBoundary!.ExecuteAsync(
                    authorityRequest,
                    async token =>
                    {
                        // Yield before crossing transport so a boundary that captures or starts the callback
                        // without awaiting it closes first and cannot dispatch after returning.
                        await Task.Yield();
                        if (Volatile.Read(ref boundaryOpen) == 0 || Interlocked.Increment(ref commitCount) != 1)
                        {
                            throw new InvalidOperationException("The provider transport callback may run exactly once and only while its authority boundary is open.");
                        }

                        using var commitLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, boundaryLifetime.Token);
                        try
                        {
                            commitLifetime.Token.ThrowIfCancellationRequested();
                            injectedProviderRequestStarted?.Invoke();
                            await commitTransportWrite(commitLifetime.Token);
                            return true;
                        }
                        finally
                        {
                            Volatile.Write(ref commitCompleted, 1);
                        }
                    },
                    cancellationToken);
            }
            finally
            {
                Volatile.Write(ref boundaryOpen, 0);
                await boundaryLifetime.CancelAsync();
            }

            if (result is null)
            {
                throw AuthorityProtocolStopped();
            }

            var observedCommitCount = Volatile.Read(ref commitCount);
            var observedCommitCompleted = Volatile.Read(ref commitCompleted);
            if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.Decided
                && DecisionExactlyMatchesRequest(result.Decision, authorityRequest)
                && result.Decision!.Disposition == GovernedLoopEffectAuthorityDisposition.Direct
                && result.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                && result.CommitInvoked
                && result.Result is true
                && observedCommitCount == 1
                && observedCommitCompleted == 1)
            {
                return;
            }

            if (!IsCoherentStoppedResult(result, authorityRequest, observedCommitCount, observedCommitCompleted))
            {
                throw AuthorityProtocolStopped();
            }

            throw Stopped(result.Detail, result.Status, result.EvidenceStatus, result.Decision);
        };
    }

    private static bool IsCoherentStoppedResult(
        GovernedLoopEffectAuthorityExecutionResult<bool> result,
        GovernedLoopEffectAuthorityRequest request,
        int commitCount,
        int commitCompleted)
    {
        if (commitCount != 0 || commitCompleted != 0 || result.CommitInvoked || result.Result is true)
        {
            return false;
        }

        var exactDecision = result.Decision is not null && DecisionExactlyMatchesRequest(result.Decision, request);
        return result.Status switch
        {
            GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest
                or GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable => result.Decision is null
                    && result.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
            GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected => exactDecision
                && result.Decision!.Disposition != GovernedLoopEffectAuthorityDisposition.Direct
                && result.EvidenceStatus != GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown
                && Enum.IsDefined(result.EvidenceStatus),
            GovernedLoopEffectAuthorityExecutionStatus.Decided => exactDecision
                && result.Decision!.Disposition != GovernedLoopEffectAuthorityDisposition.Direct
                && result.EvidenceStatus is GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                    or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
            _ => false,
        };
    }

    private static bool DecisionExactlyMatchesRequest(
        GovernedLoopEffectAuthorityDecision? decision,
        GovernedLoopEffectAuthorityRequest request)
    {
        if (decision is null || !GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            return false;
        }

        var receipt = request.AdmissionReceipt;
        var admitted = decision.AdmittedAuthority;
        return string.Equals(decision.RunId, request.ExecutionBinding.RunId, StringComparison.Ordinal)
            && decision.ExecutionGeneration == request.ExecutionBinding.ExecutionGeneration
            && string.Equals(decision.NodeId, request.NodeId, StringComparison.Ordinal)
            && decision.NodeAttempt == request.NodeAttempt
            && string.Equals(decision.EffectOperationId, request.EffectOperationId, StringComparison.Ordinal)
            && string.Equals(decision.CorrelationId, request.CorrelationId, StringComparison.Ordinal)
            && decision.BoundaryKind == request.BoundaryKind
            && string.Equals(decision.AdmissionReceiptHash, receipt.ContentHash, StringComparison.Ordinal)
            && AuthorityCeilingSubset.IsEqual(decision.RequiredAuthority, request.RequiredAuthority)
            && decision.RequiredCapabilityPins.SequenceEqual(request.RequiredCapabilityPins)
            && Equals(admitted.Grant, receipt.Intent.AuthorityGrant)
            && Equals(admitted.Binding.Profile, receipt.Evidence.GrantProfile)
            && Equals(admitted.Binding.Role, receipt.Intent.Role)
            && Equals(admitted.Binding.Loop, receipt.Intent.Publication)
            && Equals(admitted.Boundary, receipt.Evidence.GrantBoundary)
            && AuthorityCeilingSubset.IsEqual(admitted.Ceiling, receipt.Evidence.EffectiveAuthority)
            && admitted.CapabilityPins.SequenceEqual(receipt.Evidence.CapabilityAdmission.Pins)
            && admitted.ObservedCapabilityPins.Count == 0
            && string.Equals(admitted.DependencyEvidenceHash, receipt.Evidence.GrantDependencyEvidenceHash, StringComparison.Ordinal);
    }

    private static GovernedLoopEffectAuthorityStoppedException AuthorityProtocolStopped()
        => Stopped(
            "The provider authority boundary returned a missing, malformed, mismatched, or incomplete protocol result; the exact transport outcome could not be proved.",
            GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
            decision: null);

    private static bool CapabilityAdmissionMatches(CapabilityAdmissionSnapshot? left, CapabilityAdmissionSnapshot right)
    {
        return left is not null
            && CapabilityAdmissionSnapshotValidator.Validate(left) is null
            && left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.WorkspaceScopeId, right.WorkspaceScopeId, StringComparison.Ordinal)
            && string.Equals(left.RequirementsHash, right.RequirementsHash, StringComparison.Ordinal)
            && left.AdmittedAtUtc == right.AdmittedAtUtc
            && left.Pins.SequenceEqual(right.Pins)
            && left.Evidence.SequenceEqual(right.Evidence);
    }

    private static GovernedLoopEffectAuthorityStoppedException Stopped(
        string detail,
        GovernedLoopEffectAuthorityExecutionStatus status,
        GovernedLoopEffectAuthorityEvidenceStoreStatus evidenceStatus,
        GovernedLoopEffectAuthorityDecision? decision)
    {
        var safeDetail = string.IsNullOrWhiteSpace(detail) ? "The governed provider effect was stopped before transport." : detail;
        return new GovernedLoopEffectAuthorityStoppedException(safeDetail, status, evidenceStatus, decision);
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

    private static void ValidateRequestEnvelope(CustomLoopInferenceAttemptRequest request, LlmInferenceSurface configuredSurface)
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
        ArgumentNullException.ThrowIfNull(request.CapabilityAdmission);

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

        if (request.AllowTools && request.AdmittedToolAssignments.Count == 0)
        {
            throw new ArgumentException("Tool exposure requires at least one immutable admitted tool assignment.", nameof(request));
        }

        if (request.IsExit)
        {
            if (request.AllowTools || request.AdmittedToolAssignments.Count > 0 || !string.Equals(request.StepId, "exit", StringComparison.Ordinal))
            {
                throw new ArgumentException("Exit attempts must be tool-less and use the deterministic `exit` step id.", nameof(request));
            }
        }
        else if (string.Equals(request.StepId, "exit", StringComparison.Ordinal))
        {
            throw new ArgumentException("Inference attempts cannot use the deterministic `exit` step id.", nameof(request));
        }
    }

    private static void ValidateAuthoritySnapshot(CustomLoopInferenceAttemptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.AuthoritySnapshot);
        if (!request.AuthoritySnapshot.IsValid
            || !string.Equals(request.AuthoritySnapshot.RoleId, request.RoleId, StringComparison.Ordinal)
            || request.AuthoritySnapshot.AdmittedMaximum.Length != request.AdmittedToolAssignments.Count
            || request.AuthoritySnapshot.AdmittedMaximum.Any(value => !request.AdmittedToolAssignments.Contains(value))
            || request.AuthoritySnapshot.EffectiveAssignments.Any(value => !request.AdmittedToolAssignments.Contains(value)))
        {
            throw new ArgumentException("The attempt authority snapshot is invalid or widens the immutable admitted maximum.", nameof(request));
        }

        if (request.AllowTools != request.AuthoritySnapshot.EffectiveAssignments.Length > 0)
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
