using System.Globalization;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
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
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
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

internal sealed class BoundedCorrelatedToolBroker : IToolBroker
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);
    private readonly IToolBroker _inner;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly ToolResultRetentionService _toolResultRetention;
    private readonly CorrelatedToolEvidenceObserver _observer;
    private readonly IGovernedLoopEffectAuthorityBoundary? _effectAuthorityBoundary;
    private readonly WorkspacePaths _paths;
    private readonly CustomLoopInferenceAttemptRequest _attempt;
    private readonly int _toolRequestsUsedInRun;
    private readonly int? _retryToolRequestLimit;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _requestsObserved;
    private int _toolRequestsConsumed;
    private bool _visibleOverLimitDenied;

    /// <summary>
    /// Wraps the governed broker with per-attempt and per-run limits, immutable admission correlation,
    /// current role-authority checks, and strict durable evidence capture.
    /// </summary>
    /// <param name="inner">The inner.</param>
    /// <param name="auditLog">The audit log.</param>
    /// <param name="authorityProvider">The authority provider.</param>
    /// <param name="toolResultRetention">The tool result retention.</param>
    /// <param name="observer">The observer.</param>
    /// <param name="effectAuthorityBoundary">The exact durable effect-authority boundary used only for tool intake.</param>
    /// <param name="paths">The paths.</param>
    /// <param name="request">The request.</param>
    public BoundedCorrelatedToolBroker(
        IToolBroker inner,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ToolResultRetentionService toolResultRetention,
        CorrelatedToolEvidenceObserver observer,
        IGovernedLoopEffectAuthorityBoundary effectAuthorityBoundary,
        WorkspacePaths paths,
        CustomLoopInferenceAttemptRequest request)
        : this(
            inner,
            auditLog,
            authorityProvider,
            toolResultRetention,
            observer,
            effectAuthorityBoundary ?? throw new ArgumentNullException(nameof(effectAuthorityBoundary)),
            paths,
            request,
            governedAuthorityRequired: true)
    {
    }

    /// <summary>
    /// Wraps the retained legacy custom-loop broker without adding canonical graph authority semantics.
    /// </summary>
    /// <param name="inner">The inner.</param>
    /// <param name="auditLog">The audit log.</param>
    /// <param name="authorityProvider">The authority provider.</param>
    /// <param name="toolResultRetention">The tool result retention.</param>
    /// <param name="observer">The observer.</param>
    /// <param name="paths">The paths.</param>
    /// <param name="request">The request.</param>
    public BoundedCorrelatedToolBroker(
        IToolBroker inner,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ToolResultRetentionService toolResultRetention,
        CorrelatedToolEvidenceObserver observer,
        WorkspacePaths paths,
        CustomLoopInferenceAttemptRequest request)
        : this(
            inner,
            auditLog,
            authorityProvider,
            toolResultRetention,
            observer,
            effectAuthorityBoundary: null,
            paths,
            request,
            governedAuthorityRequired: false)
    {
    }

    private BoundedCorrelatedToolBroker(
        IToolBroker inner,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ToolResultRetentionService toolResultRetention,
        CorrelatedToolEvidenceObserver observer,
        IGovernedLoopEffectAuthorityBoundary? effectAuthorityBoundary,
        WorkspacePaths paths,
        CustomLoopInferenceAttemptRequest request,
        bool governedAuthorityRequired)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
        _toolResultRetention = toolResultRetention ?? throw new ArgumentNullException(nameof(toolResultRetention));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _effectAuthorityBoundary = effectAuthorityBoundary;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _attempt = request ?? throw new ArgumentNullException(nameof(request));
        _toolRequestsUsedInRun = request.ToolRequestsUsedInRun;
        _retryToolRequestLimit = request.RetryDispatchBudget?.RemainingToolCalls;
        if (governedAuthorityRequired && _effectAuthorityBoundary is null)
        {
            throw new ArgumentNullException(nameof(effectAuthorityBoundary));
        }
    }

    /// <summary>
    /// Gets commands advertised to the model until the attempt or run request budget is exhausted.
    /// </summary>
    public IReadOnlyList<ToolCommand> AvailableCommands => Volatile.Read(ref _requestsObserved) >= CustomLoopLimits.MaxGovernedToolRequestsPerAttempt
        || _toolRequestsUsedInRun + Volatile.Read(ref _toolRequestsConsumed) >= CustomLoopLimits.MaxGovernedToolRequestsPerRun
        || _retryToolRequestLimit is { } retryLimit && Volatile.Read(ref _requestsObserved) >= retryLimit
            ? []
            : _inner.AvailableCommands;

    /// <summary>
    /// Gets the number of requests that consumed this attempt's governed-tool budget.
    /// </summary>
    public int ToolRequestsConsumed => Volatile.Read(ref _toolRequestsConsumed);

    /// <summary>
    /// Serializes, bounds, correlates, authorizes, governs, retains, and records one tool request.
    /// </summary>
    /// <param name="request">The model-issued request; its fields are bounded before evidence reservation or actuation.</param>
    /// <param name="cancellationToken">The token used to cancel authority, governance, retention, and evidence work.</param>
    /// <returns>
    /// A task whose result is the governed tool outcome. The first over-limit request receives a
    /// visible retained denial; a repeated over-limit request fails the attempt without actuation.
    /// </returns>
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
            boundedRequest = BoundRequest(request, requestOrdinal);
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
        var retryLimitExceeded = _retryToolRequestLimit is { } retryLimit && requestOrdinal > retryLimit;
        if ((attemptLimitExceeded || runLimitExceeded || retryLimitExceeded) && _visibleOverLimitDenied)
        {
            Interlocked.Increment(ref _toolRequestsConsumed);
            var (scope, limit) = ResolveLimit(attemptLimitExceeded, runLimitExceeded);
            await _observer.RecordIntegrityAsync(correlatedRequest, resolvedTarget, authority, requestOrdinal, cancellationToken);
            using var auditIntegrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
            await RecordAuthorityAsync(
                null,
                correlatedRequest,
                authority,
                resolvedTarget,
                requestOrdinal,
                AuditSchema.Outcomes.Failed,
                "A governed tool request repeated after the one visible over-limit denial; its exact non-actuating identity was retained and the attempt failed.",
                scope,
                limit,
                auditIntegrityWindow.Token);
            throw new CustomLoopToolEvidenceIntegrityException("A governed tool request repeated after the one visible over-limit denial; the attempt failed without actuation.");
        }

        await _observer.ReserveAsync(correlatedRequest, resolvedTarget, authority, requestOrdinal, cancellationToken);
        Interlocked.Increment(ref _toolRequestsConsumed);

        if (attemptLimitExceeded || runLimitExceeded || retryLimitExceeded)
        {
            _visibleOverLimitDenied = true;
            var (scope, limit) = ResolveLimit(attemptLimitExceeded, runLimitExceeded);
            return await DenyAsync(correlatedRequest, authority, resolvedTarget, requestOrdinal, scope, limit, cancellationToken);
        }

        var assignment = MapAssignment(correlatedRequest.Command);
        if (!authority.IsValid || assignment is null || !authority.EffectiveAssignments.Contains(assignment.Value) || !_inner.AvailableCommands.Contains(correlatedRequest.Command))
        {
            return await DenyAuthorityAsync(correlatedRequest, authority, resolvedTarget, requestOrdinal, cancellationToken);
        }

        if (_effectAuthorityBoundary is not null)
        {
            var intake = await EvaluateIntakeAuthorityAsync(correlatedRequest, resolvedTarget, cancellationToken);
            if (intake.Decision!.Disposition == GovernedLoopEffectAuthorityDisposition.Deny)
            {
                var detail = $"Current governed-loop authority denied the exact workspace-tool intake ({intake.Decision.Reason.ToString().ToLowerInvariant()}).";
                return await DenyAuthorityAsync(correlatedRequest, authority, resolvedTarget, requestOrdinal, cancellationToken, detail);
            }

            if (intake.Decision.Disposition != GovernedLoopEffectAuthorityDisposition.Direct)
            {
                throw Stopped(intake);
            }
        }

        var result = await _inner.ExecuteAsync(correlatedRequest, cancellationToken);
        await _observer.RecordReturnedAsync(result, cancellationToken);
        return result;
    }

    private (string Scope, int Limit) ResolveLimit(bool attemptLimitExceeded, bool runLimitExceeded)
        => attemptLimitExceeded
            ? ("attempt", CustomLoopLimits.MaxGovernedToolRequestsPerAttempt)
            : runLimitExceeded
                ? ("run", CustomLoopLimits.MaxGovernedToolRequestsPerRun)
                : ("retry", _retryToolRequestLimit!.Value);

    private async Task<GovernedLoopEffectAuthorityExecutionResult<bool>> EvaluateIntakeAuthorityAsync(
        ToolRequest request,
        string resolvedTarget,
        CancellationToken cancellationToken)
    {
        var authorityRequest = WorkspaceToolEffectAuthorityRequestFactory.Create(
            _attempt.AdmissionReceipt!,
            _attempt.ExecutionBinding!,
            _attempt.GraphArtifact!,
            _attempt.StepId,
            _attempt.Attempt,
            _attempt.AttemptCorrelationId,
            request,
            resolvedTarget,
            GovernedLoopEffectBoundaryKind.WorkspaceToolIntake);
        var callbackOpen = 1;
        var callbackCount = 0;
        GovernedLoopEffectAuthorityExecutionResult<bool>? result;
        try
        {
            result = await _effectAuthorityBoundary!.ExecuteAsync(
                authorityRequest,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref callbackOpen) == 0 || Interlocked.Increment(ref callbackCount) != 1)
                    {
                        throw AuthorityProtocolStopped("The workspace-tool intake callback ran more than once or after its authority boundary returned.");
                    }

                    return Task.FromResult(true);
                },
                cancellationToken);
        }
        finally
        {
            Volatile.Write(ref callbackOpen, 0);
        }

        ValidateIntakeProtocol(authorityRequest, result, Volatile.Read(ref callbackCount));
        if (result!.Status != GovernedLoopEffectAuthorityExecutionStatus.Decided
            || result.Decision!.Disposition == GovernedLoopEffectAuthorityDisposition.Pause)
        {
            throw Stopped(result);
        }

        return result;
    }

    private static void ValidateIntakeProtocol(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityExecutionResult<bool>? result,
        int callbackCount)
    {
        if (result is null
            || !Enum.IsDefined(result.Status)
            || !Enum.IsDefined(result.EvidenceStatus)
            || callbackCount is < 0 or > 1
            || result.CommitInvoked != (callbackCount == 1)
            || result.Result != (callbackCount == 1))
        {
            throw AuthorityProtocolStopped("The workspace-tool intake boundary returned a malformed or callback-inconsistent result.");
        }

        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.Decided)
        {
            if (result.Decision is null
                || !GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(result.Decision, request)
                || result.EvidenceStatus != GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended)
            {
                throw AuthorityProtocolStopped("A decided workspace-tool intake did not carry exact newly appended authority evidence.");
            }

            if (result.Decision.Disposition == GovernedLoopEffectAuthorityDisposition.Direct)
            {
                if (callbackCount != 1 || !result.CommitInvoked || !result.Result)
                {
                    throw AuthorityProtocolStopped("Direct workspace-tool intake did not complete its exact single-use callback.");
                }
            }
            else if (callbackCount != 0 || result.CommitInvoked || result.Result)
            {
                throw AuthorityProtocolStopped("A denied or paused workspace-tool intake invoked its direct callback.");
            }

            return;
        }

        if (callbackCount != 0 || result.CommitInvoked || result.Result)
        {
            throw AuthorityProtocolStopped("A stopped workspace-tool intake crossed its direct callback.");
        }

        if (result.Status is GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest or GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable)
        {
            if (result.Decision is not null || result.EvidenceStatus != GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown)
            {
                throw AuthorityProtocolStopped("An invalid or unavailable workspace-tool intake carried contradictory authority evidence.");
            }

            return;
        }

        if (result.Status == GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected)
        {
            if (result.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown
                || result.Decision is not null && !GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(result.Decision, request))
            {
                throw AuthorityProtocolStopped("An evidence-rejected workspace-tool intake carried malformed or mismatched evidence.");
            }

            return;
        }

        throw AuthorityProtocolStopped("The workspace-tool intake boundary returned an unsupported execution status.");
    }

    private static GovernedLoopEffectAuthorityStoppedException Stopped(GovernedLoopEffectAuthorityExecutionResult<bool> result)
        => new(
            BoundedStopDetail(result.Detail),
            result.Status,
            result.EvidenceStatus,
            result.Decision);

    private static string BoundedStopDetail(string? detail)
        => string.IsNullOrWhiteSpace(detail)
            || detail.Length > CustomLoopLimits.MaxToolGovernanceDetailCharacters
            || detail.IndexOf('\0') >= 0
                ? "The workspace-tool intake stopped before inner governance or actuation."
                : detail;

    private static GovernedLoopEffectAuthorityStoppedException AuthorityProtocolStopped(string detail)
        => new(
            detail,
            GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
            decision: null);

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
        var result = WorkspaceMutationEvidenceProjection.ProjectResult(
            new ToolResult(ToolExecutionOutcome.Denied, $"denied: governed {scope} tool-request limit reached.", requestId, resolvedTarget, request, governance));
        result = await RetainAsync(result, requestOrdinal, cancellationToken);
        await _observer.ObserveOutcomeAsync(result, cancellationToken);
        await _observer.RecordReturnedAsync(result, cancellationToken);
        return result;
    }

    private async Task<ToolResult> DenyAuthorityAsync(
        ToolRequest request,
        CustomLoopToolAuthoritySnapshot authority,
        string resolvedTarget,
        int requestOrdinal,
        CancellationToken cancellationToken,
        string? detailOverride = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var detail = detailOverride ?? (!authority.IsValid
            ? authority.Detail
            : "The requested command is outside the immutable admitted maximum, current directory-role ceiling, implemented catalog, or attempt-start authority.");
        await RecordAuthorityAsync(requestId, request, authority, resolvedTarget, requestOrdinal, AuditSchema.Outcomes.Denied, detail, null, null, cancellationToken);
        var governance = new ToolGovernanceEvidence(ToolAuthorityDecision.Denied, detail, null, null, null, null, ToolApprovalDecision.NotEvaluated, null, null);
        await _observer.ObserveDecisionAsync(requestId, request, resolvedTarget, governance, cancellationToken);
        var result = WorkspaceMutationEvidenceProjection.ProjectResult(
            new ToolResult(ToolExecutionOutcome.Denied, $"denied: {detail}", requestId, resolvedTarget, request, governance));
        result = await RetainAsync(result, requestOrdinal, cancellationToken);
        await _observer.ObserveOutcomeAsync(result, cancellationToken);
        await _observer.RecordReturnedAsync(result, cancellationToken);
        return result;
    }

    private Task RecordAuthorityAsync(string? requestId, ToolRequest request, CustomLoopToolAuthoritySnapshot authority, string resolvedTarget, int requestOrdinal, string outcome, string detail, string? limitScope, int? limit, CancellationToken cancellationToken)
    {
        var mutation = WorkspaceMutationEvidenceProjection.IsMutation(request.Command);
        var evidenceTarget = WorkspaceMutationEvidenceProjection.ProjectResolvedTarget(request, resolvedTarget);
        var evidenceRequest = WorkspaceMutationEvidenceProjection.ProjectRequest(request);
        var metadata = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = ToolCommandFormatter.Format(request.Command),
            ["target_path"] = evidenceRequest.TargetPath,
            ["resolved_path"] = evidenceTarget,
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
            evidenceTarget,
            outcome,
            mutation ? $"Governed workspace mutation authority outcome: {outcome}." : detail,
            metadata), cancellationToken);
    }

    private async Task<ToolResult> RetainAsync(ToolResult result, int requestOrdinal, CancellationToken cancellationToken)
    {
        return await _toolResultRetention.RetainAsync(
            result,
            new Dictionary<string, object?> { ["tool_request_ordinal"] = requestOrdinal },
            cancellationToken);
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

    private ToolRequest BoundRequest(ToolRequest request, int requestOrdinal)
    {
        if (!Enum.IsDefined(request.Command))
        {
            throw new CustomLoopToolEvidenceIntegrityException("A governed tool request used an unsupported command and was rejected before governance or actuation.");
        }

        ValidateBounded(request.TargetPath, nameof(request.TargetPath), CustomLoopLimits.MaxGovernedToolTargetCharacters, required: true);
        ValidateBounded(request.Content, nameof(request.Content), CustomLoopLimits.MaxGovernedToolArgumentCharacters, required: false);
        ValidateBounded(request.Pattern, nameof(request.Pattern), CustomLoopLimits.MaxGovernedToolArgumentCharacters, required: false);
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? CreateDeterministicCorrelationId(requestOrdinal)
            : request.CorrelationId;
        ValidateBounded(correlationId, nameof(request.CorrelationId), CustomLoopLimits.MaxArtifactIdCharacters, required: true);
        return request with { CorrelationId = correlationId, AuditCorrelation = null };
    }

    private string CreateDeterministicCorrelationId(int requestOrdinal)
    {
        var canonical = string.Join(
            '\n',
            "workspace-tool-correlation-v1",
            _attempt.RunId,
            _attempt.StepId,
            _attempt.Attempt.ToString(CultureInfo.InvariantCulture),
            _attempt.AttemptCorrelationId,
            requestOrdinal.ToString(CultureInfo.InvariantCulture));
        return "workspace-tool-correlation-" + CustomLoopTraceContentHash.Compute(canonical);
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
