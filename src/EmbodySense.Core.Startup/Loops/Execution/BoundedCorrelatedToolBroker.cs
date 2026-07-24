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

internal sealed class BoundedCorrelatedToolBroker : IToolBroker
{
    private readonly IToolBroker _inner;
    private readonly IAuditLog _auditLog;
    private readonly ICustomLoopToolAuthorityProvider _authorityProvider;
    private readonly ToolResultRetentionService _toolResultRetention;
    private readonly CorrelatedToolEvidenceObserver _observer;
    private readonly WorkspacePaths _paths;
    private readonly CustomLoopInferenceAttemptRequest _attempt;
    private readonly int _toolRequestsUsedInRun;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _requestsObserved;
    private int _toolRequestsConsumed;
    private bool _visibleOverLimitDenied;

    public BoundedCorrelatedToolBroker(
        IToolBroker inner,
        IAuditLog auditLog,
        ICustomLoopToolAuthorityProvider authorityProvider,
        ToolResultRetentionService toolResultRetention,
        CorrelatedToolEvidenceObserver observer,
        WorkspacePaths paths,
        CustomLoopInferenceAttemptRequest request)
    {
        _inner = inner;
        _auditLog = auditLog;
        _authorityProvider = authorityProvider;
        _toolResultRetention = toolResultRetention;
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
        if ((attemptLimitExceeded || runLimitExceeded) && _visibleOverLimitDenied)
        {
            Interlocked.Increment(ref _toolRequestsConsumed);
            var scope = attemptLimitExceeded ? "attempt" : "run";
            var limit = attemptLimitExceeded ? CustomLoopLimits.MaxGovernedToolRequestsPerAttempt : CustomLoopLimits.MaxGovernedToolRequestsPerRun;
            await _observer.RecordIntegrityAsync(correlatedRequest, resolvedTarget, authority, requestOrdinal, cancellationToken);
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
                cancellationToken);
            throw new CustomLoopToolEvidenceIntegrityException("A governed tool request repeated after the one visible over-limit denial; the attempt failed without actuation.");
        }

        await _observer.ReserveAsync(correlatedRequest, resolvedTarget, authority, requestOrdinal, cancellationToken);
        Interlocked.Increment(ref _toolRequestsConsumed);

        if (attemptLimitExceeded || runLimitExceeded)
        {
            _visibleOverLimitDenied = true;
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
        result = await RetainAsync(result, requestOrdinal, cancellationToken);
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
        result = await RetainAsync(result, requestOrdinal, cancellationToken);
        await _observer.ObserveOutcomeAsync(result, cancellationToken);
        await _observer.RecordReturnedAsync(result, cancellationToken);
        return result;
    }

    private Task RecordAuthorityAsync(string? requestId, ToolRequest request, CustomLoopToolAuthoritySnapshot authority, string resolvedTarget, int requestOrdinal, string outcome, string detail, string? limitScope, int? limit, CancellationToken cancellationToken)
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
