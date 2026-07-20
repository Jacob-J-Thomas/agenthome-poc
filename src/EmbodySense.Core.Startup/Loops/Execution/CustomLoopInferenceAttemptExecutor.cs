using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Governance.Permissions;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Clients.LocalWorkspace;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
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
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference;

namespace EmbodySense.Core.Startup.Loops.Execution;

public delegate ILlmInferenceClient CustomLoopInferenceClientFactory(LlmInferenceClientOptions options, IToolBroker? toolBroker);

public sealed class CustomLoopInferenceAttemptExecutor : ICustomLoopInferenceAttemptExecutor, ICustomLoopModelAvailability
{
    private readonly LlmInferenceClientOptions _options;
    private readonly WorkspacePaths _paths;
    private readonly IToolApprovalPrompt _approvalPrompt;
    private readonly CustomLoopInferenceClientFactory? _clientFactory;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly ICustomLoopToolEvidenceSink _evidenceSink;

    public CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IAgentToolApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory? clientFactory = null) : this(
            options,
            new ToolApprovalPromptAdapter(approvalPrompt),
            new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(CreatePaths(options))),
            new CustomLoopRunToolEvidenceSink(new CustomLoopRunStore(CreatePaths(options))),
            clientFactory)
    {
    }

    internal CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IToolApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory? clientFactory = null) : this(options, approvalPrompt, new AdmittedMaximumAuthorityProvider(), new NullToolEvidenceSink(), clientFactory)
    {
    }

    public CustomLoopInferenceAttemptExecutor(
        LlmInferenceClientOptions options,
        IToolApprovalPrompt approvalPrompt,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ICustomLoopToolEvidenceSink evidenceSink,
        CustomLoopInferenceClientFactory? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(approvalPrompt);
        ArgumentNullException.ThrowIfNull(authorityProvider);
        ArgumentNullException.ThrowIfNull(evidenceSink);
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

    public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
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
            var broker = new ToolBroker(_paths, permissionService, _approvalPrompt, new LocalWorkspaceClient(_paths), _auditLog, loopDefinition, observer);
            boundedBroker = new BoundedCorrelatedToolBroker(broker, _auditLog, _authorityProvider, observer, _paths, request);
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

        if (request.ToolRequestsUsedInRun < 0 || request.ToolRequestsUsedInRun > CustomLoopLimits.MaxRecordedGovernedToolRequestsPerRun)
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

    private sealed class ReloadingToolPermissionService : IToolPermissionService
    {
        private readonly WorkspacePaths _paths;
        private readonly IPermissionPolicyStore _policyStore;

        public ReloadingToolPermissionService(WorkspacePaths paths, IPermissionPolicyStore policyStore)
        {
            _paths = paths;
            _policyStore = policyStore;
        }

        public ToolPermissionCheck Evaluate(ToolRequest request)
        {
            return new ToolPermissionService(_paths, _policyStore.Load(_paths)).Evaluate(request);
        }
    }

    private sealed class AdmittedMaximumAuthorityProvider : ICustomLoopToolAuthorityProvider
    {
        private static readonly CustomLoopToolAssignment[] Catalog = [CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search];

        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            var admitted = admittedMaximum.ToArray();
            return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
                roleId,
                admitted,
                admitted,
                Catalog,
                admitted,
                CustomLoopToolAuthorityProvider.ComputeRoleCeilingHash(roleId, admitted),
                CustomLoopToolAuthorityProvider.ComputeCatalogHash(),
                DateTimeOffset.UtcNow,
                true,
                "Test-only authority treats the immutable admitted maximum as the current role ceiling."));
        }
    }

    private sealed class NullToolEvidenceSink : ICustomLoopToolEvidenceSink
    {
        public Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CorrelatedToolEvidenceObserver : IToolGovernanceObserver
    {
        private readonly ICustomLoopToolEvidenceSink _sink;
        private readonly CustomLoopInferenceAttemptRequest _attempt;
        private readonly Dictionary<string, RequestEvidenceState> _requests = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public CorrelatedToolEvidenceObserver(ICustomLoopToolEvidenceSink sink, CustomLoopInferenceAttemptRequest attempt)
        {
            _sink = sink;
            _attempt = attempt;
        }

        public async Task ReserveAsync(ToolRequest request, string resolvedTarget, CustomLoopToolAuthoritySnapshot authority, int requestOrdinal, CancellationToken cancellationToken)
        {
            var correlationId = request.CorrelationId ?? throw new CustomLoopToolEvidenceIntegrityException("A bounded tool request must have a correlation id before evidence reservation.");
            lock (_gate)
            {
                if (!_requests.TryAdd(correlationId, new RequestEvidenceState(requestOrdinal, request, resolvedTarget, authority)))
                {
                    throw new CustomLoopToolEvidenceIntegrityException("A tool request correlation id was reused within one inference attempt.");
                }
            }

            await RecordAsync(State(correlationId), CustomLoopToolEvidencePhase.RequestReserved, null, null, null, false, cancellationToken);
        }

        public Task ObserveApprovalRequestAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ObserveDecisionAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default)
        {
            return RecordAsync(State(request), CustomLoopToolEvidencePhase.GovernanceDecided, requestId, evidence, null, false, cancellationToken);
        }

        public Task ObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken = default)
        {
            return RecordAsync(State(result.Request), CustomLoopToolEvidencePhase.OutcomeObserved, result.RequestId, result.Governance, result, false, cancellationToken);
        }

        public Task RecordReturnedAsync(ToolResult result, CancellationToken cancellationToken)
        {
            return RecordAsync(State(result.Request), CustomLoopToolEvidencePhase.OutcomeObserved, result.RequestId, result.Governance, result, true, cancellationToken);
        }

        public Task RecordIntegrityAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            return RecordAsync(State(request), CustomLoopToolEvidencePhase.IntegrityFailed, null, null, null, false, cancellationToken);
        }

        private async Task RecordAsync(RequestEvidenceState state, CustomLoopToolEvidencePhase phase, string? brokerRequestId, ToolGovernanceEvidence? governance, ToolResult? result, bool returnedToModel, CancellationToken cancellationToken)
        {
            var canonical = result is not null ? ToolResultFormatter.FormatResults([result]) : null;
            var evidence = new CustomLoopToolTraceEvidence(
                phase,
                state.Ordinal,
                state.Request.CorrelationId!,
                brokerRequestId,
                state.Request.Command,
                state.Request.TargetPath,
                state.Request.Content,
                state.Request.Pattern,
                state.ResolvedTarget,
                state.Authority,
                BoundGovernance(governance),
                result?.Outcome,
                canonical,
                canonical is null ? null : CustomLoopTraceContentHash.Compute(canonical),
                canonical?.Length,
                returnedToModel,
                CustomLoopLimits.MaxGovernedToolEvidenceReservationUtf8Bytes);
            await _sink.RecordAsync(_attempt.RunId, _attempt.Iteration, _attempt.StepId, _attempt.Attempt, evidence, cancellationToken);
        }

        private static ToolGovernanceEvidence? BoundGovernance(ToolGovernanceEvidence? governance)
        {
            if (governance is null)
            {
                return null;
            }

            ValidateGovernanceText(governance.AuthorityDetail, nameof(governance.AuthorityDetail), required: true);
            ValidateGovernanceText(governance.PermissionMatchedPath, nameof(governance.PermissionMatchedPath), required: false, CustomLoopLimits.MaxGovernedToolTargetCharacters);
            ValidateGovernanceText(governance.PermissionDetail, nameof(governance.PermissionDetail), required: false);
            ValidateGovernanceText(governance.ApprovalDecisionBy, nameof(governance.ApprovalDecisionBy), required: false);
            ValidateGovernanceText(governance.ApprovalDetail, nameof(governance.ApprovalDetail), required: false);
            return governance;
        }

        private static void ValidateGovernanceText(string? value, string field, bool required, int maximumCharacters = CustomLoopLimits.MaxToolGovernanceDetailCharacters)
        {
            if (required && string.IsNullOrWhiteSpace(value) || value is not null && value.Length > maximumCharacters)
            {
                throw new CustomLoopToolEvidenceIntegrityException($"Governance field `{field}` exceeds its exact durable evidence bound.");
            }
        }

        private RequestEvidenceState State(ToolRequest request)
        {
            return State(request.CorrelationId ?? throw new CustomLoopToolEvidenceIntegrityException("Governed tool evidence lost its request correlation id."));
        }

        private RequestEvidenceState State(string correlationId)
        {
            lock (_gate)
            {
                return _requests.TryGetValue(correlationId, out var state)
                    ? state
                    : throw new CustomLoopToolEvidenceIntegrityException("Governed tool evidence was observed before its exact request reservation.");
            }
        }

        private sealed record RequestEvidenceState(int Ordinal, ToolRequest Request, string ResolvedTarget, CustomLoopToolAuthoritySnapshot Authority);
    }

    private sealed class BoundedCorrelatedToolBroker : IToolBroker
    {
        private readonly IToolBroker _inner;
        private readonly IAuditLog _auditLog;
        private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
        private readonly CorrelatedToolEvidenceObserver _observer;
        private readonly WorkspacePaths _paths;
        private readonly CustomLoopInferenceAttemptRequest _attempt;
        private readonly int _toolRequestsUsedInRun;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private int _requestsObserved;
        private int _toolRequestsConsumed;
        private ToolRequest? _overLimitDeniedRequest;

        public BoundedCorrelatedToolBroker(
            IToolBroker inner,
            IAuditLog auditLog,
            ICustomLoopToolAuthorityProvider authorityProvider,
            CorrelatedToolEvidenceObserver observer,
            WorkspacePaths paths,
            CustomLoopInferenceAttemptRequest request)
        {
            _inner = inner;
            _auditLog = auditLog;
            _authorityProvider = authorityProvider;
            _observer = observer;
            _paths = paths;
            _attempt = request;
            _toolRequestsUsedInRun = request.ToolRequestsUsedInRun;
        }

        public IReadOnlyList<ToolCommand> AvailableCommands => Volatile.Read(ref _requestsObserved) >= CustomLoopLimits.MaxGovernedToolRequestsPerAttempt
            || _toolRequestsUsedInRun + Volatile.Read(ref _toolRequestsConsumed) >= CustomLoopLimits.MaxGovernedToolRequestsPerRun
                ? []
                : _inner.AvailableCommands;

        public int ToolRequestsConsumed => Volatile.Read(ref _toolRequestsConsumed);

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                return await ExecuteSerialAsync(request, cancellationToken);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private async Task<ToolResult> ExecuteSerialAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var requestOrdinal = Interlocked.Increment(ref _requestsObserved);
            ToolRequest boundedRequest;
            try
            {
                boundedRequest = BoundRequest(request);
            }
            catch (CustomLoopToolEvidenceIntegrityException exception)
            {
                Interlocked.Increment(ref _toolRequestsConsumed);
                await AuditMalformedRequestAsync(request, requestOrdinal, exception.Message, cancellationToken);
                throw;
            }

            var authority = await _authorityProvider.ResolveAsync(_attempt.RoleId, _attempt.AdmittedToolAssignments, cancellationToken);
            var correlation = CreateAuditCorrelation(authority);
            var correlatedRequest = boundedRequest with { AuditCorrelation = correlation };
            string resolvedTarget;
            try
            {
                resolvedTarget = ResolveTarget(correlatedRequest.TargetPath);
            }
            catch (CustomLoopToolEvidenceIntegrityException exception)
            {
                Interlocked.Increment(ref _toolRequestsConsumed);
                await AuditMalformedRequestAsync(correlatedRequest, requestOrdinal, exception.Message, cancellationToken);
                throw;
            }

            var attemptLimitExceeded = requestOrdinal > CustomLoopLimits.MaxGovernedToolRequestsPerAttempt;
            var runLimitExceeded = _toolRequestsUsedInRun + requestOrdinal > CustomLoopLimits.MaxGovernedToolRequestsPerRun;
            if ((attemptLimitExceeded || runLimitExceeded) && _overLimitDeniedRequest is not null)
            {
                Interlocked.Increment(ref _toolRequestsConsumed);
                await _observer.RecordIntegrityAsync(_overLimitDeniedRequest, cancellationToken);
                throw new CustomLoopToolEvidenceIntegrityException("A governed tool request repeated after the one visible over-limit denial; the attempt failed without actuation.");
            }

            await _observer.ReserveAsync(correlatedRequest, resolvedTarget, authority, requestOrdinal, cancellationToken);
            Interlocked.Increment(ref _toolRequestsConsumed);

            if (attemptLimitExceeded || runLimitExceeded)
            {
                _overLimitDeniedRequest = correlatedRequest;
                var scope = attemptLimitExceeded ? "attempt" : "run";
                var limit = attemptLimitExceeded ? CustomLoopLimits.MaxGovernedToolRequestsPerAttempt : CustomLoopLimits.MaxGovernedToolRequestsPerRun;
                return await DenyAsync(correlatedRequest, authority, resolvedTarget, requestOrdinal, scope, limit, cancellationToken);
            }

            var assignment = MapAssignment(correlatedRequest.Command);
            if (!authority.IsValid || assignment is null || !authority.EffectiveAssignments.Contains(assignment.Value) || !_inner.AvailableCommands.Contains(correlatedRequest.Command))
            {
                return await DenyAuthorityAsync(correlatedRequest, authority, resolvedTarget, requestOrdinal, cancellationToken);
            }

            var result = await _inner.ExecuteAsync(correlatedRequest, cancellationToken);
            await _observer.RecordReturnedAsync(result, cancellationToken);
            return result;
        }

        private Task AuditMalformedRequestAsync(ToolRequest request, int requestOrdinal, string detail, CancellationToken cancellationToken)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["command_value"] = (int)request.Command,
                ["target_present"] = request.TargetPath is not null,
                ["target_characters"] = request.TargetPath?.Length,
                ["target_hash"] = HashOptional(request.TargetPath),
                ["content_present"] = request.Content is not null,
                ["content_characters"] = request.Content?.Length,
                ["content_hash"] = HashOptional(request.Content),
                ["pattern_present"] = request.Pattern is not null,
                ["pattern_characters"] = request.Pattern?.Length,
                ["pattern_hash"] = HashOptional(request.Pattern),
                ["correlation_present"] = request.CorrelationId is not null,
                ["correlation_characters"] = request.CorrelationId?.Length,
                ["correlation_hash"] = HashOptional(request.CorrelationId),
                ["run_id"] = _attempt.RunId,
                ["loop_id"] = _attempt.LoopId,
                ["role_id"] = _attempt.RoleId,
                ["definition_version"] = _attempt.DefinitionVersion,
                ["definition_hash"] = _attempt.DefinitionHash,
                ["iteration"] = _attempt.Iteration,
                ["step_id"] = _attempt.StepId,
                ["attempt"] = _attempt.Attempt,
                ["attempt_correlation_id"] = _attempt.AttemptCorrelationId,
                ["tool_requests_used_in_run"] = _toolRequestsUsedInRun,
                ["tool_request_ordinal"] = requestOrdinal
            };
            return _auditLog.AppendAsync(AuditEvent.Create(
                AuditSchema.Actors.Tool,
                AuditSchema.Actions.ToolLoopAuthorityEvaluate,
                "malformed-tool-request",
                AuditSchema.Outcomes.Failed,
                detail,
                metadata), cancellationToken);
        }

        private static string? HashOptional(string? value)
        {
            return value is null ? null : CustomLoopTraceContentHash.Compute(value);
        }

        private async Task<ToolResult> DenyAsync(ToolRequest request, CustomLoopToolAuthoritySnapshot authority, string resolvedTarget, int requestOrdinal, string scope, int limit, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var detail = $"Denied workspace tool request because the custom-loop {scope} tool-request limit was reached.";
            await RecordAuthorityAsync(requestId, request, authority, resolvedTarget, requestOrdinal, AuditSchema.Outcomes.Denied, detail, scope, limit, cancellationToken);
            var governance = new ToolGovernanceEvidence(ToolAuthorityDecision.Denied, detail, null, null, null, null, ToolApprovalDecision.NotEvaluated, null, null);
            await _observer.ObserveDecisionAsync(requestId, request, resolvedTarget, governance, cancellationToken);
            var result = new ToolResult(ToolExecutionOutcome.Denied, $"denied: governed {scope} tool-request limit reached.", requestId, resolvedTarget, request, governance);
            await _observer.ObserveOutcomeAsync(result, cancellationToken);
            await _observer.RecordReturnedAsync(result, cancellationToken);
            return result;
        }

        private async Task<ToolResult> DenyAuthorityAsync(ToolRequest request, CustomLoopToolAuthoritySnapshot authority, string resolvedTarget, int requestOrdinal, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var detail = !authority.IsValid
                ? authority.Detail
                : "The requested command is outside the immutable admitted maximum, current directory-role ceiling, implemented catalog, or attempt-start authority.";
            await RecordAuthorityAsync(requestId, request, authority, resolvedTarget, requestOrdinal, AuditSchema.Outcomes.Denied, detail, null, null, cancellationToken);
            var governance = new ToolGovernanceEvidence(ToolAuthorityDecision.Denied, detail, null, null, null, null, ToolApprovalDecision.NotEvaluated, null, null);
            await _observer.ObserveDecisionAsync(requestId, request, resolvedTarget, governance, cancellationToken);
            var result = new ToolResult(ToolExecutionOutcome.Denied, $"denied: {detail}", requestId, resolvedTarget, request, governance);
            await _observer.ObserveOutcomeAsync(result, cancellationToken);
            await _observer.RecordReturnedAsync(result, cancellationToken);
            return result;
        }

        private Task RecordAuthorityAsync(string requestId, ToolRequest request, CustomLoopToolAuthoritySnapshot authority, string resolvedTarget, int requestOrdinal, string outcome, string detail, string? limitScope, int? limit, CancellationToken cancellationToken)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["command"] = ToolCommandFormatter.Format(request.Command),
                ["target_path"] = request.TargetPath,
                ["resolved_path"] = resolvedTarget,
                ["run_id"] = _attempt.RunId,
                ["loop_id"] = _attempt.LoopId,
                ["role_id"] = _attempt.RoleId,
                ["definition_version"] = _attempt.DefinitionVersion,
                ["definition_hash"] = _attempt.DefinitionHash,
                ["iteration"] = _attempt.Iteration,
                ["step_id"] = _attempt.StepId,
                ["attempt"] = _attempt.Attempt,
                ["attempt_correlation_id"] = _attempt.AttemptCorrelationId,
                ["tool_request_correlation_id"] = request.CorrelationId,
                ["admitted_commands"] = Join(authority.AdmittedMaximum),
                ["current_role_commands"] = Join(authority.CurrentRoleCeiling),
                ["effective_commands"] = Join(authority.EffectiveAssignments),
                ["role_ceiling_hash"] = authority.RoleCeilingHash,
                ["catalog_hash"] = authority.CatalogHash,
                ["tool_requests_used_in_run"] = _toolRequestsUsedInRun,
                ["tool_request_ordinal"] = requestOrdinal,
                ["limit_scope"] = limitScope,
                ["limit"] = limit
            };
            return _auditLog.AppendAsync(AuditEvent.Create(
                AuditSchema.Actors.Tool,
                AuditSchema.Actions.ToolLoopAuthorityEvaluate,
                resolvedTarget,
                outcome,
                detail,
                metadata), cancellationToken);
        }

        private ToolAuditCorrelation CreateAuditCorrelation(CustomLoopToolAuthoritySnapshot authority)
        {
            return new ToolAuditCorrelation(
                _attempt.RunId,
                _attempt.LoopId,
                _attempt.RoleId,
                _attempt.DefinitionVersion,
                _attempt.DefinitionHash,
                _attempt.Iteration,
                _attempt.StepId,
                _attempt.Attempt,
                _attempt.AttemptCorrelationId,
                Join(authority.AdmittedMaximum),
                Join(authority.CurrentRoleCeiling),
                Join(authority.EffectiveAssignments),
                authority.RoleCeilingHash,
                authority.CatalogHash);
        }

        private ToolRequest BoundRequest(ToolRequest request)
        {
            if (!Enum.IsDefined(request.Command))
            {
                throw new CustomLoopToolEvidenceIntegrityException("A governed tool request used an unsupported command and was rejected before governance or actuation.");
            }

            ValidateBounded(request.TargetPath, nameof(request.TargetPath), CustomLoopLimits.MaxGovernedToolTargetCharacters, required: true);
            ValidateBounded(request.Content, nameof(request.Content), CustomLoopLimits.MaxGovernedToolArgumentCharacters, required: false);
            ValidateBounded(request.Pattern, nameof(request.Pattern), CustomLoopLimits.MaxGovernedToolArgumentCharacters, required: false);
            var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? Guid.NewGuid().ToString("N") : request.CorrelationId;
            ValidateBounded(correlationId, nameof(request.CorrelationId), CustomLoopLimits.MaxArtifactIdCharacters, required: true);
            return request with { CorrelationId = correlationId, AuditCorrelation = null };
        }

        private string ResolveTarget(string targetPath)
        {
            try
            {
                return Path.GetFullPath(targetPath, _paths.RootPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new CustomLoopToolEvidenceIntegrityException("A governed tool target could not be resolved before evidence reservation.", exception);
            }
        }

        private static void ValidateBounded(string? value, string name, int maximumCharacters, bool required)
        {
            if (required && string.IsNullOrWhiteSpace(value))
            {
                throw new CustomLoopToolEvidenceIntegrityException($"Governed tool field `{name}` is required before evidence reservation.");
            }

            if (value is not null && (value.Length > maximumCharacters || value.IndexOf('\0') >= 0))
            {
                throw new CustomLoopToolEvidenceIntegrityException($"Governed tool field `{name}` exceeds its safe evidence bound.");
            }
        }

        private static CustomLoopToolAssignment? MapAssignment(ToolCommand command)
        {
            return command switch
            {
                ToolCommand.List => CustomLoopToolAssignment.List,
                ToolCommand.Read => CustomLoopToolAssignment.Read,
                ToolCommand.Search => CustomLoopToolAssignment.Search,
                _ => null
            };
        }

        private static string Join(IEnumerable<CustomLoopToolAssignment> assignments)
        {
            return string.Join(',', assignments.OrderBy(value => value).Select(value => value.ToString().ToLowerInvariant()));
        }
    }
}
